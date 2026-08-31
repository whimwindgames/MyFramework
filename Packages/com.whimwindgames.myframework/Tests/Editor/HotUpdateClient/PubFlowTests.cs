using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using NUnit.Framework;
using UnityEngine;

public sealed class PubFlowTests
{
	const string TEST_SCRIPT =
		"Packages/com.whimwindgames.myframework/Tests/Editor/HotUpdateClient/PubFlowTests.cs";
	[Serializable]
	sealed class BaseTrust
	{
		public int schema;
		public string env;
		public string platform;
		public string baseId;
		public string baseUrl;
		public string pubKey;
		public string resList;
	}

	const string ENV = "test";
	const string PLATFORM = "Android";
	const string BASE = "base-1";
	const string RES_LIST = "config/res.json";
	static readonly string[] CODE_DLLS = { "Frame_HotFix.dll.bytes", "HotFix.dll.bytes" };
	const string ENTRY_DLL = "HotFix.dll.bytes";
	static readonly UTF8Encoding sUtf8 = new(false, true);

	string mRoot;
	string mPrivPem;
	string mPubKey;
	MemStore mStore;
	PubEnv mEnv;
	bool mContentAddressed;

	sealed class MemStore : IObjStore
	{
		readonly Dictionary<string, byte[]> mObjs = new(StringComparer.Ordinal);
		public readonly List<string> ops = new();
		public string clientBase { get; }

		internal MemStore(string clientBase)
		{
			this.clientBase = clientBase;
		}

		public void put(string key, string file)
		{
			ops.Add("put:" + key);
			mObjs[key] = File.ReadAllBytes(file);
		}

		public void get(string key, string file)
		{
			ops.Add("get:" + key);
			if (!mObjs.TryGetValue(key, out byte[] raw))
			{
				throw new FileNotFoundException("mem miss " + key);
			}
			File.WriteAllBytes(file, raw);
		}

		public string[] list(string prefix)
		{
			List<string> keys = new();
			foreach (string key in mObjs.Keys)
			{
				if (key.StartsWith(prefix, StringComparison.Ordinal)) keys.Add(key);
			}
			keys.Sort(StringComparer.Ordinal);
			return keys.ToArray();
		}

		public IObjLease take(string key)
		{
			ops.Add("take:" + key);
			return new MemLease();
		}

		public bool has(string key) => mObjs.ContainsKey(key);
		public byte[] raw(string key) => mObjs[key];
		public void seed(string key, byte[] raw) => mObjs[key] = raw;
		public void drop(string key) => mObjs.Remove(key);

		public void Dispose() { }

		sealed class MemLease : IObjLease
		{
			public void keep() { }
			public void Dispose() { }
		}
	}

