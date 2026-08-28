using System.Diagnostics;
using MyFramework.BuildStudio;

namespace MyFramework.BuildStudio.Core;

public enum PreflightSeverity { Info, Warning, Error }

public sealed record PreflightItem(string Id, string Label, bool Ok,
    PreflightSeverity Severity, string Detail);

public sealed class PreflightReport
{
    public required ProjectDocument Project { get; init; }
    public required MfBuildProfile Profile { get; init; }
    public UnityInstallation? Unity { get; init; }
    public List<PreflightItem> Items { get; } = [];
    public bool CanBuild => Items.All(item => item.Ok || item.Severity != PreflightSeverity.Error);
}

public static class BuildPreflight
{
    public static async Task<PreflightReport> RunAsync(ProjectDocument project,
        MfBuildProfile profile, string? customHubRoot = null,
        CancellationToken cancellationToken = default)
        => await RunAsync(project, profile, customHubRoot, null, cancellationToken);

    public static async Task<PreflightReport> RunAsync(ProjectDocument project,
        MfBuildProfile profile, string? customHubRoot,
        IReadOnlyDictionary<string, string>? arguments,
        CancellationToken cancellationToken = default)
    {
        UnityInstallation? unity = null;
        PreflightReport report = new() { Project = project, Profile = profile };
        try
        {
            unity = UnityInstallationLocator.FindExact(project.Structure.unity.version,
                customHubRoot);
            report.Items.Add(ok("unity", "Unity 精确版本", unity.Version));
        }
        catch (Exception exception)
        {
            report.Items.Add(error("unity", "Unity 精确版本", exception.Message));
        }
        if (unity is not null)
        {
            report = new PreflightReport
            {
                Project = project,
                Profile = profile,
                Unity = unity,
            }.with(report.Items);
            bool target = UnityInstallationLocator.HasTargetModule(unity, profile.target);
            report.Items.Add(target ? ok("module", "平台模块", profile.target) :
                error("module", "平台模块", "Missing module for " + profile.target));
        }
        addProfileRequirements(report, project, profile, arguments);
        bool open = File.Exists(Path.Combine(project.ProjectRoot, "Temp", "UnityLockfile"));
        report.Items.Add(open ? new PreflightItem("project-open", "Unity 项目占用", false,
            PreflightSeverity.Warning, "构建时将请求保存并关闭当前 Unity Editor。") :
            ok("project-open", "Unity 项目占用", "未占用"));
        string git = await gitStatus(project.ProjectRoot, cancellationToken);
        bool clean = string.IsNullOrWhiteSpace(git);
        report.Items.Add(clean ? ok("git", "Git 工作区", "干净") : new PreflightItem(
            "git", "Git 工作区", false, PreflightSeverity.Warning,
            git == "git unavailable"
                ? "Git 不可用；已跳过检查，仅提醒且不影响构建。"
                : "检测到未提交修改；建议自行确认，仅提醒且不影响构建。"));
        report.Items.Add(string.IsNullOrWhiteSpace(project.Structure.content.assetBundleConfig) ||
                         project.Structure.content.bundleRoots.Count > 0
            ? ok("content", "AssetBundle 计划",
                project.Structure.content.bundleRoots.Count + " 个根")
            : error("content", "AssetBundle 计划", "AB 配置没有 Bundle 根。"));
        report.Items.Add(project.Structure.managedCode.hybridClrEnabled ||
                         profile.action is "validate" or "assets"
            ? ok("hybridclr", "HybridCLR", project.Structure.managedCode.hybridClrEnabled ?
                "已启用" : "该任务不需要")
            : error("hybridclr", "HybridCLR", "当前任务要求启用 HybridCLR。"));
        DriveInfo drive = new(Path.GetPathRoot(project.ProjectRoot)!);
        long freeGiB = drive.AvailableFreeSpace / (1024L * 1024L * 1024L);
        report.Items.Add(freeGiB >= 20 ? ok("disk", "磁盘空间", freeGiB + " GiB 可用") :
            error("disk", "磁盘空间", "少于 20 GiB。"));
        return report;
    }

