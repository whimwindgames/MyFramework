using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

internal static class HotStoreProtocol
{
	internal const int Version = 2;
	internal static string identity => "hot-store/" +
		Version.ToString(CultureInfo.InvariantCulture);
}

internal sealed class SshHostKey
{
	internal string host;
	internal int port;
	internal string line;
	internal string fingerprint;
}

internal sealed class SshStore : IObjStore
{
	const string TOOL = "/usr/local/bin/hot-store";
	const int IO_IDLE_TIMEOUT = 60 * 1000;
	const int EXIT_TIMEOUT = 15 * 1000;
	const int COPY_BUFFER = 1024 * 1024;
	readonly string mHost;
	readonly int mPort;
	readonly string mUser;
	readonly string mKey;
	readonly string mKnown;
	public string clientBase { get; }
	bool mDone;

	internal SshStore(SshCfg cfg)
	{
		if (cfg == null)
		{
			throw new ArgumentNullException(nameof(cfg));
		}
		cfg.prep();
		mHost = cfg.host;
		mPort = cfg.port;
		mUser = cfg.user;
		mKey = cfg.key;
		clientBase = cfg.clientBase;
		mKnown = knownPath();
		Directory.CreateDirectory(Path.GetDirectoryName(mKnown));
		check();
	}

	internal static string knownFingerprint(SshCfg cfg)
	{
		if (cfg == null || !File.Exists(knownPath()))
		{
			return string.Empty;
		}
		cfg.prep();
		string expected = hostField(cfg.host, cfg.port);
		foreach (string line in File.ReadAllLines(knownPath(), new UTF8Encoding(false, true)))
		{
			SshHostKey key = parseHostKey(line, cfg.host, cfg.port);
			if (key != null && line.Split(' ')[0] == expected)
			{
				return key.fingerprint;
			}
		}
		return string.Empty;
	}

	internal static SshHostKey scanHost(SshCfg cfg)
	{
		if (cfg == null) throw new ArgumentNullException(nameof(cfg));
		cfg.prep();
		ProcessStartInfo start = new()
		{
			FileName = File.Exists("/usr/bin/ssh-keyscan") ?
				"/usr/bin/ssh-keyscan" : "ssh-keyscan",
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			StandardOutputEncoding = new UTF8Encoding(false, true),
			StandardErrorEncoding = new UTF8Encoding(false, true),
		};
		add(start, "-T", "10", "-p", cfg.port.ToString(CultureInfo.InvariantCulture),
			cfg.host);
		using Process proc = new() { StartInfo = start };
		if (!proc.Start()) throw new IOException("ssh-keyscan启动失败");
		string output = proc.StandardOutput.ReadToEnd();
		string error = proc.StandardError.ReadToEnd();
		if (!proc.WaitForExit(15000))
		{
			proc.Kill();
			throw new TimeoutException("读取SSH主机密钥超时");
		}
		if (proc.ExitCode != 0)
		{
			throw new IOException("读取SSH主机密钥失败:" + safe(error));
		}
		SshHostKey fallback = null;
		string[] lines = output.Split(new[] { '\r', '\n' },
			StringSplitOptions.RemoveEmptyEntries);
		for (int i = 0; i < lines.Length; ++i)
		{
			SshHostKey key = parseHostKey(lines[i], cfg.host, cfg.port);
			if (key == null) continue;
			if (key.line.Contains(" ssh-ed25519 ", StringComparison.Ordinal))
			{
				return key;
			}
			fallback ??= key;
		}
		return fallback ?? throw new InvalidDataException("服务器未返回可用SSH主机密钥");
	}

	internal static void trustHost(SshHostKey key)
	{
		if (key == null || parseHostKey(key.line, key.host, key.port) == null)
		{
			throw new InvalidDataException("SSH主机密钥错误");
		}
		string path = knownPath();
		Directory.CreateDirectory(Path.GetDirectoryName(path));
		string temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
		try
		{
			File.WriteAllText(temp, key.line + "\n", new UTF8Encoding(false));
			if (File.Exists(path)) File.Replace(temp, path, null);
			else File.Move(temp, path);
		}
		finally
		{
			if (File.Exists(temp)) File.Delete(temp);
		}
	}

