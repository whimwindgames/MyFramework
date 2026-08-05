using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

public static class AbBuild
{
	const int HASH_VER = 1;

	public static void check(string[] roots)
	{
		if (AbCfg.dirty())
		{
			throw new System.InvalidOperationException("AB配置存在未保存修改");
		}
		check(AbCfg.load(), roots);
	}

	public static void check(AbCfg cfg, string[] roots)
	{
		if (cfg == null) throw new System.ArgumentNullException(nameof(cfg));
		AbCheck.need(AbPlan.make(cfg, roots));
	}

	public static bool run(BuildTarget tgt, string path, string[] roots)
	{
		checkPath(path);
		AbCfg cfg = AbCfg.load();
		return AbPipe.exec(tgt, path, roots, cfg.zip);
	}

	public static bool run(BuildTarget tgt, string path, AbCfg cfg, string[] roots)
	{
		checkPath(path);
		if (cfg == null) throw new System.ArgumentNullException(nameof(cfg));
		return AbPipe.exec(tgt, path, cfg, roots, cfg.zip);
	}

	public static string hash(BuildTarget tgt, string[] roots)
	{
		if (AbCfg.dirty())
		{
			throw new System.InvalidOperationException("AB配置存在未保存修改");
		}
		return hash(tgt, AbCfg.load(), roots);
	}

	public static string hash(BuildTarget tgt, AbCfg cfg, string[] roots)
	{
		if (cfg == null) throw new System.ArgumentNullException(nameof(cfg));
		AbPlan plan = AbPlan.make(cfg, roots);
		AbCheck.need(plan);
		StringBuilder val = new();
		val.Append(HASH_VER).Append('\n');
		val.Append(Application.unityVersion).Append('\n');
		val.Append((int)tgt).Append('\n');
		val.Append((int)cfg.zip).Append('\n');
		val.Append(JsonUtility.ToJson(cfg, false)).Append('\n');
		foreach (AbPkg pkg in plan.pkgs)
		{
			val.Append(pkg.name).Append('\n');
			foreach (string dep in pkg.deps)
			{
				val.Append(dep).Append('\n');
			}
			foreach (AbAst ast in pkg.asts)
			{
				val.Append(ast.path).Append('\n');
				val.Append(ast.key).Append('\n');
				val.Append(ast.name).Append('\n');
				val.Append(AssetDatabase.GetAssetDependencyHash(ast.path)).Append('\n');
			}
		}
		return Hash128.Compute(val.ToString()).ToString();
	}

	public static bool ready(string path, string[] roots)
	{
		return ready(path, AbCfg.load(), roots);
	}

	public static bool ready(string path, AbCfg cfg, string[] roots)
	{
		if (cfg == null) throw new System.ArgumentNullException(nameof(cfg));
		if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path) ||
			!File.Exists(Path.Combine(path, FrameBaseDefine.AB_INDEX_FILE)))
		{
			return false;
		}
		AbPlan plan = AbPlan.make(cfg, roots);
		AbCheck.need(plan);
		foreach (AbPkg pkg in plan.pkgs)
		{
			string file = pkg.name.Replace('/', Path.DirectorySeparatorChar);
			if (!File.Exists(Path.Combine(path, file))) return false;
		}
		return true;
	}

	static void checkPath(string path)
	{
		if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path))
			throw new InvalidDataException("AB输出必须是绝对目录");
		string full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar,
			Path.AltDirectorySeparatorChar);
		string root = Path.GetPathRoot(full).TrimEnd(Path.DirectorySeparatorChar,
			Path.AltDirectorySeparatorChar);
		if (string.Equals(full, root, System.StringComparison.OrdinalIgnoreCase) ||
			File.Exists(full))
			throw new InvalidDataException("AB输出不能是文件系统根目录或普通文件:" + full);
		for (DirectoryInfo dir = new(full); dir != null; dir = dir.Parent)
			if (dir.Exists && (dir.Attributes & FileAttributes.ReparsePoint) != 0)
				throw new InvalidDataException("AB输出路径不能经过符号链接:" + dir.FullName);
	}
}
