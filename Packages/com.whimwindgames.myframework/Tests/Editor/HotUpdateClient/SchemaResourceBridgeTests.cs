using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

public sealed class SchemaResourceBridgeTests
{
	[TearDown]
	public void TearDown()
	{
		FrameCrossParam.mReadPath = null;
		FrameCrossParam.mReadPathA = null;
	}

	[Test]
	public void ReleaseResolverOverridesLegacyReadPath()
	{
		FrameCrossParam.mReadPath = path => "/release/" + path;

		Assert.That(FrameUtility.availableReadPath("ui/main.ab"), Is.EqualTo("/release/ui/main.ab"));
		Assert.That(FrameUtility.getReadPath("data/table.ab"), Is.EqualTo("/release/data/table.ab"));
	}

	[Test]
	public async Task AsyncReleaseResolverKeepsCancellationContract()
	{
		FrameCrossParam.mReadPathA = (path, ct) => Task.FromResult("/release/" + path);

		string value = await FrameUtility.getReadPathA("audio/bgm.ab", CancellationToken.None);

		Assert.That(value, Is.EqualTo("/release/audio/bgm.ab"));
	}

	[Test]
	public void EditorLaunchPreparationPreservesHostResourceBridge()
	{
		Func<string, string> read = path => "/editor-stage/" + path;
		Func<string, CancellationToken, Task<string>> readAsync =
			(path, ct) => Task.FromResult("/editor-stage/" + path);
		FrameCrossParam.mReadPath = read;
		FrameCrossParam.mReadPathA = readAsync;

		MethodInfo prepare = typeof(HybridCLRSystem).GetMethod("schemaPrepareEdit",
			BindingFlags.Static | BindingFlags.NonPublic);
		Assert.That(prepare, Is.Not.Null);
		prepare.Invoke(null, null);

		Assert.That(FrameCrossParam.mReadPath, Is.SameAs(read));
		Assert.That(FrameCrossParam.mReadPathA, Is.SameAs(readAsync));
		Assert.That(FrameUtility.availableReadPath("StreamingAssets.bytes"),
			Is.EqualTo("/editor-stage/StreamingAssets.bytes"));
	}

	[Test]
	public void HotFixPreStartKeepsLegacyAndSchema11Overloads()
	{
		Type type = typeof(GameHotFixBase<>);
		BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;

		Assert.That(type.GetMethod("preStart", flags, null,
			new Type[1] { typeof(Action) }, null), Is.Not.Null);
		Assert.That(type.GetMethod("preStart", flags, null,
			new Type[2] { typeof(byte[]), typeof(Action) }, null), Is.Not.Null);
	}
}
