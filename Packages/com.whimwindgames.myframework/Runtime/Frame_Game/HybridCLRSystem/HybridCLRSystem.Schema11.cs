using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using Cysharp.Threading.Tasks;
#if USE_HYBRID_CLR && !UNITY_EDITOR
using HybridCLR;
#endif
using static FrameBaseDefine;
using static FrameBaseUtility;

// Schema 11启动桥。旧launchHotFix保留在原文件中，两条链共用同一个单次启动门。
public partial class HybridCLRSystem
{
	private sealed class SchemaLoadSet
	{
		public readonly List<byte[]> aot = new();
		public readonly List<byte[]> code = new();
		public byte[] secret;
	}

	private static int sRun;

	public static async UniTask<UpdRet<bool>> launch(UpdRes res,
		CancellationToken ct = default, Func<CancellationToken, UniTask> hand = null)
	{
		if (mHotFixLaunched || Interlocked.CompareExchange(ref sRun, 1, 0) != 0)
		{
			return schemaFail(UpdCode.Busy, "hotfix_run");
		}
		bool latched = false;
		try
		{
			if (res == null)
			{
				return schemaFail(UpdCode.Load, "upd_res");
			}
#if !UNITY_EDITOR && !USE_HYBRID_CLR
			return schemaFail(UpdCode.Compat, "hybridclr_disabled");
#else
			UpdCfg cfg = res.getCfg();
			HashSet<string> hot = schemaCheckHot(cfg);
			schemaCheckPreload(hot);
			SchemaLoadSet data = await schemaReadAll(cfg, res, ct);
			ct.ThrowIfCancellationRequested();
			Volatile.Write(ref sRun, 2);
			latched = true;

			Assembly entryAsm = null;
			List<Assembly> loaded = new();
#if UNITY_EDITOR
			for (int i = 0; i < cfg.codeDlls.Length; ++i)
			{
				Assembly asm = schemaFindAssembly(schemaAssemblyName(cfg.codeDlls[i]));
				if (asm == null)
				{
					return schemaFail(UpdCode.Load, "hotfix_asm");
				}
				if (string.Equals(cfg.codeDlls[i], cfg.entryDll, StringComparison.OrdinalIgnoreCase))
				{
					entryAsm = asm;
				}
				loaded.Add(asm);
			}
#else
			schemaLoadAot(cfg.aotDlls, data.aot);
			for (int i = 0; i < data.code.Count; ++i)
			{
				Assembly asm = Assembly.Load(data.code[i]);
				if (string.Equals(cfg.codeDlls[i], cfg.entryDll, StringComparison.OrdinalIgnoreCase))
				{
					entryAsm = asm;
				}
				loaded.Add(asm);
			}
#endif
			schemaCheckAssemblies(cfg, loaded);
			if (entryAsm == null)
			{
				return schemaFail(UpdCode.Load, "hotfix_asm");
			}
			schemaBindResource(res);
			await schemaStartHot(entryAsm, data.secret, ct);
			res.markHealthy();
			if (hand != null)
			{
				await hand(ct);
			}
			schemaDestroyAot();
			return UpdRet<bool>.pass(true);
#endif
		}
		catch (OperationCanceledException ex)
		{
			return UpdRet<bool>.fail(new UpdErr(UpdCode.Cancel, "hotfix", UpdPhase.Load, ex));
		}
		catch (UpdBad bad)
		{
			return UpdRet<bool>.fail(bad.err);
		}
		catch (Exception ex)
		{
			return UpdRet<bool>.fail(new UpdErr(UpdCode.Load, "hotfix", UpdPhase.Load, ex));
		}
		finally
		{
			if (!latched)
			{
				Volatile.Write(ref sRun, 0);
			}
		}
	}

#if UNITY_EDITOR
	public static async UniTask<UpdRet<bool>> launchEdit(CancellationToken ct = default,
		Func<CancellationToken, UniTask> hand = null)
	{
		if (mHotFixLaunched || Interlocked.CompareExchange(ref sRun, 1, 0) != 0)
		{
			return schemaFail(UpdCode.Busy, "hotfix_run");
		}
		bool latched = false;
		try
		{
			Assembly entryAsm = schemaFindAssembly(HOTFIX);
			if (entryAsm == null)
			{
				return schemaFail(UpdCode.Load, "hotfix_asm");
			}
			FrameCrossParam.mLang = ResLocalizationText.mCurLanguage;
			FrameCrossParam.mVer = "editor";
			FrameCrossParam.mLocalizationName = FrameCrossParam.mLang;
			FrameCrossParam.mPersistentDataVersion = FrameCrossParam.mVer;
			FrameCrossParam.mReadPath = null;
			FrameCrossParam.mReadPathA = null;
			Volatile.Write(ref sRun, 2);
			latched = true;
			await schemaStartHot(entryAsm, Array.Empty<byte>(), ct);
			if (hand != null)
			{
				await hand(ct);
			}
			schemaDestroyAot();
			return UpdRet<bool>.pass(true);
		}
		catch (OperationCanceledException ex)
		{
			return UpdRet<bool>.fail(new UpdErr(UpdCode.Cancel, "hotfix", UpdPhase.Load, ex));
		}
		catch (UpdBad bad)
		{
			return UpdRet<bool>.fail(bad.err);
		}
		catch (Exception ex)
		{
			return UpdRet<bool>.fail(new UpdErr(UpdCode.Load, "hotfix", UpdPhase.Load, ex));
		}
		finally
		{
			if (!latched)
			{
				Volatile.Write(ref sRun, 0);
			}
		}
	}
#endif

