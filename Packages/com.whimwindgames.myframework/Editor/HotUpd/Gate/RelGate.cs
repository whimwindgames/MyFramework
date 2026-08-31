using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

[Flags]
public enum RelGatePhase
{
	Project = 1,
	Plan = 2,
	Candidate = 4,
	All = Project | Plan | Candidate,
}

public static class RelGateSeverity
{
	public const string Info = "info";
	public const string Warning = "warning";
	public const string Error = "error";
}

[Serializable]
public sealed class RelGateDiagnostic
{
	public string gate;
	public string phase;
	public string severity;
	public string code;
	public string message;
	public string path;
}

[Serializable]
public sealed class RelGateReport
{
	public int schema = 1;
	public bool ok;
	public string env;
	public string platform;
	public string baseId;
	public string releaseId;
	public string phases;
	public string timeUtc;
	public long durationMs;
	public RelGateDiagnostic[] diagnostics = Array.Empty<RelGateDiagnostic>();
}

// 随Base冻结并供门禁只读使用的信任记录。AOT程序集是完整Base能力，不是补丁元数据清单。
public sealed class RelGateBase
{
	public string env { get; }
	public string platform { get; }
	public string baseId { get; }
	public string baseUrl { get; }
	public string publicKey { get; }
	public bool contentAddressed { get; }
	public string resourceList { get; }
	public string[] aotAssemblies { get; }

	public RelGateBase(UpdCfg cfg, IEnumerable<string> aotAssemblies)
	{
		if (cfg == null) throw new ArgumentNullException(nameof(cfg));
		UpdRule.cfg(cfg);
		env = cfg.env;
		platform = cfg.platform;
		baseId = cfg.baseId;
		baseUrl = cfg.baseUrl;
		publicKey = cfg.pubKey;
		contentAddressed = cfg.contentAddressed;
		resourceList = cfg.resList;
		this.aotAssemblies = normalizeAssemblies(aotAssemblies);
	}

	public static RelGateBase from(UpdCfg cfg, AotBaseInfo info)
	{
		if (info == null || info.dlls == null || info.baseUrl != cfg?.baseUrl ||
			info.pubKey != cfg?.pubKey ||
			info.contentAddressed != cfg.contentAddressed)
		{
			throw new InvalidDataException("门禁Base冻结记录与Release配置不一致");
		}
		return new RelGateBase(cfg, info.dlls);
	}

	static string[] normalizeAssemblies(IEnumerable<string> values)
	{
		HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
		foreach (string raw in values ?? Array.Empty<string>())
		{
			string name = Path.GetFileNameWithoutExtension((raw ?? string.Empty).Trim());
			if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(
				Path.GetInvalidFileNameChars()) >= 0)
			{
				throw new InvalidDataException("门禁Base AOT程序集名非法:" + raw);
			}
			if (!names.Add(name))
			{
				throw new InvalidDataException("门禁Base AOT程序集重复:" + name);
			}
		}
		List<string> sorted = new(names);
		sorted.Sort(StringComparer.OrdinalIgnoreCase);
		return sorted.ToArray();
	}
}

// 所有门禁共享的只读输入：Release DLL计划、AB计划和对应Base冻结记录。
public sealed class RelGateInput
{
	public UpdCfg cfg { get; }
	public HotPlan hot { get; }
	public AbPlan assets { get; }
	public RelGateBase trustedBase { get; }
	public string stage { get; }
	public string releaseId { get; }

	public RelGateInput(UpdCfg cfg, HotPlan hot, AbPlan assets,
		RelGateBase trustedBase, string stage = null, string releaseId = null)
	{
		this.cfg = clone(cfg ?? throw new ArgumentNullException(nameof(cfg)));
		this.hot = hot ?? throw new ArgumentNullException(nameof(hot));
		this.assets = assets ?? throw new ArgumentNullException(nameof(assets));
		this.trustedBase = trustedBase ??
			throw new ArgumentNullException(nameof(trustedBase));
		UpdRule.prod(this.cfg);
		HotList.chk(this.hot);
		HotList.chkCfg(this.cfg, this.hot.hot);
		AbCheck.need(this.assets);
		if (trustedBase.env != this.cfg.env ||
			trustedBase.platform != this.cfg.platform ||
			trustedBase.baseId != this.cfg.baseId ||
			trustedBase.baseUrl != this.cfg.baseUrl ||
			trustedBase.publicKey != this.cfg.pubKey ||
			trustedBase.contentAddressed != this.cfg.contentAddressed ||
			trustedBase.resourceList != this.cfg.resList)
		{
			throw new InvalidDataException("门禁输入的Release与Base身份不一致");
		}
		this.stage = normalizeStage(stage);
		if (!string.IsNullOrEmpty(releaseId) && !UpdFmt.isId(releaseId))
		{
			throw new InvalidDataException("门禁Release标识非法:" + releaseId);
		}
		this.releaseId = releaseId;
	}