	public void put(string key, string file)
	{
		need();
		key = chkKey(key, false, false);
		file = chkFile(file, true);
		FileInfo info = new(file);
		string sha = hash(file);
		try
		{
			using SshCmd cmd = open("put", enc(key),
				info.Length.ToString(CultureInfo.InvariantCulture), sha);
			string offsetText = cmd.readLine("等待上传断点");
			if (!long.TryParse(offsetText, NumberStyles.None, CultureInfo.InvariantCulture,
				out long offset) || offset < 0 || offset > info.Length)
			{
				cmd.input.Close();
				cmd.finish();
				throw new InvalidDataException("SSH断点位置错误");
			}
			using (FileStream input = new(file, FileMode.Open, FileAccess.Read, FileShare.Read))
			{
				input.Position = offset;
				cmd.copyInput(input);
			}
			cmd.input.Close();
			string result = cmd.readLine("等待上传确认");
			cmd.finish();
			if (result != "OK")
			{
				throw new InvalidDataException("SSH上传确认错误");
			}
		}
		catch (Exception ex)
		{
			throw fail("上传", key, ex);
		}
	}

	public void get(string key, string file)
	{
		need();
		key = chkKey(key, false, false);
		file = chkFile(file, false);
		string parent = Path.GetDirectoryName(file);
		Directory.CreateDirectory(parent);
		string temp = Path.Combine(parent, "." + Path.GetFileName(file) +
			".ssh-" + Guid.NewGuid().ToString("N"));
		try
		{
			using SshCmd cmd = open("get", enc(key));
			cmd.input.Close();
			using (FileStream output = new(temp, FileMode.CreateNew, FileAccess.Write,
				FileShare.None))
			{
				cmd.copyOutput(output);
				output.Flush(true);
			}
			cmd.finish();
			move(temp, file);
		}
		catch (Exception ex)
		{
			drop(temp);
			throw fail("下载", key, ex);
		}
	}

	public string[] list(string prefix)
	{
		need();
		prefix = chkKey(prefix, true, true);
		try
		{
			string raw = run("list", enc(prefix));
			string[] lines = raw.Split(new[] { '\r', '\n' },
				StringSplitOptions.RemoveEmptyEntries);
			List<string> keys = new(lines.Length);
			HashSet<string> unique = new(StringComparer.Ordinal);
			for (int i = 0; i < lines.Length; ++i)
			{
				string key = chkKey(dec(lines[i]), false, true);
				if (!key.StartsWith(prefix, StringComparison.Ordinal) || !unique.Add(key))
				{
					throw new InvalidDataException("SSH对象列表响应错误");
				}
				keys.Add(key);
			}
			return keys.ToArray();
		}
		catch (Exception ex)
		{
			throw fail("列举", prefix, ex);
		}
	}

	public IObjLease take(string key)
	{
		need();
		key = chkKey(key, false, false);
		string id = Guid.NewGuid().ToString("N");
		try
		{
			expectOk(run("take", enc(key), id), "获取发布锁");
			return new SshLease(this, key, id);
		}
		catch (Exception ex)
		{
			throw fail("获取发布锁", key, ex);
		}
	}

	public void Dispose()
	{
		mDone = true;
	}

	internal void check()
	{
		need();
		string value = run("check").Trim();
		if (value != HotStoreProtocol.identity)
		{
			throw new IOException("SSH发布服务版本不匹配");
		}
		_ = list("test/.ssh-check-" + Guid.NewGuid().ToString("N") + "/");
	}

	internal void keep(string key, string id)
	{
		need();
		try
		{
			expectOk(run("keep", enc(key), id), "续租发布锁");
		}
		catch (Exception ex)
		{
			throw fail("续租发布锁", key, ex);
		}
	}