	private static UniTask<SchemaLoadSet> schemaReadAll(UpdCfg cfg, UpdRes res, CancellationToken ct)
	{
		return UniTask.RunOnThreadPool(() => schemaReadSet(cfg, res, ct), cancellationToken: ct);
	}

	private static SchemaLoadSet schemaReadSet(UpdCfg cfg, UpdRes res, CancellationToken ct)
	{
		SchemaLoadSet data = new();
		for (int i = 0; i < cfg.aotDlls.Length; ++i)
		{
			ct.ThrowIfCancellationRequested();
			data.aot.Add(res.read(cfg.aotDlls[i]));
		}
		for (int i = 0; i < cfg.codeDlls.Length; ++i)
		{
			ct.ThrowIfCancellationRequested();
			data.code.Add(res.read(cfg.codeDlls[i]));
		}
		ct.ThrowIfCancellationRequested();
		data.secret = string.IsNullOrEmpty(cfg.secret) ? Array.Empty<byte>() : res.read(cfg.secret);
		return data;
	}

	private static void schemaLoadAot(string[] names, List<byte[]> data)
	{
#if USE_HYBRID_CLR && !UNITY_EDITOR
		for (int i = 0; i < names.Length; ++i)
		{
			LoadImageErrorCode code = RuntimeApi.LoadMetadataForAOTAssembly(data[i], HomologousImageMode.SuperSet);
			if (code != LoadImageErrorCode.OK)
			{
				UpdFail.bad(UpdCode.Load, "aot_" + schemaAssemblyName(names[i]), UpdPhase.Load);
			}
		}
#endif
	}

	private static void schemaBindResource(UpdRes res)
	{
		FrameCrossParam.mLang = ResLocalizationText.mCurLanguage;
		FrameCrossParam.mVer = res.releaseId;
		FrameCrossParam.mReadPath = res.getPath;
		FrameCrossParam.mReadPathA = res.getPathA;
		FrameCrossParam.mLocalizationName = FrameCrossParam.mLang;
		FrameCrossParam.mStreamingAssetsVersion = res.releaseId;
		FrameCrossParam.mPersistentDataVersion = res.releaseId;
		FrameCrossParam.mRemoteVersion = res.releaseId;
	}

	private static async UniTask schemaStartHot(Assembly hotAssembly, byte[] secret, CancellationToken ct)
	{
		Type type = schemaFindEntry(hotAssembly);
		MethodInfo pre = schemaFindMethod(type, "preStart", typeof(byte[]), typeof(Action));
		if (pre == null)
		{
			UpdFail.bad(UpdCode.Load, "hotfix_api", UpdPhase.Load);
		}

		using CancellationTokenSource wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
		wait.CancelAfter(60000);
		int term = 0;
		UniTaskCompletionSource<bool> done = new();
		using CancellationTokenRegistration cancel = wait.Token.Register(() =>
		{
			if (Interlocked.CompareExchange(ref term, 2, 0) == 0)
			{
				done.TrySetCanceled(wait.Token);
			}
		});
		Action onDone = () =>
		{
			if (Interlocked.CompareExchange(ref term, 1, 0) != 0)
			{
				return;
			}
			logBase("热更初始化完毕");
			done.TrySetResult(true);
		};
		Action onStart = () =>
		{
			if (Volatile.Read(ref term) != 0)
			{
				return;
			}
			try
			{
				IHotEnt instance = (IHotEnt)Activator.CreateInstance(type);
				instance.start(error =>
				{
					if (error != null)
					{
						if (Interlocked.CompareExchange(ref term, 2, 0) == 0)
						{
							done.TrySetException(schemaRootException(error));
						}
						return;
					}
					onDone();
				}, wait.Token);
			}
			catch (Exception ex)
			{
				if (Interlocked.CompareExchange(ref term, 2, 0) == 0)
				{
					done.TrySetException(schemaRootException(ex));
				}
			}
		};
		try
		{
			pre.Invoke(null, new object[2] { secret, onStart });
		}
		catch (Exception ex)
		{
			if (Interlocked.CompareExchange(ref term, 2, 0) == 0)
			{
				done.TrySetException(schemaRootException(ex));
			}
		}
		try
		{
			await done.Task;
		}
		catch (OperationCanceledException) when (!ct.IsCancellationRequested)
		{
			UpdFail.bad(UpdCode.Load, "hotfix_timeout", UpdPhase.Load);
		}
	}

