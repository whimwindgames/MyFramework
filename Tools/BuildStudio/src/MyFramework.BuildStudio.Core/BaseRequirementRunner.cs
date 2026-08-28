using System.Diagnostics;
using System.Text.Json;
using MyFramework.BuildStudio;

namespace MyFramework.BuildStudio.Core;

public sealed class BaseRequirementReport
{
    public string GeneratedAtUtc { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public bool BaselineFound { get; init; }
    public string BaselineCommit { get; init; } = string.Empty;
    public string BaselineBaseId { get; init; } = string.Empty;
    public string BaselineEnvironment { get; init; } = string.Empty;
    public string BaselineTarget { get; init; } = string.Empty;
    public string BaselineRecordedAtUtc { get; init; } = string.Empty;
    public bool RequiresBasePackage { get; init; }
    public bool ContainsHotCode { get; init; }
    public bool ContainsHotAssets { get; init; }
    public string Recommendation { get; init; } = string.Empty;
    public int ChangeCount { get; init; }
    public string ReportPath { get; init; } = string.Empty;
    public string LogPath { get; init; } = string.Empty;
}

public static class BaseRequirementRunner
{
    const string LegacyFishingMenuPath =
        "FishGame/Framework/Analyze Base Requirement/Working Tree";
    const string FishingBatchMethod =
        "FishGame.EditorTools.FishingBaseRequirementDetector.RunBatch";

    public static bool IsSupported(ProjectDocument project) =>
        project.Structure.properties.TryGetValue("baseRequirementAnalyzer", out string? method) &&
        !string.IsNullOrWhiteSpace(method);

    public static string ResolveAnalyzerMethod(ProjectDocument project)
    {
        if (!project.Structure.properties.TryGetValue("baseRequirementAnalyzer",
                out string? configured) || string.IsNullOrWhiteSpace(configured))
            throw new InvalidOperationException("当前项目未提供 Base 必要性检测器。");

        string method = configured.Trim();
        if (string.Equals(method, LegacyFishingMenuPath, StringComparison.Ordinal))
            return FishingBatchMethod;
        if (method.Contains('/', StringComparison.Ordinal))
            throw new InvalidDataException(
                $"Base 检测器配置的是 Unity 菜单路径，无法用于自动检测：{method}。" +
                "请将 baseRequirementAnalyzer 改为可由 Unity -executeMethod 调用的静态方法。");
        return method;
    }

    public static async Task<BaseRequirementReport> RunAsync(ProjectDocument project,
        UnityInstallation unity, string? target, string? environment,
        IProgress<BuildProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        string method = ResolveAnalyzerMethod(project);
        await EditorCloseCoordinator.EnsureClosedAsync(project.ProjectRoot, progress,
            cancellationToken: cancellationToken);
        // Unity recreates the project's Temp directory while opening in batch mode. Keeping
        // the report there makes both the report and log disappear before the analyzer runs.
        string work = Path.Combine(BuildStudioPaths.AppDataRoot, "BaseChecks",
            "base-requirement-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        PathSecurity.EnsureNoLinks(work);
        string reportPath = Path.Combine(work, "report.json");
        string logPath = Path.Combine(work, "UnityEditor.log");
        string reportArgument = project.Structure.properties.GetValueOrDefault(
            "baseRequirementReportArgument", "-fishingBaseReport");
        string environmentArgument = project.Structure.properties.GetValueOrDefault(
            "baseRequirementEnvironmentArgument", "-fishingBaseEnvironment");
        string targetArgument = project.Structure.properties.GetValueOrDefault(
            "baseRequirementTargetArgument", "-fishingBaseTarget");
        ProcessStartInfo start = new(unity.EditorPath)
        {
            WorkingDirectory = project.ProjectRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string value in new[]
                 {
                     "-batchmode", "-quit", "-accept-apiupdate", "-projectPath",
                     project.ProjectRoot, "-executeMethod", method, reportArgument,
                     reportPath,
                 }) start.ArgumentList.Add(value);
        if (!string.IsNullOrWhiteSpace(environment))
        {
            start.ArgumentList.Add(environmentArgument);
            start.ArgumentList.Add(environment);
        }
        if (!string.IsNullOrWhiteSpace(target))
        {
            start.ArgumentList.Add(targetArgument);
            start.ArgumentList.Add(target);
        }
        start.ArgumentList.Add("-logFile");
        start.ArgumentList.Add(logPath);
        progress?.Report(new BuildProgress("base-check", "started",
            "正在由 Unity 分析本次改动是否需要新 Base…", -1));
        using Process process = Process.Start(start) ?? throw new InvalidOperationException(
            "Unable to start Unity.");
        try { await process.WaitForExitAsync(cancellationToken); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(true);
            throw;
        }
        if (process.ExitCode != 0 || !File.Exists(reportPath))
            throw new InvalidOperationException(failureMessage(logPath));
        using JsonDocument json = JsonDocument.Parse(await File.ReadAllTextAsync(reportPath,
            cancellationToken));
        JsonElement root = json.RootElement;
        BaseRequirementReport result = new()
        {
            GeneratedAtUtc = text(root, "generatedAtUtc"),
            Source = text(root, "source"),
            BaselineFound = boolean(root, "baselineFound"),
            BaselineCommit = text(root, "baselineCommit"),
            BaselineBaseId = text(root, "baselineBaseId"),
            BaselineEnvironment = text(root, "baselineEnvironment"),
            BaselineTarget = text(root, "baselineTarget"),
            BaselineRecordedAtUtc = text(root, "baselineRecordedAtUtc"),
            RequiresBasePackage = boolean(root, "requiresBasePackage"),
            ContainsHotCode = boolean(root, "containsHotCode"),
            ContainsHotAssets = boolean(root, "containsHotAssets"),
            Recommendation = text(root, "recommendation"),
            ChangeCount = root.TryGetProperty("changes", out JsonElement changes) &&
                          changes.ValueKind == JsonValueKind.Array ? changes.GetArrayLength() : 0,
            ReportPath = reportPath,
            LogPath = logPath,
        };
        progress?.Report(new BuildProgress("base-check", "succeeded",
            result.Recommendation, 1));
        return result;
    }

    static string text(JsonElement root, string name) => root.TryGetProperty(name,
        out JsonElement value) && value.ValueKind == JsonValueKind.String
        ? value.GetString() ?? string.Empty : string.Empty;

    static bool boolean(JsonElement root, string name) => root.TryGetProperty(name,
        out JsonElement value) && value.ValueKind is JsonValueKind.True;

    static string failureMessage(string logPath)
    {
        string message = "Base 检测失败";
        if (File.Exists(logPath))
        {
            string[] indicators =
            [
                "error CS", "executeMethod", "Exception:", "Unhandled Exception",
                "Scripts have compiler errors", "Aborting batchmode",
            ];
            string? detail = File.ReadLines(logPath).Reverse().FirstOrDefault(line =>
                indicators.Any(value => line.Contains(value,
                    StringComparison.OrdinalIgnoreCase)));
            if (!string.IsNullOrWhiteSpace(detail)) message += "：" + detail.Trim();
        }
        return message + "；日志: " + logPath;
    }
}
