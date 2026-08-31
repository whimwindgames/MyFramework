using System;
using System.Collections.Generic;
using UnityEditor;

public static class DllBuild
{
	public static string obfMapPath(bool enabled)
	{
		return enabled ? DllObf.mapPath() : null;
	}

	public static HotPlan plan(UpdCfg cfg)
	{
		return HotList.fromCfg(cfg);
	}

	public static HotPlan plan(UpdCfg cfg, HotCap cap)
	{
		return HotList.fromCfg(cfg, cap);
	}

	public static HotPlan plan(UpdCfg cfg, HotCap cap, IEnumerable<string> baseReq)
	{
		return HotList.plan(cfg, cap, baseReq);
	}

	public static DllProd make(DllProdReq req)
	{
		return new DllProd(req);
	}

	public static DllMetaReport analyzeAot(DllMetaReq req)
	{
		return DllMeta.analyze(req);
	}

	// 返回新配置，不原地修改调用方持有的运行配置。
	public static UpdCfg withAot(UpdCfg cfg, DllMetaReport report)
	{
		if (report == null) throw new ArgumentNullException(nameof(report));
		return withAot(cfg, report.aotDlls);
	}

	public static UpdCfg withAot(UpdCfg cfg, IEnumerable<string> aotDlls)
	{
		if (cfg == null) throw new ArgumentNullException(nameof(cfg));
		UpdCfg value = cloneCfg(cfg);
		value.aotDlls = aotDlls == null ? Array.Empty<string>() :
			new List<string>(aotDlls).ToArray();
		DllMeta.requireConfigured(value.aotDlls, value.aotDlls);
		UpdRule.prod(value);
		return value;
	}

	public static DllProdStep step(BuildTarget target, UpdCfg cfg, HotPlan plan,
		string compiledDir = null, string baselineRoot = null, bool development = false,
		bool useObf = false)
	{
		return step(target, cfg, plan, compiledDir, baselineRoot, development,
			useObf, true);
	}

	// 独立重载保留原step方法的二进制签名；关闭分析只允许测试和结构诊断。
	public static DllProdStep step(BuildTarget target, UpdCfg cfg, HotPlan plan,
		string compiledDir, string baselineRoot, bool development, bool useObf,
		bool analyzeMetadata)
	{
		return new DllProdStep(new DllProdReq
		{
			target = target,
			cfg = cfg,
			plan = plan,
			compiledDir = compiledDir,
			baselineRoot = baselineRoot,
			development = development,
			useObf = useObf,
			analyzeMetadata = analyzeMetadata,
		});
	}

	public static bool runDiag(DllProdReq req, string stage, bool isDebug = false)
	{
		DllProd prod = make(req);
		prod.mAssetBundleFullPath = stage;
		return prod.buildHotFix(false, isDebug);
	}

	static UpdCfg cloneCfg(UpdCfg cfg)
	{
		return new UpdCfg
		{
			baseUrl = cfg.baseUrl,
			env = cfg.env,
			platform = cfg.platform,
			baseId = cfg.baseId,
			pubKey = cfg.pubKey,
			contentAddressed = cfg.contentAddressed,
			retry = cfg.retry,
			timeout = cfg.timeout,
			aotDlls = cfg.aotDlls == null ? null : (string[])cfg.aotDlls.Clone(),
			codeDlls = cfg.codeDlls == null ? null : (string[])cfg.codeDlls.Clone(),
			entryDll = cfg.entryDll,
			hotId = cfg.hotId,
			secret = cfg.secret,
			resList = cfg.resList,
		};
	}
}

public sealed class DllProdStep : IProdStep
{
	readonly DllProd mProd;
	readonly UpdCfg mCfg;
	readonly HotPlan mPlan;

	public string name => "managed-code";
	public int order => ProdOrder.MANAGED_CODE;
	public DllReport report { get; private set; }

	public DllProdStep(DllProdReq req)
	{
		if (req == null) throw new ArgumentNullException(nameof(req));
		mProd = new DllProd(req);
		mCfg = mProd.cfg;
		mPlan = mProd.plan;
	}

	public void check(ProdCtx ctx)
	{
		checkCtx(ctx);
		mProd.check();
	}

	public void run(ProdCtx ctx)
	{
		checkCtx(ctx);
		report = mProd.make(ctx.stage);
	}

	void checkCtx(ProdCtx ctx)
	{
		if (ctx == null) throw new ArgumentNullException(nameof(ctx));
		if (mCfg == null || mPlan == null || ctx.env != mCfg.env ||
			ctx.platform != mCfg.platform || ctx.baseId != mCfg.baseId ||
			ctx.plan == null || ctx.plan.hot.id != mPlan.hot.id ||
			!HotList.same(ctx.plan.cap, mPlan.cap))
			throw new InvalidOperationException("ProdFlow上下文与DLL生产计划不一致");
	}
}
