using NUnit.Framework;
using UnityEngine;
using System;
using System.Reflection;

public class AssetImportScopeTests
{
	[TestCase("Assets/GameResources", true)]
	[TestCase("Assets/GameResources/Fish/Shark.fbx", true)]
	[TestCase("Assets\\GameResources\\UI\\Main.png", true)]
	[TestCase("Assets/GameResources2/Fish.png", false)]
	[TestCase("Assets/_Project/Art/Fish.png", false)]
	[TestCase("Assets/Plugins/ThirdParty.png", false)]
	[TestCase("Packages/com.example.host/GameResources/Fish.png", false)]
	[TestCase("", false)]
	[TestCase(null, false)]
	public void ImportPolicyOnlyOwnsFrameworkGameResources(string path, bool expected)
	{
		Assert.That(AssetsImport.isGameResourceAssetPath(path), Is.EqualTo(expected));
	}
}

public class MatureProjectCompatibilityTests
{
	[Test]
	public void NewParametersKeepLegacySerializedDefaults()
	{
		FramworkParam param = new();
		Assert.That(param.mPhysicsMode, Is.EqualTo(FRAME_PHYSICS_MODE.LEGACY_FRAMEWORK));
		Assert.That(param.mScreenMode, Is.EqualTo(FRAME_SCREEN_MODE.LEGACY_FRAMEWORK));
		Assert.That(param.mUGUIRootName, Is.EqualTo("UGUIRoot"));
		Assert.That(param.mMainCameraName, Is.EqualTo("MainCamera"));
	}

	[Test]
	public void PreserveHostPhysicsDoesNotMutateUnitySettings()
	{
		SimulationMode oldMode = Physics.simulationMode;
		bool oldAutoSync = Physics.autoSyncTransforms;
		GameEntryBase.applyPhysicsMode(FRAME_PHYSICS_MODE.PRESERVE_HOST);
		Assert.That(Physics.simulationMode, Is.EqualTo(oldMode));
		Assert.That(Physics.autoSyncTransforms, Is.EqualTo(oldAutoSync));
	}

	[Test]
	public void SceneBindingsAcceptInjectedInactiveHostObjects()
	{
		GameObject root = new("HostCanvas");
		GameObject camera = new("HostUICamera");
		camera.transform.SetParent(root.transform);
		root.SetActive(false);
		try
		{
			FrameSceneBindings.bindUGUIRoot(root);
			FrameSceneBindings.bindUICamera(camera);
			Assert.That(FrameSceneBindings.getUGUIRoot(), Is.SameAs(root));
			Assert.That(FrameSceneBindings.getUICamera(), Is.SameAs(camera));
		}
		finally
		{
			FrameSceneBindings.reset();
			UnityEngine.Object.DestroyImmediate(root);
		}
	}
}

public class FrameScreenContextTests
{
	[Test]
	public void LandscapeAndPortraitUseActualDimensions()
	{
		FrameScreenSnapshot landscape = new(new(2560, 1080), new(0, 0, 2560, 1080), ScreenOrientation.AutoRotation);
		FrameScreenSnapshot portrait = new(new(1080, 1920), new(0, 0, 1080, 1920), ScreenOrientation.Portrait);
		Assert.That(landscape.isLandscape(), Is.True);
		Assert.That(landscape.getAspect(), Is.EqualTo(2560.0f / 1080.0f).Within(0.0001f));
		Assert.That(portrait.isPortrait(), Is.True);
	}

	[Test]
	public void SafeAreaProvidesInsetsAndNormalizedRect()
	{
		FrameScreenSnapshot snapshot = new(new(2400, 1080), new(80, 20, 2240, 1000), ScreenOrientation.LandscapeLeft);
		Assert.That(snapshot.getSafeAreaInsets(), Is.EqualTo(new Vector4(80, 20, 80, 60)));
		Rect normalized = snapshot.getNormalizedSafeArea();
		Assert.That(normalized.x, Is.EqualTo(80.0f / 2400.0f).Within(0.0001f));
		Assert.That(normalized.width, Is.EqualTo(2240.0f / 2400.0f).Within(0.0001f));
	}

	[Test]
	public void SafeAreaIsClampedToScreenBounds()
	{
		FrameScreenSnapshot snapshot = new(new(100, 50), new(-20, -10, 180, 90), ScreenOrientation.Unknown);
		Assert.That(snapshot.SafeArea, Is.EqualTo(new Rect(0, 0, 100, 50)));
	}
}

public class SelectiveFrameworkBootstrapTests
{
	[TestCase(typeof(GameFramework), "initPlatformSystem")]
	[TestCase(typeof(GameFramework), "initFrameSystem")]
	[TestCase(typeof(GameFramework), "onFrameSystemRegistered")]
	[TestCase(typeof(GameFrameworkHotFix), "initPlatformSystem")]
	[TestCase(typeof(GameFrameworkHotFix), "initFrameSystem")]
	[TestCase(typeof(GameFrameworkHotFix), "recoverFrameworkCrossParam")]
	public void BootstrapStagesAreOverridable(Type frameworkType, string methodName)
	{
		MethodInfo method = frameworkType.GetMethod(methodName,
			BindingFlags.Instance | BindingFlags.NonPublic);
		Assert.That(method, Is.Not.Null);
		Assert.That(method.IsVirtual, Is.True);
		Assert.That(method.IsFinal, Is.False);
	}

	[Test]
	public void HotFixSupportsCustomFrameworkFactoryWithoutRemovingLegacyOverloads()
	{
		Assert.That(typeof(GameFrameworkHotFix).GetMethod("startHotFix",
			new[] { typeof(Action) }), Is.Not.Null);
		Assert.That(typeof(GameFrameworkHotFix).GetMethod("startHotFix",
			new[] { typeof(Action<Exception>) }), Is.Not.Null);
		Assert.That(typeof(GameFrameworkHotFix).GetMethod("startHotFix",
			new[] { typeof(Func<GameFrameworkHotFix>), typeof(Action<Exception>) }), Is.Not.Null);
	}
}
