using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using HybridCLR.Editor.Commands;
using Mono.Cecil;
using Mono.Cecil.Cil;
using UnityEditor;
using UnityEngine;

public sealed class DllProdReq
{
	public UpdCfg cfg;
	public HotPlan plan;
	public BuildTarget target;
	// 为空时调用HybridCLR的CompilePlayerScripts入口；测试或CI可传入已编译目录。
	public string compiledDir;
	// 为空时读取项目 HybridCLRData/AOTBaselines。
	public string baselineRoot;
	// 首包生产可传入尚未公开的候选AOT基线；为空时读取已冻结Base。
	public AotBaseInfo baseline;
	public bool development;
	public bool useObf;
	// 正式生产默认分析最终DLL；测试夹具或只做结构诊断时可显式关闭。
	public bool analyzeMetadata = true;
}

public sealed class DllReport
{
	public string stage;
	public string baseline;
	public string[] codeDlls;
	public string[] aotDlls;
	public string mapPath;
}

public sealed class DllProd
{
	readonly DllProdReq mReq;
	AotBaseInfo mBase;

	// 保留ArcadeHub生产器中常用的字段名，项目适配器迁入时无需再包一层状态。
	public BuildTarget mTarget;
	public string mAssetBundleFullPath;
	public string mName;
	public string mEnv;
	public string mHotUpdateBaseId;
	internal UpdCfg cfg => mReq.cfg;
	internal HotPlan plan => mReq.plan;

	public DllProd(DllProdReq req)
	{
		mReq = clone(req ?? throw new ArgumentNullException(nameof(req)));
		mTarget = mReq.target;
		mEnv = mReq.cfg?.env;
		mHotUpdateBaseId = mReq.cfg?.baseId;
		mName = platform(mTarget);
	}

	public bool check()
	{
		checkReq();
		mBase = mReq.baseline ?? AotBase.inspect(mReq.baselineRoot, mReq.cfg,
			mReq.plan, mReq.target, mReq.useObf);
		checkBaseline(mBase);
		checkAot(mBase);
		if (!string.IsNullOrWhiteSpace(mReq.compiledDir))
			checkCompiled(safeDir(mReq.compiledDir, "热更DLL编译目录"));
		return true;
	}

	// generateAll属于完整Player构建事务，不能在补丁生产中隐式触发。
	public bool buildHotFix(bool generateAll, bool isDebug = false)
	{
		if (generateAll) throw new InvalidOperationException(
			"AOT基线只能由完整客户端构建生成，不能由补丁生产器单独提交");
		if (string.IsNullOrWhiteSpace(mAssetBundleFullPath))
			throw new InvalidOperationException("请先配置DLL生产Stage目录");
		make(mAssetBundleFullPath, isDebug);
		return true;
	}

	public DllReport make(string stage, bool isDebug = false)
	{
		check();
		string target = safeDir(stage, "DLL生产Stage");
		string source = null;
		bool temporary = false;
		string candidate = target + ".managed-" + Guid.NewGuid().ToString("N");
		try
		{
			if (string.IsNullOrWhiteSpace(mReq.compiledDir))
			{
				source = compile();
				temporary = true;
			}
			else source = safeDir(mReq.compiledDir, "热更DLL编译目录");
			checkCompiled(source);
			Directory.CreateDirectory(candidate);
			produce(candidate, source, isDebug);
			validateManaged(candidate);
			if (mReq.analyzeMetadata)
			{
				DllMetaReport meta = DllMeta.analyzeCandidate(candidate, mBase,
					mReq.plan.hot, mReq.target);
				DllMeta.requireConfigured(mReq.cfg.aotDlls, meta.aotDlls);
			}
			using ManagedCommit commit = new(candidate, target);
			commit.promote();
			candidate = null;
			return new DllReport
			{
				stage = target,
				baseline = mBase.path,
				codeDlls = (string[])mReq.cfg.codeDlls.Clone(),
				aotDlls = (string[])mReq.cfg.aotDlls.Clone(),
				mapPath = mReq.useObf ? DllObf.mapPath() : null,
			};
		}
		finally
		{
			if (!string.IsNullOrEmpty(candidate) && Directory.Exists(candidate))
				Directory.Delete(candidate, true);
			if (temporary && !string.IsNullOrEmpty(source) && Directory.Exists(source))
				Directory.Delete(source, true);
		}
	}

