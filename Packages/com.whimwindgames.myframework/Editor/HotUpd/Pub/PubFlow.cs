using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

[Serializable]
public sealed class PubItem
{
	public string env;
	public string platform;
	public string baseId;
	public string relId;
	public long seq;
	public int fileCnt;
	public long totalSize;
	public string manSha;
}

[Serializable]
public sealed class PubHead
{
	public bool has;
	public bool hasRel;
	public bool isSame;
	public string relId;
	public long seq;
	public bool hasPrevious;
	public string previousRelId;
	public long previousSeq;
}

public sealed class PubResult
{
	public long seq;
	public RelGateProof gate;
	public RelAuditSaved audit;
	public RelServerReadback server;
	public string auditObjectKey;
}

// 发布环境配置。全部显式注入，不读取全局单例，窗口与无头入口共用。
public sealed class PubEnv
{
	// 允许发布的环境列表，默认test/prod。
	public string[] envIds = { "test", "prod" };
	// 发布输出根目录，包含{env}/releases、{env}/latest等子目录。
	public string pubRoot;
	// Latest签名私钥（P-256 PEM）的绝对路径，必须位于项目与Git工作区外。
	// 仅为单环境旧调用保留；test/prod并存时必须使用privateKeyPathForEnv。
	public string privateKeyPath;
	// 按环境解析签名路径，防止test/prod复用同一把私钥。
	public Func<string, string> privateKeyPathForEnv;
	// 加密PEM密码按需取得，返回值由RelSign读取后清零。
	public Func<string, char[]> privateKeyPasswordForEnv;
	// 受信Base记录查询：(env, platform, baseId) -> 冻结时的UpdCfg。
	// 测试或宿主可显式覆盖；默认读取发布输出自身的base/<platform>/<baseId>.json。
	public Func<string, string, string, UpdCfg> baseRegistry;

	internal void prep()
	{
		if (envIds == null || envIds.Length == 0)
		{
			throw new InvalidDataException("发布环境列表为空");
		}
		for (int i = 0; i < envIds.Length; ++i)
		{
			if (!UpdFmt.isId(envIds[i]))
			{
				throw new InvalidDataException("发布环境标识非法:" + envIds[i]);
			}
			for (int j = 0; j < i; ++j)
			{
				if (envIds[j] == envIds[i])
				{
					throw new InvalidDataException("发布环境重复:" + envIds[i]);
				}
			}
		}
	}

	internal UpdCfg cfg(string env, string platform, string baseId)
	{
		UpdCfg value = baseRegistry != null ? baseRegistry(env, platform, baseId) :
			PubBases.release(root(), env, platform, baseId);
		return value ?? throw new InvalidDataException("受信Base记录查询返回空值");
	}

	internal string signingKey(string env)
	{
		if (Array.IndexOf(envIds, env) < 0)
		{
			throw new InvalidDataException("签名环境不在允许列表:" + env);
		}
		if (privateKeyPathForEnv == null && envIds.Length > 1)
		{
			throw new InvalidDataException("test/prod并存时必须分别配置签名私钥路径");
		}
		string selected = privateKeyPathForEnv != null ?
			privateKeyPathForEnv(env) : privateKeyPath;
		if (string.IsNullOrWhiteSpace(selected) ||
			!Path.IsPathRooted(selected) || !File.Exists(selected))
		{
			throw new FileNotFoundException(env + " Latest签名私钥不存在", selected);
		}
		string full = Path.GetFullPath(selected);
		if (privateKeyPathForEnv != null)
		{
			for (int i = 0; i < envIds.Length; ++i)
			{
				if (envIds[i] == env) continue;
				string other = privateKeyPathForEnv(envIds[i]);
				if (!string.IsNullOrWhiteSpace(other) && Path.IsPathRooted(other) &&
					string.Equals(full, Path.GetFullPath(other),
						StringComparison.OrdinalIgnoreCase))
				{
					throw new InvalidDataException("test/prod不能复用同一签名私钥");
				}
			}
		}
		string proj = Path.GetFullPath(Path.Combine(Application.dataPath, ".."))
			.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
			Path.DirectorySeparatorChar;
		if ((full + Path.DirectorySeparatorChar)
			.StartsWith(proj, StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidDataException("Latest签名私钥不能位于项目或Git工作区内");
		}
		return full;
	}

	internal RelSign signer(string env)
	{
		return new RelSign(signingKey(env), privateKeyPasswordForEnv == null ? null :
			() => privateKeyPasswordForEnv(env));
	}

	internal string root()
	{
		if (string.IsNullOrWhiteSpace(pubRoot) || !Path.IsPathRooted(pubRoot))
		{
			throw new InvalidDataException("请配置绝对发布输出目录");
		}
		string full = Path.GetFullPath(pubRoot).TrimEnd(Path.DirectorySeparatorChar,
			Path.AltDirectorySeparatorChar);
		if (!Directory.Exists(full))
		{
			throw new DirectoryNotFoundException("发布输出目录不存在:" + full);
		}
		PubFlow.noLinks(full);
		return full;
	}
}

public static class PubBases
{
	// 默认从Release输出目录读取由RelBuild冻结的信任记录，使产物可在新checkout或CI中发布。
	public static UpdCfg release(string pubRoot, string env, string platform,
		string baseId)
	{
		return RelBuild.loadBase(pubRoot, env, platform, baseId);
	}

