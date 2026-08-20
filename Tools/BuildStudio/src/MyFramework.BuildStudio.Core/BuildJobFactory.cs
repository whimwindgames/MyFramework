using MyFramework.BuildStudio;

namespace MyFramework.BuildStudio.Core;

public static class BuildJobFactory
{
    public static MfBuildJob Create(ProjectDocument project, MfBuildProfile profile,
        string environment, string? outputRoot = null, string? version = null,
        long buildNumber = 0, bool clean = false, bool development = false,
        IEnumerable<string>? modules = null)
    {
        if (environment is not ("test" or "prod"))
            throw new InvalidDataException("Environment must be test or prod.");
        if (profile.allowedEnvironments.Count > 0 &&
            !profile.allowedEnvironments.Contains(environment, StringComparer.Ordinal))
            throw new InvalidDataException("Profile does not support environment: " + environment);
        List<string> requiredModules = project.Structure.modules.Where(value => !value.optional)
            .Select(value => value.id).ToList();
        List<string> selectedModules = (modules ?? requiredModules).Distinct(
            StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToList();
        HashSet<string> declaredModules = new(project.Structure.modules.Select(value => value.id),
            StringComparer.Ordinal);
        string? unknown = selectedModules.FirstOrDefault(value => !declaredModules.Contains(value));
        if (unknown is not null) throw new InvalidDataException("Unknown module: " + unknown);
        string? missing = requiredModules.FirstOrDefault(value => !selectedModules.Contains(value,
            StringComparer.Ordinal));
        if (missing is not null) throw new InvalidDataException(
            "Required module was not selected: " + missing);
        string jobId = "mf-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" +
                       Guid.NewGuid().ToString("N")[..8];
        string resolvedOutput = string.IsNullOrWhiteSpace(outputRoot)
            ? defaultOutputRoot(project, profile, jobId)
            : Path.GetFullPath(outputRoot);
        return new MfBuildJob
        {
            jobId = jobId,
            projectRoot = project.ProjectRoot,
            structurePath = Path.GetRelativePath(project.ProjectRoot, project.StructurePath)
                .Replace('\\', '/'),
            structureHash = project.Structure.structureHash,
            profileId = profile.id,
            action = profile.action,
            target = profile.target,
            environment = environment,
            outputRoot = resolvedOutput,
            version = version ?? string.Empty,
            buildNumber = buildNumber,
            clean = clean,
            development = development,
            modules = selectedModules,
        };
    }

    static string defaultOutputRoot(ProjectDocument project, MfBuildProfile profile,
        string jobId)
    {
        if (profile.action == "validate") return string.Empty;
        return Path.GetFullPath(Path.Combine(BuildStudioPaths.OutputsRoot,
            project.Structure.project.id, profile.id, jobId));
    }
}
