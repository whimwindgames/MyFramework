using System;
using System.Collections;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEngine.Networking;
using UnityEngine.TestTools;

public sealed class UpdDownloadTests
{
    private string mPart;

    [SetUp] public void SetUp() => mPart = Path.Combine(Path.GetTempPath(),
        "upd-download-" + Guid.NewGuid().ToString("N") + ".part");
    [TearDown] public void TearDown() { if (File.Exists(mPart)) File.Delete(mPart); }

    [Test]
    public void ContinuousProgressHasNoTotalDurationLimit()
    {
        var watch = new UpdDownloadWatch(100, 30000);
        for (long second = 1; second <= 86400; ++second)
            Assert.That(watch.stalled(100 + second, second * 1000), Is.False);
        Assert.That(watch.stalled(86500, 86429999), Is.False);
        Assert.That(watch.stalled(86500, 86430000), Is.True);
    }

    [Test]
    public void ResumeOffsetAndEmptyCallbacksDoNotCountAsNewProgress()
    {
        var watch = new UpdDownloadWatch(1024 * 1024, 30000);
        Assert.That(watch.stalled(1024 * 1024, 29999), Is.False);
        Assert.That(watch.stalled(1024 * 1024, 30000), Is.True);
    }

    [Test]
    public void NetworkRetriesAreUnlimitedWithCappedBackoff()
    {
        var retry = new UpdDownloadRetry(0);
        var network = new UpdErr(UpdCode.Net, "no_progress_timeout", canRetry: true);
        for (int i = 0; i < 10000; ++i)
        {
            Assert.That(retry.again(network, false), Is.True);
            Assert.That(retry.delayMs, Is.InRange(250, 8000));
        }
        Assert.That(retry.again(network, true), Is.True);
        Assert.That(retry.delayMs, Is.EqualTo(250));
        Assert.That(retry.again(new UpdErr(UpdCode.Http, "status", http: 503,
            canRetry: true), false), Is.True);
    }

    [Test]
    public void FatalErrorsStopAndCorruptionBudgetCannotBeResetByNetworkProgress()
    {
        var retry = new UpdDownloadRetry(3);
        var corrupt = new UpdErr(UpdCode.Hash, "file", canRetry: true);
        for (int i = 0; i < 3; ++i)
        {
            Assert.That(retry.again(corrupt, true), Is.True);
            Assert.That(retry.again(new UpdErr(UpdCode.Net, "request", canRetry: true),
                true), Is.True);
        }
        Assert.That(retry.again(corrupt, true), Is.False);
        Assert.That(retry.again(new UpdErr(UpdCode.Http, "status", http: 404), false), Is.False);
        Assert.That(retry.again(new UpdErr(UpdCode.Disk, "file_write"), false), Is.False);
        Assert.That(retry.again(new UpdErr(UpdCode.Cancel, "request"), false), Is.False);
        var range = new UpdDownloadRetry(1);
        Assert.That(range.again(new UpdErr(UpdCode.Range, "range_reset", canRetry: true), true), Is.True);
        Assert.That(range.again(new UpdErr(UpdCode.Range, "range_reset", canRetry: true), true), Is.False);
    }

    [UnityTest]
    public IEnumerator FatalPeerStopsInfiniteRetryAndPreservesOriginalError() => FatalPeer().ToCoroutine();

    private async UniTask FatalPeer()
    {
        bool peerCancelled = false;
        var missing = new UpdErr(UpdCode.Http, "status", http: 404);
        async UniTask<UpdRet<bool>> Download(int index, CancellationToken token)
        {
            if (index == 0)
            {
                await UniTask.Delay(100, DelayType.Realtime);
                return UpdRet<bool>.fail(missing);
            }
            while (!token.IsCancellationRequested) await UniTask.Yield();
            peerCancelled = true;
            return UpdRet<bool>.fail(new UpdErr(UpdCode.Cancel, "request"));
        }
        UpdRet<bool> result = await UpdCore.runDownloads(2, Download, CancellationToken.None);
        Assert.That(result.ok, Is.False);
        Assert.That(result.err, Is.SameAs(missing));
        Assert.That(peerCancelled, Is.True);
    }

    [UnityTest]
    public IEnumerator SlowRealRequestOutlivesConfiguredTimeout() => SlowRequest().ToCoroutine();

    private async UniTask SlowRequest()
    {
        using var server = new LocalServer(async (stream, stop) =>
        {
            await Headers(stream, 200, 12 * 1024);
            for (int i = 0; i < 12; ++i)
            {
                await stream.WriteAsync(new byte[1024], 0, 1024, stop);
                await Task.Delay(200, stop);
            }
        });
        using var sink = new FileSink(mPart, 0, 12 * 1024, _ => { });
        using var req = Request(server, sink);
        var http = new UpdHttp("https://example.invalid/", "test", 1);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        UpdErr err = await http.sendDownload(req, sink, CancellationToken.None);
        sink.close();
        Assert.That(err, Is.Null);
        Assert.That(req.timeout, Is.Zero);
        Assert.That(clock.Elapsed.TotalSeconds, Is.GreaterThan(1));
        Assert.That(req.result, Is.EqualTo(UnityWebRequest.Result.Success));
        Assert.That(sink.isFull, Is.True);
        Assert.That(new FileInfo(mPart).Length, Is.EqualTo(12 * 1024));
    }

    [UnityTest]
    public IEnumerator StalledRealRequestKeepsPartForResume() => StalledRequest().ToCoroutine();