	// 从AOT基线冻结记录构造发布侧UpdCfg。发布平台必须与当前激活构建平台一致，
	// 防止把Android的Release发布到iOS的Base记录上。仅供旧宿主显式选择。
	public static UpdCfg aot(string env, string platform, string baseId)
	{
		BuildTarget target = EditorUserBuildSettings.activeBuildTarget;
		if (!string.Equals(platform, target.ToString(), StringComparison.Ordinal))
		{
			throw new InvalidDataException("发布平台与当前激活构建平台不一致:" +
				platform + " != " + target);
		}
		AotBaseInfo info = AotBase.info(env, baseId);
		return new UpdCfg
		{
			env = env,
			platform = platform,
			baseId = baseId,
			baseUrl = info.baseUrl,
			pubKey = info.pubKey,
		};
	}
}

public sealed class PubFlow : IDisposable
{
	sealed class PubData
	{
		public UpdCfg cfg;
		public UpdLatest head;
		public byte[] headRaw;
		public UpdMan man;
		public byte[] manRaw;
		public string manSha;
		public string relDir;
	}

	static readonly UTF8Encoding sUtf8 = new(false, true);
	readonly IObjStore mStore;
	readonly PubEnv mEnv;
	readonly Action<string, int, int> mProg;
	bool mDone;

	public PubFlow(IObjStore store, PubEnv env, Action<string, int, int> progress = null)
	{
		mStore = store ?? throw new ArgumentNullException(nameof(store));
		mEnv = env ?? throw new ArgumentNullException(nameof(env));
		mEnv.prep();
		mProg = progress;
	}

	public static PubItem[] scan(PubEnv env, string platform)
	{
		if (env == null) throw new ArgumentNullException(nameof(env));
		env.prep();
		if (!UpdFmt.isId(platform))
		{
			throw new InvalidDataException("当前发布平台不受支持");
		}
		string root = env.root();
		List<PubItem> items = new();
		foreach (string envId in env.envIds)
		{
			string latestRoot = Path.Combine(root, envId, "latest", platform);
			if (!Directory.Exists(latestRoot)) continue;
			noLinks(latestRoot);
			foreach (string latestFile in Directory.GetFiles(latestRoot, "*.json",
				SearchOption.TopDirectoryOnly))
			{
				try
				{
					string baseId = Path.GetFileNameWithoutExtension(latestFile);
					if (!UpdFmt.isId(baseId)) throw new InvalidDataException(
						"Latest文件名非法:" + latestFile);
					UpdCfg cfg = env.cfg(envId, platform, baseId);
					byte[] headRaw = read(latestFile, UpdLim.LatestMax);
					UpdLatest head = openHead(cfg, headRaw, true);
					string relId = head.releaseId;
					string relDir = Path.Combine(root, envId, "releases", relId);
					noLinks(relDir);
					byte[] manRaw = read(Path.Combine(relDir, "manifest.json"),
						UpdLim.ManMax);
					UpdMan man = UpdJson.man(manRaw);
					if (man == null || man.env != envId || man.platform != platform ||
						man.baseId != baseId || man.releaseId != relId)
					{
						throw new InvalidDataException(
							"本地Manifest身份错误:" + relId);
					}
					UpdRule.man(cfg, new UpdLatest { releaseId = relId }, man);
					string manSha = UpdHash.data(manRaw);
					if (head.manifestSize != manRaw.LongLength ||
						head.manifestSha != manSha)
					{
						throw new InvalidDataException(
							"Latest与Manifest不一致:" + relId);
					}
					long total = 0;
					for (int i = 0; i < man.files.Length; ++i)
					{
						total = checked(total + man.files[i].size);
					}
					items.Add(new PubItem
					{
						env = envId,
						platform = platform,
						baseId = baseId,
						relId = relId,
						seq = head.seq,
						fileCnt = man.files.Length,
						totalSize = total,
						manSha = manSha,
					});
				}
				catch (Exception ex)
				{
					Debug.LogError("已跳过损坏Latest:" + latestFile + "\n" + ex);
				}
			}
		}
		items.Sort((a, b) =>
		{
			int envCmp = string.CompareOrdinal(a.env, b.env);
			if (envCmp != 0) return envCmp;
			return string.CompareOrdinal(a.baseId, b.baseId);
		});
		return items.ToArray();
	}

	public static PubItem find(PubEnv env, string platform, string relId)
	{
		if (!UpdFmt.isId(relId))
		{
			throw new InvalidDataException("Release标识非法");
		}
		PubItem found = null;
		foreach (PubItem item in scan(env, platform))
		{
			if (item.relId != relId) continue;
			if (found != null)
			{
				throw new InvalidDataException("Release标识在本地不唯一:" + relId);
			}
			found = item;
		}
		return found ?? throw new FileNotFoundException("本地Release不存在", relId);
	}

