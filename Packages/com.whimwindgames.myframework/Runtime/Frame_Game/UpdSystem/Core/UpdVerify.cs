using System;
using System.Collections.Generic;
using System.Threading;

// Progress of the full verification pass that the loading screen runs.
public sealed class UpdVerifyReport
{
    public int Total;
    public int Done;
    public int Failed;
    public long TotalBytes;
    public long DoneBytes;

    public float progress => TotalBytes > 0
        ? (float)((double)DoneBytes / TotalBytes)
        : (Total > 0 ? (float)Done / Total : 1f);
}

public static class UpdVerify
{
    // Verifies (and materializes when missing) every file of the active release, so gameplay
    // never pays the verification cost. Blocking: call it from a worker thread, like the other
    // store operations. The report callback may come from any worker thread and must be
    // marshalled by the caller. streamingRoot is Application.streamingAssetsPath.
    public static UpdVerifyReport all(UpdCfg cfg, string streamingRoot, int workers,
        Action<UpdVerifyReport> report, CancellationToken ct)
    {
        if (cfg == null) throw new ArgumentNullException(nameof(cfg));
        var store = new UpdStore(cfg);
        store.setPacked(new UpdPackedSource(streamingRoot, cfg.platform));
        return store.verifyAll(store.loadActive(), workers, report, ct);
    }
}
