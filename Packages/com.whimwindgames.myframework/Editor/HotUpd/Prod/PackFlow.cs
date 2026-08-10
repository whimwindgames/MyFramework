using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using HybridCLR.Editor;
using HybridCLR.Editor.Commands;
using HybridSettings = HybridCLR.Editor.Settings.HybridCLRSettings;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditorInternal;
using UnityEngine;

public sealed class PackReq
{
	public UpdCfg cfg;
	public HotPlan plan;
	public BuildTarget target;
	// 整个Base客户端的不可覆盖输出目录。
	public string outputRoot;
	// outputRoot内的Player相对路径，例如 Game.app、Game.exe、Game.apk。
	public string playerPath;
	public string[] scenes;
	public BuildOptions options;
	public string baselineRoot;
	public bool useObf;
	// 可选：把完整Stage临时内置到StreamingAssets/<platform>并回读Player校验。
	public string embedStage;
	public string runSetPath = "Assets/Resources/PlatRunSet.asset";
	// 可选：把一个已经完成的Stage作为首个签名Release，与Player/Base一起提交。
	public RelReq release;
	// 可选：项目自己的Base登记、审计等事务。
	public IPackCommitHook commitHook;
	// 测试、CI或定制构建器可替换；为空时使用Unity/HybridCLR正式实现。
	public IPackApi api;
}

public sealed class PackReport
{
	public string outputRoot;
	public string player;
	public string baseline;
	public bool embedded;
	public string releaseId;
	public string detail;
}

public sealed class PackBuildResult
{
	public bool succeeded;
	public string detail;
}

public interface IPackApi
{
	void validate(BuildTarget target);
	void generateAll(BuildTarget target);
	PackBuildResult build(BuildPlayerOptions options);
	string strippedAot(BuildTarget target);
}

// 项目侧状态与Player、AOT基线、首个Release一起提交。
// validate不得修改状态；promote后的失败由Dispose回滚；accept后只释放资源。
public interface IPackCommitHook : IDisposable
{
	void validate();
	void promote();
	void accept();
}

public sealed class UnityPackApi : IPackApi
{
	public void validate(BuildTarget target)
	{
		if (EditorUserBuildSettings.activeBuildTarget != target)
			throw new BuildFailedException("完整客户端必须先切换到目标活动平台");
		if (EditorUserBuildSettings.buildScriptsOnly)
			throw new BuildFailedException("完整客户端不能使用Build Scripts Only");
		BuildTargetGroup group = BuildPipeline.GetBuildTargetGroup(target);
		NamedBuildTarget named = NamedBuildTarget.FromBuildTargetGroup(group);
		if (PlayerSettings.GetScriptingBackend(named) != ScriptingImplementation.IL2CPP)
			throw new BuildFailedException("HybridCLR完整客户端必须使用IL2CPP后端");
		if (!SettingsUtil.Enable)
			throw new BuildFailedException("HybridCLR当前未启用");
		if (target == BuildTarget.Android && EditorUserBuildSettings.exportAsGoogleAndroidProject)
			throw new BuildFailedException("通用整包生产不接受Android Export Project");
	}

	public void generateAll(BuildTarget target)
	{
		if (target != EditorUserBuildSettings.activeBuildTarget)
			throw new BuildFailedException("HybridCLR GenerateAll目标与活动平台不一致");
		PrebuildCommand.GenerateAll();
	}

	public PackBuildResult build(BuildPlayerOptions options)
	{
		BuildReport report = BuildPipeline.BuildPlayer(options);
		return new PackBuildResult
		{
			succeeded = report.summary.result == BuildResult.Succeeded,
			detail = report.summary.result + ", errors=" + report.summary.totalErrors +
				", size=" + report.summary.totalSize,
		};
	}

	public string strippedAot(BuildTarget target)
	{
		string path = SettingsUtil.GetAssembliesPostIl2CppStripDir(target);
		return Path.IsPathRooted(path) ? Path.GetFullPath(path) :
			Path.GetFullPath(Path.Combine(SettingsUtil.ProjectDir, path));
	}
}

// 完整客户端与AOT基线的双产物事务。公开身份只在Player、运行配置、
// stripped AOT和工程临时状态全部验证并恢复后才提升。
public sealed class PackFlow
{
	readonly PackReq mReq;
	readonly IPackApi mApi;

	public PackReport report { get; private set; }
	public string result => report?.detail;

