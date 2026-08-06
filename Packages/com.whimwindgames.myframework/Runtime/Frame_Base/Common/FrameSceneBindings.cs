using UnityEngine;
using static FrameBaseUtility;

// 成熟宿主项目可以直接注入现有场景对象；未注入时继续按旧名称查找。
public static class FrameSceneBindings
{
	private const string DEFAULT_UGUI_ROOT = "UGUIRoot";
	private const string DEFAULT_UI_CAMERA = "UICamera";
	private const string DEFAULT_UI_BLUR_CAMERA = "BlurCamera";
	private const string DEFAULT_MAIN_CAMERA = "MainCamera";

	private static string mUGUIRootName = DEFAULT_UGUI_ROOT;
	private static string mUICameraName = DEFAULT_UI_CAMERA;
	private static string mUIBlurCameraName = DEFAULT_UI_BLUR_CAMERA;
	private static string mMainCameraName = DEFAULT_MAIN_CAMERA;
	private static GameObject mUGUIRoot;
	private static GameObject mUICamera;
	private static GameObject mUIBlurCamera;
	private static GameObject mMainCamera;

	public static void configureNames(string uguiRootName, string uiCameraName,
		string uiBlurCameraName, string mainCameraName)
	{
		mUGUIRootName = fallbackName(uguiRootName, DEFAULT_UGUI_ROOT);
		mUICameraName = fallbackName(uiCameraName, DEFAULT_UI_CAMERA);
		mUIBlurCameraName = fallbackName(uiBlurCameraName, DEFAULT_UI_BLUR_CAMERA);
		mMainCameraName = fallbackName(mainCameraName, DEFAULT_MAIN_CAMERA);
	}

	public static void bindUGUIRoot(GameObject value) { mUGUIRoot = value; }
	public static void bindUICamera(GameObject value) { mUICamera = value; }
	public static void bindUIBlurCamera(GameObject value) { mUIBlurCamera = value; }
	public static void bindMainCamera(GameObject value) { mMainCamera = value; }

	public static GameObject getUGUIRoot(bool errorIfNull = false)
	{
		return mUGUIRoot != null ? mUGUIRoot : findRootGameObject(mUGUIRootName, errorIfNull);
	}

	public static GameObject getUICamera(GameObject uguiRoot = null, bool errorIfNull = false)
	{
		if (mUICamera != null)
		{
			return mUICamera;
		}
		uguiRoot ??= getUGUIRoot(errorIfNull);
		return uguiRoot != null ? findGameObject(mUICameraName, uguiRoot, errorIfNull) : null;
	}

	public static GameObject getUIBlurCamera(GameObject uguiRoot = null, bool errorIfNull = false)
	{
		if (mUIBlurCamera != null)
		{
			return mUIBlurCamera;
		}
		uguiRoot ??= getUGUIRoot(errorIfNull);
		return uguiRoot != null ? findGameObject(mUIBlurCameraName, uguiRoot, errorIfNull) : null;
	}

	public static GameObject getMainCamera(bool errorIfNull = false)
	{
		return mMainCamera != null ? mMainCamera : findRootGameObject(mMainCameraName, errorIfNull);
	}

	public static void reset()
	{
		mUGUIRoot = null;
		mUICamera = null;
		mUIBlurCamera = null;
		mMainCamera = null;
		configureNames(null, null, null, null);
	}

	private static string fallbackName(string value, string fallback)
	{
		return string.IsNullOrWhiteSpace(value) ? fallback : value;
	}
}
