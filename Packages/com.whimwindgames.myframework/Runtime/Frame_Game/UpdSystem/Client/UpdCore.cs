using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Cysharp.Threading.Tasks;

public sealed class UpdCore
{
    private sealed class UpdHead
    {
        public UpdLatest latest;
        public string sha;
    }

    private sealed class UpdDoc
    {
        public UpdMan man;
        public byte[] raw;
        public UpdCfg cfg;
    }

    private sealed class DownJob
    {
        public UpdFile file;
        public long last;
    }

    private sealed class PlanRow
    {
        public UpdFile file;
        public bool local;
        public bool ready;
        public long part;
    }

    private static int sRun;
    private readonly UpdCfg mCfg;
    private readonly Action<UpdProg> mProg;
    private readonly UpdGate mGate;
    private readonly string mStoreRoot;
    private readonly string mBuiltinRoot;
    private UpdStore mStore;
    private UpdHttp mHttp;
    private UpdSign mSign;
    private UpdBuiltin mBuiltin;
    private UpdActive mRejected;
    private UpdPhase mPhase;
    private Stopwatch mDnTime;
    private long mDnGot;
    private long mDnSize;
    private long mLastMs;
    private long mRateGot;
    private long mRateMs;
    private long mDnBps;
    private Stopwatch mInstTime;
    private int mStep;
    private float mRate;

    public UpdCore(UpdCfg cfg, Action<UpdProg> onProg = null, UpdGate gate = null)
        : this(cfg, onProg, gate, null, null)
    {
    }

    internal UpdCore(UpdCfg cfg, Action<UpdProg> onProg, UpdGate gate,
        string storeRoot, string builtinRoot)
    {
        mCfg = copyCfg(cfg);
        mProg = onProg;
        mGate = gate;
        mStoreRoot = storeRoot;
        mBuiltinRoot = builtinRoot;
    }

    public async UniTask<UpdRet<UpdRes>> run(CancellationToken ct = default)
    {
        if (Interlocked.CompareExchange(ref sRun, 1, 0) != 0)
        {
            return UpdRet<UpdRes>.fail(new UpdErr(UpdCode.Busy, "upd_run"));
        }
        resetProg();
        bool done = false;
        try
        {
            UpdRet<UpdRes> result = await runInner(ct);
            done = result.ok;
            return result;
        }
        catch (OperationCanceledException ex)
        {
            return UpdRet<UpdRes>.fail(new UpdErr(UpdCode.Cancel, "update", mPhase, ex));
        }
        catch (UpdBad bad)
        {
            return UpdRet<UpdRes>.fail(bad.err);
        }
        catch (IOException ex)
        {
            return UpdRet<UpdRes>.fail(new UpdErr(UpdCode.Disk, "update", mPhase, ex));
        }
        catch (Exception ex)
        {
            return UpdRet<UpdRes>.fail(new UpdErr(UpdCode.State, "update", mPhase, ex));
        }
        finally
        {
            Volatile.Write(ref sRun, done ? 2 : 0);
        }
    }

