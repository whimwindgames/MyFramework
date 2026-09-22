using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using UnityEngine;

internal sealed class UpdStore
{
    private readonly UpdDisk mDisk;
    private readonly UpdDisk mBlobDisk;
    private readonly bool mShared;
    private readonly string mPlatformRoot;
    private readonly string mBlobs;
    private readonly string mReleases;
    private readonly string mTemp;
    private readonly string mState;
    private readonly string mActive;
    private readonly string mPrevious;
    private readonly string mCandidate;
    private readonly string mRejected;
    private readonly string mLock;
    private readonly object mLockSync = new object();
    private FileStream mHeldLock;
    private int mLockLeases;
    private UpdPackedSource mPacked;
    private readonly Dictionary<string, VerifiedFile> mVerified = new(StringComparer.Ordinal);
    private readonly Dictionary<string, object> mFileLocks = new(StringComparer.Ordinal);

    private sealed class VerifiedFile
    {
        public string sha;
        public long size;
        public long modified;
        public long created;
    }

    internal void setPacked(UpdPackedSource packed) => mPacked = packed;
    internal bool hasPacked(UpdFile file) => mPacked != null && mPacked.contains(file);

    private object fileLock(string sha)
    {
        lock (mFileLocks)
        {
            if (!mFileLocks.TryGetValue(sha, out var gate)) mFileLocks.Add(sha, gate = new object());
            return gate;
        }
    }

    // Verified identities are shared by scan, stage, commit and the resource reader.
    // They are deliberately process-local; changed/replaced files and new processes rehash.
    private bool verified(string path, UpdFile file, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var info = new FileInfo(path);
        if (!info.Exists || info.Length != file.size) return false;
        lock (mVerified)
        {
            if (mVerified.TryGetValue(path, out var value) && value.sha == file.sha256 &&
                value.size == info.Length && value.modified == info.LastWriteTimeUtc.Ticks &&
                value.created == info.CreationTimeUtc.Ticks) return true;
        }
        if (UpdHash.file(path, ct) != file.sha256) return false;
        remember(path, file);
        return true;
    }
    private void remember(string path, UpdFile file)
    {
        var info = new FileInfo(path);
        lock (mVerified) mVerified[path] = new VerifiedFile
        {
            sha = file.sha256, size = info.Length,
            modified = info.LastWriteTimeUtc.Ticks, created = info.CreationTimeUtc.Ticks,
        };
    }

    internal string resolve(UpdFile file, string releaseId, CancellationToken ct)
    {
        lock (fileLock(file.sha256))
        {
            if (match(file, ct)) return filePath(file);
            if (!hasPacked(file)) UpdFail.bad(UpdCode.Hash, file.path, UpdPhase.Load);
            var timer = System.Diagnostics.Stopwatch.StartNew();
            using (takeLock())
            {
                requireSpace(file.size);
                string temp = copyPath(releaseId, file.path);
                try
                {
                    mPacked.extract(file, temp, ct);
                    stage(file, releaseId, temp, UpdPhase.Load, ct);
                    ct.ThrowIfCancellationRequested();
                    commit(file, releaseId);
                }
                finally { dropCopy(temp); }
            }
            Debug.Log($"[Startup.Resource] builtin {file.path} bytes={file.size} seconds={timer.Elapsed.TotalSeconds:F3}");
            return filePath(file);
        }
    }

    private sealed class StoreLock : IDisposable
    {
        private UpdStore mStore;

        public StoreLock(UpdStore store)
        {
            mStore = store;
        }

        public void Dispose()
        {
            UpdStore store = Interlocked.Exchange(ref mStore, null);
            if (store != null)
            {
                store.releaseLock();
            }
        }
    }

