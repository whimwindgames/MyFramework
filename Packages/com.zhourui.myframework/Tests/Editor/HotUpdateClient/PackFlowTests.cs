using System;
using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

public sealed class PackFlowTests
{
	string mRoot;
	string mStage;
	string mStripped;
	string mBaselines;
	string mOutput;
	string mReleaseOutput;
	string mPrivateKey;
	string mPublicKey;
	string mProject;
	string mRunPath;
	string mEmbedPath;
	bool mHadRun;
	bool mHadEmbed;
	bool mHadHybridSettings;

	[SetUp]
	public void SetUp()
	{
		string temp = Directory.Exists("/private/tmp") ? "/private/tmp" : Path.GetTempPath();
		mRoot = Path.Combine(temp, "myframework-pack-" + Guid.NewGuid().ToString("N"));
		mStage = Path.Combine(mRoot, "stage");
		mStripped = Path.Combine(mRoot, "stripped");
		mBaselines = Path.Combine(mRoot, "baselines");
		mOutput = Path.Combine(mRoot, "player-base");
		mReleaseOutput = Path.Combine(mRoot, "release-output");
		mPrivateKey = Path.Combine(mRoot, "private.pem");
		mProject = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
		mRunPath = "Assets/Resources/PlatRunSet.asset";
		mEmbedPath = Path.Combine(mProject, "Assets", "StreamingAssets");
		mHadRun = AssetDatabase.LoadMainAssetAtPath(mRunPath) != null;
		mHadEmbed = Directory.Exists(mEmbedPath);
		mHadHybridSettings = File.Exists(Path.Combine(mProject,
			"ProjectSettings", "HybridCLRSettings.asset"));
		Directory.CreateDirectory(mStage);
		Directory.CreateDirectory(mStripped);
		AbItem item = new()
		{
			key = "ui/main.prefab",
			name = "ui/main.prefab",
			bundle = "ui/main.unity3d",
			scene = string.Empty,
			atlas = string.Empty,
		};
		File.WriteAllBytes(Path.Combine(mStage, FrameBaseDefine.AB_INDEX_FILE),
			AbIndex.encode(new[] { item }));
		string bundle = Path.Combine(mStage, "ui");
		Directory.CreateDirectory(bundle);
		File.WriteAllText(Path.Combine(bundle, "main.unity3d"), "bundle-data");
		copyAsm("Frame_Base", mStripped);
		copyAsmBytes("Frame_Base", Path.Combine(mStage, "Frame_Base.dll.bytes"));
		copyAsmBytes(FrameBaseDefine.HOTFIX_FRAME,
			Path.Combine(mStage, FrameBaseDefine.HOTFIX_FRAME_BYTES_FILE));
		copyAsmBytes(FrameBaseDefine.HOTFIX,
			Path.Combine(mStage, FrameBaseDefine.HOTFIX_BYTES_FILE));
		RelKeyPair pair = RelKey.generate();
		File.WriteAllText(mPrivateKey, pair.privatePem);
		mPublicKey = pair.publicKey;
	}

	[TearDown]
	public void TearDown()
	{
		if (!mHadRun && AssetDatabase.LoadMainAssetAtPath(mRunPath) != null)
			AssetDatabase.DeleteAsset(mRunPath);
		if (!mHadEmbed && Directory.Exists(mEmbedPath))
			AssetDatabase.DeleteAsset("Assets/StreamingAssets");
		if (!mHadEmbed && File.Exists(mEmbedPath + ".meta")) File.Delete(mEmbedPath + ".meta");
		string hybrid = Path.Combine(mProject, "ProjectSettings", "HybridCLRSettings.asset");
		if (!mHadHybridSettings && File.Exists(hybrid)) File.Delete(hybrid);
		AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
		if (Directory.Exists(mRoot)) Directory.Delete(mRoot, true);
	}

