namespace MyFramework.BuildStudio.Core;

public static class PathSecurity
{
    public static void EnsureNoLinks(string path)
    {
        string full = Path.GetFullPath(path);
        FileSystemInfo info = File.Exists(full) ? new FileInfo(full) : new DirectoryInfo(full);
        if (info.Exists && info.LinkTarget is not null)
            throw new InvalidDataException("Path is a symbolic link: " + full);
        DirectoryInfo? directory = info is FileInfo file ? file.Directory : info as DirectoryInfo;
        while (directory is not null)
        {
            if (directory.Exists && directory.LinkTarget is not null)
                throw new InvalidDataException("Path traverses a symbolic link: " + full);
            directory = directory.Parent;
        }
    }

    public static string ResolveInside(string root, string relative, string label)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathFullyQualified(relative))
            throw new InvalidDataException(label + " must be relative.");
        string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        string full = Path.GetFullPath(Path.Combine(fullRoot, relative));
        StringComparison comparison = OperatingSystem.IsWindows() ?
            StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!full.StartsWith(fullRoot + Path.DirectorySeparatorChar, comparison))
            throw new InvalidDataException(label + " escapes its root.");
        return full;
    }
}
