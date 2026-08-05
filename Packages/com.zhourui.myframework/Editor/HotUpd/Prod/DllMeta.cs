using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using HybridCLR.Editor;
using HybridCLR.Editor.Commands;
using HybridCLR.Editor.HotUpdate;
using HybridSettings = HybridCLR.Editor.Settings.HybridCLRSettings;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

public sealed class DllMetaReq
{
	public UpdCfg cfg;
	public HotPlan plan;
	public BuildTarget target;
	// 可传最终Stage（*.dll.bytes）或HybridCLR编译目录（*.dll）。
	public string hotDir;
	public string baselineRoot;
	public bool useObf;
}

public sealed class DllMetaReport
{
	public string baseline;
	// HybridCLR原始程序集名，例如 System.Private.CoreLib.dll。
	public string[] assemblies;
	// 可直接写入UpdCfg的规范文件名，例如 System.Private.CoreLib.dll.bytes。
	public string[] aotDlls;
}

// 热更DLL必须同时通过两类分析：AOT泛型引用决定需要随补丁下发的元数据，
// MissingMetadataChecker则保证补丁没有访问Base已经裁掉的程序集、类型或成员。
public static class DllMeta
{
	const string AOT_BEGIN = "// {{ AOT assemblies";
	const string AOT_END = "// }}";

	public static DllMetaReport analyze(DllMetaReq req)
	{
		if (req == null) throw new ArgumentNullException(nameof(req));
		if (req.cfg == null) throw new ArgumentNullException(nameof(req.cfg));
		if (req.plan == null) throw new ArgumentNullException(nameof(req.plan));
		UpdRule.prod(req.cfg);
		HotList.chk(req.plan);
		HotList.chkCfg(req.cfg, req.plan.hot);
		AotBaseInfo baseline = AotBase.inspect(req.baselineRoot, req.cfg, req.plan,
			req.target, req.useObf);
		return analyzeCandidate(req.hotDir, baseline, req.plan.hot, req.target);
	}

	internal static DllMetaReport analyzeCandidate(string hotDir, AotBaseInfo baseline,
		HotSet hot, BuildTarget target)
	{
		if (baseline == null) throw new ArgumentNullException(nameof(baseline));
		HotList.chk(hot);
		string source = safeDir(hotDir, "最终热更DLL目录");
		checkHot(source, hot);
		using HotSettingsTx settings = new(hot);
		using HotOutputTx output = new(source, hot, target);
		string generated = tempOutput();
		string oldAot = settings.cfg.strippedAOTDllOutputRootDir;
		string oldGenerated = settings.cfg.outputAOTGenericReferenceFile;
		try
		{
			// SettingsUtil会在root后拼接BuildTarget，所以这里指向冻结目录的父目录。
			settings.cfg.strippedAOTDllOutputRootDir =
				Path.GetDirectoryName(baseline.path).Replace('\\', '/');
			settings.cfg.outputAOTGenericReferenceFile = relativeToAssets(generated);
			AOTReferenceGeneratorCommand.GenerateAOTGenericReference(target);
			string[] required = parseGeneratedAotAssemblies(
				File.ReadAllLines(generated, new System.Text.UTF8Encoding(false, true)));
			ensurePatchAotSubset(baseline.dlls, required);
			checkMissing(baseline.path, output.path, hot, target);
			return new DllMetaReport
			{
				baseline = baseline.path,
				assemblies = required,
				aotDlls = outputs(required),
			};
		}
		catch (InvalidDataException)
		{
			throw;
		}
		catch (Exception ex)
		{
			throw new InvalidDataException("HybridCLR AOT元数据分析失败", ex);
		}
		finally
		{
			settings.cfg.strippedAOTDllOutputRootDir = oldAot;
			settings.cfg.outputAOTGenericReferenceFile = oldGenerated;
			if (File.Exists(generated)) File.Delete(generated);
			if (File.Exists(generated + ".meta")) File.Delete(generated + ".meta");
		}
	}

