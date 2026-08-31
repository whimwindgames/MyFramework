using System;
using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEngine.TestTools;

public sealed class DllMetaTests
{
	string mRoot;
	string mProject;

	[SetUp]
	public void SetUp()
	{
		string temp = Directory.Exists("/private/tmp") ? "/private/tmp" : Path.GetTempPath();
		mRoot = Path.Combine(temp, "myframework-meta-" + Guid.NewGuid().ToString("N"));
		mProject = Path.GetFullPath(Path.Combine(UnityEngine.Application.dataPath, ".."));
		Directory.CreateDirectory(mRoot);
	}

	[TearDown]
	public void TearDown()
	{
		LogAssert.ignoreFailingMessages = false;
		if (Directory.Exists(mRoot)) Directory.Delete(mRoot, true);
	}

	[Test]
	public void GeneratedAotParserReturnsCanonicalAssemblies()
	{
		string[] result = DllMeta.parseGeneratedAotAssemblies(new[]
		{
			"public class AOTGenericReferences",
			"{",
			"    // {{ AOT assemblies",
			"    public static readonly string[] Values =",
			"    {",
			"        \"Frame_Base.dll\",",
			"        \"System.Private.CoreLib.dll\",",
			"    };",
			"    // }}",
			"}",
		});

		Assert.That(result, Is.EqualTo(new[]
		{
			"Frame_Base.dll",
			"System.Private.CoreLib.dll",
		}));
	}

	[TestCaseSource(nameof(BadGeneratedLists))]
	public void GeneratedAotParserRejectsNonCanonicalInput(string[] lines)
	{
		Assert.Throws<InvalidDataException>(() => DllMeta.parseGeneratedAotAssemblies(lines));
	}

	static object[] BadGeneratedLists => new object[]
	{
		new[] { "// {{ AOT assemblies", "\"Frame_Base.dll\"," },
		new[] { "// {{ AOT assemblies", "\"Frame_Base.dll\"", "// }}" },
		new[] { "// {{ AOT assemblies", "\"netstandard.dll\",", "// }}" },
		new[]
		{
			"// {{ AOT assemblies", "\"System.Private.CoreLib.dll\",",
			"\"Frame_Base.dll\",", "// }}",
		},
		new[]
		{
			"// {{ AOT assemblies", "\"Frame_Base.dll\",",
			"\"frame_base.dll\",", "// }}",
		},
	};

	[Test]
	public void NewAotRequirementForcesNewBase()
	{
		InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
			DllMeta.ensurePatchAotSubset(new[] { "Frame_Base.dll" },
				new[] { "Frame_Base.dll", "System.Private.CoreLib.dll" }));

		StringAssert.Contains("新Base ID", error.Message);
	}

	[Test]
	public void WithAotReturnsIndependentCanonicalConfig()
	{
		UpdCfg source = cfg();
		UpdCfg value = DllBuild.withAot(source, new[]
		{
			"Frame_Base.dll.bytes",
			"System.Private.CoreLib.dll.bytes",
		});

		Assert.That(source.aotDlls, Is.Empty);
		Assert.That(value, Is.Not.SameAs(source));
		Assert.That(value.codeDlls, Is.Not.SameAs(source.codeDlls));
		Assert.That(value.aotDlls, Is.EqualTo(new[]
		{
			"Frame_Base.dll.bytes",
			"System.Private.CoreLib.dll.bytes",
		}));
	}

	[Test]
	public void UnitySystemReferenceResolvesForExplicitTarget()
	{
		string facade = UnityRefPath.netstandard(BuildTarget.StandaloneOSX);

		Assert.That(File.Exists(facade), Is.True);
		Assert.That(Path.GetFileName(facade), Is.EqualTo("netstandard.dll"));
	}

	[Test]
	public void CandidateAotBaselineIsMappedUnderBuildTargetAndCleaned()
	{
		string candidate = Path.Combine(mRoot,
			"StandaloneOSX.candidate-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(candidate);
		copy("Frame_Base", candidate);
		string mappedRoot;
		using (DllMeta.AotResolverTx tx = new(new AotBaseInfo
		{
			path = candidate,
			dlls = new[] { "Frame_Base.dll" },
		}, BuildTarget.StandaloneOSX))
		{
			mappedRoot = tx.root;
			Assert.That(mappedRoot, Is.Not.EqualTo(mRoot));
			Assert.That(File.Exists(Path.Combine(mappedRoot, "StandaloneOSX",
				"Frame_Base.dll")), Is.True);
		}
		Assert.That(Directory.Exists(mappedRoot), Is.False);
	}

	[Test]
	public void PromotedAotBaselineUsesItsParentWithoutCopying()
	{
		string target = Path.Combine(mRoot, BuildTarget.StandaloneOSX.ToString());
		Directory.CreateDirectory(target);
		copy("Frame_Base", target);
		using DllMeta.AotResolverTx tx = new(new AotBaseInfo
		{
			path = target,
			dlls = new[] { "Frame_Base.dll" },
		}, BuildTarget.StandaloneOSX);

		Assert.That(tx.root, Is.EqualTo(mRoot));
		Assert.That(File.Exists(Path.Combine(target, "Frame_Base.dll")), Is.True);
	}

	[Test]
	public void MissingMetadataCheckerRejectsIncompleteFrozenBase()
	{
		string baseline = Path.Combine(mRoot, "baseline");
		string hotDir = Path.Combine(mRoot, "hot");
		Directory.CreateDirectory(baseline);
		Directory.CreateDirectory(hotDir);
		copy("Frame_Base", baseline);
		copy(FrameBaseDefine.HOTFIX_FRAME, hotDir);
		copy(FrameBaseDefine.HOTFIX, hotDir);
		HotSet hot = new(new[]
		{
			FrameBaseDefine.HOTFIX_FRAME_BYTES_FILE,
			FrameBaseDefine.HOTFIX_BYTES_FILE,
		}, FrameBaseDefine.HOTFIX_BYTES_FILE);
		LogAssert.ignoreFailingMessages = true;

		InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
			DllMeta.checkMissing(baseline, hotDir, hot, BuildTarget.StandaloneOSX));

		StringAssert.Contains("已经裁剪", error.Message);
		Assert.That(Directory.GetDirectories(Path.Combine(mProject, "Library", "MyFramework",
			"HotUpd"), "Meta-*", SearchOption.TopDirectoryOnly), Is.Empty);
	}

	UpdCfg cfg()
	{
		string[] hot =
		{
			FrameBaseDefine.HOTFIX_FRAME_BYTES_FILE,
			FrameBaseDefine.HOTFIX_BYTES_FILE,
		};
		return new UpdCfg
		{
			baseUrl = "https://cdn.example.com/game/",
			env = "test",
			platform = FrameBaseDefine.MACOS,
			baseId = "base-meta",
			pubKey = "key",
			aotDlls = Array.Empty<string>(),
			codeDlls = hot,
			entryDll = FrameBaseDefine.HOTFIX_BYTES_FILE,
			hotId = UpdRule.hotId(hot, FrameBaseDefine.HOTFIX_BYTES_FILE),
			secret = string.Empty,
			resList = FrameBaseDefine.AB_INDEX_FILE,
		};
	}

	void copy(string name, string target)
	{
		string source = Path.Combine(mProject, "Library", "ScriptAssemblies", name + ".dll");
		Assert.That(File.Exists(source), Is.True, "测试需要Unity先完成脚本编译:" + source);
		File.Copy(source, Path.Combine(target, name + ".dll"), false);
	}
}
