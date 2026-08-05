using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using static FrameDefine;

// 显式Group计划：配置只保存资源GUID、稳定逻辑地址和目标AB，不扫描任何默认目录。

public sealed class AbAst
{
	public string path;
	public string key;
	public string name;
	public string scene;
	public readonly List<string> deps = new();
}

public sealed class AbPkg
{
	public string name;
	public string key;
	public readonly List<AbAst> asts = new();
	public readonly List<string> deps = new();
	public readonly List<string> allDeps = new();
}

public sealed class AbPlan
{
	public readonly List<AbPkg> pkgs = new();
	public readonly List<string> errs = new();
	public readonly HashSet<string> atlas = new(StringComparer.Ordinal);
	public int astCnt;
	public int edgeCnt;

	public static AbPlan make(AbCfg cfg)
	{
		if (cfg == null) throw new ArgumentNullException(nameof(cfg));
		AbPlan pre = make(cfg, new HashSet<string>(StringComparer.Ordinal));
		HashSet<string> texs = AbImport.atlasTex(pre, AbImport.atlasFiles(pre));
		AbPlan plan = make(cfg, texs);
		plan.chkAtlCfg(pre, texs);
		return plan;
	}

	// null表示构建全部；空集合表示本次不构建AB。
	public static AbPlan make(AbCfg cfg, IEnumerable<string> names)
	{
		if (cfg == null) throw new ArgumentNullException(nameof(cfg));
		if (names == null) return make(cfg);
		List<string> selErrs = new();
		HashSet<string> picks = getPicks(cfg, names, selErrs);
		AbPlan pre = make(cfg, new HashSet<string>(StringComparer.Ordinal));
		fillPicks(pre, picks);
		AbPlan plan = makePick(cfg, picks);
		plan.errs.InsertRange(0, selErrs);
		return plan;
	}

	public bool contains(string path)
	{
		string val = norm(path);
		foreach (AbPkg pkg in pkgs)
		foreach (AbAst ast in pkg.asts)
			if (ast.path == val) return true;
		return false;
	}

	public string findSrc(string key)
	{
		if (trySrc(key, out string src)) return src;
		throw new InvalidDataException("AB计划缺少逻辑地址:" + key);
	}

	public bool tryKey(string src, out string key)
	{
		string path = norm(src);
		foreach (AbPkg pkg in pkgs)
		foreach (AbAst ast in pkg.asts)
		if (ast.path == path)
		{
			key = ast.key;
			return true;
		}
		key = null;
		return false;
	}

	public bool trySrc(string key, out string src)
	{
		foreach (AbPkg pkg in pkgs)
		foreach (AbAst ast in pkg.asts)
		if (ast.key == key)
		{
			src = ast.path;
			return true;
		}
		src = null;
		return false;
	}

	public AssetBundleBuild[] builds()
	{
		AssetBundleBuild[] vals = new AssetBundleBuild[pkgs.Count];
		for (int i = 0; i < pkgs.Count; ++i)
		{
			AbPkg pkg = pkgs[i];
			string[] asts = new string[pkg.asts.Count];
			string[] names = new string[pkg.asts.Count];
			for (int j = 0; j < pkg.asts.Count; ++j)
			{
				asts[j] = pkg.asts[j].path;
				names[j] = pkg.asts[j].name;
			}
			vals[i] = new AssetBundleBuild
			{
				assetBundleName = pkg.name,
				assetNames = asts,
				addressableNames = names,
			};
		}
		return vals;
	}

	// 供编辑器资产导入钩子判断资源是否由显式AB配置管理。
	public static bool includes(AbCfg cfg, string assetPath)
	{
		if (cfg?.groups == null) return false;
		string guid = normGuid(AssetDatabase.AssetPathToGUID(norm(assetPath)));
		if (string.IsNullOrEmpty(guid)) return false;
		foreach (AbGroup group in cfg.groups)
		foreach (AbEntry entry in group?.entries ?? new List<AbEntry>())
			if (entry != null && normGuid(entry.guid) == guid) return true;
		return false;
	}

