namespace MyFramework.BuildStudio.Core;

public sealed class BuildStudioSettings
{
    public int schema = 1;
    public string unityHubRoot = string.Empty;
    public string defaultOutputRoot = string.Empty;
    public List<string> recentProjects = [];
    public int retainBuilds = 100;

    public static BuildStudioSettings Load(string? path = null)
    {
        string file = path ?? BuildStudioPaths.SettingsPath;
        if (!File.Exists(file)) return new BuildStudioSettings();
        BuildStudioSettings value = BuildStudioJson.Deserialize<BuildStudioSettings>(
            File.ReadAllText(file));
        if (value.schema != 1) throw new InvalidDataException(
            "Unsupported Build Studio settings schema.");
        value.recentProjects ??= [];
        return value;
    }

    public void Save(string? path = null) => BuildStudioJson.WriteAtomic(
        path ?? BuildStudioPaths.SettingsPath, this);

    public void RememberProject(string projectRoot)
    {
        string full = Path.GetFullPath(projectRoot);
        recentProjects.RemoveAll(item => string.Equals(item, full,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase :
                StringComparison.Ordinal));
        recentProjects.Insert(0, full);
        if (recentProjects.Count > 20) recentProjects.RemoveRange(20, recentProjects.Count - 20);
    }
}
