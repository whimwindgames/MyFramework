using System;
using System.Runtime.InteropServices;

// Native SHA-256 shipped with the package (Runtime/Plugins/Android/libs/<abi>/libarcadesha.so).
// The managed implementation measures about 2 MB/s under IL2CPP on low-end Android, which
// turns a 28 MB bundle check into 12 seconds; the native one reaches roughly 68 MB/s on the
// same device while producing an identical digest. Platforms or builds without the binary
// fall back to the managed path automatically.
internal static class UpdNative
{
    const int Unknown = 0;
    const int Available = 1;
    const int Unavailable = 2;

    static int sState = Unknown;

    [DllImport("arcadesha", EntryPoint = "arcade_sha256_file")]
    static extern int sha256File(string path, byte[] output);

    // Returns false when the native library is missing or failed; callers must then use the
    // managed implementation. Only library-loading failures disable the fast path for good,
    // so a transient file error cannot silently downgrade performance for the whole run.
    public static bool tryFile(string path, byte[] output)
    {
        if (sState == Unavailable) return false;
        try
        {
            bool ok = sha256File(path, output) == 0;
            sState = Available;
            return ok;
        }
        catch (DllNotFoundException) { sState = Unavailable; return false; }
        catch (EntryPointNotFoundException) { sState = Unavailable; return false; }
        catch (Exception) { return false; }
    }
}
