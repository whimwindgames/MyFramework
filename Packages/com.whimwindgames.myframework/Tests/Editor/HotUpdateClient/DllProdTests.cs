using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEditor;

public sealed class DllProdTests
{
	string mRoot;
	string mAotSource;
	string mCompiled;
	string mStage;
	string mBaselineRoot;
	string mProject;
	RelKeyPair mKeys;

	[SetUp]
	public void SetUp()
	{
		string temp = Directory.Exists("/private/tmp") ? "/private/tmp" : Path.GetTempPath();
		mRoot = Path.Combine(temp, "myframework-dll-" + Guid.NewGuid().ToString("N"));
		mAotSource = Path.Combine(mRoot, "stripped-aot");
		mCompiled = Path.Combine(mRoot, "compiled");
		mStage = Path.Combine(mRoot, "stage");
		mBaselineRoot = Path.Combine(mRoot, "baselines");
		mProject = Path.GetFullPath(Path.Combine(UnityEngine.Application.dataPath, ".."));
		mKeys = RelKey.generate();
		Directory.CreateDirectory(mAotSource);
		Directory.CreateDirectory(mCompiled);
		Directory.CreateDirectory(mStage);
		copyAsm("Frame_Base", mAotSource);
		copyAsm(FrameBaseDefine.HOTFIX_FRAME, mCompiled);
		copyAsm(FrameBaseDefine.HOTFIX, mCompiled);
	}

	[TearDown]
	public void TearDown()
	{
		if (Directory.Exists(mRoot)) Directory.Delete(mRoot, true);
	}

	[Test]
	public void FrozenBaselineProducesOnlyDeclaredManagedFiles()
	{
		UpdCfg cfg = makeCfg();
		HotPlan plan = HotList.fromCfg(cfg);
		string baseline = freeze(cfg, plan);
		DllProd prod = makeProd(cfg, plan);

		DllReport report = prod.make(mStage);

		Assert.That(report.baseline, Is.EqualTo(baseline));
		Assert.That(report.codeDlls, Is.EqualTo(cfg.codeDlls));
		Assert.That(report.aotDlls, Is.EqualTo(cfg.aotDlls));
		Assert.That(report.mapPath, Is.Null);
		Assert.That(Directory.GetFiles(mStage), Has.Length.EqualTo(3));
		Assert.That(UpdHash.file(Path.Combine(mStage, FrameBaseDefine.HOTFIX_FRAME_BYTES_FILE)),
			Is.EqualTo(UpdHash.file(Path.Combine(mCompiled, FrameBaseDefine.HOTFIX_FRAME_FILE))));
		Assert.That(UpdHash.file(Path.Combine(mStage, FrameBaseDefine.HOTFIX_BYTES_FILE)),
			Is.EqualTo(UpdHash.file(Path.Combine(mCompiled, FrameBaseDefine.HOTFIX_FILE))));
		Assert.That(UpdHash.file(Path.Combine(mStage, "Frame_Base.dll.bytes")),
			Is.EqualTo(UpdHash.file(Path.Combine(mAotSource, "Frame_Base.dll"))));
		Assert.That(prod.getAot(), Is.EqualTo(new[] { "Frame_Base.dll" }));
	}

	[Test]
	public void HybridClrCompileEntryProducesConfiguredHotDlls()
	{
		UpdCfg cfg = makeCfg();
		HotPlan plan = HotList.fromCfg(cfg);
		freeze(cfg, plan);
		DllProd prod = DllBuild.make(new DllProdReq
		{
			cfg = cfg,
			plan = plan,
			target = BuildTarget.StandaloneOSX,
			baselineRoot = mBaselineRoot,
			analyzeMetadata = false,
		});

		DllReport report = prod.make(mStage);

		Assert.That(report.codeDlls, Is.EqualTo(cfg.codeDlls));
		Assert.That(File.Exists(Path.Combine(mStage,
			FrameBaseDefine.HOTFIX_FRAME_BYTES_FILE)), Is.True);
		Assert.That(File.Exists(Path.Combine(mStage,
			FrameBaseDefine.HOTFIX_BYTES_FILE)), Is.True);
		Assert.That(Directory.GetDirectories(Path.Combine(mProject, "Library", "MyFramework",
			"HotUpd"), "Compile-*", SearchOption.TopDirectoryOnly), Is.Empty);
	}

