using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Compilation;
using UnityEngine;

// 从当前Unity安装解析系统引用。所有结果只服务于生产分析，不写入项目配置，
// 因而不会把开发机用户名、Unity Hub目录或磁盘路径冻结进Base/Release。
public static class UnityRefPath
{
	static readonly object sGate = new();
	static readonly HashSet<string> sLogged = new(StringComparer.Ordinal);

	public static IReadOnlyList<string> dirs()
	{
		return dirs(EditorUserBuildSettings.activeBuildTarget);
	}

	public static IReadOnlyList<string> dirs(BuildTarget target)
	{
		return findDirs(target);
	}

	public static string netstandard()
	{
		return netstandard(EditorUserBuildSettings.activeBuildTarget);
	}

	public static string netstandard(BuildTarget target)
	{
		List<string> tried = new();
		foreach (string dir in dirs(target))
		{
			string file = Path.Combine(dir, "netstandard.dll");
			tried.Add(file);
			if (validAssembly(file, "netstandard")) return file;
		}
		throw new FileNotFoundException(diag(
			"Unity系统引用netstandard.dll不存在或身份错误", target, tried));
	}

	public static void verify()
	{
		verify(EditorUserBuildSettings.activeBuildTarget);
	}

	public static void verify(BuildTarget target)
	{
		string facade = netstandard(target);
		string key = target + "|" + apiLevel(target) + "|" + facade;
		lock (sGate)
		{
			if (!sLogged.Add(key)) return;
		}
		Debug.Log("Unity系统引用解析完成, Unity:" + Application.unityVersion +
			", Target:" + target + ", Api:" + apiLevel(target) +
			", netstandard:" + facade);
	}

	static string[] findDirs(BuildTarget target)
	{
		if (target == BuildTarget.NoTarget)
			throw new PlatformNotSupportedException("不能为NoTarget解析Unity系统引用");
		List<string> values = new();
		HashSet<string> seen = new(pathComparer());
		ApiCompatibilityLevel api = apiLevel(target);
		foreach (string dir in CompilationPipeline.GetSystemAssemblyDirectories(api) ??
			Array.Empty<string>()) add(values, seen, dir);

		// Unity 6布局与旧版Resources/Scripting布局作为显式兼容回退。
		string app = EditorApplication.applicationContentsPath;
		add(values, seen, Path.Combine(app, "NetStandard", "ref", "2.1.0"));
		add(values, seen, Path.Combine(app, "Resources", "Scripting", "NetStandard",
			"ref", "2.1.0"));
		string mono = monoDir();
		addMono(values, seen, Path.Combine(app, "MonoBleedingEdge", "lib", "mono", mono));
		addMono(values, seen, Path.Combine(app, "Resources", "Scripting",
			"MonoBleedingEdge", "lib", "mono", mono));
		if (values.Count == 0) throw new DirectoryNotFoundException(diag(
			"Unity没有提供任何可用的系统引用目录", target, Array.Empty<string>()));
		return values.ToArray();
	}

	static ApiCompatibilityLevel apiLevel(BuildTarget target)
	{
		BuildTargetGroup group = BuildPipeline.GetBuildTargetGroup(target);
		if (group == BuildTargetGroup.Unknown)
			throw new PlatformNotSupportedException("无法解析构建目标组:" + target);
		return PlayerSettings.GetApiCompatibilityLevel(
			NamedBuildTarget.FromBuildTargetGroup(group));
	}

	static void addMono(List<string> values, HashSet<string> seen, string mono)
	{
		add(values, seen, mono);
		add(values, seen, Path.Combine(mono, "Facades"));
	}

	static void add(List<string> values, HashSet<string> seen, string path)
	{
		if (string.IsNullOrWhiteSpace(path)) return;
		string full;
		try
		{
			full = trim(Path.GetFullPath(path));
		}
		catch (Exception)
		{
			return;
		}
		if (Directory.Exists(full) && seen.Add(full)) values.Add(full);
	}

	static bool validAssembly(string path, string name)
	{
		try
		{
			FileInfo file = new(path);
			return file.Exists && file.Length > 0 &&
				(file.Attributes & FileAttributes.ReparsePoint) == 0 &&
				string.Equals(AssemblyName.GetAssemblyName(path).Name, name,
					StringComparison.OrdinalIgnoreCase);
		}
		catch (Exception)
		{
			return false;
		}
	}

	static string monoDir()
	{
		return Application.platform switch
		{
			RuntimePlatform.OSXEditor => "unityaot-macos",
			RuntimePlatform.WindowsEditor => "unityaot-win32",
			RuntimePlatform.LinuxEditor => "unityaot-linux",
			_ => throw new PlatformNotSupportedException(
				"不支持当前Unity Editor平台:" + Application.platform),
		};
	}

	static StringComparer pathComparer()
	{
		return Application.platform == RuntimePlatform.WindowsEditor ?
			StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
	}

	static string diag(string title, BuildTarget target, IEnumerable<string> tried)
	{
		return title + "\nUnity:" + Application.unityVersion +
			"\nEditor平台:" + Application.platform + "\n构建目标:" + target +
			"\nApiCompatibilityLevel:" + apiLevel(target) +
			"\napplicationContentsPath:" + EditorApplication.applicationContentsPath +
			"\n尝试路径:\n" + string.Join("\n", tried);
	}

	static string trim(string path)
	{
		string root = Path.GetPathRoot(path);
		return path.Length > root.Length ? path.TrimEnd(Path.DirectorySeparatorChar,
			Path.AltDirectorySeparatorChar) : path;
	}
}
