using System;

public interface IObjLease : IDisposable
{
	void keep();
}

public interface IObjStore : IDisposable
{
	string clientBase { get; }
	void put(string key, string file);
	void get(string key, string file);
	string[] list(string prefix);
	IObjLease take(string key);
}

public static class StoreBind
{
	static Func<IObjStore> sMake;

	public static bool ready => sMake != null;

	public static void bind(Func<IObjStore> make)
	{
		sMake = make;
	}

	internal static IObjStore open()
	{
		IObjStore store = sMake?.Invoke();
		if (store == null)
		{
			throw new InvalidOperationException("发布存储适配器未配置");
		}
		return store;
	}
}