	[Test]
	public void BaselineTamperingIsRejected()
	{
		UpdCfg cfg = makeCfg();
		HotPlan plan = HotList.fromCfg(cfg);
		string baseline = freeze(cfg, plan);
		using (FileStream file = new(Path.Combine(baseline, "Frame_Base.dll"),
			FileMode.Append, FileAccess.Write, FileShare.None)) file.WriteByte(0x5a);

		InvalidDataException error = Assert.Throws<InvalidDataException>(() => makeProd(cfg, plan).check());

		StringAssert.Contains("损坏", error.Message);
	}

	[Test]
	public void ExistingBaseIdCannotBeReboundToDifferentAot()
	{
		UpdCfg cfg = makeCfg();
		HotPlan plan = HotList.fromCfg(cfg);
		freeze(cfg, plan);
		File.Delete(Path.Combine(mAotSource, "Frame_Base.dll"));
		copyAsm("Frame_Game", mAotSource);

		InvalidDataException error = Assert.Throws<InvalidDataException>(() => freeze(cfg, plan));

		StringAssert.Contains("新的Base ID", error.Message);
	}

	[Test]
	public void FrozenCapabilityRejectsNewHotAssembly()
	{
		UpdCfg original = makeCfg();
		HotPlan originalPlan = HotList.fromCfg(original);
		string baseline = freeze(original, originalPlan);
		HotCap frozen = HotList.loadCap(baseline);
		UpdCfg changed = makeCfg();
		changed.codeDlls = new[]
		{
			FrameBaseDefine.HOTFIX_FRAME_BYTES_FILE,
			"Feature.dll.bytes",
			FrameBaseDefine.HOTFIX_BYTES_FILE,
		};
		changed.hotId = UpdRule.hotId(changed.codeDlls, changed.entryDll);

		Assert.Throws<InvalidDataException>(() => HotList.fromCfg(changed, frozen));
	}

	[Test]
	public void WrongInternalAssemblyNameDoesNotReplaceOldStage()
	{
		UpdCfg cfg = makeCfg();
		HotPlan plan = HotList.fromCfg(cfg);
		freeze(cfg, plan);
		File.Copy(Path.Combine(mAotSource, "Frame_Base.dll"),
			Path.Combine(mCompiled, FrameBaseDefine.HOTFIX_FILE), true);
		string old = Path.Combine(mStage, FrameBaseDefine.HOTFIX_BYTES_FILE);
		File.WriteAllText(old, "old-stage");

		Assert.Throws<InvalidDataException>(() => makeProd(cfg, plan).make(mStage));
		Assert.That(File.ReadAllText(old), Is.EqualTo("old-stage"));
		Assert.That(Directory.GetFileSystemEntries(mRoot, "stage.managed-*"), Is.Empty);
		Assert.That(Directory.GetFileSystemEntries(mRoot, "stage.managed-backup-*"), Is.Empty);
	}

	[Test]
	public void MetadataAnalysisFailureRestoresOldStageAndTemporaryState()
	{
		UpdCfg cfg = makeCfg();
		HotPlan plan = HotList.fromCfg(cfg);
		freeze(cfg, plan);
		string old = Path.Combine(mStage, FrameBaseDefine.HOTFIX_BYTES_FILE);
		File.WriteAllText(old, "old-stage");
		string settings = Path.Combine(mProject, "ProjectSettings", "HybridCLRSettings.asset");
		bool hadSettings = File.Exists(settings);
		string settingsSha = hadSettings ? UpdHash.file(settings) : null;
		DllProd prod = DllBuild.make(new DllProdReq
		{
			cfg = cfg,
			plan = plan,
			target = BuildTarget.StandaloneOSX,
			compiledDir = mCompiled,
			baselineRoot = mBaselineRoot,
		});

		InvalidDataException error = Assert.Throws<InvalidDataException>(() => prod.make(mStage));

		StringAssert.Contains("AOT", error.Message);
		Assert.That(File.ReadAllText(old), Is.EqualTo("old-stage"));
		Assert.That(File.Exists(settings), Is.EqualTo(hadSettings));
		if (hadSettings) Assert.That(UpdHash.file(settings), Is.EqualTo(settingsSha));
		Assert.That(Directory.GetFileSystemEntries(mRoot, "stage.managed-*"), Is.Empty);
		Assert.That(Directory.GetFileSystemEntries(mRoot, "stage.managed-backup-*"), Is.Empty);
		string temp = Path.Combine(mProject, "Library", "MyFramework", "HotUpd");
		Assert.That(Directory.GetFileSystemEntries(temp, "AOTRef-*"), Is.Empty);
		string hybrid = Path.Combine(mProject, "HybridCLRData");
		if (Directory.Exists(hybrid)) Assert.That(Directory.GetFileSystemEntries(hybrid,
			"*.meta-backup-*", SearchOption.AllDirectories), Is.Empty);
	}

