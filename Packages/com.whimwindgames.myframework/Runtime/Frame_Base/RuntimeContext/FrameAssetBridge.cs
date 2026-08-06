using System;
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

	public Task<FrameAssetLease<GameObject>> InstantiateAsync(string address, Transform parent = null,
		CancellationToken cancellationToken = default)
	{
		return executeAsync(address, FrameAssetKind.INSTANCE,
			(provider, token) => provider.InstantiateAsync(address, parent, token), cancellationToken);
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
		if (string.IsNullOrWhiteSpace(address))
		{
			throw new ArgumentException("Asset address cannot be empty.", nameof(address));
		}
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
			publish<T>(address, kind, FrameAssetOperationState.FAILED, exception.Message);
			mLogSink.Write(new FrameLogRecord(FrameLogLevel.ERROR, mOwner,
				$"Asset operation failed for '{address}'.", exception));
			throw;
		}
	}

	private void publish<T>(string address, FrameAssetKind kind,
		FrameAssetOperationState state, string error = null) where T : UnityEngine.Object
	{
		mEvents.Publish(new FrameAssetOperationChanged(
			Interlocked.Increment(ref mSequence), address, typeof(T), kind, state, error));
	}
}
