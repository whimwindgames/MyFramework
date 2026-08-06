using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

public enum FrameAssetKind
{
	ASSET,
	INSTANCE,
}

public enum FrameAssetOperationState
{
	LOADING,
	SUCCEEDED,
	RELEASED,
	CANCELED,
	FAILED,
}

public readonly struct FrameAssetOperationChanged
{
	public readonly long Sequence;
	public readonly string Address;
	public readonly Type AssetType;
	public readonly FrameAssetKind Kind;
	public readonly FrameAssetOperationState State;
	public readonly string Error;
	public readonly DateTimeOffset TimestampUtc;

	public FrameAssetOperationChanged(long sequence, string address, Type assetType,
		FrameAssetKind kind, FrameAssetOperationState state, string error)
	{
		Sequence = sequence;
		Address = address ?? string.Empty;
		AssetType = assetType;
		Kind = kind;
		State = state;
		Error = error ?? string.Empty;
		TimestampUtc = DateTimeOffset.UtcNow;
	}
}

/// <summary>
/// A host-owned asset or instance plus its matching release action. Disposing is idempotent.
/// Addressables, Resources, AssetBundle and editor providers can preserve their own lifetime rules.
/// </summary>
public sealed class FrameAssetLease<T> : IDisposable where T : UnityEngine.Object
{
	private Action<T> mRelease;

	public string Address { get; }
	public FrameAssetKind Kind { get; }
	public T Value { get; }
	public bool IsDisposed => mRelease == null;

	public FrameAssetLease(string address, T value, FrameAssetKind kind, Action<T> release)
	{
		if (string.IsNullOrWhiteSpace(address))
		{
			throw new ArgumentException("Asset address cannot be empty.", nameof(address));
		}
		Value = value != null ? value : throw new ArgumentNullException(nameof(value));
		Address = address;
		Kind = kind;
		mRelease = release ?? throw new ArgumentNullException(nameof(release));
	}

	public void Dispose()
	{
		Action<T> release = Interlocked.Exchange(ref mRelease, null);
		release?.Invoke(Value);
	}
}

/// <summary>
/// A host-owned collection loaded by one logical address plus its matching release action.
/// The collection is immutable from the consumer's point of view and disposal is idempotent.
/// </summary>
public sealed class FrameAssetCollectionLease<T> : IDisposable where T : UnityEngine.Object
{
	private Action<IReadOnlyList<T>> mRelease;

	public string Address { get; }
	public IReadOnlyList<T> Values { get; }
	public int Count => Values.Count;
	public bool IsDisposed => mRelease == null;

	public FrameAssetCollectionLease(string address, IReadOnlyList<T> values,
		Action<IReadOnlyList<T>> release)
	{
		if (string.IsNullOrWhiteSpace(address))
		{
			throw new ArgumentException("Asset address cannot be empty.", nameof(address));
		}
		Address = address;
		Values = values ?? throw new ArgumentNullException(nameof(values));
		mRelease = release ?? throw new ArgumentNullException(nameof(release));
	}

	public void Dispose()
	{
		Action<IReadOnlyList<T>> release = Interlocked.Exchange(ref mRelease, null);
		release?.Invoke(Values);
	}
}

/// <summary>
/// Host asset backend. The framework deliberately does not reference Addressables or choose a
/// Resources/AssetBundle policy; each application installs the provider matching its production pipeline.
/// </summary>
public interface IFrameAssetProvider
{
	Task<FrameAssetLease<T>> LoadAsync<T>(string address,
		CancellationToken cancellationToken = default) where T : UnityEngine.Object;

	Task<FrameAssetLease<GameObject>> InstantiateAsync(string address, Transform parent = null,
		CancellationToken cancellationToken = default);
}

/// <summary>
/// Optional provider capability for callers that require an immediate asset result, such as
/// bootstrap configuration or an existing synchronous prefab factory. Providers should implement
/// this only for addresses whose backend can complete synchronously without blocking remote I/O.
/// </summary>
public interface IFrameSynchronousAssetProvider
{
	FrameAssetLease<T> Load<T>(string address) where T : UnityEngine.Object;

