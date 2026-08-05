using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

public static class UpdRule
{
    public static void cfg(UpdCfg cfg)
    {
        baseCfg(cfg);
        Uri uri;
        if (!Uri.TryCreate(cfg.baseUrl, UriKind.Absolute, out uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            cfg.retry < 0 || cfg.retry > UpdLim.Retry ||
            cfg.timeout < 1 || cfg.timeout > 300)
        {
            UpdFail.bad(UpdCode.Config, "config");
        }
    }

    // 资源生产与本地Release校验不依赖部署URL，只校验双方共享的协议身份与文件契约。
    public static void prod(UpdCfg cfg)
    {
        baseCfg(cfg);
        if (cfg.aotDlls == null || cfg.aotDlls.Length > UpdLim.AotMax ||
            cfg.codeDlls == null || cfg.codeDlls.Length < 2 ||
            cfg.codeDlls.Length > UpdLim.CodeMax || !UpdFmt.isPath(cfg.entryDll) ||
            !UpdFmt.isSha(cfg.hotId) || !UpdFmt.isPath(cfg.resList) ||
            (!string.IsNullOrEmpty(cfg.secret) &&
            (!UpdFmt.isPath(cfg.secret) || samePath(cfg.secret, cfg.resList))))
        {
            UpdFail.bad(UpdCode.Config, "config");
        }
        HashSet<string> dlls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        HashSet<string> code = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < cfg.aotDlls.Length; ++i)
        {
            string dll = cfg.aotDlls[i];
            if (!isDll(dll) || !dlls.Add(dll) || samePath(dll, cfg.secret) ||
                samePath(dll, cfg.resList) || !names.Add(dllName(dll)))
            {
                UpdFail.bad(UpdCode.Config, "config");
            }
        }
        for (int i = 0; i < cfg.codeDlls.Length; ++i)
        {
            string dll = cfg.codeDlls[i];
            if (!isDll(dll) ||
                !dlls.Add(dll) || !code.Add(dll) || samePath(dll, cfg.secret) ||
                samePath(dll, cfg.resList) || !names.Add(dllName(dll)))
            {
                UpdFail.bad(UpdCode.Config, "config");
            }
        }
        if (!code.Contains(cfg.entryDll) ||
            !code.Contains(UpdContract.FrameHotDll) ||
            !code.Contains(UpdContract.EntryDll) ||
            !samePath(cfg.entryDll, UpdContract.EntryDll) ||
            cfg.hotId != hotId(cfg.codeDlls, cfg.entryDll))
        {
            UpdFail.bad(UpdCode.Config, "config");
        }
    }

    public static string hotText(string[] dlls, string entry)
    {
        StringBuilder text = new StringBuilder();
        text.Append("schema=1\nentry=").Append(entry ?? string.Empty).Append('\n');
        if (dlls != null)
        {
            for (int i = 0; i < dlls.Length; ++i)
            {
                text.Append("dll=").Append(dlls[i] ?? string.Empty).Append('\n');
            }
        }
        return text.ToString();
    }

    public static string hotId(string[] dlls, string entry)
    {
        return UpdHash.data(Encoding.UTF8.GetBytes(hotText(dlls, entry)));
    }

