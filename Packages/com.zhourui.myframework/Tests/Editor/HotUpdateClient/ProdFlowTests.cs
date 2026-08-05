using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;

public sealed class ProdFlowTests
{
	const string PRIVATE_KEY = @"-----BEGIN EC PRIVATE KEY-----
MHcCAQEEIA2cf6+WptVEp4gue/gUp9Foqfcp9Ukcgi9H/r63+L4CoAoGCCqGSM49
AwEHoUQDQgAExfOti6UHch1nfkzc9nYeEwKwZn6+o08ZgeO/K4P1Mn9xOVlEOIQF
DOs0b/ryx/L+8xFS9Sf0tFIvuuIViBcHeg==
-----END EC PRIVATE KEY-----
";
	const string PUBLIC_KEY = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAExfOti6UHch1nfkzc9nYeEwKwZn6+" +
		"o08ZgeO/K4P1Mn9xOVlEOIQFDOs0b/ryx/L+8xFS9Sf0tFIvuuIViBcHeg==";

	string mRoot;
	string mStage;
	string mOutput;
	string mKey;

	[SetUp]
	public void SetUp()
	{
		string temp = Directory.Exists("/private/tmp") ? "/private/tmp" : Path.GetTempPath();
		mRoot = Path.Combine(temp, "myframework-prod-" + Guid.NewGuid().ToString("N"));
		mStage = Path.Combine(mRoot, "stage");
		mOutput = Path.Combine(mRoot, "output");
		mKey = Path.Combine(mRoot, "private.pem");
		Directory.CreateDirectory(mRoot);
		Directory.CreateDirectory(mOutput);
		File.WriteAllText(mKey, PRIVATE_KEY);
	}

	[TearDown]
	public void TearDown()
	{
		if (Directory.Exists(mRoot)) Directory.Delete(mRoot, true);
	}

	[Test]
	public void MakeAllRunsCanonicalStepsAndPublishesAtomically()
	{
		List<string> calls = new();
		IProdStep managed = new Step("managed-code", ProdOrder.MANAGED_CODE, calls,
			ctx => writeManaged(ctx.stage));
		IProdStep assets = new Step("asset-bundle", ProdOrder.ASSET_BUNDLE, calls,
			ctx => writeAssets(ctx.stage, "bundle-v1"));
		ProdFlow flow = new(new ProdReq
		{
			stage = mStage,
			release = request(true),
			// 故意反序传入，编排器必须按order执行。
			steps = new[] { managed, assets },
		});

		string created = flow.makeAll();

		Assert.That(created, Is.EqualTo("test-Android-base-flow-1"));
		Assert.That(calls, Is.EqualTo(new[]
		{
			"check:asset-bundle", "check:managed-code",
			"run:asset-bundle", "run:managed-code",
		}));
		Assert.That(File.ReadAllText(Path.Combine(mStage, "ui", "main.unity3d")),
			Is.EqualTo("bundle-v1"));
		Assert.That(RelBuild.verify(request(false)).releaseId, Is.EqualTo(created));
		Assert.That(flow.preview().releaseId, Is.EqualTo("test-Android-base-flow-2"));
		Assert.That(Directory.GetFileSystemEntries(mRoot, "stage.candidate-*"), Is.Empty);
		Assert.That(Directory.GetFileSystemEntries(mRoot, "stage.backup-*"), Is.Empty);
		Assert.That(File.Exists(mStage + ".prod.lock"), Is.True);
	}

	[Test]
	public void FailedStepKeepsOldStageAndPublishesNothing()
	{
		Directory.CreateDirectory(mStage);
		File.WriteAllText(Path.Combine(mStage, "old.txt"), "old-stage");
		IProdStep assets = new Step("asset-bundle", ProdOrder.ASSET_BUNDLE, null,
			ctx => writeAssets(ctx.stage, "new-bundle"));
		IProdStep broken = new Step("managed-code", ProdOrder.MANAGED_CODE, null,
			_ => throw new InvalidOperationException("compile failed"));
		ProdFlow flow = new(new ProdReq
		{
			stage = mStage,
			release = request(true),
			steps = new[] { assets, broken },
		});

		InvalidOperationException error = Assert.Throws<InvalidOperationException>(
			() => flow.makeAll());

		Assert.That(error.Message, Is.EqualTo("compile failed"));
		Assert.That(File.ReadAllText(Path.Combine(mStage, "old.txt")), Is.EqualTo("old-stage"));
		Assert.That(File.Exists(Path.Combine(mStage, "ui", "main.unity3d")), Is.False);
		Assert.That(Directory.Exists(Path.Combine(mOutput, "test", "releases")), Is.False);
		Assert.That(File.Exists(Path.Combine(mOutput, "test", "latest", "Android",
			"base-flow.json")), Is.False);
		Assert.That(Directory.GetFileSystemEntries(mRoot, "stage.candidate-*"), Is.Empty);
	}

