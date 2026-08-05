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
		SecureRandom random = new();
		ECKeyPairGenerator generator = new();
		generator.Init(new ECKeyGenerationParameters(X9ObjectIdentifiers.Prime256v1, random));
		AsymmetricCipherKeyPair pair = generator.GenerateKeyPair();

		using StringWriter output = new();
		PemWriter writer = new(output);
		writer.WriteObject(pair.Private);
		writer.Writer.Flush();

		byte[] publicDer = SubjectPublicKeyInfoFactory
			.CreateSubjectPublicKeyInfo(pair.Public).GetDerEncoded();
		return new RelKeyPair
		{
			privatePem = output.ToString(),
			publicKey = Convert.ToBase64String(publicDer),
		};
	}
}