	public static string keyOf(AbCfg cfg, string assetPath)
	{
		if (cfg?.groups == null) throw new ArgumentNullException(nameof(cfg));
		string guid = normGuid(AssetDatabase.AssetPathToGUID(norm(assetPath)));
		string key = null;
		foreach (AbGroup group in cfg.groups)
		foreach (AbEntry entry in group?.entries ?? new List<AbEntry>())
		{
			if (entry == null || normGuid(entry.guid) != guid) continue;
			if (key != null) throw new InvalidDataException("资源重复配置:" + assetPath);
			key = normKey(entry.address);
		}
		if (key == null) throw new InvalidDataException("资源未显式配置:" + assetPath);
		if (!chkKey(key)) throw new InvalidDataException("资源逻辑地址无效:" + assetPath);
		return key;
	}

	internal static AbPlan make(AbCfg cfg, HashSet<string> atlas)
	{
		return make(cfg, atlas, null);
	}

	static AbPlan make(AbCfg cfg, HashSet<string> atlas, HashSet<string> picks)
	{
		if (cfg == null) throw new ArgumentNullException(nameof(cfg));
		if (atlas == null) throw new ArgumentNullException(nameof(atlas));
		AbPlan plan = new();
		plan.atlas.UnionWith(atlas);
		if (cfg.schemaVersion != AbCfg.SCHEMA)
		{
			plan.errs.Add("AB配置版本不兼容:" + cfg.schemaVersion);
			return plan;
		}
		if (cfg.groups == null)
		{
			plan.errs.Add("AB Group列表为空");
			return plan;
		}

		HashSet<string> ids = new(StringComparer.Ordinal);
		Dictionary<string, string> bundles = new(StringComparer.Ordinal);
		Dictionary<string, string> paths = new(StringComparer.Ordinal);
		Dictionary<string, string> keys = new(StringComparer.Ordinal);
		for (int i = 0; i < cfg.groups.Count; ++i)
		{
			AbGroup group = cfg.groups[i];
			if (picks != null && (group == null ||
				!picks.Contains(normName(group.bundleName)))) continue;
			plan.addGroup(group, i, ids, bundles, paths, keys, atlas);
		}

		plan.pkgs.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
		foreach (AbPkg pkg in plan.pkgs)
		{
			pkg.asts.Sort((a, b) => string.CompareOrdinal(a.path, b.path));
			plan.astCnt += pkg.asts.Count;
		}
		plan.addDeps(atlas);
		return plan;
	}

	static HashSet<string> getPicks(AbCfg cfg, IEnumerable<string> names,
		List<string> errs)
	{
		Dictionary<string, List<string>> cfgMap = new(StringComparer.Ordinal);
		foreach (AbGroup group in cfg.groups ?? new List<AbGroup>())
		{
			if (group == null) continue;
			string shown = (group.bundleName ?? string.Empty).Trim().Replace('\\', '/');
			string name = normName(group.bundleName);
			if (!chkName(name, out _)) continue;
			if (!cfgMap.TryGetValue(name, out List<string> vals))
			{
				vals = new List<string>();
				cfgMap.Add(name, vals);
			}
			vals.Add(shown);
		}

		Dictionary<string, string> roots = new(StringComparer.Ordinal);
		HashSet<string> picks = new(StringComparer.Ordinal);
		foreach (string raw in names)
		{
			string shown = (raw ?? string.Empty).Trim().Replace('\\', '/');
			string name = normName(raw);
			if (!chkName(name, out string nameErr))
			{
				errs.Add("AB选择名称无效:" + nameErr);
				continue;
			}
			if (roots.TryGetValue(name, out string old))
			{
				errs.Add(string.Equals(old, shown, StringComparison.Ordinal) ?
					"AB选择名称重复:" + shown :
					"AB选择名称大小写冲突:" + old + "/" + shown);
				continue;
			}
			roots.Add(name, shown);
			if (!cfgMap.TryGetValue(name, out List<string> vals))
			{
				errs.Add("AB选择不存在:" + shown);
				continue;
			}
			if (vals.Count > 1)
			{
				bool caseErr = vals.Exists(val => !string.Equals(val, vals[0],
					StringComparison.Ordinal));
				errs.Add(caseErr ? "AB配置包名大小写冲突:" + string.Join("/", vals) :
					"AB配置包名重复:" + shown);
			}
			picks.Add(name);
		}
		return picks;
	}