    private async UniTask<UpdRet<UpdRes>> runInner(CancellationToken ct)
    {
        UpdRule.cfg(mCfg);
        mStore = new UpdStore(mCfg, mStoreRoot);
        mHttp = new UpdHttp(mCfg.baseUrl, mCfg.env, mCfg.timeout);
        mSign = new UpdSign(mCfg.pubKey);
        mBuiltin = new UpdBuiltin(mStore, mCfg.timeout, mCfg.platform, mBuiltinRoot);

        report(UpdPhase.Latest, null, 0, 4);
        UpdRes local = await openLocal(ct);
        report(UpdPhase.Latest, null, 1, 4);
        UpdRet<UpdHead> headRet = await getHead(local != null, ct);
        if (!headRet.ok)
        {
            if (local != null && canUseLocal(headRet.err))
            {
                report(UpdPhase.Latest, null, 4, 4);
                report(UpdPhase.Ready);
                return UpdRet<UpdRes>.pass(local);
            }
            return UpdRet<UpdRes>.fail(headRet.err);
        }
        UpdHead head = headRet.value;
        if (sameHead(head, mRejected))
        {
            if (local == null)
            {
                return UpdRet<UpdRes>.fail(new UpdErr(UpdCode.State,
                    "rejected_latest", UpdPhase.Latest));
            }
            report(UpdPhase.Latest, null, 4, 4);
            report(UpdPhase.Ready);
            return UpdRet<UpdRes>.pass(local);
        }
        if (mRejected != null)
        {
            await clearRejected(ct);
            mRejected = null;
        }
        await acceptHead(head, ct);
        report(UpdPhase.Latest, null, 4, 4);
        if (local != null && sameHead(head, local))
        {
            report(UpdPhase.Ready);
            return UpdRet<UpdRes>.pass(local);
        }

        report(UpdPhase.Manifest);
        UpdRet<UpdDoc> docRet = await getDoc(head.latest, ct);
        if (!docRet.ok)
        {
            return UpdRet<UpdRes>.fail(docRet.err);
        }
        UpdDoc doc = docRet.value;
        report(UpdPhase.Manifest, null, 1, 1);
        report(UpdPhase.Plan);
        UpdRet<List<UpdFile>> syncRet = await syncFiles(doc.man, ct);
        if (!syncRet.ok)
        {
            return UpdRet<UpdRes>.fail(syncRet.err);
        }
        UpdRes res = await install(doc, head, syncRet.value, ct);
        report(UpdPhase.Ready);
        return UpdRet<UpdRes>.pass(res);
    }

    private UniTask<UpdRes> openLocal(CancellationToken ct)
    {
        return runIo(() =>
        {
            using (FileStream gate = mStore.takeLock())
            {
                UpdActive active = mStore.prepareBoot(out mRejected);
                UpdRes res = tryOpen(active);
                if (res != null)
                {
                    try
                    {
                        res.checkBoot();
                        return res;
                    }
                    catch (UpdBad)
                    {
                    }
                }
                UpdActive previous = mStore.loadPrevious();
                UpdRes fallback = tryOpen(previous);
                if (fallback == null)
                {
                    mStore.clearActive();
                    return null;
                }
                try
                {
                    fallback.checkBoot();
                    mStore.promotePrevious();
                    return fallback;
                }
                catch (UpdBad)
                {
                    mStore.clearActive();
                    mStore.clearPrevious();
                    return null;
                }
            }
        }, ct);
    }

    private UniTask acceptHead(UpdHead head, CancellationToken ct)
    {
        return runIo(() =>
        {
            using (FileStream gate = mStore.takeLock())
            {
                UpdState state = mStore.loadState();
                if (state == null)
                {
                    UpdActive active = mStore.loadActive();
                    if (active != null)
                    {
                        state = stateOf(active);
                    }
                }
                UpdRule.state(mCfg, head.latest, head.sha, state);
                if (!sameHead(head, state))
                {
                    mStore.saveState(stateOf(head));
                }
            }
        }, ct);
    }

    private UniTask clearRejected(CancellationToken ct)
    {
        return runIo(() =>
        {
            using (FileStream gate = mStore.takeLock())
            {
                mStore.clearRejected();
            }
        }, ct);
    }

    private UpdRes tryOpen(UpdActive active)
    {
        try
        {
            return UpdRes.open(mStore, mCfg, active);
        }
        catch (UpdBad)
        {
            return null;
        }
    }