    public UpdStore(UpdCfg cfg, string root = null)
    {
        if (cfg == null)
        {
            throw new ArgumentNullException(nameof(cfg));
        }
        string baseRoot = root ?? Application.persistentDataPath;
        mPlatformRoot = Path.Combine(baseRoot, "Upd", cfg.env, cfg.platform);
        mDisk = new UpdDisk(Path.Combine(mPlatformRoot, cfg.baseId));
        mShared = cfg.contentAddressed;
        mBlobDisk = mShared
            ? new UpdDisk(Path.Combine(mPlatformRoot, "shared"))
            : mDisk;
        mBlobs = mBlobDisk.makeDir(Path.Combine(mBlobDisk.root, "blobs"));
        mReleases = mDisk.makeDir(Path.Combine(mDisk.root, "releases"));
        mTemp = mBlobDisk.makeDir(mShared
            ? Path.Combine(mBlobDisk.root, "temp", cfg.baseId)
            : Path.Combine(mDisk.root, "temp"));
        mState = Path.Combine(mDisk.root, "state.json");
        mActive = Path.Combine(mDisk.root, "active.json");
        mPrevious = Path.Combine(mDisk.root, "previous.json");
        mCandidate = Path.Combine(mDisk.root, "candidate.json");
        mRejected = Path.Combine(mDisk.root, "rejected.json");
        mLock = Path.Combine(mBlobDisk.root, "upd.lock");
    }

    public IDisposable takeLock()
    {
        lock (mLockSync)
        {
            if (mHeldLock != null)
            {
                ++mLockLeases;
                return new StoreLock(this);
            }
            try
            {
                mBlobDisk.makeParent(mLock);
                mHeldLock = new FileStream(mLock, FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None, 1, FileOptions.WriteThrough);
                mLockLeases = 1;
                return new StoreLock(this);
            }
            catch (Exception ex)
            {
                throw new UpdBad(new UpdErr(UpdCode.Busy, "upd_lock",
                    UpdPhase.Idle, ex), ex);
            }
        }
    }

    private void releaseLock()
    {
        lock (mLockSync)
        {
            if (mHeldLock == null || mLockLeases <= 0)
            {
                throw new InvalidOperationException("upd_lock_lease");
            }
            if (--mLockLeases == 0)
            {
                mHeldLock.Dispose();
                mHeldLock = null;
            }
        }
    }

    public UpdState loadState()
    {
        return load(mState, UpdJson.state);
    }

    public void saveState(UpdState state)
    {
        mDisk.writeJson(mState, state);
    }

    public UpdActive loadActive()
    {
        return load(mActive, UpdJson.active);
    }

    public void saveActive(UpdActive active)
    {
        mDisk.writeJson(mActive, active);
    }

    public void clearActive()
    {
        mDisk.delete(mActive);
    }

    public UpdActive loadPrevious()
    {
        return load(mPrevious, UpdJson.active);
    }

    public void savePrevious(UpdActive previous)
    {
        mDisk.writeJson(mPrevious, previous);
    }

    public void clearPrevious()
    {
        mDisk.delete(mPrevious);
    }

    public UpdCandidate loadCandidate()
    {
        UpdCandidate candidate = load(mCandidate, UpdJson.candidate);
        if (candidate == null)
        {
            return null;
        }
        if (candidate.schema != UpdLim.Schema ||
            candidate.attempts < 1 || candidate.attempts > 2 ||
            !UpdRes.valid(candidate.target) ||
            candidate.previous != null && !UpdRes.valid(candidate.previous) ||
            same(candidate.target, candidate.previous))
        {
            mDisk.quarantine(mCandidate);
            return null;
        }
        return candidate;
    }

    public void saveCandidate(UpdCandidate candidate)
    {
        mDisk.writeJson(mCandidate, candidate);
    }

    public void clearCandidate()
    {
        mDisk.delete(mCandidate);
    }

    public UpdActive loadRejected()
    {
        UpdActive rejected = load(mRejected, UpdJson.active);
        if (rejected != null && !UpdRes.valid(rejected))
        {
            mDisk.quarantine(mRejected);
            return null;
        }
        return rejected;
    }

    public void clearRejected()
    {
        mDisk.delete(mRejected);
    }

    public UpdActive prepareBoot(out UpdActive rejected)
    {
        rejected = loadRejected();
        UpdActive active = loadActive();
        UpdCandidate candidate = loadCandidate();
        if (candidate == null)
        {
            return active;
        }
        if (!same(active, candidate.target))
        {
            clearCandidate();
            if (same(active, loadPrevious()))
            {
                clearPrevious();
            }
            return active;
        }
        if (candidate.attempts < 2)
        {
            ++candidate.attempts;
            saveCandidate(candidate);
            return active;
        }
        if (candidate.previous == null)
        {
            saveRejected(candidate.target);
            rejected = candidate.target;
            clearCandidate();
            clearActive();
            return null;
        }
        saveRejected(candidate.target);
        rejected = candidate.target;
        saveActive(candidate.previous);
        clearPrevious();
        clearCandidate();
        return candidate.previous;
    }

