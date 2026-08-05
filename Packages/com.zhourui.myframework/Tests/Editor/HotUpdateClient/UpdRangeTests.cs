using NUnit.Framework;

public sealed class UpdRangeTests
{
    [Test]
    public void FullDownload_AcceptsExactLength()
    {
        Assert.That(UpdRange.check(0, 100, 200, "100", null), Is.Null);
    }

    [Test]
    public void Resume_AcceptsExactContentRange()
    {
        Assert.That(UpdRange.check(40, 100, 206, "60", "bytes 40-99/100"), Is.Null);
    }

    [Test]
    public void Resume_RejectsServerThatIgnoresRange()
    {
        UpdErr err = UpdRange.check(40, 100, 200, "100", null);
        Assert.That(err.code, Is.EqualTo(UpdCode.Range));
        Assert.That(err.detail, Is.EqualTo("range_reset"));
        Assert.That(err.canRetry, Is.True);
    }

    [TestCase("59", "bytes 40-99/100", "content_length")]
    [TestCase("60", "bytes 41-99/100", "content_range")]
    public void Resume_RejectsInconsistentHeaders(string length, string range,
        string detail)
    {
        UpdErr err = UpdRange.check(40, 100, 206, length, range);
        Assert.That(err.code, Is.EqualTo(UpdCode.Range));
        Assert.That(err.detail, Is.EqualTo(detail));
    }

    [TestCase(404, false)]
    [TestCase(408, true)]
    [TestCase(429, true)]
    [TestCase(503, true)]
    public void HttpStatus_ClassifiesRetryability(long status, bool retry)
    {
        UpdErr err = UpdRange.check(0, 100, status, null, null);
        Assert.That(err.code, Is.EqualTo(UpdCode.Http));
        Assert.That(err.http, Is.EqualTo(status));
        Assert.That(err.canRetry, Is.EqualTo(retry));
    }
}
