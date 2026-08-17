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
        return new MfBuildJob
        {
            jobId = "mf-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" +
                    Guid.NewGuid().ToString("N")[..8],
            projectRoot = project.ProjectRoot,
            structurePath = Path.GetRelativePath(project.ProjectRoot, project.StructurePath)
                .Replace('\\', '/'),
            structureHash = project.Structure.structureHash,
            profileId = profile.id,
            action = profile.action,
            target = profile.target,
            environment = environment,
            outputRoot = string.IsNullOrWhiteSpace(outputRoot) ? string.Empty :
                Path.GetFullPath(outputRoot),
            version = version ?? string.Empty,
            buildNumber = buildNumber,
            clean = clean,
            development = development,
            modules = modules?.Distinct(StringComparer.Ordinal).OrderBy(value => value,
                StringComparer.Ordinal).ToList() ?? [],
        };
    }
}