	[Test]
	public void SuccessfulBuildPublishesPlayerAndBaselineTogether()
	{
		UpdCfg cfg = makeCfg();
		HotPlan plan = HotList.fromCfg(cfg);
		FakePackApi api = new(mStripped, cfg, mRunPath, true);
		PackFlow flow = makeFlow(cfg, plan, api, true, true);

		PackReport report = flow.build();

		Assert.That(api.validated, Is.EqualTo(1));
		Assert.That(api.generated, Is.EqualTo(1));
		Assert.That(api.built, Is.EqualTo(1));
		Assert.That(report.outputRoot, Is.EqualTo(mOutput));
		Assert.That(report.player, Is.EqualTo(Path.Combine(mOutput, "Game.app")));
		Assert.That(report.embedded, Is.True);
		Assert.That(Directory.Exists(report.player), Is.True);
		Assert.That(Directory.Exists(report.baseline), Is.True);
		Assert.That(report.releaseId, Is.EqualTo("test-MacOS-base-pack-1"));
		Assert.That(RelBuild.verify(new RelReq
		{
			root = mReleaseOutput,
			cfg = cfg,
			plan = plan,
		}).releaseId, Is.EqualTo(report.releaseId));
		Assert.That(AotBase.check(new AotBaseReq
		{
			root = mBaselines,
			cfg = cfg,
			plan = plan,
			target = BuildTarget.StandaloneOSX,
		}).dlls, Is.EqualTo(new[] { "Frame_Base.dll" }));
		Assert.That(Directory.GetFileSystemEntries(mRoot, "*.candidate-*"), Is.Empty);
		assertProjectRestored();
	}

	[Test]
	public void FailedPlayerBuildLeavesNoPlayerOrBaseline()
	{
		UpdCfg cfg = makeCfg();
		HotPlan plan = HotList.fromCfg(cfg);
		FakePackApi api = new(mStripped, cfg, mRunPath, false);
		PackFlow flow = makeFlow(cfg, plan, api, true, false);

		Assert.Throws<UnityEditor.Build.BuildFailedException>(() => flow.build());

		Assert.That(Directory.Exists(mOutput), Is.False);
		Assert.That(Directory.Exists(AotBase.path(mBaselines, cfg.env, cfg.baseId,
			BuildTarget.StandaloneOSX)), Is.False);
		Assert.That(Directory.GetFileSystemEntries(mRoot, "*.candidate-*"), Is.Empty);
		assertProjectRestored();
	}

	[Test]
	public void InvalidStrippedAotRollsBackSuccessfulPlayerCandidate()
	{
		File.Delete(Path.Combine(mStripped, "Frame_Base.dll"));
		copyAsm(FrameBaseDefine.HOTFIX_FRAME, mStripped);
		UpdCfg cfg = makeCfg();
		HotPlan plan = HotList.fromCfg(cfg);
		FakePackApi api = new(mStripped, cfg, mRunPath, true);
		PackFlow flow = makeFlow(cfg, plan, api, false, false);

		Assert.Throws<InvalidDataException>(() => flow.build());

		Assert.That(api.built, Is.EqualTo(1));
		Assert.That(Directory.Exists(mOutput), Is.False);
		Assert.That(Directory.Exists(AotBase.path(mBaselines, cfg.env, cfg.baseId,
			BuildTarget.StandaloneOSX)), Is.False);
		assertProjectRestored();
	}

	[Test]
	public void GenerateAllFailureRestoresTemporaryProjectConfiguration()
	{
		UpdCfg cfg = makeCfg();
		HotPlan plan = HotList.fromCfg(cfg);
		FakePackApi api = new(mStripped, cfg, mRunPath, true) { throwGenerate = true };
		PackFlow flow = makeFlow(cfg, plan, api, true, false);

		Assert.Throws<InvalidOperationException>(() => flow.build());

		Assert.That(api.built, Is.Zero);
		Assert.That(Directory.Exists(mOutput), Is.False);
		assertProjectRestored();
	}

	[Test]
	public void AotPendingPromotionRollsBackUntilAccepted()
	{
		UpdCfg cfg = makeCfg();
		HotPlan plan = HotList.fromCfg(cfg);
		string target = AotBase.path(mBaselines, cfg.env, cfg.baseId,
			BuildTarget.StandaloneOSX);
		using (AotPending pending = AotBase.prepare(new AotBaseReq
		{
			source = mStripped,
			root = mBaselines,
			cfg = cfg,
			plan = plan,
			target = BuildTarget.StandaloneOSX,
		}))
		{
			Assert.That(Directory.Exists(target), Is.False);
			Assert.That(Directory.Exists(pending.path), Is.True);
			pending.promote();
			Assert.That(Directory.Exists(target), Is.True);
		}

		Assert.That(Directory.Exists(target), Is.False);
	}

