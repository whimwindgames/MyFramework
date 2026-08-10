using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

[Serializable]
public sealed class RelKeyInfo
{
	public string id;
	public string publicKey;
	public string privateFile;
	public string createdUtc;
	public bool encrypted;
}

// 项目外的P-256 keyring。每个project/env有独立active/pending状态，私钥永不写入仓库。
public sealed class RelKeyStore
{
	const int SCHEMA = 1;
	const string STATE_FILE = "keyring.json";
	static readonly UTF8Encoding sUtf8 = new(false, true);

	[Serializable]
	sealed class State
	{
		public int schema = SCHEMA;
		public string project;
		public string env;
		public RelKeyInfo active;
		public RelKeyInfo pending;
		public string transitionSha;
		public string transitionBaseId;
		public long transitionSeq;
		public string transitionUtc;
	}

	readonly string mDir;
	readonly string mStatePath;
	State mState;

	RelKeyStore(string root, string project, string env)
	{
		checkId(project, "项目密钥标识");
		checkEnv(env);
		string fullRoot = Path.GetFullPath(root ?? string.Empty)
			.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		if (string.IsNullOrEmpty(fullRoot))
		{
			throw new InvalidDataException("密钥根目录为空");
		}
		outsideProject(fullRoot);
		mDir = Path.Combine(fullRoot, project, env);
		mStatePath = Path.Combine(mDir, STATE_FILE);
		mState = load(project, env);
	}

	public string directory => mDir;
	public RelKeyInfo active => copy(mState.active);
	public RelKeyInfo pending => copy(mState.pending);
	public string activePrivateKeyPath => privatePath(mState.active);
	public string pendingPrivateKeyPath => privatePath(mState.pending);
	public bool hasTransition => !string.IsNullOrEmpty(mState.transitionSha);

	public static RelKeyStore open(string project, string env)
	{
		string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		if (string.IsNullOrWhiteSpace(home))
		{
			throw new DirectoryNotFoundException("无法读取当前用户目录");
		}
		return new RelKeyStore(Path.Combine(home, ".myframework-keys"), project, env);
	}

	internal static RelKeyStore openAt(string root, string project, string env)
	{
		if (string.IsNullOrWhiteSpace(root) || !Path.IsPathRooted(root))
		{
			throw new InvalidDataException("测试密钥根目录必须是绝对路径");
		}
		return new RelKeyStore(root, project, env);
	}

