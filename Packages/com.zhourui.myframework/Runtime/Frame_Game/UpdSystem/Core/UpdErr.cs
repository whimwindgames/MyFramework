using System;

public sealed class UpdErr
{
    public readonly UpdCode code;
    public readonly string detail;
    public readonly UpdPhase phase;
    public readonly string message;
    public readonly long http;
    public readonly bool canRetry;

    public UpdErr(UpdCode code, string detail, UpdPhase phase = UpdPhase.Idle, Exception ex = null,
        long http = 0, bool canRetry = false)
    {
        this.code = code;
        this.detail = detail ?? string.Empty;
        this.phase = phase;
        message = ex == null ? string.Empty : ex.Message;
        this.http = http;
        this.canRetry = canRetry;
    }

    public override string ToString()
    {
        return string.IsNullOrEmpty(message)
            ? code + ":" + detail
            : code + ":" + detail + ":" + message;
    }
}

public sealed class UpdRet<T>
{
    public readonly bool ok;
    public readonly T value;
    public readonly UpdErr err;

    private UpdRet(bool ok, T value, UpdErr err)
    {
        this.ok = ok;
        this.value = value;
        this.err = err;
    }

    public static UpdRet<T> pass(T value)
    {
        return new UpdRet<T>(true, value, null);
    }

    public static UpdRet<T> fail(UpdErr err)
    {
        return new UpdRet<T>(false, default, err);
    }
}

public sealed class UpdBad : Exception
{
    public readonly UpdErr err;

    public UpdBad(UpdErr err, Exception inner = null) : base(err == null ? string.Empty : err.ToString(), inner)
    {
        this.err = err;
    }
}

public static class UpdFail
{
    public static void bad(UpdCode code, string detail, UpdPhase phase = UpdPhase.Idle,
        Exception ex = null)
    {
        throw new UpdBad(new UpdErr(code, detail, phase, ex), ex);
    }
}
