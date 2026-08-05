using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

public sealed class AbBuildTests
{
	string mRoot;
	string mAssetRoot;
	string mAssetPath;
	string mBundleOut;
	string mReleaseOut;
	string mKey;
	string mPublicKey;
	AbCfg mCfg;

	[SetUp]
	public void SetUp()
	{
		string token = Guid.NewGuid().ToString("N");
		string temp = Directory.Exists("/private/tmp") ? "/private/tmp" : Path.GetTempPath();
		mRoot = Path.Combine(temp, "myframework-ab-" + token);
		mBundleOut = Path.Combine(mRoot, "bundles");
		mReleaseOut = Path.Combine(mRoot, "release");
		mKey = Path.Combine(mRoot, "private.pem");
		Directory.CreateDirectory(mRoot);
		Directory.CreateDirectory(mReleaseOut);
		RelKeyPair pair = RelKey.generate();
		File.WriteAllText(mKey, pair.privatePem);
		mPublicKey = pair.publicKey;

		mAssetRoot = "Assets/__MyFrameworkAbTests_" + token;
		mAssetPath = mAssetRoot + "/data.txt";
		Directory.CreateDirectory(assetFull(mAssetRoot));
		File.WriteAllText(assetFull(mAssetPath), "bundle-v1");
		AssetDatabase.ImportAsset(mAssetPath,
			ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);

		mCfg = ScriptableObject.CreateInstance<AbCfg>();
		mCfg.schemaVersion = AbCfg.SCHEMA;
		mCfg.zip = AbZip.Raw;
		mCfg.groups = new List<AbGroup>
		{
			new()
			{
				id = "test-data",
				bundleName = "test/data.unity3d",
				entries = new List<AbEntry>
				{
					new()
					{
						guid = AssetDatabase.AssetPathToGUID(mAssetPath),
						address = "test/data.txt",
					},
				},
			},
		};
	}

	[TearDown]
	public void TearDown()
	{
		if (mCfg != null) UnityEngine.Object.DestroyImmediate(mCfg);
		if (!string.IsNullOrEmpty(mAssetRoot)) AssetDatabase.DeleteAsset(mAssetRoot);
		if (Directory.Exists(mRoot)) Directory.Delete(mRoot, true);
	}

	[Test]
	public void ExplicitBuildProducesIndexAndChangesWithAsset()
	{
		string hash1 = AbBuild.hash(BuildTarget.StandaloneOSX, mCfg, null);
		Assert.That(AbBuild.run(BuildTarget.StandaloneOSX, mBundleOut, mCfg, null), Is.True);
		Assert.That(AbBuild.ready(mBundleOut, mCfg, null), Is.True);

		List<AbItem> items = AbIndex.decode(File.ReadAllBytes(Path.Combine(mBundleOut,
			FrameBaseDefine.AB_INDEX_FILE)));
		Assert.That(items, Has.Count.EqualTo(1));
		Assert.That(items[0].key, Is.EqualTo("test/data.txt"));
		Assert.That(items[0].bundle, Is.EqualTo("test/data.unity3d"));
		AssetBundle bundle = AssetBundle.LoadFromFile(Path.Combine(mBundleOut,
			"test", "data.unity3d"));
		try
		{
			Assert.That(bundle, Is.Not.Null);
			TextAsset text = bundle.LoadAsset<TextAsset>("test/data.txt");
			Assert.That(text, Is.Not.Null);
			Assert.That(text.text, Is.EqualTo("bundle-v1"));
		}
		finally
		{
			if (bundle != null) bundle.Unload(true);
		}

		File.WriteAllText(assetFull(mAssetPath), "bundle-v2");
		AssetDatabase.ImportAsset(mAssetPath,
			ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
		string hash2 = AbBuild.hash(BuildTarget.StandaloneOSX, mCfg, null);
		Assert.That(hash2, Is.Not.EqualTo(hash1));
		Assert.That(AbBuild.run(BuildTarget.StandaloneOSX, mBundleOut, mCfg, null), Is.True);
		Assert.That(AbBuild.ready(mBundleOut, mCfg, null), Is.True);
	}

	[Test]
	public void AbProdStepPublishesThroughProdFlow()
	{
		UpdCfg cfg = makeUpdCfg();
		ProdFlow flow = new(new ProdReq
		{
			stage = mBundleOut,
			release = new RelReq
			{
				root = mReleaseOut,
				privateKey = mKey,
				cfg = cfg,
				plan = HotList.fromCfg(cfg),
				newBase = true,
			},
			steps = new IProdStep[]
			{
				new AbProdStep(BuildTarget.StandaloneOSX, mCfg),
				new ManagedStep(),
			},
		});

		string release = flow.makeAll();

		Assert.That(release, Is.EqualTo("test-MacOS-base-ab-1"));
		Assert.That(AbBuild.ready(mBundleOut, mCfg, null), Is.True);
		RelCheck check = RelBuild.verify(new RelReq
		{
			root = mReleaseOut,
			cfg = cfg,
			plan = HotList.fromCfg(cfg),
		});
		Assert.That(check.releaseId, Is.EqualTo(release));
	}

	[Test]
	public void PlanRejectsDuplicateStableAddress()
	{
		string other = mAssetRoot + "/other.txt";
		File.WriteAllText(assetFull(other), "other");
		AssetDatabase.ImportAsset(other,
			ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
		mCfg.groups.Add(new AbGroup
		{
			id = "test-other",
			bundleName = "test/other.unity3d",
			entries = new List<AbEntry>
			{
				new()
				{
					guid = AssetDatabase.AssetPathToGUID(other),
					address = "test/data.txt",
				},
			},
		});

		InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
			AbCheck.need(AbPlan.make(mCfg)));
		Assert.That(error.Message, Does.Contain("资源地址重复"));
	}

	[Test]
	public void BuildRejectsRelativeOutput()
	{
		Assert.Throws<InvalidDataException>(() => AbBuild.run(
			BuildTarget.StandaloneOSX, "Library/relative-ab", mCfg, null));
	}

	UpdCfg makeUpdCfg()
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
			platform = FrameBaseDefine.MACOS,
			baseId = "base-ab",
			pubKey = mPublicKey,
			aotDlls = new[] { "AotMeta.dll.bytes" },
			codeDlls = hot,
			entryDll = FrameBaseDefine.HOTFIX_BYTES_FILE,
			hotId = UpdRule.hotId(hot, FrameBaseDefine.HOTFIX_BYTES_FILE),
			secret = string.Empty,
			resList = FrameBaseDefine.AB_INDEX_FILE,
		};
	}

	static string assetFull(string assetPath)
	{
		string rel = assetPath.Substring("Assets".Length).TrimStart('/');
		return Path.Combine(Application.dataPath, rel);
	}

	sealed class ManagedStep : IProdStep
	{
		public string name => "managed-code";
		public int order => ProdOrder.MANAGED_CODE;

		public void check(ProdCtx ctx)
		{
		}

		public void run(ProdCtx ctx)
		{
			File.WriteAllBytes(Path.Combine(ctx.stage, "AotMeta.dll.bytes"),
				new byte[] { 1, 2, 3 });
			File.WriteAllBytes(Path.Combine(ctx.stage,
				FrameBaseDefine.HOTFIX_FRAME_BYTES_FILE), new byte[] { 4, 5, 6 });
			File.WriteAllBytes(Path.Combine(ctx.stage,
				FrameBaseDefine.HOTFIX_BYTES_FILE), new byte[] { 7, 8, 9 });
		}
	}
}