	private static Type schemaFindEntry(Assembly assembly)
	{
		Type[] types;
		try
		{
			types = assembly.GetTypes();
		}
		catch (ReflectionTypeLoadException ex)
		{
			UpdFail.bad(UpdCode.Load, "hotfix_types", UpdPhase.Load, ex);
			return null;
		}
		Type found = null;
		for (int i = 0; i < types.Length; ++i)
		{
			Type type = types[i];
			if (type == null || type.IsAbstract || type.IsInterface ||
				!typeof(IHotEnt).IsAssignableFrom(type)) continue;
			if (found != null)
			{
				UpdFail.bad(UpdCode.Load, "hotfix_entry", UpdPhase.Load);
			}
			found = type;
		}
		if (found == null)
		{
			UpdFail.bad(UpdCode.Load, "hotfix_entry", UpdPhase.Load);
		}
		return found;
	}

	private static MethodInfo schemaFindMethod(Type type, string name, params Type[] parameters)
	{
		while (type != null)
		{
			MethodInfo value = type.GetMethod(name,
				BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public |
				BindingFlags.NonPublic | BindingFlags.DeclaredOnly, null, parameters, null);
			if (value != null)
			{
				return value;
			}
			type = type.BaseType;
		}
		return null;
	}

	private static Assembly schemaFindAssembly(string name)
	{
		foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
		{
			if (assembly.GetName().Name == name)
			{
				return assembly;
			}
		}
		return null;
	}

	private static HashSet<string> schemaCheckHot(UpdCfg cfg)
	{
		if (cfg == null || cfg.aotDlls == null || cfg.codeDlls == null)
		{
			UpdFail.bad(UpdCode.Config, "hot_config", UpdPhase.Load);
		}
		HashSet<string> deny = new(StringComparer.OrdinalIgnoreCase)
		{
			"Frame_Base",
			"Frame_Game",
			"HotUpd_Core",
			"HotUpd_Client",
		};
		for (int i = 0; i < cfg.aotDlls.Length; ++i)
		{
			deny.Add(schemaAssemblyName(cfg.aotDlls[i]));
		}
		HashSet<string> hot = new(StringComparer.OrdinalIgnoreCase);
		int frameAt = -1;
		int entryAt = -1;
		for (int i = 0; i < cfg.codeDlls.Length; ++i)
		{
			string name = schemaAssemblyName(cfg.codeDlls[i]);
			if (!UpdFmt.isId(name) || !hot.Add(name) || deny.Contains(name))
			{
				UpdFail.bad(UpdCode.Compat, "aot_hot_name", UpdPhase.Load);
			}
			if (name == HOTFIX_FRAME) frameAt = i;
			if (name == HOTFIX) entryAt = i;
		}
		if (frameAt < 0 || entryAt < 0 || frameAt >= entryAt ||
			schemaAssemblyName(cfg.entryDll) != HOTFIX)
		{
			UpdFail.bad(UpdCode.Compat, "hot_order", UpdPhase.Load);
		}
		return hot;
	}

	private static void schemaCheckAssemblies(UpdCfg cfg, List<Assembly> loaded)
	{
		if (loaded.Count != cfg.codeDlls.Length)
		{
			UpdFail.bad(UpdCode.Load, "hot_count", UpdPhase.Load);
		}
		HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
		for (int i = 0; i < loaded.Count; ++i)
		{
			string expected = schemaAssemblyName(cfg.codeDlls[i]);
			string actual = loaded[i].GetName().Name;
			if (!string.Equals(actual, expected, StringComparison.Ordinal) || !names.Add(actual))
			{
				UpdFail.bad(UpdCode.Load, "hot_name", UpdPhase.Load);
			}
		}
	}

	private static void schemaCheckPreload(HashSet<string> hot)
	{
#if !UNITY_EDITOR
		foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
		{
			if (hot.Contains(assembly.GetName().Name))
			{
				UpdFail.bad(UpdCode.Compat, "hot_preload", UpdPhase.Load);
			}
		}
#endif
	}

	private static string schemaAssemblyName(string path)
	{
		string name = Path.GetFileName(path);
		if (name.EndsWith(".bytes", StringComparison.OrdinalIgnoreCase))
		{
			name = name.Substring(0, name.Length - 6);
		}
		return Path.GetFileNameWithoutExtension(name);
	}

	private static Exception schemaRootException(Exception ex)
	{
		while (ex is TargetInvocationException && ex.InnerException != null)
		{
			ex = ex.InnerException;
		}
		return ex;
	}

	private static void schemaDestroyAot()
	{
		GameEntryBase entry = GameEntryBase.getInstance();
		if (entry == null)
		{
			return;
		}
		entry.getFrameworkAOT()?.destroy();
		entry.setFrameworkAOT(null);
	}

	private static UpdRet<bool> schemaFail(UpdCode code, string detail)
	{
		return UpdRet<bool>.fail(new UpdErr(code, detail, UpdPhase.Load));
	}
}
