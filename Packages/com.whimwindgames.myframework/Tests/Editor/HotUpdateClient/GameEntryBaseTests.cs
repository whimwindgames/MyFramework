using System;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

public sealed class GameEntryBaseTests
{
	private sealed class TestEntry : GameEntryBase
	{
		public void DestroyLifecycle()
		{
			base.OnDestroy();
		}
	}

	private GameObject mFirstObject;
	private GameObject mSecondObject;

	[TearDown]
	public void TearDown()
	{
		if (mSecondObject != null)
		{
			UnityEngine.Object.DestroyImmediate(mSecondObject);
		}
		if (mFirstObject != null)
		{
			UnityEngine.Object.DestroyImmediate(mFirstObject);
		}
	}

	[Test]
	public void PrimaryEntryOwnsAndIdempotentlyClearsGlobalLifecycle()
	{
		TestEntry entry = CreateInactiveEntry("Primary", ref mFirstObject);

		entry.Awake();

		Assert.That(GameEntryBase.getInstance(), Is.SameAs(entry));
		Assert.That(GameEntryBase.getInstanceObject(), Is.SameAs(mFirstObject));

		entry.DestroyLifecycle();
		entry.DestroyLifecycle();

		Assert.That(GameEntryBase.getInstance(), Is.Null);
		Assert.That(GameEntryBase.getInstanceObject(), Is.Null);
	}

	[Test]
	public void DuplicateEntryFailsWithoutReplacingOrClearingPrimaryEntry()
	{
		TestEntry primary = CreateInactiveEntry("Primary", ref mFirstObject);
		TestEntry duplicate = CreateInactiveEntry("Duplicate", ref mSecondObject);
		primary.Awake();
		const string expected = "Only one GameEntryBase may own the process lifecycle. " +
			"Existing: GameEntryBaseTests+TestEntry on 'Primary'; duplicate: " +
			"GameEntryBaseTests+TestEntry on 'Duplicate'.";
		LogAssert.Expect(LogType.Error, expected);

		InvalidOperationException exception = Assert.Throws<InvalidOperationException>(duplicate.Awake);

		Assert.That(exception.Message, Is.EqualTo(expected));
		Assert.That(GameEntryBase.getInstance(), Is.SameAs(primary));
		duplicate.DestroyLifecycle();
		Assert.That(GameEntryBase.getInstance(), Is.SameAs(primary));

		primary.DestroyLifecycle();
		Assert.That(GameEntryBase.getInstance(), Is.Null);
	}

	private static TestEntry CreateInactiveEntry(string name, ref GameObject owner)
	{
		owner = new GameObject(name);
		owner.SetActive(false);
		return owner.AddComponent<TestEntry>();
	}
}