	public static string defaultProject()
	{
		string raw = Path.GetFileName(Path.GetFullPath(Path.Combine(Application.dataPath, "..")))
			.ToLowerInvariant();
		StringBuilder value = new();
		bool dash = false;
		foreach (char ch in raw)
		{
			if ((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9'))
			{
				value.Append(ch);
				dash = false;
			}
			else if (!dash && value.Length > 0)
			{
				value.Append('-');
				dash = true;
			}
		}
		string result = value.ToString().Trim('-');
		if (result.Length > 64) result = result.Substring(0, 64).TrimEnd('-');
		checkId(result, "自动项目密钥标识");
		return result;
	}

	// 初次创建active密钥。prod可传密码生成加密PEM；密码不被本类保存。
	public RelKeyInfo create(char[] password = null)
	{
		if (mState.active != null || mState.pending != null)
		{
			throw new InvalidOperationException("当前环境已经存在密钥");
		}
		RelKeyInfo created = makeKey(password);
		try
		{
			mState.active = created;
			save();
			return copy(created);
		}
		catch
		{
			mState.active = null;
			drop(privatePath(created));
			throw;
		}
	}

	// 生成pending密钥，但不会改变active；下一份Base应使用返回的新公钥。
	public RelKeyInfo beginRotation(char[] password = null)
	{
		if (mState.active == null)
		{
			throw new InvalidOperationException("当前环境没有active密钥");
		}
		if (mState.pending != null)
		{
			throw new InvalidOperationException("已经存在待完成的密钥轮换");
		}
		RelKeyInfo created = makeKey(password);
		try
		{
			mState.pending = created;
			clearTransition();
			save();
			return copy(created);
		}
		catch
		{
			mState.pending = null;
			drop(privatePath(created));
			throw;
		}
	}

	// 用旧active私钥签发过渡Latest，并记录不可混淆的轮换证据。
	public byte[] signTransition(UpdCfg oldBase, UpdLatest latest,
		Func<char[]> password = null)
	{
		if (mState.active == null || mState.pending == null)
		{
			throw new InvalidOperationException("签发过渡Latest前必须先开始轮换");
		}
		if (oldBase == null || oldBase.env != mState.env ||
			oldBase.pubKey != mState.active.publicKey)
		{
			throw new InvalidDataException("过渡Latest必须属于当前环境和旧active公钥");
		}
		UpdRule.latest(oldBase, latest);
		byte[] body = sUtf8.GetBytes(JsonUtility.ToJson(latest, false));
		RelSign signer = new(activePrivateKeyPath, password);
		UpdBox box = new()
		{
			schema = UpdLim.Schema,
			alg = "ES256",
			data = Convert.ToBase64String(body),
			sig = Convert.ToBase64String(signer.sign(body)),
		};
		UpdRet<byte[]> opened = new UpdSign(mState.active.publicKey).open(box);
		if (!opened.ok || !same(body, opened.value))
		{
			throw new InvalidDataException("过渡Latest使用旧active密钥验签失败:" + opened.err);
		}
		byte[] raw = sUtf8.GetBytes(JsonUtility.ToJson(box, false));
		mState.transitionSha = UpdHash.data(raw);
		mState.transitionBaseId = oldBase.baseId;
		mState.transitionSeq = latest.seq;
		mState.transitionUtc = DateTime.UtcNow.ToString("o");
		save();
		return raw;
	}

	// 只有过渡Latest已签发且下一份Base确实冻结pending公钥后，才归档禁用旧私钥。
	public string completeRotation(string pubRoot, string platform, string baseId)
	{
		UpdCfg newBase = RelBuild.loadBase(pubRoot, mState.env, platform, baseId);
		return completeRotation(newBase);
	}

	internal string completeRotation(UpdCfg newBase)
	{
		if (mState.active == null || mState.pending == null || !hasTransition)
		{
			throw new InvalidOperationException("密钥轮换缺少active、pending或过渡Latest证据");
		}
		if (newBase == null || newBase.env != mState.env ||
			newBase.pubKey != mState.pending.publicKey ||
			newBase.baseId == mState.transitionBaseId)
		{
			throw new InvalidDataException("下一份Base未使用pending公钥，或仍是旧Base");
		}
		UpdRule.cfg(newBase);
		RelKeyInfo old = mState.active;
		RelKeyInfo next = mState.pending;
		string oldPath = privatePath(old);
		string archiveDir = Path.Combine(mDir, "archive");
		secureDir(archiveDir);
		string archived = Path.Combine(archiveDir, old.id + ".pem.disabled");
		if (File.Exists(archived))
		{
			throw new IOException("归档私钥已存在:" + archived);
		}
		File.Move(oldPath, archived);
		try
		{
			chmod(archived, "400");
			mState.active = next;
			mState.pending = null;
			clearTransition();
			save();
			return archived;
		}
		catch
		{
			mState.active = old;
			mState.pending = next;
			if (!File.Exists(oldPath) && File.Exists(archived))
			{
				File.Move(archived, oldPath);
				chmod(oldPath, "600");
			}
			throw;
		}
	}

	// 接受旧EditorPrefs中的PEM正文或绝对路径，复制到约定目录；不会自动清除旧值。
	public RelKeyInfo importLegacy(string legacyValue, Func<char[]> password = null)
	{
		if (mState.active != null || mState.pending != null)
		{
			throw new InvalidOperationException("目标环境已经存在密钥，禁止覆盖导入");
		}
		string value = (legacyValue ?? string.Empty).Trim();
		string pem = Path.IsPathRooted(value) && File.Exists(value) ?
			File.ReadAllText(Path.GetFullPath(value)) : value;
		char[] secret = password?.Invoke();
		string publicKey;
		try
		{
			publicKey = RelKey.publicKey(pem, secret);
		}
		finally
		{
			if (secret != null) Array.Clear(secret, 0, secret.Length);
		}
		RelKeyInfo imported = makeKey(pem, publicKey, RelKey.encrypted(pem));
		try
		{
			mState.active = imported;
			save();
			return copy(imported);
		}
		catch
		{
			mState.active = null;
			drop(privatePath(imported));
			throw;
		}
	}

	RelKeyInfo makeKey(char[] password)
	{
		RelKeyPair pair = RelKey.generate(password);
		return makeKey(pair.privatePem, pair.publicKey,
			password != null && password.Length > 0);
	}

	RelKeyInfo makeKey(string pem, string publicKey, bool encrypted)
	{
		string hash = UpdHash.data(sUtf8.GetBytes(publicKey));
		string id = "key-" + hash.Substring(0, 16);
		string relative = "keys/" + id + ".pem";
		RelKeyInfo value = new()
		{
			id = id,
			publicKey = publicKey,
			privateFile = relative,
			createdUtc = DateTime.UtcNow.ToString("o"),
			encrypted = encrypted,
		};
		string path = privatePath(value);
		secureDir(Path.GetDirectoryName(path));
		writeNew(path, sUtf8.GetBytes(pem));
		chmod(path, "600");
		return value;
	}

	State load(string project, string env)
	{
		if (!File.Exists(mStatePath))
		{
			return new State { project = project, env = env };
		}
		ensureNoLinks(mStatePath);
		State value = JsonUtility.FromJson<State>(File.ReadAllText(mStatePath, sUtf8));
		if (value == null || value.schema != SCHEMA || value.project != project ||
			value.env != env)
		{
			throw new InvalidDataException("密钥keyring身份或版本错误");
		}
		// Unity JsonUtility会把序列化前的null嵌套对象回读为空对象，先恢复语义。
		value.active = emptyInfo(value.active);
		value.pending = emptyInfo(value.pending);
		checkInfo(value.active, true);
		checkInfo(value.pending, true);
		if (value.active == null && value.pending != null)
		{
			throw new InvalidDataException("keyring不能在没有active时存在pending密钥");
		}
		if (value.active != null && value.pending != null && value.active.id == value.pending.id)
		{
			throw new InvalidDataException("active与pending密钥重复");
		}
		bool hasTransition = !string.IsNullOrEmpty(value.transitionSha);
		if (hasTransition && (value.active == null || value.pending == null ||
			!UpdFmt.isSha(value.transitionSha) ||
			!UpdFmt.isId(value.transitionBaseId) || value.transitionSeq < 0 ||
			!DateTime.TryParse(value.transitionUtc, null,
				System.Globalization.DateTimeStyles.RoundtripKind, out _)))
		{
			throw new InvalidDataException("keyring过渡Latest证据错误");
		}
		if (!hasTransition && (!string.IsNullOrEmpty(value.transitionBaseId) ||
			value.transitionSeq != 0 || !string.IsNullOrEmpty(value.transitionUtc)))
		{
			throw new InvalidDataException("keyring存在不完整的过渡Latest证据");
		}
		return value;
	}

	void save()
	{
		secureDir(mDir);
		byte[] raw = sUtf8.GetBytes(JsonUtility.ToJson(mState, true));
		string temp = mStatePath + ".tmp-" + Guid.NewGuid().ToString("N");
		try
		{
			writeNew(temp, raw);
			chmod(temp, "600");
			if (File.Exists(mStatePath)) File.Replace(temp, mStatePath, null);
			else File.Move(temp, mStatePath);
			chmod(mStatePath, "600");
		}
		finally
		{
			drop(temp);
		}
	}

	string privatePath(RelKeyInfo info)
	{
		if (info == null) return string.Empty;
		checkInfo(info, false);
		string path = Path.GetFullPath(Path.Combine(mDir,
			info.privateFile.Replace('/', Path.DirectorySeparatorChar)));
		string prefix = Path.GetFullPath(mDir).TrimEnd(Path.DirectorySeparatorChar,
			Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
		if (!path.StartsWith(prefix, StringComparison.Ordinal))
		{
			throw new InvalidDataException("私钥相对路径越界");
		}
		return path;
	}

	void checkInfo(RelKeyInfo info, bool requireFile)
	{
		if (info == null) return;
		checkId(info.id, "密钥标识");
		if (string.IsNullOrEmpty(info.publicKey) || info.publicKey.Length > 2048 ||
			string.IsNullOrEmpty(info.privateFile) ||
			!info.privateFile.StartsWith("keys/", StringComparison.Ordinal) ||
			!info.privateFile.EndsWith(".pem", StringComparison.Ordinal) ||
			info.privateFile.Contains("..") || Path.IsPathRooted(info.privateFile) ||
			string.IsNullOrEmpty(info.createdUtc))
		{
			throw new InvalidDataException("密钥元数据错误");
		}
		_ = new UpdSign(info.publicKey);
		if (requireFile)
		{
			string path = privatePath(info);
			FileInfo file = new(path);
			if (!file.Exists) throw new FileNotFoundException("keyring私钥不存在", path);
			if (file.Length < 100 || file.Length > 64 * 1024)
			{
				throw new InvalidDataException("keyring私钥大小异常");
			}
			ensureNoLinks(path);
		}
	}

	void clearTransition()
	{
		mState.transitionSha = null;
		mState.transitionBaseId = null;
		mState.transitionSeq = 0;
		mState.transitionUtc = null;
	}

	static RelKeyInfo copy(RelKeyInfo value)
	{
		return value == null ? null : new RelKeyInfo
		{
			id = value.id,
			publicKey = value.publicKey,
			privateFile = value.privateFile,
			createdUtc = value.createdUtc,
			encrypted = value.encrypted,
		};
	}

	static RelKeyInfo emptyInfo(RelKeyInfo value)
	{
		if (value == null) return null;
		bool empty = string.IsNullOrEmpty(value.id) && string.IsNullOrEmpty(value.publicKey) &&
			string.IsNullOrEmpty(value.privateFile) && string.IsNullOrEmpty(value.createdUtc) &&
			!value.encrypted;
		return empty ? null : value;
	}

	static void checkId(string value, string name)
	{
		if (!UpdFmt.isId(value) || value.Length > 64)
		{
			throw new InvalidDataException(name + "非法:" + value);
		}
	}

	static void checkEnv(string env)
	{
		if (env != "test" && env != "prod")
		{
			throw new InvalidDataException("密钥环境只能是test或prod");
		}
	}

	static void outsideProject(string path)
	{
		string project = Path.GetFullPath(Path.Combine(Application.dataPath, ".."))
			.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
			Path.DirectorySeparatorChar;
		string target = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar,
			Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
		if (target.StartsWith(project, StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidDataException("密钥目录不能位于项目或Git工作区内");
		}
	}

	static void secureDir(string path)
	{
		Directory.CreateDirectory(path);
		ensureNoLinks(path);
		chmod(path, "700");
	}

	static void writeNew(string path, byte[] raw)
	{
		using FileStream output = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
		output.Write(raw, 0, raw.Length);
		output.Flush(true);
	}

	static void ensureNoLinks(string path)
	{
		FileSystemInfo item = File.Exists(path) ? new FileInfo(path) : new DirectoryInfo(path);
		for (FileSystemInfo current = item; current != null;)
		{
			if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
			{
				throw new InvalidDataException("密钥路径不能经过符号链接:" + current.FullName);
			}
			current = current is FileInfo file ? file.Directory :
				(current as DirectoryInfo)?.Parent;
		}
	}

	static void chmod(string path, string mode)
	{
		if (Application.platform == RuntimePlatform.WindowsEditor) return;
		ProcessStartInfo start = new()
		{
			FileName = "/bin/chmod",
			UseShellExecute = false,
			CreateNoWindow = true,
		};
		start.ArgumentList.Add(mode);
		start.ArgumentList.Add(path);
		using Process proc = Process.Start(start);
		if (proc == null || !proc.WaitForExit(5000) || proc.ExitCode != 0)
		{
			try { proc?.Kill(); } catch { }
			throw new IOException("设置密钥文件权限失败:" + path);
		}
	}

	static bool same(byte[] left, byte[] right)
	{
		if (left == null || right == null || left.Length != right.Length) return false;
		int diff = 0;
		for (int i = 0; i < left.Length; ++i) diff |= left[i] ^ right[i];
		return diff == 0;
	}

	static void drop(string path)
	{
		try { if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path); }
		catch { }
	}
}

// 一次性兼容旧项目的全局EditorPrefs。导出后旧值仍保留，必须由用户确认后手动清除。
public static class RelKeyMigration
{
	public const string LegacyPref = "MyFramework.HotUpd.privKey";

	public static bool hasLegacy => !string.IsNullOrWhiteSpace(
		EditorPrefs.GetString(LegacyPref, string.Empty));

	public static RelKeyInfo export(string project, string env,
		Func<char[]> password = null)
	{
		string legacy = EditorPrefs.GetString(LegacyPref, string.Empty);
		if (string.IsNullOrWhiteSpace(legacy))
		{
			throw new InvalidOperationException("旧EditorPrefs中没有可迁移私钥");
		}
		return RelKeyStore.open(project, env).importLegacy(legacy, password);
	}

	public static void clearLegacy()
	{
		EditorPrefs.DeleteKey(LegacyPref);
	}
}