    private async UniTask<UpdRet<UpdHead>> getHead(bool hasLocal, CancellationToken ct)
    {
        UpdErr last = null;
        int retry = hasLocal ? 0 : mCfg.retry;
        int timeout = hasLocal ? Math.Min(5, mCfg.timeout) : mCfg.timeout;
        for (int i = 0; i <= retry; ++i)
        {
            UpdRet<UpdMem> mem = await mHttp.latest(mCfg.platform, mCfg.baseId,
                timeout, ct);
            if (mem.ok)
            {
                try
                {
                    UpdBox box = UpdJson.box(mem.value.data);
                    UpdRet<byte[]> opened = mSign.open(box);
                    if (!opened.ok)
                    {
                        return UpdRet<UpdHead>.fail(opened.err);
                    }
                    UpdLatest latest = UpdJson.latest(opened.value);
                    UpdRule.latest(mCfg, latest);
                    return UpdRet<UpdHead>.pass(new UpdHead
                    {
                        latest = latest,
                        sha = UpdHash.data(opened.value),
                    });
                }
                catch (UpdBad bad)
                {
                    return UpdRet<UpdHead>.fail(bad.err);
                }
            }
            last = mem.err;
            if (last == null || !last.canRetry || i >= retry)
            {
                break;
            }
            await waitRetry(i, ct);
        }
        return UpdRet<UpdHead>.fail(last ?? new UpdErr(UpdCode.Net, "latest",
            UpdPhase.Latest));
    }

    private async UniTask<UpdRet<UpdDoc>> getDoc(UpdLatest latest, CancellationToken ct)
    {
        UpdErr last = null;
        for (int i = 0; i <= mCfg.retry; ++i)
        {
            UpdRet<UpdMem> mem = await mHttp.manifest(latest.releaseId, ct);
            if (mem.ok)
            {
                try
                {
                    if (mem.value.data.LongLength != latest.manifestSize ||
                        UpdHash.data(mem.value.data) != latest.manifestSha)
                    {
                        last = new UpdErr(UpdCode.Hash, "manifest", UpdPhase.Manifest,
                            null, 0, true);
                    }
                    else
                    {
                        UpdMan man = UpdJson.man(mem.value.data);
                        UpdCfg cfg = UpdRule.man(mCfg, latest, man);
                        return UpdRet<UpdDoc>.pass(new UpdDoc
                        {
                            man = man,
                            raw = mem.value.data,
                            cfg = cfg,
                        });
                    }
                }
                catch (UpdBad bad)
                {
                    return UpdRet<UpdDoc>.fail(bad.err);
                }
            }
            else
            {
                last = mem.err;
            }
            if (!canRetry(last, i))
            {
                break;
            }
            await waitRetry(i, ct);
        }
        return UpdRet<UpdDoc>.fail(last ?? new UpdErr(UpdCode.Net, "manifest",
            UpdPhase.Manifest));
    }

