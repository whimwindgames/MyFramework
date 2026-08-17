using MyFramework.BuildStudio;

namespace MyFramework.BuildStudio.Core;

public sealed record ProjectDocument(string ProjectRoot, string StructurePath,
    MfProjectStructure Structure);

public static class ProjectStructureStore
{
    public static ProjectDocument LoadProject(string projectRoot)
    {
        if (string.IsNullOrWhiteSpace(projectRoot))
            throw new InvalidDataException("Unity project path is required.");
        string root = Path.GetFullPath(projectRoot).TrimEnd(Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        if (!Directory.Exists(Path.Combine(root, "Assets")) ||
            !File.Exists(Path.Combine(root, "ProjectSettings", "ProjectVersion.txt")))
            throw new InvalidDataException("Selected folder is not a Unity project: " + root);
        PathSecurity.EnsureNoLinks(root);
        string structurePath = Path.Combine(root, MfBuildSchema.ProjectFileName);
        if (!File.Exists(structurePath)) throw new FileNotFoundException(
            "Generate MyFrameworkProject.json from the Unity MenuItem first.", structurePath);
        return new ProjectDocument(root, structurePath, LoadStructure(structurePath));
    }

    public static MfProjectStructure LoadStructure(string path)
    {
        if (!Path.IsPathFullyQualified(path))
            throw new InvalidDataException("Project structure path must be absolute.");
        string full = Path.GetFullPath(path);
        if (!File.Exists(full)) throw new FileNotFoundException(
            "Project structure is missing.", full);
        PathSecurity.EnsureNoLinks(full);
        MfProjectStructure structure = BuildStudioJson.Deserialize<MfProjectStructure>(
            File.ReadAllText(full));
        Validate(structure);
        string actual = BuildStudioJson.ComputeStructureHash(structure);
        if (!string.Equals(actual, structure.structureHash, StringComparison.Ordinal))
            throw new InvalidDataException("Project structure hash mismatch.");
        return structure;
    }

    public static void Validate(MfProjectStructure value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.schema != MfBuildSchema.Project)
            throw new InvalidDataException("Unsupported project structure schema.");
        if (value.project is null || !validId(value.project.id))
            throw new InvalidDataException("Project id is invalid.");
        if (string.IsNullOrWhiteSpace(value.project.displayName) ||
            value.unity is null || string.IsNullOrWhiteSpace(value.unity.version) ||
            value.framework is null || string.IsNullOrWhiteSpace(value.framework.version))
            throw new InvalidDataException("Project identity is incomplete.");
        if (!validHash(value.structureHash))
            throw new InvalidDataException("Project structure hash is invalid.");
        unique(value.profiles?.Select(item => item?.id), "profile");
        unique(value.modules?.Select(item => item?.id), "module");
        unique(value.packages?.Select(item => item?.name), "package");
        foreach (MfBuildProfile profile in value.profiles ?? [])
        {
            if (!validId(profile.id) || string.IsNullOrWhiteSpace(profile.action) ||
                string.IsNullOrWhiteSpace(profile.target))
                throw new InvalidDataException("Build profile is invalid.");
            rejectSecretKeys(profile.properties, "profile " + profile.id);
        }
        rejectSecretKeys(value.properties, "project");
        foreach (MfModuleInfo module in value.modules ?? [])
            rejectSecretKeys(module.properties, "module " + module.id);
    }

    static void rejectSecretKeys(IDictionary<string, string>? values, string owner)
    {
        foreach (string key in values?.Keys ?? [])
        {
            string lowered = key.ToLowerInvariant();
            if (lowered.Contains("password", StringComparison.Ordinal) ||
                lowered.Contains("secret", StringComparison.Ordinal) ||
                lowered.Contains("token", StringComparison.Ordinal) ||
                lowered.Contains("privatekey", StringComparison.Ordinal))
                throw new InvalidDataException($"{owner} contains a secret key: {key}");
        }
    }

    static void unique(IEnumerable<string?>? values, string label)
    {
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (string? value in values ?? [])
            if (string.IsNullOrWhiteSpace(value) || !seen.Add(value))
                throw new InvalidDataException($"Duplicate or empty {label}: {value}");
    }

    static bool validId(string? value) => !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 80 && value.All(character => char.IsAsciiLetterOrDigit(character) ||
            character is '-' or '_' or '.');
    static bool validHash(string? value) => value is { Length: 64 } &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