	public PackFlow(PackReq req)
	{
		mReq = clone(req ?? throw new ArgumentNullException(nameof(req)));
		mApi = req.api ?? new UnityPackApi();
	}

	public string output()
	{
		string root = safeOutput(mReq.outputRoot);
		string relative = safeRelative(mReq.playerPath, "Player相对路径");
		return Path.Combine(root, osPath(relative));
	}

	public bool validate()
	{
		UpdRule.cfg(mReq.cfg);
		UpdRule.prod(mReq.cfg);
		HotList.chk(mReq.plan);
		HotList.chkCfg(mReq.cfg, mReq.plan.hot);
		if (mReq.target == BuildTarget.NoTarget || mReq.cfg.platform != platform(mReq.target))
			throw new InvalidDataException("Player BuildTarget与运行平台不一致");
		mReq.outputRoot = safeOutput(mReq.outputRoot);
		mReq.playerPath = safeRelative(mReq.playerPath, "Player相对路径");
		string player = Path.GetFullPath(Path.Combine(mReq.outputRoot, osPath(mReq.playerPath)));
		if (!inside(mReq.outputRoot, player)) throw new InvalidDataException("Player输出越过Base目录");
		if (Directory.Exists(mReq.outputRoot) || File.Exists(mReq.outputRoot))
			throw new InvalidOperationException("当前Base客户端输出已存在且不可覆盖:" + mReq.outputRoot);
		string baseline = AotBase.path(mReq.baselineRoot, mReq.cfg.env,
			mReq.cfg.baseId, mReq.target);
		if (Directory.Exists(baseline) || File.Exists(baseline))
			throw new InvalidOperationException("当前Base ID已存在AOT基线，不能重复生产客户端");
		mReq.scenes = scenes(mReq.scenes);
		if ((mReq.options & BuildOptions.BuildScriptsOnly) != 0)
			throw new BuildFailedException("完整客户端不能使用Build Scripts Only");
		if (!string.IsNullOrWhiteSpace(mReq.embedStage))
			safeTree(mReq.embedStage, "内置Stage", true);
		checkRelease();
		checkRunPath(mReq.runSetPath);
		probeWritable(Path.GetDirectoryName(mReq.outputRoot));
		probeWritable(Path.GetDirectoryName(baseline));
		mApi.validate(mReq.target);
		DllObf.chk(mReq.plan.hot, mReq.useObf);
		mReq.commitHook?.validate();
		return true;
	}

	public PackReport build()
	{
		validate();
		using PackPending pack = new(mReq.outputRoot, mReq.playerPath);
		using HybridSettingsTx hybrid = new(mReq.plan.cap);
		using RunSetTx run = new(mReq.runSetPath, mReq.cfg, HotList.aotDeny(mReq.plan.cap));
		using EmbedTx embed = new(mReq.embedStage, mReq.cfg.platform);
		using IPackCommitHook hook = mReq.commitHook;
		AotPending aot = null;
		RelBuild.RelPending release = null;
		try
		{
			using (PackBuildGuard.use())
				mApi.generateAll(mReq.target);
			BuildPlayerOptions options = new()
			{
				scenes = (string[])mReq.scenes.Clone(),
				locationPathName = pack.candidatePlayer,
				target = mReq.target,
				targetGroup = BuildPipeline.GetBuildTargetGroup(mReq.target),
				options = mReq.options | BuildOptions.DetailedBuildReport,
			};
			PackBuildResult built;
			using (PackBuildGuard.use())
				built = mApi.build(options) ??
					throw new BuildFailedException("Player构建器没有返回结果");
			if (!built.succeeded) throw new BuildFailedException("客户端构建失败:" + built.detail);
			checkArtifact(pack.candidateRoot, pack.candidatePlayer, mReq.target);
			if (embed.enabled) checkEmbed(pack.candidateRoot, pack.candidatePlayer,
				mReq.target, mReq.cfg.platform, embed.source);

			string stripped = safeTree(mApi.strippedAot(mReq.target),
				"最终Player stripped AOT", false);
			aot = AotBase.prepare(new AotBaseReq
			{
				source = stripped,
				root = mReq.baselineRoot,
				cfg = mReq.cfg,
				plan = mReq.plan,
				target = mReq.target,
				useObf = mReq.useObf,
			});
			checkPatchAot(aot.info, mReq.cfg);
			if (mReq.release != null) release = RelBuild.prepare(mReq.release);

			// 任何正式产物可见前，先恢复工程配置与StreamingAssets。
			embed.restore();
			run.restore();
			hybrid.restore();

			release?.promote();
			pack.promote();
			aot.promote();
			hook?.promote();
			string releaseId = release?.publish();
			pack.accept();
			aot.accept();
			hook?.accept();
			report = new PackReport
			{
				outputRoot = pack.finalRoot,
				player = pack.finalPlayer,
				baseline = aot.path,
				embedded = embed.enabled,
				releaseId = releaseId,
				detail = built.detail,
			};
			return report;
		}
		finally
		{
			release?.Dispose();
			aot?.Dispose();
		}
	}

