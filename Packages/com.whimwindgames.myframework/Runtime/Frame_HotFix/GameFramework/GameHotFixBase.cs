#if USE_OBFUZ
using Obfuz;
#endif
using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using static FrameBaseHotFix;
using static FrameBaseDefine;
using static UnityUtility;
using static FrameUtility;
using static FrameBaseUtility;

// HotFix中顶层管理器的基类,负责热更启动、框架组件初始化和首屏就绪确认。
#if USE_OBFUZ
[ObfuzIgnore]
#endif
public abstract class GameHotFixBase<T> : IHotEnt where T : GameHotFixBase<T>
{
	protected static GameHotFixBase<T> mInstance;
	protected readonly List<FrameSystem> mFrameComponentInit = new();
	protected Action<Exception> mFinishCallback;
	private CancellationToken mStartCt;
	private int mFinishState;
	private int mLoadState;

	// 保留旧入口签名，旧项目无需改名或改调用方式。
	public void start(Action callback)
	{
		start(error =>
		{
			if (error != null)
			{
				logException(error, "热更入口启动失败");
				return;
			}
			callback?.Invoke();
		}, CancellationToken.None);
	}

	public void start(Action<Exception> callback, CancellationToken ct)
	{
		mFinishCallback = callback;
		mStartCt = ct;
		Volatile.Write(ref mFinishState, 0);
		Volatile.Write(ref mLoadState, 0);
		ct.ThrowIfCancellationRequested();
		GameFrameworkHotFix.mOnPackageName += getAndroidPluginBundleName;
		GameFrameworkHotFix.startHotFix(error =>
		{
			if (mStartCt.IsCancellationRequested) return;
			if (error != null)
			{
				finish(error);
				return;
			}
			try
			{
				initFrameSystem();
				mGameFrameworkHotFix.sortList();
				mFrameComponentInit.Sort(FrameSystem.compareInit);
				registerAllTable();
				registerAll();
				loadData(onAllLoaded);
			}
			catch (Exception ex) { finish(ex); }
		});
	}

	public static void callbackFinish()
	{
		mInstance?.finish(null);
	}

	public static GameHotFixBase<T> createHotFixInstance()
	{
		mInstance = createInstance<GameHotFixBase<T>>(typeof(T));
		return mInstance;
	}

	protected void onAllLoaded()
	{
		if (mStartCt.IsCancellationRequested ||
			Interlocked.CompareExchange(ref mLoadState, 1, 0) != 0) return;
		bool inited = false;
		try
		{
			if (isEditor())
			{
				mExcelManager.checkAll();
#if USE_SQLITE
				mSQLiteManager.checkAll();
#endif
			}
			onPreInit();
			foreach (FrameSystem frame in mFrameComponentInit)
			{
				mStartCt.ThrowIfCancellationRequested();
				DateTime start = DateTime.Now;
				frame.init();
				if (isDevOrEditor() && (int)(DateTime.Now - start).TotalMilliseconds > 1)
				{
					log(frame.getName() + "初始化消耗时间:" +
						(int)(DateTime.Now - start).TotalMilliseconds + "毫秒");
				}
			}
			foreach (FrameSystem frame in mFrameComponentInit)
			{
				mStartCt.ThrowIfCancellationRequested();
				DateTime start = DateTime.Now;
				frame.lateInit();
				if (isDevOrEditor() && (int)(DateTime.Now - start).TotalMilliseconds > 1)
				{
					log(frame.getName() + " late初始化消耗时间:" +
						(int)(DateTime.Now - start).TotalMilliseconds + "毫秒");
				}
			}
			onPostInit();
			mStartCt.ThrowIfCancellationRequested();
			mGameFrameworkHotFix.setAllInited(true);
			inited = true;
			enterScene(getStartGameSceneType());
			mStartCt.ThrowIfCancellationRequested();
			waitReady(readyDone);
		}
		catch (OperationCanceledException) when (mStartCt.IsCancellationRequested)
		{
			resetInit(inited);
		}
		catch (Exception ex)
		{
			resetInit(inited);
			finish(ex);
		}
	}

	private void resetInit(bool inited)
	{
		if (!inited) return;
		try { mGameFrameworkHotFix.setAllInited(false); }
		catch (Exception ex) { logException(ex); }
	}

	private void finish(Exception error)
	{
		if (mStartCt.IsCancellationRequested ||
			Interlocked.CompareExchange(ref mFinishState, 1, 0) != 0) return;
		Action<Exception> callback = mFinishCallback;
		mFinishCallback = null;
		GameFrameworkHotFix.mOnPackageName -= getAndroidPluginBundleName;
		try { callback?.Invoke(error); }
		catch (Exception ex) { logException(ex); }
	}

	protected virtual string getAndroidPluginBundleName()
	{
		return FrameCrossParam.mAndroidPluginPackage;
	}

	protected abstract void registerAll();
	// 旧项目可继续覆盖该入口；不使用表格的项目无需实现。
	protected virtual void registerAllTable() { }
	protected abstract void initFrameSystem();
	protected virtual void loadData(Action done)
	{
#if USE_SQLITE
		mSQLiteManager.loadAllAsync(() => mExcelManager.loadAllAsync(done));
#else
		mExcelManager.loadAllAsync(done);
#endif
	}
	protected virtual void onPreInit() { }
	protected virtual void onPostInit() { }
	protected virtual void waitReady(Action<Exception> done) { done?.Invoke(null); }
	protected abstract Type getStartGameSceneType();

	protected void registeFrameSystem<T0>(Action<T0> callback) where T0 : FrameSystem, new()
	{
		mFrameComponentInit.Add(mGameFrameworkHotFix.registeFrameSystem(callback));
	}

	// 旧启动链仍可从持久化目录读取密钥；Schema 11直接传入已校验字节。
#if USE_OBFUZ
	[ObfuzIgnore]
#endif
	protected static void preStart(Action callback)
	{
		if (isEditor())
		{
			callback?.Invoke();
			return;
		}
		string filePath = F_PERSISTENT_ASSETS_PATH + DYNAMIC_SECRET_FILE;
		if (!isWebGL()) filePath = "file://" + filePath;
		GameEntryBase.startCoroutine(openFileAsyncInternal(filePath, true,
			bytes => preStart(bytes, callback)));
	}

#if USE_OBFUZ
	[ObfuzIgnore]
#endif
	protected static void preStart(byte[] secret, Action callback)
	{
		try
		{
			if (!isEditor()) HotPreReg.run(secret);
			callback?.Invoke();
		}
		catch (Exception ex)
		{
			Debug.LogException(ex);
			throw;
		}
	}

	private void readyDone(Exception error)
	{
		if (mStartCt.IsCancellationRequested || Volatile.Read(ref mFinishState) != 0) return;
		if (error != null)
		{
			resetInit(true);
			finish(error);
			return;
		}
		log("启动游戏耗时:" +
			(int)(DateTime.Now - mGameFrameworkHotFix.getStartTime()).TotalMilliseconds +
			"毫秒");
		finish(null);
	}
}
