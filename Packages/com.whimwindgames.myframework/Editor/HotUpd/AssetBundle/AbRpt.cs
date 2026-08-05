using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

// 基于YooAsset 3.0.4 BuildReport改写；报告结构和存储均为本项目本地格式，见NOTICE.md。
[Serializable]
public sealed class AbRptPkg
{
	public string name;
	public long size;
	public List<string> asts = new();
	public List<string> deps = new();
}

[Serializable]
public sealed class AbRpt
{
	const string RPT_PATH = "Library/MyFramework/AssetBundle/AbRpt.json";
	public int ver = 1;
	public string time;
	public string tgt;
	public string outDir;
	public double secs;
	public int astCnt;
	public int edgeCnt;
	public long size;
	public List<AbRptPkg> pkgs = new();

	public static AbRpt make(AbPlan plan, AssetBundleManifest man,
		string srcDir, string outDir, BuildTarget tgt, double secs)
	{
		AbRpt rpt = new()
		{
			time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
			tgt = tgt.ToString(),
			outDir = Path.GetFullPath(outDir),
			secs = secs,
			astCnt = plan.astCnt,
		};
		foreach (AbPkg pkg in plan.pkgs)
		{
			string file = Path.Combine(srcDir, pkg.name);
			AbRptPkg val = new()
			{
				name = pkg.name,
				size = new FileInfo(file).Length,
				deps = new List<string>(man.GetDirectDependencies(pkg.name)),
			};
			val.deps.Sort(StringComparer.Ordinal);
			foreach (AbAst ast in pkg.asts)
			{
				val.asts.Add(ast.path);
			}
			val.asts.Sort(StringComparer.Ordinal);
			rpt.edgeCnt += val.deps.Count;
			rpt.size += val.size;
			rpt.pkgs.Add(val);
		}
		return rpt;
	}

	public static string prep(AbRpt rpt)
	{
		if (rpt == null)
		{
			throw new ArgumentNullException(nameof(rpt));
		}
		string dir = Path.GetDirectoryName(RPT_PATH);
		Directory.CreateDirectory(dir);
		string tmp = RPT_PATH + ".tmp-" + Guid.NewGuid().ToString("N");
		File.WriteAllText(tmp, JsonUtility.ToJson(rpt, true), new UTF8Encoding(false));
		return tmp;
	}

	public static void keep(string tmp)
	{
		if (string.IsNullOrEmpty(tmp) || !File.Exists(tmp))
		{
			throw new FileNotFoundException("AB报告候选文件不存在", tmp);
		}
		string bak = RPT_PATH + ".bak-" + Guid.NewGuid().ToString("N");
		bool had = File.Exists(RPT_PATH);
		try
		{
			if (had)
			{
				File.Move(RPT_PATH, bak);
			}
			File.Move(tmp, RPT_PATH);
		}
		catch
		{
			if (!File.Exists(RPT_PATH) && File.Exists(bak))
			{
				File.Move(bak, RPT_PATH);
			}
			throw;
		}
		if (File.Exists(bak))
		{
			try
			{
				File.Delete(bak);
			}
			catch (Exception ex)
			{
				Debug.LogWarning("旧AB报告备份清理失败:" + ex.Message);
			}
		}
	}

	public static void drop(string tmp)
	{
		if (!string.IsNullOrEmpty(tmp) && File.Exists(tmp))
		{
			File.Delete(tmp);
		}
	}

	public static AbRpt load()
	{
		if (!File.Exists(RPT_PATH))
		{
			return null;
		}
		AbRpt rpt = JsonUtility.FromJson<AbRpt>(File.ReadAllText(RPT_PATH));
		if (rpt == null || rpt.ver != 1)
		{
			throw new InvalidDataException("AB构建报告格式无效:" + RPT_PATH);
		}
		return rpt;
	}
}