	static void fillPicks(AbPlan pre, HashSet<string> picks)
	{
		Dictionary<string, AbPkg> pkgMap = new(StringComparer.Ordinal);
		Dictionary<string, AbPkg> astMap = new(StringComparer.Ordinal);
		foreach (AbPkg pkg in pre.pkgs)
		{
			pkgMap[pkg.name] = pkg;
			foreach (AbAst ast in pkg.asts) astMap[ast.path] = pkg;
		}
		bool changed;
		do
		{
			changed = false;
			foreach (string name in new List<string>(picks))
			{
				if (!pkgMap.TryGetValue(name, out AbPkg pkg)) continue;
				foreach (string dep in pkg.allDeps) changed |= picks.Add(dep);
			}
			List<string> files = new();
			foreach (AbPkg pkg in pre.pkgs)
			{
				if (!picks.Contains(pkg.name)) continue;
				foreach (AbAst ast in pkg.asts)
					if (AssetDatabase.LoadAssetAtPath<UnityEngine.U2D.SpriteAtlas>(
						ast.path) != null) files.Add(ast.path);
			}
			files.Sort(StringComparer.Ordinal);
			HashSet<string> texs = AbImport.atlasTex(pre, files.ToArray());
			foreach (string tex in texs)
			{
				if (!astMap.TryGetValue(tex, out AbPkg pkg))
					throw new InvalidDataException("图集成员缺少AB归属:" + tex);
				changed |= picks.Add(pkg.name);
			}
		}
		while (changed);
	}

	static AbPlan makePick(AbCfg cfg, HashSet<string> picks)
	{
		AbPlan pre = make(cfg, new HashSet<string>(StringComparer.Ordinal), picks);
		HashSet<string> texs = AbImport.atlasTex(pre, AbImport.atlasFiles(pre));
		AbPlan plan = make(cfg, texs, picks);
		plan.chkAtlCfg(pre, texs);
		return plan;
	}

	void addGroup(AbGroup group, int index, HashSet<string> ids,
		Dictionary<string, string> bundles, Dictionary<string, string> paths,
		Dictionary<string, string> keys, HashSet<string> atlas)
	{
		if (group == null)
		{
			errs.Add("AB Group为空:" + (index + 1));
			return;
		}
		string id = normId(group.id);
		string shown = (group.bundleName ?? string.Empty).Trim().Replace('\\', '/');
		string bundle = normName(group.bundleName);
		bool valid = true;
		if (!chkId(id))
		{
			errs.Add("AB Group ID无效:" + group.id);
			valid = false;
		}
		else if (!ids.Add(id))
		{
			errs.Add("AB Group ID重复:" + id);
			valid = false;
		}
		if (!chkName(bundle, out string nameErr))
		{
			errs.Add("AB包名无效:" + nameErr);
			valid = false;
		}
		else if (bundles.TryGetValue(bundle, out string old))
		{
			errs.Add(string.Equals(old, shown, StringComparison.Ordinal) ?
				"AB包名重复:" + shown :
				"AB包名大小写冲突:" + old + "/" + shown);
			valid = false;
		}
		else bundles.Add(bundle, shown);
		if (group.entries == null || group.entries.Count == 0)
		{
			errs.Add("AB Group没有显式资源:" + bundle);
			valid = false;
		}
		if (!valid) return;

		AbPkg pkg = new() { name = bundle, key = "G|" + id };
		pkgs.Add(pkg);
		for (int i = 0; i < group.entries.Count; ++i)
			addEntry(pkg, group.entries[i], i, paths, keys, atlas);
	}

