using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

public sealed class HotSet
{
	public readonly string[] dlls;
	public readonly string entry;
	public readonly string id;

	public HotSet(string[] dlls, string entry)
	{
		this.dlls = dlls == null ? null : (string[])dlls.Clone();
		this.entry = entry;
		id = UpdRule.hotId(this.dlls, entry);
	}
}

public sealed class HotCap
{
	public readonly string[] optAot;
	public readonly string[] allow;

	public HotCap(string[] optAot, string[] allow)
	{
		this.optAot = optAot == null ? null : (string[])optAot.Clone();
		this.allow = allow == null ? null : (string[])allow.Clone();
	}
}

public sealed class HotPlan
{
	public readonly HotCap cap;
	public readonly HotSet hot;
	public readonly string[] baseReq;

	public HotPlan(HotCap cap, HotSet hot, string[] baseReq)
	{
		this.cap = cap;
		this.hot = hot;
		this.baseReq = baseReq == null ? null : (string[])baseReq.Clone();
	}
}

// Release生产层只依赖已确认的程序集计划；HybridCLR编译分类将在后续编排层产生同一类型。
public static class HotList
{
	internal const string CapName = ".base-cap";
	const string DLL_TAIL = ".dll.bytes";
	static readonly string[] sFixedAot =
	{
		"Frame_Base",
		"Frame_Game",
		"HotUpd_Core",
		"HotUpd_Client",
	};

	public static HotPlan fromCfg(UpdCfg cfg)
	{
		if (cfg == null) throw new ArgumentNullException(nameof(cfg));
		UpdRule.prod(cfg);
		HotSet hot = new(cfg.codeDlls, cfg.entryDll);
		List<string> allow = new() { FrameBaseDefine.HOTFIX_FRAME };
		List<string> rest = new();
		for (int i = 0; i < cfg.codeDlls.Length; ++i)
		{
			string name = rawName(cfg.codeDlls[i]);
			if (name != FrameBaseDefine.HOTFIX_FRAME)
				rest.Add(name);
		}
		rest.Sort(StringComparer.Ordinal);
		allow.AddRange(rest);
		HotPlan plan = new(new HotCap(Array.Empty<string>(), allow.ToArray()), hot, Array.Empty<string>());
		chk(plan);
		chkCfg(cfg, hot);
		return plan;
	}

	// 使用已经随主包冻结的能力生产补丁。Release只能缩小Hot集合，不能把主包
	// 不认识的程序集临时改成Hot。
	public static HotPlan fromCfg(UpdCfg cfg, HotCap cap)
	{
		return plan(cfg, cap, Array.Empty<string>());
	}

	// 项目资源分析器可把AB中的MonoScript程序集作为Base AOT前置需求传入，
	// 通用框架不需要知道项目的资源分组规则。
	public static HotPlan plan(UpdCfg cfg, HotCap cap, IEnumerable<string> baseReq)
	{
		if (cfg == null) throw new ArgumentNullException(nameof(cfg));
		HotSet hot = new(cfg.codeDlls, cfg.entryDll);
		List<string> required = new(baseReq ?? Array.Empty<string>());
		required.Sort(StringComparer.Ordinal);
		HotPlan value = new(cap, hot, required.ToArray());
		chk(value);
		chkCfg(cfg, hot);
		return value;
	}