    public void activate(UpdActive target)
    {
        UpdActive current = loadActive();
        if (current == null)
        {
            clearPrevious();
        }
        else if (!same(current, target))
        {
            savePrevious(current);
        }
        saveCandidate(new UpdCandidate
        {
            schema = UpdLim.Schema,
            target = target,
            previous = current,
            attempts = 1,
        });
        saveActive(target);
    }

    public void promotePrevious()
    {
        UpdActive previous = loadPrevious();
        if (previous == null)
        {
            clearActive();
            return;
        }
        saveActive(previous);
        clearPrevious();
        clearCandidate();
    }

    public void markHealthy(string releaseId)
    {
        UpdCandidate candidate = loadCandidate();
        UpdActive active = loadActive();
        if (candidate != null && active != null && active.releaseId == releaseId &&
            same(active, candidate.target))
        {
            clearCandidate();
        }
    }

    public byte[] loadMan(string releaseId)
    {
        return mDisk.read(manPath(releaseId), UpdLim.ManMax);
    }

    public void saveMan(string releaseId, byte[] data)
    {
        mDisk.write(manPath(releaseId), data);
    }

    public string filePath(UpdFile file)
    {
        if (file == null || !UpdFmt.isSha(file.sha256))
        {
            UpdFail.bad(UpdCode.Schema, "blob");
        }
        string key = mShared
            ? file.sha256.Substring(0, 2) + "/" + file.sha256
            : file.sha256;
        return mBlobDisk.child(mBlobs, key);
    }

    public string partPath(string releaseId, string path)
    {
        return rawPart(releaseId, path);
    }

    public string copyPath(string releaseId, string path)
    {
        string value = mBlobDisk.child(tempDir(releaseId), path) + ".copy-" +
            Guid.NewGuid().ToString("N");
        mBlobDisk.makeParent(value);
        return value;
    }

    public void dropCopy(string path)
    {
        mBlobDisk.delete(path);
    }

    public bool match(UpdFile file, CancellationToken ct)
    {
        string path = filePath(file);
        if (!File.Exists(path) && mShared)
        {
            importLegacy(file, path, ct);
        }
        if (!File.Exists(path))
        {
            return false;
        }
        bool matched = verified(path, file, ct);
        if (!matched)
        {
            mBlobDisk.quarantine(path);
        }
        return matched;
    }

