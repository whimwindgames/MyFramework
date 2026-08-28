using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;

public sealed class RelBuildTests
{
	[Serializable]
	sealed class LegacyBase
	{
		public int schema;
		public string env;
		public string platform;
		public string baseId;
		public string baseUrl;
		public string pubKey;
	}

	string mRoot;
	string mSource;
	string mOutput;
	string mKey;
	string mPublicKey;

	[SetUp]
	public void SetUp()
	{
		string temp = Directory.Exists("/private/tmp") ? "/private/tmp" : Path.GetTempPath();
		mRoot = Path.Combine(temp, "myframework-rel-" + Guid.NewGuid().ToString("N"));
		mSource = Path.Combine(mRoot, "source");
		mOutput = Path.Combine(mRoot, "output");
		mKey = Path.Combine(mRoot, "private.pem");
		Directory.CreateDirectory(mSource);
		Directory.CreateDirectory(mOutput);
		RelKeyPair pair = RelKey.generate();
		File.WriteAllText(mKey, pair.privatePem);
		mPublicKey = pair.publicKey;
		writeSource("bundle-v1");
	}

	[TearDown]
	public void TearDown()
	{
		if (Directory.Exists(mRoot)) Directory.Delete(mRoot, true);
	}

	[Test]
	public void BuildVerifyAdvanceAndPointRelease()
	{
		UpdCfg cfg = makeCfg();
		RelReq first = request(cfg, true);
		RelView firstView = RelBuild.preview(first);
		Assert.That(firstView.releaseId, Is.EqualTo("test-Android-base-7-1"));
		Assert.That(firstView.bundles, Is.EqualTo(new[] { "ui/main.unity3d" }));

		string release1 = RelBuild.make(first);
		UpdCfg trust = RelBuild.loadBase(mOutput, cfg.env, cfg.platform, cfg.baseId);
		Assert.That(trust.resList, Is.EqualTo(cfg.resList));
		RelCheck check1 = RelBuild.verify(request(cfg, false));
		Assert.That(release1, Is.EqualTo(firstView.releaseId));
		Assert.That(check1.releaseId, Is.EqualTo(release1));
		Assert.That(check1.seq, Is.EqualTo(1));
		assertLatest(cfg, release1, 1);

		writeSource("bundle-v2");
		RelReq second = request(cfg, false);
		string release2 = RelBuild.make(second);
		Assert.That(release2, Is.EqualTo("test-Android-base-7-2"));
		assertLatest(cfg, release2, 2);

		RelReq rollback = request(cfg, false);
		rollback.releaseId = release1;
		long seq = RelBuild.point(rollback);
		Assert.That(seq, Is.EqualTo(3));
		assertLatest(cfg, release1, 3);
		Assert.That(RelBuild.verify(request(cfg, false)).releaseId, Is.EqualTo(release1));

		string file = Path.Combine(mOutput, "test", "releases", release1, "files", "ui", "main.unity3d");
		File.WriteAllText(file, "tampered");
		Assert.Throws<InvalidDataException>(() => RelBuild.verify(request(cfg, false)));
	}

	[Test]
	public void DisposingUnpublishedCandidateLeavesNoBaseOrRelease()
	{
		UpdCfg cfg = makeCfg();
		RelReq req = request(cfg, true);
		string release;
		using (RelBuild.RelPending pending = RelBuild.prepare(req))
		{
			release = pending.releaseId;
			pending.promote();
		}

		Assert.That(Directory.Exists(Path.Combine(mOutput, "test", "releases", release)), Is.False);
		Assert.That(File.Exists(Path.Combine(mOutput, "test", "base", "Android", "base-7.json")), Is.False);
		Assert.That(File.Exists(Path.Combine(mOutput, "test", "latest", "Android", "base-7.json")), Is.False);
	}

	[Test]
	public void BuildIgnoresOperatingSystemMetadata()
	{
		File.WriteAllText(Path.Combine(mSource, ".DS_Store"), "finder");
		File.WriteAllText(Path.Combine(mSource, "ui", "._main.unity3d"),
			"resource-fork");
		UpdCfg cfg = makeCfg();

		string release = RelBuild.make(request(cfg, true));

		string files = Path.Combine(mOutput, cfg.env, "releases", release, "files");
		Assert.That(File.Exists(Path.Combine(files, ".DS_Store")), Is.False);
		Assert.That(File.Exists(Path.Combine(files, "ui", "._main.unity3d")), Is.False);
		Assert.That(RelBuild.verify(request(cfg, false)).releaseId, Is.EqualTo(release));
	}

