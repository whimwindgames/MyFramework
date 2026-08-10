using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;

public sealed class RelGateTests
{
	const string TEST_SCRIPT =
		"Packages/com.whimwindgames.myframework/Tests/Editor/HotUpdateClient/RelGateTests.cs";
	IDisposable mRegistry;

	sealed class ProbeGate : IRelGate
	{
		readonly Action<RelGateInput, RelGateOutput> mCheck;
		public string id { get; }
		public int order { get; }
		public RelGatePhase phases { get; }

		internal ProbeGate(string id, int order, RelGatePhase phases,
			Action<RelGateInput, RelGateOutput> check)
		{
			this.id = id;
			this.order = order;
			this.phases = phases;
			mCheck = check;
		}

		public void check(RelGateInput input, RelGateOutput output)
		{
			mCheck(input, output);
		}
	}

	[SetUp]
	public void SetUp()
	{
		mRegistry = RelGateRegistry.isolateForTests();
	}

	[TearDown]
	public void TearDown()
	{
		mRegistry?.Dispose();
		mRegistry = null;
	}

	[Test]
	public void MonoScriptGateAcceptsAssemblyFrozenInBase()
	{
		RelGateReport report = RelGateRunner.run(input(
			new[] { "HotUpd_Client.Tests.dll" }), RelGatePhase.Plan);

		Assert.That(report.ok, Is.True);
		Assert.That(report.diagnostics, Is.Empty);
	}

	[Test]
	public void MonoScriptGateReportsAssemblyMissingFromHotAndBase()
	{
		RelGateReport report = RelGateRunner.run(input(Array.Empty<string>()),
			RelGatePhase.Plan);

		Assert.That(report.ok, Is.False);
		Assert.That(report.diagnostics, Has.Length.EqualTo(1));
		Assert.That(report.diagnostics[0].gate, Is.EqualTo("framework.mono-script"));
		Assert.That(report.diagnostics[0].code, Is.EqualTo("mono.assembly_missing"));
		Assert.That(report.diagnostics[0].path, Is.EqualTo("HotUpd_Client.Tests"));
	}

	[Test]
	public void RequiredAssetGateIsDeclarativeAndEnvironmentScoped()
	{
		RelGateRegistry.register(new RelRequiredAssetGate("sample.required",
			new[]
			{
				new RelRequiredAsset("config/gate.json", TEST_SCRIPT, "test"),
				new RelRequiredAsset("config/prod-only.json", TEST_SCRIPT, "prod"),
			}));
		RelGateInput value = input(new[] { "HotUpd_Client.Tests.dll" },
			"config/gate.json");

		RelGateReport report = RelGateRunner.run(value, RelGatePhase.Plan);

		Assert.That(report.ok, Is.True);
		Assert.That(report.diagnostics, Is.Empty);
	}

	[Test]
	public void RunnerConvertsPluginExceptionAndOrdersDiagnostics()
	{
		RelGateRegistry.register(new ProbeGate("sample.second", 300,
			RelGatePhase.Project, (_, output) =>
			{
				output.warning("sample.z", "warning");
				output.error("sample.a", "error");
			}));
		RelGateRegistry.register(new ProbeGate("sample.first", 200,
			RelGatePhase.Project, (_, _) => throw new IOException("broken")));

		RelGateReport report = RelGateRunner.run(
			input(new[] { "HotUpd_Client.Tests.dll" }), RelGatePhase.Project);

		Assert.That(report.ok, Is.False);
		Assert.That(report.diagnostics, Has.Length.EqualTo(3));
		Assert.That(report.diagnostics[0].gate, Is.EqualTo("sample.first"));
		Assert.That(report.diagnostics[0].code, Is.EqualTo("gate.exception"));
		Assert.That(report.diagnostics[1].code, Is.EqualTo("sample.a"));
		Assert.That(report.diagnostics[2].code, Is.EqualTo("sample.z"));
	}

	[Test]
	public void RunnerRejectsPluginMutationOfInput()
	{
		RelGateRegistry.register(new ProbeGate("sample.mutating", 10,
			RelGatePhase.Project, (value, _) => value.assets.astCnt++));

		RelGateReport report = RelGateRunner.run(
			input(new[] { "HotUpd_Client.Tests.dll" }), RelGatePhase.Project);

		Assert.That(report.ok, Is.False);
		Assert.That(report.diagnostics, Has.Some.Matches<RelGateDiagnostic>(
			item => item.code == "gate.input_mutated"));
	}

	[Test]
	public void UnifiedCliUsesRegisteredInputProviderAndReturnsJsonReport()
	{
		RelGateInput value = input(new[] { "HotUpd_Client.Tests.dll" });
		RelGateRegistry.bindInput(request =>
		{
			Assert.That(request.env, Is.EqualTo("test"));
			Assert.That(request.phases, Is.EqualTo(RelGatePhase.Plan));
			return value;
		});

		int code = RelGateCli.run(new[]
		{
			"-gateEnv", "test",
			"-gatePlatform", "Android",
			"-gateBaseId", "base-1",
			"-gatePhase", "plan",
		}, out RelGateReceipt receipt);

		Assert.That(code, Is.Zero);
		Assert.That(receipt.ok, Is.True);
		Assert.That(receipt.report.schema, Is.EqualTo(1));
		Assert.That(receipt.report.env, Is.EqualTo("test"));
	}

	static RelGateInput input(string[] baseAot, string key = "config/gate.json")
	{
		UpdCfg cfg = new()
		{
			baseUrl = "https://hot.test/",
			env = "test",
			platform = "Android",
			baseId = "base-1",
			pubKey = "test-public-key",
			aotDlls = Array.Empty<string>(),
			codeDlls = new[]
			{
				FrameBaseDefine.HOTFIX_FRAME_BYTES_FILE,
				FrameBaseDefine.HOTFIX_BYTES_FILE,
			},
			entryDll = FrameBaseDefine.HOTFIX_BYTES_FILE,
			resList = FrameBaseDefine.AB_INDEX_FILE,
		};
		cfg.hotId = UpdRule.hotId(cfg.codeDlls, cfg.entryDll);
		AbPlan plan = new() { astCnt = 1 };
		AbPkg pkg = new() { name = "gate-tests", key = "gate-tests" };
		pkg.asts.Add(new AbAst
		{
			path = TEST_SCRIPT,
			key = key,
			name = "RelGateTests",
		});
		plan.pkgs.Add(pkg);
		return new RelGateInput(cfg, HotList.fromCfg(cfg), plan,
			new RelGateBase(cfg, baseAot));
	}
}