	public PubHead check(PubItem item)
	{
		ensureOpen();
		string env = item?.env ?? throw new ArgumentNullException(nameof(item));
		using FileStream gate = takeLock(env);
		PubData data = loadData(item.platform, item.relId, true, true);
		matchItem(item, data);
		string[] keys = relKeys(data.cfg.env, data.man.releaseId);
		bool hasRel = keys.Length > 0;
		if (hasRel)
		{
			checkRel(data, keys);
			readRel(data);
		}
		UpdLatest head = readHead(data.cfg, out byte[] raw);
		PubHead state = toHead(head, null, hasRel);
		state.isSame = same(raw, data.headRaw);
		return state;
	}

	// Latest曝光没有无凭证重载：调用方必须提交与当前Release绑定且验签通过的
	// 全阶段门禁证据。证据先写入远端不可变audit树并回读，再开始上传/曝光。
	public PubResult pubRel(PubItem item, string gateEvidencePath)
	{
		ensureOpen();
		DateTime started = DateTime.UtcNow;
		System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
		if (item == null || !UpdFmt.isId(item.env) || !UpdFmt.isId(item.platform) ||
			!UpdFmt.isId(item.baseId) || !UpdFmt.isId(item.relId) ||
			Array.IndexOf(mEnv.envIds, item.env) < 0)
		{
			throw new InvalidDataException("待发布Release身份错误");
		}
		string env = item.env;
		using FileStream gate = takeLock(env);
		PubData data = loadData(item.platform, item.relId, true, true);
		matchItem(item, data);
		RelGateProof proof = RelAudit.loadGate(gateEvidencePath, data.cfg, item);
		using IObjLease lease = mStore.take(lockKey(data.cfg));
		lease.keep();
		putGateEvidence(item, data.cfg, proof);
		lease.keep();
		string manKey = relKey(data.cfg.env, item.relId, "manifest.json");
		string[] keys = relKeys(data.cfg.env, item.relId);
		bool needsPut = !hasKey(keys, manKey);
		if (needsPut)
		{
			checkPart(data, keys);
			putRel(data, lease, keys);
			lease.keep();
			keys = relKeys(data.cfg.env, item.relId);
		}
		lease.keep();
		checkRel(data, keys);
		readRel(data, lease);
		UpdLatest old = readHead(data.cfg, out byte[] oldRaw);
		if (!same(oldRaw, data.headRaw) && old != null)
		{
			if (data.head.seq <= old.seq)
			{
				throw new InvalidDataException("待发布Latest序号不高于远端");
			}
			step("保存上一版", 0, 1);
			lease.keep();
			putPrevious(data.cfg, old, oldRaw);
			step("保存上一版", 1, 1);
		}
		step("曝光 Latest", 0, 1);
		lease.keep();
		putHead(data.cfg, data.head, data.headRaw);
		step("曝光 Latest", 1, 1);
		lease.keep();
		RelServerReadback server = serverReadback(data, old);
		watch.Stop();
		string auditKey = RelAudit.eventObjectKey(item, data.head.seq);
		RelAuditSaved audit = finishAudit("publish", auditKey, data.cfg, item,
			data.head.seq, proof, server, proof.evidence.operatorId, started,
			watch.ElapsedMilliseconds, lease);
		return new PubResult
		{
			seq = data.head.seq,
			gate = proof,
			audit = audit,
			server = server,
			auditObjectKey = auditKey,
		};
	}

	public PubHead remote(PubItem item)
	{
		ensureOpen();
		UpdCfg cfg = scopeCfg(item);
		UpdLatest head = readHead(cfg, out _);
		UpdLatest previous = readPrevious(cfg, out _);
		bool hasRel = false;
		if (previous != null)
		{
			PubData data = loadRemote(cfg, previous);
			string[] keys = relKeys(cfg.env, previous.releaseId);
			checkRel(data, keys);
			readRel(data);
			hasRel = true;
		}
		return toHead(head, previous, hasRel);
	}

	public PubResult rollback(PubItem scope, string operatorId)
	{
		ensureOpen();
		DateTime started = DateTime.UtcNow;
		System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
		UpdCfg cfg = scopeCfg(scope);
		using FileStream gate = takeLock(cfg.env);
		using IObjLease lease = mStore.take(lockKey(cfg));
		lease.keep();
		UpdLatest old = readHead(cfg, out byte[] oldRaw);
		if (old == null)
		{
			throw new InvalidOperationException("远端Latest不存在，不能执行回退");
		}
		UpdLatest previous = readPrevious(cfg, out _);
		if (previous == null)
		{
			throw new InvalidOperationException("远端没有上一版，不能执行回退");
		}
		PubData data = loadRemote(cfg, previous);
		string[] keys = relKeys(cfg.env, previous.releaseId);
		checkRel(data, keys);
		readRel(data, lease);
		PubItem item = toItem(data, previous.seq);
		RelGateProof proof = readGateEvidence(item, cfg);
		lease.keep();
		long seq = checked(Math.Max(old.seq, previous.seq) + 1);
		byte[] raw = makeHead(data, seq);
		UpdLatest head = openHead(cfg, raw, true);
		lease.keep();
		putPrevious(cfg, old, oldRaw);
		lease.keep();
		putHead(cfg, head, raw);
		lease.keep();
		RelServerReadback server = serverReadback(data, old, head);
		watch.Stop();
		string auditKey = RelAudit.eventObjectKey(item, seq);
		RelAuditSaved audit = finishAudit("rollback", auditKey, cfg, item, seq,
			proof, server, operatorId, started, watch.ElapsedMilliseconds, lease);
		return new PubResult
		{
			seq = seq,
			gate = proof,
			audit = audit,
			server = server,
			auditObjectKey = auditKey,
		};
	}