	public static string[] parseGeneratedAotAssemblies(IEnumerable<string> lines)
	{
		List<string> values = new();
		bool inside = false;
		bool complete = false;
		foreach (string source in lines ?? Array.Empty<string>())
		{
			string line = source?.Trim() ?? string.Empty;
			if (line == AOT_BEGIN)
			{
				if (inside) throw new InvalidDataException("HybridCLR生成的AOT清单起始标记重复");
				inside = true;
				continue;
			}
			if (inside && line == AOT_END)
			{
				complete = true;
				break;
			}
			if (!inside || !line.StartsWith("\"", StringComparison.Ordinal)) continue;
			int end = line.IndexOf('"', 1);
			if (end <= 1 || line.Substring(end + 1).Trim() != ",")
				throw new InvalidDataException("HybridCLR生成的AOT清单格式错误:" + line);
			values.Add(line.Substring(1, end - 1));
		}
		if (!inside || !complete)
			throw new InvalidDataException("HybridCLR生成的AOT清单标记缺失");
		return canonical(values, true);
	}

	public static void ensurePatchAotSubset(IEnumerable<string> frozenValues,
		IEnumerable<string> requiredValues)
	{
		HashSet<string> frozen = new(canonical(frozenValues, false),
			StringComparer.OrdinalIgnoreCase);
		foreach (string name in canonical(requiredValues, true))
			if (!frozen.Contains(name)) throw new InvalidDataException(
				"补丁新增AOT元数据程序集需求，必须发布新Base ID和主包:" + name);
	}

	public static void requireConfigured(IEnumerable<string> configured,
		IEnumerable<string> analyzed)
	{
		string[] left = canonicalOutputs(configured);
		string[] right = canonicalOutputs(analyzed);
		if (!left.SequenceEqual(right, StringComparer.Ordinal))
			throw new InvalidDataException("运行配置aotDlls与自动分析结果不一致；配置:[" +
				string.Join(",", left) + "] 分析:[" + string.Join(",", right) + "]");
	}

	public static string[] outputs(IEnumerable<string> assemblies)
	{
		string[] names = canonical(assemblies, false);
		string[] values = new string[names.Length];
		for (int i = 0; i < names.Length; ++i) values[i] = names[i] + ".bytes";
		return values;
	}

	static string[] canonicalOutputs(IEnumerable<string> values)
	{
		List<string> names = new();
		foreach (string value in values ?? Array.Empty<string>())
		{
			string output = value?.Trim();
			if (string.IsNullOrEmpty(output) || output != Path.GetFileName(output) ||
				!output.EndsWith(".dll.bytes", StringComparison.OrdinalIgnoreCase))
				throw new InvalidDataException("AOT元数据输出名非法:" + value);
			names.Add(output.Substring(0, output.Length - ".bytes".Length));
		}
		return outputs(canonical(names, true));
	}

	static string[] canonical(IEnumerable<string> values, bool requireOrder)
	{
		HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
		List<string> names = new();
		string previous = null;
		foreach (string value in values ?? Array.Empty<string>())
		{
			string name = value?.Trim();
			if (string.IsNullOrEmpty(name) || name != Path.GetFileName(name) ||
				!name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
				name.Equals("netstandard.dll", StringComparison.OrdinalIgnoreCase) ||
				!seen.Add(name))
				throw new InvalidDataException("AOT程序集清单非法:" + value);
			if (requireOrder && previous != null &&
				string.CompareOrdinal(previous, name) >= 0)
				throw new InvalidDataException("AOT程序集清单没有按规范排序:" + name);
			names.Add(name);
			previous = name;
		}
		names.Sort(StringComparer.Ordinal);
		return names.ToArray();
	}