	[Test]
	public void DllProdStepPublishesThroughProdFlow()
	{
		UpdCfg cfg = makeCfg();
		HotPlan plan = HotList.fromCfg(cfg);
		freeze(cfg, plan);
		string output = Path.Combine(mRoot, "release-output");
		string key = Path.Combine(mRoot, "private.pem");
		File.WriteAllText(key, mKeys.privatePem);
		ProdFlow flow = new(new ProdReq
		{
			stage = mStage,
			release = new RelReq
			{
				root = output,
				privateKey = key,
				cfg = cfg,
				plan = plan,
				newBase = true,
			},
			steps = new IProdStep[]
			{
				new AssetStep(),
				DllBuild.step(BuildTarget.StandaloneOSX, cfg, plan, mCompiled, mBaselineRoot,
					false, false, false),
			},
		});

		string release = flow.makeAll();

		Assert.That(release, Is.EqualTo("test-MacOS-base-dll-1"));
		Assert.That(RelBuild.verify(new RelReq
		{
			root = output,
			cfg = cfg,
			plan = plan,
		}).releaseId, Is.EqualTo(release));
		Assert.That(File.Exists(Path.Combine(mStage, "Frame_Base.dll.bytes")), Is.True);
	}

	string freeze(UpdCfg cfg, HotPlan plan)
	{
		return AotBase.freeze(new AotBaseReq
		{
			source = mAotSource,
			root = mBaselineRoot,
			cfg = cfg,
			plan = plan,
			target = BuildTarget.StandaloneOSX,
		});
	}

	DllProd makeProd(UpdCfg cfg, HotPlan plan)
	{
		return DllBuild.make(new DllProdReq
		{
			cfg = cfg,
			plan = plan,
			target = BuildTarget.StandaloneOSX,
			compiledDir = mCompiled,
			baselineRoot = mBaselineRoot,
			analyzeMetadata = false,
		});
	}

	UpdCfg makeCfg()
	{
		string[] hot =
		{
			FrameBaseDefine.HOTFIX_FRAME_BYTES_FILE,
			FrameBaseDefine.HOTFIX_BYTES_FILE,
		};
		return new UpdCfg
		{
			baseUrl = "https://cdn.example.com/game/",
			env = "test",
			platform = FrameBaseDefine.MACOS,
			baseId = "base-dll",
			pubKey = mKeys.publicKey,
			aotDlls = new[] { "Frame_Base.dll.bytes" },
			codeDlls = hot,
			entryDll = FrameBaseDefine.HOTFIX_BYTES_FILE,
			hotId = UpdRule.hotId(hot, FrameBaseDefine.HOTFIX_BYTES_FILE),
			secret = string.Empty,
			resList = FrameBaseDefine.AB_INDEX_FILE,
		};
	}

	void copyAsm(string name, string target)
	{
		string source = Path.Combine(mProject, "Library", "ScriptAssemblies", name + ".dll");
		Assert.That(File.Exists(source), Is.True, "测试需要Unity先完成脚本编译:" + source);
		File.Copy(source, Path.Combine(target, name + ".dll"), false);
	}

	sealed class AssetStep : IProdStep
	{
		public string name => "asset-bundle";
		public int order => ProdOrder.ASSET_BUNDLE;
		public void check(ProdCtx ctx) { }

		public void run(ProdCtx ctx)
		{
			AbItem item = new()
			{
				key = "ui/main.prefab",
				name = "ui/main.prefab",
				bundle = "ui/main.unity3d",
				scene = string.Empty,
				atlas = string.Empty,
			};
			File.WriteAllBytes(Path.Combine(ctx.stage, FrameBaseDefine.AB_INDEX_FILE),
				AbIndex.encode(new[] { item }));
			string dir = Path.Combine(ctx.stage, "ui");
			Directory.CreateDirectory(dir);
			File.WriteAllText(Path.Combine(dir, "main.unity3d"), "bundle");
		}
	}
}
