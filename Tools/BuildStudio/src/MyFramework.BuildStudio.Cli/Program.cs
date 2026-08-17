using System.Diagnostics;
using MyFramework.BuildStudio;
using MyFramework.BuildStudio.Core;

namespace MyFramework.BuildStudio.Cli;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
            {
                usage();
                return 0;
            }
            Arguments options = new(args.Skip(1).ToArray());
            return args[0] switch
            {
                "scan" => scan(options),
                "generate" => await generate(options),
                "preflight" => await preflight(options),
                "build" => await build(options),
                "history" => history(options),
                _ => throw new InvalidDataException("Unknown command: " + args[0]),
            };
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.GetType().Name + ": " + exception.Message);
            return 2;
        }
    }

    static int scan(Arguments args)
    {
        ProjectDocument project = ProjectStructureStore.LoadProject(args.Required("project"));
        Console.WriteLine(BuildStudioJson.Serialize(project.Structure));
        return 0;
    }

    static async Task<int> generate(Arguments args)
    {
        string root = Path.GetFullPath(args.Required("project"));
        string version = projectVersion(root);
        UnityInstallation unity = UnityInstallationLocator.FindExact(version,
            args.Optional("unity-root"));
        string log = Path.Combine(root, "Temp", "BuildStudio", "structure.log");
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        ProcessStartInfo start = new(unity.EditorPath) { UseShellExecute = false };
        foreach (string value in new[]
                 {
                     "-batchmode", "-quit", "-accept-apiupdate", "-projectPath", root,
                     "-executeMethod",
                     "MyFramework.BuildStudio.Editor.MfProjectStructureCli.runCli",
                     "-logFile", log,
                 }) start.ArgumentList.Add(value);
        using Process process = Process.Start(start) ?? throw new InvalidOperationException(
            "Unable to start Unity.");
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new InvalidOperationException(
            "Structure generation failed. Log: " + log);
        Console.WriteLine(Path.Combine(root, MfBuildSchema.ProjectFileName));
        return 0;
    }

    static async Task<int> preflight(Arguments args)
    {
        ProjectDocument project = ProjectStructureStore.LoadProject(args.Required("project"));
        MfBuildProfile profile = findProfile(project, args.Required("profile"));
        PreflightReport report = await BuildPreflight.RunAsync(project, profile,
            args.Optional("unity-root"));
        foreach (PreflightItem item in report.Items)
            Console.WriteLine($"{(item.Ok ? "OK" : item.Severity.ToString().ToUpperInvariant())} " +
                              $"{item.Label}: {item.Detail}");
        return report.CanBuild ? 0 : 2;
    }

    static async Task<int> build(Arguments args)
    {
        ProjectDocument project = ProjectStructureStore.LoadProject(args.Required("project"));
        MfBuildProfile profile = findProfile(project, args.Required("profile"));
        PreflightReport preflight = await BuildPreflight.RunAsync(project, profile,
            args.Optional("unity-root"));
        if (!preflight.CanBuild) throw new InvalidOperationException(
            "Preflight failed: " + string.Join("; ", preflight.Items.Where(item =>
                !item.Ok && item.Severity == PreflightSeverity.Error).Select(item => item.Detail)));
        MfBuildJob job = BuildJobFactory.Create(project, profile,
            args.Optional("env") ?? "test", args.Optional("output"), args.Optional("version"),
            args.Long("build-number"), args.Flag("clean"), args.Flag("development"),
            args.Many("module"));
        Progress<BuildProgress> progress = new(value =>
            Console.WriteLine($"[{value.Stage}] {value.State}: {value.Message}"));
        MfBuildReceipt receipt = await new UnityBuildRunner().RunAsync(new BuildRunRequest
        {
            Project = project,
            Unity = preflight.Unity ?? throw new InvalidOperationException("Unity is unavailable."),
            Job = job,
        }, progress);
        new BuildHistoryRepository().Add(receipt);
        Console.WriteLine(BuildStudioJson.Serialize(receipt));
        return receipt.ok ? 0 : receipt.status == "canceled" ? 130 : 2;
    }

    static int history(Arguments args)
    {
        foreach (BuildHistoryItem item in new BuildHistoryRepository().List(
                     (int)Math.Clamp(args.Long("limit", 20), 1, 1000)))
            Console.WriteLine($"{item.StartedAtUtc} {item.Status,-9} {item.ProjectId} " +
                              $"{item.ProfileId} {item.Target} {item.DurationMs}ms {item.JobId}");
        return 0;
    }

    static MfBuildProfile findProfile(ProjectDocument project, string id) =>
        project.Structure.profiles.SingleOrDefault(value => value.id == id) ??
        throw new InvalidDataException("Unknown profile: " + id);

    static string projectVersion(string root)
    {
        string path = Path.Combine(root, "ProjectSettings", "ProjectVersion.txt");
        if (!File.Exists(path)) throw new FileNotFoundException("ProjectVersion.txt is missing.", path);
        const string prefix = "m_EditorVersion:";
        string? line = File.ReadLines(path).FirstOrDefault(value =>
            value.StartsWith(prefix, StringComparison.Ordinal));
        return line?[prefix.Length..].Trim() ?? throw new InvalidDataException(
            "Unity version is missing from ProjectVersion.txt.");
    }

    static void usage()
    {
        Console.WriteLine("""
            MyFramework Build Studio CLI

              mf-build scan --project <path>
              mf-build generate --project <path> [--unity-root <path>]
              mf-build preflight --project <path> --profile <id>
              mf-build build --project <path> --profile <id> [--env test|prod]
                             [--output <path>] [--version <value>]
                             [--build-number <number>] [--module <id>] [--clean]
              mf-build history [--limit 20]
            """);
    }
}

sealed class Arguments
{
    readonly Dictionary<string, List<string>> _values = new(StringComparer.Ordinal);

    internal Arguments(string[] args)
    {
        for (int i = 0; i < args.Length; ++i)
        {
            string token = args[i];
            if (!token.StartsWith("--", StringComparison.Ordinal))
                throw new InvalidDataException("Unexpected argument: " + token);
            string key = token[2..];
            string value = i + 1 < args.Length && !args[i + 1].StartsWith("--",
                StringComparison.Ordinal) ? args[++i] : "true";
            if (!_values.TryGetValue(key, out List<string>? list))
                _values[key] = list = [];
            list.Add(value);
        }
    }

    internal string Required(string key) => Optional(key) ?? throw new InvalidDataException(
        "Missing --" + key);
    internal string? Optional(string key) => _values.TryGetValue(key, out var values) ?
        values.Last() : null;
    internal IReadOnlyList<string> Many(string key) => _values.TryGetValue(key,
        out var values) ? values : [];
    internal bool Flag(string key) => string.Equals(Optional(key), "true",
        StringComparison.OrdinalIgnoreCase);
    internal long Long(string key, long fallback = 0) => long.TryParse(Optional(key),
        out long value) ? value : fallback;
}