	internal void release(string key, string id)
	{
		if (mDone) return;
		try
		{
			expectOk(run("release", enc(key), id), "释放发布锁");
		}
		catch (Exception ex)
		{
			throw fail("释放发布锁", key, ex);
		}
	}

	string run(string op, params string[] args)
	{
		using SshCmd cmd = open(op, args);
		cmd.input.Close();
		string value = cmd.readAll("等待" + op + "响应");
		cmd.finish();
		return value;
	}

	SshCmd open(string op, params string[] args)
	{
		ProcessStartInfo start = new()
		{
			FileName = sshPath(),
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			StandardInputEncoding = new UTF8Encoding(false),
			StandardOutputEncoding = new UTF8Encoding(false, true),
			StandardErrorEncoding = new UTF8Encoding(false, true),
		};
		add(start, "-i", mKey);
		add(start, "-p", mPort.ToString(CultureInfo.InvariantCulture));
		add(start, "-o", "BatchMode=yes");
		add(start, "-o", "IdentitiesOnly=yes");
		add(start, "-o", "StrictHostKeyChecking=yes");
		add(start, "-o", "UserKnownHostsFile=" + mKnown);
		add(start, "-o", "ConnectTimeout=10");
		add(start, "-o", "ServerAliveInterval=15");
		add(start, "-o", "ServerAliveCountMax=4");
		add(start, mUser + "@" + mHost, TOOL, op);
		for (int i = 0; i < args.Length; ++i)
		{
			add(start, args[i]);
		}
		try
		{
			return new SshCmd(start, op);
		}
		catch (Exception ex)
		{
			throw new IOException("无法启动本机SSH命令", ex);
		}
	}

	static void add(ProcessStartInfo start, params string[] values)
	{
		for (int i = 0; i < values.Length; ++i)
		{
			start.ArgumentList.Add(values[i]);
		}
	}

	static string sshPath()
	{
		if (File.Exists("/usr/bin/ssh"))
		{
			return "/usr/bin/ssh";
		}
		return "ssh";
	}

	static string knownPath()
	{
		string project = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
		return Path.Combine(project, "Library", "MyFramework", "Ssh", "known_hosts");
	}

	static string hostField(string host, int port)
	{
		return port == 22 ? host : "[" + host + "]:" + port;
	}

	static SshHostKey parseHostKey(string line, string host, int port)
	{
		string value = (line ?? string.Empty).Trim();
		if (value.Length == 0 || value[0] == '#') return null;
		string[] parts = value.Split(new[] { ' ', '\t' },
			StringSplitOptions.RemoveEmptyEntries);
		if (parts.Length != 3 || parts[0] != hostField(host, port) ||
			(parts[1] != "ssh-ed25519" && parts[1] != "ecdsa-sha2-nistp256" &&
			parts[1] != "ssh-rsa"))
		{
			return null;
		}
		byte[] raw;
		try
		{
			raw = Convert.FromBase64String(parts[2]);
		}
		catch (FormatException)
		{
			return null;
		}
		if (raw.Length < 32 || raw.Length > 8192) return null;
		using SHA256 sha = SHA256.Create();
		string fingerprint = Convert.ToBase64String(sha.ComputeHash(raw)).TrimEnd('=');
		return new SshHostKey
		{
			host = host,
			port = port,
			line = string.Join(" ", parts),
			fingerprint = "SHA256:" + fingerprint,
		};
	}

	static string enc(string value)
	{
		if (string.IsNullOrEmpty(value))
		{
			return ".";
		}
		return Convert.ToBase64String(Encoding.UTF8.GetBytes(value ?? string.Empty))
			.TrimEnd('=').Replace('+', '-').Replace('/', '_');
	}

	static string dec(string value)
	{
		string raw = (value ?? string.Empty).Replace('-', '+').Replace('_', '/');
		raw += new string('=', (4 - raw.Length % 4) % 4);
		return new UTF8Encoding(false, true).GetString(Convert.FromBase64String(raw));
	}

