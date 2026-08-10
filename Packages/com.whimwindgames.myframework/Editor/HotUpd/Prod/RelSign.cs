using System;
using System.IO;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;
using UnityEngine;

public sealed class RelSign
{
	readonly ECPrivateKeyParameters mKey;

	public RelSign(string path)
		: this(path, null)
	{
	}

	// 密码通过回调按需取得并在读取后清零，调用方不得把密码持久化到项目配置。
	public RelSign(string path, Func<char[]> password)
	{
		string full = checkPath(path);
		char[] secret = password?.Invoke();
		try
		{
			mKey = RelKey.read(File.ReadAllText(full), secret);
		}
		finally
		{
			if (secret != null) Array.Clear(secret, 0, secret.Length);
		}
	}

	public byte[] sign(byte[] data)
	{
		if (data == null || data.Length == 0) throw new ArgumentException("Latest签名载荷为空", nameof(data));
		DsaDigestSigner signer = new(new ECDsaSigner(), new Sha256Digest(), PlainDsaEncoding.Instance);
		signer.Init(true, new ParametersWithRandom(mKey, new SecureRandom()));
		signer.BlockUpdate(data, 0, data.Length);
		byte[] sig = signer.GenerateSignature();
		if (sig.Length != 64) throw new InvalidDataException("ES256签名长度错误");
		return sig;
	}

	static string checkPath(string path)
	{
		if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path))
			throw new InvalidDataException("Latest私钥必须使用项目外绝对路径");
		string full = Path.GetFullPath(path);
		FileInfo file = new(full);
		if (!file.Exists || (file.Attributes & FileAttributes.ReparsePoint) != 0)
			throw new InvalidDataException("Latest私钥不存在或为符号链接");
		for (DirectoryInfo dir = file.Directory; dir != null; dir = dir.Parent)
			if ((dir.Attributes & FileAttributes.ReparsePoint) != 0)
				throw new InvalidDataException("Latest私钥路径不能经过符号链接");
		string project = Path.GetFullPath(Path.Combine(Application.dataPath, ".."))
			.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
		string key = full + Path.DirectorySeparatorChar;
		if (key.StartsWith(project, StringComparison.OrdinalIgnoreCase))
			throw new InvalidDataException("Latest私钥不能位于项目或Git工作区内");
		return full;
	}
}
