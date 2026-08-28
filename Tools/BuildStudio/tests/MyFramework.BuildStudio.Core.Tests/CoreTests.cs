using System.Text.Json;
using MyFramework.BuildStudio;
using MyFramework.BuildStudio.Core;
using Xunit;

namespace MyFramework.BuildStudio.Core.Tests;

public sealed class CoreTests
{
    [Fact]
    public void UnityGeneratedStructureHasMatchingCrossRuntimeHash()
    {
        string root = repositoryRoot();
        ProjectDocument project = ProjectStructureStore.LoadProject(root);

        Assert.Equal(project.Structure.structureHash,
            BuildStudioJson.ComputeStructureHash(project.Structure));
        Assert.Equal(MfBuildSchema.Project, project.Structure.schema);
    }

    [Fact]
    public void StrictJsonRejectsUnknownJobField()
    {
        string json = """
            {"schema":1,"jobId":"job","unexpected":true}
            """;

        Assert.Throws<JsonException>(() => BuildStudioJson.Deserialize<MfBuildJob>(json));
    }

    [Fact]
    public void JobFactoryBindsProfileAndStructure()
    {
        ProjectDocument project = ProjectStructureStore.LoadProject(repositoryRoot());
        MfBuildProfile profile = Assert.Single(project.Structure.profiles,
            value => value.id == "validate");

        MfBuildJob job = BuildJobFactory.Create(project, profile, "test");

        Assert.Equal(project.Structure.structureHash, job.structureHash);
        Assert.Equal("validate", job.action);
        Assert.Equal("Current", job.target);
        Assert.StartsWith("mf-", job.jobId, StringComparison.Ordinal);
    }

    [Fact]
    public void JobFactoryDefaultsAndEnforcesRequiredModules()
    {
        ProjectDocument source = ProjectStructureStore.LoadProject(repositoryRoot());
        MfProjectStructure structure = source.Structure;
        structure.modules =
        [
            new MfModuleInfo { id = "required-game", optional = false },
            new MfModuleInfo { id = "optional-game", optional = true },
        ];
        ProjectDocument project = new(source.ProjectRoot, source.StructurePath, structure);
        MfBuildProfile profile = Assert.Single(structure.profiles,
            value => value.id == "validate");

        MfBuildJob defaults = BuildJobFactory.Create(project, profile, "test");

        Assert.Equal(["required-game"], defaults.modules);
        Assert.Equal(string.Empty, defaults.outputRoot);
        Assert.Throws<InvalidDataException>(() => BuildJobFactory.Create(project, profile,
            "test", modules: []));
        Assert.Throws<InvalidDataException>(() => BuildJobFactory.Create(project, profile,
            "test", modules: ["required-game", "unknown-game"]));
    }

    [Fact]
    public void JobFactoryCreatesUniqueProjectLocalOutputForProducingProfiles()
    {
        ProjectDocument source = ProjectStructureStore.LoadProject(repositoryRoot());
        MfBuildProfile profile = new()
        {
            id = "assets-android",
            action = "assets",
            target = "Android",
            allowedEnvironments = ["test", "prod"],
        };

        MfBuildJob job = BuildJobFactory.Create(source, profile, "test");

        Assert.Equal(Path.Combine(BuildStudioPaths.OutputsRoot, source.Structure.project.id,
            profile.id, job.jobId), job.outputRoot);
        Assert.False(Directory.Exists(job.outputRoot));
    }

    [Fact]
    public void JobFactoryCopiesUploadChoiceIntoImmutableJobInput()
    {
        ProjectDocument project = ProjectStructureStore.LoadProject(repositoryRoot());
        MfBuildProfile profile = Assert.Single(project.Structure.profiles,
            value => value.id == "validate");
        Dictionary<string, string> arguments = new() { ["upload"] = "true" };

        MfBuildJob job = BuildJobFactory.Create(project, profile, "test",
            arguments: arguments);
        arguments["upload"] = "false";

        Assert.Equal("true", job.arguments["upload"]);
    }