    private static bool samePath(string left, string right)
    {
        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private static void baseCfg(UpdCfg cfg)
    {
        if (cfg == null || !isEnv(cfg.env) || !UpdFmt.isId(cfg.platform) ||
            !UpdFmt.isId(cfg.baseId) || string.IsNullOrEmpty(cfg.pubKey) ||
            !UpdFmt.isPath(cfg.resList))
        {
            UpdFail.bad(UpdCode.Config, "config");
        }
    }

    private static bool isDll(string value)
    {
        return UpdFmt.isPath(value) &&
            value.EndsWith(".dll.bytes", StringComparison.OrdinalIgnoreCase);
    }

    private static string dllName(string path)
    {
        string name = Path.GetFileName(path);
        return name.Substring(0, name.Length - ".dll.bytes".Length);
    }

    private static bool isEnv(string value)
    {
        return value == "test" || value == "prod";
    }

    public static void box(UpdBox box)
    {
        if (box == null || box.schema != UpdLim.Schema || box.alg != "ES256" ||
            string.IsNullOrEmpty(box.data) || string.IsNullOrEmpty(box.sig))
        {
            UpdFail.bad(UpdCode.Sign, "latest_box", UpdPhase.Latest);
        }
    }

    public static void latest(UpdCfg cfg, UpdLatest value)
    {
        if (value == null || value.schema != UpdLim.Schema || value.seq < 0 ||
            value.env != cfg.env || value.platform != cfg.platform || value.baseId != cfg.baseId ||
            !UpdFmt.isId(value.releaseId) || !UpdFmt.isSha(value.manifestSha) ||
            value.manifestSize < 1 || value.manifestSize > UpdLim.ManMax)
        {
            UpdFail.bad(UpdCode.Compat, "latest", UpdPhase.Latest);
        }
    }

    public static void state(UpdCfg cfg, UpdLatest value, string sha, UpdState state)
    {
        if (state == null)
        {
            return;
        }
        if (state.schema != UpdLim.Schema || state.env != cfg.env ||
            state.platform != cfg.platform || state.baseId != cfg.baseId || state.seq < 0 ||
            !UpdFmt.isId(state.releaseId) || !UpdFmt.isSha(state.latestSha))
        {
            UpdFail.bad(UpdCode.State, "state", UpdPhase.Latest);
        }
        if (value.seq < state.seq || (value.seq == state.seq &&
            (sha != state.latestSha || value.releaseId != state.releaseId)))
        {
            UpdFail.bad(UpdCode.State, "latest_seq", UpdPhase.Latest);
        }
    }

    public static UpdCfg man(UpdCfg cfg, UpdLatest latest, UpdMan man)
    {
        if (man == null || man.schema != UpdLim.Schema || man.env != cfg.env ||
            man.releaseId != latest.releaseId ||
            man.platform != cfg.platform || man.baseId != cfg.baseId ||
            man.files == null || man.files.Length == 0 || man.files.Length > UpdLim.FileMax)
        {
            UpdFail.bad(UpdCode.Compat, "manifest", UpdPhase.Manifest);
        }
        HashSet<string> paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        HashSet<string> exact = new HashSet<string>(StringComparer.Ordinal);
        long total = 0;
        for (int i = 0; i < man.files.Length; ++i)
        {
            UpdFile file = man.files[i];
            if (file == null || !UpdFmt.isPath(file.path) || !paths.Add(file.path) ||
                !UpdFmt.isSha(file.sha256) || file.size <= 0 || file.size > UpdLim.FileSize)
            {
                UpdFail.bad(UpdCode.Schema, "manifest_file", UpdPhase.Manifest);
            }
            try
            {
                total = checked(total + file.size);
            }
            catch (OverflowException ex)
            {
                UpdFail.bad(UpdCode.Schema, "manifest_size", UpdPhase.Manifest, ex);
            }
            if (total > UpdLim.TotalSize)
            {
                UpdFail.bad(UpdCode.Schema, "manifest_size", UpdPhase.Manifest);
            }
            exact.Add(file.path);
        }
        UpdCfg rel = relCfg(cfg, man);
        for (int i = 0; i < rel.aotDlls.Length; ++i)
        {
            need(exact, rel.aotDlls[i]);
        }
        for (int i = 0; i < rel.codeDlls.Length; ++i)
        {
            need(exact, rel.codeDlls[i]);
        }
        if (!string.IsNullOrEmpty(rel.secret))
        {
            need(exact, rel.secret);
        }
        need(exact, rel.resList);
        return rel;
    }

    public static UpdCfg relCfg(UpdCfg cfg, UpdMan man)
    {
        if (cfg == null || man == null)
        {
            UpdFail.bad(UpdCode.Config, "config");
        }
        UpdCfg value = new UpdCfg
        {
            baseUrl = cfg.baseUrl,
            env = cfg.env,
            platform = cfg.platform,
            baseId = cfg.baseId,
            pubKey = cfg.pubKey,
            retry = cfg.retry,
            timeout = cfg.timeout,
            aotDlls = man.aotDlls == null ? null : (string[])man.aotDlls.Clone(),
            codeDlls = man.codeDlls == null ? null : (string[])man.codeDlls.Clone(),
            entryDll = man.entryDll,
            hotId = man.hotId,
            secret = man.secret ?? string.Empty,
            resList = cfg.resList,
        };
        prod(value);
        return value;
    }

    private static void need(HashSet<string> files, string path)
    {
        if (!files.Contains(path))
        {
            UpdFail.bad(UpdCode.Schema, "required_file", UpdPhase.Manifest);
        }
    }
}
