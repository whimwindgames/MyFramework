using System.Diagnostics;

namespace MyFramework.BuildStudio.Core;

public static class StructureGeneratorRunner
{
    public static async Task<string> GenerateAsync(string projectRoot,
        string? customHubRoot = null, CancellationToken cancellationToken = default)
    {
        string root = Path.GetFullPath(projectRoot);
        string version = ReadProjectVersion(root);
        UnityInstallation unity = UnityInstallationLocator.FindExact(version, customHubRoot);
        string work = Path.Combine(root, "Temp", "BuildStudio");
        Directory.CreateDirectory(work);
        string log = Path.Combine(work, "structure.log");
        ProcessStartInfo start = new(unity.EditorPath)
        {
            WorkingDirectory = root,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string value in new[]
                 {
                     "-batchmode", "-quit", "-accept-apiupdate", "-projectPath", root,
                     "-executeMethod",
                     "MyFramework.BuildStudio.Editor.MfProjectStructureCli.runCli",
                     "-logFile", log,
                 }) start.ArgumentList.Add(value);
        using Process process = Process.Start(start) ?? throw new InvalidOperationException(
            "Unable to start Unity.");
        try { await process.WaitForExitAsync(cancellationToken); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(true);
            throw;
        }
        if (process.ExitCode != 0) throw new InvalidOperationException(
            "Structure generation failed. Log: " + log);
        string output = Path.Combine(root, MyFramework.BuildStudio.MfBuildSchema.ProjectFileName);
        _ = ProjectStructureStore.LoadStructure(output);
        return output;
    }

    public static string ReadProjectVersion(string projectRoot)
    {
        string path = Path.Combine(projectRoot, "ProjectSettings", "ProjectVersion.txt");
        if (!File.Exists(path)) throw new FileNotFoundException("ProjectVersion.txt is missing.", path);
        const string prefix = "m_EditorVersion:";
        string? line = File.ReadLines(path).FirstOrDefault(value =>
            value.StartsWith(prefix, StringComparison.Ordinal));
        return line?[prefix.Length..].Trim() ?? throw new InvalidDataException(
            "Unity version is missing from ProjectVersion.txt.");
    }
}
