using System;
using System.IO;
using Org.BouncyCastle.Asn1.X9;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.OpenSsl;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.X509;

public sealed class RelKeyPair
{
	public string privatePem { get; internal set; }
	public string publicKey { get; internal set; }
}

// 生成Schema 11 Release使用的P-256密钥。调用方负责把私钥保存到项目与Git工作区外。
public static class RelKey
{
	public static RelKeyPair generate()
	{
		return generate(null);
	}

	// password非空时使用AES-256-CBC生成加密PEM；密码不得写入项目配置或EditorPrefs。
	public static RelKeyPair generate(char[] password)
	{
		checkPassword(password);
		SecureRandom random = new();
		ECKeyPairGenerator generator = new();
		generator.Init(new ECKeyGenerationParameters(X9ObjectIdentifiers.Prime256v1, random));
		AsymmetricCipherKeyPair pair = generator.GenerateKeyPair();

		using StringWriter output = new();
		PemWriter writer = new(output);
		if (password == null || password.Length == 0)
		{
			writer.WriteObject(pair.Private);
		}
		else
		{
			writer.WriteObject(pair.Private, "AES-256-CBC", password, random);
		}
		writer.Writer.Flush();

		byte[] publicDer = SubjectPublicKeyInfoFactory
			.CreateSubjectPublicKeyInfo(pair.Public).GetDerEncoded();
		return new RelKeyPair
		{
			privatePem = output.ToString(),
			publicKey = Convert.ToBase64String(publicDer),
		};
	}

	internal static string publicKey(string privatePem, char[] password)
	{
		ECPrivateKeyParameters key = read(privatePem, password);
		ECPublicKeyParameters publicKey = new("EC",
			key.Parameters.G.Multiply(key.D).Normalize(), key.PublicKeyParamSet);
		byte[] der = SubjectPublicKeyInfoFactory
			.CreateSubjectPublicKeyInfo(publicKey).GetDerEncoded();
		return Convert.ToBase64String(der);
	}

	internal static ECPrivateKeyParameters read(string privatePem, char[] password)
	{
		if (string.IsNullOrWhiteSpace(privatePem))
		{
			throw new InvalidDataException("Latest私钥PEM为空");
		}
		checkPassword(password);
		Password finder = null;
		try
		{
			using StringReader input = new(privatePem);
			if (password != null && password.Length > 0) finder = new Password(password);
			PemReader reader = password == null || password.Length == 0 ?
				new PemReader(input) : new PemReader(input, finder);
			object value = reader.ReadObject();
			ECPrivateKeyParameters key = value as ECPrivateKeyParameters;
			if (value is AsymmetricCipherKeyPair pair)
			{
				key = pair.Private as ECPrivateKeyParameters;
			}
			if (key == null ||
				!X9ObjectIdentifiers.Prime256v1.Equals(key.PublicKeyParamSet))
			{
				throw new InvalidDataException("Latest私钥必须是P-256 PEM");
			}
			return key;
		}
		catch (InvalidDataException)
		{
			throw;
		}
		catch (Exception ex)
		{
			throw new InvalidDataException("Latest私钥PEM读取失败，密码可能错误", ex);
		}
		finally
		{
			finder?.clear();
		}
	}

	internal static bool encrypted(string privatePem)
	{
		return !string.IsNullOrEmpty(privatePem) &&
			(privatePem.Contains("Proc-Type: 4,ENCRYPTED") ||
			privatePem.Contains("BEGIN ENCRYPTED PRIVATE KEY"));
	}

	static void checkPassword(char[] password)
	{
		if (password != null && password.Length > 0 && password.Length < 12)
		{
			throw new InvalidDataException("加密私钥密码至少需要12个字符");
		}
	}

	sealed class Password : IPasswordFinder
	{
		readonly char[] mValue;

		internal Password(char[] value)
		{
			mValue = (char[])value.Clone();
		}

		public char[] GetPassword()
		{
			return (char[])mValue.Clone();
		}

		internal void clear()
		{
			Array.Clear(mValue, 0, mValue.Length);
		}
	}
}
