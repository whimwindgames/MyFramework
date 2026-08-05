using System;
using NUnit.Framework;

public sealed class UpdSignTests
{
    private const string PublicKey =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE1QdaoDzs9olPLELLgmGILIeFrienyBMJSOoiK3SO73QT2HzmhTOaR568G19Hcw5AS70gykcAxGBXZpxEeCWEbQ==";
    private const string Data = "bXlmcmFtZXdvcmstaG90dXBkYXRlLXNjaGVtYS0xMQ==";
    private const string Signature =
        "OC1RJ6un9atY/+Gg32l6/PN6rlI0eKuf90PX9piMHcozhgxXiLYSC5nGiq79xCf7WHucBf7kYXxm1C1m/N06Gw==";

    [Test]
    public void Open_AcceptsKnownEs256Vector()
    {
        UpdRet<byte[]> result = new UpdSign(PublicKey).open(box(Signature));

        Assert.That(result.ok, Is.True);
        Assert.That(result.value, Is.EqualTo(Convert.FromBase64String(Data)));
    }

    [Test]
    public void Open_RejectsTamperedSignature()
    {
        byte[] changed = Convert.FromBase64String(Signature);
        changed[0] ^= 0x01;

        UpdRet<byte[]> result = new UpdSign(PublicKey).open(box(Convert.ToBase64String(changed)));

        Assert.That(result.ok, Is.False);
        Assert.That(result.err.code, Is.EqualTo(UpdCode.Sign));
        Assert.That(result.err.phase, Is.EqualTo(UpdPhase.Latest));
    }

    [Test]
    public void Constructor_RejectsMalformedPublicKey()
    {
        UpdBad bad = Assert.Throws<UpdBad>(() => new UpdSign("not-base64"));
        Assert.That(bad.err.code, Is.EqualTo(UpdCode.Config));
    }

    private static UpdBox box(string signature)
    {
        return new UpdBox
        {
            schema = UpdLim.Schema,
            alg = "ES256",
            data = Data,
            sig = signature,
        };
    }
}