    private async UniTask<UpdRet<List<UpdFile>>> syncFiles(UpdMan man, CancellationToken ct)
    {
        List<UpdFile> changed = new List<UpdFile>();
        List<DownJob> need = new List<DownJob>();
        List<DownJob> downs = new List<DownJob>();
        int total = man.files.Length;
        const int batch = 8;
        for (int at = 0; at < total; at += batch)
        {
            int from = at;
            int count = Math.Min(batch, total - from);
            PlanRow[] rows = await runIo(() => scanRows(man, from, count, ct), ct);
            for (int i = 0; i < rows.Length; ++i)
            {
                PlanRow row = rows[i];
                int done = from + i;
                report(UpdPhase.Plan, row.file.path, done, total);
                if (!row.local)
                {
                    changed.Add(row.file);
                    if (!row.ready)
                    {
                        need.Add(new DownJob { file = row.file, last = row.part });
                    }
                }
                report(UpdPhase.Plan, row.file.path, done + 1, total);
            }
        }
        await runIo(() => mStore.cleanTemp(man.releaseId), ct);
        long copySize = 0;
        for (int i = 0; i < need.Count; ++i)
        {
            copySize = checked(copySize + need[i].file.size);
        }
        await runIo(() => mStore.requireSpace(copySize), ct);
        long copyDone = 0;
        if (copySize > 0)
        {
            report(UpdPhase.Copy, null, 0, need.Count, 0, copySize);
        }
        for (int i = 0; i < need.Count; ++i)
        {
            DownJob job = need[i];
            UpdFile file = job.file;
            long fileDone = 0;
            long before = copyDone;
            UpdRet<bool> copied = await mBuiltin.copy(file, man.releaseId, ct,
                value =>
                {
                    long safe = Math.Max(fileDone, Math.Min(file.size, value));
                    fileDone = safe;
                    report(UpdPhase.Copy, file.path, i, need.Count,
                        checked(before + safe), copySize);
                });
            if (!copied.ok)
            {
                return UpdRet<List<UpdFile>>.fail(copied.err);
            }
            copyDone = checked(copyDone + file.size);
            report(UpdPhase.Copy, file.path, i + 1, need.Count,
                copyDone, copySize);
            if (!copied.value)
            {
                downs.Add(job);
            }
        }
        prepDn(downs);
        if (mDnSize > 0)
        {
            report(UpdPhase.Ask, null, 1, 1, 0, mDnSize);
            if (mGate != null)
            {
                await mGate(mDnSize, ct);
                ct.ThrowIfCancellationRequested();
            }
            startDn();
            report(UpdPhase.Down, null, 0, downs.Count, 0, mDnSize);
        }
        int next = -1;
        int failed = 0;
        int wCnt = Math.Min(4, downs.Count);
        UniTask<UpdRet<bool>>[] workers = new UniTask<UpdRet<bool>>[wCnt];
        async UniTask<UpdRet<bool>> runDn()
        {
            while (Volatile.Read(ref failed) == 0)
            {
                int index = Interlocked.Increment(ref next);
                if (index >= downs.Count)
                {
                    return UpdRet<bool>.pass(true);
                }
                UpdRet<bool> result = await safeDown(downs[index], man.releaseId,
                    index, downs.Count, ct);
                if (result.ok) continue;
                Interlocked.Exchange(ref failed, 1);
                return result;
            }
            return UpdRet<bool>.pass(true);
        }
        for (int i = 0; i < wCnt; ++i)
        {
            workers[i] = runDn();
        }
        UpdRet<bool>[] results = await UniTask.WhenAll(workers);
        for (int i = 0; i < results.Length; ++i)
        {
            if (!results[i].ok)
            {
                return UpdRet<List<UpdFile>>.fail(results[i].err);
            }
        }
        return UpdRet<List<UpdFile>>.pass(changed);
    }

    private PlanRow[] scanRows(UpdMan man, int from, int count, CancellationToken ct)
    {
        PlanRow[] rows = new PlanRow[count];
        for (int i = 0; i < count; ++i)
        {
            ct.ThrowIfCancellationRequested();
            UpdFile file = man.files[from + i];
            PlanRow row = new PlanRow { file = file };
            row.local = mStore.match(file, ct);
            if (!row.local)
            {
                row.part = mStore.partSize(man.releaseId, file);
                if (row.part == file.size)
                {
                    row.ready = mStore.partMatch(man.releaseId, file, ct);
                    if (!row.ready) row.part = 0;
                }
            }
            rows[i] = row;
        }
        return rows;
    }

    private void prepDn(List<DownJob> jobs)
    {
        mDnGot = 0;
        mDnSize = 0;
        mLastMs = -100;
        mRateGot = 0;
        mRateMs = 0;
        mDnBps = 0;
        for (int i = 0; i < jobs.Count; ++i)
        {
            DownJob job = jobs[i];
            mDnSize = checked(mDnSize + job.file.size - job.last);
        }
        mDnTime = null;
    }

    private void startDn()
    {
        mDnTime = Stopwatch.StartNew();
        mLastMs = -100;
        mRateGot = mDnGot;
        mRateMs = 0;
        mDnBps = 0;
    }

