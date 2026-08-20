using System.Diagnostics;
using System.Text;
using MyFramework.BuildStudio;

namespace MyFramework.BuildStudio.Core;

public sealed record BuildProgress(string Stage, string State, string Message, float Progress,
    string? RawLog = null);

public sealed class BuildRunRequest
{
    public required ProjectDocument Project { get; init; }
    public required UnityInstallation Unity { get; init; }
    public required MfBuildJob Job { get; init; }
    public string? WorkingRoot { get; init; }
}

public sealed class UnityBuildRunner
{
    public async Task<MfBuildReceipt> RunAsync(BuildRunRequest request,
        IProgress<BuildProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await EditorCloseCoordinator.EnsureClosedAsync(request.Project.ProjectRoot, progress,
            cancellationToken: cancellationToken);
        string workRoot = request.WorkingRoot ?? BuildStudioPaths.JobsRoot;
        string jobDirectory = Path.Combine(workRoot, request.Job.jobId);
        if (Directory.Exists(jobDirectory)) throw new IOException(
            "Build job directory already exists: " + jobDirectory);
        Directory.CreateDirectory(jobDirectory);
        PathSecurity.EnsureNoLinks(jobDirectory);
        string jobPath = Path.Combine(jobDirectory, "BuildJob.json");
        string receiptPath = Path.Combine(jobDirectory, "BuildReceipt.json");
        string eventPath = Path.Combine(jobDirectory, "events.jsonl");
        string logPath = Path.Combine(jobDirectory, "UnityEditor.log");
        BuildStudioJson.WriteAtomic(jobPath, request.Job);

        ProcessStartInfo start = makeStart(request, jobPath, receiptPath, eventPath, logPath);
        using Process process = new() { StartInfo = start, EnableRaisingEvents = true };
        progress?.Report(new BuildProgress("queue", "started", "Starting Unity worker", 0));
        if (!process.Start()) throw new InvalidOperationException("Unable to start Unity worker.");

        long eventOffset = 0;
        long logOffset = 0;
        try
        {
            while (!process.HasExited)
            {
                eventOffset = await readEvents(eventPath, eventOffset, progress, cancellationToken);
                logOffset = await readLog(logPath, logOffset, progress, cancellationToken);
                await Task.Delay(250, cancellationToken);
            }
            await process.WaitForExitAsync(cancellationToken);
            await readEvents(eventPath, eventOffset, progress, cancellationToken);
            await readLog(logPath, logOffset, progress, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(true);
            await process.WaitForExitAsync(CancellationToken.None);
            MfBuildReceipt canceled = canceledReceipt(request.Job, jobDirectory);
            BuildStudioJson.WriteAtomic(receiptPath, canceled);
            progress?.Report(new BuildProgress("canceled", "canceled", "Build canceled", -1));
            return canceled;
        }

        if (!File.Exists(receiptPath))
        {
            MfBuildReceipt missing = failedReceipt(request.Job, jobDirectory, process.ExitCode,
                "Unity worker did not produce BuildReceipt.json.");
            BuildStudioJson.WriteAtomic(receiptPath, missing);
            return missing;
        }
        MfBuildReceipt receipt = BuildStudioJson.Deserialize<MfBuildReceipt>(
            await File.ReadAllTextAsync(receiptPath, cancellationToken));
        receipt.values["jobDirectory"] = jobDirectory;
        receipt.values["unityLog"] = logPath;
        if (receipt.exitCode == 0 && process.ExitCode != 0)
        {
            receipt.ok = false;
            receipt.status = "failed";
            receipt.exitCode = process.ExitCode;
            receipt.error = "Unity process exited with code " + process.ExitCode;
        }
        BuildStudioJson.WriteAtomic(receiptPath, receipt);
        return receipt;
    }

    static ProcessStartInfo makeStart(BuildRunRequest request, string jobPath,
        string receiptPath, string eventPath, string logPath)
    {
        ProcessStartInfo start = new(request.Unity.EditorPath)
        {
            WorkingDirectory = request.Project.ProjectRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string argument in new[]
                 {
                     "-batchmode", "-quit", "-accept-apiupdate", "-projectPath",
                     request.Project.ProjectRoot,
                 }) start.ArgumentList.Add(argument);
        if (request.Job.target != "Current")
        {
            start.ArgumentList.Add("-buildTarget");
            start.ArgumentList.Add(request.Job.target);
        }
        foreach (string argument in new[]
                 {
                     "-executeMethod", "MyFramework.BuildStudio.Editor.MfBuildCli.runCli",
                     "-mfJob", jobPath, "-mfReceipt", receiptPath,
                     "-mfEventFile", eventPath, "-logFile", logPath,
                 }) start.ArgumentList.Add(argument);
        return start;
    }

    static async Task<long> readEvents(string path, long offset,
        IProgress<BuildProgress>? progress, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return offset;
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (offset > stream.Length) offset = 0;
        stream.Position = offset;
        using StreamReader reader = new(stream, Encoding.UTF8, true, leaveOpen: true);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                MfBuildEvent evt = BuildStudioJson.Deserialize<MfBuildEvent>(line);
                progress?.Report(new BuildProgress(evt.stage, evt.state, evt.message,
                    evt.progress));
            }
            catch (Exception exception)
            {
                progress?.Report(new BuildProgress("events", "warning",
                    "Invalid worker event: " + exception.Message, -1, line));
            }
        }
        return stream.Position;
    }

    static async Task<long> readLog(string path, long offset,
        IProgress<BuildProgress>? progress, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return offset;
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (offset > stream.Length) offset = 0;
        stream.Position = offset;
        using StreamReader reader = new(stream, Encoding.UTF8, true, leaveOpen: true);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (usefulLog(line))
                progress?.Report(new BuildProgress("unity-log", "log", line, -1, line));
        }
        return stream.Position;
    }

    static bool usefulLog(string line)
    {
        string value = line.ToLowerInvariant();
        return value.Contains("error cs", StringComparison.Ordinal) ||
               value.Contains("exception:", StringComparison.Ordinal) ||
               value.Contains("buildfailed", StringComparison.Ordinal) ||
               value.Contains("build studio", StringComparison.Ordinal) ||
               value.Contains("assetbundle构建", StringComparison.Ordinal) ||
               value.Contains("hybridclr generate", StringComparison.Ordinal) ||
               value.Contains("release complete", StringComparison.Ordinal) ||
               value.Contains("[fishing", StringComparison.Ordinal) ||
               value.Contains("[arcade", StringComparison.Ordinal);
    }

    static MfBuildReceipt canceledReceipt(MfBuildJob job, string outputRoot) => new()
    {
        jobId = job.jobId,
        ok = false,
        status = "canceled",
        error = "Canceled by user.",
        startedAtUtc = DateTime.UtcNow.ToString("O"),
        finishedAtUtc = DateTime.UtcNow.ToString("O"),
        exitCode = 130,
        structureHash = job.structureHash,
        profileId = job.profileId,
        action = job.action,
        target = job.target,
        environment = job.environment,
        outputRoot = outputRoot,
    };

    static MfBuildReceipt failedReceipt(MfBuildJob job, string outputRoot, int exitCode,
        string error) => new()
    {
        jobId = job.jobId,
        ok = false,
        status = "failed",
        error = error,
        startedAtUtc = DateTime.UtcNow.ToString("O"),
        finishedAtUtc = DateTime.UtcNow.ToString("O"),
        exitCode = exitCode,
        structureHash = job.structureHash,
        profileId = job.profileId,
        action = job.action,
        target = job.target,
        environment = job.environment,
        outputRoot = outputRoot,
    };
}

public static class BuildStudioPaths
{
    public static string AppDataRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WhimwindGames", "MyFrameworkBuildStudio");
    public static string JobsRoot { get; } = Path.Combine(AppDataRoot, "Jobs");
    public static string OutputsRoot { get; } = Path.Combine(AppDataRoot, "Outputs");
    public static string DatabasePath { get; } = Path.Combine(AppDataRoot, "history.db");
    public static string SettingsPath { get; } = Path.Combine(AppDataRoot, "settings.json");
}