	static string hash(string file)
	{
		using SHA256 sha = SHA256.Create();
		using FileStream input = new(file, FileMode.Open, FileAccess.Read, FileShare.Read);
		byte[] sum = sha.ComputeHash(input);
		StringBuilder value = new(sum.Length * 2);
		for (int i = 0; i < sum.Length; ++i)
		{
			value.Append(sum[i].ToString("x2", CultureInfo.InvariantCulture));
		}
		return value.ToString();
	}

	static IOException fail(string op, string key, Exception ex)
	{
		return new IOException("SSH" + op + "失败 key=" + safe(key), ex);
	}

	static string safe(string value)
	{
		if (string.IsNullOrEmpty(value))
		{
			return "-";
		}
		StringBuilder text = new();
		for (int i = 0; i < value.Length && text.Length < 96; ++i)
		{
			char ch = value[i];
			text.Append(char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' ||
				ch == '.' || ch == '/' ? ch : '?');
		}
		return text.ToString();
	}

	static string chkKey(string key, bool emptyOk, bool slashOk)
	{
		string value = (key ?? string.Empty).Trim();
		if (value.Length == 0 && emptyOk)
		{
			return string.Empty;
		}
		if (value.Length == 0 || value[0] == '/' || (!slashOk && value[^1] == '/') ||
			Encoding.UTF8.GetByteCount(value) > 1024 || value.Contains("\\") ||
			value.Contains("//"))
		{
			throw new InvalidDataException("SSH对象Key格式错误");
		}
		string[] parts = value.Split('/');
		for (int i = 0; i < parts.Length; ++i)
		{
			if (parts[i] == "." || parts[i] == "..")
			{
				throw new InvalidDataException("SSH对象Key格式错误");
			}
			for (int j = 0; j < parts[i].Length; ++j)
			{
				if (char.IsControl(parts[i][j]))
				{
					throw new InvalidDataException("SSH对象Key含控制字符");
				}
			}
		}
		return value;
	}

	static string chkFile(string file, bool mustExist)
	{
		if (string.IsNullOrWhiteSpace(file) || !Path.IsPathRooted(file))
		{
			throw new InvalidDataException("SSH本地文件路径必须是绝对路径");
		}
		string full = Path.GetFullPath(file);
		if (mustExist && !File.Exists(full))
		{
			throw new FileNotFoundException("SSH上传文件不存在", full);
		}
		if (Directory.Exists(full) || string.IsNullOrEmpty(Path.GetFileName(full)))
		{
			throw new InvalidDataException("SSH本地文件路径错误");
		}
		return full;
	}

	static void expectOk(string value, string op)
	{
		if ((value ?? string.Empty).Trim() != "OK")
		{
			throw new InvalidDataException(op + "响应错误");
		}
	}

	static void move(string temp, string file)
	{
		if (File.Exists(file))
		{
			File.Replace(temp, file, null);
		}
		else
		{
			File.Move(temp, file);
		}
	}

	static void drop(string path)
	{
		if (!string.IsNullOrEmpty(path) && File.Exists(path))
		{
			File.Delete(path);
		}
	}

	void need()
	{
		if (mDone)
		{
			throw new ObjectDisposedException(nameof(SshStore));
		}
	}

	sealed class SshCmd : IDisposable
	{
		readonly Process mProc;
		readonly StringBuilder mErr = new();
		readonly object mErrLock = new();
		readonly string mOp;
		bool mDone;

		internal StreamWriter input => mProc.StandardInput;


		internal SshCmd(ProcessStartInfo start, string op)
		{
			mOp = op;
			mProc = new Process { StartInfo = start };
			mProc.ErrorDataReceived += onError;
			if (!mProc.Start())
			{
				throw new IOException("SSH进程启动失败");
			}
			mProc.BeginErrorReadLine();
		}

