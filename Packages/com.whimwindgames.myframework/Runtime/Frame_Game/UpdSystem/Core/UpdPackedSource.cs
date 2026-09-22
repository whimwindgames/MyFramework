using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;

// The catalog belongs to the installed Player, never to a writable download directory.
// It is only a source hint: every extracted file is checked against the signed release.
public sealed class UpdPackedSource
{
    public const string CatalogName = "builtin-catalog.txt";
    private readonly string mRoot;
    private readonly string mArchive;
    private readonly string mPrefix;
    private readonly Dictionary<string, UpdFile> mFiles = new(StringComparer.Ordinal);

    public UpdPackedSource(string streamingRoot, string platform)
    {
        if (!UpdFmt.isId(platform)) throw new ArgumentException(nameof(platform));
        if (streamingRoot.StartsWith("jar:file:", StringComparison.OrdinalIgnoreCase))
        {
            int split = streamingRoot.IndexOf("!/", StringComparison.Ordinal);
            if (split < 0) throw new ArgumentException(nameof(streamingRoot));
            mArchive = new Uri(streamingRoot.Substring(4, split - 4)).LocalPath;
            mPrefix = streamingRoot.Substring(split + 2).TrimEnd('/') + "/" + platform + "/";
        }
        else
        {
            string root = streamingRoot.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
                ? new Uri(streamingRoot).LocalPath : streamingRoot;
            mRoot = Path.GetFullPath(Path.Combine(root, platform));
        }
        using Stream stream = open(CatalogName);
        if (stream == null) return; // Older Players retain the copy/download path.
        if (stream.Length > UpdLim.ManMax) throw new InvalidDataException("builtin_catalog_size");
        using var reader = new StreamReader(stream, Encoding.UTF8, true);
        if (reader.ReadLine() != "BUILTIN-1") throw new InvalidDataException("builtin_catalog_version");
        string line;
        while ((line = reader.ReadLine()) != null)
        {
            string[] row = line.Split('\t');
            if (row.Length != 3 || !UpdFmt.isSha(row[0]) || !UpdFmt.isPath(row[2]) ||
                !long.TryParse(row[1], NumberStyles.None, CultureInfo.InvariantCulture, out long size) ||
                size <= 0 || size > UpdLim.FileSize || mFiles.Count >= UpdLim.FileMax)
                throw new InvalidDataException("builtin_catalog_entry");
            mFiles.Add(row[2], new UpdFile { path = row[2], sha256 = row[0], size = size });
        }
    }

    public bool contains(UpdFile file) => mFiles.TryGetValue(file.path, out var packed) &&
        packed.sha256 == file.sha256 && packed.size == file.size;

    internal void extract(UpdFile file, string destination, CancellationToken ct)
    {
        if (!contains(file)) throw new FileNotFoundException("builtin_identity", file.path);
        using Stream input = open(file.path);
        if (input == null || input.Length != file.size) throw new InvalidDataException("builtin_missing");
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var buffer = new byte[128 * 1024];
        long copied = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            int count = input.Read(buffer, 0, buffer.Length);
            if (count == 0) break;
            copied += count;
            if (copied > file.size) throw new InvalidDataException("builtin_size");
            output.Write(buffer, 0, count);
        }
        if (copied != file.size) throw new InvalidDataException("builtin_size");
    }

    private Stream open(string path)
    {
        if (mArchive == null)
        {
            string full = Path.Combine(mRoot, path.Replace('/', Path.DirectorySeparatorChar));
            return File.Exists(full) ? File.OpenRead(full) : null;
        }
        // A separate archive per request allows unrelated bundles to load concurrently.
        var archive = ZipFile.OpenRead(mArchive);
        try
        {
            var entry = archive.GetEntry(mPrefix + path);
            if (entry == null) { archive.Dispose(); return null; }
            return new EntryStream(entry.Open(), archive, entry.Length);
        }
        catch { archive.Dispose(); throw; }
    }

    // Zip entry streams need not support Length; also own the archive's file handle.
    private sealed class EntryStream : Stream
    {
        private readonly Stream mInput;
        private readonly ZipArchive mOwner;
        private readonly long mLength;
        public EntryStream(Stream input, ZipArchive owner, long length)
        { mInput = input; mOwner = owner; mLength = length; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => mLength;
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => mInput.Read(buffer, offset, count);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        { if (disposing) { mInput.Dispose(); mOwner.Dispose(); } base.Dispose(disposing); }
    }
}
