using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

public sealed class UpdCoreOfflineTests
{
    private const string PublicKey =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE1QdaoDzs9olPLELLgmGILIeFrienyBMJSOoiK3SO73QT2HzmhTOaR568G19Hcw5AS70gykcAxGBXZpxEeCWEbQ==";
    private string mRoot;

    [SetUp]
    public void SetUp()
    {
        mRoot = Path.Combine(Path.GetTempPath(), "myframework-core-offline",
            Guid.NewGuid().ToString("N"));
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
    public async Task Run_UsesVerifiedActiveReleaseWhenLatestIsOffline()
    {
        UpdCfg cfg = config();
        UpdStore store = new UpdStore(cfg, mRoot);
        byte[] frame = { 1, 3, 3, 7 };
        byte[] entry = { 2, 4, 6, 8 };
        byte[] catalog = { 9, 9, 9 };
        UpdFile frameFile = file(UpdContract.FrameHotDll, frame);
        UpdFile entryFile = file(UpdContract.EntryDll, entry);
        UpdFile catalogFile = file(cfg.resList, catalog);
        seed(store, frameFile, frame);
        seed(store, entryFile, entry);
        seed(store, catalogFile, catalog);
        byte[] manifest = manifestOf(cfg, frameFile, entryFile, catalogFile);
        UpdActive active = new UpdActive
        {
            schema = UpdLim.Schema,
            seq = 3,
            releaseId = "release-3",
            manifestSha = UpdHash.data(manifest),
            manifestSize = manifest.Length,
            latestSha = new string('d', 64),
        };
        store.saveMan(active.releaseId, manifest);
        store.saveActive(active);
        List<UpdPhase> phases = new List<UpdPhase>();
        UpdCore core = new UpdCore(cfg, progress => phases.Add(progress.phase),
            null, mRoot, Path.Combine(mRoot, "builtin"));

        UpdRet<UpdRes> result = await core.run(CancellationToken.None);

        Assert.That(result.ok, Is.True, result.err == null ? string.Empty : result.err.ToString());
        Assert.That(result.value.releaseId, Is.EqualTo(active.releaseId));
        Assert.That(result.value.read(UpdContract.EntryDll), Is.EqualTo(entry));
        Assert.That(phases[phases.Count - 1], Is.EqualTo(UpdPhase.Ready));
    }

    private static UpdCfg config()
    {
        return new UpdCfg
        {
            baseUrl = "https://127.0.0.1:1/updates",
            env = "test",
            platform = "Android",
            baseId = "base-offline",
            pubKey = PublicKey,
            retry = 0,
            timeout = 1,
            resList = "StreamingAssets.bytes",
        };
    }

    private static UpdFile file(string path, byte[] data)
    {
        return new UpdFile
        {
            path = path,
            sha256 = UpdHash.data(data),
            size = data.Length,
        };
    }

    private static void seed(UpdStore store, UpdFile file, byte[] data)
    {
        string copy = store.copyPath("release-3", file.path);
        File.WriteAllBytes(copy, data);
        store.stage(file, "release-3", copy, UpdPhase.Copy, CancellationToken.None);
        store.commit(file, "release-3");
    }

    private static byte[] manifestOf(UpdCfg cfg, params UpdFile[] files)
    {
        string[] code = { UpdContract.FrameHotDll, UpdContract.EntryDll };
        string hotId = UpdRule.hotId(code, UpdContract.EntryDll);
        StringBuilder list = new StringBuilder();
        for (int i = 0; i < files.Length; ++i)
        {
            if (i > 0) list.Append(',');
            list.Append("{\"path\":\"").Append(files[i].path)
                .Append("\",\"sha256\":\"").Append(files[i].sha256)
                .Append("\",\"size\":").Append(files[i].size).Append('}');
        }
        string json = "{\"schema\":11,\"env\":\"test\",\"releaseId\":\"release-3\"," +
            "\"platform\":\"Android\",\"baseId\":\"base-offline\",\"aotDlls\":[]," +
            "\"codeDlls\":[\"Frame_HotFix.dll.bytes\",\"HotFix.dll.bytes\"]," +
            "\"entryDll\":\"HotFix.dll.bytes\",\"hotId\":\"" + hotId +
            "\",\"secret\":\"\",\"files\":[" + list + "]}";
        return Encoding.UTF8.GetBytes(json);
    }
}
