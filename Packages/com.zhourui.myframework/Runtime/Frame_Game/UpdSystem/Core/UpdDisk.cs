using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;

internal sealed class UpdDisk
{
    private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
    private readonly string mRoot;

    public UpdDisk(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new ArgumentException("root");
        }
        mRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        if (File.Exists(mRoot))
        {
            throw new IOException("root_file");
        }
        Directory.CreateDirectory(mRoot);
        check(mRoot);
    }

    public string root
    {
        get { return mRoot; }
    }

    public string makeDir(string path)
    {
        string full = fullPath(path);
        if (File.Exists(full))
        {
            throw new IOException("dir_file");
        }
        check(full);
        Directory.CreateDirectory(full);
        check(full);
        return full;
    }

    public string child(string root, string path)
    {
        if (!UpdFmt.isPath(path))
        {
            UpdFail.bad(UpdCode.Schema, "path");
        }
        string local = path.Replace('/', Path.DirectorySeparatorChar);
        string full = fullPath(Path.Combine(root, local));
        if (!under(root, full))
        {
            UpdFail.bad(UpdCode.Schema, "path_escape");
        }
        check(full);
        return full;
    }

    public void makeParent(string path)
    {
        string full = fullPath(path);
        string parent = Path.GetDirectoryName(full);
        if (string.IsNullOrEmpty(parent))
        {
            throw new IOException("parent");
        }
        makeDir(parent);
    }

    public byte[] read(string path, int max)
    {
        string full = fullPath(path);
        check(full);
        FileInfo info = new FileInfo(full);
        if (!info.Exists || info.Length < 0 || info.Length > max)
        {
            UpdFail.bad(UpdCode.State, "read_size");
        }
        return File.ReadAllBytes(full);
    }

    public long fileSize(string path)
    {
        string full = fullPath(path);
        check(full);
        FileInfo info = new FileInfo(full);
        if (!info.Exists)
        {
            throw new FileNotFoundException("file", full);
        }
        return info.Length;
    }

    public long available()
    {
        try
        {
            string root = Path.GetPathRoot(mRoot);
            if (string.IsNullOrEmpty(root))
            {
                throw new IOException("drive_root");
            }
            return new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex)
        {
            throw new IOException("drive_space", ex);
        }
    }

    public void write(string path, byte[] data)
    {
        if (data == null)
        {
            UpdFail.bad(UpdCode.State, "write_null");
        }
        string full = fullPath(path);
        makeParent(full);
        string temp = full + ".new-" + Guid.NewGuid().ToString("N");
        try
        {
            using (FileStream stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 64 * 1024, FileOptions.WriteThrough))
            {
                stream.Write(data, 0, data.Length);
                stream.Flush(true);
            }
            move(temp, full);
        }
        finally
        {
            tryDelete(temp);
        }
    }

    public void writeJson(string path, object value)
    {
        if (value == null)
        {
            UpdFail.bad(UpdCode.State, "json_null");
        }
        write(path, Utf8.GetBytes(JsonConvert.SerializeObject(value, Formatting.None)));
    }

    public void move(string source, string dst)
    {
        string src = fullPath(source);
        string target = fullPath(dst);
        check(src);
        makeParent(target);
        if (File.Exists(target))
        {
            check(target);
            File.Replace(src, target, null);
        }
        else
        {
            File.Move(src, target);
        }
    }

    public void delete(string path)
    {
        string full = fullPath(path);
        if (File.Exists(full))
        {
            check(full);
            File.Delete(full);
        }
    }

    public void quarantine(string path)
    {
        string full = fullPath(path);
        if (!File.Exists(full))
        {
            return;
        }
        string bad = full + ".bad-" + DateTime.UtcNow.Ticks.ToString();
        move(full, bad);
    }

    public void dropDir(string path)
    {
        string full = fullPath(path);
        if (!Directory.Exists(full))
        {
            return;
        }
        check(full);
        string[] entries = Directory.GetFileSystemEntries(full);
        for (int i = 0; i < entries.Length; ++i)
        {
            check(entries[i]);
            if (Directory.Exists(entries[i]))
            {
                dropDir(entries[i]);
            }
            else
            {
                File.Delete(entries[i]);
            }
        }
        Directory.Delete(full);
    }

    public string[] getFiles(string path)
    {
        List<string> files = new List<string>();
        addFiles(fullPath(path), files);
        return files.ToArray();
    }

    private void addFiles(string path, List<string> files)
    {
        check(path);
        string[] entries = Directory.GetFileSystemEntries(path);
        for (int i = 0; i < entries.Length; ++i)
        {
            check(entries[i]);
            if (Directory.Exists(entries[i]))
            {
                addFiles(entries[i], files);
            }
            else
            {
                files.Add(entries[i]);
            }
        }
    }

    private string fullPath(string path)
    {
        string full = Path.GetFullPath(path);
        if (full != mRoot && !under(mRoot, full))
        {
            throw new InvalidDataException("path_escape");
        }
        return full;
    }

    private void check(string path)
    {
        string full = fullPath(path);
        FileSystemInfo item = File.Exists(full)
            ? (FileSystemInfo)new FileInfo(full)
            : new DirectoryInfo(full);
        while (item != null)
        {
            if (item.Exists && (item.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("reparse");
            }
            if (string.Equals(Path.GetFullPath(item.FullName), mRoot, pathCmp()))
            {
                return;
            }
            item = item is DirectoryInfo dir ? dir.Parent : ((FileInfo)item).Directory;
        }
        throw new InvalidDataException("path_root");
    }

    private static bool under(string root, string path)
    {
        string prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, pathCmp());
    }

    private static StringComparison pathCmp()
    {
        return Path.DirectorySeparatorChar == '\\'
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
    }

    private static void tryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
}
