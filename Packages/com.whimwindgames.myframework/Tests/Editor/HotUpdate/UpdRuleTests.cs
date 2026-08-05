using NUnit.Framework;

public sealed class UpdRuleTests
{
    private const string ShaA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string ShaB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string ShaC = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";

    [Test]
    public void Config_AcceptsHttpsWithoutMutableUrlParts()
    {
        Assert.DoesNotThrow(() => UpdRule.cfg(cfg()));
    }

    [TestCase("http://updates.example.com/game")]
    [TestCase("https://user@updates.example.com/game")]
    [TestCase("https://updates.example.com/game?channel=test")]
    [TestCase("https://updates.example.com/game#latest")]
    public void Config_RejectsUnsafeBaseUrl(string url)
    {
        UpdCfg value = cfg();
        value.baseUrl = url;
        UpdBad bad = Assert.Throws<UpdBad>(() => UpdRule.cfg(value));
        Assert.That(bad.err.code, Is.EqualTo(UpdCode.Config));
    }

    [Test]
    public void State_RejectsSequenceRollback()
    {
        UpdCfg value = cfg();
        UpdLatest head = latest();
        head.seq = 4;
        UpdState state = new UpdState
        {
            schema = UpdLim.Schema,
            env = value.env,
            platform = value.platform,
            baseId = value.baseId,
            seq = 5,
            releaseId = "release-5",
            latestSha = ShaB,
        };

        UpdBad bad = Assert.Throws<UpdBad>(() => UpdRule.state(value, head, ShaA, state));
        Assert.That(bad.err.code, Is.EqualTo(UpdCode.State));
        Assert.That(bad.err.detail, Is.EqualTo("latest_seq"));
    }

    [Test]
    public void Manifest_RequiresEveryBootAndResourceFile()
    {
        UpdCfg value = cfg();
        UpdLatest head = latest();
        UpdMan manifest = man(value, includeCatalog: false);

        UpdBad bad = Assert.Throws<UpdBad>(() => UpdRule.man(value, head, manifest));
        Assert.That(bad.err.code, Is.EqualTo(UpdCode.Schema));
        Assert.That(bad.err.detail, Is.EqualTo("required_file"));
    }

    [Test]
    public void Manifest_ReturnsReleaseSpecificConfig()
    {
        UpdCfg value = cfg();
        UpdMan manifest = man(value, includeCatalog: true);

        UpdCfg release = UpdRule.man(value, latest(), manifest);

        Assert.That(release.entryDll, Is.EqualTo(UpdContract.EntryDll));
        Assert.That(release.codeDlls, Is.EqualTo(manifest.codeDlls));
        Assert.That(release.codeDlls, Is.Not.SameAs(manifest.codeDlls));
        Assert.That(release.resList, Is.EqualTo(value.resList));
    }

    private static UpdCfg cfg()
    {
        return new UpdCfg
        {
            baseUrl = "https://updates.example.com/game",
            env = "test",
            platform = "Android",
            baseId = "base-1",
            pubKey = "public-key",
            retry = 3,
            timeout = 30,
            resList = "catalog.json",
        };
    }

    private static UpdLatest latest()
    {
        return new UpdLatest
        {
            schema = UpdLim.Schema,
            env = "test",
            platform = "Android",
            baseId = "base-1",
            seq = 7,
            releaseId = "release-7",
            manifestSha = ShaA,
            manifestSize = 123,
        };
    }

    private static UpdMan man(UpdCfg value, bool includeCatalog)
    {
        string[] code = { UpdContract.FrameHotDll, UpdContract.EntryDll };
        UpdFile[] files = includeCatalog
            ? new[]
            {
                file(UpdContract.FrameHotDll, ShaA),
                file(UpdContract.EntryDll, ShaB),
                file(value.resList, ShaC),
            }
            : new[]
            {
                file(UpdContract.FrameHotDll, ShaA),
                file(UpdContract.EntryDll, ShaB),
            };
        return new UpdMan
        {
            schema = UpdLim.Schema,
            env = value.env,
            releaseId = "release-7",
            platform = value.platform,
            baseId = value.baseId,
            aotDlls = new string[0],
            codeDlls = code,
            entryDll = UpdContract.EntryDll,
            hotId = UpdRule.hotId(code, UpdContract.EntryDll),
            secret = string.Empty,
            files = files,
        };
    }

    private static UpdFile file(string path, string sha)
    {
        return new UpdFile { path = path, sha256 = sha, size = 1 };
    }
}