    private async UniTask<UpdRet<bool>> safeDown(DownJob job, string releaseId,
        int done, int total, CancellationToken ct)
    {
        try
        {
            return await downFile(job, releaseId, done, total, ct);
        }
        catch (OperationCanceledException ex)
        {
            return UpdRet<bool>.fail(new UpdErr(UpdCode.Cancel, "file",
                UpdPhase.Down, ex));
        }
        catch (UpdBad bad)
        {
            return UpdRet<bool>.fail(bad.err);
        }
        catch (IOException ex)
        {
            return UpdRet<bool>.fail(new UpdErr(UpdCode.Disk, "file",
                UpdPhase.Down, ex));
        }
        catch (Exception ex)
        {
            return UpdRet<bool>.fail(new UpdErr(UpdCode.State, "file",
                UpdPhase.Down, ex));
        }
    }

    private async UniTask<UpdRet<bool>> downFile(DownJob job, string releaseId, int done,
        int total, CancellationToken ct)
    {
        UpdFile file = job.file;
        UpdErr last = null;
        int tries = 0;
        while (tries <= mCfg.retry)
        {
            long got = await runIo(() => mStore.prepPart(releaseId, file), ct);
            if (got == file.size)
            {
                if (await runIo(() => mStore.partMatch(releaseId, file, ct), ct))
                {
                    fixDn(job, got);
                    pushDn(job, done + 1, total, true);
                    return UpdRet<bool>.pass(true);
                }
                await runIo(() => mStore.resetPart(releaseId, file.path), ct);
                got = 0;
            }
            fixDn(job, got);
            pushDn(job, done, total, true);
            UpdRet<long> result = await mHttp.download(releaseId, file,
                mCfg.contentAddressed,
                mStore.partPath(releaseId, file.path), got, size =>
                {
                    addDn(job, size);
                    pushDn(job, done, total, false);
                }, ct);
            bool matched = result.ok &&
                await runIo(() => mStore.partMatch(releaseId, file, ct), ct);
            if (matched)
            {
                addDn(job, file.size);
                pushDn(job, done + 1, total, true);
                return UpdRet<bool>.pass(true);
            }
            if (result.ok)
            {
                await runIo(() => mStore.resetPart(releaseId, file.path), ct);
            }
            last = result.ok
                ? new UpdErr(UpdCode.Hash, "file", UpdPhase.Down, null, 0, true)
                : result.err;
            long safe = await runIo(() => mStore.partSize(releaseId, file), ct);
            fixDn(job, safe);
            if (last != null && last.code == UpdCode.Range &&
                last.detail == "range_reset" && safe > 0)
            {
                await runIo(() => mStore.resetPart(releaseId, file.path), ct);
                fixDn(job, 0);
                pushDn(job, done, total, true);
                continue;
            }
            pushDn(job, done, total, true);
            if (!canRetry(last, tries))
            {
                break;
            }
            await waitRetry(tries, ct);
            ++tries;
        }
        return UpdRet<bool>.fail(last ?? new UpdErr(UpdCode.Net, "file", UpdPhase.Down));
    }

    private void addDn(DownJob job, long got)
    {
        if (got > job.last)
        {
            mDnGot = checked(mDnGot + got - job.last);
            job.last = got;
        }
    }

    private void fixDn(DownJob job, long safe)
    {
        if (safe < job.last)
        {
            mDnSize = checked(mDnSize + job.last - safe);
        }
        else if (safe > job.last)
        {
            mDnGot = checked(mDnGot + safe - job.last);
        }
        job.last = safe;
    }

    private void pushDn(DownJob job, int done, int total, bool force)
    {
        long ms = mDnTime == null ? 0 : mDnTime.ElapsedMilliseconds;
        if (!force && ms - mLastMs < 100 && job.last < job.file.size)
        {
            return;
        }
        mLastMs = ms;
        long span = ms - mRateMs;
        if (span >= 500)
        {
            long bytes = Math.Max(0, mDnGot - mRateGot);
            long now = (long)(bytes * 1000.0 / span);
            mDnBps = mDnBps == 0 ? now : (mDnBps * 2 + now) / 3;
            mRateGot = mDnGot;
            mRateMs = ms;
        }
        long bps = mDnBps;
        long left = Math.Max(0, mDnSize - mDnGot);
        int eta = left == 0 ? 0 : bps <= 0 ? -1 : (int)Math.Min(int.MaxValue,
            Math.Ceiling((double)left / bps));
        report(UpdPhase.Down, job.file.path, done, total, mDnGot, mDnSize, bps, eta);
    }

