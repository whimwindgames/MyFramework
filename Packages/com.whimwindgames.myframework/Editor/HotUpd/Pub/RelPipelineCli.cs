using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

public sealed class RelPipelineRequest
{
	public string env { get; internal set; }
	public string platform { get; internal set; }
	public string baseId { get; internal set; }
	public string operatorId { get; internal set; }
	public string[] args { get; internal set; }
}

// 项目生产适配器可以在provider内调用PackFlow或ProdFlow；返回值必须是刚生成的
// Release、对应门禁输入，以及同一发布目录/环境密钥配置。
public sealed class RelPipelineProduct
{
	public PubEnv publish;
	public RelGateInput gate;
	public string releaseId;
}

public static class RelPipelineRegistry
{
	static Func<RelPipelineRequest, RelPipelineProduct> sProducer;

	public static void bindProducer(Func<RelPipelineRequest, RelPipelineProduct> producer)
	{
		sProducer = producer ?? throw new ArgumentNullException(nameof(producer));
	}

	internal static RelPipelineProduct produce(RelPipelineRequest request)
	{
		return sProducer?.Invoke(request) ?? throw new InvalidOperationException(
			"项目尚未通过RelPipelineRegistry.bindProducer注册生产适配器");
	}

	internal static IDisposable isolateForTests()
	{
		Func<RelPipelineRequest, RelPipelineProduct> old = sProducer;
		sProducer = null;
		return new Restore(old);
	}

	sealed class Restore : IDisposable
	{
		Func<RelPipelineRequest, RelPipelineProduct> mOld;
		internal Restore(Func<RelPipelineRequest, RelPipelineProduct> old) { mOld = old; }
		public void Dispose()
		{
			if (mOld == null && sProducer == null) return;
			sProducer = mOld;
			mOld = null;
		}
	}
}

public static class RelPipeline
{
	public static PubReceipt run(RelPipelineRequest request,
		Func<IObjStore> storeFactory)
	{
		if (request == null) throw new ArgumentNullException(nameof(request));
		if (storeFactory == null) throw new ArgumentNullException(nameof(storeFactory));
		checkRequest(request);
		DateTime started = DateTime.UtcNow;
		System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
		RelPipelineProduct product = RelPipelineRegistry.produce(request) ??
			throw new InvalidDataException("项目生产适配器返回空值");
		if (product.publish == null || product.gate == null ||
			!UpdFmt.isId(product.releaseId))
		{
			throw new InvalidDataException("项目生产适配器返回值不完整");
		}
		PubItem item = PubFlow.find(product.publish, request.platform,
			product.releaseId);
		match(request, product.gate, item);
		RelGateReport gate = RelGateRunner.need(product.gate, RelGatePhase.All);
		RelGateProof proof = RelAudit.makeGate(product.publish, item, gate,
			request.operatorId);
		IObjStore store = storeFactory() ?? throw new InvalidOperationException(
			"发布存储工厂返回空值");
		PubResult result;
		using (PubFlow flow = new(store, product.publish,
			(text, done, total) => Console.WriteLine(
				"REL_STEP=" + text + " " + done + "/" + total)))
		{
			result = flow.pubRel(item, proof.path);
		}
		watch.Stop();
		PubReceipt receipt = new()
		{
			action = "produce-gate-publish",
			ok = true,
			operatorId = request.operatorId.Trim(),
			timeUtc = started.ToString("o"),
			durationMs = watch.ElapsedMilliseconds,
		};
		PubCli.applyItem(receipt, item);
		PubCli.applyResult(receipt, result);
		return receipt;
	}

	static void match(RelPipelineRequest request, RelGateInput gate, PubItem item)
	{
		if (item.env != request.env || item.platform != request.platform ||
			item.baseId != request.baseId || gate.cfg.env != item.env ||
			gate.cfg.platform != item.platform || gate.cfg.baseId != item.baseId ||
			gate.releaseId != item.relId || string.IsNullOrEmpty(gate.stage))
		{
			throw new InvalidDataException("生产、门禁与发布的Release/Base身份不一致");
		}
	}

