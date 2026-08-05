using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using UnityEngine;

internal sealed class UpdStore
{
    private readonly UpdDisk mDisk;
    private readonly string mBlobs;
    private readonly string mReleases;
    private readonly string mTemp;
    private readonly string mState;
    private readonly string mActive;
    private readonly string mPrevious;
    private readonly string mCandidate;
    private readonly string mRejected;
    private readonly string mLock;

    public UpdStore(UpdCfg cfg, string root = null)
    {
        if (cfg == null)
        {
            throw new ArgumentNullException(nameof(cfg));
        }
        string baseRoot = root ?? Application.persistentDataPath;
        mDisk = new UpdDisk(Path.Combine(baseRoot, "Upd", cfg.env, cfg.platform, cfg.baseId));
        mBlobs = mDisk.makeDir(Path.Combine(mDisk.root, "blobs"));
        mReleases = mDisk.makeDir(Path.Combine(mDisk.root, "releases"));
        mTemp = mDisk.makeDir(Path.Combine(mDisk.root, "temp"));
        mState = Path.Combine(mDisk.root, "state.json");
        mActive = Path.Combine(mDisk.root, "active.json");
        mPrevious = Path.Combine(mDisk.root, "previous.json");
        mCandidate = Path.Combine(mDisk.root, "candidate.json");
        mRejected = Path.Combine(mDisk.root, "rejected.json");
        mLock = Path.Combine(mDisk.root, "upd.lock");
    }

    public FileStream takeLock()
    {
        try
        {
            mDisk.makeParent(mLock);
            return new FileStream(mLock, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                1, FileOptions.WriteThrough);
        }
        catch (Exception ex)
        {
            throw new UpdBad(new UpdErr(UpdCode.Busy, "upd_lock", UpdPhase.Idle, ex), ex);
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
        return mDisk.child(mBlobs, file.sha256);
    }

    public string partPath(string releaseId, string path)
    {
        return rawPart(releaseId, path);
    }

    public string copyPath(string releaseId, string path)
    {
        string value = mDisk.child(tempDir(releaseId), path) + ".copy-" +
            Guid.NewGuid().ToString("N");
        mDisk.makeParent(value);
        return value;
    }

    public void dropCopy(string path)
    {
        mDisk.delete(path);
    }

    public bool match(UpdFile file, CancellationToken ct)
    {
        string path = filePath(file);
        if (!File.Exists(path))
        {
            return false;
        }
        bool matched = mDisk.fileSize(path) == file.size &&
            UpdHash.file(path, ct) == file.sha256;
        if (!matched)
        {
            mDisk.quarantine(path);
        }
        return matched;
    }

    public long partSize(string releaseId, UpdFile file)
    {
        string path = rawPart(releaseId, file.path);
        if (!File.Exists(path)) return 0;
        long size = mDisk.fileSize(path);
        return size <= file.size ? size : 0;
    }

    public bool partMatch(string releaseId, UpdFile file, CancellationToken ct)
    {
        string part = rawPart(releaseId, file.path);
        if (!File.Exists(part) || mDisk.fileSize(part) != file.size)
        {
            return false;
        }
        if (UpdHash.file(part, ct) == file.sha256)
        {
            return true;
        }
        mDisk.delete(part);
        return false;
    }

    public long prepPart(string releaseId, UpdFile file)
    {
        string path = rawPart(releaseId, file.path);
        mDisk.makeParent(path);
        if (!File.Exists(path)) return 0;
        long size = mDisk.fileSize(path);
        if (size > file.size)
        {
            mDisk.delete(path);
            return 0;
        }
        return size;
    }

    public void resetPart(string releaseId, string path)
    {
        mDisk.delete(rawPart(releaseId, path));
    }

    public void stage(UpdFile file, string releaseId, string source, UpdPhase phase,
        CancellationToken ct)
    {
        if (!File.Exists(source) || mDisk.fileSize(source) != file.size ||
            UpdHash.file(source, ct) != file.sha256)
        {
            mDisk.delete(source);
            UpdFail.bad(UpdCode.Hash, "stage", phase);
        }
        string part = partPath(releaseId, file.path);
        mDisk.move(source, part);
    }

    public void commit(UpdFile file, string releaseId)
    {
        string source = rawPart(releaseId, file.path);
        if (!File.Exists(source) || mDisk.fileSize(source) != file.size ||
            UpdHash.file(source, CancellationToken.None) != file.sha256)
        {
            throw new UpdBad(new UpdErr(UpdCode.Hash, "part", UpdPhase.Install,
                null, 0, true));
        }
        string target = filePath(file);
        if (File.Exists(target))
        {
            if (mDisk.fileSize(target) != file.size ||
                UpdHash.file(target, CancellationToken.None) != file.sha256)
            {
                mDisk.quarantine(target);
            }
            else
            {
                mDisk.delete(source);
                return;
            }
        }
        mDisk.move(source, target);
    }

    public void prune(UpdActive active, UpdActive previous)
    {
        HashSet<string> keepBlobs = new HashSet<string>(StringComparer.Ordinal);
        HashSet<string> keepReleases = new HashSet<string>(StringComparer.Ordinal);
        collect(active, keepBlobs, keepReleases);
        collect(previous, keepBlobs, keepReleases);
        string[] files = mDisk.getFiles(mBlobs);
        for (int i = 0; i < files.Length; ++i)
        {
            if (!keepBlobs.Contains(Path.GetFileName(files[i])))
            {
                mDisk.delete(files[i]);
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
        if (mDisk.available() < checked(bytes + reserve))
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
                mDisk.dropDir(dirs[i]);
            }
        }
    }

    private string rawPart(string releaseId, string path)
    {
        return rawPath(releaseId, path) + ".part";
    }

    private string rawPath(string releaseId, string path)
    {
        return mDisk.child(tempPath(releaseId), path);
    }

    private string tempDir(string releaseId)
    {
        return mDisk.makeDir(tempPath(releaseId));
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
