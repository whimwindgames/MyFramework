using System;
using System.Threading;

// Progress of the full verification pass that the loading screen runs.
public sealed class UpdVerifyReport
{
    public int Total;
    public int Done;
    public int Failed;
    public long TotalBytes;
    public long DoneBytes;
    public string FailedPath;
    public Exception FirstError;

    public float progress => TotalBytes > 0
        ? (float)((double)DoneBytes / TotalBytes)
        : (Total > 0 ? (float)Done / Total : 1f);
}

public static class UpdVerify
{
    // Use the exact release/store returned by UpdCore.run. Its verified identities are then
    // reused by getPath/getPathA. No Unity API is accessed here; call from a worker thread.
    // Each callback receives a separate snapshot and runs serially on a worker thread.
    public static UpdVerifyReport all(UpdRes resources, int workers,
        Action<UpdVerifyReport> report, CancellationToken ct)
    {
        if (resources == null) throw new ArgumentNullException(nameof(resources));
        return resources.verifyAll(workers, report, ct);
    }
}
