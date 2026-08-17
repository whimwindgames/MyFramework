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
        Assert.Throws<InvalidDataException>(() => BuildJobFactory.Create(project, profile,
            "test", modules: []));
        Assert.Throws<InvalidDataException>(() => BuildJobFactory.Create(project, profile,
            "test", modules: ["required-game", "unknown-game"]));
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

            UnityInstallation unity = UnityInstallationLocator.FindExact(version, root);

            Assert.Equal(Path.GetFullPath(executable), unity.EditorPath);
            Assert.True(UnityInstallationLocator.HasTargetModule(unity, "Android"));
            Assert.False(UnityInstallationLocator.HasTargetModule(unity, "iOS"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
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

    static string temporary(string name)
    {
        string root = Path.Combine(repositoryRoot(), "tmp", "BuildStudioDotnetTests",
            name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
