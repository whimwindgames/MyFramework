using System;
using System.IO;
using UnityEditor;
using static FrameBaseDefine;

// Obfuz及其他代码保护方案通过项目适配器接入，框架不绑定具体插件版本。
// 保留ArcadeHub已经使用的外部API名称，迁入项目时无需修改适配器调用面。
public interface IObfApi
{
	string cap();
	void chk(HotSet hot);
	void run(string outDir, string aotDir, HotSet hot, bool isDebug);
	string mapPath();
}

internal static class DllObf
{
	public const string NoCap = "none";
	const string CAP_PRE = "obfuz-v1:";

	public static string cap()
	{
		IObfApi obf = find();
		string value = obf == null ? NoCap : obf.cap();
		if (!isCap(value)) throw new InvalidDataException("Obfuz适配器返回了非法Base能力标识");
		return value;
	}

	public static bool isCap(string value)
	{
		if (value == NoCap) return true;
		if (value == null || value.Length != CAP_PRE.Length + 64 ||
			!value.StartsWith(CAP_PRE, StringComparison.Ordinal)) return false;
		for (int i = CAP_PRE.Length; i < value.Length; ++i)
		{
			char c = value[i];
			if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
		}
		return true;
	}

	public static void chk(HotSet hot, bool use)
	{
		HotList.chk(hot);
		if (use) api().chk(hot);
	}

	public static void run(string outDir, string aotDir, HotSet hot, bool use, bool isDebug)
	{
		chk(hot, use);
		if (use)
		{
			api().run(outDir, aotDir, hot, isDebug);
			return;
		}
		string key = Path.Combine(outDir, DYNAMIC_SECRET_FILE);
		if (File.Exists(key)) File.Delete(key);
	}

	public static string mapPath()
	{
		return api().mapPath();
	}

	static IObfApi api()
	{
		IObfApi obf = find();
		if (obf == null) throw new InvalidOperationException("当前项目未安装可用的Obfuz编辑器适配器");
		return obf;
	}

	static IObfApi find()
	{
		Type found = null;
		foreach (Type type in TypeCache.GetTypesDerivedFrom<IObfApi>())
		{
			if (type == null || type.IsAbstract || type.IsInterface) continue;
			if (found != null) throw new InvalidOperationException("Obfuz适配器只能存在一个");
			found = type;
		}
		return found == null ? null : (IObfApi)Activator.CreateInstance(found);
	}
}
