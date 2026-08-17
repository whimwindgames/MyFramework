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

	[Test]
	public void RuntimeLoaderReadsSchemaIndexAndNormalizesLogicalAddress()
	{
		AbItem scene = item("Games/Fishing/Scenes/Fishing.unity", "fishing/scene.unity3d");
		scene.scene = "Assets/_Project/Scenes/0.unity";
		AbItem value = item("UI/Main.prefab", "ui/main.unity3d");
		value.name = "ui/main.prefab";
		value.atlas = "MainAtlas";
		byte[] raw = AbIndex.encode(new[] { scene, value });
		TestLoader loader = new();

		loader.Load(raw);

		Assert.That(AbIndex.isCurrent(raw), Is.True);
		Assert.That(loader.isInited(), Is.True);
		AssetBundleInfo bundle = loader.getAssetBundleInfo("ui/main");
		Assert.That(bundle, Is.Not.Null);
		Assert.That(loader.getAssetBundleInfoByKey("UI/Main.prefab"), Is.SameAs(bundle));
		Assert.That(loader.getAssetBundleInfoByKey("missing.prefab"), Is.Null);
		Assert.That(bundle.getAssetInfo("ui/main.prefab"), Is.Not.Null);
		Assert.That(bundle.getAssetInfo("ui/main.prefab").getAssetName(),
			Is.EqualTo("ui/main.prefab"));
		Assert.That(bundle.getAssetInfo("ui/main.prefab").getAssetPath(),
			Is.EqualTo("ui/main.prefab"));
		Assert.That(AbIndex.tryRuntimeAtlas(out AbItem[] atlases), Is.True);
		Assert.That(atlases.Length, Is.EqualTo(1));
		Assert.That(atlases[0].key, Is.EqualTo("UI/Main.prefab"));
		Assert.That(AbIndex.tryRuntimeScene("games/fishing/scenes/fishing.unity",
			out string scenePath), Is.True);
		Assert.That(scenePath, Is.EqualTo("Assets/_Project/Scenes/0.unity"));
		loader.ClearRuntimeIndex();
		Assert.That(AbIndex.tryRuntimeAtlas(out _), Is.False);
		Assert.That(AbIndex.tryRuntimeScene("Games/Fishing/Scenes/Fishing.unity", out _), Is.False);
	}

	[Test]
	public void IndexRejectsCaseInsensitiveLogicalAddressCollision()
	{
		Assert.Throws<InvalidDataException>(() => AbIndex.encode(new[]
		{
			item("UI/Main.prefab", "ui/first.unity3d"),
			item("ui/main.prefab", "ui/second.unity3d"),
		}));
	}

	[Test]
	public void IndexRejectsDuplicateAtlasNames()
	{
		AbItem first = item("ui/first.spriteatlasv2", "ui/first.unity3d");
		first.atlas = "MainAtlas";
		AbItem second = item("ui/second.spriteatlasv2", "ui/second.unity3d");
		second.atlas = "MainAtlas";

		Assert.Throws<InvalidDataException>(() => AbIndex.encode(new[] { first, second }));
	}

	[Test]
	public void EditorCatalogResolvesLogicalAddressThroughSchemaMap()
	{
		Func<string, string> original = AbIndex.keySrc;
		try
		{
			AbIndex.keySrc = null;
			Assert.That(AbIndex.tryEditSrc("logical/readme", out _), Is.False);

			AbIndex.keySrc = key => key == "logical/readme"
				? "Packages/com.whimwindgames.myframework/README.md"
				: null;
			Assert.That(AbIndex.tryEditSrc("logical/readme", out string source), Is.True);
			Assert.That(source, Is.EqualTo("Packages/com.whimwindgames.myframework/README.md"));
			Assert.That(new AssetDataBaseLoader().hasKey("logical/readme"), Is.True);
		}
		finally
		{
			AbIndex.keySrc = original;
		}
	}

	sealed class TestLoader : AssetBundleLoader
	{
		public void Load(byte[] data)
		{
			initAssetConfig(data, "test");
		}

		public void ClearRuntimeIndex()
		{
			clearRuntimeIndex();
		}
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
