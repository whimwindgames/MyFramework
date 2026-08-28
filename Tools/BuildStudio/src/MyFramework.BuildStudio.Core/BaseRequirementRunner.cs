using System.Diagnostics;
using System.Text.Json;
using MyFramework.BuildStudio;

namespace MyFramework.BuildStudio.Core;

public sealed class BaseRequirementReport
{
    public string GeneratedAtUtc { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
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
    public static bool IsSupported(ProjectDocument project) =>
        project.Structure.properties.TryGetValue("baseRequirementAnalyzer", out string? method) &&
        !string.IsNullOrWhiteSpace(method);

    public static async Task<BaseRequirementReport> RunAsync(ProjectDocument project,
        UnityInstallation unity, IProgress<BuildProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!project.Structure.properties.TryGetValue("baseRequirementAnalyzer",
                out string? method) || string.IsNullOrWhiteSpace(method))
            throw new InvalidOperationException("当前项目未提供 Base 必要性检测器。");
        await EditorCloseCoordinator.EnsureClosedAsync(project.ProjectRoot, progress,
            cancellationToken: cancellationToken);
        string work = Path.Combine(project.ProjectRoot, "Temp", "BuildStudio",
            "base-requirement-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        PathSecurity.EnsureNoLinks(work);
        string reportPath = Path.Combine(work, "report.json");
        string logPath = Path.Combine(work, "UnityEditor.log");
        string reportArgument = project.Structure.properties.GetValueOrDefault(
            "baseRequirementReportArgument", "-fishingBaseReport");
        ProcessStartInfo start = new(unity.EditorPath)
        {
            WorkingDirectory = project.ProjectRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string value in new[]
                 {
                     "-batchmode", "-quit", "-accept-apiupdate", "-projectPath",
                     project.ProjectRoot, "-executeMethod", method.Trim(), reportArgument,
                     reportPath, "-logFile", logPath,
                 }) start.ArgumentList.Add(value);
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
            throw new InvalidOperationException("Base 检测失败，日志: " + logPath);
        using JsonDocument json = JsonDocument.Parse(await File.ReadAllTextAsync(reportPath,
            cancellationToken));
        JsonElement root = json.RootElement;
        BaseRequirementReport result = new()
        {
            GeneratedAtUtc = text(root, "generatedAtUtc"),
            Source = text(root, "source"),
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
}
