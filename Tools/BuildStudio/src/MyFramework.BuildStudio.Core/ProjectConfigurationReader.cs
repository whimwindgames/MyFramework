using MyFramework.BuildStudio;

namespace MyFramework.BuildStudio.Core;

public sealed record ProjectConfigurationValue(string Name, string Value, string SourcePath);

public sealed class ProjectConfigurationSnapshot
{
    public string FilePath { get; init; } = string.Empty;
    public string? Error { get; init; }
    public Dictionary<string, ProjectConfigurationValue> Values { get; } =
        new(StringComparer.Ordinal);
    public bool Exists => File.Exists(FilePath);
}

public sealed record ProjectConfigurationDisplayItem(string Label, bool Configured,
    string Detail);

public static class ProjectConfigurationReader
{
    const int MaxBytes = 128 * 1024;
    const int MaxDepth = 4;

    public static ProjectConfigurationSnapshot Read(ProjectDocument project,
        MfBuildProfile profile)
    {
        if (!profile.properties.TryGetValue("configurationFile", out string? configured) ||
            string.IsNullOrWhiteSpace(configured)) return new ProjectConfigurationSnapshot();
        string path;
        try { path = ResolvePath(configured); }
        catch (Exception exception)
        {
            return new ProjectConfigurationSnapshot { Error = exception.Message };
        }

        ProjectConfigurationSnapshot result = new() { FilePath = path };
        if (!File.Exists(path)) return result;
        Dictionary<string, string> expanded = new(StringComparer.Ordinal)
        {
            ["HOME"] = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ["PROJECT_ROOT"] = project.ProjectRoot,
            ["MF_PROJECT_ROOT"] = project.ProjectRoot,
            ["FISHING_WORKSPACE_ROOT"] = Path.GetFullPath(Path.Combine(
                project.ProjectRoot, "..")),
        };
        try
        {
            loadFile(path, result, expanded, new HashSet<string>(StringComparer.Ordinal), 0);
            return result;
        }
        catch (Exception exception)
        {
            ProjectConfigurationSnapshot failed = new()
            {
                FilePath = path,
                Error = exception.Message,
            };
            foreach ((string name, ProjectConfigurationValue value) in result.Values)
                failed.Values[name] = value;
            return failed;
        }
    }

    public static string ResolvePath(string configured)
    {
        string path = configured.Trim();
        if (path.StartsWith("~/", StringComparison.Ordinal) ||
            path.StartsWith("~\\", StringComparison.Ordinal))
            path = Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.UserProfile), path[2..]);
        if (!Path.IsPathRooted(path)) throw new InvalidDataException(
            "配置文件必须使用绝对路径或 ~/ 路径。");
        return Path.GetFullPath(path);
    }

    public static IReadOnlyList<ProjectConfigurationDisplayItem> Describe(
        MfBuildProfile profile, ProjectConfigurationSnapshot snapshot)
    {
        if (!profile.properties.TryGetValue("configurationDisplay", out string? declaration) ||
            string.IsNullOrWhiteSpace(declaration)) return [];
        List<ProjectConfigurationDisplayItem> result = [];
        foreach (string raw in declaration.Split(';', StringSplitOptions.RemoveEmptyEntries |
                     StringSplitOptions.TrimEntries))
        {
            int split = raw.IndexOf('=');
            if (split <= 0 || split == raw.Length - 1) continue;
            string label = raw[..split].Trim();
            string[] names = raw[(split + 1)..].Split('|',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            ProjectConfigurationValue? value = names.Select(name =>
                    snapshot.Values.GetValueOrDefault(name)).FirstOrDefault(item => item is not null);
            if (value is null)
            {
                result.Add(new ProjectConfigurationDisplayItem(label, false, "未配置"));
                continue;
            }
            bool secret = names.Any(name => name.Contains("KEY", StringComparison.Ordinal));
            string detail = secret ? describeSecret(value) : value.Value;
            result.Add(new ProjectConfigurationDisplayItem(label, true, detail));
        }
        return result;
    }

    static string describeSecret(ProjectConfigurationValue value)
    {
        string raw = value.Value.Trim();
        bool likelyPath = Path.IsPathRooted(raw) || raw.StartsWith("~/",
            StringComparison.Ordinal) || raw.Contains('/') || raw.Contains('\\');
        if (!likelyPath) return "已配置（内容已隐藏）";
        string shown = Path.GetFileName(raw.TrimEnd(Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar));
        return "已配置 · " + (string.IsNullOrWhiteSpace(shown) ? "Key 文件" : shown);
    }

    static void loadFile(string path, ProjectConfigurationSnapshot snapshot,
        Dictionary<string, string> expanded, HashSet<string> stack, int depth)
    {
        if (depth > MaxDepth) throw new InvalidDataException("配置 source 嵌套过深。");
        path = Path.GetFullPath(path);
        FileInfo file = new(path);
        if (!file.Exists) throw new FileNotFoundException("配置文件不存在。", path);
        if (file.Length <= 0 || file.Length > MaxBytes ||
            (file.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("配置文件为空、过大或是链接: " + path);
        if (!stack.Add(path)) throw new InvalidDataException("配置存在 source 循环: " + path);
        try
        {
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                if (line.StartsWith("source ", StringComparison.Ordinal))
                {
                    string source = expand(unquote(line[7..].Trim()), expanded);
                    if (!Path.IsPathRooted(source)) throw new InvalidDataException(
                        "配置 source 必须是绝对路径: " + source);
                    loadFile(source, snapshot, expanded, stack, depth + 1);
                    continue;
                }
                if (line.StartsWith("export ", StringComparison.Ordinal))
                    line = line[7..].TrimStart();
                int split = line.IndexOf('=');
                if (split <= 0) throw new InvalidDataException(
                    "不支持的配置行: " + path);
                string name = line[..split].Trim();
                if (!isName(name)) throw new InvalidDataException("非法配置名: " + name);
                string value = expand(unquote(line[(split + 1)..].Trim()), expanded);
                if (string.IsNullOrWhiteSpace(value)) continue;
                expanded[name] = value;
                snapshot.Values[name] = new ProjectConfigurationValue(name, value, path);
            }
        }
        finally { stack.Remove(path); }
    }

    static bool isName(string value)
    {
        if (value.Length == 0 || value[0] is not ('_' or >= 'A' and <= 'Z')) return false;
        return value.All(item => item == '_' || item is >= 'A' and <= 'Z' or >= '0' and <= '9');
    }

    static string unquote(string value)
    {
        if (value.Length >= 2 && (value[0] == '"' && value[^1] == '"' ||
                                  value[0] == '\'' && value[^1] == '\''))
            return value[1..^1];
        return value;
    }

    static string expand(string value, IReadOnlyDictionary<string, string> values)
    {
        if (value.Contains("$(", StringComparison.Ordinal) || value.Contains('`'))
            throw new InvalidDataException("配置不支持执行命令。");
        foreach ((string name, string replacement) in values.OrderByDescending(item =>
                     item.Key.Length))
        {
            value = value.Replace("${" + name + "}", replacement,
                StringComparison.Ordinal);
            value = value.Replace("$" + name, replacement, StringComparison.Ordinal);
        }
        return value;
    }
}