	public IReadOnlyList<string> getAot()
	{
		check();
		return (string[])mBase.dlls.Clone();
	}

	void checkReq()
	{
		if (mReq.cfg == null) throw new ArgumentNullException(nameof(mReq.cfg));
		if (mReq.plan == null) throw new ArgumentNullException(nameof(mReq.plan));
		UpdRule.prod(mReq.cfg);
		HotList.chk(mReq.plan);
		HotList.chkCfg(mReq.cfg, mReq.plan.hot);
		if (mReq.target == BuildTarget.NoTarget || mReq.cfg.platform != platform(mReq.target))
			throw new InvalidDataException("DLL生产BuildTarget与发布平台不一致");
		if (mReq.useObf != (mReq.cfg.secret == FrameBaseDefine.DYNAMIC_SECRET_FILE))
			throw new InvalidDataException("Obfuz生产与运行配置的动态密钥文件不一致");
		DllObf.chk(mReq.plan.hot, mReq.useObf);
	}

	void checkAot(AotBaseInfo info)
	{
		HashSet<string> frozen = new(info.dlls, StringComparer.OrdinalIgnoreCase);
		HashSet<string> hot = new(StringComparer.OrdinalIgnoreCase);
		foreach (string file in mReq.plan.hot.dlls) hot.Add(HotList.rawName(file));
		foreach (string file in info.dlls)
			if (hot.Contains(Path.GetFileNameWithoutExtension(file)))
				throw new InvalidDataException("Release热更程序集与Base AOT同名:" + file);
		foreach (string name in mReq.plan.baseReq)
			if (!frozen.Contains(name + ".dll"))
				throw new InvalidDataException("AB MonoScript程序集不在目标Base AOT中，必须发布新Base:" + name);
		foreach (string output in mReq.cfg.aotDlls)
		{
			string name = HotList.rawName(output);
			if (!frozen.Contains(name + ".dll"))
				throw new InvalidDataException("补丁需要的AOT元数据不在冻结Base中:" + output);
		}
	}

	void checkBaseline(AotBaseInfo info)
	{
		if (info?.dlls == null || info.cap == null ||
			string.IsNullOrWhiteSpace(info.path) || info.baseUrl != mReq.cfg.baseUrl ||
			info.pubKey != mReq.cfg.pubKey ||
			info.contentAddressed != mReq.cfg.contentAddressed ||
			!HotList.same(info.cap, mReq.plan.cap))
			throw new InvalidDataException("候选AOT基线与DLL生产计划不一致");
		_ = safeDir(info.path, "AOT基线");
	}

	string compile()
	{
		string project = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
		string root = Path.Combine(project, "Library", "MyFramework", "HotUpd");
		Directory.CreateDirectory(root);
		string output = Path.Combine(root, "Compile-" + Guid.NewGuid().ToString("N"));
		try
		{
			CompileDllCommand.CompileDll(output, mReq.target, mReq.development);
			return safeDir(output, "HybridCLR热更编译输出");
		}
		catch
		{
			if (Directory.Exists(output)) Directory.Delete(output, true);
			throw;
		}
	}

	void checkCompiled(string source)
	{
		foreach (string output in mReq.plan.hot.dlls)
		{
			string expected = HotList.rawName(output);
			checkDll(Path.Combine(source, expected + ".dll"), expected);
		}
	}

	void produce(string candidate, string source, bool isDebug)
	{
		foreach (string output in mReq.plan.hot.dlls)
		{
			string name = HotList.rawName(output);
			copyChecked(Path.Combine(source, name + ".dll"), Path.Combine(candidate, output));
		}
		foreach (string output in mReq.cfg.aotDlls)
		{
			string name = HotList.rawName(output);
			copyChecked(Path.Combine(mBase.path, name + ".dll"), Path.Combine(candidate, output));
		}
		DllObf.run(candidate, mBase.path, mReq.plan.hot, mReq.useObf, isDebug);
	}