	void addEntry(AbPkg pkg, AbEntry entry, int index,
		Dictionary<string, string> paths, Dictionary<string, string> keys,
		HashSet<string> atlas)
	{
		if (entry == null)
		{
			errs.Add("AB资源条目为空:" + pkg.name + "/" + (index + 1));
			return;
		}
		string guid = normGuid(entry.guid);
		if (!chkGuid(guid))
		{
			errs.Add("AB资源GUID无效:" + pkg.name + "/" + entry.guid);
			return;
		}
		string path = norm(AssetDatabase.GUIDToAssetPath(guid));
		if (string.IsNullOrEmpty(path) || AssetDatabase.IsValidFolder(path) ||
			normGuid(AssetDatabase.AssetPathToGUID(path)) != guid)
		{
			errs.Add("AB资源GUID未解析到有效文件:" + pkg.name + "/" + guid);
			return;
		}
		string key = normKey(entry.address);
		if (!chkKey(key) || !chkUtf8(key, "资源地址", path))
		{
			errs.Add("AB资源逻辑地址无效:" + path + " -> " + entry.address);
			return;
		}
		if (paths.TryGetValue(path, out string oldPkg))
		{
			errs.Add("资源重复配置:" + path + "，" + oldPkg + "/" + pkg.name);
			return;
		}
		paths.Add(path, pkg.name);
		if (keys.TryGetValue(key, out string oldPath))
		{
			errs.Add("资源地址重复:" + key + "，" + oldPath + "/" + path);
			return;
		}
		keys.Add(key, path);

		if (!canPack(path, atlas))
		{
			if (!atlas.Contains(path)) errs.Add("显式资源不可打入AssetBundle:" + path);
			return;
		}
		bool scene = hasEnd(path, ".unity");
		if (scene != hasEnd(key, ".unity"))
		{
			errs.Add(scene ? "场景资源地址必须以.unity结尾:" + path + " -> " + key :
				"非场景资源地址不能以.unity结尾:" + path + " -> " + key);
			return;
		}
		if (pkg.asts.Count > 0 && hasEnd(pkg.asts[0].path, ".unity") != scene)
		{
			errs.Add("场景与普通资源不能混入同一AB:" + pkg.name);
			return;
		}
		pkg.asts.Add(new AbAst
		{
			path = path,
			key = key,
			name = key,
		});
	}

	void addDeps(HashSet<string> atlas)
	{
		Dictionary<string, AbPkg> astMap = new(StringComparer.Ordinal);
		Dictionary<string, AbAst> asts = new(StringComparer.Ordinal);
		Dictionary<string, AbPkg> pkgMap = new(StringComparer.Ordinal);
		foreach (AbPkg pkg in pkgs)
		{
			pkgMap[pkg.name] = pkg;
			foreach (AbAst ast in pkg.asts)
			{
				astMap[ast.path] = pkg;
				asts[ast.path] = ast;
			}
		}
		foreach (AbPkg pkg in pkgs)
		{
			HashSet<string> deps = new(StringComparer.Ordinal);
			HashSet<string> seen = new(StringComparer.Ordinal);
			foreach (AbAst ast in pkg.asts)
				findDeps(ast, pkg, astMap, asts, atlas, seen, deps);
			pkg.deps.AddRange(deps);
			pkg.deps.Sort(StringComparer.Ordinal);
		}
		foreach (AbPkg pkg in pkgs)
		{
			HashSet<string> vals = new(StringComparer.Ordinal);
			addAll(pkg.name, pkgMap, vals);
			vals.Remove(pkg.name);
			pkg.allDeps.AddRange(vals);
			pkg.allDeps.Sort(StringComparer.Ordinal);
			edgeCnt += pkg.deps.Count;
		}
	}

	void findDeps(AbAst ast, AbPkg src, Dictionary<string, AbPkg> astMap,
		Dictionary<string, AbAst> asts, HashSet<string> atlas,
		HashSet<string> seen, HashSet<string> deps)
	{
		if (!seen.Add(ast.path)) return;
		foreach (string raw in AssetDatabase.GetDependencies(ast.path, false))
		{
			string dep = norm(raw);
			if (dep == ast.path) continue;
			if (astMap.TryGetValue(dep, out AbPkg pkg))
			{
				AbAst depAst = asts[dep];
				if (!ast.deps.Contains(depAst.key)) ast.deps.Add(depAst.key);
				if (pkg != src) deps.Add(pkg.name);
				continue;
			}
			if (isAsset(dep) && canPack(dep, atlas))
				errs.Add("资源依赖未显式配置:" + ast.path + " -> " + dep);
		}
		ast.deps.Sort(StringComparer.Ordinal);
	}

	void chkAtlCfg(AbPlan pre, HashSet<string> texs)
	{
		foreach (string path in texs)
			if (!pre.contains(path)) errs.Add("图集纹理缺少显式AB归属:" + path);
	}

	static void addAll(string name, Dictionary<string, AbPkg> pkgMap,
		HashSet<string> vals)
	{
		if (!pkgMap.TryGetValue(name, out AbPkg pkg)) return;
		foreach (string dep in pkg.deps)
			if (vals.Add(dep)) addAll(dep, pkgMap, vals);
	}

