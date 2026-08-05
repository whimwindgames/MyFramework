using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using NUnit.Framework;

public sealed class HybridCLRSystemTests
{
	[Test]
	public void PublicApiKeepsLegacyAndSchema11Entries()
	{
		Type type = typeof(HybridCLRSystem);
		MethodInfo legacy = type.GetMethod("launchHotFix", new Type[1] { typeof(Action) });
		MethodInfo schema = type.GetMethod("launch", new Type[3]
		{
			typeof(UpdRes),
			typeof(CancellationToken),
			typeof(Func<CancellationToken, UniTask>),
		});

		Assert.That(legacy, Is.Not.Null);
		Assert.That(legacy.ReturnType, Is.EqualTo(typeof(void)));
		Assert.That(schema, Is.Not.Null);
		Assert.That(schema.ReturnType, Is.EqualTo(typeof(UniTask<UpdRet<bool>>)));
	}

	[Test]
	public void FrameCrossParamKeepsBothResourceContracts()
	{
		Type type = typeof(FrameCrossParam);
		Assert.That(type.GetField("mAssetReadPath"), Is.Not.Null);
		Assert.That(type.GetField("mReadPath"), Is.Not.Null);
		Assert.That(type.GetField("mReadPathA"), Is.Not.Null);
		Assert.That(type.GetField("mLang"), Is.Not.Null);
		Assert.That(type.GetField("mVer"), Is.Not.Null);
	}

	[Test]
	public async Task NullResourceFailsWithoutLatchingTheLaunchGate()
	{
		UpdRet<bool> first = await HybridCLRSystem.launch(null).AsTask();
		UpdRet<bool> second = await HybridCLRSystem.launch(null).AsTask();

		Assert.That(first.ok, Is.False);
		Assert.That(first.err.code, Is.EqualTo(UpdCode.Load));
		Assert.That(first.err.detail, Is.EqualTo("upd_res"));
		Assert.That(second.ok, Is.False);
		Assert.That(second.err.code, Is.EqualTo(UpdCode.Load));
		Assert.That(second.err.detail, Is.EqualTo("upd_res"));
	}
}
