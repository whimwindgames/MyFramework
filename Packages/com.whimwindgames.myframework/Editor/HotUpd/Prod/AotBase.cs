using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;

public sealed class AotRef
{
	public string env;
	public string id;
}

public sealed class AotBaseReq
{
	// HybridCLR裁剪后的AOT程序集目录。freeze时必填，check时忽略。
	public string source;
	// 可选的基线根目录；为空时使用项目 HybridCLRData/AOTBaselines。
	public string root;
	public UpdCfg cfg;
	public HotPlan plan;
	public BuildTarget target;
	public bool useObf;
}

public sealed class AotBaseInfo
{
	public string path;
	public string[] dlls;
	public HotCap cap;
	public string obfCap;
	public string baseUrl;
	public string pubKey;
	public bool contentAddressed;
}

// prepare/promote/accept用于把AOT基线纳入Player产物的外层事务。
// promote后、accept前发生失败时，Dispose会撤回本次新建的公开基线。
public sealed class AotPending : IDisposable
{
	readonly string mTarget;
	string mCandidate;
	readonly bool mExisting;
	bool mPromoted;
	bool mAccepted;

	public AotBaseInfo info { get; }
	public string path => mPromoted || mExisting ? mTarget : mCandidate;

	internal AotPending(string target, string candidate, AotBaseInfo info, bool existing)
	{
		mTarget = target;
		mCandidate = candidate;
		this.info = info;
		mExisting = existing;
	}

	public void promote()
	{
		if (mPromoted || mAccepted) throw new InvalidOperationException("AOT基线事务已经结束");
		if (!mExisting)
		{
			if (Directory.Exists(mTarget) || File.Exists(mTarget))
				throw new IOException("AOT基线在事务期间被其他产物占用:" + mTarget);
			Directory.Move(mCandidate, mTarget);
			mCandidate = null;
			info.path = mTarget;
		}
		mPromoted = true;
	}

	public void accept()
	{
		if (!mPromoted || mAccepted) throw new InvalidOperationException("AOT基线尚未提升或已经确认");
		mAccepted = true;
	}

	public void Dispose()
	{
		if (mAccepted || mExisting) return;
		if (mPromoted && Directory.Exists(mTarget)) Directory.Delete(mTarget, true);
		if (!string.IsNullOrEmpty(mCandidate) && Directory.Exists(mCandidate))
			Directory.Delete(mCandidate, true);
	}
}

// AOT基线是Base ID的一部分：一旦冻结，程序集内容、Hot能力、启动地址、
// 公钥和Obfuz VM能力都不可原地改变。
public static class AotBase
{
	internal const string ObfMark = ".obf-cap";
	internal const string BootMark = ".boot-cap";
	const string BASE_MARK = ".baseline";
	const string AOT_LIST = ".aot-list";
	static readonly UTF8Encoding sUtf8 = new(false, true);

	public static string path(string env, string baseId)
	{
		return path(null, env, baseId, EditorUserBuildSettings.activeBuildTarget);
	}

	public static string path(string env, string baseId, BuildTarget target)
	{
		return path(null, env, baseId, target);
	}

	public static string path(string root, string env, string baseId, BuildTarget target)
	{
		if (!isEnv(env) || !UpdFmt.isId(baseId) || target == BuildTarget.NoTarget)
			throw new InvalidDataException("AOT基线身份错误");
		string baseRoot = string.IsNullOrWhiteSpace(root) ? defaultRoot() : safeRoot(root);
		return Path.Combine(baseRoot, env, baseId, target.ToString());
	}

	public static bool exists(string env, string baseId)
	{
		try
		{
			read(path(env, baseId), env, baseId, EditorUserBuildSettings.activeBuildTarget,
				null, null, false);
			return true;
		}
		catch { return false; }
	}

	public static AotRef[] list()
	{
		string root = defaultRoot();
		if (!Directory.Exists(root)) return Array.Empty<AotRef>();
		List<AotRef> values = new();
		foreach (string envDir in Directory.GetDirectories(root, "*", SearchOption.TopDirectoryOnly))
		{
			string env = Path.GetFileName(envDir);
			if (!isEnv(env)) continue;
			foreach (string baseDir in Directory.GetDirectories(envDir, "*", SearchOption.TopDirectoryOnly))
			{
				string id = Path.GetFileName(baseDir);
				if (UpdFmt.isId(id) && exists(env, id)) values.Add(new AotRef { env = env, id = id });
			}
		}
		values.Sort((left, right) =>
		{
			int result = string.CompareOrdinal(left.env, right.env);
			return result != 0 ? result : string.CompareOrdinal(left.id, right.id);
		});
		return values.ToArray();
	}

