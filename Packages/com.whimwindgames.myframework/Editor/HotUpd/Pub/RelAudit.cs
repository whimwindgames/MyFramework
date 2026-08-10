using System;
using System.IO;
using System.Text;
using UnityEngine;

[Serializable]
public sealed class RelGateEvidence
{
	public int schema = 1;
	public string operatorId;
	public string timeUtc;
	public string env;
	public string platform;
	public string baseId;
	public string releaseId;
	public string manifestSha;
	public int fileCount;
	public long totalSize;
	public RelGateReport gate;
}

public sealed class RelGateProof
{
	public RelGateEvidence evidence { get; internal set; }
	public byte[] raw { get; internal set; }
	public string sha { get; internal set; }
	public string path { get; internal set; }
}

[Serializable]
public sealed class RelServerReadback
{
	public bool releaseObjects;
	public bool manifest;
	public bool files;
	public bool latest;
	public string latestReleaseId;
	public long latestSeq;
	public string previousReleaseId;
}

[Serializable]
public sealed class RelAuditEvent
{
	public int schema = 1;
	public string action;
	public string operatorId;
	public string machine;
	public string startedUtc;
	public string completedUtc;
	public long durationMs;
	public string env;
	public string platform;
	public string baseId;
	public string releaseId;
	public long seq;
	public string manifestSha;
	public int fileCount;
	public long totalSize;
	public string gateEvidenceSha;
	public RelGateReport gate;
	public RelServerReadback server;
}

public sealed class RelAuditSaved
{
	public RelAuditEvent audit { get; internal set; }
	public byte[] raw { get; internal set; }
	public string sha { get; internal set; }
	public string path { get; internal set; }
}

// Release门禁证据与发布审计都使用对应Base冻结的ES256密钥签名。
// 它们存放在独立audit树中，不进入不可变Release内容对象集合。
public static class RelAudit
{
	const int MAX = UpdLim.LatestMax;
	static readonly UTF8Encoding sUtf8 = new(false, true);

	public static RelGateProof makeGate(PubEnv env, PubItem item,
		RelGateReport report, string operatorId)
	{
		if (env == null) throw new ArgumentNullException(nameof(env));
		string root = env.root();
		UpdCfg cfg = env.cfg(item?.env, item?.platform, item?.baseId);
		string path = gatePath(root, item.env, item.relId);
		if (File.Exists(path))
		{
			RelGateProof existing = openGate(read(path), cfg, item);
			if (existing.evidence.operatorId != cleanOperator(operatorId))
			{
				throw new InvalidDataException("当前Release已有其他操作者的门禁凭证");
			}
			existing.path = path;
			return existing;
		}
		checkGate(report, item);
		RelGateEvidence evidence = new()
		{
			operatorId = cleanOperator(operatorId),
			timeUtc = DateTime.UtcNow.ToString("o"),
			env = item.env,
			platform = item.platform,
			baseId = item.baseId,
			releaseId = item.relId,
			manifestSha = item.manSha,
			fileCount = item.fileCnt,
			totalSize = item.totalSize,
			gate = report,
		};
		byte[] raw = sign(env.signer(item.env), evidence);
		RelGateProof proof = openGate(raw, cfg, item);
		writeImmutable(path, raw);
		proof.path = path;
		return proof;
	}

