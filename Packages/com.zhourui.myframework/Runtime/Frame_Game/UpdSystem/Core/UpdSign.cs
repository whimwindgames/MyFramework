using System;
using Org.BouncyCastle.Asn1.X9;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;

public sealed class UpdSign
{
    private readonly ECPublicKeyParameters mKey;

    public UpdSign(string pubKey)
    {
        if (string.IsNullOrEmpty(pubKey))
        {
            UpdFail.bad(UpdCode.Config, "public_key");
        }
        mKey = readKey(pubKey);
    }

    public UpdRet<byte[]> open(UpdBox box)
    {
        try
        {
            UpdRule.box(box);
            byte[] data = UpdFmt.b64(box.data, 1, UpdLim.LatestMax);
            byte[] sig = UpdFmt.b64(box.sig, 64, 64);
            DsaDigestSigner signer = new DsaDigestSigner(new ECDsaSigner(), new Sha256Digest(),
                PlainDsaEncoding.Instance);
            signer.Init(false, mKey);
            signer.BlockUpdate(data, 0, data.Length);
            if (!signer.VerifySignature(sig))
            {
                UpdFail.bad(UpdCode.Sign, "verify", UpdPhase.Latest);
            }
            return UpdRet<byte[]>.pass(data);
        }
        catch (UpdBad bad)
        {
            return UpdRet<byte[]>.fail(bad.err);
        }
        catch (Exception ex)
        {
            return UpdRet<byte[]>.fail(new UpdErr(UpdCode.Sign, "verify", UpdPhase.Latest, ex));
        }
    }

    private static ECPublicKeyParameters readKey(string text)
    {
        try
        {
            byte[] data = UpdFmt.b64(text, 1, 512);
            ECPublicKeyParameters key = PublicKeyFactory.CreateKey(data) as ECPublicKeyParameters;
            if (key == null || key.Q == null || key.Q.IsInfinity || !key.Q.IsValid() ||
                !X9ObjectIdentifiers.Prime256v1.Equals(key.PublicKeyParamSet))
            {
                UpdFail.bad(UpdCode.Config, "public_key");
            }
            return key;
        }
        catch (UpdBad bad) when (bad.err != null && bad.err.code == UpdCode.Config)
        {
            throw;
        }
        catch (Exception ex)
        {
            UpdFail.bad(UpdCode.Config, "public_key", UpdPhase.Idle, ex);
            return null;
        }
    }
}
