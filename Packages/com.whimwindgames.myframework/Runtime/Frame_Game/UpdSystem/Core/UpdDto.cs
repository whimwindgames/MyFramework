using System;

public enum UpdPhase
{
    Idle,
    Latest,
    Manifest,
    Plan,
    Copy,
    Ask,
    Down,
    Install,
    Ready,
    Load,
}


public enum UpdCode
{
    Config,
    Net,
    Http,
    Range,
    Sign,
    Hash,
    Schema,
    Compat,
    Disk,
    State,
    Busy,
    Cancel,
    Load,
}

public static class UpdLim
{
    public const int Schema = 11;
    public const int LatestMax = 32 * 1024;
    public const int ManMax = 4 * 1024 * 1024;
    public const int StateMax = 16 * 1024;
    public const int FileMax = 4096;
    public const int CodeMax = 256;
    public const int AotMax = 256;
    public const int PathMax = 256;
    public const int IdMax = 96;
    public const int Retry = 3;
    public const int Timeout = 30;
    public const long FileSize = 512L * 1024 * 1024;
    public const long TotalSize = 1024L * 1024 * 1024;
}

[Serializable]
public sealed class UpdCfg
{
    public string baseUrl;
    public string env;
    public string platform;
    public string baseId;
    public string pubKey;
    // 随Base冻结。false保持旧版Release文件布局；true使用跨Base共享的SHA-256内容仓库。
    public bool contentAddressed;
    public int retry = UpdLim.Retry;
    public int timeout = UpdLim.Timeout;
    public string[] aotDlls = Array.Empty<string>();
    public string[] codeDlls;
    public string entryDll;
    public string hotId;
    public string secret;
    public string resList;
}

[Serializable]
public sealed class UpdBox
{
    public int schema;
    public string alg;
    public string data;
    public string sig;
}

[Serializable]
public sealed class UpdLatest
{
    public int schema;
    public string env;
    public string platform;
    public string baseId;
    public long seq;
    public string releaseId;
    public string manifestSha;
    public long manifestSize;
}

[Serializable]
public sealed class UpdMan
{
    public int schema;
    public string env;
    public string releaseId;
    public string platform;
    public string baseId;
    public string[] aotDlls;
    public string[] codeDlls;
    public string entryDll;
    public string hotId;
    public string secret;
    public UpdFile[] files;
}

[Serializable]
public sealed class UpdFile
{
    public string path;
    public string sha256;
    public long size;
}

[Serializable]
public sealed class UpdState
{
    public int schema;
    public string env;
    public string platform;
    public string baseId;
    public long seq;
    public string releaseId;
    public string latestSha;
}

[Serializable]
public sealed class UpdActive
{
    public int schema;
    public long seq;
    public string releaseId;
    public string manifestSha;
    public long manifestSize;
    public string latestSha;
}

[Serializable]
public sealed class UpdCandidate
{
    public int schema;
    public UpdActive target;
    public UpdActive previous;
    public int attempts;
}

public sealed class UpdProg
{
    public UpdPhase phase;
    public string path;
    public int done;
    public int total;
    public long downGot;
    public long downSize;
    public long bps;
    public int etaSec = -1;
    public float rate;
}
