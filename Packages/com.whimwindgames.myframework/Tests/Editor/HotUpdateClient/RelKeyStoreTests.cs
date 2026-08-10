using System;
using System.IO;
using System.Text;
using NUnit.Framework;

public sealed class RelKeyStoreTests
{
	string mRoot;

	[SetUp]
	public void SetUp()
	{
		string temp = Directory.Exists("/private/tmp") ? "/private/tmp" : Path.GetTempPath();
		mRoot = Path.Combine(temp, "myframework-keyring-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(mRoot);
	}

	[TearDown]
	public void TearDown()
	{
		if (Directory.Exists(mRoot)) Directory.Delete(mRoot, true);
	}

	[Test]
	public void CreateSeparatesTestAndProdAndSupportsEncryptedPem()
	{
		RelKeyStore test = RelKeyStore.openAt(mRoot, "sample-game", "test");
		RelKeyInfo testInfo = test.create();
		Assert.That(File.Exists(test.activePrivateKeyPath), Is.True);
		Assert.That(testInfo.encrypted, Is.False);

		char[] password = "correct-horse-battery".ToCharArray();
		try
		{
			RelKeyStore prod = RelKeyStore.openAt(mRoot, "sample-game", "prod");
			RelKeyInfo prodInfo = prod.create(password);
			Assert.That(prodInfo.encrypted, Is.True);
			Assert.That(prod.activePrivateKeyPath, Is.Not.EqualTo(test.activePrivateKeyPath));
			Assert.That(File.ReadAllText(prod.activePrivateKeyPath),
				Does.Contain("Proc-Type: 4,ENCRYPTED"));

			byte[] probe = Encoding.UTF8.GetBytes("encrypted-key-probe");
			RelSign signer = new(prod.activePrivateKeyPath,
				() => "correct-horse-battery".ToCharArray());
			UpdBox box = new()
			{
				schema = UpdLim.Schema,
				alg = "ES256",
				data = Convert.ToBase64String(probe),
				sig = Convert.ToBase64String(signer.sign(probe)),
			};
			Assert.That(new UpdSign(prodInfo.publicKey).open(box).ok, Is.True);
			Assert.Throws<InvalidDataException>(() => new RelSign(prod.activePrivateKeyPath));
		}
		finally
		{
			Array.Clear(password, 0, password.Length);
		}
	}

	[Test]
	public void RotationRequiresTransitionAndNextBaseThenArchivesOldKey()
	{
		RelKeyStore store = RelKeyStore.openAt(mRoot, "sample-game", "test");
		RelKeyInfo old = store.create();
		string oldPath = store.activePrivateKeyPath;
		RelKeyInfo pending = store.beginRotation();
		UpdCfg oldBase = cfg("test", "base-1", old.publicKey);
		UpdCfg nextBase = cfg("test", "base-2", pending.publicKey);

		Assert.Throws<InvalidOperationException>(() => store.completeRotation(nextBase));
		UpdLatest latest = new()
		{
			schema = UpdLim.Schema,
			env = "test",
			platform = "Android",
			baseId = "base-1",
			seq = 7,
			releaseId = "test-android-base-1-7",
			manifestSha = new string('a', 64),
			manifestSize = 128,
		};
		byte[] transition = store.signTransition(oldBase, latest);
		UpdBox box = UpdJson.box(transition);
		Assert.That(new UpdSign(old.publicKey).open(box).ok, Is.True);
		Assert.Throws<InvalidDataException>(() => store.completeRotation(
			cfg("test", "base-2", old.publicKey)));

		string archived = store.completeRotation(nextBase);
		Assert.That(File.Exists(oldPath), Is.False);
		Assert.That(File.Exists(archived), Is.True);
		Assert.That(archived, Does.EndWith(".pem.disabled"));
		Assert.That(store.active.id, Is.EqualTo(pending.id));
		Assert.That(store.pending, Is.Null);
		Assert.That(store.hasTransition, Is.False);

		RelKeyStore reloaded = RelKeyStore.openAt(mRoot, "sample-game", "test");
		Assert.That(reloaded.active.id, Is.EqualTo(pending.id));
		Assert.That(reloaded.activePrivateKeyPath, Is.EqualTo(store.activePrivateKeyPath));
	}

	[Test]
	public void LegacyPathIsCopiedAndSourceIsLeftForManualCleanup()
	{
		RelKeyPair pair = RelKey.generate();
		string legacy = Path.Combine(mRoot, "legacy-private.txt");
		File.WriteAllText(legacy, pair.privatePem);
		RelKeyStore store = RelKeyStore.openAt(mRoot, "sample-game", "test");

		RelKeyInfo imported = store.importLegacy(legacy);

		Assert.That(imported.publicKey, Is.EqualTo(pair.publicKey));
		Assert.That(File.Exists(store.activePrivateKeyPath), Is.True);
		Assert.That(store.activePrivateKeyPath, Is.Not.EqualTo(legacy));
		Assert.That(File.Exists(legacy), Is.True,
			"旧来源必须保留，等待用户核验后手动清理");
	}

	[Test]
	public void PubEnvRejectsSharedKeyAcrossTestAndProd()
	{
		RelKeyPair pair = RelKey.generate();
		string path = Path.Combine(mRoot, "shared.pem");
		File.WriteAllText(path, pair.privatePem);
		PubEnv env = new()
		{
			privateKeyPathForEnv = _ => path,
		};

		Assert.Throws<InvalidDataException>(() => env.signingKey("test"));
	}

	static UpdCfg cfg(string env, string baseId, string publicKey)
	{
		return new UpdCfg
		{
			baseUrl = "https://hot.test/",
			env = env,
			platform = "Android",
			baseId = baseId,
			pubKey = publicKey,
			resList = "AssetBundleInfo.json",
		};
	}
}