    [Fact]
    public void ProfileCatalogSeparatesActionAndPlatformAndHidesAuxiliaryProfiles()
    {
        MfBuildProfile assetsAndroid = profile("assets-android", "assets", "Android",
            "assets", "AssetBundle");
        MfBuildProfile assetsMac = profile("assets-macos", "assets", "StandaloneOSX",
            "assets", "AssetBundle");
        MfBuildProfile releaseAndroid = profile("release-android", "release", "Android",
            "release", "热更新");
        releaseAndroid.properties["supportsUpload"] = "true";
        releaseAndroid.properties["defaultUpload"] = "true";
        MfBuildProfile hidden = profile("validate", "validate", "Current", "validate", "校验");
        hidden.properties["uiHidden"] = "true";

        IReadOnlyList<BuildActionOption> actions = BuildProfileCatalog.Actions(
            [assetsAndroid, assetsMac, releaseAndroid, hidden]);

        Assert.Equal(["assets", "release"], actions.Select(value => value.Id));
        Assert.Equal(["Android", "StandaloneOSX"], BuildProfileCatalog.Platforms(
            [assetsAndroid, assetsMac, releaseAndroid, hidden], "assets")
            .Select(value => value.Target));
        Assert.False(BuildProfileCatalog.SupportsUpload(assetsAndroid));
        Assert.True(BuildProfileCatalog.DefaultUpload(releaseAndroid));
    }

