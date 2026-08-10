using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.U2D;
using UnityEditor;
using UnityEditor.Compilation;
using static StringUtility;

// 基于YooAsset 3.0.4构建前后校验思路改写并本地化，见NOTICE.md。

public static class AbCheck
{
	public static List<string> scan(AbPlan plan)
	{
		if (plan == null)
		{
			throw new ArgumentNullException(nameof(plan));
		}
		List<string> errs = new(plan.errs);
		Dictionary<string, List<string>> graph = new(StringComparer.Ordinal);
		foreach (AbPkg pkg in plan.pkgs)
		{
			if (pkg.asts.Count == 0)
			{
				errs.Add("空资源包:" + pkg.name);
			}
			if (graph.ContainsKey(pkg.name)) errs.Add("AB包名重复:" + pkg.name);
			else graph.Add(pkg.name, pkg.deps);
		}
		foreach (AbPkg pkg in plan.pkgs)
		foreach (string dep in pkg.deps)
		{
			if (!graph.ContainsKey(dep))
				errs.Add("AB依赖缺失:" + pkg.name + " -> " + dep);
		}
		if (findLoop(graph, out string loop))
		{
			errs.Add("AB循环依赖:" + loop);
		}
		return errs;
	}

	public static void need(AbPlan plan)
	{
		List<string> errs = scan(plan);
		if (errs.Count > 0)
		{
			throw new InvalidDataException(string.Join("\n", errs));
		}
	}
	public static string[] monoAsms(AbPlan plan)
	{
		need(plan);
		HashSet<string> names = new(StringComparer.Ordinal);
		HashSet<string> player = playerAsms();
		foreach (AbPkg pkg in plan.pkgs)
		foreach (AbAst ast in pkg.asts)
		foreach (string dep in AssetDatabase.GetDependencies(ast.path, true))
		{
			if (!dep.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) continue;
			MonoScript script = AssetDatabase.LoadAssetAtPath<MonoScript>(dep);
			if (script == null) throw new InvalidDataException(
				"AB MonoScript无法读取:" + dep);
			string name = CompilationPipeline.GetAssemblyNameFromScriptPath(dep);
			if (string.IsNullOrWhiteSpace(name)) throw new InvalidDataException(
				"AB MonoScript未归属Player程序集:" + dep);
			name = Path.GetFileNameWithoutExtension(name);
			// AssetDatabase依赖图会包含ShaderGUI等编辑器脚本；这些脚本不会
			// 序列化进Player AssetBundle，不能作为Base AOT/Hot DLL要求。
			if (player.Contains(name)) names.Add(name);
		}
		List<string> vals = new(names);
		vals.Sort(StringComparer.Ordinal);
		return vals.ToArray();
	}

	internal static bool isPlayerAsm(string name)
	{
		return !string.IsNullOrWhiteSpace(name) && playerAsms().Contains(name);
	}

	static HashSet<string> playerAsms()
	{
		HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
		foreach (UnityEditor.Compilation.Assembly assembly in
			CompilationPipeline.GetAssemblies(AssembliesType.Player))
		{
			if (!string.IsNullOrWhiteSpace(assembly.name)) names.Add(assembly.name);
		}
		return names;
	}

	public static void post(AbPlan plan, AssetBundleManifest man, string outDir)
	{
		if (plan == null) throw new ArgumentNullException(nameof(plan));
		if (plan.pkgs.Count == 0)
		{
			if (man != null && man.GetAllAssetBundles().Length != 0)
				throw new InvalidDataException("空AB计划生成了计划外资源包");
			return;
		}
		if (man == null)
		{
			throw new InvalidDataException("Unity未返回AssetBundleManifest");
		}
		HashSet<string> want = new(StringComparer.Ordinal);
		foreach (AbPkg pkg in plan.pkgs)
		{
			want.Add(pkg.name);
		}
		HashSet<string> got = new(man.GetAllAssetBundles(), StringComparer.Ordinal);
		List<string> miss = new();
		List<string> extra = new();
		foreach (string name in want)
		{
			if (!got.Contains(name))
			{
				miss.Add(name);
			}
		}
		foreach (string name in got)
		{
			if (!want.Contains(name))
			{
				extra.Add(name);
			}
			if (!File.Exists(Path.Combine(outDir, name)))
			{
				throw new FileNotFoundException("Unity清单中的AB文件不存在", name);
			}
		}
		miss.Sort(StringComparer.Ordinal);
		extra.Sort(StringComparer.Ordinal);
		if (miss.Count > 0 || extra.Count > 0)
		{
			throw new InvalidDataException("AB构建结果与计划不一致，缺少:[" +
				string.Join(",", miss) + "]，多出:[" + string.Join(",", extra) + "]");
		}
		Dictionary<string, List<string>> graph = new(StringComparer.Ordinal);
		foreach (string name in got)
		{
			List<string> deps = new(man.GetDirectDependencies(name));
			deps.Sort(StringComparer.Ordinal);
			graph[name] = deps;
		}
		if (findLoop(graph, out string loop))
		{
			throw new InvalidDataException("Unity实际AB循环依赖:" + loop);
		}
	}

	public static void atlases(AbPlan plan, string outDir, string[] files)
	{
		foreach (string file in files)
		{
			string path = file;
			AbPkg atlasPkg = null;
			AbAst item = null;
			foreach (AbPkg pkg in plan.pkgs)
			{
				if (pkg.asts.Exists(ast => string.Equals(ast.path, path,
					StringComparison.Ordinal)))
				{
					atlasPkg = pkg;
					item = pkg.asts.Find(ast => string.Equals(ast.path, path,
						StringComparison.Ordinal));
					break;
				}
			}
			if (atlasPkg == null)
			{
				throw new InvalidDataException("图集未进入AB构建计划:" + path);
			}
			AssetBundle bundle = AssetBundle.LoadFromFile(
				Path.Combine(outDir, atlasPkg.name));
			if (bundle == null)
			{
				throw new InvalidDataException("图集AB无法加载:" + atlasPkg.name);
			}
			try
			{
				SpriteAtlas atlas = bundle.LoadAsset<SpriteAtlas>(item.name);
				if (atlas == null || atlas.spriteCount <= 0)
				{
					throw new InvalidDataException("图集AB没有有效Sprite:" +
						atlasPkg.name + " -> " + path);
				}
			}
			finally
			{
				bundle.Unload(true);
			}
		}
	}

	static bool findLoop(Dictionary<string, List<string>> graph, out string loop)
	{
		Dictionary<string, byte> state = new(StringComparer.Ordinal);
		List<string> stack = new();
		foreach (string name in graph.Keys)
		{
			if (walk(name, graph, state, stack, out loop))
			{
				return true;
			}
		}
		loop = null;
		return false;
	}

	static bool walk(string name, Dictionary<string, List<string>> graph,
		Dictionary<string, byte> state, List<string> stack, out string loop)
	{
		if (state.TryGetValue(name, out byte val))
		{
			if (val == 2)
			{
				loop = null;
				return false;
			}
			int idx = stack.IndexOf(name);
			List<string> vals = idx < 0 ? new(stack) : stack.GetRange(idx, stack.Count - idx);
			vals.Add(name);
			loop = string.Join(" -> ", vals);
			return true;
		}
		state[name] = 1;
		stack.Add(name);
		if (graph.TryGetValue(name, out List<string> deps))
		{
			foreach (string dep in deps)
			{
				if (graph.ContainsKey(dep) && walk(dep, graph, state, stack, out loop))
				{
					return true;
				}
			}
		}
		stack.RemoveAt(stack.Count - 1);
		state[name] = 2;
		loop = null;
		return false;
	}
}
