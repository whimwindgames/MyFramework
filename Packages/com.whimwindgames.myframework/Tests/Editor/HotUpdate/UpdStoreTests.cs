using System;
using System.IO;
using System.Threading;
using NUnit.Framework;

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

    private UpdStore makeStore()
    {
        return new UpdStore(new UpdCfg
        {
            env = "test",
            platform = "Android",
            baseId = "base-1",
        }, mRoot);
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