	public static HotCap loadCap(string root)
	{
		string path = Path.Combine(root ?? string.Empty, CapName);
		FileInfo file = new(path);
		if (!file.Exists || file.Length <= 0 || file.Length > 64 * 1024 ||
			(file.Attributes & FileAttributes.ReparsePoint) != 0)
			throw new InvalidDataException("冻结Base能力不存在或非法:" + path);
		string text = File.ReadAllText(path, new UTF8Encoding(false, true));
		if (text.IndexOf('\r') >= 0) throw new InvalidDataException("冻结Base能力必须使用LF换行");
		string[] lines = text.Split('\n');
		if (lines.Length < 5 || lines[0] != "schema=3" ||
			lines[1] != "entry=" + FrameBaseDefine.HOTFIX ||
			lines[lines.Length - 1].Length != 0)
			throw new InvalidDataException("冻结Base能力格式错误");

		List<string> aot = new();
		List<string> allow = new();
		int group = 0;
		for (int i = 2; i < lines.Length - 1; ++i)
		{
			string line = lines[i];
			int next;
			List<string> target;
			string value;
			if (line.StartsWith("aot=", StringComparison.Ordinal))
			{
				next = 1; target = aot; value = line.Substring(4);
			}
			else if (line.StartsWith("known=", StringComparison.Ordinal))
			{
				next = 2; target = allow; value = line.Substring(6);
			}
			else throw new InvalidDataException("冻结Base能力格式错误");
			if (next < group) throw new InvalidDataException("冻结Base能力字段顺序错误");
			group = next;
			target.Add(value);
		}
		HotCap cap = new(aot.ToArray(), allow.ToArray());
		chkCap(cap);
		if (text != capText(cap)) throw new InvalidDataException("冻结Base能力不是规范格式");
		return cap;
	}

	public static void save(string root, HotCap cap)
	{
		chkCap(cap);
		if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
			throw new DirectoryNotFoundException("AOT基线候选目录不存在:" + root);
		string path = Path.Combine(root, CapName);
		if (File.Exists(path))
			throw new InvalidDataException("HybridCLR输出包含框架保留文件:" + CapName);
		File.WriteAllText(path, capText(cap), new UTF8Encoding(false));
	}

	internal static bool same(HotCap left, HotCap right)
	{
		chkCap(left);
		chkCap(right);
		return capText(left) == capText(right);
	}

	public static void chk(HotSet set)
	{
		if (set?.dlls == null || set.dlls.Length < 2 || set.dlls.Length > UpdLim.CodeMax ||
			!UpdFmt.isPath(set.entry) ||
			!set.entry.EndsWith(DLL_TAIL, StringComparison.OrdinalIgnoreCase) ||
			!UpdFmt.isSha(set.id) ||
			set.id != UpdRule.hotId(set.dlls, set.entry))
		{
			throw new InvalidDataException("热更程序集清单身份错误");
		}
		HashSet<string> dlls = new(StringComparer.OrdinalIgnoreCase);
		HashSet<string> fixedSet = new(sFixedAot, StringComparer.OrdinalIgnoreCase);
		int frameAt = -1;
		int entryAt = -1;
		for (int i = 0; i < set.dlls.Length; ++i)
		{
			string dll = set.dlls[i];
			if (!UpdFmt.isPath(dll) || !dll.EndsWith(DLL_TAIL, StringComparison.OrdinalIgnoreCase) ||
				!dlls.Add(dll))
				throw new InvalidDataException("热更程序集清单文件非法:" + dll);
			string name = rawName(dll);
			if (fixedSet.Contains(name)) throw new InvalidDataException("固定AOT不得进入RelHotSet:" + name);
			if (name == FrameBaseDefine.HOTFIX_FRAME) frameAt = i;
			if (string.Equals(dll, set.entry, StringComparison.OrdinalIgnoreCase)) entryAt = i;
		}
		if (frameAt < 0 || entryAt < 0 || frameAt >= entryAt || !dlls.Contains(set.entry))
			throw new InvalidDataException("固定热更层顺序或唯一入口错误");
	}

	public static void chk(HotSet set, HotCap cap)
	{
		chk(set);
		chkCap(cap);
		HashSet<string> allow = new(cap.allow, StringComparer.OrdinalIgnoreCase);
		foreach (string dll in set.dlls)
			if (!allow.Contains(rawName(dll))) throw new InvalidDataException("Release程序集不在Base能力内:" + dll);
	}

