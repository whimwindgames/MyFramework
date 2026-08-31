using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

// 通用生产顺序。项目适配器可以在各区间内增加自己的步骤，但不能依赖注册顺序。
public static class ProdOrder
{
	public const int ASSET_BUNDLE = 100;
	public const int MANAGED_CODE = 200;
	public const int FINALIZE = 300;
}

public interface IProdStep
{
	string name { get; }
	int order { get; }
	void check(ProdCtx ctx);
	void run(ProdCtx ctx);
}

public sealed class ProdCtx
{
	public string stage { get; }
	public string env { get; }
	public string platform { get; }
	public string baseId { get; }
	public HotPlan plan { get; }
	public bool candidate { get; }

	internal ProdCtx(string stage, RelReq release, bool candidate)
	{
		this.stage = stage;
		env = release.cfg.env;
		platform = release.cfg.platform;
		baseId = release.cfg.baseId;
		plan = release.plan;
		this.candidate = candidate;
	}
}

public sealed class ProdReq
{
	// 正式Stage缓存目录。生产过程只写同父目录候选，成功后才原子替换这里。
	public string stage;
	public RelReq release;
	public IProdStep[] steps;
	// 增量步骤需要读取旧Stage时开启；完整AB + DLL生产应保持false。
	public bool keepStage;
}

// 将项目专用AB/HybridCLR实现编排为统一的Stage -> Release事务。
// check / preview / makeAll沿用ArcadeHub生产入口名称。
public sealed class ProdFlow
{
	readonly string mStage;
	readonly RelReq mRelease;
	readonly IProdStep[] mSteps;
	readonly bool mKeepStage;

	public string current { get; private set; }

	public ProdFlow(ProdReq req)
	{
		if (req == null) throw new ArgumentNullException(nameof(req));
		mStage = safeStage(req.stage);
		mRelease = clone(req.release ?? throw new ArgumentNullException(nameof(req.release)));
		mSteps = order(req.steps);
		mKeepStage = req.keepStage;
	}

	public bool check()
	{
		checkBoundary(mStage, mRelease.root);
		RelBuild.validateConfig(clone(mRelease));
		ProdCtx ctx = new(mStage, mRelease, false);
		foreach (IProdStep step in mSteps)
		{
			current = "check:" + step.name;
			step.check(ctx);
		}
		current = null;
		return true;
	}

	// 预览已经存在的正式Stage，不执行生产步骤。
	public RelView preview()
	{
		check();
		if (!Directory.Exists(mStage))
			throw new DirectoryNotFoundException("Stage不存在，无法预览:" + mStage);
		RelReq release = clone(mRelease);
		release.src = mStage;
		current = "preview";
		try { return RelBuild.preview(release); }
		finally { current = null; }
	}

	public string makeAll()
	{
		check();
		using ProdGate gate = new(mStage);
		string candidate = mStage + ".candidate-" + Guid.NewGuid().ToString("N");
		try
		{
			seed(candidate);
			ProdCtx ctx = new(candidate, mRelease, true);
			foreach (IProdStep step in mSteps)
			{
				current = "run:" + step.name;
				step.run(ctx);
				checkTree(candidate);
			}

			RelReq release = clone(mRelease);
			release.src = candidate;
			current = "prepare-release";
			using RelBuild.RelPending pending = RelBuild.prepare(release);
			using StageCommit stage = new(candidate, mStage);
			current = "promote-release";
			pending.promote();
			current = "promote-stage";
			stage.promote();
			current = "publish-release";
			string created = pending.publish();
			stage.accept();
			mRelease.releaseId = created;
			mRelease.newBase = false;
			return created;
		}
		finally
		{
			current = null;
			if (Directory.Exists(candidate))
			{
				try { Directory.Delete(candidate, true); }
				catch (Exception ex) { Debug.LogWarning("生产候选目录清理失败:" + ex.Message); }
			}
		}
	}

	void seed(string candidate)
	{
		if (File.Exists(candidate) || Directory.Exists(candidate))
			throw new IOException("生产候选路径冲突:" + candidate);
		if (mKeepStage && Directory.Exists(mStage)) copyTree(mStage, candidate);
		else Directory.CreateDirectory(candidate);
		checkTree(candidate);
	}