	[Test]
	public void FrozenBaseRejectsChangedUrl()
	{
		UpdCfg cfg = makeCfg();
		RelBuild.make(request(cfg, true));
		UpdCfg changed = makeCfg();
		changed.baseUrl = "https://other.example.com/";

		Assert.Throws<InvalidDataException>(() => RelBuild.verify(request(changed, false)));
	}

	[Test]
	public void LegacyBaseDefaultsToSchema11ResourceIndex()
	{
		UpdCfg cfg = makeCfg();
		string dir = Path.Combine(mOutput, cfg.env, "base", cfg.platform);
		Directory.CreateDirectory(dir);
		LegacyBase legacy = new()
		{
			schema = UpdLim.Schema,
			env = cfg.env,
			platform = cfg.platform,
			baseId = cfg.baseId,
			baseUrl = cfg.baseUrl,
			pubKey = cfg.pubKey,
		};
		File.WriteAllText(Path.Combine(dir, cfg.baseId + ".json"),
			JsonUtility.ToJson(legacy, false));

		UpdCfg trust = RelBuild.loadBase(mOutput, cfg.env, cfg.platform, cfg.baseId);

		Assert.That(trust.resList, Is.EqualTo(FrameBaseDefine.AB_INDEX_FILE));
	}

	RelReq request(UpdCfg cfg, bool newBase)
	{
		return new RelReq
		{
			src = mSource,
			root = mOutput,
			privateKey = mKey,
			cfg = cfg,
			plan = HotList.fromCfg(cfg),
			newBase = newBase,
		};
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
			baseUrl = "https://cdn.example.com/",
			env = "test",
			platform = FrameBaseDefine.ANDROID,
			baseId = "base-7",
			pubKey = mPublicKey,
			aotDlls = new[] { "AotMeta.dll.bytes" },
			codeDlls = hot,
			entryDll = FrameBaseDefine.HOTFIX_BYTES_FILE,
			hotId = UpdRule.hotId(hot, FrameBaseDefine.HOTFIX_BYTES_FILE),
			secret = string.Empty,
			resList = FrameBaseDefine.AB_INDEX_FILE,
		};
	}

	void writeSource(string bundle)
	{
		File.WriteAllBytes(Path.Combine(mSource, "AotMeta.dll.bytes"), new byte[] { 1, 2, 3 });
		File.WriteAllBytes(Path.Combine(mSource, FrameBaseDefine.HOTFIX_FRAME_BYTES_FILE), new byte[] { 4, 5, 6 });
		File.WriteAllBytes(Path.Combine(mSource, FrameBaseDefine.HOTFIX_BYTES_FILE), new byte[] { 7, 8, 9 });
		AbItem item = new()
		{
			key = "ui/main.prefab",
			name = "ui/main.prefab",
			bundle = "ui/main.unity3d",
			scene = string.Empty,
			atlas = string.Empty,
		};
		File.WriteAllBytes(Path.Combine(mSource, FrameBaseDefine.AB_INDEX_FILE), AbIndex.encode(new[] { item }));
		string dir = Path.Combine(mSource, "ui");
		Directory.CreateDirectory(dir);
		File.WriteAllText(Path.Combine(dir, "main.unity3d"), bundle);
	}

	void assertLatest(UpdCfg cfg, string release, long seq)
	{
		string path = Path.Combine(mOutput, "test", "latest", "Android", "base-7.json");
		UpdBox box = UpdJson.box(File.ReadAllBytes(path));
		UpdRet<byte[]> opened = new UpdSign(cfg.pubKey).open(box);
		Assert.That(opened.ok, Is.True, opened.err?.ToString());
		UpdLatest latest = UpdJson.latest(opened.value);
		Assert.That(latest.releaseId, Is.EqualTo(release));
		Assert.That(latest.seq, Is.EqualTo(seq));
	}
}
