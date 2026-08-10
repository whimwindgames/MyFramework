using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

// SSH发布连接配置。纯数据对象，不含持久化；窗口用SshCfgStore读写EditorPrefs，
// 无头调用方直接构造并传入参数。私钥只允许引用项目外的本机文件路径。
public sealed class SshCfg
{
	const int KEY_MAX = 64 * 1024;
	static readonly Regex sHost = new(
		@"^[a-z0-9](?:[a-z0-9.-]{0,251}[a-z0-9])?$",
		RegexOptions.CultureInvariant);
	static readonly Regex sUser = new(
		@"^[a-z_][a-z0-9_-]{0,31}$",
		RegexOptions.CultureInvariant);

	public string host;
	public int port = 22;
	public string user = "hotdeploy";
	public string key;
	public string url;

	public bool isReady
	{
		get
		{
			try
			{
				prep();
				return true;
			}
			catch
			{
				return false;
			}
		}
	}

	public string clientBase
	{
		get
		{
			try
			{
				return validUrl();
			}
			catch
			{
				return string.Empty;
			}
		}
	}

	public string check()
	{
		prep();
		using SshStore store = new(this);
		checkWeb(clientBase);
		return "SSH与HTTPS只读检查成功";
	}

	internal void prep()
	{
		host = (host ?? string.Empty).Trim().ToLowerInvariant();
		user = (user ?? string.Empty).Trim();
		key = string.IsNullOrWhiteSpace(key) ? string.Empty :
			Path.GetFullPath(key.Trim());
		url = (url ?? string.Empty).Trim().TrimEnd('/') + "/";
		validInfo();
		validKey();
		_ = validUrl();
	}

	void validInfo()
	{
		bool ipv4 = IPAddress.TryParse(host, out IPAddress address) &&
			address.AddressFamily == AddressFamily.InterNetwork;
		if ((!ipv4 && (!sHost.IsMatch(host) || host.Contains(".."))) ||
			port < 1 || port > 65535 || !sUser.IsMatch(user))
		{
			throw new InvalidDataException("SSH服务器、端口或用户名格式错误");
		}
	}

	string validUrl()
	{
		string value = (url ?? string.Empty).Trim().TrimEnd('/') + "/";
		if (!Uri.TryCreate(value, UriKind.Absolute, out Uri uri) ||
			uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo) ||
			uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.Query) ||
			!string.IsNullOrEmpty(uri.Fragment))
		{
			throw new InvalidDataException("客户端资源地址必须是HTTPS根地址");
		}
		return uri.AbsoluteUri;
	}

	void validKey()
	{
		string path = key ?? string.Empty;
		if (!Path.IsPathRooted(path) || !File.Exists(path))
		{
			throw new FileNotFoundException("SSH私钥不存在");
		}
		string proj = Path.GetFullPath(Path.Combine(Application.dataPath, ".."))
			.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
			Path.DirectorySeparatorChar;
		string full = Path.GetFullPath(path) + Path.DirectorySeparatorChar;
		if (full.StartsWith(proj, StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidDataException("SSH私钥不能位于项目或Git工作区内");
		}
		FileInfo info = new(path);
		if (info.Length < 100 || info.Length > KEY_MAX || hasLink(path))
		{
			throw new InvalidDataException("SSH私钥文件不安全或大小异常");
		}
		using StreamReader input = new(path, new UTF8Encoding(false, true), false, 256);
		if (input.ReadLine() != "-----BEGIN OPENSSH PRIVATE KEY-----")
		{
			throw new InvalidDataException("只支持OpenSSH私钥");
		}
	}

	static void checkWeb(string baseUrl)
	{
		Uri root = new(baseUrl, UriKind.Absolute);
		Uri uri = new(root, ".hot-health?v=" + Guid.NewGuid().ToString("N"));
		try
		{
			HttpWebRequest req = WebRequest.CreateHttp(uri);
			req.Method = "GET";
			req.AllowAutoRedirect = false;
			req.Timeout = 10000;
			req.ReadWriteTimeout = 10000;
			req.CachePolicy = new System.Net.Cache.RequestCachePolicy(
				System.Net.Cache.RequestCacheLevel.NoCacheNoStore);
			using HttpWebResponse res = (HttpWebResponse)req.GetResponse();
			if (res.StatusCode != HttpStatusCode.OK || res.ResponseUri.Scheme != root.Scheme ||
				res.ResponseUri.Host != root.Host || res.ResponseUri.Port != root.Port ||
				res.ContentLength > 32)
			{
				throw new InvalidDataException("HTTPS只读响应错误");
			}
			using StreamReader input = new(res.GetResponseStream(), Encoding.ASCII, false, 64);
			if (input.ReadToEnd().Trim() != "ok")
			{
				throw new InvalidDataException("HTTPS健康检查内容错误");
			}
		}
		catch (Exception ex) when (ex is not InvalidDataException)
		{
			throw new IOException("HTTPS只读检查失败", ex);
		}
	}

	static bool hasLink(string path)
	{
		string cur = Path.GetFullPath(path);
		while (!string.IsNullOrEmpty(cur))
		{
			if ((File.GetAttributes(cur) & FileAttributes.ReparsePoint) != 0)
			{
				return true;
			}
			string next = Path.GetDirectoryName(cur);
			if (string.IsNullOrEmpty(next) || next == cur)
			{
				break;
			}
			cur = next;
		}
		return false;
	}
}

// 窗口侧的EditorPrefs持久化。键按项目路径哈希隔离，避免多项目串配置。
public static class SshCfgStore
{
	const string PRE = "MyFramework.HotUpd.Pub.";

	public static SshCfg load()
	{
		return new SshCfg
		{
			host = EditorPrefs.GetString(pref("host")),
			port = EditorPrefs.GetInt(pref("port"), 22),
			user = EditorPrefs.GetString(pref("user"), "hotdeploy"),
			key = EditorPrefs.GetString(pref("key")),
			url = EditorPrefs.GetString(pref("url")),
		};
	}

	public static void save(SshCfg cfg)
	{
		if (cfg == null) throw new ArgumentNullException(nameof(cfg));
		cfg.prep();
		string nextBase = cfg.clientBase;
		EditorPrefs.SetString(pref("host"), cfg.host);
		EditorPrefs.SetInt(pref("port"), cfg.port);
		EditorPrefs.SetString(pref("user"), cfg.user);
		EditorPrefs.SetString(pref("key"), cfg.key);
		EditorPrefs.SetString(pref("url"), cfg.url);
		SshCfg saved = load();
		if (saved.host != cfg.host || saved.port != cfg.port || saved.user != cfg.user ||
			saved.key != cfg.key || saved.clientBase != nextBase)
		{
			throw new IOException("SSH发布设置保存后回读不一致");
		}
	}

	static string pref(string name)
	{
		string root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."))
			.Replace('\\', '/').TrimEnd('/');
		using SHA256 hash = SHA256.Create();
		byte[] raw = hash.ComputeHash(Encoding.UTF8.GetBytes(root));
		string id = Convert.ToBase64String(raw, 0, 12)
			.Replace('+', '-').Replace('/', '_');
		return PRE + id + "." + name;
	}
}