		internal void finish()
		{
			if (mDone) return;
			if (!mProc.WaitForExit(EXIT_TIMEOUT))
			{
				stop();
				throw new TimeoutException("SSH" + mOp + "超时");
			}
			mProc.WaitForExit();
			mDone = true;
			if (mProc.ExitCode != 0)
			{
				string error;
				lock (mErrLock)
				{
					error = mErr.ToString().Trim();
				}
				throw new IOException("SSH" + mOp + "命令失败 exit=" +
					mProc.ExitCode + " detail=" + safe(error));
			}
		}

		internal string readLine(string phase)
		{
			return wait(mProc.StandardOutput.ReadLineAsync(), phase);
		}

		internal string readAll(string phase)
		{
			return wait(mProc.StandardOutput.ReadToEndAsync(), phase);
		}

		internal void copyInput(Stream source)
		{
			byte[] buffer = new byte[COPY_BUFFER];
			for (int count = source.Read(buffer, 0, buffer.Length); count > 0;
				count = source.Read(buffer, 0, buffer.Length))
			{
				wait(mProc.StandardInput.BaseStream.WriteAsync(buffer, 0, count),
					"上传数据停滞");
			}
			wait(mProc.StandardInput.BaseStream.FlushAsync(), "上传刷新停滞");
		}

		internal void copyOutput(Stream target)
		{
			byte[] buffer = new byte[COPY_BUFFER];
			while (true)
			{
				int count = wait(mProc.StandardOutput.BaseStream.ReadAsync(
					buffer, 0, buffer.Length), "下载数据停滞");
				if (count == 0) return;
				target.Write(buffer, 0, count);
			}
		}

		T wait<T>(Task<T> task, string phase)
		{
			if (Task.WaitAny(task, Task.Delay(IO_IDLE_TIMEOUT)) != 0)
			{
				stop();
				throw new TimeoutException("SSH" + mOp + phase + "，超过60秒无响应");
			}
			return task.GetAwaiter().GetResult();
		}

		void wait(Task task, string phase)
		{
			if (Task.WaitAny(task, Task.Delay(IO_IDLE_TIMEOUT)) != 0)
			{
				stop();
				throw new TimeoutException("SSH" + mOp + phase + "，超过60秒无响应");
			}
			task.GetAwaiter().GetResult();
		}

		public void Dispose()
		{
			if (!mDone)
			{
				stop();
			}
			mProc.Dispose();
		}

		void onError(object sender, DataReceivedEventArgs args)
		{
			if (string.IsNullOrEmpty(args.Data)) return;
			lock (mErrLock)
			{
				if (mErr.Length < 512)
				{
					if (mErr.Length > 0) mErr.Append(' ');
					mErr.Append(args.Data);
				}
			}
		}

		void stop()
		{
			try
			{
				if (!mProc.HasExited)
				{
					mProc.Kill();
					mProc.WaitForExit(5000);
				}
			}
			catch
			{
				// 退出清理不能覆盖原始上传、下载或连接异常。
			}
			mDone = true;
		}
	}
}

internal sealed class SshLease : IObjLease
{
	SshStore mStore;
	readonly string mKey;
	readonly string mId;
	long mNext;

	internal SshLease(SshStore store, string key, string id)
	{
		mStore = store;
		mKey = key;
		mId = id;
		mNext = DateTime.UtcNow.Ticks + TimeSpan.TicksPerHour;
	}

	public void keep()
	{
		if (mStore == null)
		{
			throw new ObjectDisposedException(nameof(SshLease));
		}
		long now = DateTime.UtcNow.Ticks;
		if (now < mNext) return;
		mStore.keep(mKey, mId);
		mNext = now + TimeSpan.TicksPerHour;
	}

	public void Dispose()
	{
		if (mStore == null)
		{
			return;
		}
		SshStore store = mStore;
		mStore = null;
		try
		{
			store.release(mKey, mId);
		}
		catch (Exception ex)
		{
			UnityEngine.Debug.LogError(
				"发布主体已结束，但SSH锁释放失败；锁将在租约到期后失效:" + ex.Message);
		}
	}
}