	void validateManaged(string root)
	{
		HashSet<string> expected = new(StringComparer.OrdinalIgnoreCase);
		foreach (string output in mReq.plan.hot.dlls)
		{
			expected.Add(output);
			checkDll(Path.Combine(root, output), HotList.rawName(output));
		}
		foreach (string output in mReq.cfg.aotDlls)
		{
			expected.Add(output);
			checkIdentity(Path.Combine(root, output), HotList.rawName(output), false);
		}
		if (mReq.useObf) expected.Add(FrameBaseDefine.DYNAMIC_SECRET_FILE);
		foreach (string path in Directory.GetFiles(root, "*", SearchOption.TopDirectoryOnly))
			if (!expected.Contains(Path.GetFileName(path)))
				throw new InvalidDataException("DLL生产器产生了未声明文件:" + path);
		foreach (string name in expected)
		{
			FileInfo file = new(Path.Combine(root, name));
			if (!file.Exists || file.Length <= 0 || (file.Attributes & FileAttributes.ReparsePoint) != 0)
				throw new InvalidDataException("DLL生产结果缺失或非法:" + name);
		}
		if (Directory.GetDirectories(root).Length != 0)
			throw new InvalidDataException("DLL生产候选不得包含子目录");
	}

	internal static void checkDll(string path, string expected)
	{
		checkIdentity(path, expected, true);
	}

	static void checkIdentity(string path, string expected, bool hotRules)
	{
		FileInfo file = new(path);
		if (!file.Exists || file.Length <= 0 || (file.Attributes & FileAttributes.ReparsePoint) != 0)
			throw new FileNotFoundException("热更DLL不存在或非法", path);
		try
		{
			using AssemblyDefinition asm = AssemblyDefinition.ReadAssembly(path,
				new ReaderParameters { ReadingMode = ReadingMode.Deferred, ReadSymbols = false });
			if (asm.Name.Name != expected)
				throw new InvalidDataException("DLL内部程序集名不匹配:" + expected);
			if (hotRules)
				foreach (ModuleDefinition module in asm.Modules)
					foreach (TypeDefinition type in module.Types) checkType(type, expected);
		}
		catch (InvalidDataException) { throw; }
		catch (Exception ex) { throw new InvalidDataException("热更DLL无法读取:" + path, ex); }
	}

	static void checkType(TypeDefinition type, string asmName)
	{
		foreach (MethodDefinition method in type.Methods)
		{
			if (method.IsPInvokeImpl) badNative(asmName, type, method, "P/Invoke");
			foreach (CustomAttribute attr in method.CustomAttributes)
			{
				string name = attr.AttributeType.Name;
				if (name == "MonoPInvokeCallbackAttribute" ||
					name == "ReversePInvokeWrapperGenerationAttribute")
					badNative(asmName, type, method, "反向P/Invoke包装特性");
			}
			if (!method.HasBody) continue;
			foreach (Instruction instruction in method.Body.Instructions)
				if (instruction.OpCode.Code == Code.Calli)
					badNative(asmName, type, method, "IL calli");
		}
		foreach (TypeDefinition child in type.NestedTypes) checkType(child, asmName);
	}

	static void badNative(string asm, TypeDefinition type, MethodDefinition method, string kind)
	{
		throw new InvalidDataException("热更程序集包含" + kind + "，必须改为AOT并发布新Base:" +
			asm + "/" + type.FullName + "." + method.Name);
	}

	static void copyChecked(string source, string target)
	{
		FileInfo input = new(source);
		if (!input.Exists || input.Length <= 0 || (input.Attributes & FileAttributes.ReparsePoint) != 0)
			throw new FileNotFoundException("DLL生产输入文件不存在或非法", source);
		Directory.CreateDirectory(Path.GetDirectoryName(target));
		File.Copy(source, target, false);
		FileInfo output = new(target);
		if (!output.Exists || output.Length != input.Length || fileSha(source) != fileSha(target))
			throw new IOException("DLL生产文件复制校验失败:" + source);
	}

	static string fileSha(string path)
	{
		using FileStream input = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
		using SHA256 sha = SHA256.Create();
		return Convert.ToBase64String(sha.ComputeHash(input));
	}

	static DllProdReq clone(DllProdReq req)
	{
		return new DllProdReq
		{
			cfg = cloneCfg(req.cfg),
			plan = clonePlan(req.plan),
			target = req.target,
			compiledDir = req.compiledDir,
			baselineRoot = req.baselineRoot,
			baseline = cloneBase(req.baseline),
			development = req.development,
			useObf = req.useObf,
			analyzeMetadata = req.analyzeMetadata,
		};
	}