	[SetUp]
	public void SetUp()
	{
		string temp = Directory.Exists("/private/tmp") ? "/private/tmp" : Path.GetTempPath();
		mRoot = Path.Combine(temp, "myframework-pub-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(mRoot);
		RelKeyPair pair = RelKey.generate();
		mPrivPem = Path.Combine(mRoot, "latest.pem");
		File.WriteAllText(mPrivPem, pair.privatePem);
		mPubKey = pair.publicKey;
		mStore = new MemStore("https://hot.test/");
		mContentAddressed = false;
		mEnv = new PubEnv
		{
			envIds = new[] { ENV },
			pubRoot = mRoot,
			privateKeyPath = mPrivPem,
			baseRegistry = registry,
		};
	}

	[TearDown]
	public void TearDown()
	{
		if (Directory.Exists(mRoot)) Directory.Delete(mRoot, true);
	}

	UpdCfg registry(string env, string platform, string baseId)
	{
		return new UpdCfg
		{
			baseUrl = mStore.clientBase,
			env = env,
			platform = platform,
			baseId = baseId,
			pubKey = mPubKey,
			contentAddressed = mContentAddressed,
			resList = RES_LIST,
			aotDlls = Array.Empty<string>(),
			codeDlls = CODE_DLLS,
			entryDll = ENTRY_DLL,
			hotId = UpdRule.hotId(CODE_DLLS, ENTRY_DLL),
		};
	}

	PubFlow flow()
	{
		return new PubFlow(mStore, mEnv);
	}

	string gate(string relId, string operatorId = "pub-flow-tests")
	{
		PubItem item = PubFlow.find(mEnv, PLATFORM, relId);
		RelGateReport report = new()
		{
			ok = true,
			env = item.env,
			platform = item.platform,
			baseId = item.baseId,
			releaseId = item.relId,
			phases = "project,plan,candidate",
			timeUtc = DateTime.UtcNow.ToString("o"),
			diagnostics = Array.Empty<RelGateDiagnostic>(),
		};
		return RelAudit.makeGate(mEnv, item, report, operatorId).path;
	}

	PubResult publish(PubFlow pub, string relId)
	{
		PubItem item = PubFlow.find(mEnv, PLATFORM, relId);
		return pub.pubRel(item, gate(relId));
	}

	RelGateInput gateInput(PubItem item)
	{
		UpdCfg cfg = registry(item.env, item.platform, item.baseId);
		AbPlan assets = new() { astCnt = 1 };
		AbPkg pkg = new() { name = "pipeline-tests", key = "pipeline-tests" };
		pkg.asts.Add(new AbAst
		{
			path = TEST_SCRIPT,
			key = RES_LIST,
			name = "PubFlowTests",
		});
		assets.pkgs.Add(pkg);
		return new RelGateInput(cfg, HotList.fromCfg(cfg), assets,
			new RelGateBase(cfg, new[] { "HotUpd_Client.Tests.dll" }),
			Path.Combine(mRoot, ENV, "releases", item.relId, "files"), item.relId);
	}

	byte[] signLatest(UpdLatest head)
	{
		byte[] body = sUtf8.GetBytes(JsonUtility.ToJson(head, false));
		RelSign signer = new(mPrivPem);
		UpdBox box = new()
		{
			schema = UpdLim.Schema,
			alg = "ES256",
			data = Convert.ToBase64String(body),
			sig = Convert.ToBase64String(signer.sign(body)),
		};
		return sUtf8.GetBytes(JsonUtility.ToJson(box, false));
	}

	string makeRelease(string relId, long seq, int extraFiles)
	{
		return makeRelease(relId, seq, extraFiles, BASE);
	}

	string makeRelease(string relId, long seq, int extraFiles, string baseId)
	{
		string relDir = Path.Combine(mRoot, ENV, "releases", relId);
		string filesDir = Path.Combine(relDir, "files");
		Directory.CreateDirectory(filesDir);
		List<UpdFile> files = new();
		writeFile(filesDir, "Frame_HotFix.dll.bytes", 2048, files);
		writeFile(filesDir, "HotFix.dll.bytes", 3072, files);
		writeFile(filesDir, RES_LIST, 256, files);
		for (int i = 0; i < extraFiles; ++i)
		{
			writeFile(filesDir, "bundles/bundle-" + i + ".unity3d", 4096 + i, files);
		}
		UpdMan man = new()
		{
			schema = UpdLim.Schema,
			env = ENV,
			releaseId = relId,
			platform = PLATFORM,
			baseId = baseId,
			aotDlls = Array.Empty<string>(),
			codeDlls = CODE_DLLS,
			entryDll = ENTRY_DLL,
			hotId = UpdRule.hotId(CODE_DLLS, ENTRY_DLL),
			files = files.ToArray(),
		};
		byte[] manRaw = sUtf8.GetBytes(JsonUtility.ToJson(man, false));
		File.WriteAllBytes(Path.Combine(relDir, "manifest.json"), manRaw);
		UpdLatest head = new()
		{
			schema = UpdLim.Schema,
			env = ENV,
			platform = PLATFORM,
			baseId = baseId,
			seq = seq,
			releaseId = relId,
			manifestSha = UpdHash.data(manRaw),
			manifestSize = manRaw.LongLength,
		};
		string latestDir = Path.Combine(mRoot, ENV, "latest", PLATFORM);
		Directory.CreateDirectory(latestDir);
		File.WriteAllBytes(Path.Combine(latestDir, baseId + ".json"), signLatest(head));
		return relId;
	}

	static void writeFile(string root, string path, int size, List<UpdFile> files)
	{
		string full = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
		Directory.CreateDirectory(Path.GetDirectoryName(full));
		byte[] raw = new byte[size];
		new System.Random(path.GetHashCode()).NextBytes(raw);
		File.WriteAllBytes(full, raw);
		files.Add(new UpdFile
		{
			path = path,
			sha256 = UpdHash.file(full),
			size = size,
		});
	}

	static string relFileKey(string relId, string path)
	{
		return ENV + "/releases/" + relId + "/files/" + path;
	}

	static string latestKey()
	{
		return latestKey(BASE);
	}

	static string latestKey(string baseId)
	{
		return ENV + "/latest/" + PLATFORM + "/" + baseId + ".json";
	}

	int countPut(string key)
	{
		int count = 0;
		for (int i = 0; i < mStore.ops.Count; ++i)
		{
			if (mStore.ops[i] == "put:" + key) ++count;
		}
		return count;
	}

	UpdLatest openRemoteLatest()
	{
		UpdBox box = UpdJson.box(mStore.raw(latestKey()));
		UpdRet<byte[]> opened = new UpdSign(mPubKey).open(box);
		Assert.IsTrue(opened.ok, "远端Latest验签失败");
		return UpdJson.latest(opened.value);
	}

	[Test]
	public void ScanFindsLocalRelease()
	{
		makeRelease("rel-a01", 1, 2);
		PubItem[] items = PubFlow.scan(mEnv, PLATFORM);
		Assert.AreEqual(1, items.Length);
		Assert.AreEqual(ENV, items[0].env);
		Assert.AreEqual(BASE, items[0].baseId);
		Assert.AreEqual("rel-a01", items[0].relId);
		Assert.AreEqual(5, items[0].fileCnt);
	}

	[Test]
	public void DefaultRegistryReadsFrozenReleaseBase()
	{
		writeBaseTrust();
		mEnv.baseRegistry = null;
		makeRelease("rel-base01", 1, 1);

		PubItem item = PubFlow.find(mEnv, PLATFORM, "rel-base01");

		Assert.AreEqual(BASE, item.baseId);
		Assert.AreEqual(4, item.fileCnt);
		Assert.IsTrue(UpdFmt.isSha(item.manSha));
	}

	[Test]
	public void CliScanReceiptDoesNotRequireSshOrPrivateKey()
	{
		writeBaseTrust();
		mEnv.baseRegistry = null;
		makeRelease("rel-cli01", 1, 1);
		string[] args =
		{
			"-pubAction", "scan",
			"-pubPlatform", PLATFORM,
			"-pubRoot", mRoot,
		};

		int code = PubCli.run(args, out PubReceipt receipt);

		Assert.AreEqual(0, code, receipt.error);
		Assert.IsTrue(receipt.ok);
		Assert.AreEqual(3, receipt.schema);
		Assert.AreEqual(1, receipt.items.Length);
		Assert.GreaterOrEqual(receipt.durationMs, 0);
	}

	[Test]
	public void PubRelFreshUploadExposesLatestLast()
	{
		makeRelease("rel-b01", 1, 2);
		using (PubFlow pub = flow())
		{
			PubResult result = publish(pub, "rel-b01");
			Assert.AreEqual(1, result.seq);
			Assert.IsTrue(mStore.has(ENV + "/audit/rel-b01/gate.json"));
			Assert.IsTrue(mStore.has(result.auditObjectKey));
		}
		Assert.IsTrue(mStore.has(ENV + "/releases/rel-b01/manifest.json"));
		Assert.IsTrue(mStore.has(relFileKey("rel-b01", "bundles/bundle-0.unity3d")));
		Assert.IsTrue(mStore.has(latestKey()));
		int manAt = mStore.ops.IndexOf("put:" + ENV + "/releases/rel-b01/manifest.json");
		int headAt = mStore.ops.IndexOf("put:" + latestKey());
		int fileAt = mStore.ops.IndexOf("put:" + relFileKey("rel-b01", "HotFix.dll.bytes"));
		Assert.Greater(manAt, fileAt, "Manifest必须在Release文件之后上传");
		Assert.Greater(headAt, manAt, "Latest必须最后曝光");
		Assert.AreEqual("rel-b01", openRemoteLatest().releaseId);
	}

	[Test]
	public void ContentAddressedPublish_ReusesImmutableBlobAcrossBaseIds()
	{
		mContentAddressed = true;
		makeRelease("rel-cas-a01", 1, 1, "base-1");
		string firstFile = Path.Combine(mRoot, ENV, "releases", "rel-cas-a01",
			"files", "HotFix.dll.bytes");
		string sha = UpdHash.file(firstFile);
		string blob = PubFlow.blobKey(ENV, sha);
		Assert.That(blob, Is.EqualTo(UpdHttp.blobPath(ENV, sha)),
			"发布端和客户端下载路径必须共享同一协议");
		using (PubFlow pub = flow()) publish(pub, "rel-cas-a01");

		makeRelease("rel-cas-b01", 1, 1, "base-2");
		using (PubFlow pub = flow()) publish(pub, "rel-cas-b01");

		Assert.That(mStore.has(blob), Is.True);
		Assert.That(countPut(blob), Is.EqualTo(1),
			"相同SHA-256内容跨Base只能上传一次");
		Assert.That(mStore.has(relFileKey("rel-cas-a01", "HotFix.dll.bytes")),
			Is.False);
		Assert.That(mStore.has(relFileKey("rel-cas-b01", "HotFix.dll.bytes")),
			Is.False);
		Assert.That(mStore.has(ENV + "/releases/rel-cas-a01/manifest.json"), Is.True);
		Assert.That(mStore.has(ENV + "/releases/rel-cas-b01/manifest.json"), Is.True);
		Assert.That(mStore.has(latestKey("base-1")), Is.True);
		Assert.That(mStore.has(latestKey("base-2")), Is.True);
	}

	[Test]
	public void ContentAddressedPublish_RejectsCorruptExistingBlobBeforeLatest()
	{
		mContentAddressed = true;
		makeRelease("rel-cas-good01", 1, 1, "base-1");
		string file = Path.Combine(mRoot, ENV, "releases", "rel-cas-good01",
			"files", "HotFix.dll.bytes");
		string blob = PubFlow.blobKey(ENV, UpdHash.file(file));
		using (PubFlow pub = flow()) publish(pub, "rel-cas-good01");
		byte[] corrupt = { 9, 9, 9 };
		mStore.seed(blob, corrupt);

		makeRelease("rel-cas-bad01", 1, 1, "base-2");
		using PubFlow retry = flow();
		Assert.Throws<InvalidDataException>(() => publish(retry, "rel-cas-bad01"));

		CollectionAssert.AreEqual(corrupt, mStore.raw(blob),
			"发布器不能覆盖已经存在的不可变Blob");
		Assert.That(mStore.has(latestKey("base-2")), Is.False);
	}

	[Test]
	public void PublishRetryAdoptsExistingRemoteAuditWithoutChangingIt()
	{
		makeRelease("rel-retry01", 1, 1);
		PubResult first;
		using (PubFlow pub = flow()) first = publish(pub, "rel-retry01");
		byte[] remote = (byte[])mStore.raw(first.auditObjectKey).Clone();
		File.Delete(first.audit.path);

		PubResult retried;
		using (PubFlow pub = flow()) retried = publish(pub, "rel-retry01");

		CollectionAssert.AreEqual(remote, mStore.raw(first.auditObjectKey));
		Assert.AreEqual(first.audit.sha, retried.audit.sha);
		Assert.IsTrue(File.Exists(retried.audit.path));
	}

	[Test]
	public void PubRelWithoutSignedGateEvidenceCannotTouchRemote()
	{
		makeRelease("rel-nogate01", 1, 1);
		PubItem item = PubFlow.find(mEnv, PLATFORM, "rel-nogate01");
		using PubFlow pub = flow();

		Assert.Throws<InvalidDataException>(() => pub.pubRel(item,
			Path.Combine(mRoot, "missing-gate.json")));

		Assert.IsFalse(mStore.ops.Exists(value =>
			value.StartsWith("put:", StringComparison.Ordinal)));
		Assert.IsFalse(mStore.has(latestKey()));
	}

	[Test]
	public void PubRelWithTamperedGateEvidenceCannotTouchRemote()
	{
		makeRelease("rel-badgate01", 1, 1);
		PubItem item = PubFlow.find(mEnv, PLATFORM, "rel-badgate01");
		string valid = gate(item.relId);
		UpdBox box = UpdJson.box(File.ReadAllBytes(valid));
		char replacement = box.sig[0] == 'A' ? 'B' : 'A';
		box.sig = replacement + box.sig.Substring(1);
		string tampered = Path.Combine(mRoot, "tampered-gate.json");
		File.WriteAllText(tampered, JsonUtility.ToJson(box, false), sUtf8);
		using PubFlow pub = flow();

		Assert.Throws<InvalidDataException>(() => pub.pubRel(item, tampered));

		Assert.IsFalse(mStore.ops.Exists(value =>
			value.StartsWith("put:", StringComparison.Ordinal)));
		Assert.IsFalse(mStore.has(latestKey()));
	}

	[Test]
	public void UnifiedPipelineProducesGatesPublishesAndAudits()
	{
		using IDisposable pipelineRegistry = RelPipelineRegistry.isolateForTests();
		using IDisposable gateRegistry = RelGateRegistry.isolateForTests();
		RelPipelineRegistry.bindProducer(request =>
		{
			Assert.AreEqual(ENV, request.env);
			makeRelease("rel-pipeline01", 1, 1);
			PubItem item = PubFlow.find(mEnv, PLATFORM, "rel-pipeline01");
			return new RelPipelineProduct
			{
				publish = mEnv,
				gate = gateInput(item),
				releaseId = item.relId,
			};
		});

		int code = RelPipelineCli.run(new[]
		{
			"-relEnv", ENV,
			"-relPlatform", PLATFORM,
			"-relBaseId", BASE,
			"-auditOperator", "ci-pipeline",
		}, () => mStore, out PubReceipt receipt);

		Assert.AreEqual(0, code, receipt.error);
		Assert.IsTrue(receipt.ok);
		Assert.AreEqual("produce-gate-publish", receipt.action);
		Assert.AreEqual("ci-pipeline", receipt.operatorId);
		Assert.AreEqual("rel-pipeline01", receipt.releaseId);
		Assert.AreEqual(1, receipt.seq);
		Assert.IsTrue(receipt.gate.ok);
		Assert.AreEqual("project,plan,candidate", receipt.gate.phases);
		Assert.IsTrue(receipt.server.latest);
		Assert.IsTrue(UpdFmt.isSha(receipt.gateEvidenceSha));
		Assert.IsTrue(UpdFmt.isSha(receipt.auditSha));
		Assert.IsTrue(File.Exists(receipt.auditPath));
		Assert.IsTrue(mStore.has(receipt.auditObjectKey));
	}

	[Test]
	public void PubRelPartialResumeKeepsKnownKeys()
	{
		makeRelease("rel-c01", 1, 2);
		// 模拟上次中断：远端只有一个Release文件。
		string seedKey = relFileKey("rel-c01", "HotFix.dll.bytes");
		mStore.seed(seedKey, File.ReadAllBytes(Path.Combine(mRoot, ENV, "releases",
			"rel-c01", "files", "HotFix.dll.bytes")));
		using (PubFlow pub = flow())
		{
			Assert.AreEqual(1, publish(pub, "rel-c01").seq);
		}
		Assert.IsFalse(mStore.ops.Contains("put:" + seedKey),
			"已存在且校验通过的文件不应重复上传");
		Assert.IsTrue(mStore.has(ENV + "/releases/rel-c01/manifest.json"));
		Assert.AreEqual("rel-c01", openRemoteLatest().releaseId);
	}

	[Test]
	public void PubRelUnknownRemoteKeyIsRejected()
	{
		makeRelease("rel-d01", 1, 1);
		mStore.seed(ENV + "/releases/rel-d01/files/stray.bin", new byte[] { 1 });
		using (PubFlow pub = flow())
		{
			IOException ex = Assert.Throws<IOException>(
				() => publish(pub, "rel-d01"));
			StringAssert.Contains("未知对象", ex.Message);
		}
		Assert.IsFalse(mStore.has(latestKey()), "拒绝后不得曝光Latest");
	}

	[Test]
	public void PubRelRemoteNewerSeqIsRejected()
	{
		makeRelease("rel-e01", 1, 1);
		// 远端已经有更高seq的Latest。
		makeRelease("rel-e02", 9, 1);
		using (PubFlow first = flow())
		{
			Assert.AreEqual(9, publish(first, "rel-e02").seq);
		}
		makeRelease("rel-e03", 2, 1);
		using (PubFlow pub = flow())
		{
			InvalidDataException ex = Assert.Throws<InvalidDataException>(
				() => publish(pub, "rel-e03"));
			StringAssert.Contains("序号不高于远端", ex.Message);
		}
		Assert.AreEqual("rel-e02", openRemoteLatest().releaseId);
	}

	[Test]
	public void PubRelSavesPreviousBeforeExposing()
	{
		makeRelease("rel-f01", 1, 1);
		using (PubFlow first = flow())
		{
			publish(first, "rel-f01");
		}
		makeRelease("rel-f02", 2, 1);
		using (PubFlow second = flow())
		{
			publish(second, "rel-f02");
		}
		string prevKey = ENV + "/previous/" + PLATFORM + "/" + BASE + ".json";
		Assert.IsTrue(mStore.has(prevKey), "发布新版前必须保存上一版");
		UpdBox box = UpdJson.box(mStore.raw(prevKey));
		UpdRet<byte[]> opened = new UpdSign(mPubKey).open(box);
		Assert.AreEqual("rel-f01", UpdJson.latest(opened.value).releaseId);
		Assert.AreEqual("rel-f02", openRemoteLatest().releaseId);
	}

	[Test]
	public void RollbackWithoutPreviousIsRejected()
	{
		makeRelease("rel-g01", 1, 1);
		using (PubFlow pub = flow())
		{
			publish(pub, "rel-g01");
			InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
				() => pub.rollback(new PubItem { env = ENV, platform = PLATFORM, baseId = BASE },
					"rollback-tests"));
			StringAssert.Contains("没有上一版", ex.Message);
		}
	}

