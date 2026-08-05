using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.U2D;
using UnityEngine;
using UnityEngine.U2D;
using static FileUtility;
using static FrameDefine;
using static StringUtility;

// 基于YooAsset 3.0.4 LegacyBuildPipeline改写；移除包清单/SBP/Runtime依赖，见NOTICE.md。
public static class AbPipe
{
	public static bool isRun { get; private set; }

	// names为null时构建全部，空集合只生成空索引。
	public static bool exec(BuildTarget tgt, string path,
		IEnumerable<string> names, AbZip zip)
	{
		if (AbCfg.dirty())
		{
			throw new InvalidOperationException("AB配置存在未保存修改");
		}
		return exec(tgt, path, AbCfg.load(), names, zip);
	}

	// 显式配置入口用于项目适配器、CI和测试；不会读取或改写ProjectSettings配置。
	public static bool exec(BuildTarget tgt, string path, AbCfg cfg,
		IEnumerable<string> names, AbZip zip)
	{
		if (isRun)
		{
			Debug.LogError("已有AssetBundle构建正在执行");
			return false;
		}
		if (string.IsNullOrWhiteSpace(path))
		{
			throw new ArgumentException("AB输出目录不能为空", nameof(path));
		}
		string dst = Path.GetFullPath(path ?? string.Empty).TrimEnd(
			Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		string dstParent = Path.GetDirectoryName(dst) ?? throw new InvalidOperationException(
			"AB输出目录没有有效父目录:" + dst);
		// Unity 6拒绝把场景AssetBundle直接构建到Library；构建现场固定放在项目Temp，
		// 校验完成后再原子提升到调用方目标（目标本身仍可位于Library暂存区）。
		string project = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
		string tmp = Path.Combine(project, "Temp", "MyFramework", "AbBuild-" +
			Guid.NewGuid().ToString("N"));
		string rptTmp = null;
		DateTime start = DateTime.Now;
		isRun = true;
		try
		{
			if (cfg == null) throw new ArgumentNullException(nameof(cfg));
			Directory.CreateDirectory(dstParent);
			Directory.CreateDirectory(tmp);

			AbPlan plan = AbPlan.make(cfg, names);
			AbCheck.need(plan);
			string[] files = AbImport.atlasFiles(plan);
			BuildAssetBundleOptions opts = getOpts(zip);
			SpriteAtlas[] atlases = Array.Empty<SpriteAtlas>();
			bool didPack = false;
			AssetBundleManifest man = null;
			try
			{
				if (plan.pkgs.Count != 0 && files.Length > 0)
				{
					atlases = loadAtlases(files);
					didPack = true;
					SpriteAtlasUtility.PackAtlases(atlases, tgt, false);
				}
				if (plan.pkgs.Count != 0)
				{
					man = BuildPipeline.BuildAssetBundles(
						tmp, plan.builds(), opts, tgt);
				}
				AbCheck.post(plan, man, tmp);
				AbCheck.atlases(plan, tmp, files);
			}
			finally
			{
				if (didPack)
				{
					SpriteAtlasUtility.CleanupAtlasPacking();
				}
			}
			writeList(tmp, plan, man);
			cleanOut(tmp);
			AbRpt rpt = AbRpt.make(plan, man, tmp, dst, tgt,
				(DateTime.Now - start).TotalSeconds);
			rptTmp = AbRpt.prep(rpt);

			commit(tmp, dst, rptTmp);
			rptTmp = null;
			Debug.Log("AssetBundle构建完成，包:" + plan.pkgs.Count +
				"，资源:" + plan.astCnt + "，压缩:" + zip +
				"，耗时:" + (DateTime.Now - start));
			return true;
		}
		catch (Exception ex)
		{
			Debug.LogException(ex);
			Debug.LogError("AssetBundle构建失败:" + ex.Message);
			return false;
		}
		finally
		{
			Exception clnErr = null;
			try
			{
				if (Directory.Exists(tmp))
				{
					Directory.Delete(tmp, true);
				}
				delMeta(tmp);
				AbRpt.drop(rptTmp);
			}
			catch (Exception ex)
			{
				clnErr = ex;
			}
			isRun = false;
			if (clnErr != null)
			{
				Debug.LogException(clnErr);
				throw new InvalidOperationException("AssetBundle生产现场恢复失败", clnErr);
			}
		}
	}

	static BuildAssetBundleOptions getOpts(AbZip zip)
	{
		BuildAssetBundleOptions opts = BuildAssetBundleOptions.StrictMode;
		return zip switch
		{
			AbZip.Lzma => opts,
			AbZip.Lz4 => opts | BuildAssetBundleOptions.ChunkBasedCompression,
			AbZip.Raw => opts | BuildAssetBundleOptions.UncompressedAssetBundle,
			_ => throw new ArgumentOutOfRangeException(nameof(zip), zip, "AB压缩模式无效"),
		};
	}

	static SpriteAtlas[] loadAtlases(string[] files)
	{
		SpriteAtlas[] atlases = new SpriteAtlas[files.Length];
		for (int i = 0; i < files.Length; ++i)
		{
			atlases[i] = AssetDatabase.LoadAssetAtPath<SpriteAtlas>(files[i]) ??
				throw new InvalidDataException("图集加载失败:" + files[i]);
		}
		return atlases;
	}

	static void writeList(string dir, AbPlan plan, AssetBundleManifest man)
	{
		if (plan.pkgs.Count != 0 && man == null)
			throw new InvalidDataException("非空AB计划缺少Unity构建清单");
		List<AbItem> items = new();
		foreach (AbPkg pkg in plan.pkgs)
		{
			List<string> bdeps = new(man.GetDirectDependencies(pkg.name));
			bdeps.Sort(StringComparer.Ordinal);
			foreach (string dep in pkg.deps)
				if (!bdeps.Contains(dep)) throw new InvalidDataException(
					"实际AB清单缺少计划依赖:" + pkg.name + " -> " + dep);
			string file = Path.Combine(dir, pkg.name);
			AssetBundle bundle = AssetBundle.LoadFromFile(file);
			if (bundle == null) throw new InvalidDataException("AB回读失败:" + pkg.name);
			try
			{
				string[] sceneVals = bundle.GetAllScenePaths();
				string[] nameVals = bundle.GetAllAssetNames();
				bool scenePkg = pkg.asts.Count > 0 && hasEnd(pkg.asts[0].path, ".unity");
				string[] actual = scenePkg ? sceneVals : nameVals;
				if ((scenePkg && nameVals.Length != 0) || (!scenePkg && sceneVals.Length != 0) ||
					actual.Length != pkg.asts.Count)
				{
					throw new InvalidDataException("AB内部地址数量不一致:" + pkg.name);
				}
				// Unity会将AB内部地址标准化为小写，配置地址本身允许保留可读大小写。
				HashSet<string> names = new(actual, StringComparer.OrdinalIgnoreCase);
				if (names.Count != actual.Length) throw new InvalidDataException(
					"AB内部地址重复:" + pkg.name);
				foreach (AbAst ast in pkg.asts)
				{
					string expected = scenePkg ? ast.path : ast.name;
					string actualName = null;
					foreach (string val in actual)
					{
						if (string.Equals(val, expected, StringComparison.OrdinalIgnoreCase))
						{
							if (actualName != null) throw new InvalidDataException(
								"AB内部地址匹配不唯一:" + pkg.name + " -> " + expected);
							actualName = val;
						}
					}
					if (actualName == null || !names.Remove(actualName))
					{
						throw new InvalidDataException("AB内部地址与计划不一致:" +
							pkg.name + " -> " + expected);
					}
					string scene = scenePkg ? actualName : string.Empty;
					ast.scene = scene;
					AbItem item = new()
					{
						key = ast.key,
						name = scenePkg ? ast.name : actualName,
						bundle = pkg.name,
						scene = ast.scene,
						atlas = getAtlName(ast.path),
					};
					item.bdeps.AddRange(bdeps);
					items.Add(item);
				}
				if (names.Count != 0) throw new InvalidDataException(
					"AB包含计划外内部地址:" + pkg.name + " -> " +
					string.Join(",", names));
			}
			finally
			{
				bundle.Unload(true);
			}
		}
		items.Sort((a, b) => string.CompareOrdinal(a.key, b.key));
		byte[] data = AbIndex.encode(items);
		writeFile(Path.Combine(dir, STREAMING_ASSET_FILE), data, data.Length, false);
	}

	static string getAtlName(string path)
	{
		SpriteAtlas atlas = AssetDatabase.LoadAssetAtPath<SpriteAtlas>(path);
		return atlas != null ? atlas.name : string.Empty;
	}

	static bool hasEnd(string val, string end)
	{
		return val.EndsWith(end, StringComparison.OrdinalIgnoreCase);
	}

	static void cleanOut(string dir)
	{
		foreach (string file in Directory.GetFiles(dir, "*.manifest",
			SearchOption.AllDirectories))
		{
			File.Delete(file);
			delMeta(file);
		}
		string root = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar,
			Path.AltDirectorySeparatorChar));
		string filePath = Path.Combine(dir, root);
		if (File.Exists(filePath))
		{
			File.Delete(filePath);
		}
		delMeta(filePath);
	}

	static void commit(string tmp, string dst, string rptTmp)
	{
		string ready = dst + ".candidate-" + Guid.NewGuid().ToString("N");
		string bak = dst + ".backup-" + Guid.NewGuid().ToString("N");
		bool had = Directory.Exists(dst);
		try
		{
			stageOnTargetVolume(tmp, ready);
			if (had)
			{
				moveDir(dst, bak);
			}
			moveDir(ready, dst);
			AbRpt.keep(rptTmp);
		}
		catch (Exception commitError)
		{
			try
			{
				if (had && Directory.Exists(bak))
				{
					if (Directory.Exists(dst)) deleteDir(dst);
					moveDir(bak, dst);
				}
				else if (!had && Directory.Exists(dst)) deleteDir(dst);
			}
			catch (Exception rollbackError)
			{
				throw new AggregateException("AssetBundle提交失败且旧产物恢复失败",
					commitError, rollbackError);
			}
			throw;
		}
		finally
		{
			delMeta(tmp);
			delMeta(ready);
			delMeta(bak);
			if (Directory.Exists(ready))
			{
				try { deleteDir(ready); }
				catch (Exception ex) { Debug.LogWarning("AB候选目录清理失败:" + ex.Message); }
			}
		}
		if (Directory.Exists(bak))
		{
			try
			{
				deleteDir(bak);
			}
			catch (Exception ex)
			{
				Debug.LogWarning("旧AssetBundle目录清理失败:" + ex.Message);
			}
		}
	}

	// Unity构建现场与目标目录可能位于不同磁盘。先把完整产物放到目标同级，
	// 回读每个文件后再使用同卷Directory.Move提升，避免跨卷移动破坏原子提交。
	static void stageOnTargetVolume(string src, string dst)
	{
		if (Directory.Exists(dst) || File.Exists(dst))
			throw new IOException("AB候选路径已存在:" + dst);
		try
		{
			Directory.Move(src, dst);
			return;
		}
		catch (IOException)
		{
			if (Directory.Exists(dst) || !Directory.Exists(src)) throw;
		}
		try
		{
			copyTree(src, dst);
			deleteDir(src);
		}
		catch
		{
			if (Directory.Exists(dst))
			{
				try { deleteDir(dst); }
				catch (Exception ex) { Debug.LogError("跨卷AB候选清理失败:" + ex); }
			}
			throw;
		}
	}

	static void copyTree(string src, string dst)
	{
		DirectoryInfo dir = new(src);
		if (!dir.Exists || (dir.Attributes & FileAttributes.ReparsePoint) != 0)
			throw new InvalidDataException("AB复制源不存在或为链接:" + src);
		Directory.CreateDirectory(dst);
		foreach (FileInfo file in dir.GetFiles())
		{
			if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
				throw new InvalidDataException("AB复制源包含链接文件:" + file.FullName);
			string output = Path.Combine(dst, file.Name);
			File.Copy(file.FullName, output, false);
			FileInfo saved = new(output);
			if (!saved.Exists || saved.Length != file.Length ||
				UpdHash.file(saved.FullName) != UpdHash.file(file.FullName))
				throw new IOException("AB跨卷复制校验失败:" + file.FullName);
		}
		foreach (DirectoryInfo child in dir.GetDirectories())
		{
			if ((child.Attributes & FileAttributes.ReparsePoint) != 0)
				throw new InvalidDataException("AB复制源包含链接目录:" + child.FullName);
			copyTree(child.FullName, Path.Combine(dst, child.Name));
		}
	}

	// 杀毒软件、索引服务和Unity自身的文件观察器可能短暂持有句柄；
	// 有限重试只处理瞬时IO占用，权限或路径错误仍会在约1秒内明确失败。
	static void moveDir(string src, string dst)
	{
		retryIo(() => Directory.Move(src, dst));
	}

	static void deleteDir(string path)
	{
		retryIo(() => Directory.Delete(path, true));
	}

	static void retryIo(Action action)
	{
		const int ATTEMPTS = 20;
		for (int i = 0; ; ++i)
		{
			try
			{
				action();
				return;
			}
			catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) &&
				i + 1 < ATTEMPTS)
			{
				System.Threading.Thread.Sleep(50);
			}
		}
	}

	static void delMeta(string path)
	{
		if (File.Exists(path + ".meta"))
		{
			File.Delete(path + ".meta");
		}
	}
}