	public void Dispose()
	{
		if (mDone) return;
		mStore.Dispose();
		mDone = true;
	}

	void putGateEvidence(PubItem item, UpdCfg cfg, RelGateProof proof)
	{
		step("存档门禁凭证", 0, 1);
		string key = RelAudit.gateObjectKey(item);
		putImmutableAudit(key, proof.raw);
		byte[] saved = getRaw(key, UpdLim.LatestMax);
		if (!same(saved, proof.raw))
			throw new InvalidDataException("远端门禁凭证回读不一致");
		RelAudit.openGate(saved, cfg, item);
		step("存档门禁凭证", 1, 1);
	}

	RelGateProof readGateEvidence(PubItem item, UpdCfg cfg)
	{
		string key = RelAudit.gateObjectKey(item);
		if (!hasObj(key))
			throw new InvalidDataException("回退目标没有已验签门禁凭证，禁止曝光Latest");
		return RelAudit.openGate(getRaw(key, UpdLim.LatestMax), cfg, item);
	}

	void putAuditEvent(string key, RelAuditSaved audit, UpdCfg cfg, PubItem item,
		RelGateProof proof, string action)
	{
		step("存档发布审计", 0, 1);
		putImmutableAudit(key, audit.raw);
		byte[] saved = getRaw(key, UpdLim.LatestMax);
		RelAuditSaved remote = RelAudit.openEvent(saved, cfg, item, audit.audit.seq,
			proof.sha, action);
		if (!same(saved, audit.raw) || remote.sha != audit.sha)
			throw new InvalidDataException("远端发布审计回读不一致");
		step("存档发布审计", 1, 1);
	}

	RelAuditSaved finishAudit(string action, string key, UpdCfg cfg, PubItem item,
		long seq, RelGateProof proof, RelServerReadback server, string operatorId,
		DateTime started, long durationMs, IObjLease lease)
	{
		RelAuditSaved audit;
		if (hasObj(key))
		{
			lease?.keep();
			audit = RelAudit.adoptEvent(mEnv, getRaw(key, UpdLim.LatestMax), cfg,
				item, seq, proof.sha, action);
		}
		else
		{
			audit = RelAudit.makeEvent(mEnv, action, item, seq, proof, server,
				operatorId, started, durationMs);
		}
		lease?.keep();
		putAuditEvent(key, audit, cfg, item, proof, action);
		return audit;
	}

	void putImmutableAudit(string key, byte[] raw)
	{
		if (hasObj(key))
		{
			if (!rawSame(key, raw, UpdLim.LatestMax))
				throw new IOException("不可变审计对象已存在且内容不同:" + key);
			return;
		}
		try
		{
			putRaw(key, raw);
		}
		catch (Exception ex)
		{
			if (rawSame(key, raw, UpdLim.LatestMax)) return;
			throw new IOException("不可变审计对象上传失败:" + key, ex);
		}
	}

	byte[] getRaw(string key, int max)
	{
		string temp = tempFile();
		try
		{
			get(key, temp);
			return read(temp, max);
		}
		finally
		{
			dropTemp(temp);
		}
	}

	RelServerReadback serverReadback(PubData data, UpdLatest old,
		UpdLatest expected = null)
	{
		UpdLatest actual = readHead(data.cfg, out byte[] raw);
		UpdLatest target = expected ?? data.head;
		byte[] targetRaw = expected == null ? data.headRaw : null;
		if (actual == null || actual.releaseId != data.man.releaseId ||
			actual.seq != target.seq || actual.manifestSha != data.manSha ||
			actual.manifestSize != data.manRaw.LongLength ||
			(targetRaw != null && !same(raw, targetRaw)))
		{
			throw new InvalidDataException("Latest曝光后的服务器回读不一致");
		}
		UpdLatest previous = readPrevious(data.cfg, out _);
		return new RelServerReadback
		{
			releaseObjects = true,
			manifest = true,
			files = true,
			latest = true,
			latestReleaseId = actual.releaseId,
			latestSeq = actual.seq,
			previousReleaseId = previous?.releaseId ?? old?.releaseId,
		};
	}

	static PubItem toItem(PubData data, long seq)
	{
		long total = 0;
		for (int i = 0; i < data.man.files.Length; ++i)
			total = checked(total + data.man.files[i].size);
		return new PubItem
		{
			env = data.cfg.env,
			platform = data.cfg.platform,
			baseId = data.cfg.baseId,
			relId = data.man.releaseId,
			seq = seq,
			fileCnt = data.man.files.Length,
			totalSize = total,
			manSha = data.manSha,
		};
	}