	static IProdStep[] order(IProdStep[] value)
	{
		if (value == null || value.Length == 0)
			throw new InvalidDataException("生产步骤不能为空");
		IProdStep[] steps = (IProdStep[])value.Clone();
		Array.Sort(steps, (left, right) =>
		{
			if (left == null || right == null) return left == null ? -1 : 1;
			int result = left.order.CompareTo(right.order);
			return result != 0 ? result : string.CompareOrdinal(left.name, right.name);
		});
		HashSet<string> names = new(StringComparer.Ordinal);
		for (int i = 0; i < steps.Length; ++i)
		{
			IProdStep step = steps[i];
			if (step == null || !UpdFmt.isId(step.name) || step.order < 0 ||
				!names.Add(step.name))
				throw new InvalidDataException("生产步骤身份非法或重复");
			if (i > 0 && steps[i - 1].order == step.order)
				throw new InvalidDataException("生产步骤顺序值必须唯一:" + step.order);
		}
		return steps;
	}

	static string safeStage(string value)
	{
		if (string.IsNullOrWhiteSpace(value) || !Path.IsPathRooted(value))
			throw new InvalidDataException("请配置绝对Stage目录");
		string full = trim(Path.GetFullPath(value));
		if (full == trim(Path.GetPathRoot(full)) || File.Exists(full))
			throw new InvalidDataException("Stage不能是文件系统根目录或普通文件:" + full);
		ensureNoLinks(full, true);
		return full;
	}

	static void checkBoundary(string stage, string releaseRoot)
	{
		if (string.IsNullOrWhiteSpace(releaseRoot) || !Path.IsPathRooted(releaseRoot)) return;
		string left = stage + Path.DirectorySeparatorChar;
		string right = trim(Path.GetFullPath(releaseRoot)) + Path.DirectorySeparatorChar;
		if (left.StartsWith(right, StringComparison.OrdinalIgnoreCase) ||
			right.StartsWith(left, StringComparison.OrdinalIgnoreCase))
			throw new InvalidDataException("Stage与Release输出目录不能相互包含");
	}

	static void checkTree(string root)
	{
		DirectoryInfo dir = new(root);
		if (!dir.Exists || (dir.Attributes & FileAttributes.ReparsePoint) != 0)
			throw new InvalidDataException("Stage不存在或为符号链接:" + root);
		foreach (FileSystemInfo info in dir.GetFileSystemInfos())
		{
			if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
				throw new InvalidDataException("Stage包含符号链接:" + info.FullName);
			if (info is DirectoryInfo child) checkTree(child.FullName);
		}
	}

	static void copyTree(string source, string target)
	{
		checkTree(source);
		Directory.CreateDirectory(target);
		foreach (string file in Directory.GetFiles(source, "*", SearchOption.TopDirectoryOnly))
			File.Copy(file, Path.Combine(target, Path.GetFileName(file)), false);
		foreach (string child in Directory.GetDirectories(source, "*", SearchOption.TopDirectoryOnly))
			copyTree(child, Path.Combine(target, Path.GetFileName(child)));
	}

	static void ensureNoLinks(string path, bool allowMissing)
	{
		DirectoryInfo dir = new(path);
		if (!dir.Exists && !allowMissing)
			throw new DirectoryNotFoundException("目录不存在:" + path);
		for (DirectoryInfo cur = dir; cur != null; cur = cur.Parent)
			if (cur.Exists && (cur.Attributes & FileAttributes.ReparsePoint) != 0)
				throw new InvalidDataException("生产路径不能经过符号链接:" + cur.FullName);
	}

	static string trim(string path)
	{
		string root = Path.GetPathRoot(path);
		return path.Length > root.Length ? path.TrimEnd(Path.DirectorySeparatorChar,
			Path.AltDirectorySeparatorChar) : path;
	}