	[Test]
	public void InvalidCandidateFailsBeforeReplacingOldStage()
	{
		Directory.CreateDirectory(mStage);
		File.WriteAllText(Path.Combine(mStage, "old.txt"), "old-stage");
		ProdFlow flow = new(new ProdReq
		{
			stage = mStage,
			release = request(true),
			steps = new IProdStep[]
			{
				new Step("asset-bundle", ProdOrder.ASSET_BUNDLE, null,
					ctx => writeAssets(ctx.stage, "bundle")),
			},
		});

		Assert.Throws<FileNotFoundException>(() => flow.makeAll());
		Assert.That(File.ReadAllText(Path.Combine(mStage, "old.txt")), Is.EqualTo("old-stage"));
		Assert.That(Directory.Exists(Path.Combine(mOutput, "test", "releases")), Is.False);
	}

	[Test]
	public void DuplicateOrderIsRejected()
	{
		IProdStep left = new Step("left", ProdOrder.ASSET_BUNDLE, null, _ => { });
		IProdStep right = new Step("right", ProdOrder.ASSET_BUNDLE, null, _ => { });

		Assert.Throws<InvalidDataException>(() => new ProdFlow(new ProdReq
		{
			stage = mStage,
			release = request(true),
			steps = new[] { left, right },
		}));
	}

	RelReq request(bool newBase)
	{
		UpdCfg cfg = makeCfg();
		return new RelReq
		{
			root = mOutput,
			privateKey = mKey,
			cfg = cfg,
			plan = HotList.fromCfg(cfg),
			newBase = newBase,
		};
	}

	static UpdCfg makeCfg()
	{
		string[] hot =
		{
			FrameBaseDefine.HOTFIX_FRAME_BYTES_FILE,
			FrameBaseDefine.HOTFIX_BYTES_FILE,
		};
		return new UpdCfg
		{
			baseUrl = "https://cdn.example.com/",
			env = "test",
			platform = FrameBaseDefine.ANDROID,
			baseId = "base-flow",
			pubKey = PUBLIC_KEY,
			aotDlls = new[] { "AotMeta.dll.bytes" },
			codeDlls = hot,
			entryDll = FrameBaseDefine.HOTFIX_BYTES_FILE,
			hotId = UpdRule.hotId(hot, FrameBaseDefine.HOTFIX_BYTES_FILE),
			secret = string.Empty,
			resList = FrameBaseDefine.AB_INDEX_FILE,
		};
	}

	static void writeManaged(string stage)
	{
		File.WriteAllBytes(Path.Combine(stage, "AotMeta.dll.bytes"), new byte[] { 1, 2, 3 });
		File.WriteAllBytes(Path.Combine(stage, FrameBaseDefine.HOTFIX_FRAME_BYTES_FILE),
			new byte[] { 4, 5, 6 });
		File.WriteAllBytes(Path.Combine(stage, FrameBaseDefine.HOTFIX_BYTES_FILE),
			new byte[] { 7, 8, 9 });
	}

	static void writeAssets(string stage, string value)
	{
		AbItem item = new()
		{
			key = "ui/main.prefab",
			name = "ui/main.prefab",
			bundle = "ui/main.unity3d",
			scene = string.Empty,
			atlas = string.Empty,
		};
		File.WriteAllBytes(Path.Combine(stage, FrameBaseDefine.AB_INDEX_FILE),
			AbIndex.encode(new[] { item }));
		string dir = Path.Combine(stage, "ui");
		Directory.CreateDirectory(dir);
		File.WriteAllText(Path.Combine(dir, "main.unity3d"), value);
	}

	sealed class Step : IProdStep
	{
		readonly List<string> mCalls;
		readonly Action<ProdCtx> mRun;
		public string name { get; }
		public int order { get; }

		public Step(string name, int order, List<string> calls, Action<ProdCtx> run)
		{
			this.name = name;
			this.order = order;
			mCalls = calls;
			mRun = run;
		}

		public void check(ProdCtx ctx)
		{
			Assert.That(ctx.candidate, Is.False);
			mCalls?.Add("check:" + name);
		}

		public void run(ProdCtx ctx)
		{
			Assert.That(ctx.candidate, Is.True);
			mCalls?.Add("run:" + name);
			mRun(ctx);
		}
	}
}