	void putRel(PubData data, IObjLease lease, string[] keys)
	{
		HashSet<string> done = new(keys, StringComparer.Ordinal);
		for (int i = 0; i < data.man.files.Length; ++i)
		{
			lease.keep();
			UpdFile file = data.man.files[i];
			step("上传 Release", i, data.man.files.Length + 1);
			string key = relKey(data.cfg.env, data.man.releaseId,
				"files/" + file.path);
			if (done.Contains(key))
			{
				if (!fileSame(key, file))
					throw new IOException("未完成Release文件校验失败:" + file.path);
				continue;
			}
			putFile(key, filePath(data, file.path), file);
		}
		step("上传 Manifest", data.man.files.Length,
			data.man.files.Length + 1);
		lease.keep();
		putMan(relKey(data.cfg.env, data.man.releaseId, "manifest.json"),
			data.manRaw);
		step("Release 上传完成", data.man.files.Length + 1,
			data.man.files.Length + 1);
	}

	void readRel(PubData data, IObjLease lease = null)
	{
		readMan(data, lease);
		string temp = tempDir();
		try
		{
			for (int i = 0; i < data.man.files.Length; ++i)
			{
				lease?.keep();
				UpdFile file = data.man.files[i];
				step("回读 Release", i, data.man.files.Length);
				string path = Path.Combine(temp, "files", osPath(file.path));
				get(relKey(data.cfg.env, data.man.releaseId,
					"files/" + file.path), path);
				checkFile(path, file);
				dropTemp(path);
			}
			step("Release 回读完成", data.man.files.Length,
				data.man.files.Length);
		}
		finally
		{
			dropTemp(temp);
		}
	}

	void readMan(PubData data, IObjLease lease = null)
	{
		string temp = tempFile();
		try
		{
			step("回读 Manifest", 0, 1);
			lease?.keep();
			get(relKey(data.cfg.env, data.man.releaseId, "manifest.json"), temp);
			byte[] raw = read(temp, UpdLim.ManMax);
			if (!same(raw, data.manRaw))
			{
				throw new InvalidDataException("远端Manifest回读不一致");
			}
			UpdMan man = UpdJson.man(raw);
			UpdRule.man(data.cfg, new UpdLatest
			{
				releaseId = data.man.releaseId,
			}, man);
			step("Manifest 回读完成", 1, 1);
		}
		finally
		{
			dropTemp(temp);
		}
	}

	void checkRel(PubData data, string[] keys)
	{
		HashSet<string> expect = new(StringComparer.Ordinal)
		{
			relKey(data.cfg.env, data.man.releaseId, "manifest.json"),
		};
		for (int i = 0; i < data.man.files.Length; ++i)
		{
			expect.Add(relKey(data.cfg.env, data.man.releaseId,
				"files/" + data.man.files[i].path));
		}
		HashSet<string> actual = new(StringComparer.Ordinal);
		for (int i = 0; i < keys.Length; ++i)
		{
			if (!actual.Add(keys[i]))
			{
				throw new IOException("发布存储返回了重复对象");
			}
		}
		if (!expect.SetEquals(actual))
		{
			throw new IOException("远端Release对象不完整或存在额外对象");
		}
	}

	void checkPart(PubData data, string[] keys)
	{
		HashSet<string> expect = new(StringComparer.Ordinal);
		for (int i = 0; i < data.man.files.Length; ++i)
		{
			expect.Add(relKey(data.cfg.env, data.man.releaseId,
				"files/" + data.man.files[i].path));
		}
		HashSet<string> actual = new(StringComparer.Ordinal);
		for (int i = 0; i < keys.Length; ++i)
		{
			if (!actual.Add(keys[i]))
			{
				throw new IOException("发布存储返回了重复对象");
			}
			if (!expect.Contains(keys[i]))
			{
				throw new IOException("未完成Release存在未知对象，禁止续传");
			}
		}
	}

	void putHead(UpdCfg cfg, UpdLatest head, byte[] raw)
	{
		UpdRule.latest(cfg, head);
		string key = headKey(cfg);
		UpdLatest old = readHead(cfg, out byte[] oldRaw);
		if (same(oldRaw, raw)) return;
		if (old != null && head.seq <= old.seq)
		{
			throw new InvalidDataException("待发布Latest序号不高于远端");
		}
		try
		{
			putRaw(key, raw);
		}
		catch (Exception ex)
		{
			try
			{
				UpdLatest maybe = readHead(cfg, out byte[] maybeRaw);
				if (maybe != null && same(maybeRaw, raw)) return;
			}
			catch
			{
				// 保留原始上传异常，下面用明确的未知状态错误返回。
			}
			throw new IOException("Latest上传失败且远端回读未确认，禁止继续发布", ex);
		}
		UpdLatest saved = readHead(cfg, out byte[] savedRaw);
		if (!same(savedRaw, raw) || saved.seq != head.seq ||
			saved.releaseId != head.releaseId ||
			saved.manifestSha != head.manifestSha)
		{
			throw new InvalidDataException("远端Latest回读不一致");
		}
	}

	void putPrevious(UpdCfg cfg, UpdLatest head, byte[] raw)
	{
		UpdRule.latest(cfg, head);
		if (raw == null || !sameLatest(cfg, head, raw))
		{
			throw new InvalidDataException("上一版Latest内容错误");
		}
		putRaw(previousKey(cfg), raw);
		UpdLatest saved = readPrevious(cfg, out byte[] savedRaw);
		if (!same(savedRaw, raw) || saved.releaseId != head.releaseId ||
			saved.seq != head.seq)
		{
			throw new InvalidDataException("远端上一版回读不一致");
		}
	}

