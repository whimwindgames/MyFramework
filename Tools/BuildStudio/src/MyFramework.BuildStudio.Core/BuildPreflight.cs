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
        bool open = File.Exists(Path.Combine(project.ProjectRoot, "Temp", "UnityLockfile"));
        report.Items.Add(open ? new PreflightItem("project-open", "Unity 项目占用", false,
            PreflightSeverity.Warning, "构建时将请求保存并关闭当前 Unity Editor。") :
            ok("project-open", "Unity 项目占用", "未占用"));
        string git = await gitStatus(project.ProjectRoot, cancellationToken);
        bool clean = string.IsNullOrWhiteSpace(git);
        report.Items.Add(clean ? ok("git", "Git 工作区", "干净") : new PreflightItem(
            "git", "Git 工作区", !profile.requiresCleanGit,
            profile.requiresCleanGit ? PreflightSeverity.Error : PreflightSeverity.Warning,
            profile.requiresCleanGit ? "正式 Profile 要求干净工作区。" : "存在未提交修改。"));
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