    static void addProfileRequirements(PreflightReport report, ProjectDocument project,
        MfBuildProfile profile, IReadOnlyDictionary<string, string>? arguments)
    {
        if (profile.properties.TryGetValue("configurationFile", out string? configured) &&
            !string.IsNullOrWhiteSpace(configured))
        {
            try
            {
                string full = ProjectConfigurationReader.ResolvePath(configured);
                report.Items.Add(File.Exists(full)
                    ? ok("configuration-file", "项目发布配置", full)
                    : error("configuration-file", "项目发布配置", "配置文件不存在: " + full));
            }
            catch (Exception exception)
            {
                report.Items.Add(error("configuration-file", "项目发布配置",
                    exception.Message));
            }
        }

        if (profile.properties.TryGetValue("requiredEnvironment", out string? declaration) &&
            !string.IsNullOrWhiteSpace(declaration))
        {
            string[] groups = declaration.Split(',', StringSplitOptions.RemoveEmptyEntries |
                StringSplitOptions.TrimEntries);
            string[] missing = groups.Where(group => group.Split('|',
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .All(name => string.IsNullOrWhiteSpace(
                    Environment.GetEnvironmentVariable(name))))
                .Select(group => group.Replace("|", " 或 ", StringComparison.Ordinal))
                .ToArray();
            report.Items.Add(missing.Length == 0
                ? ok("environment", "外部配置", "已提供")
                : error("environment", "外部配置", "缺少 " + string.Join("、", missing) +
                    "；本地测试请选择无需外部配置的本地构建 Profile。"));
        }

        if (profile.properties.TryGetValue("requiresLocalHybridClr", out string? local) &&
            string.Equals(local, "true", StringComparison.OrdinalIgnoreCase))
        {
            string host = OperatingSystem.IsMacOS() ? "OSXEditor" :
                OperatingSystem.IsWindows() ? "WindowsEditor" : string.Empty;
            string path = Path.Combine(project.ProjectRoot, "HybridCLRData",
                "LocalIl2CppData-" + host, "il2cpp");
            report.Items.Add(host.Length > 0 && Directory.Exists(path)
                ? ok("hybridclr-local", "HybridCLR 本地 IL2CPP", "已安装")
                : error("hybridclr-local", "HybridCLR 本地 IL2CPP",
                    "尚未安装；请在 Unity 执行 HybridCLR/Installer。"));
        }

        bool upload = arguments is not null && arguments.TryGetValue("upload",
            out string? uploadValue) && string.Equals(uploadValue, "true",
            StringComparison.OrdinalIgnoreCase);
        if (BuildProfileCatalog.SupportsUpload(profile))
        {
            ProjectConfigurationSnapshot snapshot = ProjectConfigurationReader.Read(project,
                profile);
            IReadOnlyList<ProjectConfigurationDisplayItem> display =
                ProjectConfigurationReader.Describe(profile, snapshot);
            string[] missingUpload = display.Where(item => item.Label.Contains("上传",
                    StringComparison.Ordinal) && !item.Configured)
                .Select(item => item.Label).ToArray();
            report.Items.Add(!upload ? ok("upload", "上传发布", "未启用；只保留本地产物") :
                missingUpload.Length == 0 ? ok("upload", "上传发布", "已启用") :
                new PreflightItem("upload", "上传发布", false, PreflightSeverity.Warning,
                    "已启用，但配置面板中缺少 " + string.Join("、", missingUpload) +
                    "；Unity 执行上传时会给出具体错误。"));
            addExistingBase(report, profile, snapshot, arguments);
        }
    }

    static void addExistingBase(PreflightReport report, MfBuildProfile profile,
        ProjectConfigurationSnapshot snapshot,
        IReadOnlyDictionary<string, string>? arguments)
    {
        if (!profile.properties.TryGetValue("existingBaseIdVariable", out string? idName) ||
            !profile.properties.TryGetValue("existingBaseRootVariable", out string? rootName))
            return;
        snapshot.Values.TryGetValue(idName, out ProjectConfigurationValue? id);
        snapshot.Values.TryGetValue(rootName, out ProjectConfigurationValue? root);
        string environment = arguments is not null && arguments.TryGetValue("environment",
            out string? selected) && !string.IsNullOrWhiteSpace(selected) ? selected :
            profile.allowedEnvironments.Count == 1 ? profile.allowedEnvironments[0] : "test";
        string capability = profile.properties.GetValueOrDefault(
            "existingBaseCapabilityFile", ".base-cap");
        string? baseline = id is null || root is null ? null : Path.Combine(root.Value,
            environment, id.Value, profile.target, capability);
        ProjectConfigurationValue? releaseRoot = null;
        if (profile.properties.TryGetValue("existingBaseReleaseRootVariable",
                out string? releaseRootName))
            snapshot.Values.TryGetValue(releaseRootName, out releaseRoot);
        string publishPlatform = profile.properties.GetValueOrDefault(
            "existingBasePublishPlatform", profile.target);
        string? trust = id is null || releaseRoot is null ? null : Path.Combine(
            releaseRoot.Value, environment, "base", publishPlatform, id.Value + ".json");
        bool baselineOk = baseline is not null && File.Exists(baseline);
        bool trustOk = trust is null || File.Exists(trust);
        report.Items.Add(baselineOk && trustOk
            ? ok("existing-base", "现有 Base", id!.Value + " · 基线与发布信任可用")
            : error("existing-base", "现有 Base", id is null || root is null
                ? "未配置 Base ID 或基线目录；请选择“Base + 热更新”。"
                : !baselineOk ? id.Value + " 的基线不存在；请选择“Base + 热更新”。"
                : id.Value + " 的发布信任记录不存在；请选择“Base + 热更新”。"));
    }

    static async Task<string> gitStatus(string root, CancellationToken cancellationToken)
    {
        try
        {
            ProcessStartInfo start = new("git")
            {
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            start.ArgumentList.Add("status");
            start.ArgumentList.Add("--porcelain");
            using Process process = Process.Start(start) ?? throw new InvalidOperationException(
                "Unable to start git.");
            string output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            return process.ExitCode == 0 ? output : "git unavailable";
        }
        catch { return "git unavailable"; }
    }

    static PreflightReport with(this PreflightReport report, IEnumerable<PreflightItem> values)
    {
        report.Items.AddRange(values);
        return report;
    }
    static PreflightItem ok(string id, string label, string detail) => new(id, label, true,
        PreflightSeverity.Info, detail);
    static PreflightItem error(string id, string label, string detail) => new(id, label, false,
        PreflightSeverity.Error, detail);
}