	void checkRelease()
	{
		if (mReq.release == null) return;
		RelReq release = mReq.release;
		if (!release.newBase || !sameCfg(release.cfg, mReq.cfg) ||
			!samePlan(release.plan, mReq.plan))
			throw new InvalidDataException("首个Release与Player Base配置不一致");
		string stage = safeTree(release.src, "首个Release Stage", true);
		if (!string.IsNullOrWhiteSpace(mReq.embedStage) && !string.Equals(stage,
			trim(Path.GetFullPath(mReq.embedStage)), StringComparison.OrdinalIgnoreCase))
			throw new InvalidDataException("内置Stage与首个Release必须来自同一目录");
		if (!string.IsNullOrWhiteSpace(release.root) && Path.IsPathRooted(release.root))
		{
			string relRoot = trim(Path.GetFullPath(release.root));
			checkSeparate(mReq.outputRoot, relRoot, "Player输出与Release输出");
			checkSeparate(AotBase.path(mReq.baselineRoot, mReq.cfg.env,
				mReq.cfg.baseId, mReq.target), relRoot, "AOT基线与Release输出");
		}
		RelBuild.validateConfig(release);
	}

	static void checkPatchAot(AotBaseInfo info, UpdCfg cfg)
	{
		HashSet<string> frozen = new(info.dlls, StringComparer.OrdinalIgnoreCase);
		foreach (string output in cfg.aotDlls)
		{
			string name = HotList.rawName(output) + ".dll";
			if (!frozen.Contains(name)) throw new InvalidDataException(
				"Player stripped AOT缺少运行配置声明的元数据程序集:" + name);
		}
	}

	static void checkArtifact(string root, string player, BuildTarget target)
	{
		safeTree(root, "Player候选目录", false, false);
		bool fileTarget = target == BuildTarget.Android ||
			target == BuildTarget.StandaloneWindows || target == BuildTarget.StandaloneWindows64;
		if (fileTarget)
		{
			FileInfo file = new(player);
			if (!file.Exists || file.Length <= 0 || (file.Attributes & FileAttributes.ReparsePoint) != 0)
				throw new BuildFailedException("Player主产物不存在或非法:" + player);
			if (target == BuildTarget.StandaloneWindows || target == BuildTarget.StandaloneWindows64)
			{
				string data = Path.Combine(Path.GetDirectoryName(player),
					Path.GetFileNameWithoutExtension(player) + "_Data");
				safeTree(data, "Windows Player Data", false, false);
			}
		}
		else safeTree(player, "Player主产物", false, false);
	}

	static void checkEmbed(string root, string player, BuildTarget target,
		string platform, string source)
	{
		if (target == BuildTarget.Android)
		{
			using ZipArchive zip = ZipFile.OpenRead(player);
			string prefix = player.EndsWith(".aab", StringComparison.OrdinalIgnoreCase) ?
				"base/assets/" : "assets/";
			Dictionary<string, ZipArchiveEntry> packed = new(StringComparer.Ordinal);
			foreach (ZipArchiveEntry entry in zip.Entries)
			{
				if (!entry.FullName.StartsWith(prefix + platform + "/", StringComparison.Ordinal) ||
					entry.FullName.EndsWith("/", StringComparison.Ordinal)) continue;
				string rel = entry.FullName.Substring((prefix + platform + "/").Length);
				if (!packed.TryAdd(rel, entry)) throw new InvalidDataException("Player包含重复内置资源:" + rel);
			}
			Dictionary<string, string> expected = hashes(source);
			if (!packed.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(expected.Keys))
				throw new InvalidDataException("Player内置资源集合与Stage不一致");
			foreach ((string rel, string sha) in expected)
			{
				using Stream input = packed[rel].Open();
				if (streamSha(input) != sha) throw new InvalidDataException("Player内置资源校验失败:" + rel);
			}
			return;
		}

		string streaming = target switch
		{
			BuildTarget.StandaloneOSX => Path.Combine(player, "Contents", "Resources", "Data", "StreamingAssets", platform),
			BuildTarget.iOS => Path.Combine(player, "Data", "Raw", platform),
			BuildTarget.StandaloneWindows => Path.Combine(root, Path.GetFileNameWithoutExtension(player) + "_Data", "StreamingAssets", platform),
			BuildTarget.StandaloneWindows64 => Path.Combine(root, Path.GetFileNameWithoutExtension(player) + "_Data", "StreamingAssets", platform),
			_ => throw new InvalidOperationException("当前平台尚不支持内置Stage回读:" + target),
		};
		Dictionary<string, string> actual = hashes(safeTree(streaming, "Player内置资源", false));
		Dictionary<string, string> wanted = hashes(source);
		if (actual.Count != wanted.Count || actual.Any(item =>
			!wanted.TryGetValue(item.Key, out string sha) || sha != item.Value))
			throw new InvalidDataException("Player内置资源内容与Stage不一致");
	}

