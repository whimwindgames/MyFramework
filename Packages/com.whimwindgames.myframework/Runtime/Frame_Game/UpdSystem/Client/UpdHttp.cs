using System;
using System.Globalization;
using System.IO;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine.Networking;

internal sealed class UpdMem
{
    public byte[] data;
}

internal sealed class UpdHttp
{
    private readonly Uri mBase;
    private readonly string mEnv;
    private readonly int mTimeout;

    public UpdHttp(string baseUrl, string env, int timeout)
    {
        mBase = new Uri(baseUrl.TrimEnd('/') + "/", UriKind.Absolute);
        mEnv = env;
        mTimeout = timeout;
    }

    public UniTask<UpdRet<UpdMem>> latest(string platform, string baseId,
        int timeout, CancellationToken ct)
    {
        string path = esc(mEnv) + "/latest/" + esc(platform) + "/" + esc(baseId) +
            ".json?v=" + Guid.NewGuid().ToString("N");
        return getMem(url(path), UpdLim.LatestMax, UpdPhase.Latest, true, timeout, ct);
    }

    public UniTask<UpdRet<UpdMem>> manifest(string releaseId, CancellationToken ct)
    {
        string path = esc(mEnv) + "/releases/" + esc(releaseId) + "/manifest.json";
        return getMem(url(path), UpdLim.ManMax, UpdPhase.Manifest, false, mTimeout, ct);
    }

    public async UniTask<UpdRet<long>> download(string releaseId, UpdFile file, string dst,
        long from, Action<long> prog, CancellationToken ct)
    {
        if (file == null || from < 0 || from >= file.size || prog == null)
        {
            return UpdRet<long>.fail(new UpdErr(UpdCode.State, "download_args", UpdPhase.Down));
        }
        string path = esc(mEnv) + "/releases/" + esc(releaseId) + "/files/" +
            escPath(file.path);
        FileSink sink = null;
        try
        {
            sink = new FileSink(dst, from, file.size, prog);
            using (UnityWebRequest req = makeReq(url(path), sink))
            {
                sink.bind(req);
                req.SetRequestHeader("Accept-Encoding", "identity");
                if (from > 0)
                {
                    req.SetRequestHeader("Range", "bytes=" + from.ToString(CultureInfo.InvariantCulture) + "-");
                }
                UpdErr waitErr = await send(req, UpdPhase.Down, ct);
                sink.close();
                if (waitErr != null)
                {
                    return UpdRet<long>.fail(waitErr);
                }
                if (sink.err != null)
                {
                    return UpdRet<long>.fail(sink.err);
                }
                UpdErr reqErr = getErr(req, UpdPhase.Down);
                if (reqErr != null)
                {
                    return UpdRet<long>.fail(reqErr);
                }
                if (!sink.isFull || sink.got != file.size)
                {
                    return UpdRet<long>.fail(new UpdErr(UpdCode.Range, "file_length",
                        UpdPhase.Down, null, req.responseCode, true));
                }
                return UpdRet<long>.pass(sink.got);
            }
        }
        catch (Exception ex)
        {
            return UpdRet<long>.fail(new UpdErr(UpdCode.Disk, "file_write", UpdPhase.Down, ex));
        }
        finally
        {
            if (sink != null)
            {
                sink.close();
                sink.Dispose();
            }
        }
    }

    private async UniTask<UpdRet<UpdMem>> getMem(string uri, int max, UpdPhase phase,
        bool noCache, int timeout, CancellationToken ct)
    {
        MemSink sink = new MemSink(max, phase);
        try
        {
            using (UnityWebRequest req = makeReq(uri, sink, timeout))
            {
                if (noCache)
                {
                    req.SetRequestHeader("Cache-Control",
                        "no-cache, no-store, max-age=0, must-revalidate");
                    req.SetRequestHeader("Pragma", "no-cache");
                }
                req.SetRequestHeader("Accept", "application/json");
                UpdErr waitErr = await send(req, phase, ct);
                if (waitErr != null)
                {
                    return UpdRet<UpdMem>.fail(waitErr);
                }
                if (sink.err != null)
                {
                    return UpdRet<UpdMem>.fail(sink.err);
                }
                UpdErr reqErr = getErr(req, phase);
                if (reqErr != null)
                {
                    return UpdRet<UpdMem>.fail(reqErr);
                }
                if (req.responseCode != 200)
                {
                    return UpdRet<UpdMem>.fail(httpErr(req, phase));
                }
                return UpdRet<UpdMem>.pass(new UpdMem { data = sink.bytes() });
            }
        }
        catch (Exception ex)
        {
            return UpdRet<UpdMem>.fail(new UpdErr(UpdCode.Net, "request", phase, ex, 0, true));
        }
        finally
        {
            sink.Dispose();
        }
    }

