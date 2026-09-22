using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using NUnit.Framework;

public sealed class UpdPackedSourceTests
{
    private string root;
    private string builtin;
    private UpdCfg cfg;
    private UpdFile login;
    private UpdFile fishing;
    [SetUp] public void Setup()
    {
        root = Path.Combine(Path.GetTempPath(), "upd-lazy-" + Guid.NewGuid().ToString("N"));
        builtin = Path.Combine(root, "packed");
        cfg = new UpdCfg { env = "test", platform = "Android", baseId = "base-1", contentAddressed = true };
        login = Write("ui/login.bundle", new byte[] { 1, 2, 3 });
        fishing = Write("games/fishing.bundle", new byte[] { 4, 5, 6, 7 });
        File.WriteAllText(Path.Combine(builtin, cfg.platform, UpdPackedSource.CatalogName),
            "BUILTIN-1\n" + Row(login) + Row(fishing), new UTF8Encoding(false));
    }
    [TearDown] public void Cleanup() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    [TestCase(false)] [TestCase(true)]
    public void CatalogAndLoginLeaveUnrequestedGameplayInPackage(bool apk)
    {
        string source = builtin;
        if (apk)
        {
            string archive = Path.Combine(root, "game with spaces.apk");
            using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
                foreach (var packedPath in Directory.GetFiles(builtin, "*", SearchOption.AllDirectories))
                    zip.CreateEntryFromFile(packedPath, "assets/" + packedPath.Substring(builtin.Length + 1), CompressionLevel.Optimal);
            source = "jar:" + new Uri(archive).AbsoluteUri + "!/assets";
        }
        var store = Store(source);
        Assert.That(store.hasPacked(login), Is.True);
        Assert.That(File.Exists(store.filePath(login)), Is.False, "Planning must not copy bytes");
        string path = store.resolve(login, "release-1", CancellationToken.None);
        Assert.That(File.ReadAllBytes(path), Is.EqualTo(new byte[] { 1, 2, 3 }));
        Assert.That(File.Exists(store.filePath(fishing)), Is.False, "Login must not prepare the entire release");
        Assert.That(store.resolve(login, "release-1", CancellationToken.None), Is.EqualTo(path));
    }

    [Test] public void ManifestIdentityMustMatchCatalog()
    {
        var store = Store(builtin);
        var changed = new UpdFile { path = login.path, size = login.size, sha256 = new string('a', 64) };
        Assert.That(store.hasPacked(changed), Is.False);
        Assert.Throws<UpdBad>(() => store.resolve(changed, "release-2", CancellationToken.None));
    }

    [Test] public void CorruptPackageBytesCannotBecomeCommittedResource()
    {
        File.WriteAllBytes(Path.Combine(builtin, cfg.platform, login.path), new byte[] { 7, 8, 9 });
        var store = Store(builtin);
        Assert.Throws<UpdBad>(() => store.resolve(login, "release-1", CancellationToken.None));
        Assert.That(File.Exists(store.filePath(login)), Is.False);
    }

    [Test] public void CorruptCacheIsRepairedFromMatchingPackage()
    {
        var store = Store(builtin);
        string path = store.resolve(login, "release-1", CancellationToken.None);
        File.WriteAllBytes(path, new byte[] { 9, 9, 9 });
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5));
        Assert.That(File.ReadAllBytes(store.resolve(login, "release-1", CancellationToken.None)),
            Is.EqualTo(new byte[] { 1, 2, 3 }));
    }

    [Test] public void CancelDoesNotCommitAndCanRetry()
    {
        var store = Store(builtin);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() => store.resolve(login, "release-1", cancel.Token));
        Assert.That(File.Exists(store.filePath(login)), Is.False);
        Assert.That(File.Exists(store.resolve(login, "release-1", CancellationToken.None)), Is.True);
    }

    [Test] public void NewProcessRevalidatesExistingBytesEvenWithPreservedTimestamp()
    {
        var store = Store(builtin);
        string path = store.resolve(login, "release-1", CancellationToken.None);
        var stamp = File.GetLastWriteTimeUtc(path);
        File.WriteAllBytes(path, new byte[] { 9, 9, 9 });
        File.SetLastWriteTimeUtc(path, stamp);
        var reopened = Store(builtin);
        Assert.That(File.ReadAllBytes(reopened.resolve(login, "release-1", CancellationToken.None)),
            Is.EqualTo(new byte[] { 1, 2, 3 }));
    }

    [Test] public void CatalogRejectsPathTraversal()
    {
        File.WriteAllText(Path.Combine(builtin, cfg.platform, UpdPackedSource.CatalogName),
            "BUILTIN-1\n" + login.sha256 + "\t3\t../escape\n");
        Assert.Throws<InvalidDataException>(() => new UpdPackedSource(builtin, cfg.platform));
    }

    private UpdStore Store(string source)
    {
        var store = new UpdStore(cfg, Path.Combine(root, "cache"));
        store.setPacked(new UpdPackedSource(source, cfg.platform));
        return store;
    }
    private UpdFile Write(string path, byte[] data)
    {
        string dst = Path.Combine(builtin, cfg.platform, path);
        Directory.CreateDirectory(Path.GetDirectoryName(dst)); File.WriteAllBytes(dst, data);
        return new UpdFile { path = path, sha256 = UpdHash.data(data), size = data.Length };
    }
    private static string Row(UpdFile file) => file.sha256 + "\t" + file.size + "\t" + file.path + "\n";
}
