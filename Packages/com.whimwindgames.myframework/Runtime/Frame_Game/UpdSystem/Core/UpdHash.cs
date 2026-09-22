using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

public static class UpdHash
{
    public static string data(byte[] data)
    {
        byte[] digest = new byte[32];
        var sha = new UpdSha256();
        sha.Update(data ?? Array.Empty<byte>());
        sha.Finish(digest);
        return hex(digest);
    }

    public static string file(string path)
    {
        return file(path, CancellationToken.None);
    }

    public static string file(string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var sha = new UpdSha256();
        byte[] buf = new byte[128 * 1024];
        using (FileStream stream = File.OpenRead(path))
        {
            int got;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                got = stream.Read(buf, 0, buf.Length);
                if (got <= 0) break;
                sha.Update(buf.AsSpan(0, got));
            }
        }
        ct.ThrowIfCancellationRequested();
        byte[] digest = new byte[32];
        sha.Finish(digest);
        return hex(digest);
    }

    private static string hex(byte[] data)
    {
        StringBuilder text = new StringBuilder(data.Length * 2);
        for (int i = 0; i < data.Length; ++i)
        {
            text.Append(data[i].ToString("x2", CultureInfo.InvariantCulture));
        }
        return text.ToString();
    }
}