    private UnityWebRequest makeReq(string uri, DownloadHandler sink, int timeout = 0)
    {
        UnityWebRequest req = new UnityWebRequest(uri, UnityWebRequest.kHttpVerbGET, sink, null)
        {
            disposeDownloadHandlerOnDispose = false,
            redirectLimit = 0,
            timeout = timeout > 0 ? timeout : mTimeout,
        };
        return req;
    }

    private static async UniTask<UpdErr> send(UnityWebRequest req, UpdPhase phase,
        CancellationToken ct)
    {
        try
        {
            await req.SendWebRequest().ToUniTask(cancellationToken: ct, cancelImmediately: true);
            return null;
        }
        catch (UnityWebRequestException ex)
        {
            if (ct.IsCancellationRequested)
            {
                return new UpdErr(UpdCode.Cancel, "request", phase, ex);
            }
            return null;
        }
        catch (OperationCanceledException ex)
        {
            return new UpdErr(UpdCode.Cancel, "request", phase, ex);
        }
        catch (Exception ex)
        {
            return new UpdErr(UpdCode.Net, "request", phase, ex, 0, true);
        }
    }

    private static UpdErr getErr(UnityWebRequest req, UpdPhase phase)
    {
        if (req.result == UnityWebRequest.Result.Success)
        {
            return null;
        }
        if (req.result == UnityWebRequest.Result.ProtocolError)
        {
            return httpErr(req, phase);
        }
        return new UpdErr(UpdCode.Net, "request", phase, null, req.responseCode, true);
    }

    private static UpdErr httpErr(UnityWebRequest req, UpdPhase phase)
    {
        long code = req.responseCode;
        bool retry = code == 408 || code == 429 || code >= 500;
        return new UpdErr(UpdCode.Http, "status", phase, null, code, retry);
    }

    private string url(string path)
    {
        Uri value = new Uri(mBase, path);
        if (value.Scheme != Uri.UriSchemeHttps || value.Host != mBase.Host ||
            value.Port != mBase.Port)
        {
            throw new InvalidDataException("http_url");
        }
        return value.AbsoluteUri;
    }

    private static string escPath(string path)
    {
        string[] parts = path.Split('/');
        for (int i = 0; i < parts.Length; ++i)
        {
            parts[i] = esc(parts[i]);
        }
        return string.Join("/", parts);
    }

    private static string esc(string value)
    {
        return Uri.EscapeDataString(value);
    }
}

internal sealed class MemSink : DownloadHandlerScript
{
    private readonly MemoryStream mData;
    private readonly int mMax;
    private readonly UpdPhase mPhase;

    public UpdErr err { get; private set; }

    public MemSink(int max, UpdPhase phase) : base(new byte[64 * 1024])
    {
        mMax = max;
        mPhase = phase;
        mData = new MemoryStream(Math.Min(max, 64 * 1024));
    }

    public byte[] bytes()
    {
        return mData.ToArray();
    }

    protected override bool ReceiveData(byte[] data, int length)
    {
        try
        {
            if (data == null || length < 0 || length > data.Length || mData.Length + length > mMax)
            {
                err = new UpdErr(UpdCode.Schema, "body_size", mPhase);
                return false;
            }
            mData.Write(data, 0, length);
            return true;
        }
        catch (Exception ex)
        {
            err = new UpdErr(UpdCode.Net, "body_read", mPhase, ex, 0, true);
            return false;
        }
    }
}

