using System;
using System.IO;
using NUnit.Framework;

public sealed class CustomHotEntryTests
{
	private const string CustomEntry = "FishGame.Framework.HotFix.dll.bytes";

	[Test]
	public void ProductionContractAcceptsExplicitCustomEntryAfterFrameHotFix()
	{
		string[] hot =
		{
			UpdContract.FrameHotDll,
			CustomEntry,
		};
		UpdCfg configuration = Configuration(hot, CustomEntry);
		HotSet set = new(hot, CustomEntry);

		Assert.DoesNotThrow(() => UpdRule.prod(configuration));
		Assert.DoesNotThrow(() => HotList.chk(set));
	}

	[Test]
	public void ProductionContractRejectsCustomEntryBeforeFrameHotFix()
	{
		string[] hot =
		{
			CustomEntry,
			UpdContract.FrameHotDll,
		};
		UpdCfg configuration = Configuration(hot, CustomEntry);
		HotSet set = new(hot, CustomEntry);

		Assert.Throws<UpdBad>(() => UpdRule.prod(configuration));
		Assert.Throws<InvalidDataException>(() => HotList.chk(set));
	}

	[Test]
	public void DefaultHotFixEntryRemainsCompatible()
	{
		string[] hot =
		{
			UpdContract.FrameHotDll,
			UpdContract.EntryDll,
		};

		Assert.DoesNotThrow(() => UpdRule.prod(Configuration(hot, UpdContract.EntryDll)));
		Assert.DoesNotThrow(() => HotList.chk(new HotSet(hot, UpdContract.EntryDll)));
	}

	private static UpdCfg Configuration(string[] hot, string entry)
	{
		return new UpdCfg
		{
			baseUrl = "https://updates.example.com/game",
			env = "test",
			platform = FrameBaseDefine.MACOS,
			baseId = "base-custom-entry",
			pubKey = "public-key",
			aotDlls = Array.Empty<string>(),
			codeDlls = hot,
			entryDll = entry,
			hotId = UpdRule.hotId(hot, entry),
			secret = string.Empty,
			resList = FrameBaseDefine.AB_INDEX_FILE,
		};
	}
}