	internal string fingerprint()
	{
		StringBuilder value = new();
		value.Append(cfg.env).Append('\n').Append(cfg.platform).Append('\n')
			.Append(cfg.baseId).Append('\n').Append(cfg.baseUrl).Append('\n')
			.Append(cfg.pubKey).Append('\n').Append(cfg.contentAddressed ? "cas1" : "rel1")
			.Append('\n').Append(cfg.resList).Append('\n')
			.Append(cfg.entryDll).Append('\n').Append(cfg.hotId).Append('\n')
			.Append(cfg.secret).Append('\n').Append(stage).Append('\n').Append(releaseId)
			.Append('\n');
		append(value, cfg.aotDlls);
		append(value, cfg.codeDlls);
		append(value, trustedBase.aotAssemblies);
		append(value, hot.cap?.optAot);
		append(value, hot.cap?.allow);
		append(value, hot.hot?.dlls);
		append(value, hot.baseReq);
		value.Append("ab=").Append(assets.astCnt).Append('|').Append(assets.edgeCnt)
			.Append('\n');
		append(value, assets.errs);
		List<string> atlas = new(assets.atlas);
		atlas.Sort(StringComparer.Ordinal);
		append(value, atlas);
		foreach (AbPkg pkg in assets.pkgs)
		{
			value.Append("pkg=").Append(pkg.name).Append('|').Append(pkg.key).Append('\n');
			append(value, pkg.deps);
			append(value, pkg.allDeps);
			foreach (AbAst ast in pkg.asts)
			{
				value.Append("ast=").Append(ast.path).Append('|').Append(ast.key)
					.Append('|').Append(ast.name).Append('|').Append(ast.scene).Append('\n');
				append(value, ast.deps);
			}
		}
		return UpdHash.data(Encoding.UTF8.GetBytes(value.ToString()));
	}

	static void append(StringBuilder value, IEnumerable<string> items)
	{
		foreach (string item in items ?? Array.Empty<string>())
		{
			value.Append(item).Append('\n');
		}
	}

	static string normalizeStage(string value)
	{
		if (string.IsNullOrWhiteSpace(value)) return null;
		if (!Path.IsPathRooted(value))
		{
			throw new InvalidDataException("门禁候选Stage必须是绝对路径");
		}
		return Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar,
			Path.AltDirectorySeparatorChar);
	}

	static UpdCfg clone(UpdCfg cfg)
	{
		return new UpdCfg
		{
			baseUrl = cfg.baseUrl,
			env = cfg.env,
			platform = cfg.platform,
			baseId = cfg.baseId,
			pubKey = cfg.pubKey,
			contentAddressed = cfg.contentAddressed,
			retry = cfg.retry,
			timeout = cfg.timeout,
			aotDlls = cfg.aotDlls == null ? null : (string[])cfg.aotDlls.Clone(),
			codeDlls = cfg.codeDlls == null ? null : (string[])cfg.codeDlls.Clone(),
			entryDll = cfg.entryDll,
			hotId = cfg.hotId,
			secret = cfg.secret,
			resList = cfg.resList,
		};
	}
}

public interface IRelGate
{
	string id { get; }
	int order { get; }
	RelGatePhase phases { get; }
	void check(RelGateInput input, RelGateOutput output);
}

// 插件只能通过Output提交结构化诊断，gate/phase由Runner注入，不能伪造。
public sealed class RelGateOutput
{
	readonly string mGate;
	readonly string mPhase;
	readonly List<RelGateDiagnostic> mItems = new();

	internal RelGateOutput(string gate, string phase)
	{
		mGate = gate;
		mPhase = phase;
	}

	public void info(string code, string message, string path = null)
	{
		add(RelGateSeverity.Info, code, message, path);
	}

