using System;
using System.Diagnostics;

// Cross-assembly startup telemetry. Fractions count completed work, never elapsed time.
// UI polls immutable snapshots, so worker threads never touch Unity objects.
public static class FrameBootProgress
{
    public enum Step { Verify, ReadCode, Metadata, Assemblies, Framework, Data, Systems, Views }
    public const int StepCount = 8;
    public sealed class Snapshot
    {
        public readonly Step step;
        public readonly long done, total;
        public readonly string detail;
        public readonly double elapsed;
        public readonly float fraction;
        internal Snapshot(Step step, long done, long total, string detail, double elapsed)
        {
            this.step = step; this.done = done; this.total = total;
            this.detail = detail; this.elapsed = elapsed;
            fraction = ((int)step + (float)done / total) / StepCount;
        }
    }
    static readonly object Gate = new();
    static readonly Stopwatch Clock = new();
    static Step sStep;
    static long sDone, sTotal = 1;
    static string sDetail;
    static Exception sError;
    static bool sActive;

    public static void Begin()
    {
        lock (Gate)
        {
            sStep = Step.Verify; sDone = 0; sTotal = 1;
            sDetail = "校验本地资源"; sError = null; sActive = true; Clock.Restart();
        }
    }
    public static void Report(Step step, int done, int total, string detail)
        => Report(step, (long)done, total, detail);

    public static void Report(Step step, long done, long total, string detail)
    {
        lock (Gate)
        {
            if (!sActive || sError != null || step < sStep) return;
            total = Math.Max(1, total); done = Math.Max(0, Math.Min(done, total));
            if (step == sStep && (double)done / total < (double)sDone / sTotal) return;
            if (step != sStep)
                UnityEngine.Debug.Log($"[Startup] {sStep} complete at {Clock.Elapsed.TotalSeconds:F2}s; {step}");
            sStep = step; sDone = done; sTotal = total; sDetail = detail;
        }
    }
    public static Snapshot Read()
    {
        lock (Gate) return new Snapshot(sStep, sDone, sTotal, sDetail, Clock.Elapsed.TotalSeconds);
    }
    public static Exception Error { get { lock (Gate) return sError; } }
    public static void Fail(Exception error)
    {
        lock (Gate) { if (sActive) sError ??= error; }
    }
    public static void End()
    {
        lock (Gate) { sActive = false; Clock.Stop(); }
    }
}