	public static IReadOnlyList<string> check(string env, string baseId)
	{
		return read(path(env, baseId), env, baseId,
			EditorUserBuildSettings.activeBuildTarget, null, null, false).dlls;
	}

	// 项目发布适配器通过稳定API读取随Base冻结的启动能力，
	// 无需自行解析框架私有标记文件。
	public static AotBaseInfo info(string env, string baseId)
	{
		return read(path(env, baseId), env, baseId,
			EditorUserBuildSettings.activeBuildTarget, null, null, false);
	}

	public static AotBaseInfo check(AotBaseReq req)
	{
		checkReq(req, false);
		string target = path(req.root, req.cfg.env, req.cfg.baseId, req.target);
		return read(target, req.cfg.env, req.cfg.baseId, req.target,
			req.cfg, req.plan, req.useObf);
	}

	public static string freeze(AotBaseReq req)
	{
		using AotPending pending = prepare(req);
		pending.promote();
		pending.accept();
		return pending.path;
	}

	public static AotPending prepare(AotBaseReq req)
	{
		checkReq(req, true);
		string source = safeDir(req.source, "HybridCLR裁剪AOT目录");
		string target = path(req.root, req.cfg.env, req.cfg.baseId, req.target);
		checkSeparate(source, target);
		ensurePhysical(target);
		if (File.Exists(target)) throw new InvalidDataException("AOT基线路径被普通文件占用:" + target);
		string parent = Path.GetDirectoryName(target);
		Directory.CreateDirectory(parent);
		ensurePhysical(parent);
		string candidate = target + ".candidate-" + Guid.NewGuid().ToString("N");
		try
		{
			Directory.CreateDirectory(candidate);
			copyAot(source, candidate, req.plan.cap);
			List<string> dlls = findAot(candidate);
			writeList(candidate, dlls);
			HotList.save(candidate, req.plan.cap);
			writeObf(candidate, req.useObf ? requiredObfCap() : DllObf.NoCap);
			writeBoot(candidate, req.cfg.baseUrl, req.cfg.pubKey,
				req.cfg.contentAddressed);
			File.WriteAllText(Path.Combine(candidate, BASE_MARK), baselineIdentity(candidate,
				req.cfg.env, req.cfg.baseId, req.target), new UTF8Encoding(false));
			read(candidate, req.cfg.env, req.cfg.baseId, req.target, req.cfg, req.plan,
				req.useObf);

			if (Directory.Exists(target))
			{
				AotBaseInfo old = read(target, req.cfg.env, req.cfg.baseId, req.target,
					req.cfg, req.plan, req.useObf);
				if (File.ReadAllText(Path.Combine(candidate, BASE_MARK), sUtf8) !=
					File.ReadAllText(Path.Combine(old.path, BASE_MARK), sUtf8))
					throw new InvalidDataException("Base ID已绑定不同AOT基线，请为新主包使用新的Base ID");
				Directory.Delete(candidate, true);
				candidate = null;
				return new AotPending(target, null, old, true);
			}
			AotBaseInfo info = read(candidate, req.cfg.env, req.cfg.baseId, req.target,
				req.cfg, req.plan, req.useObf);
			AotPending pending = new(target, candidate, info, false);
			candidate = null;
			return pending;
		}
		finally
		{
			if (!string.IsNullOrEmpty(candidate) && Directory.Exists(candidate))
				Directory.Delete(candidate, true);
		}
	}

	internal static AotBaseInfo inspect(string root, UpdCfg cfg, HotPlan plan,
		BuildTarget target, bool useObf)
	{
		return check(new AotBaseReq
		{
			root = root,
			cfg = cfg,
			plan = plan,
			target = target,
			useObf = useObf,
		});
	}