	PackFlow makeFlow(UpdCfg cfg, HotPlan plan, FakePackApi api, bool embed,
		bool release)
	{
		return new PackFlow(new PackReq
		{
			cfg = cfg,
			plan = plan,
			target = BuildTarget.StandaloneOSX,
			outputRoot = mOutput,
			playerPath = "Game.app",
			scenes = new[] { "Assets/Resources/Scene/start.unity" },
			options = BuildOptions.Development,
			baselineRoot = mBaselines,
			embedStage = embed ? mStage : null,
			runSetPath = mRunPath,
			release = release ? new RelReq
			{
				src = mStage,
				root = mReleaseOutput,
				privateKey = mPrivateKey,
				cfg = cfg,
				plan = plan,
				newBase = true,
			} : null,
			api = api,
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
			baseId = "base-pack",
			pubKey = mPublicKey,
			aotDlls = new[] { "Frame_Base.dll.bytes" },
			codeDlls = hot,
			entryDll = FrameBaseDefine.HOTFIX_BYTES_FILE,
			hotId = UpdRule.hotId(hot, FrameBaseDefine.HOTFIX_BYTES_FILE),
			secret = string.Empty,
			resList = FrameBaseDefine.AB_INDEX_FILE,
		};
	}

	void assertProjectRestored()
	{
		Assert.That(AssetDatabase.LoadMainAssetAtPath(mRunPath) != null, Is.EqualTo(mHadRun));
		Assert.That(Directory.Exists(mEmbedPath), Is.EqualTo(mHadEmbed));
		Assert.That(File.Exists(Path.Combine(mProject, "ProjectSettings",
			"HybridCLRSettings.asset")), Is.EqualTo(mHadHybridSettings));
	}

	void copyAsm(string name, string target)
	{
		string source = Path.Combine(mProject, "Library", "ScriptAssemblies", name + ".dll");
		Assert.That(File.Exists(source), Is.True, "测试需要Unity先完成脚本编译:" + source);
		File.Copy(source, Path.Combine(target, name + ".dll"), false);
	}

	void copyAsmBytes(string name, string target)
	{
		string source = Path.Combine(mProject, "Library", "ScriptAssemblies", name + ".dll");
		Assert.That(File.Exists(source), Is.True);
		File.Copy(source, target, false);
	}

	sealed class FakePackApi : IPackApi
	{
		readonly string mStripped;
		readonly UpdCfg mCfg;
		readonly string mRunPath;
		readonly bool mSuccess;
		public bool throwGenerate;
		public int validated;
		public int generated;
		public int built;

		public FakePackApi(string stripped, UpdCfg cfg, string runPath, bool success)
		{
			mStripped = stripped;
			mCfg = cfg;
			mRunPath = runPath;
			mSuccess = success;
		}

		public void validate(BuildTarget target)
		{
			Assert.That(target, Is.EqualTo(BuildTarget.StandaloneOSX));
			++validated;
		}

		public void generateAll(BuildTarget target)
		{
			++generated;
			PlatRunSet run = AssetDatabase.LoadAssetAtPath<PlatRunSet>(mRunPath);
			Assert.That(run, Is.Not.Null);
			Assert.That(run.mBaseUrl, Is.EqualTo(mCfg.baseUrl));
			Assert.That(run.mBaseId, Is.EqualTo(mCfg.baseId));
			Assert.That(run.mAotDeny, Does.Contain("Frame_Base"));
			if (throwGenerate) throw new InvalidOperationException("generate failed");
		}

		public PackBuildResult build(BuildPlayerOptions options)
		{
			++built;
			if (!mSuccess) return new PackBuildResult { succeeded = false, detail = "fake failure" };
			string player = options.locationPathName;
			Directory.CreateDirectory(player);
			string info = Path.Combine(player, "Contents", "info.txt");
			Directory.CreateDirectory(Path.GetDirectoryName(info));
			File.WriteAllText(info, "fake-player");
			string source = Path.Combine(Application.dataPath, "StreamingAssets", mCfg.platform);
			if (Directory.Exists(source))
			{
				string target = Path.Combine(player, "Contents", "Resources", "Data",
					"StreamingAssets", mCfg.platform);
				copyTree(source, target);
			}
			return new PackBuildResult { succeeded = true, detail = "fake success" };
		}

		public string strippedAot(BuildTarget target) { return mStripped; }

		static void copyTree(string source, string target)
		{
			Directory.CreateDirectory(target);
			foreach (string file in Directory.GetFiles(source))
			{
				if (file.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) continue;
				File.Copy(file, Path.Combine(target, Path.GetFileName(file)), false);
			}
			foreach (string child in Directory.GetDirectories(source))
				copyTree(child, Path.Combine(target, Path.GetFileName(child)));
		}
	}
}
