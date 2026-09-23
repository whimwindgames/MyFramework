using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

public sealed class UpdVerifyTests
{
    string root;
    UpdCfg cfg;
    UpdStore store;
    UpdRes resources;
    UpdFile[] files;

    [SetUp]
    public void Setup()
    {
        root = Path.Combine(Path.GetTempPath(), "upd-verify-" + Guid.NewGuid().ToString("N"));
        cfg = new UpdCfg { env = "test", platform = "Android", baseId = "base-1",
            contentAddressed = true };
        string packed = Path.Combine(root, "packed", cfg.platform);
        Directory.CreateDirectory(packed);
        files = new[]
        {
            write(packed, "ui/first.bundle", 0x31),
            write(packed, "ui/second.bundle", 0x72),
        };
        var catalog = new StringBuilder("BUILTIN-1\n");
        foreach (UpdFile file in files)
            catalog.Append(file.sha256).Append('\t').Append(file.size).Append('\t')
                .Append(file.path).Append('\n');
        File.WriteAllText(Path.Combine(packed, UpdPackedSource.CatalogName),
            catalog.ToString(), new UTF8Encoding(false));
        store = new UpdStore(cfg, Path.Combine(root, "cache"));
        store.setPacked(new UpdPackedSource(Path.Combine(root, "packed"), cfg.platform));
        resources = UpdRes.make(store, new UpdMan { files = files },
            new UpdActive { releaseId = "release-1" }, cfg);
    }

    [TearDown]
    public void Cleanup()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    [Test]
    public void WorkerVerificationReusesIdentityForFirstPageRead()
    {
        var updates = new List<UpdVerifyReport>();
        UpdVerifyReport done = Task.Run(() => UpdVerify.all(resources, 2,
            report => updates.Add(report), CancellationToken.None)).GetAwaiter().GetResult();
        Assert.That(done.Done, Is.EqualTo(files.Length));
        Assert.That(done.Failed, Is.Zero);
        Assert.That(updates[0].Done, Is.Zero,
            "A reported snapshot must not change while workers continue.");
        Assert.That(updates[^1].Done, Is.EqualTo(files.Length));

        string firstPath = store.filePath(files[0]);
        using (var held = new FileStream(firstPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.That(resources.getPath(files[0].path), Is.EqualTo(firstPath),
                "The resource reader must reuse the verification pass instead of reopening the file.");
        }
    }

    [Test]
    public void VerificationReportsCorruptResourceAndPreservesFirstError()
    {
        string firstPath = store.resolve(files[0], resources.releaseId, CancellationToken.None);
        File.WriteAllBytes(firstPath, new byte[(int)files[0].size]);
        File.SetLastWriteTimeUtc(firstPath, DateTime.UtcNow.AddSeconds(5));
        // A damaged built-in file cannot be repaired by the signed catalog.
        string packaged = Path.Combine(root, "packed", cfg.platform, files[0].path);
        File.WriteAllBytes(packaged, new byte[(int)files[0].size]);
        UpdVerifyReport result = Task.Run(() => UpdVerify.all(resources, 2, null,
            CancellationToken.None)).GetAwaiter().GetResult();
        Assert.That(result.Failed, Is.EqualTo(1));
        Assert.That(result.FailedPath, Is.EqualTo(files[0].path));
        Assert.That(result.FirstError, Is.Not.Null);
    }

    static UpdFile write(string packed, string relative, byte fill)
    {
        byte[] data = new byte[256 * 1024];
        Array.Fill(data, fill);
        string path = Path.Combine(packed, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllBytes(path, data);
        return new UpdFile { path = relative, size = data.Length, sha256 = UpdHash.data(data) };
    }
}
