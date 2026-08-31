using System;
using System.IO;
using System.Text;
using System.Threading;
using NUnit.Framework;
using UnityEngine;

public sealed class UpdStoreTests
{
    private string mRoot;

    [SetUp]
    public void SetUp()
    {
        mRoot = Path.Combine(Path.GetTempPath(), "myframework-upd-tests", Guid.NewGuid().ToString("N"));
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(mRoot))
        {
            Directory.Delete(mRoot, true);
        }
    }

    [Test]
    public void Candidate_RollsBackToPreviousAfterTwoUnhealthyBoots()
    {
        UpdStore store = makeStore();
        UpdActive previous = active("release-1", 1, 'a');
        UpdActive candidate = active("release-2", 2, 'b');
        store.saveActive(previous);
        store.activate(candidate);

        Assert.That(store.prepareBoot(out UpdActive firstRejected).releaseId,
            Is.EqualTo(candidate.releaseId));
        Assert.That(firstRejected, Is.Null);
        Assert.That(store.prepareBoot(out UpdActive rejected).releaseId,
            Is.EqualTo(previous.releaseId));
        Assert.That(rejected.releaseId, Is.EqualTo(candidate.releaseId));
        Assert.That(store.loadCandidate(), Is.Null);
        Assert.That(store.loadPrevious(), Is.Null);
    }

    [Test]
    public void Candidate_WithoutPreviousIsRejectedAfterTwoUnhealthyBoots()
    {
        UpdStore store = makeStore();
        UpdActive candidate = active("release-1", 1, 'a');
        store.activate(candidate);

        Assert.That(store.prepareBoot(out _).releaseId, Is.EqualTo(candidate.releaseId));
        Assert.That(store.prepareBoot(out UpdActive rejected), Is.Null);
        Assert.That(rejected.releaseId, Is.EqualTo(candidate.releaseId));
        Assert.That(store.loadActive(), Is.Null);
    }

    [Test]
    public void MarkHealthy_PreventsCandidateRollback()
    {
        UpdStore store = makeStore();
        UpdActive candidate = active("release-1", 1, 'a');
        store.activate(candidate);

        store.markHealthy(candidate.releaseId);

        Assert.That(store.loadCandidate(), Is.Null);
        Assert.That(store.prepareBoot(out UpdActive rejected).releaseId,
            Is.EqualTo(candidate.releaseId));
        Assert.That(rejected, Is.Null);
    }

    [Test]
    public void Lock_AllowsOnlyOneUpdaterProcess()
    {
        UpdStore store = makeStore();
        using (store.takeLock())
        {
            UpdBad bad = Assert.Throws<UpdBad>(() =>
            {
                using (store.takeLock()) { }
            });
            Assert.That(bad.err.code, Is.EqualTo(UpdCode.Busy));
        }
    }

    [Test]
    public void DiskAvailable_ReturnsPositiveSpaceForStoreRoot()
    {
        UpdDisk disk = new UpdDisk(mRoot);

        Assert.That(disk.available(), Is.GreaterThan(0));
    }

#if UNITY_EDITOR_OSX
    [Test]
    public void DiskAvailable_OnMacOSMatchesManagedProbe()
    {
        UpdDisk disk = new UpdDisk(mRoot);
        long nativeAvailable = disk.available();
        string root = Path.GetPathRoot(mRoot);
        long managedAvailable = new DriveInfo(root).AvailableFreeSpace;

        Assert.That(Math.Abs(nativeAvailable - managedAvailable),
            Is.LessThan(2L * 1024 * 1024 * 1024));
    }

    [Test]
    public void DiskUnsignedSpaceBytes_SaturatesInsteadOfOverflowing()
    {
        Assert.That(UpdDisk.unsignedSpaceBytes(71794683, 4096),
            Is.EqualTo(294071021568L));
        Assert.That(UpdDisk.unsignedSpaceBytes(ulong.MaxValue, 4096),
            Is.EqualTo(long.MaxValue));
        Assert.That(UpdDisk.unsignedSpaceBytes(0, 4096), Is.Zero);
    }