	public void warning(string code, string message, string path = null)
	{
		add(RelGateSeverity.Warning, code, message, path);
	}

	public void error(string code, string message, string path = null)
	{
		add(RelGateSeverity.Error, code, message, path);
	}

	internal RelGateDiagnostic[] finish()
	{
		mItems.Sort(compare);
		return mItems.ToArray();
	}

	void add(string severity, string code, string message, string path)
	{
		if (!validCode(code) || string.IsNullOrWhiteSpace(message) || message.Length > 4096)
		{
			throw new InvalidDataException("门禁诊断code或message非法");
		}
		string cleanPath = string.IsNullOrWhiteSpace(path) ? null :
			path.Trim().Replace('\\', '/');
		if (cleanPath != null && cleanPath.Length > 2048)
		{
			throw new InvalidDataException("门禁诊断path过长");
		}
		mItems.Add(new RelGateDiagnostic
		{
			gate = mGate,
			phase = mPhase,
			severity = severity,
			code = code,
			message = message.Trim(),
			path = cleanPath,
		});
	}

	static bool validCode(string value)
	{
		if (string.IsNullOrEmpty(value) || value.Length > 96) return false;
		for (int i = 0; i < value.Length; ++i)
		{
			char ch = value[i];
			if ((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9') ||
				ch == '.' || ch == '_' || ch == '-') continue;
			return false;
		}
		return true;
	}

	static int compare(RelGateDiagnostic left, RelGateDiagnostic right)
	{
		int value = string.CompareOrdinal(left.severity, right.severity);
		if (value != 0) return value;
		value = string.CompareOrdinal(left.code, right.code);
		if (value != 0) return value;
		value = string.CompareOrdinal(left.path, right.path);
		return value != 0 ? value : string.CompareOrdinal(left.message, right.message);
	}
}

public static class RelGateRegistry
{
	static readonly Dictionary<string, IRelGate> sGates = new(StringComparer.Ordinal);
	static Func<RelGateRequest, RelGateInput> sInput;

	public static void register(IRelGate gate)
	{
		if (gate == null) throw new ArgumentNullException(nameof(gate));
		checkId(gate.id);
		if (gate.id == RelMonoScriptGate.instance.id &&
			gate.GetType() != typeof(RelMonoScriptGate))
		{
			throw new InvalidOperationException("不能覆盖框架内置门禁:" + gate.id);
		}
		if (gate.phases == 0 || (gate.phases & ~RelGatePhase.All) != 0)
		{
			throw new InvalidDataException("门禁阶段非法:" + gate.id);
		}
		if (sGates.TryGetValue(gate.id, out IRelGate old) && old.GetType() != gate.GetType())
		{
			throw new InvalidOperationException("门禁标识已由其他插件注册:" + gate.id);
		}
		sGates[gate.id] = gate;
	}

	public static void unregister(string id)
	{
		if (!string.IsNullOrEmpty(id)) sGates.Remove(id);
	}

	// CLI只允许一个项目输入适配器；业务项目负责把自己的生产配置转换为通用输入。
	public static void bindInput(Func<RelGateRequest, RelGateInput> provider)
	{
		sInput = provider ?? throw new ArgumentNullException(nameof(provider));
	}

	internal static RelGateInput makeInput(RelGateRequest request)
	{
		return sInput?.Invoke(request) ?? throw new InvalidOperationException(
			"项目尚未通过RelGateRegistry.bindInput注册门禁输入适配器");
	}

	internal static IRelGate[] gates()
	{
		List<IRelGate> values = new(sGates.Values) { RelMonoScriptGate.instance };
		values.Sort((left, right) =>
		{
			int value = left.order.CompareTo(right.order);
			return value != 0 ? value : string.CompareOrdinal(left.id, right.id);
		});
		return values.ToArray();
	}

	internal static IDisposable isolateForTests()
	{
		Dictionary<string, IRelGate> gates = new(sGates, StringComparer.Ordinal);
		Func<RelGateRequest, RelGateInput> input = sInput;
		sGates.Clear();
		sInput = null;
		return new Restore(gates, input);
	}

	static void checkId(string value)
	{
		if (!UpdFmt.isId(value)) throw new InvalidDataException("门禁标识非法:" + value);
	}