	PubData loadData(string platform, string relId, bool needHead, bool chkFiles)
	{
		if (!UpdFmt.isId(platform) || !UpdFmt.isId(relId))
		{
			throw new InvalidDataException("云发布参数错误");
		}
		string root = mEnv.root();
		string env = findEnv(root, relId);
		string relDir = Path.Combine(root, env, "releases", relId);
		noLinks(relDir);
		byte[] manRaw = read(Path.Combine(relDir, "manifest.json"), UpdLim.ManMax);
		UpdMan man = UpdJson.man(manRaw);
		if (man == null || man.env != env || man.releaseId != relId ||
			man.platform != platform || !UpdFmt.isId(man.baseId))
		{
			throw new InvalidDataException("本地Manifest身份错误");
		}
		UpdCfg cfg = makeCfg(man);
		if (!string.Equals(mStore.clientBase, cfg.baseUrl, StringComparison.Ordinal))
		{
			throw new InvalidDataException("发布目标与Base冻结地址不一致；当前发布目标:" +
				mStore.clientBase + "；Base冻结地址:" + cfg.baseUrl);
		}
		UpdRule.man(cfg, new UpdLatest { releaseId = relId }, man);
		PubData data = new()
		{
			cfg = cfg,
			man = man,
			manRaw = manRaw,
			manSha = UpdHash.data(manRaw),
			relDir = relDir,
		};
		for (int i = 0; chkFiles && i < man.files.Length; ++i)
		{
			checkFile(filePath(data, man.files[i].path), man.files[i]);
		}
		if (!needHead) return data;
		UpdLatest head = readLocal(cfg, true, out byte[] headRaw);
		if (head.releaseId != relId || head.manifestSize != manRaw.LongLength ||
			head.manifestSha != data.manSha)
		{
			throw new InvalidDataException("本地Latest未指向指定Release");
		}
		data.head = head;
		data.headRaw = headRaw;
		return data;
	}

	PubData loadRemote(UpdCfg cfg, UpdLatest head)
	{
		if (head == null) throw new ArgumentNullException(nameof(head));
		string temp = tempFile();
		try
		{
			get(relKey(cfg.env, head.releaseId, "manifest.json"), temp);
			byte[] raw = read(temp, UpdLim.ManMax);
			UpdMan man = UpdJson.man(raw);
			if (man == null || man.env != cfg.env || man.platform != cfg.platform ||
				man.baseId != cfg.baseId || man.releaseId != head.releaseId)
			{
				throw new InvalidDataException("远端Previous的Manifest身份错误");
			}
			UpdRule.man(cfg, head, man);
			string sha = UpdHash.data(raw);
			if (head.manifestSha != sha || head.manifestSize != raw.LongLength)
			{
				throw new InvalidDataException("远端Previous与Manifest不一致");
			}
			return new PubData
			{
				cfg = cfg,
				head = head,
				man = man,
				manRaw = raw,
				manSha = sha,
			};
		}
		finally
		{
			dropTemp(temp);
		}
	}

	UpdCfg makeCfg(UpdMan man)
	{
		if (man == null)
		{
			throw new InvalidDataException("Manifest为空");
		}
		return mEnv.cfg(man.env, man.platform, man.baseId);
	}

	static void matchItem(PubItem item, PubData data)
	{
		long total = 0;
		for (int i = 0; i < data.man.files.Length; ++i)
			total = checked(total + data.man.files[i].size);
		if (item.env != data.cfg.env || item.platform != data.cfg.platform ||
			item.baseId != data.cfg.baseId || item.relId != data.man.releaseId ||
			item.manSha != data.manSha || item.fileCnt != data.man.files.Length ||
			item.totalSize != total || item.seq != data.head.seq)
		{
			throw new InvalidDataException("界面中的Release信息已经变化，请刷新");
		}
	}

	UpdCfg scopeCfg(PubItem item)
	{
		if (item == null || !UpdFmt.isId(item.env) ||
			!UpdFmt.isId(item.platform) || !UpdFmt.isId(item.baseId))
		{
			throw new InvalidDataException("发布范围错误");
		}
		UpdCfg cfg = mEnv.cfg(item.env, item.platform, item.baseId);
		if (!string.Equals(mStore.clientBase, cfg.baseUrl, StringComparison.Ordinal))
		{
			throw new InvalidDataException("发布目标与Base冻结地址不一致；当前发布目标:" +
				mStore.clientBase + "；Base冻结地址:" + cfg.baseUrl);
		}
		return cfg;
	}

	static PubHead toHead(UpdLatest head, UpdLatest previous, bool hasRel)
	{
		return new PubHead
		{
			has = head != null,
			hasRel = hasRel,
			relId = head?.releaseId,
			seq = head?.seq ?? 0,
			hasPrevious = previous != null,
			previousRelId = previous?.releaseId,
			previousSeq = previous?.seq ?? 0,
		};
	}