	public static void chk(HotPlan plan)
	{
		if (plan?.baseReq == null) throw new InvalidDataException("热更生产计划为空");
		chk(plan.hot, plan.cap);
		HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
		string prev = null;
		foreach (string name in plan.baseReq)
		{
			if (!isName(name) || !seen.Add(name) ||
				(prev != null && string.CompareOrdinal(prev, name) >= 0))
				throw new InvalidDataException("Base AOT需求非法或未排序:" + name);
			prev = name;
		}
	}

	internal static void chkCfg(UpdCfg cfg, HotSet hot)
	{
		chk(hot);
		UpdRule.prod(cfg);
		if (cfg.hotId != hot.id || cfg.entryDll != hot.entry || cfg.codeDlls == null ||
			cfg.codeDlls.Length != hot.dlls.Length)
			throw new InvalidDataException("运行配置与本次热更计划不一致");
		for (int i = 0; i < hot.dlls.Length; ++i)
			if (cfg.codeDlls[i] != hot.dlls[i]) throw new InvalidDataException("运行配置热更顺序与本次计划不一致");
	}

	internal static string[] aotDeny(HotCap cap)
	{
		chkCap(cap);
		HashSet<string> seen = new(cap.optAot, StringComparer.OrdinalIgnoreCase);
		List<string> names = new(cap.optAot);
		foreach (string name in sFixedAot) if (seen.Add(name)) names.Add(name);
		names.Sort(StringComparer.Ordinal);
		return names.ToArray();
	}

	static void chkCap(HotCap cap)
	{
		if (cap?.optAot == null || cap.allow == null || cap.allow.Length < 2 ||
			cap.allow.Length > UpdLim.CodeMax || cap.optAot.Length > UpdLim.AotMax ||
			cap.allow[0] != FrameBaseDefine.HOTFIX_FRAME)
			throw new InvalidDataException("Base程序集能力清单数量或固定顺序错误");
		HashSet<string> allow = checkNames(cap.allow, "Base已知Hot");
		HashSet<string> aot = checkNames(cap.optAot, "AOT程序集");
		checkOrder(cap.allow, 1, "Base已知Hot");
		checkOrder(cap.optAot, 0, "AOT程序集");
		HashSet<string> fixedSet = new(sFixedAot, StringComparer.OrdinalIgnoreCase);
		foreach (string name in allow)
			if (fixedSet.Contains(name)) throw new InvalidDataException("固定AOT不得进入Base已知Hot:" + name);
		foreach (string name in aot)
			if (allow.Contains(name) || fixedSet.Contains(name)) throw new InvalidDataException("AOT与热更分类冲突:" + name);
	}

	static HashSet<string> checkNames(IEnumerable<string> values, string label)
	{
		HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
		foreach (string name in values)
			if (!isName(name) || !names.Add(name)) throw new InvalidDataException(label + "非法:" + name);
		return names;
	}

	static void checkOrder(string[] values, int start, string label)
	{
		for (int i = start + 1; i < values.Length; ++i)
			if (string.CompareOrdinal(values[i - 1], values[i]) >= 0)
				throw new InvalidDataException(label + "未按规范排序");
	}

	internal static string rawName(string dll)
	{
		if (string.IsNullOrEmpty(dll) || !dll.EndsWith(DLL_TAIL, StringComparison.OrdinalIgnoreCase))
			throw new InvalidDataException("热更DLL名称非法:" + dll);
		string name = dll.Substring(0, dll.Length - DLL_TAIL.Length);
		if (!isName(name)) throw new InvalidDataException("程序集名称非法:" + name);
		return name;
	}

	static bool isName(string name)
	{
		return UpdFmt.isId(name) && name == Path.GetFileName(name) &&
			!name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
	}

	static string capText(HotCap cap)
	{
		StringBuilder text = new();
		text.Append("schema=3\nentry=").Append(FrameBaseDefine.HOTFIX).Append('\n');
		foreach (string name in cap.optAot) text.Append("aot=").Append(name).Append('\n');
		foreach (string name in cap.allow) text.Append("known=").Append(name).Append('\n');
		return text.ToString();
	}
}
