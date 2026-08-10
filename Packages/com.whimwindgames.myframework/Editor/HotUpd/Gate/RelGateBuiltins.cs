using System;
using System.Collections.Generic;
using System.IO;

// AB中的MonoScript程序集必须随本次Hot DLL发布，或已经存在于目标Base AOT中。
public sealed class RelMonoScriptGate : IRelGate
{
	internal static readonly RelMonoScriptGate instance = new();
	RelMonoScriptGate() { }

	public string id => "framework.mono-script";
	public int order => 100;
	public RelGatePhase phases => RelGatePhase.Plan;

	public void check(RelGateInput input, RelGateOutput output)
	{
		HashSet<string> hot = new(StringComparer.OrdinalIgnoreCase);
		foreach (string path in input.hot.hot?.dlls ?? Array.Empty<string>())
		{
			hot.Add(HotList.rawName(path));
		}
		HashSet<string> aot = new(input.trustedBase.aotAssemblies,
			StringComparer.OrdinalIgnoreCase);
		foreach (string assembly in AbCheck.monoAsms(input.assets))
		{
			bool inHot = hot.Contains(assembly);
			bool inAot = aot.Contains(assembly);
			if (!inHot && !inAot)
			{
				output.error("mono.assembly_missing",
					"AB中的MonoScript程序集既不在本次Hot DLL中，也不在目标Base AOT中",
					assembly);
			}
			else if (inHot && inAot)
			{
				output.error("mono.assembly_ambiguous",
					"AB中的MonoScript程序集同时出现在Hot DLL与Base AOT中",
					assembly);
			}
		}
	}
}

// 项目适配器用逻辑地址声明必需配置；框架不感知配置的业务名称或内容格式。
public sealed class RelRequiredAsset
{
	public string key { get; }
	public string source { get; }
	public string[] envs { get; }

	public RelRequiredAsset(string key, string source = null,
		params string[] envs)
	{
		if (!AbIndex.validKey(key))
		{
			throw new InvalidDataException("必需资源逻辑地址非法:" + key);
		}
		this.key = key;
		this.source = string.IsNullOrWhiteSpace(source) ? null :
			source.Trim().Replace('\\', '/');
		if (this.source != null &&
			((!this.source.StartsWith("Assets/", StringComparison.Ordinal) &&
			  !this.source.StartsWith("Packages/", StringComparison.Ordinal)) ||
			 this.source.Contains("..")))
		{
			throw new InvalidDataException("必需资源源文件路径非法:" + source);
		}
		this.envs = envs == null || envs.Length == 0 ?
			new[] { "test", "prod" } : (string[])envs.Clone();
		HashSet<string> seen = new(StringComparer.Ordinal);
		foreach (string env in this.envs)
		{
			if ((env != "test" && env != "prod") || !seen.Add(env))
			{
				throw new InvalidDataException("必需资源环境非法或重复:" + env);
			}
		}
		Array.Sort(this.envs, StringComparer.Ordinal);
	}

	internal bool applies(string env)
	{
		return Array.BinarySearch(envs, env, StringComparer.Ordinal) >= 0;
	}
}

public sealed class RelRequiredAssetGate : IRelGate
{
	readonly string mId;
	readonly int mOrder;
	readonly RelRequiredAsset[] mRequired;

	public string id => mId;
	public int order => mOrder;
	public RelGatePhase phases => RelGatePhase.Plan;

	public RelRequiredAssetGate(string id, IEnumerable<RelRequiredAsset> required,
		int order = 200)
	{
		if (!UpdFmt.isId(id)) throw new InvalidDataException("必需资源门禁标识非法:" + id);
		mId = id;
		mOrder = order;
		mRequired = required == null ? Array.Empty<RelRequiredAsset>() :
			new List<RelRequiredAsset>(required).ToArray();
		HashSet<string> keys = new(StringComparer.Ordinal);
		foreach (RelRequiredAsset item in mRequired)
		{
			if (item == null || !keys.Add(item.key))
			{
				throw new InvalidDataException("必需资源声明为空或逻辑地址重复");
			}
		}
		Array.Sort(mRequired, (left, right) => string.CompareOrdinal(left.key, right.key));
	}

	public void check(RelGateInput input, RelGateOutput output)
	{
		foreach (RelRequiredAsset item in mRequired)
		{
			if (!item.applies(input.cfg.env)) continue;
			if (!input.assets.trySrc(item.key, out string source))
			{
				output.error("asset.required_missing",
					"Release AB计划缺少声明的必需资源", item.key);
				continue;
			}
			if (item.source != null && !string.Equals(source, item.source,
				StringComparison.Ordinal))
			{
				output.error("asset.required_source",
					"必需资源逻辑地址指向了非声明源文件", source);
				continue;
			}
			if (!File.Exists(source))
			{
				output.error("asset.required_source_missing",
					"必需资源源文件不存在", source);
			}
		}
	}
}