	byte[] makeHead(PubData data, long seq)
	{
		UpdLatest head = new()
		{
			schema = UpdLim.Schema,
			env = data.cfg.env,
			platform = data.cfg.platform,
			baseId = data.cfg.baseId,
			seq = seq,
			releaseId = data.man.releaseId,
			manifestSha = data.manSha,
			manifestSize = data.manRaw.Length,
		};
		byte[] body = json(head);
		RelSign signer = mEnv.signer(data.cfg.env);
		UpdBox box = new()
		{
			schema = UpdLim.Schema,
			alg = "ES256",
			data = Convert.ToBase64String(body),
			sig = Convert.ToBase64String(signer.sign(body)),
		};
		byte[] raw = json(box);
		UpdLatest saved = openHead(data.cfg, raw, true);
		if (saved.releaseId != data.man.releaseId ||
			saved.manifestSha != data.manSha ||
			saved.manifestSize != data.manRaw.Length)
		{
			throw new InvalidDataException("回退Latest与Manifest不一致");
		}
		return raw;
	}

	UpdLatest readHead(UpdCfg cfg, out byte[] raw)
	{
		return readPointer(cfg, headKey(cfg), out raw);
	}

	UpdLatest readPrevious(UpdCfg cfg, out byte[] raw)
	{
		return readPointer(cfg, previousKey(cfg), out raw);
	}

	UpdLatest readPointer(UpdCfg cfg, string key, out byte[] raw)
	{
		if (!hasObj(key))
		{
			raw = null;
			return null;
		}
		string temp = tempFile();
		try
		{
			get(key, temp);
			raw = read(temp, UpdLim.LatestMax);
			return openHead(cfg, raw, true);
		}
		finally
		{
			dropTemp(temp);
		}
	}

	static bool sameLatest(UpdCfg cfg, UpdLatest expected, byte[] raw)
	{
		try
		{
			UpdLatest actual = openHead(cfg, raw, true);
			return actual.seq == expected.seq &&
				actual.releaseId == expected.releaseId &&
				actual.manifestSha == expected.manifestSha &&
				actual.manifestSize == expected.manifestSize;
		}
		catch
		{
			return false;
		}
	}

	UpdLatest readLocal(UpdCfg cfg, bool needed, out byte[] raw)
	{
		string path = headFile(cfg);
		if (!File.Exists(path))
		{
			if (needed) throw new FileNotFoundException("本地Latest不存在", path);
			raw = null;
			return null;
		}
		raw = read(path, UpdLim.LatestMax);
		return openHead(cfg, raw, true);
	}

	static UpdLatest openHead(UpdCfg cfg, byte[] raw, bool live)
	{
		UpdBox box = UpdJson.box(raw);
		UpdRet<byte[]> opened = new UpdSign(cfg.pubKey).open(box);
		if (!opened.ok)
		{
			throw new InvalidDataException("Latest验签失败:" + opened.err);
		}
		UpdLatest head = UpdJson.latest(opened.value);
		if (live) UpdRule.latest(cfg, head);
		else checkHead(cfg, head);
		return head;
	}

	static void checkHead(UpdCfg cfg, UpdLatest head)
	{
		if (head == null || head.schema != UpdLim.Schema || head.env != cfg.env ||
			head.platform != cfg.platform || head.baseId != cfg.baseId || head.seq < 0 ||
			!UpdFmt.isId(head.releaseId) || !UpdFmt.isSha(head.manifestSha) ||
			head.manifestSize < 1 || head.manifestSize > UpdLim.ManMax)
		{
			throw new InvalidDataException("远端Latest身份错误");
		}
	}

	bool hasObj(string key)
	{
		string[] keys = mStore.list(key);
		if (keys == null) throw new IOException("发布存储返回了空列表");
		for (int i = 0; i < keys.Length; ++i)
		{
			if (fixKey(keys[i], false) == key) return true;
		}
		return false;
	}

	string[] relKeys(string env, string relId)
	{
		string prefix = relKey(env, relId, string.Empty);
		string[] keys = mStore.list(prefix);
		if (keys == null) throw new IOException("发布存储返回了空列表");
		for (int i = 0; i < keys.Length; ++i)
		{
			keys[i] = fixKey(keys[i], false);
			if (!keys[i].StartsWith(prefix, StringComparison.Ordinal))
			{
				throw new IOException("发布存储返回了前缀外对象");
			}
		}
		return keys;
	}

	void get(string key, string local)
	{
		string parent = Path.GetDirectoryName(local);
		if (string.IsNullOrEmpty(parent))
		{
			throw new InvalidDataException("下载路径错误");
		}
		Directory.CreateDirectory(parent);
		if (File.Exists(local)) File.Delete(local);
		mStore.get(key, local);
		if (!File.Exists(local))
		{
			throw new IOException("存储适配器未生成下载文件");
		}
	}

	void putRaw(string key, byte[] raw)
	{
		string temp = tempFile();
		try
		{
			writeNew(temp, raw);
			mStore.put(key, temp);
		}
		finally
		{
			dropTemp(temp);
		}
	}

	void putFile(string key, string path, UpdFile file)
	{
		try
		{
			mStore.put(key, path);
		}
		catch (Exception ex)
		{
			if (fileSame(key, file)) return;
			throw new IOException("Release文件上传失败:" + file.path, ex);
		}
	}

	void putMan(string key, byte[] raw)
	{
		try
		{
			putRaw(key, raw);
		}
		catch (Exception ex)
		{
			if (rawSame(key, raw, UpdLim.ManMax)) return;
			throw new IOException("Manifest上传失败", ex);
		}
	}

