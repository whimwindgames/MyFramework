using System;
using UnityEngine;

public static class HotAsm
{
	public const string FrameHot = FrameBaseDefine.HOTFIX_FRAME;
	public const string Entry = FrameBaseDefine.HOTFIX;

	static readonly string[] sFixedAot =
	{
		"Frame_Base",
		"Frame_Game",
		"HotUpd_Client",
	};

	public static string[] fixedAot()
	{
		return (string[])sFixedAot.Clone();
	}
}

public class PlatRunSet : ScriptableObject
{
	public string mBaseUrl;
	public string mEnv;
	public string mPlatform;
	public string mBaseId;
	public string mPubKey;
	public string[] mAotDeny;

	static PlatRunSet mIns;
	public static PlatRunSet ins => mIns ??=
		Resources.Load<PlatRunSet>(FrameBaseDefine.RUN_SET_RES);

#if UNITY_EDITOR
	public static void setEdit(UpdCfg cfg, string[] deny)
	{
		clrEdit();
		if (cfg == null || deny == null)
		{
			throw new ArgumentNullException(cfg == null ? nameof(cfg) : nameof(deny));
		}
		PlatRunSet run = CreateInstance<PlatRunSet>();
		run.hideFlags = HideFlags.HideAndDontSave;
		run.mBaseUrl = cfg.baseUrl;
		run.mEnv = cfg.env;
		run.mPlatform = cfg.platform;
		run.mBaseId = cfg.baseId;
		run.mPubKey = cfg.pubKey;
		run.mAotDeny = (string[])deny.Clone();
		mIns = run;
	}

	static void clrEdit()
	{
		if (mIns != null && mIns.hideFlags == HideFlags.HideAndDontSave)
		{
			DestroyImmediate(mIns);
		}
		mIns = null;
	}
#endif

	public UpdCfg getCfg()
	{
		return new UpdCfg
		{
			baseUrl = mBaseUrl,
			env = mEnv,
			platform = mPlatform,
			baseId = mBaseId,
			pubKey = mPubKey,
			resList = FrameBaseDefine.AB_INDEX_FILE,
		};
	}

	public string[] getDeny()
	{
		return mAotDeny == null ? null : (string[])mAotDeny.Clone();
	}
}
