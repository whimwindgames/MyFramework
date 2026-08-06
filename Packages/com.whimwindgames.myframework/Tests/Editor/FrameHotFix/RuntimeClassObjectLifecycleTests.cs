using System;
using NUnit.Framework;

public sealed class RuntimeClassObjectLifecycleTests
{
	public sealed class ProcedureProbe : SceneProcedure
	{
		public static int Created;

		public override void onCreate()
		{
			++Created;
		}
	}

	public sealed class GameSceneProbe : GameScene
	{
		public static int Created;

		public override void onCreate()
		{
			++Created;
		}

		public override void createSceneProcedure()
		{
			addProcedure<ProcedureProbe>();
		}

		public override void assignStartExitProcedure()
		{
			mStartProcedure = typeof(ProcedureProbe);
			mExitProcedure = typeof(ProcedureProbe);
		}
	}

	public sealed class SceneInstanceProbe : SceneInstance
	{
		public static int Created;

		public override void onCreate()
		{
			++Created;
		}
	}

	private sealed class SceneSystemProbe : SceneSystem
	{
		public SceneInstance create(string name)
		{
			return createScene(name);
		}
	}

	[SetUp]
	public void SetUp()
	{
		ProcedureProbe.Created = 0;
		GameSceneProbe.Created = 0;
		SceneInstanceProbe.Created = 0;
	}

	[Test]
	public void AddedProcedureStartsAValidClassObjectLifecycle()
	{
		var owner = new GameSceneProbe();

		ProcedureProbe procedure = owner.addProcedure<ProcedureProbe>();

		assertCreated(procedure, ProcedureProbe.Created);
	}

	[Test]
	public void EnteredGameSceneStartsAValidClassObjectLifecycle()
	{
		var manager = new GameSceneManager();
		GameSceneManager previous = FrameBaseHotFix.mGameSceneManager;
		try
		{
			FrameBaseHotFix.mGameSceneManager = manager;
			manager.enterScene(typeof(GameSceneProbe), null);

			assertCreated(manager.getCurScene(), GameSceneProbe.Created);
		}
		finally
		{
			GameScene scene = manager.getCurScene();
			if (scene?.getObject() != null)
			{
				UnityEngine.Object.DestroyImmediate(scene.getObject());
			}
			FrameBaseHotFix.mGameSceneManager = previous;
		}
	}

	[Test]
	public void RegisteredSceneInstanceStartsAValidClassObjectLifecycle()
	{
		var system = new SceneSystemProbe();
		system.registeScene(typeof(SceneInstanceProbe), "Scenes/LifecycleProbe.unity", null);

		SceneInstance instance = system.create("LifecycleProbe");

		assertCreated(instance, SceneInstanceProbe.Created);
	}

	private static void assertCreated(ClassObject value, int createdCount)
	{
		Assert.That(value, Is.Not.Null);
		Assert.That(value.isDestroy(), Is.False);
		Assert.That(value.getAssignID(), Is.EqualTo(value.getObjectInstanceID()));
		Assert.That(createdCount, Is.EqualTo(1));
	}
}
