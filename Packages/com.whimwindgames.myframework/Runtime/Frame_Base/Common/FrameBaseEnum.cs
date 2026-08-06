using UnityEngine;

// 窗口模式类型
public enum WINDOW_MODE : byte
{
	[EnumLabel("窗口"), Tooltip("带边框的窗口模式")]
	WINDOWED,                       // 带边框的窗口模式
	[EnumLabel("全屏"), Tooltip("全屏模式")]
	FULL_SCREEN,                    // 全屏模式
	[EnumLabel("自定义全屏"), Tooltip("全屏并且使用下面设置的分辨率")]
	FULL_SCREEN_CUSTOM_RESOLUTION,  // 全屏并且使用下面设置的分辨率
}

// GameEntryBase 对宿主3D物理设置的处理方式。
// LEGACY_FRAMEWORK 必须保持为0，使旧场景缺少新增序列化字段时继续沿用历史行为。
public enum FRAME_PHYSICS_MODE : byte
{
	[EnumLabel("框架脚本模拟"), Tooltip("沿用旧行为：Physics使用Script模式并启用autoSyncTransforms")]
	LEGACY_FRAMEWORK,
	[EnumLabel("保留宿主设置"), Tooltip("不修改项目当前的Physics模拟模式和autoSyncTransforms")]
	PRESERVE_HOST,
}

// GameEntryBase 对宿主分辨率、UGUI根节点和UI相机的处理方式。
// LEGACY_FRAMEWORK 必须保持为0，以兼容旧场景的序列化数据。
public enum FRAME_SCREEN_MODE : byte
{
	[EnumLabel("框架管理"), Tooltip("沿用旧行为：由框架设置分辨率并适配UGUI根节点和UI相机")]
	LEGACY_FRAMEWORK,
	[EnumLabel("保留宿主设置"), Tooltip("不修改分辨率、UGUI根节点尺寸和UI相机参数")]
	PRESERVE_HOST,
}

// 等比缩放比例的计算方式
public enum ASPECT_BASE : byte
{
	[EnumLabel("根据屏幕宽度进行缩放")]
	USE_WIDTH_SCALE,            // 使用宽的缩放值来缩放控件
	[EnumLabel("根据屏幕高度进行缩放")]
	USE_HEIGHT_SCALE,           // 使用高的缩放值来缩放控件
	[EnumLabel("根据屏幕宽高中缩放最小的进行缩放")]
	AUTO,                       // 取宽高缩放值中最小的,保证缩放以后不会超出屏幕范围
	[EnumLabel("根据屏幕宽高中缩放最大的进行缩放")]
	INVERSE_AUTO,               // 取宽高缩放值中最大的,保证缩放以后不会在屏幕范围留出空白
	[EnumLabel("不缩放")]
	NONE,                       // 无效值
}

// 加载状态
public enum LOAD_STATE : byte
{
	NONE,                   // 已卸载
	WAIT_FOR_LOAD,          // 等待加载
	DOWNLOADING,            // 正在下载
	LOADING,                // 正在加载
	LOADED,                 // 已加载
}

// 加载资源的来源
public enum LOAD_SOURCE : byte
{
	ASSET_DATABASE,         // 从AssetDatabase加载
	ASSET_BUNDLE,           // 从AssetBundle加载
}

// 版本号比较结果
public enum VERSION_COMPARE : byte
{
	EQUAL,          // 版本号相同
	REMOTE_LOWER,   // 远端版本号更小
	LOCAL_LOWER,    // 本地版本号更小
}

// 资源加载路径的选择方式
public enum ASSET_READ_PATH : byte
{
	NONE,                   // 无效值
	SAME_TO_REMOTE,         // 读取与远端一致的文件
	PERSISTENT_FIRST,		// 优先从PersistentAssets中读取,没有再从StreamingAssets中读取
	STREAMING_ASSETS_ONLY,  // 只从StreamingAssets中读取
	REMOTE_ASSETS_ONLY,     // 只从远端读取
}