	static Dictionary<string, string> hashes(string root)
	{
		Dictionary<string, string> values = new(StringComparer.Ordinal);
		string full = trim(Path.GetFullPath(root));
		foreach (string file in Directory.GetFiles(full, "*", SearchOption.AllDirectories))
		{
			string rel = Path.GetRelativePath(full, file).Replace('\\', '/');
			if (rel.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) continue;
			values.Add(rel, fileSha(file));
		}
		return values;
	}

	static string[] scenes(string[] values)
	{
		string[] result = values == null || values.Length == 0 ?
			EditorBuildSettings.scenes.Where(item => item.enabled && !string.IsNullOrEmpty(item.path))
				.Select(item => item.path).ToArray() : (string[])values.Clone();
		if (result.Length == 0) throw new BuildFailedException("Build Settings没有启用场景");
		HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
		foreach (string path in result)
			if (string.IsNullOrEmpty(path) || !path.StartsWith("Assets/", StringComparison.Ordinal) ||
				!path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase) || !File.Exists(path) ||
				!seen.Add(path)) throw new BuildFailedException("构建场景不存在、重复或越界:" + path);
		return result;
	}

	static PackReq clone(PackReq req)
	{
		return new PackReq
		{
			cfg = cloneCfg(req.cfg),
			plan = clonePlan(req.plan),
			target = req.target,
			outputRoot = req.outputRoot,
			playerPath = req.playerPath,
			scenes = req.scenes == null ? null : (string[])req.scenes.Clone(),
			options = req.options,
			baselineRoot = req.baselineRoot,
			useObf = req.useObf,
			embedStage = req.embedStage,
			runSetPath = req.runSetPath,
			release = cloneRelease(req.release),
			commitHook = req.commitHook,
			api = req.api,
		};
	}

	static RelReq cloneRelease(RelReq req)
	{
		if (req == null) return null;
		return new RelReq
		{
			src = req.src,
			root = req.root,
			privateKey = req.privateKey,
			releaseId = req.releaseId,
			mapSrc = req.mapSrc,
			cfg = cloneCfg(req.cfg),
			plan = clonePlan(req.plan),
			newBase = req.newBase,
		};
	}

	static bool sameCfg(UpdCfg left, UpdCfg right)
	{
		return left != null && right != null && left.baseUrl == right.baseUrl &&
			left.env == right.env && left.platform == right.platform &&
			left.baseId == right.baseId && left.pubKey == right.pubKey &&
			left.retry == right.retry && left.timeout == right.timeout &&
			left.entryDll == right.entryDll && left.hotId == right.hotId &&
			left.secret == right.secret && left.resList == right.resList &&
			sameArray(left.aotDlls, right.aotDlls) && sameArray(left.codeDlls, right.codeDlls);
	}

	static bool samePlan(HotPlan left, HotPlan right)
	{
		return left != null && right != null && left.hot.id == right.hot.id &&
			left.hot.entry == right.hot.entry && HotList.same(left.cap, right.cap) &&
			sameArray(left.hot.dlls, right.hot.dlls) && sameArray(left.baseReq, right.baseReq);
	}

	static bool sameArray(string[] left, string[] right)
	{
		if (left == null || right == null || left.Length != right.Length) return false;
		for (int i = 0; i < left.Length; ++i) if (left[i] != right[i]) return false;
		return true;
	}

	static UpdCfg cloneCfg(UpdCfg cfg)
	{
		if (cfg == null) return null;
		return new UpdCfg
		{
			baseUrl = cfg.baseUrl, env = cfg.env, platform = cfg.platform,
			baseId = cfg.baseId, pubKey = cfg.pubKey, retry = cfg.retry,
			timeout = cfg.timeout,
			aotDlls = cfg.aotDlls == null ? null : (string[])cfg.aotDlls.Clone(),
			codeDlls = cfg.codeDlls == null ? null : (string[])cfg.codeDlls.Clone(),
			entryDll = cfg.entryDll, hotId = cfg.hotId, secret = cfg.secret,
			resList = cfg.resList,
		};
	}

	static HotPlan clonePlan(HotPlan plan)
	{
		if (plan == null) return null;
		return new HotPlan(new HotCap(plan.cap?.optAot, plan.cap?.allow),
			new HotSet(plan.hot?.dlls, plan.hot?.entry), plan.baseReq);
	}

	static string safeOutput(string value)
	{
		if (string.IsNullOrWhiteSpace(value) || !Path.IsPathRooted(value))
			throw new InvalidDataException("Player输出根目录必须是绝对路径");
		string full = trim(Path.GetFullPath(value));
		if (full == trim(Path.GetPathRoot(full))) throw new InvalidDataException("Player输出不能是文件系统根目录");
		ensurePhysical(full);
		return full;
	}

	static string safeRelative(string value, string label)
	{
		if (!UpdFmt.isPath(value)) throw new InvalidDataException(label + "非法:" + value);
		return value;
	}

	static string safeTree(string value, string label, bool rejectMeta, bool rejectEmpty = true)
	{
		if (string.IsNullOrWhiteSpace(value) || !Path.IsPathRooted(value))
			throw new InvalidDataException(label + "必须是绝对路径");
		string full = trim(Path.GetFullPath(value));
		DirectoryInfo root = new(full);
		if (!root.Exists || (root.Attributes & FileAttributes.ReparsePoint) != 0)
			throw new DirectoryNotFoundException(label + "不存在或为链接:" + full);
		ensurePhysical(full);
		foreach (DirectoryInfo dir in root.GetDirectories("*", SearchOption.AllDirectories))
			if ((dir.Attributes & FileAttributes.ReparsePoint) != 0)
				throw new InvalidDataException(label + "包含链接目录:" + dir.FullName);
		foreach (FileInfo file in root.GetFiles("*", SearchOption.AllDirectories))
			if ((file.Attributes & FileAttributes.ReparsePoint) != 0 ||
				(rejectEmpty && file.Length <= 0) ||
				(rejectMeta && file.Extension.Equals(".meta", StringComparison.OrdinalIgnoreCase)))
				throw new InvalidDataException(label + "包含空文件、链接或meta:" + file.FullName);
		return full;
	}

	static void checkRunPath(string path)
	{
		if (string.IsNullOrEmpty(path) || !path.StartsWith("Assets/", StringComparison.Ordinal) ||
			!path.EndsWith("/" + FrameBaseDefine.RUN_SET_RES + ".asset", StringComparison.Ordinal) ||
			path.IndexOf("/Resources/", StringComparison.Ordinal) < 0 ||
			!Directory.Exists(Path.GetDirectoryName(path)))
			throw new InvalidDataException("PlatRunSet路径必须位于已存在的Assets Resources目录:" + path);
	}

	static void probeWritable(string directory)
	{
		string root = trim(Path.GetFullPath(directory ?? string.Empty));
		DirectoryInfo exist = null;
		for (DirectoryInfo dir = new(root); dir != null; dir = dir.Parent)
		{
			if (!dir.Exists) continue;
			if ((dir.Attributes & FileAttributes.ReparsePoint) != 0)
				throw new InvalidDataException("生产路径不能经过链接:" + dir.FullName);
			exist ??= dir;
		}
		if (exist == null) throw new DirectoryNotFoundException("生产路径没有已存在父目录:" + root);
		string probe = Path.Combine(exist.FullName, ".pack-write-" + Guid.NewGuid().ToString("N"));
		try
		{
			using FileStream file = new(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None);
			file.WriteByte(1);
			file.Flush(true);
		}
		finally { if (File.Exists(probe)) File.Delete(probe); }
	}

	static bool inside(string root, string path)
	{
		return path.StartsWith(trim(Path.GetFullPath(root)) + Path.DirectorySeparatorChar,
			StringComparison.OrdinalIgnoreCase);
	}

	static void checkSeparate(string leftPath, string rightPath, string label)
	{
		string left = trim(Path.GetFullPath(leftPath)) + Path.DirectorySeparatorChar;
		string right = trim(Path.GetFullPath(rightPath)) + Path.DirectorySeparatorChar;
		if (left.StartsWith(right, StringComparison.OrdinalIgnoreCase) ||
			right.StartsWith(left, StringComparison.OrdinalIgnoreCase))
			throw new InvalidDataException(label + "不能相互包含");
	}

	static void ensurePhysical(string path)
	{
		for (DirectoryInfo dir = new(path); dir != null; dir = dir.Parent)
			if (dir.Exists && (dir.Attributes & FileAttributes.ReparsePoint) != 0)
				throw new InvalidDataException("生产路径不能经过链接:" + dir.FullName);
	}

	static string platform(BuildTarget target)
	{
		return target switch
		{
			BuildTarget.Android => FrameBaseDefine.ANDROID,
			BuildTarget.iOS => FrameBaseDefine.IOS,
			BuildTarget.StandaloneOSX => FrameBaseDefine.MACOS,
			BuildTarget.StandaloneWindows => FrameBaseDefine.WINDOWS,
			BuildTarget.StandaloneWindows64 => FrameBaseDefine.WINDOWS,
			_ => throw new InvalidDataException("当前BuildTarget尚未适配完整客户端生产:" + target),
		};
	}

	static string osPath(string relative)
	{
		return relative.Replace('/', Path.DirectorySeparatorChar);
	}

	static string trim(string path)
	{
		string root = Path.GetPathRoot(path);
		return path.Length > root.Length ? path.TrimEnd(Path.DirectorySeparatorChar,
			Path.AltDirectorySeparatorChar) : path;
	}

	static string fileSha(string path)
	{
		using FileStream input = File.OpenRead(path);
		using SHA256 sha = SHA256.Create();
		return Convert.ToBase64String(sha.ComputeHash(input));
	}

	static string streamSha(Stream input)
	{
		using SHA256 sha = SHA256.Create();
		return Convert.ToBase64String(sha.ComputeHash(input));
	}

	sealed class PackPending : IDisposable
	{
		string mCandidate;
		readonly string mTarget;
		bool mPromoted;
		bool mAccepted;
		public string candidateRoot => mCandidate;
		public string candidatePlayer { get; }
		public string finalRoot => mTarget;
		public string finalPlayer { get; }

		public PackPending(string target, string playerPath)
		{
			mTarget = target;
			mCandidate = target + ".candidate-" + Guid.NewGuid().ToString("N");
			if (Directory.Exists(mCandidate) || File.Exists(mCandidate))
				throw new IOException("Player候选目录冲突:" + mCandidate);
			Directory.CreateDirectory(mCandidate);
			candidatePlayer = Path.Combine(mCandidate, osPath(playerPath));
			finalPlayer = Path.Combine(mTarget, osPath(playerPath));
			Directory.CreateDirectory(Path.GetDirectoryName(candidatePlayer));
		}

		public void promote()
		{
			if (mPromoted || mAccepted) throw new InvalidOperationException("Player事务已经结束");
			if (Directory.Exists(mTarget) || File.Exists(mTarget))
				throw new IOException("Player输出在事务期间被其他产物占用:" + mTarget);
			Directory.Move(mCandidate, mTarget);
			mCandidate = null;
			mPromoted = true;
		}

		public void accept()
		{
			if (!mPromoted || mAccepted) throw new InvalidOperationException("Player尚未提升或已经确认");
			mAccepted = true;
		}

		public void Dispose()
		{
			if (mAccepted) return;
			if (mPromoted && Directory.Exists(mTarget)) Directory.Delete(mTarget, true);
			if (!string.IsNullOrEmpty(mCandidate) && Directory.Exists(mCandidate))
				Directory.Delete(mCandidate, true);
		}
	}

	sealed class HybridSettingsTx : IDisposable
	{
		readonly bool mHadFile;
		readonly AssemblyDefinitionAsset[] mDefs;
		readonly string[] mHot;
		readonly string[] mKeep;
		bool mDone;

		public HybridSettingsTx(HotCap cap)
		{
			mHadFile = File.Exists("ProjectSettings/HybridCLRSettings.asset");
			HybridSettings cfg = HybridSettings.Instance;
			mDefs = cfg.hotUpdateAssemblyDefinitions == null ? null :
				(AssemblyDefinitionAsset[])cfg.hotUpdateAssemblyDefinitions.Clone();
			mHot = cfg.hotUpdateAssemblies == null ? null : (string[])cfg.hotUpdateAssemblies.Clone();
			mKeep = cfg.preserveHotUpdateAssemblies == null ? null :
				(string[])cfg.preserveHotUpdateAssemblies.Clone();
			try
			{
				cfg.hotUpdateAssemblyDefinitions = Array.Empty<AssemblyDefinitionAsset>();
				cfg.hotUpdateAssemblies = (string[])cap.allow.Clone();
				cfg.preserveHotUpdateAssemblies = Array.Empty<string>();
				HybridSettings.Save();
				if (!SettingsUtil.HotUpdateAssemblyNamesExcludePreserved.SequenceEqual(cap.allow))
					throw new InvalidDataException("HybridCLR Hot程序集设置未按Base能力生效");
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
			HybridSettings cfg = HybridSettings.Instance;
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

	sealed class RunSetTx : IDisposable
	{
		readonly string mPath;
		readonly PlatRunSet mRun;
		readonly bool mCreated;
		readonly string mUrl;
		readonly string mEnv;
		readonly string mPlatform;
		readonly string mBase;
		readonly string mKey;
		readonly string[] mDeny;
		bool mDone;

		public RunSetTx(string path, UpdCfg cfg, string[] deny)
		{
			mPath = path;
			mRun = AssetDatabase.LoadAssetAtPath<PlatRunSet>(path);
			mCreated = mRun == null;
			if (mCreated)
			{
				if (AssetDatabase.LoadMainAssetAtPath(path) != null)
					throw new InvalidDataException("PlatRunSet路径被其他资产占用:" + path);
				mRun = ScriptableObject.CreateInstance<PlatRunSet>();
				AssetDatabase.CreateAsset(mRun, path);
			}
			else
			{
				mUrl = mRun.mBaseUrl; mEnv = mRun.mEnv; mPlatform = mRun.mPlatform;
				mBase = mRun.mBaseId; mKey = mRun.mPubKey;
				mDeny = mRun.mAotDeny == null ? null : (string[])mRun.mAotDeny.Clone();
			}
			try
			{
				set(cfg, deny);
				AssetDatabase.SaveAssets();
				PlatRunSet actual = AssetDatabase.LoadAssetAtPath<PlatRunSet>(path);
				if (actual == null || actual.mBaseUrl != cfg.baseUrl || actual.mEnv != cfg.env ||
					actual.mPlatform != cfg.platform || actual.mBaseId != cfg.baseId ||
					actual.mPubKey != cfg.pubKey || !same(actual.mAotDeny, deny))
					throw new InvalidDataException("最终Player运行配置资产写入失败");
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
			if (mCreated)
			{
				AssetDatabase.DeleteAsset(mPath);
			}
			else
			{
				PlatRunSet run = AssetDatabase.LoadAssetAtPath<PlatRunSet>(mPath);
				if (run == null) throw new InvalidDataException("原PlatRunSet资产无法重新加载:" + mPath);
				run.mBaseUrl = mUrl; run.mEnv = mEnv; run.mPlatform = mPlatform;
				run.mBaseId = mBase; run.mPubKey = mKey;
				run.mAotDeny = mDeny == null ? null : (string[])mDeny.Clone();
				EditorUtility.SetDirty(run);
				AssetDatabase.SaveAssets();
			}
			mDone = true;
		}

		void set(UpdCfg cfg, string[] deny)
		{
			mRun.mBaseUrl = cfg.baseUrl; mRun.mEnv = cfg.env;
			mRun.mPlatform = cfg.platform; mRun.mBaseId = cfg.baseId;
			mRun.mPubKey = cfg.pubKey; mRun.mAotDeny = (string[])deny.Clone();
			EditorUtility.SetDirty(mRun);
		}

		static bool same(string[] left, string[] right)
		{
			if (left == null || right == null || left.Length != right.Length) return false;
			for (int i = 0; i < left.Length; ++i) if (left[i] != right[i]) return false;
			return true;
		}

		public void Dispose() { restore(); }
	}

	sealed class EmbedTx : IDisposable
	{
		readonly string mTarget;
		readonly string mBackup;
		readonly bool mHadTarget;
		readonly bool mHadMeta;
		readonly bool mHadParent;
		readonly bool mHadParentMeta;
		bool mMovedOld;
		bool mInstalled;
		bool mDone;
		public string source { get; }
		public bool enabled => source != null;

		public EmbedTx(string sourcePath, string platform)
		{
			if (string.IsNullOrWhiteSpace(sourcePath)) return;
			source = safeTree(sourcePath, "内置Stage", true);
			string project = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
			mTarget = Path.Combine(project, "Assets", "StreamingAssets", platform);
			mBackup = Path.Combine(project, "Library", "MyFramework", "Pack", "EmbedOld-" +
				Guid.NewGuid().ToString("N"));
			mHadTarget = Directory.Exists(mTarget);
			mHadMeta = File.Exists(mTarget + ".meta");
			string parent = Path.GetDirectoryName(mTarget);
			mHadParent = Directory.Exists(parent);
			mHadParentMeta = File.Exists(parent + ".meta");
			string temp = Path.Combine(project, "Library", "MyFramework", "Pack", "EmbedNew-" +
				Guid.NewGuid().ToString("N"));
			try
			{
				copyTree(source, temp);
				Directory.CreateDirectory(Path.GetDirectoryName(mTarget));
				Directory.CreateDirectory(Path.GetDirectoryName(mBackup));
				if (mHadTarget)
				{
					Directory.Move(mTarget, mBackup);
					mMovedOld = true;
				}
				Directory.Move(temp, mTarget);
				mInstalled = true;
				AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
			}
			catch
			{
				if (Directory.Exists(temp)) Directory.Delete(temp, true);
				restore();
				throw;
			}
		}

		public void restore()
		{
			if (mDone || !enabled) return;
			if (mInstalled && Directory.Exists(mTarget)) Directory.Delete(mTarget, true);
			if (mMovedOld)
			{
				if (!Directory.Exists(mBackup)) throw new DirectoryNotFoundException("原StreamingAssets备份丢失");
				Directory.Move(mBackup, mTarget);
			}
			if (!mHadMeta && File.Exists(mTarget + ".meta")) File.Delete(mTarget + ".meta");
			string parent = Path.GetDirectoryName(mTarget);
			if (!mHadParent && Directory.Exists(parent) &&
				Directory.GetFileSystemEntries(parent).Length == 0) Directory.Delete(parent);
			if (!mHadParentMeta && File.Exists(parent + ".meta")) File.Delete(parent + ".meta");
			AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
			mDone = true;
		}

		public void Dispose() { restore(); }
	}

	static void copyTree(string source, string target)
	{
		Directory.CreateDirectory(target);
		foreach (string file in Directory.GetFiles(source, "*", SearchOption.TopDirectoryOnly))
		{
			if (file.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) continue;
			string dst = Path.Combine(target, Path.GetFileName(file));
			File.Copy(file, dst, false);
			if (fileSha(file) != fileSha(dst)) throw new IOException("内置Stage复制校验失败:" + file);
		}
		foreach (string child in Directory.GetDirectories(source, "*", SearchOption.TopDirectoryOnly))
			copyTree(child, Path.Combine(target, Path.GetFileName(child)));
	}
}

// 配置了HybridCLR的项目，非Development客户端必须走PackFlow，否则Player
// 与AOT基线无法形成同一事务。未配置HybridCLR的旧项目不受影响。
internal sealed class PackBuildGuard : IPreprocessBuildWithReport
{
	static int sDepth;
	public int callbackOrder => -10000;

	internal static IDisposable use()
	{
		++sDepth;
		return new Gate();
	}

	public void OnPreprocessBuild(BuildReport report)
	{
		if (sDepth > 0 || !File.Exists("ProjectSettings/HybridCLRSettings.asset")) return;
		if ((report.summary.options & BuildOptions.Development) == 0)
			throw new BuildFailedException(
				"配置HybridCLR后的正式客户端必须使用PackFlow，以事务冻结AOT基线");
		Debug.LogWarning("当前是Unity直接构建的Development诊断包，没有可发布AOT基线。");
	}

	sealed class Gate : IDisposable
	{
		bool mDone;
		public void Dispose()
		{
			if (mDone) return;
			mDone = true;
			--sDepth;
		}
	}
}
