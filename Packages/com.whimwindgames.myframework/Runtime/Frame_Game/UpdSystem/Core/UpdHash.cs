using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

public static class UpdHash
{
    public static string data(byte[] data)
    {
        using (SHA256 sha = SHA256.Create())
        {
            return hex(sha.ComputeHash(data ?? Array.Empty<byte>()));
        }
    }

    public static string file(string path)
    {
        return file(path, CancellationToken.None);
    }

    public static string file(string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using (FileStream stream = File.OpenRead(path))
        using (SHA256 sha = SHA256.Create())
        {
            byte[] buf = new byte[128 * 1024];
            int got;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                got = stream.Read(buf, 0, buf.Length);
                if (got <= 0) break;
                sha.TransformBlock(buf, 0, got, buf, 0);
            }
            ct.ThrowIfCancellationRequested();
            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return hex(sha.Hash);
        }
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
