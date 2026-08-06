using System;
using System.Collections.Generic;
using System.Threading;

public sealed class FrameConfigurationStore
{
	private readonly struct ConfigurationKey : IEquatable<ConfigurationKey>
	{
		public readonly Type Type;
		public readonly string Name;

		public ConfigurationKey(Type type, string name)
		{
			Type = type;
			Name = name ?? string.Empty;
		}

		public bool Equals(ConfigurationKey other)
		{
			return Type == other.Type && string.Equals(Name, other.Name, StringComparison.Ordinal);
		}

		public override bool Equals(object obj)
		{
			return obj is ConfigurationKey other && Equals(other);
		}

		public override int GetHashCode()
		{
			unchecked
			{
				return ((Type != null ? Type.GetHashCode() : 0) * 397) ^ StringComparer.Ordinal.GetHashCode(Name);
			}
		}
	}

	private readonly object mLock = new();
	private readonly Dictionary<ConfigurationKey, object> mConfigurations = new();
	private long mVersion;

	public long Version => Interlocked.Read(ref mVersion);

	public int Count
	{
		get
		{
			lock (mLock)
			{
				return mConfigurations.Count;
			}
		}
	}

	public void Set<T>(T configuration, string name = null) where T : class
	{
		if (configuration == null)
		{
			throw new ArgumentNullException(nameof(configuration));
		}
		lock (mLock)
		{
			mConfigurations[new ConfigurationKey(typeof(T), name)] = configuration;
			Interlocked.Increment(ref mVersion);
		}
	}

	public T Get<T>(string name = null) where T : class
	{
		if (TryGet(name, out T configuration))
		{
			return configuration;
		}
		throw new KeyNotFoundException($"Configuration {describe(typeof(T), name)} is not registered.");
	}

	public bool TryGet<T>(string name, out T configuration) where T : class
	{
		lock (mLock)
		{
			if (mConfigurations.TryGetValue(new ConfigurationKey(typeof(T), name), out object value))
			{
				configuration = (T)value;
				return true;
			}
		}
		configuration = null;
		return false;
	}

	public bool TryGet<T>(out T configuration) where T : class
	{
		return TryGet(null, out configuration);
	}

	public bool Remove<T>(string name = null) where T : class
	{
		lock (mLock)
		{
			bool removed = mConfigurations.Remove(new ConfigurationKey(typeof(T), name));
			if (removed)
			{
				Interlocked.Increment(ref mVersion);
			}
			return removed;
		}
	}

	public void Clear()
	{
		lock (mLock)
		{
			if (mConfigurations.Count == 0)
			{
				return;
			}
			mConfigurations.Clear();
			Interlocked.Increment(ref mVersion);
		}
	}

	private static string describe(Type type, string name)
	{
		return string.IsNullOrEmpty(name) ? type.FullName : $"{type.FullName} ('{name}')";
	}
}
