using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

[Serializable]
public sealed class AbItem
{
	public string key;
	[NonSerialized]
	public string src;
	public string name;
	public string bundle;
	public string scene;
	public string atlas;
	public readonly List<string> bdeps = new();
}

// Schema 11使用的确定性AssetBundle索引。旧AssetBundleLoader保持不变，新生产链可并行生成此索引。
public static class AbIndex
{
	const int MAGIC = 0x41424958;
	const int VER = 3;
	const int MAX_ITEMS = 1000000;
	const int MAX_DEPS = 10000;
	public static Func<string, string> srcKey;
	public static Func<string, string> keySrc;
	public static Func<AbItem[]> atlasMap;
	static AbItem[] sRuntimeAtlas;
#if UNITY_EDITOR
	public static Func<AbItem[]> itemMap;
#endif

	public static string editKey(string src)
	{
		string key = srcKey?.Invoke(src) ?? throw new InvalidOperationException("编辑器AB映射未初始化");
		if (!validKey(key)) throw new InvalidDataException("资源逻辑地址无效:" + key);
		return key;
	}

	public static bool tryEditKey(string src, out string key)
	{
		if (srcKey == null) throw new InvalidOperationException("编辑器AB映射未初始化");
		key = srcKey(src);
		return !string.IsNullOrEmpty(key);
	}

	public static string editSrc(string key)
	{
		if (!validKey(key)) throw new InvalidDataException("资源逻辑地址无效:" + key);
		return keySrc?.Invoke(key) ?? throw new InvalidOperationException("编辑器AB映射未初始化");
	}

	public static bool tryEditSrc(string key, out string src)
	{
		if (!validKey(key)) throw new InvalidDataException("资源逻辑地址无效:" + key);
		if (keySrc == null) throw new InvalidOperationException("编辑器AB映射未初始化");
		src = keySrc(key);
		return !string.IsNullOrEmpty(src);
	}

	public static AbItem[] editAtlas()
	{
		return atlasMap?.Invoke() ?? throw new InvalidOperationException("编辑器AB映射未初始化");
	}

	internal static void bindRuntime(IReadOnlyList<AbItem> items)
	{
		if (items == null)
		{
			sRuntimeAtlas = null;
			return;
		}
		List<AbItem> atlases = new();
		foreach (AbItem item in items)
		{
			if (!string.IsNullOrEmpty(item.atlas)) atlases.Add(item);
		}
		sRuntimeAtlas = atlases.ToArray();
	}

	public static bool tryRuntimeAtlas(out AbItem[] items)
	{
		if (sRuntimeAtlas == null)
		{
			items = null;
			return false;
		}
		items = (AbItem[])sRuntimeAtlas.Clone();
		return true;
	}

#if UNITY_EDITOR
	public static AbItem[] editItems()
	{
		return itemMap?.Invoke() ?? throw new InvalidOperationException("编辑器AB索引未初始化");
	}
#endif

	public static byte[] encode(IReadOnlyList<AbItem> items)
	{
		if (items == null) throw new ArgumentNullException(nameof(items));
		check(items);
		SerializerWrite ser = new();
		ser.write(MAGIC);
		ser.write(VER);
		ser.write(items.Count);
		foreach (AbItem item in items)
		{
			ser.writeString(item.key);
			ser.writeString(item.name);
			ser.writeString(item.bundle);
			ser.writeString(item.scene);
			ser.writeString(item.atlas);
			ser.writeList(item.bdeps);
		}
		byte[] data = new byte[ser.getDataSize()];
		Buffer.BlockCopy(ser.getBuffer(), 0, data, 0, data.Length);
		return data;
	}

	public static bool isCurrent(byte[] data)
	{
		if (data == null || data.Length < sizeof(int)) return false;
		SerializerRead ser = new();
		ser.init(data);
		return ser.read(out int magic) && magic == MAGIC;
	}

	public static List<AbItem> decode(byte[] data)
	{
		if (data == null) throw new ArgumentNullException(nameof(data));
		SerializerRead ser = new();
		ser.init(data);
		if (!ser.read(out int magic) || magic != MAGIC ||
			!ser.read(out int ver) || ver != VER ||
			!ser.read(out int count) || count < 0 || count > MAX_ITEMS)
		{
			throw new InvalidDataException("AB索引头无效");
		}
		List<AbItem> items = new(count);
		for (int i = 0; i < count; ++i)
		{
			AbItem item = new();
			if (!ser.readString(out item.key) || !ser.readString(out item.name) ||
				!ser.readString(out item.bundle) || !ser.readString(out item.scene) ||
				!ser.readString(out item.atlas) || !readVals(ser, item.bdeps))
			{
				throw new InvalidDataException("AB索引项无效:" + i);
			}
			items.Add(item);
		}
		if (ser.getIndex() != ser.getDataSize()) throw new InvalidDataException("AB索引存在尾随数据");
		check(items);
		return items;
	}

