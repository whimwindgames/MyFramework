using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

public sealed class RelGateRequest
{
	public string env { get; internal set; }
	public string platform { get; internal set; }
	public string baseId { get; internal set; }
	public string releaseId { get; internal set; }
	public string stage { get; internal set; }
	public RelGatePhase phases { get; internal set; }
}

[Serializable]
public sealed class RelGateReceipt
{
	public int schema = 1;
	public bool ok;
	public string error;
	public string timeUtc;
	public long durationMs;
	public RelGateReport report;
}

// 统一无头入口。项目只注册一次RelGateInput适配器，所有门禁共享相同参数和JSON回执。
// Unity -batchmode -executeMethod RelGateCli.runCli -- \
//   -gateEnv test -gatePlatform Android -gateBaseId base-1 \
//   [-gateReleaseId <id>] [-gateStage /abs/candidate] \
//   [-gatePhase project|plan|candidate|all] [-gateReceipt /abs/gate.json]
public static class RelGateCli
{
	static readonly UTF8Encoding sUtf8 = new(false, true);

	public static void runCli()
	{
		string[] args = Environment.GetCommandLineArgs();
		int code = run(args, out RelGateReceipt receipt);
		string json = JsonUtility.ToJson(receipt, false);
		Console.WriteLine("GATE_RECEIPT=" + json);
		string path = opt(args, "-gateReceipt", null);
		if (!string.IsNullOrWhiteSpace(path))
		{
			try
			{
				writeReceipt(path, json);
			}
			catch (Exception ex)
			{
				Console.WriteLine("GATE_RECEIPT_WRITE_FAILED=" + ex.Message);
				code = code == 0 ? 3 : code;
			}
		}
		if (Application.isBatchMode) EditorApplication.Exit(code);
	}

	public static int run(string[] args, out RelGateReceipt receipt)
	{
		System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
		receipt = new RelGateReceipt { timeUtc = DateTime.UtcNow.ToString("o") };
		try
		{
			RelGateRequest request = new()
			{
				env = need(args, "-gateEnv"),
				platform = need(args, "-gatePlatform"),
				baseId = need(args, "-gateBaseId"),
				releaseId = opt(args, "-gateReleaseId", null),
				stage = opt(args, "-gateStage", null),
				phases = parsePhase(opt(args, "-gatePhase", "all")),
			};
			checkRequest(request);
			RelGateInput input = RelGateRegistry.makeInput(request);
			matchRequest(request, input);
			receipt.report = RelGateRunner.run(input, request.phases);
			receipt.ok = receipt.report.ok;
			return receipt.ok ? 0 : 2;
		}
		catch (Exception ex)
		{
			receipt.ok = false;
			receipt.error = ex.GetType().Name + ": " + ex.Message;
			Debug.LogError("RelGateCli失败:" + ex);
			return 2;
		}
		finally
		{
			watch.Stop();
			receipt.durationMs = watch.ElapsedMilliseconds;
		}
	}

	static void matchRequest(RelGateRequest request, RelGateInput input)
	{
		if (input == null || input.cfg.env != request.env ||
			input.cfg.platform != request.platform || input.cfg.baseId != request.baseId ||
			(!string.IsNullOrEmpty(request.releaseId) &&
			 input.releaseId != request.releaseId))
		{
			throw new InvalidDataException("项目门禁输入适配器返回了不同的Release/Base身份");
		}
		if (!string.IsNullOrEmpty(request.stage) &&
			!string.Equals(input.stage, Path.GetFullPath(request.stage)
				.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
				StringComparison.Ordinal))
		{
			throw new InvalidDataException("项目门禁输入适配器返回了不同的候选Stage");
		}
	}

	static void checkRequest(RelGateRequest value)
	{
		if ((value.env != "test" && value.env != "prod") ||
			!UpdFmt.isId(value.platform) || !UpdFmt.isId(value.baseId) ||
			(!string.IsNullOrEmpty(value.releaseId) && !UpdFmt.isId(value.releaseId)))
		{
			throw new InvalidDataException("门禁CLI身份参数非法");
		}
		if (!string.IsNullOrWhiteSpace(value.stage) && !Path.IsPathRooted(value.stage))
		{
			throw new InvalidDataException("-gateStage必须是绝对路径");
		}
	}

	static RelGatePhase parsePhase(string value)
	{
		return value switch
		{
			"project" => RelGatePhase.Project,
			"plan" => RelGatePhase.Plan,
			"candidate" => RelGatePhase.Candidate,
			"all" => RelGatePhase.All,
			_ => throw new InvalidDataException("未知-gatePhase:" + value),
		};
	}

	static void writeReceipt(string path, string json)
	{
		if (!Path.IsPathRooted(path))
		{
			throw new InvalidDataException("-gateReceipt必须是绝对路径");
		}
		string full = Path.GetFullPath(path);
		string dir = Path.GetDirectoryName(full);
		Directory.CreateDirectory(dir);
		string temp = full + ".tmp-" + Guid.NewGuid().ToString("N");
		try
		{
			using (FileStream output = new(temp, FileMode.CreateNew, FileAccess.Write,
				FileShare.None))
			{
				byte[] raw = sUtf8.GetBytes(json);
				output.Write(raw, 0, raw.Length);
				output.Flush(true);
			}
			if (File.Exists(full)) File.Replace(temp, full, null);
			else File.Move(temp, full);
		}
		finally
		{
			if (File.Exists(temp)) File.Delete(temp);
		}
	}

	static string opt(string[] args, string name, string fallback)
	{
		if (args == null) return fallback;
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
}