	FrameAssetCollectionLease<T> LoadAll<T>(string address) where T : UnityEngine.Object;
}

/// <summary>
/// Optional synchronous catalog used before an immediate optional load. This keeps a missing
/// fallback address from being reported as a failed asset operation.
/// </summary>
public interface IFrameSynchronousAssetCatalog
{
	bool Exists<T>(string address) where T : UnityEngine.Object;
}

/// <summary>
/// Optional provider capability for checking an address without starting a load. Hosts whose
/// resource backend has no catalog can omit it; callers then receive a clear not-supported error.
/// </summary>
public interface IFrameAssetCatalog
{
	Task<bool> ExistsAsync<T>(string address,
		CancellationToken cancellationToken = default) where T : UnityEngine.Object;
}

/// <summary>
/// Process-level handoff used when an AOT host and a hot-update assembly cannot reference each
/// other directly. The resource runtime publishes one provider; a host runtime context observes
/// it and remains the owner of gateway replacement and fallback policy.
/// </summary>
public static class FrameAssetProviderHandoff
{
	private static IFrameAssetProvider sCurrent;

	public static event Action<IFrameAssetProvider> Changed;

	public static IFrameAssetProvider Current => Volatile.Read(ref sCurrent);

	public static void Publish(IFrameAssetProvider provider)
	{
		if (provider == null)
		{
			throw new ArgumentNullException(nameof(provider));
		}
		IFrameAssetProvider previous = Interlocked.Exchange(ref sCurrent, provider);
		if (!ReferenceEquals(previous, provider))
		{
			notifyChanged(provider);
		}
	}

	public static bool Clear(IFrameAssetProvider expected = null)
	{
		IFrameAssetProvider removed;
		if (expected == null)
		{
			removed = Interlocked.Exchange(ref sCurrent, null);
		}
		else
		{
			removed = Interlocked.CompareExchange(ref sCurrent, null, expected);
			if (!ReferenceEquals(removed, expected))
			{
				return false;
			}
		}
		if (removed == null)
		{
			return false;
		}
		notifyChanged(null);
		return true;
	}

	private static void notifyChanged(IFrameAssetProvider provider)
	{
		Delegate[] listeners = Changed?.GetInvocationList();
		if (listeners == null)
		{
			return;
		}
		foreach (Delegate listener in listeners)
		{
			try
			{
				((Action<IFrameAssetProvider>)listener).Invoke(provider);
			}
			catch (Exception exception)
			{
				Debug.LogException(exception);
			}
		}
	}
}

/// <summary>
/// Observable facade over a host-provided asset backend. It owns no backend and never disposes
/// outstanding host leases implicitly; the consumer that receives a lease remains its owner.
/// </summary>
public sealed class FrameAssetGateway
{
	private readonly object mLock = new();
	private readonly string mOwner;
	private readonly FrameEventBus mEvents;
	private readonly IFrameLogSink mLogSink;
	private IFrameAssetProvider mProvider;
	private long mSequence;
	private int mActiveLeaseCount;
	private int mStopped;

	public FrameAssetGateway(string owner, FrameEventBus events, IFrameLogSink logSink)
	{
		mOwner = string.IsNullOrWhiteSpace(owner) ? "Assets" : owner;
		mEvents = events ?? throw new ArgumentNullException(nameof(events));
		mLogSink = logSink ?? throw new ArgumentNullException(nameof(logSink));
	}

	public IFrameAssetProvider Provider
	{
		get
		{
			lock (mLock)
			{
				return mProvider;
			}
		}
	}

	public int ActiveLeaseCount => Volatile.Read(ref mActiveLeaseCount);

	public void UseProvider(IFrameAssetProvider provider, bool replace = false)
	{
		throwIfStopped();
		if (provider == null)
		{
			throw new ArgumentNullException(nameof(provider));
		}
		lock (mLock)
		{
			if (!replace && mProvider != null && !ReferenceEquals(mProvider, provider))
			{
				throw new InvalidOperationException("A frame asset provider is already installed.");
			}
			mProvider = provider;
		}
	}