	static RelReq clone(RelReq req)
	{
		if (req == null) throw new ArgumentNullException(nameof(req));
		return new RelReq
		{
			src = req.src,
			root = req.root,
			privateKey = req.privateKey,
			privateKeyPassword = req.privateKeyPassword,
			releaseId = req.releaseId,
			mapSrc = req.mapSrc,
			cfg = clone(req.cfg),
			plan = req.plan,
			newBase = req.newBase,
		};
	}

	static UpdCfg clone(UpdCfg cfg)
	{
		if (cfg == null) throw new ArgumentNullException(nameof(cfg));
		return new UpdCfg
		{
			baseUrl = cfg.baseUrl,
			env = cfg.env,
			platform = cfg.platform,
			baseId = cfg.baseId,
			pubKey = cfg.pubKey,
			contentAddressed = cfg.contentAddressed,
			retry = cfg.retry,
			timeout = cfg.timeout,
			aotDlls = cfg.aotDlls == null ? null : (string[])cfg.aotDlls.Clone(),
			codeDlls = cfg.codeDlls == null ? null : (string[])cfg.codeDlls.Clone(),
			entryDll = cfg.entryDll,
			hotId = cfg.hotId,
			secret = cfg.secret,
			resList = cfg.resList,
		};
	}

	sealed class ProdGate : IDisposable
	{
		readonly string mPath;
		FileStream mFile;

		public ProdGate(string stage)
		{
			string parent = Path.GetDirectoryName(stage);
			Directory.CreateDirectory(parent);
			ensureNoLinks(parent, false);
			mPath = stage + ".prod.lock";
			try { mFile = new FileStream(mPath, FileMode.OpenOrCreate, FileAccess.ReadWrite,
				FileShare.None); }
			catch (IOException ex) { throw new IOException("已有生产进程持有Stage锁", ex); }
		}

		public void Dispose()
		{
			mFile?.Dispose();
			mFile = null;
		}
	}

	sealed class StageCommit : IDisposable
	{
		readonly string mCandidate;
		readonly string mTarget;
		readonly string mBackup;
		bool mHadTarget;
		bool mPromoted;
		bool mAccepted;

		public StageCommit(string candidate, string target)
		{
			mCandidate = candidate;
			mTarget = target;
			mBackup = target + ".backup-" + Guid.NewGuid().ToString("N");
			if (!Directory.Exists(candidate))
				throw new DirectoryNotFoundException("Stage候选不存在:" + candidate);
			if (!string.Equals(Path.GetDirectoryName(candidate), Path.GetDirectoryName(target),
				StringComparison.OrdinalIgnoreCase))
				throw new InvalidDataException("Stage候选必须与正式目录位于同一父目录");
		}

		public void promote()
		{
			if (mPromoted || mAccepted) throw new InvalidOperationException("Stage事务已经结束");
			mHadTarget = Directory.Exists(mTarget);
			if (mHadTarget) Directory.Move(mTarget, mBackup);
			try
			{
				Directory.Move(mCandidate, mTarget);
				mPromoted = true;
			}
			catch
			{
				if (mHadTarget && Directory.Exists(mBackup) && !Directory.Exists(mTarget))
					Directory.Move(mBackup, mTarget);
				throw;
			}
		}

		public void accept()
		{
			if (!mPromoted || mAccepted) throw new InvalidOperationException("Stage尚未提升或已经确认");
			mAccepted = true;
			try { if (Directory.Exists(mBackup)) Directory.Delete(mBackup, true); }
			catch (Exception ex) { Debug.LogWarning("旧Stage备份清理失败:" + ex.Message); }
		}

		public void Dispose()
		{
			if (mAccepted) return;
			try
			{
				if (mPromoted && Directory.Exists(mTarget)) Directory.Delete(mTarget, true);
				else if (!mPromoted && Directory.Exists(mCandidate)) Directory.Delete(mCandidate, true);
				if (mHadTarget && Directory.Exists(mBackup)) Directory.Move(mBackup, mTarget);
			}
			catch (Exception ex)
			{
				Debug.LogError("Stage回滚失败，请停止生产并人工恢复:" + mBackup + ", " + ex);
			}
			mAccepted = true;
		}
	}
}
