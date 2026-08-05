using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;

public sealed class AbIndexTests
{
	[Test]
	public void RoundTripKeepsCanonicalItemsAndDependencies()
	{
		AbItem common = item("common/icon.prefab", "common/common.unity3d");
		AbItem main = item("ui/main.prefab", "ui/main.unity3d");
		main.bdeps.Add("common/common.unity3d");

		List<AbItem> value = AbIndex.decode(AbIndex.encode(new[] { common, main }));

		Assert.That(value.Count, Is.EqualTo(2));
		Assert.That(value[1].key, Is.EqualTo("ui/main.prefab"));
		Assert.That(value[1].bdeps, Is.EqualTo(new[] { "common/common.unity3d" }));
	}

	[Test]
	public void EncodeRejectsNonCanonicalOrderAndMissingBundleDependency()
	{
		Assert.Throws<InvalidDataException>(() => AbIndex.encode(new[]
		{
			item("ui/z.prefab", "ui/ui.unity3d"),
			item("ui/a.prefab", "ui/ui.unity3d"),
		}));

		AbItem value = item("ui/main.prefab", "ui/main.unity3d");
		value.bdeps.Add("missing/missing.unity3d");
		Assert.Throws<InvalidDataException>(() => AbIndex.encode(new[] { value }));
	}

	[Test]
	public void DecodeRejectsTrailingData()
	{
		byte[] raw = AbIndex.encode(Array.Empty<AbItem>());
		byte[] bad = new byte[raw.Length + 1];
		Buffer.BlockCopy(raw, 0, bad, 0, raw.Length);

		Assert.Throws<InvalidDataException>(() => AbIndex.decode(bad));
	}

	static AbItem item(string key, string bundle)
	{
		return new AbItem
		{
			key = key,
			name = key,
			bundle = bundle,
			scene = string.Empty,
			atlas = string.Empty,
		};
	}
}