    private async UniTask StalledRequest()
    {
        using var server = new LocalServer(async (stream, stop) =>
        {
            await Headers(stream, 200, 4096);
            await stream.WriteAsync(new byte[1024], 0, 1024, stop);
            await Task.Delay(Timeout.Infinite, stop);
        });
        using var sink = new FileSink(mPart, 0, 4096, _ => { });
        using var req = Request(server, sink);
        var http = new UpdHttp("https://example.invalid/", "test", 1);
        UpdErr err = await http.sendDownload(req, sink, CancellationToken.None);
        sink.close();
        Assert.That(err?.code, Is.EqualTo(UpdCode.Net));
        Assert.That(err?.detail, Is.EqualTo("no_progress_timeout"));
        Assert.That(err?.canRetry, Is.True);
        Assert.That(new FileInfo(mPart).Length, Is.EqualTo(1024));
    }

    [UnityTest]
    public IEnumerator CancellationStopsDuringStall() => CancelRequest().ToCoroutine();

    private async UniTask CancelRequest()
    {
        using var cancel = new CancellationTokenSource();
        using var server = new LocalServer(async (stream, stop) =>
        {
            await Headers(stream, 200, 4096);
            await stream.WriteAsync(new byte[1024], 0, 1024, stop);
            await Task.Delay(Timeout.Infinite, stop);
        });
        using var sink = new FileSink(mPart, 0, 4096, _ => cancel.Cancel());
        using var req = Request(server, sink);
        var http = new UpdHttp("https://example.invalid/", "test", 30);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        UpdErr err = await http.sendDownload(req, sink, cancel.Token);
        sink.close();
        Assert.That(err?.code, Is.EqualTo(UpdCode.Cancel));
        Assert.That(clock.Elapsed.TotalSeconds, Is.LessThan(5));
    }

    [UnityTest]
    public IEnumerator DroppedRequestResumesFromAcceptedOffset() => ResumeRequest().ToCoroutine();

    private async UniTask ResumeRequest()
    {
        var http = new UpdHttp("https://example.invalid/", "test", 1);
        using (var first = new LocalServer(async (stream, stop) =>
        {
            await Headers(stream, 200, 4096);
            await stream.WriteAsync(new byte[1024], 0, 1024, stop);
        }))
        using (var sink = new FileSink(mPart, 0, 4096, _ => { }))
        using (var req = Request(first, sink))
        {
            UpdErr interrupted = await http.sendDownload(req, sink, CancellationToken.None);
            sink.close();
            Assert.That(req.result, Is.Not.EqualTo(UnityWebRequest.Result.Success));
            Assert.That(interrupted?.code, Is.EqualTo(UpdCode.Net));
            Assert.That(interrupted?.canRetry, Is.True);
        }
        long offset = new FileInfo(mPart).Length;
        Assert.That(offset, Is.EqualTo(1024));
        using var second = new LocalServer(async (stream, stop) =>
        {
            await Headers(stream, 206, 3072, "Content-Range: bytes 1024-4095/4096\r\n");
            await stream.WriteAsync(new byte[3072], 0, 3072, stop);
        });
        using var resumed = new FileSink(mPart, offset, 4096, _ => { });
        using var resumedReq = Request(second, resumed);
        resumedReq.SetRequestHeader("Range", "bytes=" + offset + "-");
        Assert.That(await http.sendDownload(resumedReq, resumed, CancellationToken.None), Is.Null);
        resumed.close();
        Assert.That(second.request, Does.Contain("Range: bytes=1024-"));
        Assert.That(resumed.isFull, Is.True);
        Assert.That(new FileInfo(mPart).Length, Is.EqualTo(4096));
    }

    private static UnityWebRequest Request(LocalServer server, FileSink sink)
    {
        var req = new UnityWebRequest(server.url, "GET", sink, null)
        { disposeDownloadHandlerOnDispose = false, timeout = 1 };
        sink.bind(req);
        return req;
    }

    private static async Task Headers(NetworkStream stream, int code, int length, string extra = "")
    {
        byte[] bytes = Encoding.ASCII.GetBytes($"HTTP/1.1 {code} OK\r\nContent-Length: {length}\r\n" +
            extra + "Connection: close\r\n\r\n");
        await stream.WriteAsync(bytes, 0, bytes.Length);
    }

    private sealed class LocalServer : IDisposable
    {
        private readonly TcpListener mListener;
        private readonly CancellationTokenSource mStop = new();
        private readonly Task mRun;
        public string request;
        public string url { get; }

        public LocalServer(Func<NetworkStream, CancellationToken, Task> serve)
        {
            mListener = new TcpListener(IPAddress.Loopback, 0);
            mListener.Start();
            url = "http://127.0.0.1:" + ((IPEndPoint)mListener.LocalEndpoint).Port + "/resource";
            mRun = Task.Run(async () =>
            {
                try
                {
                    using TcpClient client = await mListener.AcceptTcpClientAsync();
                    using NetworkStream stream = client.GetStream();
                    var header = new StringBuilder();
                    var buffer = new byte[1];
                    while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                    {
                        if (await stream.ReadAsync(buffer, 0, 1, mStop.Token) == 0) return;
                        header.Append((char)buffer[0]);
                    }
                    request = header.ToString();
                    await serve(stream, mStop.Token);
                }
                catch (Exception) when (mStop.IsCancellationRequested) { }
            });
        }

        public void Dispose()
        {
            mStop.Cancel();
            mListener.Stop();
            if (!mRun.Wait(5000)) throw new TimeoutException("Test server did not stop");
            mStop.Dispose();
        }
    }
}