    private async UniTask<UpdRes> install(UpdDoc doc, UpdHead head, List<UpdFile> changed,
        CancellationToken ct)
    {
        int total = changed.Count + 1;
        mInstTime = Stopwatch.StartNew();
        report(UpdPhase.Install, null, 0, total);
        ct.ThrowIfCancellationRequested();
        using (FileStream gate = await runIo(() => mStore.takeLock(), ct))
        {
            for (int i = 0; i < changed.Count; ++i)
            {
                UpdFile file = changed[i];
                await runIo(() => mStore.commit(file, head.latest.releaseId),
                    CancellationToken.None);
                int done = i + 1;
                report(UpdPhase.Install, file.path, done, total, 0, 0, 0,
                    calcEta(mInstTime, done, total));
            }
            UpdRes res = await runIo(() =>
            {
                UpdActive target = new UpdActive
                {
                    schema = UpdLim.Schema,
                    seq = head.latest.seq,
                    releaseId = head.latest.releaseId,
                    manifestSha = head.latest.manifestSha,
                    manifestSize = head.latest.manifestSize,
                    latestSha = head.sha,
                };
                mStore.saveMan(target.releaseId, doc.raw);
                UpdRes made = UpdRes.make(mStore, doc.man, target, doc.cfg);
                made.checkBoot();
                mStore.activate(target);
                mStore.prune(target, mStore.loadPrevious());
                return made;
            }, CancellationToken.None);
            report(UpdPhase.Install, null, total, total, 0, 0, 0, 0);
            return res;
        }
    }

    private bool sameHead(UpdHead head, UpdState state)
    {
        return state != null && state.schema == UpdLim.Schema &&
            state.env == mCfg.env && state.platform == mCfg.platform &&
            state.baseId == mCfg.baseId && state.seq == head.latest.seq &&
            state.releaseId == head.latest.releaseId && state.latestSha == head.sha;
    }

    private static bool sameHead(UpdHead head, UpdRes res)
    {
        return head != null && res != null && res.seq == head.latest.seq &&
            res.releaseId == head.latest.releaseId &&
            res.manifestSha == head.latest.manifestSha &&
            res.latestSha == head.sha;
    }

    private static bool sameHead(UpdHead head, UpdActive active)
    {
        return head != null && active != null &&
            active.schema == UpdLim.Schema &&
            active.seq == head.latest.seq &&
            active.releaseId == head.latest.releaseId &&
            active.manifestSha == head.latest.manifestSha &&
            active.manifestSize == head.latest.manifestSize &&
            active.latestSha == head.sha;
    }

    private UpdState stateOf(UpdHead head)
    {
        return new UpdState
        {
            schema = UpdLim.Schema,
            env = mCfg.env,
            platform = mCfg.platform,
            baseId = mCfg.baseId,
            seq = head.latest.seq,
            releaseId = head.latest.releaseId,
            latestSha = head.sha,
        };
    }

    private UpdState stateOf(UpdActive active)
    {
        return new UpdState
        {
            schema = UpdLim.Schema,
            env = mCfg.env,
            platform = mCfg.platform,
            baseId = mCfg.baseId,
            seq = active.seq,
            releaseId = active.releaseId,
            latestSha = active.latestSha,
        };
    }

    private bool canRetry(UpdErr err, int index)
    {
        return err != null && err.canRetry && index < mCfg.retry;
    }

    private static bool canUseLocal(UpdErr err)
    {
        return err != null && (err.code == UpdCode.Net ||
            err.code == UpdCode.Http && err.canRetry);
    }