	static void checkReq(AotBaseReq req, bool source)
	{
		if (req == null) throw new ArgumentNullException(nameof(req));
		if (req.cfg == null) throw new ArgumentNullException(nameof(req.cfg));
		if (req.plan == null) throw new ArgumentNullException(nameof(req.plan));
		UpdRule.prod(req.cfg);
		HotList.chk(req.plan);
		HotList.chkCfg(req.cfg, req.plan.hot);
		if (req.target == BuildTarget.NoTarget || req.cfg.platform != platform(req.target))
			throw new InvalidDataException("AOT基线BuildTarget与发布平台不一致");
		checkBoot(req.cfg.baseUrl, req.cfg.pubKey);
		if (source && string.IsNullOrWhiteSpace(req.source))
			throw new InvalidDataException("请提供HybridCLR裁剪AOT目录");
	}

	static AotBaseInfo read(string root, string env, string baseId, BuildTarget target,
		UpdCfg cfg, HotPlan plan, bool useObf)
	{
		string full = safeDir(root, "AOT基线");
		string marker = Path.Combine(full, BASE_MARK);
		FileInfo markerFile = new(marker);
		if (!markerFile.Exists || markerFile.Length <= 0 || markerFile.Length > 1024 * 1024 ||
			(markerFile.Attributes & FileAttributes.ReparsePoint) != 0)
			throw new InvalidDataException("AOT基线不存在、身份不匹配或内容已损坏");
		string actual = File.ReadAllText(marker, sUtf8);
		if (actual != baselineIdentity(full, env, baseId, target))
			throw new InvalidDataException("AOT基线不存在、身份不匹配或内容已损坏");
		List<string> dlls = readList(full);
		if (!sameList(dlls, findAot(full)))
			throw new InvalidDataException("AOT基线清单与实际stripped程序集不一致");
		foreach (string dll in dlls)
			checkDll(Path.Combine(full, dll), Path.GetFileNameWithoutExtension(dll));
		HotCap cap = HotList.loadCap(full);
		string obf = readObf(full);
		(string baseUrl, string pubKey, bool contentAddressed) = readBoot(full);
		checkCanonicalTree(full, dlls);
		if (plan != null && !HotList.same(plan.cap, cap))
			throw new InvalidDataException("本次热更计划与冻结Base能力不一致");
		if (cfg != null && (cfg.env != env || cfg.baseId != baseId || cfg.platform != platform(target) ||
			cfg.baseUrl != baseUrl || cfg.pubKey != pubKey ||
			cfg.contentAddressed != contentAddressed))
			throw new InvalidDataException("Base ID已绑定不同启动配置");
		if (useObf)
		{
			string current = requiredObfCap();
			if (obf != current) throw new InvalidDataException("当前Obfuz VM能力与目标Base不一致");
		}
		return new AotBaseInfo
		{
			path = full,
			dlls = dlls.ToArray(),
			cap = cap,
			obfCap = obf,
			baseUrl = baseUrl,
			pubKey = pubKey,
			contentAddressed = contentAddressed,
		};
	}

	static void copyAot(string source, string candidate, HotCap cap)
	{
		DirectoryInfo input = new(source);
		if (input.GetDirectories().Length != 0)
			throw new InvalidDataException("HybridCLR裁剪AOT目录不得包含子目录");
		HashSet<string> hot = new(cap.allow, StringComparer.OrdinalIgnoreCase);
		int count = 0;
		foreach (FileInfo file in input.GetFiles())
		{
			if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
				throw new InvalidDataException("HybridCLR裁剪AOT目录包含符号链接:" + file.FullName);
			if (!file.Extension.Equals(".dll", StringComparison.OrdinalIgnoreCase))
				throw new InvalidDataException("HybridCLR裁剪AOT目录包含非DLL文件:" + file.Name);
			if (file.Name.Equals("netstandard.dll", StringComparison.OrdinalIgnoreCase)) continue;
			string name = file.Name.Substring(0, file.Name.Length - 4);
			if (!validAsmName(name)) throw new InvalidDataException("AOT程序集名称非法:" + file.Name);
			if (hot.Contains(name)) throw new InvalidDataException("热更程序集错误进入stripped AOT基线:" + file.Name);
			checkDll(file.FullName, name);
			copyChecked(file.FullName, Path.Combine(candidate, file.Name));
			if (++count > 4096) throw new InvalidDataException("AOT程序集数量超过安全上限");
		}
		if (count == 0) throw new InvalidDataException("HybridCLR裁剪AOT目录没有可冻结的程序集");
	}