	sealed class Restore : IDisposable
	{
		Dictionary<string, IRelGate> mGates;
		Func<RelGateRequest, RelGateInput> mInput;

		internal Restore(Dictionary<string, IRelGate> gates,
			Func<RelGateRequest, RelGateInput> input)
		{
			mGates = gates;
			mInput = input;
		}

		public void Dispose()
		{
			if (mGates == null) return;
			sGates.Clear();
			foreach (KeyValuePair<string, IRelGate> item in mGates)
			{
				sGates.Add(item.Key, item.Value);
			}
			sInput = mInput;
			mGates = null;
			mInput = null;
		}
	}
}

public static class RelGateRunner
{
	public static RelGateReport run(RelGateInput input,
		RelGatePhase phases = RelGatePhase.All)
	{
		if (input == null) throw new ArgumentNullException(nameof(input));
		if (phases == 0 || (phases & ~RelGatePhase.All) != 0)
		{
			throw new InvalidDataException("待执行门禁阶段非法");
		}
		Stopwatch watch = Stopwatch.StartNew();
		List<RelGateDiagnostic> diagnostics = new();
		string fingerprint = input.fingerprint();
		foreach (RelGatePhase phase in new[]
			{ RelGatePhase.Project, RelGatePhase.Plan, RelGatePhase.Candidate })
		{
			if ((phases & phase) == 0) continue;
			foreach (IRelGate gate in RelGateRegistry.gates())
			{
				if ((gate.phases & phase) == 0) continue;
				RelGateOutput output = new(gate.id, phaseName(phase));
				try
				{
					gate.check(input, output);
				}
				catch (Exception ex)
				{
					string message = ex.GetType().Name + ": " + ex.Message;
					if (message.Length > 4000) message = message.Substring(0, 4000);
					output.error("gate.exception", message);
				}
				diagnostics.AddRange(output.finish());
				if (input.fingerprint() != fingerprint)
				{
					diagnostics.Add(new RelGateDiagnostic
					{
						gate = gate.id,
						phase = phaseName(phase),
						severity = RelGateSeverity.Error,
						code = "gate.input_mutated",
						message = "门禁插件修改了只读Release/Base输入",
					});
					watch.Stop();
					return report(input, phases, diagnostics, watch.ElapsedMilliseconds);
				}
			}
		}
		watch.Stop();
		return report(input, phases, diagnostics, watch.ElapsedMilliseconds);
	}

	public static RelGateReport need(RelGateInput input,
		RelGatePhase phases = RelGatePhase.All)
	{
		RelGateReport value = run(input, phases);
		if (!value.ok)
		{
			List<string> errors = new();
			foreach (RelGateDiagnostic item in value.diagnostics)
			{
				if (item.severity == RelGateSeverity.Error)
				{
					errors.Add(item.gate + "/" + item.code + ": " + item.message);
				}
			}
			throw new InvalidDataException("Release门禁失败:\n" + string.Join("\n", errors));
		}
		return value;
	}

	internal static string phaseName(RelGatePhase phase)
	{
		return phase switch
		{
			RelGatePhase.Project => "project",
			RelGatePhase.Plan => "plan",
			RelGatePhase.Candidate => "candidate",
			_ => throw new InvalidDataException("单个门禁阶段非法"),
		};
	}

	internal static string phasesName(RelGatePhase phases)
	{
		List<string> names = new();
		foreach (RelGatePhase phase in new[]
			{ RelGatePhase.Project, RelGatePhase.Plan, RelGatePhase.Candidate })
		{
			if ((phases & phase) != 0) names.Add(phaseName(phase));
		}
		return string.Join(",", names);
	}

	static RelGateReport report(RelGateInput input, RelGatePhase phases,
		List<RelGateDiagnostic> diagnostics, long duration)
	{
		bool ok = true;
		foreach (RelGateDiagnostic item in diagnostics)
		{
			if (item.severity == RelGateSeverity.Error) ok = false;
		}
		return new RelGateReport
		{
			ok = ok,
			env = input.cfg.env,
			platform = input.cfg.platform,
			baseId = input.cfg.baseId,
			releaseId = input.releaseId,
			phases = phasesName(phases),
			timeUtc = DateTime.UtcNow.ToString("o"),
			durationMs = duration,
			diagnostics = diagnostics.ToArray(),
		};
	}
}