	bool fileSame(string key, UpdFile file)
	{
		string temp = tempFile();
		try
		{
			get(key, temp);
			checkFile(temp, file);
			return true;
		}
		catch
		{
			return false;
		}
		finally
		{
			dropTemp(temp);
		}
	}

	bool rawSame(string key, byte[] raw, int max)
	{
		string temp = tempFile();
		try
		{
			get(key, temp);
			return same(read(temp, max), raw);
		}
		catch
		{
			return false;
		}
		finally
		{
			dropTemp(temp);
		}
	}

	void ensureOpen()
	{
		if (mDone) throw new ObjectDisposedException(nameof(PubFlow));
	}

	void step(string text, int done, int total)
	{
		mProg?.Invoke(text, done, total);
	}

	static bool hasKey(string[] keys, string key)
	{
		for (int i = 0; i < keys.Length; ++i)
		{
			if (keys[i] == key) return true;
		}
		return false;
	}

	static string relKey(string env, string relId, string path)
	{
		return env + "/releases/" + relId + "/" + path;
	}

	static string headKey(UpdCfg cfg)
	{
		return cfg.env + "/latest/" + cfg.platform + "/" + cfg.baseId + ".json";
	}

	static string previousKey(UpdCfg cfg)
	{
		return cfg.env + "/previous/" + cfg.platform + "/" + cfg.baseId + ".json";
	}

	static string lockKey(UpdCfg cfg)
	{
		return cfg.env + "/locks/" + cfg.platform + "/" + cfg.baseId + ".lock";
	}

	string headFile(UpdCfg cfg)
	{
		return Path.Combine(mEnv.root(), cfg.env, "latest", cfg.platform,
			cfg.baseId + ".json");
	}

	string findEnv(string root, string relId)
	{
		string found = null;
		foreach (string env in mEnv.envIds)
		{
			string path = Path.Combine(root, env, "releases", relId, "manifest.json");
			if (!File.Exists(path)) continue;
			if (found != null)
			{
				throw new InvalidDataException("Release同时存在于测试和正式目录");
			}
			found = env;
		}
		return found ?? throw new FileNotFoundException("本地Release不存在", relId);
	}

	static string filePath(PubData data, string path)
	{
		return Path.Combine(data.relDir, "files", osPath(path));
	}

	static string osPath(string path)
	{
		return path.Replace('/', Path.DirectorySeparatorChar);
	}

	static string fixKey(string key, bool emptyOk)
	{
		string value = (key ?? string.Empty).Trim().Trim('/');
		if (value.Length == 0 && emptyOk) return string.Empty;
		if (!UpdFmt.isPath(value))
		{
			throw new InvalidDataException("对象路径错误");
		}
		return value;
	}

	FileStream takeLock(string env)
	{
		string root = Path.Combine(mEnv.root(), env);
		Directory.CreateDirectory(root);
		try
		{
			return new FileStream(Path.Combine(root, ".pub.lock"),
				FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
		}
		catch (IOException ex)
		{
			throw new IOException("已有本地发布事务正在执行", ex);
		}
	}

	static byte[] json(object value)
	{
		return sUtf8.GetBytes(JsonUtility.ToJson(value, false));
	}

	static byte[] read(string path, int max)
	{
		FileInfo info = new(path);
		if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0 ||
			info.Length < 1 || info.Length > max)
		{
			throw new InvalidDataException("发布文件大小错误:" + path);
		}
		return File.ReadAllBytes(path);
	}

	static void checkFile(string path, UpdFile file)
	{
		FileInfo info = new(path);
		if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0 ||
			info.Length != file.size || UpdHash.file(path) != file.sha256)
		{
			throw new InvalidDataException("Release文件校验失败:" + file.path);
		}
	}

	static void writeNew(string path, byte[] raw)
	{
		string dir = Path.GetDirectoryName(path);
		if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
		using FileStream output = new(path, FileMode.CreateNew, FileAccess.Write,
			FileShare.None);
		output.Write(raw, 0, raw.Length);
		output.Flush(true);
	}

	static bool same(byte[] left, byte[] right)
	{
		if (left == null || right == null || left.Length != right.Length) return false;
		for (int i = 0; i < left.Length; ++i)
		{
			if (left[i] != right[i]) return false;
		}
		return true;
	}

	static string tempDir()
	{
		string path = Path.Combine(Path.GetTempPath(), "pub-read-" +
			Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(path);
		return path;
	}

	static string tempFile()
	{
		return Path.Combine(Path.GetTempPath(), "pub-read-" +
			Guid.NewGuid().ToString("N"));
	}

	static void dropTemp(string path)
	{
		if (File.Exists(path)) File.Delete(path);
		else if (Directory.Exists(path)) Directory.Delete(path, true);
	}

	internal static void noLinks(string path)
	{
		for (DirectoryInfo dir = new(path); dir != null; dir = dir.Parent)
		{
			if (dir.Exists && (dir.Attributes & FileAttributes.ReparsePoint) != 0)
			{
				throw new InvalidDataException("发布路径不能经过符号链接:" + dir.FullName);
			}
		}
	}
}
