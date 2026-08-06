using System;
using UnityEngine;

[Serializable]
public class FramworkParam
{
	[Tooltip("窗口高度,当mWindowMode为FULL_SCREEN时无效")]
	public int mScreenHeight = 0;                                   // 窗口高度,当mWindowMode为FULL_SCREEN时无效
	[Tooltip("窗口宽度,当mWindowMode为FULL_SCREEN时无效")]
	public int mScreenWidth = 0;                                    // 窗口宽度,当mWindowMode为FULL_SCREEN时无效
	[Tooltip("默认的帧率")]
	public int mDefaultFrameRate = 60;                              // 默认的帧率
	[Tooltip("是否启用对象池中的堆栈追踪,由于堆栈追踪非常耗时,所以默认关闭,快捷键F4")]
	public bool mEnablePoolStackTrace;                              // 是否启用对象池中的堆栈追踪,由于堆栈追踪非常耗时,所以默认关闭,快捷键F4
	[Tooltip("是否启用调试脚本,用于显示调试信息的脚本,快捷键F3")]
	public bool mEnableScriptDebug;                                 // 是否启用调试脚本,用于显示调试信息的脚本,快捷键F3
	[Tooltip("加载源,从AssetBundle加载还是从Resources加载")]
	public LOAD_SOURCE mLoadSource = LOAD_SOURCE.ASSET_DATABASE;    // 加载源,从AssetBundle加载还是从Resources加载
	[Tooltip("窗口类型")]
	public WINDOW_MODE mWindowMode = WINDOW_MODE.FULL_SCREEN;       // 窗口类型
	[Tooltip("3D物理设置策略。成熟宿主项目应选择PRESERVE_HOST")]
	public FRAME_PHYSICS_MODE mPhysicsMode = FRAME_PHYSICS_MODE.LEGACY_FRAMEWORK;
	[Tooltip("屏幕与UGUI适配策略。成熟宿主项目应选择PRESERVE_HOST")]
	public FRAME_SCREEN_MODE mScreenMode = FRAME_SCREEN_MODE.LEGACY_FRAMEWORK;
	[Tooltip("框架UI根节点名称，也可以在启动前通过FrameSceneBindings直接绑定对象")]
	public string mUGUIRootName = "UGUIRoot";
	[Tooltip("框架UI相机名称，也可以在启动前通过FrameSceneBindings直接绑定对象")]
	public string mUICameraName = "UICamera";
	[Tooltip("框架UI模糊相机名称，也可以在启动前通过FrameSceneBindings直接绑定对象")]
	public string mUIBlurCameraName = "BlurCamera";
	[Tooltip("框架主相机名称，也可以在启动前通过FrameSceneBindings直接绑定对象")]
	public string mMainCameraName = "MainCamera";
}
