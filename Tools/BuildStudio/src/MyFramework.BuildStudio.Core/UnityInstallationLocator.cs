using System.Text.RegularExpressions;

namespace MyFramework.BuildStudio.Core;

public sealed record UnityInstallation(string Version, string EditorPath, string Root);

public static partial class UnityInstallationLocator
{
    public static IReadOnlyList<UnityInstallation> FindInstalled(string? customHubRoot = null)
    {
        HashSet<string> roots = new(pathComparer());
        if (!string.IsNullOrWhiteSpace(customHubRoot)) roots.Add(Path.GetFullPath(customHubRoot));
        if (OperatingSystem.IsMacOS()) roots.Add("/Applications/Unity/Hub/Editor");
        if (OperatingSystem.IsWindows())
        {
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (!string.IsNullOrEmpty(programFiles)) roots.Add(Path.Combine(programFiles,
                "Unity", "Hub", "Editor"));
        }
        List<UnityInstallation> result = [];
        foreach (string root in roots.Where(Directory.Exists))
        foreach (string versionDirectory in Directory.GetDirectories(root))
        {
            string version = Path.GetFileName(versionDirectory);
            if (!UnityVersionPattern().IsMatch(version)) continue;
            string executable = OperatingSystem.IsMacOS()
                ? Path.Combine(versionDirectory, "Unity.app", "Contents", "MacOS", "Unity")
                : Path.Combine(versionDirectory, "Editor", "Unity.exe");
            if (File.Exists(executable)) result.Add(new UnityInstallation(version,
                Path.GetFullPath(executable), Path.GetFullPath(versionDirectory)));
        }
        return result.OrderByDescending(item => item.Version, StringComparer.Ordinal).ToArray();
    }

    public static UnityInstallation FindExact(string version, string? customHubRoot = null) =>
        FindInstalled(customHubRoot).FirstOrDefault(item => item.Version == version) ??
        throw new FileNotFoundException("Unity Editor version is not installed: " + version);

    public static bool HasTargetModule(UnityInstallation unity, string target)
    {
        if (target == "Current") return true;
        string? module = target switch
        {
            "StandaloneWindows64" => "WindowsStandaloneSupport",
            "StandaloneOSX" => "MacStandaloneSupport",
            "Android" => "AndroidPlayer",
            "iOS" => "iOSSupport",
            _ => null,
        };
        return module is not null && playbackEngineRoots(unity).Any(root =>
            Directory.Exists(Path.Combine(root, module)));
    }

    static IEnumerable<string> playbackEngineRoots(UnityInstallation unity)
    {
        yield return Path.Combine(unity.Root, "PlaybackEngines");
        string editorDirectory = Path.GetDirectoryName(unity.EditorPath)!;
        if (OperatingSystem.IsMacOS())
            yield return Path.GetFullPath(Path.Combine(editorDirectory, "..",
                "PlaybackEngines"));
        if (OperatingSystem.IsWindows())
            yield return Path.Combine(editorDirectory, "Data", "PlaybackEngines");
    }

    static StringComparer pathComparer() => OperatingSystem.IsWindows() ?
        StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    [GeneratedRegex(@"^\d+\.\d+\.\d+[abfp]\d+$", RegexOptions.CultureInvariant)]
    private static partial Regex UnityVersionPattern();
}
