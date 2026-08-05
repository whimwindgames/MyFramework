using System;
using System.IO;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

internal sealed class UpdBuiltin
{
    private readonly UpdStore mStore;
    private readonly string mRoot;
    private readonly int mTimeout;

    public UpdBuiltin(UpdStore store, int timeout, string platform, string root = null)
    {
        mStore = store ?? throw new ArgumentNullException(nameof(store));
        mTimeout = timeout;
        mRoot = makeRoot(root ?? Application.streamingAssetsPath, platform);
    }

    public async UniTask<UpdRet<bool>> copy(UpdFile file, string releaseId,
        CancellationToken ct, Action<long> onProg = null)
    {
        string dst = mStore.copyPath(releaseId, file.path);
        try
        {
            UnityWebRequest.Result result;
            long code;
            using (UnityWebRequest req = new UnityWebRequest(uri(file.path),
                UnityWebRequest.kHttpVerbGET))
            {
                req.downloadHandler = new DownloadHandlerFile(dst)
                {
                    removeFileOnAbort = true,
                };
                req.redirectLimit = 0;
                req.timeout = mTimeout;
                try
                {
                    IProgress<float> prog = onProg == null ? null :
                        Progress.Create<float>(value => onProg((long)Math.Min(
                            file.size, Math.Ceiling(file.size * value))));
                    await req.SendWebRequest().ToUniTask(prog,
                        cancellationToken: ct, cancelImmediately: true);
                }
                catch (UnityWebRequestException ex)
                {
                    if (ct.IsCancellationRequested)
                    {
                        return UpdRet<bool>.fail(new UpdErr(UpdCode.Cancel, "builtin",
                            UpdPhase.Copy, ex));
                    }
                }
                if (ct.IsCancellationRequested)
                {
                    return UpdRet<bool>.fail(new UpdErr(UpdCode.Cancel, "builtin",
                        UpdPhase.Copy));
                }
                result = req.result;
                code = req.responseCode;
            }
            if (result == UnityWebRequest.Result.DataProcessingError)
            {
                return UpdRet<bool>.fail(new UpdErr(UpdCode.Disk, "builtin_write",
                    UpdPhase.Copy, null, code));
            }
            if (result != UnityWebRequest.Result.Success || code != 0 && code != 200)
            {
                mStore.dropCopy(dst);
                return UpdRet<bool>.pass(false);
            }
            try
            {
                await UniTask.RunOnThreadPool(() =>
                    mStore.stage(file, releaseId, dst, UpdPhase.Copy, ct),
                    cancellationToken: ct);
                return UpdRet<bool>.pass(true);
            }
            catch (UpdBad bad) when (bad.err != null && bad.err.code == UpdCode.Hash)
            {
                return UpdRet<bool>.pass(false);
            }
        }
        catch (OperationCanceledException ex)
        {
            return UpdRet<bool>.fail(new UpdErr(UpdCode.Cancel, "builtin", UpdPhase.Copy, ex));
        }
        catch (UpdBad bad)
        {
            return UpdRet<bool>.fail(bad.err);
        }
        catch (Exception ex)
        {
            return UpdRet<bool>.fail(new UpdErr(UpdCode.Disk, "builtin", UpdPhase.Copy, ex));
        }
        finally
        {
            mStore.dropCopy(dst);
        }
    }

    private string uri(string path)
    {
        string[] parts = path.Split('/');
        for (int i = 0; i < parts.Length; ++i)
        {
            parts[i] = Uri.EscapeDataString(parts[i]);
        }
        return mRoot + "/" + string.Join("/", parts);
    }

    private static string makeRoot(string root, string platform)
    {
        if (string.IsNullOrWhiteSpace(root) || !UpdFmt.isId(platform))
        {
            throw new ArgumentException("builtin_root");
        }
        string value = root.TrimEnd('/', '\\');
        string folder = "/" + Uri.EscapeDataString(platform);
        if (value.StartsWith("jar:file:", StringComparison.OrdinalIgnoreCase))
        {
            return value + folder;
        }
        if (value.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            Uri uri = new Uri(value, UriKind.Absolute);
            if (!uri.IsFile)
            {
                throw new ArgumentException("builtin_root");
            }
            return uri.AbsoluteUri.TrimEnd('/') + folder;
        }
        if (value.IndexOf("://", StringComparison.Ordinal) >= 0)
        {
            throw new ArgumentException("builtin_root");
        }
        string path = Path.GetFullPath(value + Path.DirectorySeparatorChar + platform);
        return new Uri(path + Path.DirectorySeparatorChar).AbsoluteUri.TrimEnd('/');
    }
}