	static List<string> findAot(string root)
	{
		List<string> values = new();
		foreach (string path in Directory.GetFiles(root, "*.dll", SearchOption.TopDirectoryOnly))
			values.Add(Path.GetFileName(path));
		return validateNames(values);
	}

	static List<string> readList(string root)
	{
		string path = Path.Combine(root, AOT_LIST);
		FileInfo file = new(path);
		if (!file.Exists || file.Length > 256 * 1024 ||
			(file.Attributes & FileAttributes.ReparsePoint) != 0)
			throw new InvalidDataException("AOT基线清单不存在或为符号链接");
		string text = File.ReadAllText(path, sUtf8);
		if (text.IndexOf('\r') >= 0 || (text.Length > 0 && !text.EndsWith("\n", StringComparison.Ordinal)))
			throw new InvalidDataException("AOT基线清单不是规范LF格式");
		string[] lines = text.Length == 0 ? Array.Empty<string>() :
			text.Substring(0, text.Length - 1).Split('\n');
		List<string> values = validateNames(lines);
		if (text != listText(values)) throw new InvalidDataException("AOT基线清单不是规范格式");
		return values;
	}

	static void writeList(string root, List<string> names)
	{
		File.WriteAllText(Path.Combine(root, AOT_LIST), listText(names), new UTF8Encoding(false));
	}

	static string listText(List<string> names)
	{
		return names.Count == 0 ? string.Empty : string.Join("\n", names) + "\n";
	}

	static List<string> validateNames(IEnumerable<string> values)
	{
		HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
		List<string> names = new();
		foreach (string raw in values ?? Array.Empty<string>())
		{
			string name = raw?.Trim();
			if (string.IsNullOrEmpty(name) || name != Path.GetFileName(name) ||
				!name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
				name.Equals("netstandard.dll", StringComparison.OrdinalIgnoreCase) ||
				!validAsmName(name.Substring(0, name.Length - 4)) || !seen.Add(name))
				throw new InvalidDataException("AOT程序集清单非法:" + raw);
			names.Add(name);
		}
		names.Sort(StringComparer.Ordinal);
		return names;
	}

