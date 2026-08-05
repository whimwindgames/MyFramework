using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.U2D;

// 基于YooAsset 3.0.4 Group/Collector思想改写并本地化，见本目录NOTICE.md。

public sealed class AbCfg : ScriptableObject
{
	const string CFG_PATH = "ProjectSettings/AbCfg.asset";
	public const int SCHEMA = 3;
	static AbCfg mInst;
	static string mSnap;
	static AbPlan mPlan;
	static bool mDirty;

	public int schemaVersion = SCHEMA;
	public AbZip zip = AbZip.Lzma;
	public List<AbGroup> groups = new();

	[InitializeOnLoadMethod]
	static void bindMap()
	{
		EditorApplication.projectChanged -= invalidate;
		EditorApplication.projectChanged += invalidate;
		AbIndex.srcKey = path => mapPlan().tryKey(path, out string key) ? key : null;
		AbIndex.keySrc = key =>
		{
			AbPlan plan = mapPlan();
			return plan.trySrc(key, out string src) ? src : null;
		};
		AbIndex.atlasMap = makeAtlas;
		AbIndex.itemMap = makeItems;
	}

	static AbPlan mapPlan()
	{
		AbCfg cfg = load();
		if (mPlan == null)
		{
			mPlan = AbPlan.make(cfg);
			if (mPlan.errs.Count != 0) throw new InvalidDataException(
				"AB计划无效:" + string.Join(";", mPlan.errs));
			chkMap(mPlan);
		}
		return mPlan;
	}

	static void chkMap(AbPlan plan)
	{
		foreach (AbPkg pkg in plan.pkgs)
		foreach (AbAst ast in pkg.asts)
		{
			if (!plan.tryKey(ast.path, out string key) || key != ast.key ||
				!plan.trySrc(key, out string src) || src != ast.path)
				throw new InvalidDataException("AB双向映射不一致:" + ast.path);
		}
	}

	static AbItem[] makeAtlas()
	{
		List<AbItem> vals = new();
		foreach (AbPkg pkg in mapPlan().pkgs)
		foreach (AbAst ast in pkg.asts)
		{
			SpriteAtlas atlas = AssetDatabase.LoadAssetAtPath<SpriteAtlas>(ast.path);
			if (atlas != null)
			{
				vals.Add(new AbItem
				{
					key = ast.key,
					src = ast.path,
					atlas = atlas.name,
				});
			}
		}
		return vals.ToArray();
	}

	static AbItem[] makeItems()
	{
		List<AbItem> vals = new();
		foreach (AbPkg pkg in mapPlan().pkgs)
		foreach (AbAst ast in pkg.asts)
		{
			SpriteAtlas atlas = AssetDatabase.LoadAssetAtPath<SpriteAtlas>(ast.path);
			AbItem item = new()
			{
				key = ast.key,
				src = ast.path,
				name = ast.name,
				bundle = pkg.name,
				scene = ast.path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase) ?
					ast.path : string.Empty,
				atlas = atlas == null ? string.Empty : atlas.name,
			};
			item.bdeps.AddRange(pkg.deps);
			vals.Add(item);
		}
		vals.Sort((a, b) => string.CompareOrdinal(a.key, b.key));
		return vals.ToArray();
	}

	public static AbCfg load()
	{
		if (mInst != null)
		{
			return mInst;
		}
		if (!File.Exists(CFG_PATH))
		{
			throw new InvalidDataException("缺少AB配置:" + CFG_PATH);
		}
		UnityEngine.Object[] objs = InternalEditorUtility.LoadSerializedFileAndForget(CFG_PATH);
		if (objs == null || objs.Length == 0 || objs[0] is not AbCfg cfg)
		{
			throw new InvalidDataException("AB配置无法读取:" + CFG_PATH);
		}
		if (cfg.schemaVersion != SCHEMA)
		{
			throw new InvalidDataException("AB配置版本不兼容:" + cfg.schemaVersion +
				"，需要:" + SCHEMA);
		}
		if (cfg.groups == null || cfg.groups.Exists(group => group == null))
		{
			throw new InvalidDataException("AB Group配置列表损坏:" + CFG_PATH);
		}
		if (!Enum.IsDefined(typeof(AbZip), cfg.zip))
		{
			throw new InvalidDataException("AB压缩模式非法:" + cfg.zip);
		}
		mInst = cfg;
		mSnap = JsonUtility.ToJson(cfg, false);
		mDirty = false;
		return mInst;
	}

	public static void init()
	{
		if (File.Exists(CFG_PATH))
		{
			_ = load();
			return;
		}
		mInst = CreateInstance<AbCfg>();
		mInst.schemaVersion = SCHEMA;
		mInst.groups = new List<AbGroup>();
		save();
		drop();
		_ = load();
	}

	public static void save()
	{
		if (mInst == null)
		{
			throw new InvalidOperationException("AB配置尚未加载");
		}
		if (!Enum.IsDefined(typeof(AbZip), mInst.zip))
		{
			throw new InvalidDataException("AB压缩模式非法:" + mInst.zip);
		}
		mInst.schemaVersion = SCHEMA;
		string text = JsonUtility.ToJson(mInst, false);
		InternalEditorUtility.SaveToSerializedFileAndForget(
			new AbCfg[] { mInst }, CFG_PATH, true);
		mSnap = text;
		mDirty = false;
		invalidate();
	}

	public static bool dirty()
	{
		return mInst != null && mDirty;
	}

	public static void touch()
	{
		if (mInst == null) throw new InvalidOperationException("AB配置尚未加载");
		mDirty = true;
		invalidate();
	}

	public static void revert()
	{
		AbCfg cfg = load();
		if (string.IsNullOrEmpty(mSnap))
		{
			throw new InvalidOperationException("AB配置没有可恢复的已保存快照");
		}
		JsonUtility.FromJsonOverwrite(mSnap, cfg);
		mDirty = false;
		invalidate();
	}

	public static void drop()
	{
		if (mInst != null)
		{
			DestroyImmediate(mInst);
		}
		mInst = null;
		mSnap = null;
		mDirty = false;
		invalidate();
	}

	public static void invalidate()
	{
		mPlan = null;
	}
}