	static void checkRequest(RelPipelineRequest request)
	{
		if ((request.env != "test" && request.env != "prod") ||
			!UpdFmt.isId(request.platform) || !UpdFmt.isId(request.baseId))
		{
			throw new InvalidDataException("全链路发布身份参数非法");
		}
		string op = (request.operatorId ?? string.Empty).Trim();
		if (op.Length < 1 || op.Length > 128)
			throw new InvalidDataException("全链路发布必须提供审计操作者");
		for (int i = 0; i < op.Length; ++i)
			if (char.IsControl(op[i])) throw new InvalidDataException("审计操作者包含控制字符");
	}
}

// 单命令入口：项目producer先生产，框架再执行全部门禁、签名证据、发布和回读审计。
// Unity -batchmode -quit -projectPath /abs/project \
//   -executeMethod RelPipelineCli.runCli -- \
//   -relEnv prod -relPlatform Android -relBaseId base-1001 \
//   -auditOperator ci-release-bot -relReceipt /abs/audit.json \
//   -sshHost host -sshUser hotdeploy -sshKey /abs/key -sshUrl https://cdn/
public static class RelPipelineCli
{
	static readonly UTF8Encoding sUtf8 = new(false, true);

	public static void runCli()
	{
		string[] args = Environment.GetCommandLineArgs();
		int code = run(args, null, out PubReceipt receipt);
		string json = JsonUtility.ToJson(receipt, false);
		Console.WriteLine("REL_RECEIPT=" + json);
		string path = opt(args, "-relReceipt", null);
		if (!string.IsNullOrWhiteSpace(path))
		{
			try { writeReceipt(path, json); }
			catch (Exception ex)
			{
				Console.WriteLine("REL_RECEIPT_WRITE_FAILED=" + ex.Message);
				code = code == 0 ? 3 : code;
			}
		}
		if (Application.isBatchMode) EditorApplication.Exit(code);
	}

	public static int run(string[] args, Func<IObjStore> storeFactory,
		out PubReceipt receipt)
	{
		DateTime started = DateTime.UtcNow;
		System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
		receipt = new PubReceipt
		{
			action = "produce-gate-publish",
			timeUtc = started.ToString("o"),
		};
		try
		{
			RelPipelineRequest request = new()
			{
				env = need(args, "-relEnv"),
				platform = need(args, "-relPlatform"),
				baseId = need(args, "-relBaseId"),
				operatorId = need(args, "-auditOperator"),
				args = args == null ? Array.Empty<string>() : (string[])args.Clone(),
			};
			Func<IObjStore> make = storeFactory ?? (() => new SshStore(new SshCfg
			{
				host = need(args, "-sshHost"),
				port = int.TryParse(opt(args, "-sshPort", "22"), out int port) ? port : 22,
				user = opt(args, "-sshUser", "hotdeploy"),
				key = need(args, "-sshKey"),
				url = need(args, "-sshUrl"),
			}));
			receipt = RelPipeline.run(request, make);
			return 0;
		}
		catch (Exception ex)
		{
			receipt.ok = false;
			receipt.error = ex.GetType().Name + ": " + ex.Message;
			Debug.LogError("RelPipelineCli失败:" + ex);
			return 2;
		}
		finally
		{
			watch.Stop();
			receipt.durationMs = watch.ElapsedMilliseconds;
		}
	}

	static string opt(string[] args, string name, string fallback)
	{
		if (args == null) return fallback;
		for (int i = 0; i < args.Length - 1; ++i)
			if (string.Equals(args[i], name, StringComparison.Ordinal)) return args[i + 1];
		return fallback;
	}

	static string need(string[] args, string name)
	{
		string value = opt(args, name, null);
		if (string.IsNullOrWhiteSpace(value))
			throw new InvalidDataException("缺少参数:" + name);
		return value;
	}

	static void writeReceipt(string path, string json)
	{
		if (!Path.IsPathRooted(path))
			throw new InvalidDataException("-relReceipt必须是绝对路径");
		string full = Path.GetFullPath(path);
		string dir = Path.GetDirectoryName(full);
		Directory.CreateDirectory(dir);
		string temp = full + ".tmp-" + Guid.NewGuid().ToString("N");
		try
		{
			File.WriteAllText(temp, json, sUtf8);
			if (File.Exists(full)) File.Replace(temp, full, null);
			else File.Move(temp, full);
		}
		finally
		{
			if (File.Exists(temp)) File.Delete(temp);
		}
	}
}