    [Fact]
    public void ProjectConfigurationDisplayShowsAddressesButMasksInlineKeys()
    {
        string root = temporary("configuration-display");
        try
        {
            string shared = Path.Combine(root, "shared.env");
            string configuration = Path.Combine(root, "test.env");
            File.WriteAllText(shared, "FISHING_PRIVATE_KEY=inline-secret-value\n");
            File.WriteAllText(configuration, "source " + shared + "\n" +
                "FISHING_UPDATE_BASE_URL=https://updates.example.test/\n" +
                "FISHING_SSH_HOST=192.0.2.10\n" +
                "FISHING_SSH_KEY=/keys/deploy_ed25519\n");
            MfProjectStructure structure = new();
            MfBuildProfile profile = new()
            {
                properties = new Dictionary<string, string>
                {
                    ["configurationFile"] = configuration,
                    ["configurationDisplay"] =
                        "热更新地址=FISHING_UPDATE_BASE_URL;热更新签名 Key=FISHING_PRIVATE_KEY;" +
                        "上传服务器 IP=FISHING_SSH_HOST;上传 Key=FISHING_SSH_KEY",
                },
            };
            ProjectDocument project = new(root, Path.Combine(root,
                MfBuildSchema.ProjectFileName), structure);

            ProjectConfigurationSnapshot snapshot = ProjectConfigurationReader.Read(project,
                profile);
            IReadOnlyList<ProjectConfigurationDisplayItem> display =
                ProjectConfigurationReader.Describe(profile, snapshot);

            Assert.Null(snapshot.Error);
            Assert.Contains(display, value => value.Label == "热更新地址" &&
                value.Detail == "https://updates.example.test/");
            Assert.Contains(display, value => value.Label == "上传服务器 IP" &&
                value.Detail == "192.0.2.10");
            Assert.Contains(display, value => value.Label == "热更新签名 Key" &&
                value.Detail.Contains("内容已隐藏", StringComparison.Ordinal));
            Assert.DoesNotContain(display.Select(value => value.Detail), value =>
                value.Contains("inline-secret-value", StringComparison.Ordinal));
            Assert.Contains(display, value => value.Label == "上传 Key" &&
                value.Detail.Contains("deploy_ed25519", StringComparison.Ordinal));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void BaseRequirementRunnerSupportsLegacyFishingMenuPath()
    {
        MfProjectStructure structure = new();
        structure.properties["baseRequirementAnalyzer"] =
            "FishGame/Framework/Analyze Base Requirement/Working Tree";
        ProjectDocument project = new("/project", "/project/MyFrameworkProject.json",
            structure);

        string method = BaseRequirementRunner.ResolveAnalyzerMethod(project);

        Assert.Equal("FishGame.EditorTools.FishingBaseRequirementDetector.RunBatch", method);
    }

    [Fact]
    public void BaseRequirementRunnerRejectsUnknownMenuPaths()
    {
        MfProjectStructure structure = new();
        structure.properties["baseRequirementAnalyzer"] = "Tools/Analyze Base";
        ProjectDocument project = new("/project", "/project/MyFrameworkProject.json",
            structure);

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            BaseRequirementRunner.ResolveAnalyzerMethod(project));

        Assert.Contains("Unity 菜单路径", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnityLocatorFindsExactFakeInstallationAndModules()
    {
        string root = temporary("unity-hub");
        try
        {
            string version = "6000.3.11f1";
            string versionRoot = Path.Combine(root, version);
            string executable = OperatingSystem.IsMacOS()
                ? Path.Combine(versionRoot, "Unity.app", "Contents", "MacOS", "Unity")
                : Path.Combine(versionRoot, "Editor", "Unity.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
            File.WriteAllText(executable, string.Empty);
            Directory.CreateDirectory(Path.Combine(versionRoot, "PlaybackEngines",
                "AndroidPlayer"));
            string nativeTarget = OperatingSystem.IsMacOS() ? "StandaloneOSX" :
                "StandaloneWindows64";
            string nativeModule = OperatingSystem.IsMacOS() ? Path.Combine(versionRoot,
                "Unity.app", "Contents", "PlaybackEngines", "MacStandaloneSupport") :
                Path.Combine(versionRoot, "Editor", "Data", "PlaybackEngines",
                    "WindowsStandaloneSupport");
            Directory.CreateDirectory(nativeModule);

            UnityInstallation unity = UnityInstallationLocator.FindExact(version, root);

            Assert.Equal(Path.GetFullPath(executable), unity.EditorPath);
            Assert.True(UnityInstallationLocator.HasTargetModule(unity, "Android"));
            Assert.True(UnityInstallationLocator.HasTargetModule(unity, nativeTarget));
            Assert.False(UnityInstallationLocator.HasTargetModule(unity, "iOS"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task DirtyGitStateIsAdvisoryForFormalProfiles()
    {
        string root = temporary("dirty-preflight");
        string unityRoot = temporary("dirty-preflight-unity");
        try
        {
            string version = "6000.3.11f1";
            string versionRoot = Path.Combine(unityRoot, version);
            string executable = OperatingSystem.IsMacOS()
                ? Path.Combine(versionRoot, "Unity.app", "Contents", "MacOS", "Unity")
                : Path.Combine(versionRoot, "Editor", "Unity.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
            File.WriteAllText(executable, string.Empty);
            MfProjectStructure structure = new();
            structure.unity.version = version;
            structure.managedCode.hybridClrEnabled = true;
            MfBuildProfile profile = new()
            {
                id = "release-current",
                action = "release",
                target = "Current",
                requiresCleanGit = true,
            };
            ProjectDocument project = new(root, Path.Combine(root,
                MfBuildSchema.ProjectFileName), structure);

            PreflightReport report = await BuildPreflight.RunAsync(project, profile,
                unityRoot);

            PreflightItem git = Assert.Single(report.Items, value => value.Id == "git");
            Assert.False(git.Ok);
            Assert.Equal(PreflightSeverity.Warning, git.Severity);
            Assert.True(report.CanBuild);
            Assert.Contains("不影响构建", git.Detail, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
            if (Directory.Exists(unityRoot)) Directory.Delete(unityRoot, true);
        }
    }

    [Fact]
    public async Task ProfileRequirementsFailBeforeStartingUnityWorker()
    {
        string root = temporary("profile-requirements");
        string unityRoot = temporary("profile-requirements-unity");
        try
        {
            string version = "6000.3.11f1";
            string versionRoot = Path.Combine(unityRoot, version);
            string executable = OperatingSystem.IsMacOS()
                ? Path.Combine(versionRoot, "Unity.app", "Contents", "MacOS", "Unity")
                : Path.Combine(versionRoot, "Editor", "Unity.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
            File.WriteAllText(executable, string.Empty);
            MfProjectStructure structure = new();
            structure.unity.version = version;
            MfBuildProfile profile = new()
            {
                id = "base-current",
                action = "base",
                target = "Current",
                properties = new Dictionary<string, string>
                {
                    ["requiredEnvironment"] =
                        "MF_TEST_REQUIRED_VALUE,MF_TEST_PUBLIC_VALUE|MF_TEST_PUBLIC_FILE",
                    ["requiresLocalHybridClr"] = "true",
                },
            };
            ProjectDocument project = new(root, Path.Combine(root,
                MfBuildSchema.ProjectFileName), structure);

            PreflightReport report = await BuildPreflight.RunAsync(project, profile,
                unityRoot);

            PreflightItem environment = Assert.Single(report.Items,
                value => value.Id == "environment");
            Assert.Equal(PreflightSeverity.Error, environment.Severity);
            Assert.Contains("MF_TEST_REQUIRED_VALUE", environment.Detail,
                StringComparison.Ordinal);
            PreflightItem hybrid = Assert.Single(report.Items,
                value => value.Id == "hybridclr-local");
            Assert.Equal(PreflightSeverity.Error, hybrid.Severity);
            Assert.False(report.CanBuild);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
            if (Directory.Exists(unityRoot)) Directory.Delete(unityRoot, true);
        }
    }

    [Fact]
    public async Task ProjectConfigurationFileIsValidatedBeforeStartingUnityWorker()
    {
        string root = temporary("profile-configuration");
        string unityRoot = temporary("profile-configuration-unity");
        try
        {
            string version = "6000.3.11f1";
            string versionRoot = Path.Combine(unityRoot, version);
            string executable = OperatingSystem.IsMacOS()
                ? Path.Combine(versionRoot, "Unity.app", "Contents", "MacOS", "Unity")
                : Path.Combine(versionRoot, "Editor", "Unity.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
            File.WriteAllText(executable, string.Empty);
            Directory.CreateDirectory(Path.Combine(versionRoot, "PlaybackEngines",
                "AndroidPlayer"));
            string configuration = Path.Combine(root, "test.env");
            MfProjectStructure structure = new();
            structure.unity.version = version;
            structure.managedCode.hybridClrEnabled = true;
            MfBuildProfile profile = new()
            {
                id = "integrated-android",
                action = "integrated",
                target = "Android",
                properties = new Dictionary<string, string>
                {
                    ["configurationFile"] = configuration,
                },
            };
            ProjectDocument project = new(root, Path.Combine(root,
                MfBuildSchema.ProjectFileName), structure);

            PreflightReport missing = await BuildPreflight.RunAsync(project, profile,
                unityRoot);
            PreflightItem missingItem = Assert.Single(missing.Items,
                value => value.Id == "configuration-file");
            Assert.Equal(PreflightSeverity.Error, missingItem.Severity);
            Assert.False(missing.CanBuild);

            File.WriteAllText(configuration, "FISHING_ENV=test\n");
            PreflightReport present = await BuildPreflight.RunAsync(project, profile,
                unityRoot);
            PreflightItem presentItem = Assert.Single(present.Items,
                value => value.Id == "configuration-file");
            Assert.True(presentItem.Ok);
            Assert.True(present.CanBuild);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
            if (Directory.Exists(unityRoot)) Directory.Delete(unityRoot, true);
        }
    }

    [Fact]
    public async Task ExistingBasePreflightRequiresBaselineAndReleaseTrust()
    {
        string root = temporary("existing-base");
        string unityRoot = temporary("existing-base-unity");
        try
        {
            const string version = "6000.3.11f1";
            string versionRoot = Path.Combine(unityRoot, version);
            string executable = OperatingSystem.IsMacOS()
                ? Path.Combine(versionRoot, "Unity.app", "Contents", "MacOS", "Unity")
                : Path.Combine(versionRoot, "Editor", "Unity.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
            File.WriteAllText(executable, string.Empty);
            string baselineRoot = Path.Combine(root, "baseline");
            string releaseRoot = Path.Combine(root, "release");
            string configuration = Path.Combine(root, "test.env");
            File.WriteAllText(configuration, "FISHING_BASE_ID=base-1\n" +
                "FISHING_BASELINE_ROOT=" + baselineRoot + "\n" +
                "FISHING_RELEASE_ROOT=" + releaseRoot + "\n");
            Directory.CreateDirectory(Path.Combine(baselineRoot, "test", "base-1", "Current"));
            File.WriteAllText(Path.Combine(baselineRoot, "test", "base-1", "Current",
                ".base-cap"), "ok");
            MfProjectStructure structure = new();
            structure.unity.version = version;
            structure.managedCode.hybridClrEnabled = true;
            MfBuildProfile profile = new()
            {
                id = "release-current",
                action = "release",
                target = "Current",
                allowedEnvironments = ["test"],
                properties = new Dictionary<string, string>
                {
                    ["configurationFile"] = configuration,
                    ["supportsUpload"] = "true",
                    ["existingBaseIdVariable"] = "FISHING_BASE_ID",
                    ["existingBaseRootVariable"] = "FISHING_BASELINE_ROOT",
                    ["existingBaseReleaseRootVariable"] = "FISHING_RELEASE_ROOT",
                    ["existingBasePublishPlatform"] = "Current",
                },
            };
            ProjectDocument project = new(root, Path.Combine(root,
                MfBuildSchema.ProjectFileName), structure);

            PreflightReport missingTrust = await BuildPreflight.RunAsync(project, profile,
                unityRoot);
            Assert.Equal(PreflightSeverity.Error, Assert.Single(missingTrust.Items,
                value => value.Id == "existing-base").Severity);

            string trust = Path.Combine(releaseRoot, "test", "base", "Current",
                "base-1.json");
            Directory.CreateDirectory(Path.GetDirectoryName(trust)!);
            File.WriteAllText(trust, "{}");
            PreflightReport valid = await BuildPreflight.RunAsync(project, profile, unityRoot);

            Assert.True(Assert.Single(valid.Items,
                value => value.Id == "existing-base").Ok);
            Assert.True(valid.CanBuild);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
            if (Directory.Exists(unityRoot)) Directory.Delete(unityRoot, true);
        }
    }

    [Fact]
    public void HistoryRoundTripsReceipt()
    {
        string root = temporary("history");
        try
        {
            BuildHistoryRepository history = new(Path.Combine(root, "history.db"));
            history.Add(new MfBuildReceipt
            {
                jobId = "job-1",
                projectId = "project",
                profileId = "validate",
                action = "validate",
                target = "Current",
                environment = "test",
                status = "succeeded",
                ok = true,
                durationMs = 12,
                startedAtUtc = "2026-08-17T00:00:00.0000000Z",
                outputRoot = root,
            });

            BuildHistoryItem item = Assert.Single(history.List());
            Assert.Equal("job-1", item.JobId);
            Assert.True(item.Ok);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task EditorCloseCoordinatorUsesTokenBoundHandshake()
    {
        string root = temporary("editor-close");
        try
        {
            string temp = Path.Combine(root, "Temp");
            string control = Path.Combine(temp, EditorCloseCoordinator.ControlDirectoryName);
            string request = Path.Combine(control, EditorCloseCoordinator.RequestFileName);
            string ack = Path.Combine(control, EditorCloseCoordinator.AckFileName);
            string unityLock = Path.Combine(temp, "UnityLockfile");
            Directory.CreateDirectory(temp);
            File.WriteAllText(unityLock, string.Empty);
            Task editor = Task.Run(async () =>
            {
                while (!File.Exists(request)) await Task.Delay(10);
                string token = (await File.ReadAllTextAsync(request)).Trim();
                Directory.CreateDirectory(control);
                await File.WriteAllTextAsync(ack, token + "\nok\n");
                File.Delete(unityLock);
            });

            await EditorCloseCoordinator.EnsureClosedAsync(root,
                timeout: TimeSpan.FromSeconds(5));
            await editor;

            Assert.False(File.Exists(request));
            Assert.False(File.Exists(ack));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    static string repositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, MfBuildSchema.ProjectFileName)) &&
                Directory.Exists(Path.Combine(directory.FullName, "Packages")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("MyFramework repository root was not found.");
    }

    static MfBuildProfile profile(string id, string action, string target,
        string uiAction, string uiLabel) => new()
    {
        id = id,
        displayName = uiLabel,
        action = action,
        target = target,
        properties = new Dictionary<string, string>
        {
            ["uiAction"] = uiAction,
            ["uiActionLabel"] = uiLabel,
        },
    };

    static string temporary(string name)
    {
        string root = Path.Combine(repositoryRoot(), "tmp", "BuildStudioDotnetTests",
            name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
