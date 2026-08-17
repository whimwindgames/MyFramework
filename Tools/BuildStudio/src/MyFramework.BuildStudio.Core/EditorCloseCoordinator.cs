namespace MyFramework.BuildStudio.Core;

public static class EditorCloseCoordinator
{
    public const string ControlDirectoryName = "MyFrameworkBuildStudio";
    public const string RequestFileName = "editor-close.request";
    public const string AckFileName = "editor-close.ack";

    public static async Task EnsureClosedAsync(string projectRoot,
        IProgress<BuildProgress>? progress = null, TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        string root = Path.GetFullPath(projectRoot);
        string lockPath = Path.Combine(root, "Temp", "UnityLockfile");
        string controlRoot = Path.Combine(root, "Temp", ControlDirectoryName);
        string requestPath = Path.Combine(controlRoot, RequestFileName);
        string ackPath = Path.Combine(controlRoot, AckFileName);
        if (!File.Exists(lockPath))
        {
            deleteIfExists(requestPath);
            deleteIfExists(ackPath);
            return;
        }

        Directory.CreateDirectory(controlRoot);
        PathSecurity.EnsureNoLinks(controlRoot);
        clearStaleRequest(requestPath);
        deleteIfExists(ackPath);
        string token = Guid.NewGuid().ToString("N");
        writeRequest(requestPath, token);
        progress?.Report(new BuildProgress("editor-close", "waiting",
            "Unity 项目正在使用，已请求保存并关闭编辑器。", -1));

        DateTime deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromMinutes(2));
        try
        {
            while (File.Exists(lockPath))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string? failure = readFailure(ackPath, token);
                if (failure is not null)
                    throw new InvalidOperationException("Unity Editor refused to close: " + failure);
                if (DateTime.UtcNow >= deadline)
                    throw new TimeoutException(
                        "Unity Editor did not close within the allowed time. Save prompts or compilation may be blocking it.");
                await Task.Delay(250, cancellationToken);
            }
            progress?.Report(new BuildProgress("editor-close", "succeeded",
                "Unity 编辑器已安全关闭。", -1));
        }
        finally
        {
            deleteOwned(requestPath, token);
            deleteOwned(ackPath, token);
        }
    }

    static void clearStaleRequest(string path)
    {
        if (!File.Exists(path)) return;
        DateTime written = File.GetLastWriteTimeUtc(path);
        if (DateTime.UtcNow - written <= TimeSpan.FromMinutes(2))
            throw new InvalidOperationException(
                "Another Build Studio process is already requesting this Unity project to close.");
        File.Delete(path);
    }

    static void writeRequest(string path, string token)
    {
        string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None);
            using StreamWriter writer = new(stream);
            writer.WriteLine(token);
            writer.Flush();
            stream.Flush(true);
            File.Move(temporary, path);
        }
        finally { deleteIfExists(temporary); }
    }

    static string? readFailure(string path, string token)
    {
        if (!File.Exists(path)) return null;
        string[] lines;
        try { lines = File.ReadAllLines(path); }
        catch (IOException) { return null; }
        if (lines.Length < 2 || !string.Equals(lines[0].Trim(), token,
                StringComparison.Ordinal)) return null;
        if (string.Equals(lines[1].Trim(), "ok", StringComparison.Ordinal)) return null;
        return lines.Length > 2 && !string.IsNullOrWhiteSpace(lines[2])
            ? lines[2].Trim() : "Unknown Editor error.";
    }

    static void deleteOwned(string path, string token)
    {
        if (!File.Exists(path)) return;
        try
        {
            using StreamReader reader = new(new FileStream(path, FileMode.Open,
                FileAccess.Read, FileShare.ReadWrite));
            if (string.Equals(reader.ReadLine()?.Trim(), token, StringComparison.Ordinal))
                File.Delete(path);
        }
        catch (IOException) { }
    }

    static void deleteIfExists(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
    }
}
