using System.Text;
using NUnit.Framework;

public sealed class UpdFormatAndJsonTests
{
    [TestCase("files/catalog.json")]
    [TestCase("Frame_HotFix.dll.bytes")]
    [TestCase("a_b-c.1/file.bytes")]
    public void Path_AcceptsPortableRelativePath(string path)
    {
        Assert.That(UpdFmt.isPath(path), Is.True);
    }

    [TestCase("")]
    [TestCase("/rooted")]
    [TestCase("../escape")]
    [TestCase("a/../escape")]
    [TestCase("a\\b")]
    [TestCase("CON")]
    [TestCase("com1.txt")]
    [TestCase("trailing.")]
    public void Path_RejectsTraversalAndPlatformHazards(string path)
    {
        Assert.That(UpdFmt.isPath(path), Is.False);
    }

    [Test]
    public void Latest_ParsesExactSchema()
    {
        const string json = "{\"schema\":11,\"env\":\"test\",\"platform\":\"Android\"," +
            "\"baseId\":\"base-1\",\"seq\":7,\"releaseId\":\"release-7\"," +
            "\"manifestSha\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"," +
            "\"manifestSize\":123}";

        UpdLatest latest = UpdJson.latest(Encoding.UTF8.GetBytes(json));

        Assert.That(latest.seq, Is.EqualTo(7));
        Assert.That(latest.releaseId, Is.EqualTo("release-7"));
        Assert.That(latest.manifestSize, Is.EqualTo(123));
    }

    [TestCase("{\"schema\":11,\"schema\":11,\"env\":\"test\",\"platform\":\"Android\",\"baseId\":\"base-1\",\"seq\":1,\"releaseId\":\"r1\",\"manifestSha\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"manifestSize\":1}")]
    [TestCase("{\"schema\":11,\"env\":\"test\",\"platform\":\"Android\",\"baseId\":\"base-1\",\"seq\":1,\"releaseId\":\"r1\",\"manifestSha\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"manifestSize\":1,\"extra\":true}")]
    [TestCase("{/*comment*/\"schema\":11,\"env\":\"test\",\"platform\":\"Android\",\"baseId\":\"base-1\",\"seq\":1,\"releaseId\":\"r1\",\"manifestSha\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"manifestSize\":1}")]
    public void Latest_RejectsAmbiguousJson(string json)
    {
        UpdBad bad = Assert.Throws<UpdBad>(() => UpdJson.latest(Encoding.UTF8.GetBytes(json)));
        Assert.That(bad.err.code, Is.EqualTo(UpdCode.Schema));
    }
}
