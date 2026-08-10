using System;
using System.Threading;
using UnityEngine;
using static FrameBaseUtility;

public class BuglyForwarder
{
	private const string CLS = "com.tencent.bugly.crashreport.CrashReport";
	private static int mInited;
	private static int mReported;
	private static int mDisabled;
	private static int mMainId;
	private static SynchronizationContext mMainCtx;

	public static void init(string appId)
	{
		if (isEditor() || string.IsNullOrWhiteSpace(appId) ||
			Interlocked.Exchange(ref mInited, 1) != 0)
		{
			return;
		}
		if (isAndroid() && !initAndroid(appId.Trim()))
		{
			return;
		}
		mMainId = Thread.CurrentThread.ManagedThreadId;
		mMainCtx = SynchronizationContext.Current;
		Application.logMessageReceivedThreaded += queueError;
	}

	private static bool initAndroid(string appId)
	{
		try
		{
			using AndroidJavaClass player = new("com.unity3d.player.UnityPlayer");
			using AndroidJavaObject activity = player.GetStatic<AndroidJavaObject>("currentActivity");
			using AndroidJavaClass cls = new(CLS);
			cls.CallStatic("setAppVersion", activity, Application.version);
			cls.CallStatic("initCrashReport", activity, appId, Debug.isDebugBuild);
			return true;
		}
		catch (Exception ex)
		{
			disable(ex);
			return false;
		}
	}

	private static void queueError(string condition, string stackTrace, LogType type)
	{
		if (type != LogType.Exception && type != LogType.Error)
		{
			return;
		}
		if (Thread.CurrentThread.ManagedThreadId == mMainId)
		{
			reportError(condition, stackTrace, type);
			return;
		}
		SynchronizationContext ctx = mMainCtx;
		ctx?.Post(_ => reportError(condition, stackTrace, type), null);
	}

	public static void setVersion(AndroidJavaObject mainActivity, string version)
	{
		if (isEditor() || Volatile.Read(ref mInited) == 0 ||
			Volatile.Read(ref mDisabled) != 0)
		{
			return;
		}
		if (isAndroid())
		{
			callAndroid(cls => cls.CallStatic("setAppVersion", mainActivity, version));
		}
		else if (isIOS())
		{
			try
			{
				iOSDllImportFrameBase.setUserData("unity_version", version);
			}
			catch (Exception ex)
			{
				disable(ex);
			}
		}
	}

	public static void reportError(string condition, string stackTrace, LogType type)
	{
		if (type != LogType.Exception && type != LogType.Error)
		{
			return;
		}
		if (Volatile.Read(ref mInited) == 0 || Volatile.Read(ref mDisabled) != 0)
		{
			return;
		}
		if (Interlocked.Exchange(ref mReported, 1) != 0)
		{
			return;
		}
		string name = condition;
		if (name.Contains("error:"))
		{
			name = name[name.IndexOf("error:", StringComparison.Ordinal)..];
		}
		if (isAndroid())
		{
			callAndroid(cls =>
				cls.CallStatic("postException", 4, name, condition, stackTrace, null));
		}
		else if (isIOS())
		{
			try
			{
				iOSDllImportFrameBase.reportException(name, condition, stackTrace);
			}
			catch (Exception ex)
			{
				disable(ex);
			}
		}
	}

	private static void callAndroid(Action<AndroidJavaClass> call)
	{
		if (Volatile.Read(ref mDisabled) != 0)
		{
			return;
		}
		try
		{
			using AndroidJavaClass cls = new(CLS);
			call(cls);
		}
		catch (Exception ex)
		{
			disable(ex);
		}
	}

	private static void disable(Exception ex)
	{
		if (Interlocked.Exchange(ref mDisabled, 1) == 0)
		{
			Debug.LogWarning("Bugly不可用，已停用错误上报:" + ex.Message);
		}
	}
}