	static bool isAsset(string path)
	{
		return !string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(path)) &&
			AssetImporter.GetAtPath(path) != null && !isCode(path);
	}

	static bool canPack(string ast, HashSet<string> atlas)
	{
		if (AssetDatabase.IsValidFolder(ast) || isCode(ast) || hasEnd(ast, ".meta") ||
			hasEnd(ast, ".DS_Store") || hasEnd(ast, ".cginc") || hasEnd(ast, ".hlsl") ||
			hasEnd(ast, ".glslinc") || hasEnd(ast, ".tpsheet") ||
			hasEnd(ast, "LightingData.asset")) return false;
		if (!hasEnd(ast, SPRITE_ATLAS_SUFFIX) && atlas.Contains(ast)) return false;
		return AssetImporter.GetAtPath(ast) != null;
	}

	static bool isCode(string path)
	{
		return hasEnd(path, ".cs") || hasEnd(path, ".asmdef") ||
			hasEnd(path, ".asmref") || hasEnd(path, ".dll");
	}

	static bool chkId(string value)
	{
		if (string.IsNullOrEmpty(value) || value.Length > 64) return false;
		foreach (char c in value)
			if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') ||
				c == '-' || c == '_' || c == '.')) return false;
		return true;
	}

	static bool chkGuid(string value)
	{
		if (value.Length != 32) return false;
		foreach (char c in value)
			if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
		return true;
	}

	static bool chkKey(string key) => AbIndex.validKey(key);

	static bool chkUtf8(string val, string kind, string ast)
	{
		int len = Encoding.UTF8.GetByteCount(val);
		return len < 256;
	}

	static bool chkName(string value, out string err)
	{
		string name = normName(value);
		if (string.IsNullOrEmpty(name))
		{
			err = "包名为空";
			return false;
		}
		if (!name.EndsWith(ASSET_BUNDLE_SUFFIX, StringComparison.Ordinal))
		{
			err = "必须以" + ASSET_BUNDLE_SUFFIX + "结尾:" + value;
			return false;
		}
		if (!isRel(name))
		{
			err = "包名路径非法:" + value;
			return false;
		}
		if (Encoding.UTF8.GetByteCount(name) >= 256)
		{
			err = "包名UTF-8长度必须小于256字节";
			return false;
		}
		err = null;
		return true;
	}

	static bool isRel(string value)
	{
		if (string.IsNullOrEmpty(value) || value[0] == '/' ||
			value[value.Length - 1] == '/' || value.IndexOf('\\') >= 0)
			return false;
		foreach (string part in value.Split('/'))
		{
			if (part.Length == 0 || part == "." || part == ".." ||
				part.EndsWith(".", StringComparison.Ordinal) ||
				part.EndsWith(" ", StringComparison.Ordinal) || isWin(part))
				return false;
			foreach (char c in part)
				if (char.IsControl(c) || c == ':' || c == '*' || c == '?' ||
					c == '"' || c == '<' || c == '>' || c == '|') return false;
		}
		return true;
	}

	static bool isWin(string part)
	{
		int dot = part.IndexOf('.');
		string name = (dot < 0 ? part : part.Substring(0, dot)).ToUpperInvariant();
		if (name == "CON" || name == "PRN" || name == "AUX" || name == "NUL")
			return true;
		return name.Length == 4 && (name.StartsWith("COM", StringComparison.Ordinal) ||
			name.StartsWith("LPT", StringComparison.Ordinal)) &&
			name[3] >= '1' && name[3] <= '9';
	}

	static string norm(string path) =>
		(path ?? string.Empty).Replace('\\', '/').TrimEnd('/');
	static string normGuid(string guid) =>
		(guid ?? string.Empty).Trim().ToLowerInvariant();
	static string normId(string value) =>
		(value ?? string.Empty).Trim().ToLowerInvariant();
	static string normKey(string key) =>
		(key ?? string.Empty).Trim().Replace('\\', '/').Trim('/');
	static string normName(string value) =>
		(value ?? string.Empty).Trim().Replace('\\', '/').ToLowerInvariant();
	static bool hasEnd(string val, string end) =>
		val.EndsWith(end, StringComparison.OrdinalIgnoreCase);
}
