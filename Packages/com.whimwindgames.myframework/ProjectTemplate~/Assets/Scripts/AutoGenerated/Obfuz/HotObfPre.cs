#if USE_OBFUZ
using System;
using Obfuz;
using Obfuz.EncryptionVM;
using UnityEngine;
using UnityEngine.Scripting;

[Preserve]
public sealed class HotObfPre : IHotPre
{
	[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
	private static void register()
	{
		HotPreReg.set(create);
	}

	private static IHotPre create() { return new HotObfPre(); }

	public void run(byte[] data)
	{
		if (data == null || data.Length == 0)
		{
			throw new ArgumentException("动态密钥为空", nameof(data));
		}
		EncryptionService<DefaultDynamicEncryptionScope>.Encryptor =
			new GeneratedEncryptionVirtualMachine(data);
	}
}
#endif