	static string baselineIdentity(string root, string env, string baseId, BuildTarget target)
	{
		StringBuilder text = new();
		text.Append("schema=7\nenv=").Append(env).Append("\nplatform=")
			.Append(target).Append("\nbaseId=").Append(baseId).Append('\n');
		Dictionary<string, string> hashes = treeHashes(root, BASE_MARK);
		List<string> paths = new(hashes.Keys);
		paths.Sort(StringComparer.Ordinal);
		foreach (string path in paths)
			text.Append(hashes[path]).Append(' ')
				.Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(path))).Append('\n');
		return text.ToString();
	}

	static Dictionary<string, string> treeHashes(string root, string ignore)
	{
		Dictionary<string, string> hashes = new(StringComparer.Ordinal);
		DirectoryInfo dir = new(root);
		if (!dir.Exists || (dir.Attributes & FileAttributes.ReparsePoint) != 0)
			throw new InvalidDataException("AOT基线目录不存在或为符号链接:" + root);
		foreach (DirectoryInfo child in dir.GetDirectories())
			throw new InvalidDataException("AOT基线不得包含子目录:" + child.FullName);
		foreach (FileInfo file in dir.GetFiles())
		{
			if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
				throw new InvalidDataException("AOT基线包含符号链接文件:" + file.FullName);
			if (file.Name != ignore) hashes.Add(file.Name, fileSha(file.FullName));
		}
		return hashes;
	}

	static void checkCanonicalTree(string root, List<string> dlls)
	{
		HashSet<string> allow = new(dlls, StringComparer.Ordinal);
		allow.Add(BASE_MARK);
		allow.Add(AOT_LIST);
		allow.Add(HotList.CapName);
		allow.Add(ObfMark);
		allow.Add(BootMark);
		foreach (string file in Directory.GetFiles(root, "*", SearchOption.TopDirectoryOnly))
			if (!allow.Contains(Path.GetFileName(file)))
				throw new InvalidDataException("AOT基线包含非规范文件:" + file);
	}

	static void writeObf(string root, string value)
	{
		if (!DllObf.isCap(value)) throw new InvalidDataException("Obfuz Base能力标识非法");
		File.WriteAllText(Path.Combine(root, ObfMark), value + "\n", new UTF8Encoding(false));
	}

	static string readObf(string root)
	{
		string path = Path.Combine(root, ObfMark);
		FileInfo file = new(path);
		if (!file.Exists || file.Length <= 0 || file.Length > 80 ||
			(file.Attributes & FileAttributes.ReparsePoint) != 0)
			throw new InvalidDataException("AOT基线的Obfuz能力标识缺失或非法");
		string text = File.ReadAllText(path, sUtf8);
		if (!text.EndsWith("\n", StringComparison.Ordinal))
			throw new InvalidDataException("AOT基线的Obfuz能力标识格式非法");
		string value = text.Substring(0, text.Length - 1);
		if (!DllObf.isCap(value)) throw new InvalidDataException("AOT基线的Obfuz能力标识格式非法");
		return value;
	}

	static string requiredObfCap()
	{
		string value = DllObf.cap();
		if (value == DllObf.NoCap) throw new InvalidOperationException("启用了代码混淆，但项目没有Obfuz适配器");
		return value;
	}

	static void writeBoot(string root, string baseUrl, string pubKey,
		bool contentAddressed)
	{
		checkBoot(baseUrl, pubKey);
		string text = "schema=2\nbaseUrl=" + enc(baseUrl) + "\npubKey=" +
			enc(pubKey) + "\nstore=" + (contentAddressed
				? "cas-sha256-v1" : "release-v1") + "\n";
		File.WriteAllText(Path.Combine(root, BootMark), text, new UTF8Encoding(false));
	}

	static (string, string, bool) readBoot(string root)
	{
		string path = Path.Combine(root, BootMark);
		FileInfo file = new(path);
		if (!file.Exists || file.Length <= 0 || file.Length > 4096 ||
			(file.Attributes & FileAttributes.ReparsePoint) != 0)
			throw new InvalidDataException("AOT基线的启动能力标识缺失或非法");
		string text = File.ReadAllText(path, sUtf8);
		string[] lines = text.Split('\n');
		bool legacy = lines.Length == 4 && lines[0] == "schema=1" &&
			lines[3].Length == 0;
		bool current = lines.Length == 5 && lines[0] == "schema=2" &&
			lines[4].Length == 0;
		if (!legacy && !current)
			throw new InvalidDataException("AOT基线的启动能力标识格式非法");
		string baseUrl = dec(lines[1], "baseUrl=");
		string pubKey = dec(lines[2], "pubKey=");
		bool contentAddressed = false;
		if (current)
		{
			if (lines[3] == "store=cas-sha256-v1") contentAddressed = true;
			else if (lines[3] != "store=release-v1")
				throw new InvalidDataException("AOT基线的内容仓库能力标识非法");
		}
		checkBoot(baseUrl, pubKey);
		return (baseUrl, pubKey, contentAddressed);
	}

	static void checkBoot(string baseUrl, string pubKey)
	{
		if (string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(pubKey) ||
			baseUrl.Length > 2048 || pubKey.Length > 2048 ||
			!Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri uri) ||
			!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
			!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) ||
			!string.IsNullOrEmpty(uri.Fragment))
			throw new InvalidDataException("Base启动地址或公钥非法");
		try { _ = new UpdSign(pubKey); }
		catch (Exception ex) { throw new InvalidDataException("Base启动公钥非法", ex); }
	}

	static string enc(string value)
	{
		return Convert.ToBase64String(sUtf8.GetBytes(value));
	}

	static string dec(string line, string prefix)
	{
		if (!line.StartsWith(prefix, StringComparison.Ordinal))
			throw new InvalidDataException("AOT基线的启动能力字段非法");
		string text = line.Substring(prefix.Length);
		byte[] raw;
		try { raw = Convert.FromBase64String(text); }
		catch (FormatException ex) { throw new InvalidDataException("AOT基线启动字段不是Base64", ex); }
		if (raw.Length == 0 || Convert.ToBase64String(raw) != text)
			throw new InvalidDataException("AOT基线启动字段不是规范Base64");
		return sUtf8.GetString(raw);
	}

	static void checkDll(string path, string expected)
	{
		string actual;
		try { actual = AssemblyName.GetAssemblyName(path).Name; }
		catch (Exception ex) { throw new InvalidDataException("无法读取AOT DLL内部程序集名:" + path, ex); }
		if (actual != expected) throw new InvalidDataException("AOT DLL内部程序集名不匹配:" + expected);
	}

	static void copyChecked(string source, string target)
	{
		FileInfo input = new(source);
		if (!input.Exists || input.Length <= 0 || (input.Attributes & FileAttributes.ReparsePoint) != 0)
			throw new FileNotFoundException("AOT生产输入文件不存在或非法", source);
		File.Copy(source, target, false);
		FileInfo output = new(target);
		if (!output.Exists || output.Length != input.Length || fileSha(source) != fileSha(target))
			throw new IOException("AOT生产文件复制校验失败:" + source);
	}

	static string fileSha(string path)
	{
		using FileStream input = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
		using SHA256 sha = SHA256.Create();
		return Convert.ToBase64String(sha.ComputeHash(input));
	}

	static bool sameList(List<string> left, List<string> right)
	{
		if (left.Count != right.Count) return false;
		for (int i = 0; i < left.Count; ++i) if (left[i] != right[i]) return false;
		return true;
	}

	static string safeDir(string value, string label)
	{
		if (string.IsNullOrWhiteSpace(value) || !Path.IsPathRooted(value))
			throw new InvalidDataException(label + "必须是绝对路径");
		string full = trim(Path.GetFullPath(value));
		DirectoryInfo dir = new(full);
		if (!dir.Exists || (dir.Attributes & FileAttributes.ReparsePoint) != 0)
			throw new DirectoryNotFoundException(label + "不存在或为符号链接:" + full);
		ensurePhysical(full);
		return full;
	}

	static string safeRoot(string value)
	{
		if (!Path.IsPathRooted(value)) throw new InvalidDataException("AOT基线根目录必须是绝对路径");
		string full = trim(Path.GetFullPath(value));
		if (full == trim(Path.GetPathRoot(full)) || File.Exists(full))
			throw new InvalidDataException("AOT基线根目录不能是文件系统根或普通文件");
		ensurePhysical(full);
		return full;
	}

	static void checkSeparate(string source, string target)
	{
		string left = trim(source) + Path.DirectorySeparatorChar;
		string right = trim(Path.GetFullPath(target)) + Path.DirectorySeparatorChar;
		if (left.StartsWith(right, StringComparison.OrdinalIgnoreCase) ||
			right.StartsWith(left, StringComparison.OrdinalIgnoreCase))
			throw new InvalidDataException("AOT源目录与冻结基线不能相互包含");
	}

	static void ensurePhysical(string path)
	{
		for (DirectoryInfo dir = new(path); dir != null; dir = dir.Parent)
			if (dir.Exists && (dir.Attributes & FileAttributes.ReparsePoint) != 0)
				throw new InvalidDataException("生产路径不能经过符号链接:" + dir.FullName);
	}

	static string defaultRoot()
	{
		string project = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
		return Path.Combine(project, "HybridCLRData", "AOTBaselines");
	}

	static string platform(BuildTarget target)
	{
		switch (target)
		{
			case BuildTarget.Android: return FrameBaseDefine.ANDROID;
			case BuildTarget.iOS: return FrameBaseDefine.IOS;
			case BuildTarget.StandaloneOSX: return FrameBaseDefine.MACOS;
			case BuildTarget.StandaloneWindows:
			case BuildTarget.StandaloneWindows64: return FrameBaseDefine.WINDOWS;
			case BuildTarget.WebGL: return FrameBaseDefine.WEBGL;
			default: throw new InvalidDataException("当前BuildTarget尚未适配HybridCLR生产:" + target);
		}
	}

	static bool validAsmName(string name)
	{
		return UpdFmt.isId(name) && name == Path.GetFileName(name) &&
			!name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
	}

	static bool isEnv(string env)
	{
		return env == "test" || env == "prod";
	}

	static string trim(string path)
	{
		string root = Path.GetPathRoot(path);
		return path.Length > root.Length ? path.TrimEnd(Path.DirectorySeparatorChar,
			Path.AltDirectorySeparatorChar) : path;
	}
}
