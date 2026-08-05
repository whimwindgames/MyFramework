using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

public sealed class UpdBuiltinTests
{
    private string mRoot;
    private UpdStore mStore;
    private string mBuiltin;

    [SetUp]
    public void SetUp()
    {
        mRoot = Path.Combine(Path.GetTempPath(), "myframework-builtin-tests",
            Guid.NewGuid().ToString("N"));
        mBuiltin = Path.Combine(mRoot, "builtin");
        mStore = new UpdStore(new UpdCfg
        {
            env = "test",
            platform = "Android",
            baseId = "base-1",
        }, Path.Combine(mRoot, "store"));
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
    public async Task Copy_StagesMatchingBuiltinFile()
    {
        byte[] data = { 3, 1, 4, 1, 5, 9 };
        UpdFile file = fileOf("files/data.bytes", data);
        writeBuiltin(file.path, data);
        UpdBuiltin builtin = new UpdBuiltin(mStore, 5, "Android", mBuiltin);

        UpdRet<bool> result = await builtin.copy(file, "release-1",
            CancellationToken.None);

        Assert.That(result.ok, Is.True);
        Assert.That(result.value, Is.True);
        Assert.That(mStore.partMatch("release-1", file, CancellationToken.None),
            Is.True);
    }

    [Test]
    public async Task Copy_DoesNotTrustMismatchedBuiltinFile()
    {
        byte[] expected = { 1, 2, 3 };
        UpdFile file = fileOf("files/data.bytes", expected);
        writeBuiltin(file.path, new byte[] { 7, 8, 9 });
        UpdBuiltin builtin = new UpdBuiltin(mStore, 5, "Android", mBuiltin);

        UpdRet<bool> result = await builtin.copy(file, "release-1",
            CancellationToken.None);

        Assert.That(result.ok, Is.True);
        Assert.That(result.value, Is.False);
        Assert.That(mStore.partSize("release-1", file), Is.Zero);
    }

    private void writeBuiltin(string relative, byte[] data)
    {
        string path = Path.Combine(mBuiltin, "Android",
            relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllBytes(path, data);
    }

    private static UpdFile fileOf(string path, byte[] data)
    {
        return new UpdFile
        {
            path = path,
            sha256 = UpdHash.data(data),
            size = data.Length,
        };
    }
}