	static AotBaseInfo cloneBase(AotBaseInfo value)
	{
		if (value == null) return null;
		return new AotBaseInfo
		{
			path = value.path,
			dlls = value.dlls == null ? null : (string[])value.dlls.Clone(),
			cap = value.cap == null ? null : new HotCap(value.cap.optAot, value.cap.allow),
			obfCap = value.obfCap,
			baseUrl = value.baseUrl,
			pubKey = value.pubKey,
			contentAddressed = value.contentAddressed,
		};
	}

	static UpdCfg cloneCfg(UpdCfg cfg)
	{
		if (cfg == null) return null;
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

	static HotPlan clonePlan(HotPlan plan)
	{
		if (plan == null) return null;
		HotCap cap = new(plan.cap?.optAot, plan.cap?.allow);
		HotSet hot = new(plan.hot?.dlls, plan.hot?.entry);
		return new HotPlan(cap, hot, plan.baseReq);
	}

	static string safeDir(string value, string label)
	{
		if (string.IsNullOrWhiteSpace(value) || !Path.IsPathRooted(value))
			throw new InvalidDataException(label + "必须是绝对路径");
		string full = trim(Path.GetFullPath(value));
		DirectoryInfo dir = new(full);
		if (!dir.Exists || (dir.Attributes & FileAttributes.ReparsePoint) != 0)
			throw new DirectoryNotFoundException(label + "不存在或为符号链接:" + full);
		for (DirectoryInfo cur = dir; cur != null; cur = cur.Parent)
			if (cur.Exists && (cur.Attributes & FileAttributes.ReparsePoint) != 0)
				throw new InvalidDataException(label + "路径不能经过符号链接:" + cur.FullName);
		return full;
	}

	static string trim(string path)
	{
		string root = Path.GetPathRoot(path);
		return path.Length > root.Length ? path.TrimEnd(Path.DirectorySeparatorChar,
			Path.AltDirectorySeparatorChar) : path;
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

	sealed class ManagedCommit : IDisposable
	{
		readonly string mCandidate;
		readonly string mStage;
		readonly string mBackup;
		readonly List<string> mInstalled = new();
		bool mPromoted;

		public ManagedCommit(string candidate, string stage)
		{
			mCandidate = candidate;
			mStage = stage;
			mBackup = stage + ".managed-backup-" + Guid.NewGuid().ToString("N");
		}

		public void promote()
		{
			Directory.CreateDirectory(mBackup);
			try
			{
				foreach (string file in managedFiles(mStage))
					File.Move(file, Path.Combine(mBackup, Path.GetFileName(file)));
				foreach (string file in Directory.GetFiles(mCandidate, "*", SearchOption.TopDirectoryOnly))
				{
					string target = Path.Combine(mStage, Path.GetFileName(file));
					File.Move(file, target);
					mInstalled.Add(target);
				}
				Directory.Delete(mCandidate);
				mPromoted = true;
			}
			catch
			{
				rollback();
				throw;
			}
		}

		public void Dispose()
		{
			if (mPromoted)
			{
				if (Directory.Exists(mBackup)) Directory.Delete(mBackup, true);
				return;
			}
			rollback();
		}

		void rollback()
		{
			foreach (string file in mInstalled) if (File.Exists(file)) File.Delete(file);
			mInstalled.Clear();
			if (Directory.Exists(mBackup))
			{
				foreach (string file in Directory.GetFiles(mBackup))
				{
					string target = Path.Combine(mStage, Path.GetFileName(file));
					if (!File.Exists(target)) File.Move(file, target);
				}
				Directory.Delete(mBackup, true);
			}
		}

		static IEnumerable<string> managedFiles(string root)
		{
			foreach (string file in Directory.GetFiles(root, "*.dll.bytes", SearchOption.TopDirectoryOnly))
				yield return file;
			foreach (string file in Directory.GetFiles(root, "*.dll", SearchOption.TopDirectoryOnly))
				yield return file;
			string secret = Path.Combine(root, FrameBaseDefine.DYNAMIC_SECRET_FILE);
			if (File.Exists(secret)) yield return secret;
		}
	}
}