	internal static void checkMissing(string baseline, string hotDir, HotSet hot,
		BuildTarget target)
	{
		string root = Path.Combine(project(), "Library", "MyFramework", "HotUpd",
			"Meta-" + Guid.NewGuid().ToString("N"));
		try
		{
			Directory.CreateDirectory(root);
			foreach (string file in Directory.GetFiles(baseline, "*.dll",
				SearchOption.TopDirectoryOnly)) copyChecked(file,
				Path.Combine(root, Path.GetFileName(file)), false);
			string facade = UnityRefPath.netstandard(target);
			copyChecked(facade, Path.Combine(root, "netstandard.dll"), false);
			try
			{
				string[] hotNames = hot.dlls.Select(HotList.rawName).ToArray();
				MissingMetadataChecker checker = new(root, hotNames);
				foreach (string output in hot.dlls)
				{
					string raw = HotList.rawName(output) + ".dll";
					if (!checker.Check(Path.Combine(hotDir, raw)))
						throw new InvalidDataException(
							"热更代码访问了Base中已经裁剪的AOT程序集、类型或成员:" + raw);
				}
			}
			catch (InvalidDataException)
			{
				throw;
			}
			catch (Exception ex)
			{
				throw new InvalidDataException(
					"热更代码访问了Base中缺失或已经裁剪的AOT元数据", ex);
			}
		}
		finally
		{
			if (Directory.Exists(root)) Directory.Delete(root, true);
		}
	}

	static void checkHot(string root, HotSet hot)
	{
		foreach (string output in hot.dlls)
		{
			string path = hotSource(root, output);
			DllProd.checkDll(path, HotList.rawName(output));
		}
	}

	static string hotSource(string root, string output)
	{
		string packed = Path.Combine(root, output);
		string raw = Path.Combine(root, HotList.rawName(output) + ".dll");
		bool hasPacked = File.Exists(packed);
		bool hasRaw = File.Exists(raw);
		if (hasPacked == hasRaw)
			throw new InvalidDataException("热更DLL必须且只能存在一种输入格式:" + output);
		return hasPacked ? packed : raw;
	}

	static string tempOutput()
	{
		string root = Path.Combine(project(), "Library", "MyFramework", "HotUpd");
		Directory.CreateDirectory(root);
		return Path.Combine(root, "AOTRef-" + Guid.NewGuid().ToString("N") + ".cs");
	}

	static string relativeToAssets(string path)
	{
		string relative = Path.GetRelativePath(Application.dataPath, path).Replace('\\', '/');
		if (!relative.StartsWith("../Library/MyFramework/HotUpd/", StringComparison.Ordinal))
			throw new InvalidDataException("AOT引用临时文件越过框架Library目录:" + path);
		return relative;
	}

	static string safeDir(string value, string label)
	{
		if (string.IsNullOrWhiteSpace(value) || !Path.IsPathRooted(value))
			throw new InvalidDataException(label + "必须是绝对路径");
		string full = trim(Path.GetFullPath(value));
		DirectoryInfo root = new(full);
		if (!root.Exists || (root.Attributes & FileAttributes.ReparsePoint) != 0)
			throw new DirectoryNotFoundException(label + "不存在或为符号链接:" + full);
		ensurePhysical(full);
		return full;
	}

	static void copyChecked(string source, string target, bool overwrite)
	{
		FileInfo input = new(source);
		if (!input.Exists || input.Length <= 0 ||
			(input.Attributes & FileAttributes.ReparsePoint) != 0)
			throw new FileNotFoundException("元数据分析输入不存在或非法", source);
		Directory.CreateDirectory(Path.GetDirectoryName(target));
		File.Copy(source, target, overwrite);
		if (new FileInfo(target).Length != input.Length || sha(source) != sha(target))
			throw new IOException("元数据分析文件复制校验失败:" + source);
	}

	static string sha(string path)
	{
		using FileStream input = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
		using SHA256 hash = SHA256.Create();
		return Convert.ToBase64String(hash.ComputeHash(input));
	}

	static string project()
	{
		return Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
	}

	static void ensurePhysical(string path)
	{
		for (DirectoryInfo dir = new(path); dir != null; dir = dir.Parent)
			if (dir.Exists && (dir.Attributes & FileAttributes.ReparsePoint) != 0)
				throw new InvalidDataException("元数据分析路径不能经过符号链接:" + dir.FullName);
	}

