using System;
using System.Collections.Generic;
using static TestAssert;

// GameFrameworkHotFix生命周期回归测试
internal static class GameFrameworkHotFixLifecycleTest
{
	static readonly List<string> mEvents = new();

	public static void Run()
	{
		GameFrameworkHotFix oldFramework = FrameBaseHotFix.mGameFrameworkHotFix;
		Action oldDestroyCallback = GameFrameworkHotFix.mOnDestroy;
		Action<int, long, long, long, long> oldMemoryCallback = GameFrameworkHotFix.mOnMemoryModifiedCheck;
		try
		{
			mEvents.Clear();
			int destroyNotifyCount = 0;
			GameFrameworkHotFix.mOnDestroy = () => mEvents.Add("framework.destroy");
			TestGameFramework framework = new();
			framework.registeFrameSystem<SecondFrameSystem>((com) =>
			{
				if (com == null)
				{
					++destroyNotifyCount;
					mEvents.Add("second.notify");
				}
			}, destroyOrder: 20);
			framework.registeFrameSystem<FirstFrameSystem>((com) =>
			{
				if (com == null)
				{
					++destroyNotifyCount;
					mEvents.Add("first.notify");
				}
			}, destroyOrder: 10);
			framework.sortList();

			framework.destroy();

			assertTrue(framework.isDestroy(), "destroy后框架应标记为已销毁");
			assertTrue(framework.isReleased(), "destroy后内部容器和回调表应释放");
			assertEqual(2, destroyNotifyCount, "每个系统都应收到一次销毁通知");
			assertEqual("framework.destroy", mEvents[0], "框架销毁回调应最先执行");
			assertEqual("first.willDestroy", mEvents[1], "willDestroy应遵循destroyOrder");
			assertEqual("second.willDestroy", mEvents[2], "willDestroy应覆盖全部系统");
			assertEqual("first.destroy", mEvents[3], "destroy应遵循destroyOrder");
			assertEqual("first.notify", mEvents[4], "系统销毁后应发送注销通知");
			assertEqual("second.destroy", mEvents[5], "destroy应覆盖全部系统");
			assertEqual("second.notify", mEvents[6], "系统销毁后应发送注销通知");

			int eventCount = mEvents.Count;
			framework.destroy();
			assertEqual(eventCount, mEvents.Count, "重复destroy不应再次触发生命周期");
			assertTrue(ReferenceEquals(oldFramework, FrameBaseHotFix.mGameFrameworkHotFix), "销毁非活动框架不得清空活动框架引用");
		}
		finally
		{
			FrameBaseHotFix.mGameFrameworkHotFix = oldFramework;
			GameFrameworkHotFix.mOnDestroy = oldDestroyCallback;
			GameFrameworkHotFix.mOnMemoryModifiedCheck = oldMemoryCallback;
			mEvents.Clear();
		}
	}

	sealed class TestGameFramework : GameFrameworkHotFix
	{
		public bool isReleased()
		{
			return mFrameComponentInit == null &&
				mFrameComponentUpdate == null &&
				mFrameComponentDestroy == null &&
				mFrameComponentMap == null &&
				mFrameCallbackList == null;
		}
	}

	sealed class FirstFrameSystem : FrameSystem
	{
		public FirstFrameSystem() { }
		public override void willDestroy() { mEvents.Add("first.willDestroy"); }
		public override void destroy()
		{
			mEvents.Add("first.destroy");
			base.destroy();
		}
	}

	sealed class SecondFrameSystem : FrameSystem
	{
		public SecondFrameSystem() { }
		public override void willDestroy() { mEvents.Add("second.willDestroy"); }
		public override void destroy()
		{
			mEvents.Add("second.destroy");
			base.destroy();
		}
	}
}