	[Test]
	public void RollbackWithoutTargetGateEvidenceIsRejectedBeforeLatestChanges()
	{
		makeRelease("rel-rg01", 1, 1);
		using PubFlow pub = flow();
		publish(pub, "rel-rg01");
		makeRelease("rel-rg02", 2, 1);
		publish(pub, "rel-rg02");
		PubItem first = new()
		{
			env = ENV,
			platform = PLATFORM,
			baseId = BASE,
			relId = "rel-rg01",
		};
		mStore.drop(RelAudit.gateObjectKey(first));

		InvalidDataException ex = Assert.Throws<InvalidDataException>(() =>
			pub.rollback(new PubItem
			{
				env = ENV,
				platform = PLATFORM,
				baseId = BASE,
			}, "rollback-tests"));

		StringAssert.Contains("没有已验签门禁凭证", ex.Message);
		Assert.AreEqual("rel-rg02", openRemoteLatest().releaseId);
	}

	[Test]
	public void RollbackReExposesPreviousWithHigherSeq()
	{
		makeRelease("rel-h01", 1, 1);
		using (PubFlow first = flow())
		{
			publish(first, "rel-h01");
		}
		makeRelease("rel-h02", 2, 1);
		using (PubFlow second = flow())
		{
			publish(second, "rel-h02");
			PubItem scope = new()
			{
				env = ENV,
				platform = PLATFORM,
				baseId = BASE,
			};
			Directory.Delete(Path.Combine(mRoot, ENV, "releases", "rel-h01"), true);
			File.Delete(Path.Combine(mRoot, ENV, "latest", PLATFORM, BASE + ".json"));
			PubHead before = second.remote(scope);
			Assert.AreEqual("rel-h01", before.previousRelId,
				"远端诊断不能依赖本地上一版Release");
			PubResult rolled = second.rollback(scope, "rollback-tests");
			Assert.AreEqual(3, rolled.seq);
			Assert.AreEqual("rollback", rolled.audit.audit.action);
		}
		UpdLatest head = openRemoteLatest();
		Assert.AreEqual("rel-h01", head.releaseId);
		Assert.AreEqual(3, head.seq);
		string prevKey = ENV + "/previous/" + PLATFORM + "/" + BASE + ".json";
		UpdBox box = UpdJson.box(mStore.raw(prevKey));
		UpdRet<byte[]> opened = new UpdSign(mPubKey).open(box);
		Assert.AreEqual("rel-h02", UpdJson.latest(opened.value).releaseId,
			"回退后原当前版必须成为新的上一版");
	}