    // 新Base首次命中相同内容时，把旧版按Base保存的Blob懒迁移到共享仓库。
    // 旧文件不删除，确保仍安装着旧Base的客户端可以继续回滚和离线启动。
    private void importLegacy(UpdFile file, string target, CancellationToken ct)
    {
        string[] roots;
        try
        {
            roots = Directory.GetDirectories(mPlatformRoot, "*", SearchOption.TopDirectoryOnly);
        }
        catch
        {
            return;
        }
        for (int i = 0; i < roots.Length; ++i)
        {
            ct.ThrowIfCancellationRequested();
            string name = Path.GetFileName(roots[i]);
            if (name == "shared" || !UpdFmt.isId(name))
            {
                continue;
            }
            string source;
            try
            {
                UpdDisk legacy = new UpdDisk(roots[i]);
                source = legacy.child(Path.Combine(legacy.root, "blobs"), file.sha256);
                if (!File.Exists(source) || legacy.fileSize(source) != file.size ||
                    UpdHash.file(source, ct) != file.sha256)
                {
                    continue;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                continue;
            }

            string importDir = mBlobDisk.makeDir(Path.Combine(mTemp, "import"));
            string copy = mBlobDisk.child(importDir, file.sha256) + ".copy-" +
                Guid.NewGuid().ToString("N");
            try
            {
                File.Copy(source, copy, false);
                if (mBlobDisk.fileSize(copy) != file.size ||
                    UpdHash.file(copy, ct) != file.sha256)
                {
                    continue;
                }
                if (File.Exists(target))
                {
                    return;
                }
                mBlobDisk.move(copy, target);
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // 旧缓存只是一条优化路径；失败时继续使用内置内容或远端下载。
            }
            finally
            {
                mBlobDisk.delete(copy);
            }
        }
    }

    public long partSize(string releaseId, UpdFile file)
    {
        string path = rawPart(releaseId, file.path);
        if (!File.Exists(path)) return 0;
        long size = mBlobDisk.fileSize(path);
        return size <= file.size ? size : 0;
    }

    public bool partMatch(string releaseId, UpdFile file, CancellationToken ct)
    {
        string part = rawPart(releaseId, file.path);
        if (!File.Exists(part) || mBlobDisk.fileSize(part) != file.size)
        {
            return false;
        }
        if (verified(part, file, ct))
        {
            return true;
        }
        mBlobDisk.delete(part);
        return false;
    }

    public long prepPart(string releaseId, UpdFile file)
    {
        string path = rawPart(releaseId, file.path);
        mBlobDisk.makeParent(path);
        if (!File.Exists(path)) return 0;
        long size = mBlobDisk.fileSize(path);
        if (size > file.size)
        {
            mBlobDisk.delete(path);
            return 0;
        }
        return size;
    }

    public void resetPart(string releaseId, string path)
    {
        mBlobDisk.delete(rawPart(releaseId, path));
    }

    public void stage(UpdFile file, string releaseId, string source, UpdPhase phase,
        CancellationToken ct)
    {
        if (!verified(source, file, ct))
        {
            mBlobDisk.delete(source);
            UpdFail.bad(UpdCode.Hash, "stage", phase);
        }
        string part = partPath(releaseId, file.path);
        mBlobDisk.move(source, part);
        remember(part, file);
    }

    public void commit(UpdFile file, string releaseId)
    {
        string source = rawPart(releaseId, file.path);
        if (!verified(source, file, CancellationToken.None))
        {
            throw new UpdBad(new UpdErr(UpdCode.Hash, "part", UpdPhase.Install,
                null, 0, true));
        }
        string target = filePath(file);
        if (File.Exists(target))
        {
            if (!verified(target, file, CancellationToken.None))
            {
                mBlobDisk.quarantine(target);
            }
            else
            {
                mBlobDisk.delete(source);
                return;
            }
        }
        mBlobDisk.move(source, target);
        remember(target, file);
    }

    public void prune(UpdActive active, UpdActive previous)
    {
        HashSet<string> keepBlobs = new HashSet<string>(StringComparer.Ordinal);
        HashSet<string> keepReleases = new HashSet<string>(StringComparer.Ordinal);
        collect(active, keepBlobs, keepReleases);
        collect(previous, keepBlobs, keepReleases);
        string[] files = mBlobDisk.getFiles(mBlobs);
        if (mShared)
        {
            keepBlobs.Clear();
            if (!collectShared(keepBlobs))
            {
                files = Array.Empty<string>();
            }
        }
        for (int i = 0; i < files.Length; ++i)
        {
            if (!keepBlobs.Contains(Path.GetFileName(files[i])))
            {
                mBlobDisk.delete(files[i]);
            }
        }
        string[] dirs = Directory.GetDirectories(mReleases, "*", SearchOption.TopDirectoryOnly);
        for (int i = 0; i < dirs.Length; ++i)
        {
            if (!keepReleases.Contains(Path.GetFileName(dirs[i])))
            {
                mDisk.dropDir(dirs[i]);
            }
        }
    }

    public void requireSpace(long bytes)
    {
        if (bytes <= 0)
        {
            return;
        }
        long reserve = Math.Max(16L * 1024 * 1024, bytes / 10);
        if (mBlobDisk.available() < checked(bytes + reserve))
        {
            UpdFail.bad(UpdCode.Disk, "space", UpdPhase.Plan);
        }
    }

    public void cleanTemp(string releaseId)
    {
        string keep = Path.GetFullPath(tempPath(releaseId));
        string[] dirs = Directory.GetDirectories(mTemp, "*", SearchOption.TopDirectoryOnly);
        for (int i = 0; i < dirs.Length; ++i)
        {
            if (!string.Equals(Path.GetFullPath(dirs[i]), keep, pathCmp()))
            {
                mBlobDisk.dropDir(dirs[i]);
            }
        }
    }

    private string rawPart(string releaseId, string path)
    {
        return rawPath(releaseId, path) + ".part";
    }

    private string rawPath(string releaseId, string path)
    {
        return mBlobDisk.child(tempPath(releaseId), path);
    }

    private string tempDir(string releaseId)
    {
        return mBlobDisk.makeDir(tempPath(releaseId));
    }

    private string tempPath(string releaseId)
    {
        if (!UpdFmt.isId(releaseId))
        {
            UpdFail.bad(UpdCode.Schema, "release_id");
        }
        return Path.Combine(mTemp, releaseId);
    }

    private string manPath(string releaseId)
    {
        if (!UpdFmt.isId(releaseId))
        {
            UpdFail.bad(UpdCode.Schema, "release_id");
        }
        return mDisk.child(mReleases, releaseId + "/manifest.json");
    }

    private void collect(UpdActive pointer, HashSet<string> blobs,
        HashSet<string> releases)
    {
        if (pointer == null || !releases.Add(pointer.releaseId))
        {
            return;
        }
        byte[] raw = loadMan(pointer.releaseId);
        if (raw.LongLength != pointer.manifestSize ||
            UpdHash.data(raw) != pointer.manifestSha)
        {
            UpdFail.bad(UpdCode.Hash, "manifest", UpdPhase.Install);
        }
        UpdMan man = UpdJson.man(raw);
        for (int i = 0; i < man.files.Length; ++i)
        {
            blobs.Add(man.files[i].sha256);
        }
    }

    // 共享Blob只能在确认所有Base的active/previous都可完整读取后回收。
    // 任一状态可疑就放弃本次Blob GC，宁可多占空间也不破坏其他Base的回滚。
    private bool collectShared(HashSet<string> blobs)
    {
        try
        {
            string[] roots = Directory.GetDirectories(mPlatformRoot, "*",
                SearchOption.TopDirectoryOnly);
            for (int i = 0; i < roots.Length; ++i)
            {
                string name = Path.GetFileName(roots[i]);
                if (name == "shared" || !UpdFmt.isId(name))
                {
                    continue;
                }
                UpdDisk disk = new UpdDisk(roots[i]);
                collectSharedPointer(disk, Path.Combine(disk.root, "active.json"), blobs);
                collectSharedPointer(disk, Path.Combine(disk.root, "previous.json"), blobs);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void collectSharedPointer(UpdDisk disk, string path,
        HashSet<string> blobs)
    {
        if (!File.Exists(path))
        {
            return;
        }
        UpdActive pointer = UpdJson.active(disk.read(path, UpdLim.StateMax));
        if (!UpdRes.valid(pointer))
        {
            throw new InvalidDataException("shared_pointer");
        }
        string releases = Path.Combine(disk.root, "releases");
        string manPath = disk.child(releases, pointer.releaseId + "/manifest.json");
        byte[] raw = disk.read(manPath, UpdLim.ManMax);
        if (raw.LongLength != pointer.manifestSize ||
            UpdHash.data(raw) != pointer.manifestSha)
        {
            throw new InvalidDataException("shared_manifest");
        }
        UpdMan man = UpdJson.man(raw);
        if (man.schema != UpdLim.Schema || man.releaseId != pointer.releaseId)
        {
            throw new InvalidDataException("shared_manifest_identity");
        }
        for (int i = 0; i < man.files.Length; ++i)
        {
            UpdFile file = man.files[i];
            if (file == null || !UpdFmt.isPath(file.path) ||
                !UpdFmt.isSha(file.sha256) || file.size <= 0 ||
                file.size > UpdLim.FileSize)
            {
                throw new InvalidDataException("shared_manifest_file");
            }
            blobs.Add(file.sha256);
        }
    }

    private T load<T>(string path, Func<byte[], T> parse) where T : class
    {
        if (!File.Exists(path))
        {
            return null;
        }
        try
        {
            return parse(mDisk.read(path, UpdLim.StateMax));
        }
        catch (UpdBad)
        {
            mDisk.quarantine(path);
            return null;
        }
    }

    private static StringComparison pathCmp()
    {
        return Path.DirectorySeparatorChar == '\\'
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
    }

    private static bool same(UpdActive left, UpdActive right)
    {
        return left != null && right != null &&
            left.schema == right.schema && left.seq == right.seq &&
            left.releaseId == right.releaseId &&
            left.manifestSha == right.manifestSha &&
            left.manifestSize == right.manifestSize &&
            left.latestSha == right.latestSha;
    }

    private void saveRejected(UpdActive rejected)
    {
        mDisk.writeJson(mRejected, rejected);
    }
}
