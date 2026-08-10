using System;
using System.IO;
using UnityEditor;
using UnityEngine;

[Serializable]
public sealed class PubReceipt
{
	public int schema = 3;
	public string action;
	public bool ok;
	public string error;
	public string operatorId;
	public string env;
	public string platform;
	public string baseId;
	public string releaseId;
	public long seq;
	public int fileCount;
	public long totalSize;
	public string manSha;
	public string gateEvidenceSha;
	public RelGateReport gate;
	public RelServerReadback server;
	public string auditPath;
	public string auditObjectKey;
	public string auditSha;
	public PubItem[] items;
	public string timeUtc;
	public long durationMs;
}

// 无头发布入口。与PubWin共用PubFlow，全部参数显式传入，输出结构化JSON回执。
// 用法：Unity -batchmode -executeMethod PubCli.runCli -- \
//   -pubAction scan|check|pub|rollback|remote \
//   -pubPlatform Android -pubRoot /abs/releases-root [-pubPrivKey /abs/latest.pem] \
//   [-pubPrivKeyPasswordEnv HOTUPDATE_KEY_PASSWORD] \
//   [-pubEnv test] [-pubBaseId base-2] [-pubRelId <releaseId>] \
//   [-pubGateReceipt /abs/<env>/audit/<releaseId>/gate.json] \
//   [-auditOperator ci-release-bot] \
//   -sshHost 47.243.79.140 [-sshPort 22] [-sshUser hotdeploy] \
//   -sshKey /abs/openssh-key -sshUrl https://47.243.79.140/ \
//   [-pubReceipt /abs/receipt.json]
// scan只需发布目录与平台；check/pub用本地Release身份，remote/rollback用env+baseId；
// pub必须提供已签名门禁凭证；rollback必须提供操作者并使用目标Release的远端凭证。
public static class PubCli
{
	public static void runCli()
	{
		string[] args = Environment.GetCommandLineArgs();
		int code = run(args, out PubReceipt receipt);
		string json = JsonUtility.ToJson(receipt, false);
		Console.WriteLine("PUB_RECEIPT=" + json);
		string path = opt(args, "-pubReceipt", null);
		if (!string.IsNullOrWhiteSpace(path))
		{
			try
			{
				writeReceipt(path, json);
			}
			catch (Exception ex)
			{
				Console.WriteLine("PUB_RECEIPT_WRITE_FAILED=" + ex.Message);
				code = code == 0 ? 3 : code;
			}
		}
		if (Application.isBatchMode)
		{
			EditorApplication.Exit(code);
		}
	}

	public static int run(string[] args, out PubReceipt receipt)
	{
		System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
		receipt = new PubReceipt
		{
			action = opt(args, "-pubAction", string.Empty),
			timeUtc = DateTime.UtcNow.ToString("o"),
		};
		try
		{
			if (string.IsNullOrWhiteSpace(receipt.action))
			{
				throw new InvalidDataException("缺少-pubAction参数");
			}
			string scopedEnv = opt(args, "-pubEnv", null);
			string keyPath = opt(args, "-pubPrivKey", null);
			string passwordEnv = opt(args, "-pubPrivKeyPasswordEnv", null);
			PubEnv env = new()
			{
				envIds = string.IsNullOrWhiteSpace(scopedEnv) ?
					new[] { "test", "prod" } : new[] { scopedEnv },
				pubRoot = need(args, "-pubRoot"),
				privateKeyPathForEnv = string.IsNullOrWhiteSpace(scopedEnv) ? null :
					value => value == scopedEnv ? keyPath : null,
				privateKeyPasswordForEnv = string.IsNullOrWhiteSpace(scopedEnv) ? null :
					value => value == scopedEnv ?
					passwordFromEnvironment(passwordEnv) : null,
			};
			string platform = need(args, "-pubPlatform");
			receipt.platform = platform;
			if (receipt.action == "scan")
			{
				receipt.items = PubFlow.scan(env, platform);
				receipt.ok = true;
				return 0;
			}
			SshCfg ssh = new()
			{
				host = need(args, "-sshHost"),
				port = int.TryParse(opt(args, "-sshPort", "22"), out int port) ? port : 22,
				user = opt(args, "-sshUser", "hotdeploy"),
				key = need(args, "-sshKey"),
				url = need(args, "-sshUrl"),
			};
			using PubFlow flow = new(new SshStore(ssh), env,
				(text, done, total) => Console.WriteLine(
					"PUB_STEP=" + text + " " + done + "/" + total));
			switch (receipt.action)
			{
				case "pub":
				{
					if (string.IsNullOrWhiteSpace(scopedEnv))
						throw new InvalidDataException("pub必须显式提供-pubEnv");
					PubItem item = PubFlow.find(env, platform, need(args, "-pubRelId"));
					applyItem(receipt, item);
					string gatePath = need(args, "-pubGateReceipt");
					receipt.operatorId = need(args, "-auditOperator").Trim();
					RelGateProof proof = RelAudit.loadGate(gatePath,
						env.cfg(item.env, item.platform, item.baseId), item);
					if (proof.evidence.operatorId != receipt.operatorId)
						throw new InvalidDataException("-auditOperator与门禁凭证操作者不一致");
					applyResult(receipt, flow.pubRel(item, gatePath));
					break;
				}
				case "check":
				{
					PubItem item = PubFlow.find(env, platform, need(args, "-pubRelId"));
					applyItem(receipt, item);
					PubHead head = flow.check(item);
					receipt.seq = head.seq;
					break;
				}
				case "remote":
				case "rollback":
				{
					PubItem item = makeScope(args);
					receipt.env = item.env;
					receipt.baseId = item.baseId;
					if (receipt.action == "remote")
					{
						PubHead head = flow.remote(item);
						receipt.releaseId = head.relId;
						receipt.seq = head.seq;
					}
					else
					{
						receipt.operatorId = need(args, "-auditOperator").Trim();
						applyResult(receipt, flow.rollback(item, receipt.operatorId));
						PubHead head = flow.remote(item);
						receipt.releaseId = head.relId;
					}
					break;
				}
				default:
					throw new InvalidDataException("未知-pubAction:" + receipt.action);
			}
			receipt.ok = true;
			return 0;
		}
		catch (Exception ex)
		{
			receipt.ok = false;
			receipt.error = ex.GetType().Name + ": " + ex.Message;
			Debug.LogError("PubCli失败:" + ex);
			return 2;
		}
		finally
		{
			watch.Stop();
			receipt.durationMs = watch.ElapsedMilliseconds;
		}
	}