	[Test]
	public void TamperedLocalFileIsRejected()
	{
		makeRelease("rel-i01", 1, 1);
		string full = Path.Combine(mRoot, ENV, "releases", "rel-i01", "files",
			"HotFix.dll.bytes");
		File.WriteAllBytes(full, new byte[] { 1, 2, 3 });
		using (PubFlow pub = flow())
		{
			InvalidDataException ex = Assert.Throws<InvalidDataException>(
				() => publish(pub, "rel-i01"));
			StringAssert.Contains("校验失败", ex.Message);
		}
		Assert.IsFalse(mStore.has(latestKey()));
	}

	[Test]
	public void RemoteHeadMatchesLocalAfterPublish()
	{
		makeRelease("rel-j01", 4, 1);
		using (PubFlow pub = flow())
		{
			publish(pub, "rel-j01");
			PubItem[] items = PubFlow.scan(mEnv, PLATFORM);
			PubHead head = pub.check(items[0]);
			Assert.IsTrue(head.has);
			Assert.IsTrue(head.isSame);
			Assert.AreEqual("rel-j01", head.relId);
			Assert.AreEqual(4, head.seq);
		}
	}

	void writeBaseTrust()
	{
		string dir = Path.Combine(mRoot, ENV, "base", PLATFORM);
		Directory.CreateDirectory(dir);
		BaseTrust trust = new()
		{
			schema = UpdLim.Schema,
			env = ENV,
			platform = PLATFORM,
			baseId = BASE,
			baseUrl = mStore.clientBase,
			pubKey = mPubKey,
			resList = RES_LIST,
		};
		File.WriteAllText(Path.Combine(dir, BASE + ".json"),
			JsonUtility.ToJson(trust, false), sUtf8);
	}
}
