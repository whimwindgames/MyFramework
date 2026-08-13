using NUnit.Framework;
using System.Reflection;

public sealed class FileUtilityPlatformTests
{
	[Test]
	public void MacOSUsesSystemFileIO()
	{
		MethodInfo method = typeof(FileUtility).GetMethod("usesSystemFileIO",
			BindingFlags.Static | BindingFlags.NonPublic, null,
			new[] { typeof(bool), typeof(bool), typeof(bool), typeof(bool) }, null);
		Assert.That(method, Is.Not.Null);
		Assert.That(method.Invoke(null, new object[] { false, false, false, true }), Is.True);
		Assert.That(method.Invoke(null, new object[] { false, false, false, false }), Is.False);
	}
}
