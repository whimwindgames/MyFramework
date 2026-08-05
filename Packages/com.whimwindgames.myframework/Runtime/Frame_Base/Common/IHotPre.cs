using System;

// 可选AOT扩展在热更入口启动前处理补丁附加数据。
public interface IHotPre
{
	void run(byte[] data);
}

[UnityEngine.Scripting.Preserve]
public static class HotPreReg
{
	private static Func<IHotPre> sMake;

	public static void set(Func<IHotPre> make)
	{
		if (make == null) throw new ArgumentNullException(nameof(make));
		if (sMake != null && sMake != make)
		{
			throw new InvalidOperationException("热更启动扩展只能注册一个");
		}
		sMake = make;
	}

	public static void run(byte[] data)
	{
		if (data == null || data.Length == 0) return;
		IHotPre pre = sMake?.Invoke();
		if (pre == null)
		{
			throw new InvalidOperationException("补丁包含附加启动数据，但客户端没有注册AOT扩展");
		}
		pre.run(data);
	}
}