internal static class UpdRange
{
    public static UpdErr check(long from, long size, long code,
        string contentLength, string contentRange)
    {
        long goodCode = from == 0 ? 200 : 206;
        if (code != goodCode)
        {
            if (from > 0 && code == 200)
            {
                return range("range_reset", code);
            }
            if (code > 0)
            {
                bool retry = code == 408 || code == 429 || code >= 500;
                return new UpdErr(UpdCode.Http, "status", UpdPhase.Down,
                    null, code, retry);
            }
            return new UpdErr(UpdCode.Net, "request", UpdPhase.Down,
                null, code, true);
        }
        long need = size - from;
        if (!string.IsNullOrEmpty(contentLength) &&
            (!long.TryParse(contentLength, NumberStyles.None,
                CultureInfo.InvariantCulture, out long length) || length != need))
        {
            return range("content_length", code);
        }
        if (from == 0)
        {
            return null;
        }
        string expected = "bytes " + from.ToString(CultureInfo.InvariantCulture) + "-" +
            (size - 1).ToString(CultureInfo.InvariantCulture) + "/" +
            size.ToString(CultureInfo.InvariantCulture);
        return string.Equals(contentRange, expected, StringComparison.OrdinalIgnoreCase)
            ? null
            : range("content_range", code);
    }

    private static UpdErr range(string detail, long code)
    {
        return new UpdErr(UpdCode.Range, detail, UpdPhase.Down,
            null, code, true);
    }
}

internal sealed class FileSink : DownloadHandlerScript
{
    private const int bufSize = 256 * 1024;
    private readonly FileStream mFile;
    private readonly long mFrom;
    private readonly long mSize;
    private readonly Action<long> mProg;
    private UnityWebRequest mReq;
    private bool mHead;
    private bool mClosed;

    public UpdErr err { get; private set; }
    public long got { get; private set; }
    public bool isFull { get; private set; }

    public FileSink(string path, long from, long size, Action<long> prog)
        : base(new byte[bufSize])
    {
        mFrom = from;
        mSize = size;
        mProg = prog;
        FileMode mode = from == 0 ? FileMode.Create : FileMode.Open;
        mFile = new FileStream(path, mode, FileAccess.Write, FileShare.Read, bufSize,
            FileOptions.SequentialScan);
        if (mFile.Length != from)
        {
            throw new InvalidDataException("part_length");
        }
        mFile.Position = from;
        got = from;
    }

    public void bind(UnityWebRequest req)
    {
        mReq = req;
    }

    public void close()
    {
        if (!mClosed)
        {
            mClosed = true;
            mFile.Dispose();
        }
    }

    protected override bool ReceiveData(byte[] data, int length)
    {
        try
        {
            if (!checkHead() || data == null || length < 0 || length > data.Length ||
                got + length > mSize)
            {
                if (err == null)
                {
                    err = rangeErr("file_length");
                }
                return false;
            }
            mFile.Write(data, 0, length);
            got += length;
            mProg(got);
            return true;
        }
        catch (UpdBad bad)
        {
            err = bad.err;
            return false;
        }
        catch (Exception ex)
        {
            err = new UpdErr(UpdCode.Disk, "file_write", UpdPhase.Down, ex);
            return false;
        }
    }

    protected override void CompleteContent()
    {
        try
        {
            if (!checkHead() || got != mSize)
            {
                if (err == null)
                {
                    err = rangeErr("file_length");
                }
                return;
            }
            isFull = true;
        }
        catch (UpdBad bad)
        {
            err = bad.err;
        }
        catch (Exception ex)
        {
            err = new UpdErr(UpdCode.Disk, "file_write", UpdPhase.Down, ex);
        }
    }

    private bool checkHead()
    {
        if (mHead)
        {
            return err == null;
        }
        mHead = true;
        if (mReq == null)
        {
            err = rangeErr("response");
            return false;
        }
        err = UpdRange.check(mFrom, mSize, mReq.responseCode,
            mReq.GetResponseHeader("Content-Length"),
            mReq.GetResponseHeader("Content-Range"));
        return err == null;
    }

    private UpdErr rangeErr(string detail)
    {
        long code = mReq == null ? 0 : mReq.responseCode;
        return new UpdErr(UpdCode.Range, detail, UpdPhase.Down, null, code, true);
    }
}
