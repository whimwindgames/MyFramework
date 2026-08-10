using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using NUnit.Framework;
using UnityEngine;

// 真实服务器冒烟：47.243.79.140 hot-store 协议 v2 全链路。
// 标记 [Explicit]，只在手动指定时运行；使用 env=test + baseId=base-smoke，
// 不影响任何真实客户端的 latest 指针。签名密钥为一次性临时密钥。
[Explicit]
public sealed class PubSmokeTests
{
	const string ENV = "test";
	const string PLATFORM = "Android";
	const string HOST = "47.243.79.140";
	static readonly string[] CODE_DLLS = { "Frame_HotFix.dll.bytes", "HotFix.dll.bytes" };
	static readonly UTF8Encoding sUtf8 = new(false, true);

	string mBase;
	string mRoot;
	string mPrivPem;
	string mPubKey;
	PubEnv mEnv;
	SshCfg mSsh;

	[SetUp]
	public void SetUp()
	{
		string temp = Directory.Exists("/private/tmp") ? "/private/tmp" : Path.GetTempPath();
		mRoot = Path.Combine(temp, "myframework-smoke-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(mRoot);
		mBase = "base-smk" + Guid.NewGuid().ToString("N").Substring(0, 8);
		RelKeyPair pair = RelKey.generate();
		mPrivPem = Path.Combine(mRoot, "latest.pem");
		File.WriteAllText(mPrivPem, pair.privatePem);
		mPubKey = pair.publicKey;
		mSsh = new SshCfg
		{
			host = HOST,
			port = 22,
			user = "hotdeploy",
			key = deployKey(),
			url = "https://" + HOST + "/",
		};
		mEnv = new PubEnv
		{
			pubRoot = mRoot,
			privateKeyPath = mPrivPem,
			baseRegistry = (env, platform, baseId) => new UpdCfg
			{
				baseUrl = "https://" + HOST + "/",
				env = env,
				platform = platform,
				baseId = baseId,
				pubKey = mPubKey,
				resList = "config/res.json",
			},
		};
	}

	static string deployKey()
	{
		string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		string stable = Path.Combine(home, ".myframework-keys", "hotdeploy", "openssh");
		if (File.Exists(stable)) return stable;
		throw new FileNotFoundException("未找到hotdeploy SSH私钥，请放入" + stable);
	}

	[TearDown]
	public void TearDown()
	{
		if (Directory.Exists(mRoot)) Directory.Delete(mRoot, true);
	}

	[Test]
	public void RealServerPublishAndRollback()
	{
		Assume.That(Application.isBatchMode, Is.True,
			"真实服务器smoke只能在batchmode/CI运行，禁止阻塞交互式Editor主线程");
		mSsh.prep();
		string known = SshStore.knownFingerprint(mSsh);
		Assert.IsNotEmpty(known, "请先在资源发布窗口核对并信任SSH主机指纹");
		SshHostKey host = SshStore.scanHost(mSsh);
		Assert.IsNotNull(host, "读取SSH主机密钥失败");
		UnityEngine.Debug.Log("主机指纹:" + host.fingerprint);
		Assert.AreEqual(known, host.fingerprint, "SSH主机密钥已变化，禁止自动信任");
		Assert.AreEqual("SSH与HTTPS只读检查成功", mSsh.check());

		using (PubFlow pub = new(new SshStore(mSsh), mEnv,
			(text, done, total) => UnityEngine.Debug.Log(text + " " + done + "/" + total)))
		{
			string rel1 = "rel-smoke-" + DateTime.UtcNow.ToString("MMddHHmmss") + "-a1";
			string rel2 = "rel-smoke-" + DateTime.UtcNow.ToString("MMddHHmmss") + "-b2";
			makeRelease(rel1, 1);
			Assert.AreEqual(1, pub.pubRel(PLATFORM, rel1));

			PubItem scope = new() { env = ENV, platform = PLATFORM, baseId = mBase };
			PubHead remote = pub.remote(scope);
			Assert.IsTrue(remote.has, "远端Latest不存在");
			Assert.AreEqual(rel1, remote.relId);

			makeRelease(rel2, 2);
			Assert.AreEqual(2, pub.pubRel(PLATFORM, rel2));
			remote = pub.remote(scope);
			Assert.AreEqual(rel2, remote.relId);
			Assert.IsTrue(remote.hasPrevious, "上一版未保存");
			Assert.AreEqual(rel1, remote.previousRelId);

			long seq = pub.rollback(scope);
			Assert.AreEqual(3, seq);
			remote = pub.remote(scope);
			Assert.AreEqual(rel1, remote.relId, "回退后Latest未指向上一版");

			verifyHttpsLatest(rel1);
			verifyHttpsRange(rel1);
		}
	}

	// 通过客户端真实路径（nginx只读HTTPS）回读Latest并验签，
	// 证明发布结果对客户端可见且可信。
	void verifyHttpsLatest(string expectRel)
	{
		System.Net.HttpWebRequest req = (System.Net.HttpWebRequest)
			System.Net.WebRequest.Create("https://" + HOST + "/" + ENV +
				"/latest/" + PLATFORM + "/" + mBase + ".json?v=" +
				Guid.NewGuid().ToString("N"));
		req.Method = "GET";
		req.Timeout = 15000;
		using System.Net.HttpWebResponse res = (System.Net.HttpWebResponse)req.GetResponse();
		Assert.AreEqual(System.Net.HttpStatusCode.OK, res.StatusCode);
		using StreamReader input = new(res.GetResponseStream(), sUtf8);
		byte[] raw = sUtf8.GetBytes(input.ReadToEnd());
		UpdBox box = UpdJson.box(raw);
		UpdRet<byte[]> opened = new UpdSign(mPubKey).open(box);
		Assert.IsTrue(opened.ok, "HTTPS回读Latest验签失败");
		Assert.AreEqual(expectRel, UpdJson.latest(opened.value).releaseId);
	}

	void verifyHttpsRange(string relId)
	{
		System.Net.HttpWebRequest req = (System.Net.HttpWebRequest)
			System.Net.WebRequest.Create("https://" + HOST + "/" + ENV +
				"/releases/" + relId + "/files/bundles/smoke.unity3d?v=" +
				Guid.NewGuid().ToString("N"));
		req.Method = "GET";
		req.Timeout = 15000;
		req.AddRange(0, 63);
		using System.Net.HttpWebResponse res =
			(System.Net.HttpWebResponse)req.GetResponse();
		Assert.AreEqual(System.Net.HttpStatusCode.PartialContent, res.StatusCode);
		Assert.AreEqual(64, res.ContentLength);
		using MemoryStream data = new();
		res.GetResponseStream().CopyTo(data);
		Assert.AreEqual(64, data.Length);
	}

	void makeRelease(string relId, long seq)
	{
		string relDir = Path.Combine(mRoot, ENV, "releases", relId);
		string filesDir = Path.Combine(relDir, "files");
		Directory.CreateDirectory(filesDir);
		List<UpdFile> files = new();
		writeFile(filesDir, "Frame_HotFix.dll.bytes", 2048, files);
		writeFile(filesDir, "HotFix.dll.bytes", 3072, files);
		writeFile(filesDir, "config/res.json", 256, files);
		writeFile(filesDir, "bundles/smoke.unity3d", 65536, files);
		UpdMan man = new()
		{
			schema = UpdLim.Schema,
			env = ENV,
			releaseId = relId,
			platform = PLATFORM,
			baseId = mBase,
			aotDlls = Array.Empty<string>(),
			codeDlls = CODE_DLLS,
			entryDll = "HotFix.dll.bytes",
			hotId = UpdRule.hotId(CODE_DLLS, "HotFix.dll.bytes"),
			files = files.ToArray(),
		};
		byte[] manRaw = sUtf8.GetBytes(JsonUtility.ToJson(man, false));
		File.WriteAllBytes(Path.Combine(relDir, "manifest.json"), manRaw);
		UpdLatest head = new()
		{
			schema = UpdLim.Schema,
			env = ENV,
			platform = PLATFORM,
			baseId = mBase,
			seq = seq,
			releaseId = relId,
			manifestSha = UpdHash.data(manRaw),
			manifestSize = manRaw.LongLength,
		};
		byte[] body = sUtf8.GetBytes(JsonUtility.ToJson(head, false));
		RelSign signer = new(mPrivPem);
		UpdBox box = new()
		{
			schema = UpdLim.Schema,
			alg = "ES256",
			data = Convert.ToBase64String(body),
			sig = Convert.ToBase64String(signer.sign(body)),
		};
		string latestDir = Path.Combine(mRoot, ENV, "latest", PLATFORM);
		Directory.CreateDirectory(latestDir);
		File.WriteAllBytes(Path.Combine(latestDir, mBase + ".json"),
			sUtf8.GetBytes(JsonUtility.ToJson(box, false)));
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
}
