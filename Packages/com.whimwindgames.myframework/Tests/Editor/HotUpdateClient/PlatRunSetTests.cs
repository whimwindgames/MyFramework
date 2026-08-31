using NUnit.Framework;
using UnityEngine;

public sealed class PlatRunSetTests
{
    [Test]
    public void GetCfg_PreservesArcadeHubDefaults()
    {
        PlatRunSet run = ScriptableObject.CreateInstance<PlatRunSet>();
        try
        {
            run.mBaseUrl = "https://updates.example.com";
            run.mEnv = "test";
            run.mPlatform = "Android";
            run.mBaseId = "base-1";
            run.mPubKey = "key";
            run.mContentAddressed = true;

            UpdCfg cfg = run.getCfg();

            Assert.That(cfg.resList, Is.EqualTo(FrameBaseDefine.AB_INDEX_FILE));
            Assert.That(cfg.baseUrl, Is.EqualTo(run.mBaseUrl));
            Assert.That(cfg.contentAddressed, Is.True);
        }
        finally
        {
            Object.DestroyImmediate(run);
        }
    }

    [Test]
    public void FixedAot_ReturnsDefensiveCopy()
    {
        string[] first = HotAsm.fixedAot();
        first[0] = "changed";
        Assert.That(HotAsm.fixedAot()[0], Is.EqualTo("Frame_Base"));
    }
}