    private static UniTask waitRetry(int index, CancellationToken ct)
    {
        int delay = 250 * (1 << Math.Min(index, 3));
        return UniTask.Delay(delay, cancellationToken: ct);
    }

    private static UniTask runIo(Action action, CancellationToken ct)
    {
        return UniTask.RunOnThreadPool(action, cancellationToken: ct);
    }

    private static UniTask<T> runIo<T>(Func<T> func, CancellationToken ct)
    {
        return UniTask.RunOnThreadPool(func, cancellationToken: ct);
    }

    private void report(UpdPhase phase, string path = null, int done = 0, int total = 0,
        long downGot = 0, long downSize = 0, long bps = 0, int etaSec = -1)
    {
        mPhase = phase;
        float rate = calcRate(phase, done, total, downGot, downSize);
        int step = stepOf(phase);
        if (step != mStep)
        {
            mStep = step;
            mRate = rate;
        }
        else
        {
            mRate = Math.Max(mRate, rate);
        }
        if (mProg == null)
        {
            return;
        }
        try
        {
            mProg(new UpdProg
            {
                phase = phase,
                path = path,
                done = done,
                total = total,
                downGot = downGot,
                downSize = downSize,
                bps = bps,
                etaSec = etaSec,
                rate = mRate,
            });
        }
        catch (Exception ex)
        {
            UnityEngine.Debug.LogException(ex);
        }
    }

    private void resetProg()
    {
        mPhase = UpdPhase.Idle;
        mDnTime = null;
        mDnGot = 0;
        mDnSize = 0;
        mLastMs = 0;
        mInstTime = null;
        mStep = -1;
        mRate = 0.0f;
    }

    private static float calcRate(UpdPhase phase, int done, int total, long downGot,
        long downSize)
    {
        float item = 0.0f;
        if (total > 0)
        {
            item = (float)Math.Min(1.0, (double)done / total);
        }
        float bytes = downSize <= 0 ? 1.0f :
            (float)Math.Min(1.0, (double)downGot / downSize);
        switch (phase)
        {
            case UpdPhase.Latest: return item * 0.20f;
            case UpdPhase.Manifest: return 0.20f + item * 0.10f;
            case UpdPhase.Plan: return 0.30f + item * 0.60f;
            case UpdPhase.Ask: return 1.0f;
            case UpdPhase.Copy: return 0.90f + bytes * 0.10f;
            case UpdPhase.Down: return bytes;
            case UpdPhase.Install: return item;
            case UpdPhase.Ready:
            case UpdPhase.Load: return 1.0f;
            default: return 0.0f;
        }
    }

    private static int stepOf(UpdPhase phase)
    {
        switch (phase)
        {
            case UpdPhase.Down: return 2;
            case UpdPhase.Install: return 3;
            case UpdPhase.Ready: return 4;
            case UpdPhase.Load: return 5;
            default: return 0;
        }
    }

    private static int calcEta(Stopwatch timer, int done, int total)
    {
        if (done <= 0 || timer == null)
        {
            return -1;
        }
        if (done >= total)
        {
            return 0;
        }
        double left = timer.Elapsed.TotalSeconds * (total - done) / done;
        return (int)Math.Min(int.MaxValue, Math.Ceiling(left));
    }

    private static UpdCfg copyCfg(UpdCfg cfg)
    {
        if (cfg == null)
        {
            return null;
        }
        return new UpdCfg
        {
            baseUrl = cfg.baseUrl,
            env = cfg.env,
            platform = cfg.platform,
            baseId = cfg.baseId,
            pubKey = cfg.pubKey,
            contentAddressed = cfg.contentAddressed,
            retry = cfg.retry,
            timeout = cfg.timeout,
            aotDlls = cfg.aotDlls == null ? null : (string[])cfg.aotDlls.Clone(),
            codeDlls = cfg.codeDlls == null ? null : (string[])cfg.codeDlls.Clone(),
            entryDll = cfg.entryDll,
            hotId = cfg.hotId,
            secret = cfg.secret,
            resList = cfg.resList,
        };
    }
}