#endif

    [Test]
    public void DiskSpaceBytes_MultipliesAvailableBlocksByBlockSize()
    {
        Assert.That(UpdDisk.spaceBytes(71794683, 4096),
            Is.EqualTo(294071021568L));
    }

    [Test]
    public void DiskSpaceBytes_SaturatesInsteadOfOverflowing()
    {
        Assert.That(UpdDisk.spaceBytes(long.MaxValue, 4096), Is.EqualTo(long.MaxValue));
        Assert.That(UpdDisk.spaceBytes(0, 4096), Is.Zero);
        Assert.That(UpdDisk.spaceBytes(1024, 0), Is.Zero);
    }

    [Test]
    public void StageAndCommit_VerifiesContentBeforePublishingBlob()
    {
        UpdStore store = makeStore();
        byte[] data = { 1, 2, 3, 4, 5 };
        UpdFile file = new UpdFile
        {
            path = "files/data.bytes",
            size = data.Length,
            sha256 = UpdHash.data(data),
        };
        string copy = store.copyPath("release-1", file.path);
        File.WriteAllBytes(copy, data);

        store.stage(file, "release-1", copy, UpdPhase.Copy, CancellationToken.None);
        store.commit(file, "release-1");

        Assert.That(store.match(file, CancellationToken.None), Is.True);
        Assert.That(File.ReadAllBytes(store.filePath(file)), Is.EqualTo(data));
    }

    [Test]
    public void ContentAddressedStore_ReusesBlobAcrossBaseIdsButKeepsStateIsolated()
    {
        byte[] data = { 8, 6, 7, 5, 3, 0, 9 };
        UpdFile file = updateFile("bundles/shared.bundle", data);
        UpdStore first = makeStore("base-1", true);
        UpdStore second = makeStore("base-2", true);
        commit(first, "release-1", file, data);
        first.saveState(new UpdState
        {
            schema = UpdLim.Schema,
            env = "test",
            platform = "Android",
            baseId = "base-1",
            seq = 1,
            releaseId = "release-1",
            latestSha = new string('a', 64),
        });

        Assert.That(second.match(file, CancellationToken.None), Is.True);
        Assert.That(second.filePath(file), Is.EqualTo(first.filePath(file)));
        Assert.That(second.loadState(), Is.Null);
    }

    [Test]
    public void ContentAddressedStore_LazilyImportsVerifiedLegacyBaseBlob()
    {
        byte[] data = { 1, 4, 1, 4, 2, 1 };
        UpdFile file = updateFile("bundles/legacy.bundle", data);
        UpdStore legacy = makeStore("base-1", false);
        UpdStore shared = makeStore("base-2", true);
        commit(legacy, "release-1", file, data);

        Assert.That(shared.match(file, CancellationToken.None), Is.True);
        Assert.That(shared.filePath(file), Is.Not.EqualTo(legacy.filePath(file)));
        Assert.That(File.ReadAllBytes(shared.filePath(file)), Is.EqualTo(data));
        Assert.That(File.Exists(legacy.filePath(file)), Is.True,
            "迁移不能删除旧Base仍可能用于回滚的缓存");
    }

    [Test]
    public void ContentAddressedStore_DoesNotReuseAcrossEnvironment()
    {
        byte[] data = { 2, 7, 1, 8, 2, 8 };
        UpdFile file = updateFile("bundles/env.bundle", data);
        UpdStore test = makeStore("base-1", true, "test");
        UpdStore prod = makeStore("base-2", true, "prod");
        commit(test, "release-1", file, data);

        Assert.That(prod.match(file, CancellationToken.None), Is.False);
        Assert.That(prod.filePath(file), Is.Not.EqualTo(test.filePath(file)));
    }

    [Test]
    public void ContentAddressedStore_SerializesUpdatesAcrossBaseIds()
    {
        UpdStore first = makeStore("base-1", true);
        UpdStore second = makeStore("base-2", true);

        using (first.takeLock())
        {
            UpdBad bad = Assert.Throws<UpdBad>(() =>
            {
                using (second.takeLock()) { }
            });
            Assert.That(bad.err.code, Is.EqualTo(UpdCode.Busy));
        }
    }

    [Test]
    public void ContentAddressedStore_PruneKeepsOtherBaseRollbackReachableBlobs()
    {
        UpdStore first = makeStore("base-1", true);
        UpdStore second = makeStore("base-2", true);
        byte[] firstData = { 1, 1, 1 };
        byte[] secondData = { 2, 2, 2 };
        byte[] staleData = { 3, 3, 3 };
        UpdFile firstFile = updateFile("bundles/first.bundle", firstData);
        UpdFile secondFile = updateFile("bundles/second.bundle", secondData);
        UpdFile staleFile = updateFile("bundles/stale.bundle", staleData);
        commit(first, "release-1", firstFile, firstData);
        commit(second, "release-2", secondFile, secondData);
        commit(first, "release-stale", staleFile, staleData);
        UpdActive firstActive = saveManifest(first, "release-1", firstFile, 1);
        UpdActive secondActive = saveManifest(second, "release-2", secondFile, 1);
        first.saveActive(firstActive);
        second.saveActive(secondActive);

        first.prune(firstActive, null);

        Assert.That(File.Exists(first.filePath(firstFile)), Is.True);
        Assert.That(File.Exists(second.filePath(secondFile)), Is.True,
            "当前Base执行GC不能删除另一个Base的活动或回滚内容");
        Assert.That(File.Exists(first.filePath(staleFile)), Is.False);
    }

    private UpdStore makeStore()
    {
        return makeStore("base-1", false);
    }

    private UpdStore makeStore(string baseId, bool contentAddressed,
        string env = "test")
    {
        return new UpdStore(new UpdCfg
        {
            env = env,
            platform = "Android",
            baseId = baseId,
            contentAddressed = contentAddressed,
        }, mRoot);
    }

    private static UpdFile updateFile(string path, byte[] data)
    {
        return new UpdFile
        {
            path = path,
            size = data.LongLength,
            sha256 = UpdHash.data(data),
        };
    }

    private static void commit(UpdStore store, string releaseId, UpdFile file,
        byte[] data)
    {
        string copy = store.copyPath(releaseId, file.path);
        File.WriteAllBytes(copy, data);
        store.stage(file, releaseId, copy, UpdPhase.Copy, CancellationToken.None);
        store.commit(file, releaseId);
    }

    private static UpdActive saveManifest(UpdStore store, string releaseId,
        UpdFile file, long seq)
    {
        UpdMan man = new UpdMan
        {
            schema = UpdLim.Schema,
            env = "test",
            releaseId = releaseId,
            platform = "Android",
            baseId = "base-" + seq,
            aotDlls = Array.Empty<string>(),
            codeDlls = new[] { "Frame_HotFix.dll.bytes", "HotFix.dll.bytes" },
            entryDll = "HotFix.dll.bytes",
            hotId = new string('a', 64),
            secret = string.Empty,
            files = new[] { file },
        };
        byte[] raw = Encoding.UTF8.GetBytes(JsonUtility.ToJson(man, false));
        store.saveMan(releaseId, raw);
        return new UpdActive
        {
            schema = UpdLim.Schema,
            seq = seq,
            releaseId = releaseId,
            manifestSha = UpdHash.data(raw),
            manifestSize = raw.LongLength,
            latestSha = new string('b', 64),
        };
    }

    private static UpdActive active(string releaseId, long seq, char hash)
    {
        return new UpdActive
        {
            schema = UpdLim.Schema,
            seq = seq,
            releaseId = releaseId,
            manifestSha = new string(hash, 64),
            manifestSize = 1,
            latestSha = new string(char.ToUpperInvariant(hash), 64).ToLowerInvariant(),
        };
    }
}
