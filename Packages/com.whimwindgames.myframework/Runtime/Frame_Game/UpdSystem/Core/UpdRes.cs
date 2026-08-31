using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

public sealed class UpdRes
{
    private sealed class ResItem
    {
        public readonly string local;
        public readonly string sha;
        public readonly long size;

        public ResItem(string local, string sha, long size)
        {
            this.local = local;
            this.sha = sha;
            this.size = size;
        }
    }

    private readonly Dictionary<string, ResItem> mFiles;
    private readonly Dictionary<string, byte[]> mBoot;
    private readonly HashSet<string> mGood;
    private readonly UpdCfg mCfg;
    private readonly UpdStore mStore;

    public string releaseId { get; }
    internal long seq { get; }
    internal string manifestSha { get; }
    internal string latestSha { get; }

    private UpdRes(UpdStore store, UpdMan man, UpdActive active, UpdCfg cfg)
    {
        releaseId = active.releaseId;
        seq = active.seq;
        manifestSha = active.manifestSha;
        latestSha = active.latestSha;
        mCfg = copy(cfg);
        mStore = store;
        mBoot = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        mGood = new HashSet<string>(StringComparer.Ordinal);
        mFiles = new Dictionary<string, ResItem>(man.files.Length, StringComparer.Ordinal);
        for (int i = 0; i < man.files.Length; ++i)
        {
            UpdFile file = man.files[i];
            mFiles.Add(file.path, new ResItem(store.filePath(file), file.sha256, file.size));
        }
    }

    public string getPath(string path)
    {
        return getPath(path, CancellationToken.None);
    }

    public Task<string> getPathA(string path, CancellationToken ct)
    {
        return Task.Run(() => getPath(path, ct), ct);
    }

    public byte[] read(string path)
    {
        ResItem file = item(path);
        lock (mBoot)
        {
            byte[] data;
            if (mBoot.TryGetValue(path, out data))
            {
                mBoot.Remove(path);
                return data;
            }
        }
        return readFile(path, file);
    }

    internal UpdCfg getCfg()
    {
        return copy(mCfg);
    }

    internal void checkBoot()
    {
        for (int i = 0; i < mCfg.aotDlls.Length; ++i)
        {
            cacheBoot(mCfg.aotDlls[i]);
        }
        for (int i = 0; i < mCfg.codeDlls.Length; ++i)
        {
            cacheBoot(mCfg.codeDlls[i]);
        }
        if (!string.IsNullOrEmpty(mCfg.secret))
        {
            cacheBoot(mCfg.secret);
        }
        getPath(mCfg.resList);
    }

    internal void markHealthy()
    {
        using (IDisposable gate = mStore.takeLock())
        {
            mStore.markHealthy(releaseId);
        }
    }

    internal static UpdRes open(UpdStore store, UpdCfg cfg, UpdActive active)
    {
        if (!valid(active))
        {
            return null;
        }
        byte[] raw = store.loadMan(active.releaseId);
        if (raw.LongLength != active.manifestSize || UpdHash.data(raw) != active.manifestSha)
        {
            UpdFail.bad(UpdCode.Hash, "manifest", UpdPhase.Load);
        }
        UpdMan man = UpdJson.man(raw);
        UpdLatest latest = latestOf(cfg, active);
        UpdCfg rel = UpdRule.man(cfg, latest, man);
        return new UpdRes(store, man, active, rel);
    }

    internal static UpdRes make(UpdStore store, UpdMan man, UpdActive active,
        UpdCfg cfg)
    {
        return new UpdRes(store, man, active, cfg);
    }

    private string getPath(string path, CancellationToken ct)
    {
        ResItem file = item(path);
        checkFile(path, file, ct);
        return file.local;
    }

    private void checkFile(string path, ResItem file, CancellationToken ct)
    {
        lock (mGood)
        {
            if (mGood.Contains(path))
            {
                return;
            }
        }
        try
        {
            if (!File.Exists(file.local) || new FileInfo(file.local).Length != file.size ||
                UpdHash.file(file.local, ct) != file.sha)
            {
                throw fileErr(UpdCode.Hash, path);
            }
            lock (mGood)
            {
                mGood.Add(path);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (UpdBad)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw fileErr(UpdCode.Disk, path, ex);
        }
    }

    private void cacheBoot(string path)
    {
        lock (mBoot)
        {
            if (mBoot.ContainsKey(path))
            {
                return;
            }
        }
        byte[] data = readFile(path, item(path));
        lock (mBoot)
        {
            if (!mBoot.ContainsKey(path))
            {
                mBoot.Add(path, data);
            }
        }
    }

    private byte[] readFile(string path, ResItem file)
    {
        try
        {
            byte[] data = File.ReadAllBytes(file.local);
            if (data.LongLength != file.size || UpdHash.data(data) != file.sha)
            {
                throw fileErr(UpdCode.Hash, path);
            }
            return data;
        }
        catch (UpdBad)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw fileErr(UpdCode.Disk, path, ex);
        }
    }

    private UpdBad fileErr(UpdCode code, string path, Exception ex = null)
    {
        return new UpdBad(new UpdErr(code, path, UpdPhase.Load, ex), ex);
    }

    private ResItem item(string path)
    {
        ResItem value = null;
        if (path == null || !mFiles.TryGetValue(path, out value))
        {
            UpdFail.bad(UpdCode.Load, "file_map", UpdPhase.Load);
        }
        return value;
    }

    private static UpdCfg copy(UpdCfg cfg)
    {
        if (cfg == null) return null;
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

    internal static bool valid(UpdActive active)
    {
        return active != null && active.schema == UpdLim.Schema && active.seq >= 0 &&
            UpdFmt.isId(active.releaseId) && UpdFmt.isSha(active.manifestSha) &&
            active.manifestSize > 0 && active.manifestSize <= UpdLim.ManMax &&
            UpdFmt.isSha(active.latestSha);
    }

    private static UpdLatest latestOf(UpdCfg cfg, UpdActive active)
    {
        return new UpdLatest
        {
            schema = UpdLim.Schema,
            env = cfg.env,
            platform = cfg.platform,
            baseId = cfg.baseId,
            seq = active.seq,
            releaseId = active.releaseId,
            manifestSha = active.manifestSha,
            manifestSize = active.manifestSize,
        };
    }
}