	static string trim(string path)
	{
		string root = Path.GetPathRoot(path);
		return path.Length > root.Length ? path.TrimEnd(Path.DirectorySeparatorChar,
			Path.AltDirectorySeparatorChar) : path;
	}

	sealed class HotSettingsTx : IDisposable
	{
		readonly bool mHadFile;
		readonly AssemblyDefinitionAsset[] mDefs;
		readonly string[] mHot;
		readonly string[] mKeep;
		bool mDone;
		public HybridSettings cfg { get; }

		public HotSettingsTx(HotSet hot)
		{
			mHadFile = File.Exists("ProjectSettings/HybridCLRSettings.asset");
			cfg = HybridSettings.Instance;
			mDefs = cfg.hotUpdateAssemblyDefinitions == null ? null :
				(AssemblyDefinitionAsset[])cfg.hotUpdateAssemblyDefinitions.Clone();
			mHot = cfg.hotUpdateAssemblies == null ? null :
				(string[])cfg.hotUpdateAssemblies.Clone();
			mKeep = cfg.preserveHotUpdateAssemblies == null ? null :
				(string[])cfg.preserveHotUpdateAssemblies.Clone();
			try
			{
				cfg.hotUpdateAssemblyDefinitions = Array.Empty<AssemblyDefinitionAsset>();
				cfg.hotUpdateAssemblies = hot.dlls.Select(HotList.rawName).ToArray();
				cfg.preserveHotUpdateAssemblies = Array.Empty<string>();
				HybridSettings.Save();
				if (!SettingsUtil.HotUpdateAssemblyNamesExcludePreserved.SequenceEqual(
					cfg.hotUpdateAssemblies))
					throw new InvalidDataException("HybridCLR Hot程序集设置未按分析计划生效");
			}
			catch
			{
				restore();
				throw;
			}
		}

		public void restore()
		{
			if (mDone) return;
			cfg.hotUpdateAssemblyDefinitions = mDefs;
			cfg.hotUpdateAssemblies = mHot;
			cfg.preserveHotUpdateAssemblies = mKeep;
			HybridSettings.Save();
			if (!mHadFile && File.Exists("ProjectSettings/HybridCLRSettings.asset"))
				File.Delete("ProjectSettings/HybridCLRSettings.asset");
			mDone = true;
		}

		public void Dispose() { restore(); }
	}

	sealed class HotOutputTx : IDisposable
	{
		readonly string mBackup;
		readonly bool mHadOutput;
		bool mMoved;
		bool mOwnedPath;
		bool mDone;
		public string path { get; }

		public HotOutputTx(string source, HotSet hot, BuildTarget target)
		{
			path = trim(Path.GetFullPath(SettingsUtil.GetHotUpdateDllsOutputDirByTarget(target)));
			mBackup = path + ".meta-backup-" + Guid.NewGuid().ToString("N");
			ensurePhysical(path);
			mHadOutput = Directory.Exists(path);
			try
			{
				if (File.Exists(path)) throw new InvalidDataException(
					"HybridCLR热更输出路径被普通文件占用:" + path);
				if (mHadOutput)
				{
					Directory.Move(path, mBackup);
					mMoved = true;
				}
				Directory.CreateDirectory(path);
				mOwnedPath = true;
				foreach (string output in hot.dlls) copyChecked(hotSource(source, output),
					Path.Combine(path, HotList.rawName(output) + ".dll"), false);
			}
			catch
			{
				restore();
				throw;
			}
		}

		void restore()
		{
			if (mDone) return;
			if (mOwnedPath && Directory.Exists(path)) Directory.Delete(path, true);
			if (mMoved)
			{
				if (!Directory.Exists(mBackup)) throw new DirectoryNotFoundException(
					"HybridCLR热更输出备份丢失:" + mBackup);
				Directory.Move(mBackup, path);
			}
			mDone = true;
		}

		public void Dispose() { restore(); }
	}
}
