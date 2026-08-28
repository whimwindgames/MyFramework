using MyFramework.BuildStudio;

namespace MyFramework.BuildStudio.Core;

public sealed record BuildActionOption(string Id, string Label)
{
    public override string ToString() => Label;
}

public sealed record BuildPlatformOption(string Target, string Label)
{
    public override string ToString() => Label;
}

public static class BuildProfileCatalog
{
    public static IReadOnlyList<MfBuildProfile> VisibleProfiles(
        IEnumerable<MfBuildProfile> profiles) => profiles.Where(profile =>
            !propertyBool(profile, "uiHidden")).ToArray();

    public static IReadOnlyList<BuildActionOption> Actions(
        IEnumerable<MfBuildProfile> profiles)
    {
        List<BuildActionOption> result = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (MfBuildProfile profile in VisibleProfiles(profiles)
                     .OrderBy(profileOrder))
        {
            string id = LogicalAction(profile);
            if (!seen.Add(id)) continue;
            result.Add(new BuildActionOption(id, property(profile, "uiActionLabel") ??
                actionLabel(id, profile.displayName)));
        }
        return result;
    }

    public static IReadOnlyList<BuildPlatformOption> Platforms(
        IEnumerable<MfBuildProfile> profiles, string logicalAction)
    {
        List<BuildPlatformOption> result = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (MfBuildProfile profile in VisibleProfiles(profiles).Where(profile =>
                     string.Equals(LogicalAction(profile), logicalAction,
                         StringComparison.Ordinal)))
        {
            if (seen.Add(profile.target))
                result.Add(new BuildPlatformOption(profile.target, platformLabel(profile.target)));
        }
        return result;
    }

    public static MfBuildProfile? Find(IEnumerable<MfBuildProfile> profiles,
        string logicalAction, string target) => VisibleProfiles(profiles).FirstOrDefault(profile =>
            string.Equals(LogicalAction(profile), logicalAction, StringComparison.Ordinal) &&
            string.Equals(profile.target, target, StringComparison.Ordinal));

    public static string LogicalAction(MfBuildProfile profile) =>
        property(profile, "uiAction") ?? profile.action;

    public static bool SupportsUpload(MfBuildProfile profile) =>
        propertyBool(profile, "supportsUpload");

    public static bool DefaultUpload(MfBuildProfile profile) =>
        SupportsUpload(profile) && propertyBool(profile, "defaultUpload");

    static string? property(MfBuildProfile profile, string name) =>
        profile.properties.TryGetValue(name, out string? value) &&
        !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

    static bool propertyBool(MfBuildProfile profile, string name) =>
        string.Equals(property(profile, name), "true", StringComparison.OrdinalIgnoreCase);

    static int profileOrder(MfBuildProfile profile) =>
        int.TryParse(property(profile, "uiOrder"), out int value) ? value : int.MaxValue;

    static string actionLabel(string action, string fallback) => action switch
    {
        "assets" => "AssetBundle",
        "release" => "热更新",
        "base-release" or "integrated" => "Base + 热更新",
        _ => string.IsNullOrWhiteSpace(fallback) ? action : fallback,
    };

    static string platformLabel(string target) => target switch
    {
        "Android" => "Android",
        "iOS" => "iOS",
        "StandaloneOSX" => "macOS",
        "StandaloneWindows64" => "Windows",
        "Current" => "当前平台",
        _ => target,
    };
}