	static bool readVals(SerializerRead ser, List<string> vals)
	{
		if (!ser.read(out int count) || count < 0 || count > MAX_DEPS) return false;
		vals.Capacity = count;
		for (int i = 0; i < count; ++i)
		{
			if (!ser.readString(out string val)) return false;
			vals.Add(val);
		}
		return true;
	}

	static void check(IReadOnlyList<AbItem> items)
	{
		if (items.Count > MAX_ITEMS) throw new InvalidDataException("AB索引项过多");
		HashSet<string> keys = new(StringComparer.OrdinalIgnoreCase);
		HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
		HashSet<string> atlases = new(StringComparer.Ordinal);
		HashSet<string> bundles = new(StringComparer.Ordinal);
		Dictionary<string, List<string>> pkgDeps = new(StringComparer.Ordinal);
		string prev = null;
		foreach (AbItem item in items)
		{
			bool isScene = item?.key?.EndsWith(".unity", StringComparison.OrdinalIgnoreCase) == true;
			if (item == null || !validKey(item.key) || !validKey(item.name) || !chkBundle(item.bundle) ||
				!validAtl(item.atlas) || (!string.IsNullOrEmpty(item.scene) &&
				(!validKey(item.scene) || !item.scene.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))) ||
				!keys.Add(item.key) || !names.Add(item.name) ||
				(!string.IsNullOrEmpty(item.atlas) && !atlases.Add(item.atlas)) ||
				(prev != null && string.CompareOrdinal(prev, item.key) >= 0) ||
				(isScene != !string.IsNullOrEmpty(item.scene)))
			{
				throw new InvalidDataException("AB索引字段无效:" + (item?.key ?? "<null>"));
			}
			prev = item.key;
			bundles.Add(item.bundle);
			if (item.bdeps == null || item.bdeps.Count > MAX_DEPS)
				throw new InvalidDataException("AB索引依赖无效:" + item.key);
			if (pkgDeps.TryGetValue(item.bundle, out List<string> oldDeps))
			{
				if (!sameVals(oldDeps, item.bdeps)) throw new InvalidDataException("同一AB的依赖不一致:" + item.bundle);
			}
			else pkgDeps.Add(item.bundle, item.bdeps);
			string packPrev = null;
			foreach (string dep in item.bdeps)
			{
				if (!chkBundle(dep) || dep == item.bundle ||
					(packPrev != null && string.CompareOrdinal(packPrev, dep) >= 0))
					throw new InvalidDataException("AB索引包依赖无效:" + item.key);
				packPrev = dep;
			}
		}
		foreach (AbItem item in items)
		foreach (string dep in item.bdeps)
			if (!bundles.Contains(dep)) throw new InvalidDataException("AB索引包依赖缺失:" + item.bundle + " -> " + dep);
		chkGraph(pkgDeps);
	}

	static bool sameVals(List<string> left, List<string> right)
	{
		if (left.Count != right.Count) return false;
		for (int i = 0; i < left.Count; ++i) if (left[i] != right[i]) return false;
		return true;
	}

	static void chkGraph(Dictionary<string, List<string>> graph)
	{
		Dictionary<string, byte> state = new(StringComparer.Ordinal);
		void visit(string pkg)
		{
			if (state.TryGetValue(pkg, out byte val))
			{
				if (val == 1) throw new InvalidDataException("AB依赖存在环:" + pkg);
				return;
			}
			state[pkg] = 1;
			if (graph.TryGetValue(pkg, out List<string> deps))
				foreach (string dep in deps) visit(dep);
			state[pkg] = 2;
		}
		foreach (string pkg in graph.Keys) visit(pkg);
	}

	public static bool validKey(string val)
	{
		return chkPath(val, 255) && !val.StartsWith("/", StringComparison.Ordinal) &&
			val.IndexOfAny(new char[] { ':', '*', '?', '"', '<', '>', '|', '\0' }) < 0;
	}

	static bool chkPath(string val, int max)
	{
		if (string.IsNullOrWhiteSpace(val) || val != val.Trim() || val.Contains('\\') ||
			val.Contains("//") || Encoding.UTF8.GetByteCount(val) > max || val.IndexOf('\0') >= 0) return false;
		foreach (string part in val.Split('/')) if (part.Length == 0 || part == "." || part == "..") return false;
		return true;
	}

	static bool chkBundle(string val)
	{
		return validKey(val) && val.EndsWith(FrameDefine.ASSET_BUNDLE_SUFFIX, StringComparison.Ordinal);
	}

	static bool validAtl(string val)
	{
		return string.IsNullOrEmpty(val) || (!string.IsNullOrWhiteSpace(val) && val == val.Trim() &&
			Encoding.UTF8.GetByteCount(val) <= 255 && val.IndexOf('\0') < 0);
	}
}
