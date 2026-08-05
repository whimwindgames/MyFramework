using System;
using System.IO;
using UnityEditor;

// 把通用AB生产器接入ProdFlow；配置和资源依赖在check/run之间必须保持不变。
public sealed class AbProdStep : IProdStep
{
	readonly BuildTarget mTarget;
	readonly AbCfg mCfg;
	readonly string[] mRoots;
	string mHash;

	public string name => "asset-bundle";
	public int order => ProdOrder.ASSET_BUNDLE;

	public AbProdStep(BuildTarget target, string[] roots = null)
		: this(target, null, roots)
	{
	}

	public AbProdStep(BuildTarget target, AbCfg cfg, string[] roots = null)
	{
		mTarget = target;
		mCfg = cfg;
		mRoots = roots == null ? null : (string[])roots.Clone();
	}

	public void check(ProdCtx ctx)
	{
		if (ctx == null) throw new ArgumentNullException(nameof(ctx));
		string platform = platformOf(mTarget);
		if (platform != ctx.platform) throw new InvalidDataException(
			"AB构建平台与Release平台不一致:" + platform + "/" + ctx.platform);
		if (mCfg == null)
		{
			AbBuild.check(mRoots);
			mHash = AbBuild.hash(mTarget, mRoots);
		}
		else
		{
			AbBuild.check(mCfg, mRoots);
			mHash = AbBuild.hash(mTarget, mCfg, mRoots);
		}
	}

	public void run(ProdCtx ctx)
	{
		if (ctx == null) throw new ArgumentNullException(nameof(ctx));
		if (!ctx.candidate || string.IsNullOrEmpty(mHash))
			throw new InvalidOperationException("AB步骤必须先由ProdFlow校验");
		string current = mCfg == null ? AbBuild.hash(mTarget, mRoots) :
			AbBuild.hash(mTarget, mCfg, mRoots);
		if (current != mHash) throw new InvalidDataException(
			"AB配置或资源在生产校验后发生变化");
		bool ok = mCfg == null ? AbBuild.run(mTarget, ctx.stage, mRoots) :
			AbBuild.run(mTarget, ctx.stage, mCfg, mRoots);
		if (!ok || !(mCfg == null ? AbBuild.ready(ctx.stage, mRoots) :
			AbBuild.ready(ctx.stage, mCfg, mRoots)))
			throw new InvalidOperationException("AssetBundle生成失败或产物不完整");
	}

	static string platformOf(BuildTarget target)
	{
		return target switch
		{
			BuildTarget.Android => FrameBaseDefine.ANDROID,
			BuildTarget.iOS => FrameBaseDefine.IOS,
			BuildTarget.StandaloneWindows64 => FrameBaseDefine.WINDOWS,
			BuildTarget.StandaloneOSX => FrameBaseDefine.MACOS,
			BuildTarget.WebGL => FrameBaseDefine.WEBGL,
			_ => throw new InvalidOperationException("当前平台不支持AssetBundle生产:" + target),
		};
	}
}