	public Task<FrameAssetLease<T>> LoadAsync<T>(string address,
		CancellationToken cancellationToken = default) where T : UnityEngine.Object
	{
		return executeAsync(address, FrameAssetKind.ASSET,
			(provider, token) => provider.LoadAsync<T>(address, token), cancellationToken);
	}

	public FrameAssetLease<T> Load<T>(string address) where T : UnityEngine.Object
	{
		throwIfStopped();
		validateAddress(address);
		IFrameAssetProvider provider = Provider ??
			throw new InvalidOperationException("No frame asset provider is installed.");
		if (provider is not IFrameSynchronousAssetProvider synchronousProvider)
		{
			throw new NotSupportedException(
				$"Asset provider {provider.GetType().FullName} does not support synchronous loading.");
		}

		publish<T>(address, FrameAssetKind.ASSET, FrameAssetOperationState.LOADING);
		try
		{
			FrameAssetLease<T> providerLease = synchronousProvider.Load<T>(address);
			if (providerLease == null)
			{
				throw new InvalidOperationException($"Asset provider returned no lease for '{address}'.");
			}
			Interlocked.Increment(ref mActiveLeaseCount);
			publish<T>(address, FrameAssetKind.ASSET, FrameAssetOperationState.SUCCEEDED);
			return new FrameAssetLease<T>(address, providerLease.Value, FrameAssetKind.ASSET, _ =>
			{
				try
				{
					providerLease.Dispose();
				}
				finally
				{
					Interlocked.Decrement(ref mActiveLeaseCount);
					publish<T>(address, FrameAssetKind.ASSET, FrameAssetOperationState.RELEASED);
				}
			});
		}
		catch (Exception exception)
		{
			publishFailure<T>(address, exception);
			throw;
		}
	}

	public FrameAssetCollectionLease<T> LoadAll<T>(string address) where T : UnityEngine.Object
	{
		throwIfStopped();
		validateAddress(address);
		IFrameAssetProvider provider = Provider ??
			throw new InvalidOperationException("No frame asset provider is installed.");
		if (provider is not IFrameSynchronousAssetProvider synchronousProvider)
		{
			throw new NotSupportedException(
				$"Asset provider {provider.GetType().FullName} does not support synchronous loading.");
		}

		publish<T>(address, FrameAssetKind.ASSET, FrameAssetOperationState.LOADING);
		try
		{
			FrameAssetCollectionLease<T> providerLease = synchronousProvider.LoadAll<T>(address);
			if (providerLease == null)
			{
				throw new InvalidOperationException($"Asset provider returned no collection lease for '{address}'.");
			}
			Interlocked.Increment(ref mActiveLeaseCount);
			publish<T>(address, FrameAssetKind.ASSET, FrameAssetOperationState.SUCCEEDED);
			return new FrameAssetCollectionLease<T>(address, providerLease.Values, _ =>
			{
				try
				{
					providerLease.Dispose();
				}
				finally
				{
					Interlocked.Decrement(ref mActiveLeaseCount);
					publish<T>(address, FrameAssetKind.ASSET, FrameAssetOperationState.RELEASED);
				}
			});
		}
		catch (Exception exception)
		{
			publishFailure<T>(address, exception);
			throw;
		}
	}

	public Task<FrameAssetLease<GameObject>> InstantiateAsync(string address, Transform parent = null,
		CancellationToken cancellationToken = default)
	{
		return executeAsync(address, FrameAssetKind.INSTANCE,
			(provider, token) => provider.InstantiateAsync(address, parent, token), cancellationToken);
	}

	public Task<bool> ExistsAsync<T>(string address,
		CancellationToken cancellationToken = default) where T : UnityEngine.Object
	{
		throwIfStopped();
		if (string.IsNullOrWhiteSpace(address))
		{
			return Task.FromResult(false);
		}
		IFrameAssetProvider provider = Provider ??
			throw new InvalidOperationException("No frame asset provider is installed.");
		if (provider is not IFrameAssetCatalog catalog)
		{
			throw new NotSupportedException(
				$"Asset provider {provider.GetType().FullName} does not expose a catalog.");
		}
		cancellationToken.ThrowIfCancellationRequested();
		return catalog.ExistsAsync<T>(address, cancellationToken);
	}