	public static RelGateProof loadGate(string path, UpdCfg cfg, PubItem item)
	{
		if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path))
		{
			throw new InvalidDataException("门禁凭证必须使用绝对路径");
		}
		string full = Path.GetFullPath(path);
		noLinks(full);
		RelGateProof proof = openGate(read(full), cfg, item);
		proof.path = full;
		return proof;
	}

	public static RelGateProof openGate(byte[] raw, UpdCfg cfg, PubItem item)
	{
		byte[] body = open(raw, cfg);
		RelGateEvidence evidence;
		try { evidence = JsonUtility.FromJson<RelGateEvidence>(sUtf8.GetString(body)); }
		catch (Exception ex) { throw new InvalidDataException("门禁凭证JSON错误", ex); }
		checkEvidence(evidence, item);
		return new RelGateProof
		{
			evidence = evidence,
			raw = (byte[])raw.Clone(),
			sha = UpdHash.data(raw),
		};
	}

	public static RelAuditSaved makeEvent(PubEnv env, string action,
		PubItem item, long seq, RelGateProof proof, RelServerReadback server,
		string operatorId, DateTime startedUtc, long durationMs)
	{
		if (env == null) throw new ArgumentNullException(nameof(env));
		if (action != "publish" && action != "rollback")
			throw new InvalidDataException("发布审计动作非法");
		if (proof == null) throw new ArgumentNullException(nameof(proof));
		UpdCfg cfg = env.cfg(item?.env, item?.platform, item?.baseId);
		checkEvidence(proof.evidence, item);
		checkServer(server, item, seq);
		RelAuditEvent audit = new()
		{
			action = action,
			operatorId = cleanOperator(operatorId),
			machine = cleanMachine(Environment.MachineName),
			startedUtc = startedUtc.ToUniversalTime().ToString("o"),
			completedUtc = DateTime.UtcNow.ToString("o"),
			durationMs = Math.Max(0, durationMs),
			env = item.env,
			platform = item.platform,
			baseId = item.baseId,
			releaseId = item.relId,
			seq = seq,
			manifestSha = item.manSha,
			fileCount = item.fileCnt,
			totalSize = item.totalSize,
			gateEvidenceSha = proof.sha,
			gate = proof.evidence.gate,
			server = server,
		};
		byte[] raw = sign(env.signer(item.env), audit);
		RelAuditSaved saved = openEvent(raw, cfg, item, seq, proof.sha, action);
		string path = eventPath(env.root(), item.env, item.relId, item.platform,
			item.baseId, seq);
		if (File.Exists(path))
		{
			RelAuditSaved existing = openEvent(read(path), cfg, item, seq,
				proof.sha, action);
			existing.path = path;
			return existing;
		}
		writeImmutable(path, raw);
		saved.path = path;
		return saved;
	}

	public static RelAuditSaved openEvent(byte[] raw, UpdCfg cfg, PubItem item,
		long seq, string gateSha, string action)
	{
		byte[] body = open(raw, cfg);
		RelAuditEvent audit;
		try { audit = JsonUtility.FromJson<RelAuditEvent>(sUtf8.GetString(body)); }
		catch (Exception ex) { throw new InvalidDataException("发布审计JSON错误", ex); }
		if (audit == null || audit.schema != 1 || audit.action != action ||
			audit.env != item.env || audit.platform != item.platform ||
			audit.baseId != item.baseId || audit.releaseId != item.relId ||
			audit.seq != seq || audit.manifestSha != item.manSha ||
			audit.fileCount != item.fileCnt || audit.totalSize != item.totalSize ||
			audit.gateEvidenceSha != gateSha || audit.durationMs < 0)
		{
			throw new InvalidDataException("发布审计与Release身份不一致");
		}
		cleanOperator(audit.operatorId);
		checkTime(audit.startedUtc, "审计开始时间");
		checkTime(audit.completedUtc, "审计完成时间");
		checkGate(audit.gate, item);
		checkServer(audit.server, item, seq);
		return new RelAuditSaved
		{
			audit = audit,
			raw = (byte[])raw.Clone(),
			sha = UpdHash.data(raw),
		};
	}

	// CI重试可能只保留远端审计。回读、验签后把同一不可变事件恢复到本地，
	// 避免因新的时间戳/随机ECDSA签名制造同seq冲突。
	public static RelAuditSaved adoptEvent(PubEnv env, byte[] raw, UpdCfg cfg,
		PubItem item, long seq, string gateSha, string action)
	{
		if (env == null) throw new ArgumentNullException(nameof(env));
		RelAuditSaved saved = openEvent(raw, cfg, item, seq, gateSha, action);
		string path = eventPath(env.root(), item.env, item.relId, item.platform,
			item.baseId, seq);
		if (File.Exists(path))
		{
			byte[] local = read(path);
			openEvent(local, cfg, item, seq, gateSha, action);
			if (!same(local, raw))
				throw new InvalidDataException("本地与远端发布审计内容冲突");
		}
		else
		{
			writeImmutable(path, raw);
		}
		saved.path = path;
		return saved;
	}

	public static string gateObjectKey(PubItem item)
	{
		return item.env + "/audit/" + item.relId + "/gate.json";
	}

	public static string eventObjectKey(PubItem item, long seq)
	{
		return item.env + "/audit/" + item.relId + "/events/" + item.platform +
			"/" + item.baseId + "/" + seq + ".json";
	}

	static byte[] sign(RelSign signer, object value)
	{
		byte[] body = sUtf8.GetBytes(JsonUtility.ToJson(value, false));
		if (body.Length < 1 || body.Length > MAX)
			throw new InvalidDataException("签名审计内容超过32KB上限");
		UpdBox box = new()
		{
			schema = UpdLim.Schema,
			alg = "ES256",
			data = Convert.ToBase64String(body),
			sig = Convert.ToBase64String(signer.sign(body)),
		};
		byte[] raw = sUtf8.GetBytes(JsonUtility.ToJson(box, false));
		if (raw.Length > MAX) throw new InvalidDataException("签名审计封装超过32KB上限");
		return raw;
	}

	static byte[] open(byte[] raw, UpdCfg cfg)
	{
		if (cfg == null) throw new ArgumentNullException(nameof(cfg));
		if (raw == null || raw.Length < 1 || raw.Length > MAX)
			throw new InvalidDataException("签名审计大小错误");
		UpdBox box = UpdJson.box(raw);
		UpdRet<byte[]> opened = new UpdSign(cfg.pubKey).open(box);
		if (!opened.ok) throw new InvalidDataException("签名审计验签失败:" + opened.err);
		return opened.value;
	}

	static void checkEvidence(RelGateEvidence value, PubItem item)
	{
		if (value == null || value.schema != 1 || item == null ||
			value.env != item.env || value.platform != item.platform ||
			value.baseId != item.baseId || value.releaseId != item.relId ||
			value.manifestSha != item.manSha || value.fileCount != item.fileCnt ||
			value.totalSize != item.totalSize)
		{
			throw new InvalidDataException("门禁凭证与Release身份不一致");
		}
		cleanOperator(value.operatorId);
		checkTime(value.timeUtc, "门禁凭证时间");
		checkGate(value.gate, item);
	}

	static void checkGate(RelGateReport report, PubItem item)
	{
		if (report == null || !report.ok || report.schema != 1 ||
			report.env != item.env || report.platform != item.platform ||
			report.baseId != item.baseId || report.releaseId != item.relId ||
			report.phases != RelGateRunner.phasesName(RelGatePhase.All) ||
			report.diagnostics == null || report.durationMs < 0)
		{
			throw new InvalidDataException("必须提供当前Release全部阶段通过的门禁结果");
		}
		checkTime(report.timeUtc, "门禁执行时间");
		foreach (RelGateDiagnostic diagnostic in report.diagnostics)
		{
			if (diagnostic == null || diagnostic.severity == RelGateSeverity.Error)
				throw new InvalidDataException("门禁结果包含错误诊断");
		}
	}

	static void checkServer(RelServerReadback server, PubItem item, long seq)
	{
		if (server == null || !server.releaseObjects || !server.manifest ||
			!server.files || !server.latest || server.latestReleaseId != item.relId ||
			server.latestSeq != seq)
		{
			throw new InvalidDataException("服务器回读审计未通过");
		}
	}

	static string cleanOperator(string value)
	{
		string clean = (value ?? string.Empty).Trim();
		if (clean.Length < 1 || clean.Length > 128)
			throw new InvalidDataException("审计操作者不能为空且不能超过128字符");
		for (int i = 0; i < clean.Length; ++i)
			if (char.IsControl(clean[i])) throw new InvalidDataException("审计操作者包含控制字符");
		return clean;
	}

	static string cleanMachine(string value)
	{
		string clean = string.IsNullOrWhiteSpace(value) ? "unknown" : value.Trim();
		return clean.Length <= 128 ? clean : clean.Substring(0, 128);
	}

	static void checkTime(string value, string name)
	{
		if (!DateTime.TryParse(value, null, System.Globalization.DateTimeStyles.RoundtripKind,
			out DateTime parsed) || parsed.Kind != DateTimeKind.Utc)
		{
			throw new InvalidDataException(name + "不是UTC ISO-8601时间");
		}
	}

	static string gatePath(string root, string env, string relId)
	{
		return Path.Combine(root, env, "audit", relId, "gate.json");
	}

	static string eventPath(string root, string env, string relId, string platform,
		string baseId, long seq)
	{
		return Path.Combine(root, env, "audit", relId, "events", platform,
			baseId, seq + ".json");
	}

	static byte[] read(string path)
	{
		FileInfo info = new(path);
		if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0 ||
			info.Length < 1 || info.Length > MAX)
		{
			throw new InvalidDataException("签名审计文件大小错误:" + path);
		}
		return File.ReadAllBytes(path);
	}

	static void writeImmutable(string path, byte[] raw)
	{
		string dir = Path.GetDirectoryName(path);
		Directory.CreateDirectory(dir);
		noLinks(dir);
		using FileStream output = new(path, FileMode.CreateNew, FileAccess.Write,
			FileShare.None);
		output.Write(raw, 0, raw.Length);
		output.Flush(true);
	}

	static void noLinks(string path)
	{
		FileSystemInfo info = File.Exists(path) ? new FileInfo(path) : new DirectoryInfo(path);
		if (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0)
			throw new InvalidDataException("审计路径不能是符号链接:" + path);
		for (DirectoryInfo dir = info is FileInfo file ? file.Directory :
			(DirectoryInfo)info; dir != null; dir = dir.Parent)
		{
			if (dir.Exists && (dir.Attributes & FileAttributes.ReparsePoint) != 0)
				throw new InvalidDataException("审计路径不能经过符号链接:" + dir.FullName);
		}
	}

	static bool same(byte[] left, byte[] right)
	{
		if (left == null || right == null || left.Length != right.Length) return false;
		for (int i = 0; i < left.Length; ++i)
			if (left[i] != right[i]) return false;
		return true;
	}
}