	internal static void applyItem(PubReceipt receipt, PubItem item)
	{
		receipt.env = item.env;
		receipt.platform = item.platform;
		receipt.baseId = item.baseId;
		receipt.releaseId = item.relId;
		receipt.fileCount = item.fileCnt;
		receipt.totalSize = item.totalSize;
		receipt.manSha = item.manSha;
	}

	internal static void applyResult(PubReceipt receipt, PubResult result)
	{
		RelAuditEvent audit = result.audit.audit;
		receipt.seq = result.seq;
		receipt.operatorId = audit.operatorId;
		receipt.env = audit.env;
		receipt.platform = audit.platform;
		receipt.baseId = audit.baseId;
		receipt.releaseId = audit.releaseId;
		receipt.fileCount = audit.fileCount;
		receipt.totalSize = audit.totalSize;
		receipt.manSha = audit.manifestSha;
		receipt.gateEvidenceSha = result.gate.sha;
		receipt.gate = result.gate.evidence.gate;
		receipt.server = result.server;
		receipt.auditPath = result.audit.path;
		receipt.auditObjectKey = result.auditObjectKey;
		receipt.auditSha = result.audit.sha;
	}

	static PubItem makeScope(string[] args)
	{
		return new PubItem
		{
			env = need(args, "-pubEnv"),
			platform = need(args, "-pubPlatform"),
			baseId = need(args, "-pubBaseId"),
			relId = opt(args, "-pubRelId", null),
		};
	}

	static string opt(string[] args, string name, string fallback)
	{
		for (int i = 0; i < args.Length - 1; ++i)
		{
			if (string.Equals(args[i], name, StringComparison.Ordinal))
			{
				return args[i + 1];
			}
		}
		return fallback;
	}

	static string need(string[] args, string name)
	{
		string value = opt(args, name, null);
		if (string.IsNullOrWhiteSpace(value))
		{
			throw new InvalidDataException("缺少参数:" + name);
		}
		return value;
	}

	static char[] passwordFromEnvironment(string name)
	{
		if (string.IsNullOrWhiteSpace(name)) return null;
		if (!validEnvironmentName(name))
		{
			throw new InvalidDataException("私钥密码环境变量名非法");
		}
		string value = Environment.GetEnvironmentVariable(name);
		if (string.IsNullOrEmpty(value))
		{
			throw new InvalidDataException("私钥密码环境变量不存在或为空:" + name);
		}
		return value.ToCharArray();
	}

	static bool validEnvironmentName(string value)
	{
		if (string.IsNullOrEmpty(value) || value.Length > 128 ||
			!((value[0] >= 'A' && value[0] <= 'Z') || value[0] == '_'))
		{
			return false;
		}
		for (int i = 1; i < value.Length; ++i)
		{
			char ch = value[i];
			if (!((ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9') || ch == '_'))
			{
				return false;
			}
		}
		return true;
	}

	static void writeReceipt(string path, string json)
	{
		if (!Path.IsPathRooted(path))
			throw new InvalidDataException("-pubReceipt必须是绝对路径");
		string full = Path.GetFullPath(path);
		string dir = Path.GetDirectoryName(full);
		Directory.CreateDirectory(dir);
		string temp = full + ".tmp-" + Guid.NewGuid().ToString("N");
		try
		{
			File.WriteAllText(temp, json, new System.Text.UTF8Encoding(false));
			if (File.Exists(full)) File.Replace(temp, full, null);
			else File.Move(temp, full);
		}
		finally
		{
			if (File.Exists(temp)) File.Delete(temp);
		}
	}
}
