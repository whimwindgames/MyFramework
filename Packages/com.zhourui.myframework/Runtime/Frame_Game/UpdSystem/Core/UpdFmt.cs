using System;
using System.Globalization;
using System.Text;

public static class UpdFmt
{
    private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

    public static string text(byte[] data)
    {
        try
        {
            return Utf8.GetString(data ?? Array.Empty<byte>());
        }
        catch (Exception ex)
        {
            UpdFail.bad(UpdCode.Schema, "utf8", UpdPhase.Idle, ex);
            return null;
        }
    }

    public static byte[] b64(string text, int min, int max)
    {
        try
        {
            byte[] data = Convert.FromBase64String(text ?? string.Empty);
            if (data.Length < min || data.Length > max)
            {
                UpdFail.bad(UpdCode.Schema, "base64_size");
            }
            return data;
        }
        catch (UpdBad)
        {
            throw;
        }
        catch (Exception ex)
        {
            UpdFail.bad(UpdCode.Schema, "base64", UpdPhase.Idle, ex);
            return null;
        }
    }

    public static DateTime time(string text)
    {
        DateTime value = default;
        if (string.IsNullOrEmpty(text) || !text.EndsWith("Z", StringComparison.Ordinal) ||
            !DateTime.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out value))
        {
            UpdFail.bad(UpdCode.Schema, "utc");
        }
        return value;
    }

    public static bool isSha(string value)
    {
        if (value == null || value.Length != 64)
        {
            return false;
        }
        for (int i = 0; i < value.Length; ++i)
        {
            char c = value[i];
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
            {
                return false;
            }
        }
        return true;
    }

    public static bool isId(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > UpdLim.IdMax ||
            value == "." || value == "..")
        {
            return false;
        }
        for (int i = 0; i < value.Length; ++i)
        {
            char c = value[i];
            if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ||
                (c >= '0' && c <= '9') || c == '-' || c == '_' || c == '.'))
            {
                return false;
            }
        }
        return true;
    }

    public static bool isPath(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > UpdLim.PathMax ||
            value[0] == '/' || value[value.Length - 1] == '/' || value.IndexOf('\\') >= 0)
        {
            return false;
        }
        string[] parts = value.Split('/');
        for (int i = 0; i < parts.Length; ++i)
        {
            string part = parts[i];
            if (part.Length == 0 || part == "." || part == ".." ||
                part.EndsWith(".", StringComparison.Ordinal) ||
                part.EndsWith(" ", StringComparison.Ordinal) || isWindowsDevice(part))
            {
                return false;
            }
            for (int j = 0; j < part.Length; ++j)
            {
                char c = part[j];
                if (char.IsControl(c) || c == ':' || c == '*' || c == '?' || c == '"' ||
                    c == '<' || c == '>' || c == '|')
                {
                    return false;
                }
            }
        }
        return true;
    }

    private static bool isWindowsDevice(string part)
    {
        int dot = part.IndexOf('.');
        string name = (dot < 0 ? part : part.Substring(0, dot)).ToUpperInvariant();
        if (name == "CON" || name == "PRN" || name == "AUX" || name == "NUL")
        {
            return true;
        }
        if (name.Length == 4 && (name.StartsWith("COM", StringComparison.Ordinal) ||
            name.StartsWith("LPT", StringComparison.Ordinal)) &&
            name[3] >= '1' && name[3] <= '9')
        {
            return true;
        }
        return false;
    }
}