	public bool Exists<T>(string address) where T : UnityEngine.Object
	{
		throwIfStopped();
		if (string.IsNullOrWhiteSpace(address))
		{
			return false;
		}
		IFrameAssetProvider provider = Provider ??
			throw new InvalidOperationException("No frame asset provider is installed.");
		if (provider is not IFrameSynchronousAssetCatalog catalog)
		{
			throw new NotSupportedException(
				$"Asset provider {provider.GetType().FullName} does not expose a synchronous catalog.");
		}
		return catalog.Exists<T>(address);
	}

	public void ClearProvider(IFrameAssetProvider provider = null)
	{
		lock (mLock)
		{
			if (provider == null || ReferenceEquals(mProvider, provider))
			{
				mProvider = null;
			}
		}
	}

	private async Task<FrameAssetLease<T>> executeAsync<T>(string address, FrameAssetKind kind,
		Func<IFrameAssetProvider, CancellationToken, Task<FrameAssetLease<T>>> operation,
		CancellationToken cancellationToken) where T : UnityEngine.Object
	{
		throwIfStopped();
		validateAddress(address);
		IFrameAssetProvider provider = Provider ??
			throw new InvalidOperationException("No frame asset provider is installed.");

		publish<T>(address, kind, FrameAssetOperationState.LOADING);
		try
		{
			cancellationToken.ThrowIfCancellationRequested();
			FrameAssetLease<T> providerLease = await operation(provider, cancellationToken);
			if (providerLease == null)
			{
				throw new InvalidOperationException($"Asset provider returned no lease for '{address}'.");
			}
			Interlocked.Increment(ref mActiveLeaseCount);
			publish<T>(address, kind, FrameAssetOperationState.SUCCEEDED);
			return new FrameAssetLease<T>(address, providerLease.Value, kind, _ =>
			{
				try
				{
					providerLease.Dispose();
				}
				finally
				{
					Interlocked.Decrement(ref mActiveLeaseCount);
					publish<T>(address, kind, FrameAssetOperationState.RELEASED);
				}
			});
		}
		catch (OperationCanceledException)
		{
			publish<T>(address, kind, FrameAssetOperationState.CANCELED);
			throw;
		}
		catch (Exception exception)
		{
			publishFailure<T>(address, exception, kind);
			throw;
		}
	}

	private static void validateAddress(string address)
	{
		if (string.IsNullOrWhiteSpace(address))
		{
			throw new ArgumentException("Asset address cannot be empty.", nameof(address));
		}
	}

	private void publishFailure<T>(string address, Exception exception,
		FrameAssetKind kind = FrameAssetKind.ASSET) where T : UnityEngine.Object
	{
		publish<T>(address, kind, FrameAssetOperationState.FAILED, exception.Message);
		mLogSink.Write(new FrameLogRecord(FrameLogLevel.ERROR, mOwner,
			$"Asset operation failed for '{address}'.", exception));
	}

	private void publish<T>(string address, FrameAssetKind kind,
		FrameAssetOperationState state, string error = null) where T : UnityEngine.Object
	{
		if (Volatile.Read(ref mStopped) != 0)
		{
			return;
		}
		try
		{
			mEvents.Publish(new FrameAssetOperationChanged(
				Interlocked.Increment(ref mSequence), address, typeof(T), kind, state, error));
		}
		catch (ObjectDisposedException) when (Volatile.Read(ref mStopped) != 0)
		{
			// A host operation raced context shutdown; its lease remains valid and host-owned.
		}
	}

	public void Shutdown()
	{
		Interlocked.Exchange(ref mStopped, 1);
		ClearProvider();
	}

	private void throwIfStopped()
	{
		if (Volatile.Read(ref mStopped) != 0)
		{
			throw new ObjectDisposedException(nameof(FrameAssetGateway));
		}
	}
}
