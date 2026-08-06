using System;
using System.Collections.Generic;

public sealed class FrameServiceRegistry
{
	private readonly object mLock = new();
	private readonly Dictionary<Type, object> mServices = new();

	public int Count
	{
		get
		{
			lock (mLock)
			{
				return mServices.Count;
			}
		}
	}

	public void Register<T>(T service, bool replace = false) where T : class
	{
		Register(typeof(T), service, replace);
	}

	public void Set<T>(T service) where T : class
	{
		Register(typeof(T), service, true);
	}

	public void Register(Type serviceType, object service, bool replace = false)
	{
		if (serviceType == null)
		{
			throw new ArgumentNullException(nameof(serviceType));
		}
		if (service == null)
		{
			throw new ArgumentNullException(nameof(service));
		}
		if (!serviceType.IsInstanceOfType(service))
		{
			throw new ArgumentException($"Service {service.GetType().FullName} is not assignable to {serviceType.FullName}.", nameof(service));
		}

		lock (mLock)
		{
			if (!replace && mServices.ContainsKey(serviceType))
			{
				throw new InvalidOperationException($"Service {serviceType.FullName} is already registered.");
			}
			mServices[serviceType] = service;
		}
	}

	public T Get<T>() where T : class
	{
		if (TryGet(out T service))
		{
			return service;
		}
		throw new KeyNotFoundException($"Service {typeof(T).FullName} is not registered.");
	}

	public bool TryGet<T>(out T service) where T : class
	{
		lock (mLock)
		{
			if (mServices.TryGetValue(typeof(T), out object value))
			{
				service = (T)value;
				return true;
			}
		}
		service = null;
		return false;
	}

	public bool TryGet(Type serviceType, out object service)
	{
		if (serviceType == null)
		{
			throw new ArgumentNullException(nameof(serviceType));
		}
		lock (mLock)
		{
			return mServices.TryGetValue(serviceType, out service);
		}
	}

	public bool Remove<T>() where T : class
	{
		lock (mLock)
		{
			return mServices.Remove(typeof(T));
		}
	}

	public IReadOnlyDictionary<Type, object> Snapshot()
	{
		lock (mLock)
		{
			return new Dictionary<Type, object>(mServices);
		}
	}

	public void Clear()
	{
		lock (mLock)
		{
			mServices.Clear();
		}
	}
}
