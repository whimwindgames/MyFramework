using System;
using NUnit.Framework;

public class FrameBaseUtilityPathTests
{
	[Test]
	public void CheckDownloadPathEncodesUnicodeAndSpaces()
	{
		const string raw = "/Volumes/External SSD/切图 3/背景图片+文本/熊猫调色.unity3d";
		string url = raw;

		FrameBaseUtility.checkDownloadPath(ref url);

		Assert.That(url, Does.StartWith("file:///Volumes/"));
		Assert.That(url, Does.Contain("%20"));
		Assert.That(url, Does.Contain("%2B"));
		Assert.That(url, Does.Not.Contain("切图"));
		Assert.That(new Uri(url).LocalPath, Is.EqualTo(raw));
	}

	[Test]
	public void CheckDownloadPathRepairsLegacyFourSlashUrl()
	{
		const string raw = "/Volumes/External SSD/切图 3.unity3d";
		string url = "file:///" + raw;

		FrameBaseUtility.checkDownloadPath(ref url);

		Assert.That(url, Does.StartWith("file:///Volumes/"));
		Assert.That(url, Does.Not.StartWith("file:////"));
		Assert.That(new Uri(url).LocalPath, Is.EqualTo(raw));
	}
}
