using System;
using System.Threading;
using System.Threading.Tasks;

public enum FrameViewLayer
{
	HUD,
	WINDOW,
	POPUP,
	OVERLAY,
}

public enum FrameViewState
{
	OPENING,
	VISIBLE,
	CLOSING,
	HIDDEN,
	CANCELED,
	FAILED,
}

public sealed class FrameViewRequest
{
	public string Route { get; }
	public FrameViewLayer Layer { get; }
	public object Arguments { get; }

	public FrameViewRequest(string route, FrameViewLayer layer, object arguments = null)
	{
		if (string.IsNullOrWhiteSpace(route))
		{
			throw new ArgumentException("View route cannot be empty.", nameof(route));
		}
		Route = route;
		Layer = layer;
		Arguments = arguments;
	}
}

/// <summary>Opaque host view handle. The concrete view object remains owned by the host UI stack.</summary>
public sealed class FrameViewHandle
{
	public string Route { get; }
	public FrameViewLayer Layer { get; }
	public object View { get; }

	public FrameViewHandle(string route, FrameViewLayer layer, object view)
	{
		if (string.IsNullOrWhiteSpace(route))
		{
			throw new ArgumentException("View route cannot be empty.", nameof(route));
		}
		Route = route;
		Layer = layer;
		View = view ?? throw new ArgumentNullException(nameof(view));
	}
}

public readonly struct FrameViewStateChanged
{
	public readonly long Sequence;
	public readonly string Route;
	public readonly FrameViewLayer Layer;
	public readonly FrameViewState State;
	public readonly string Error;
	public readonly DateTimeOffset TimestampUtc;

	public FrameViewStateChanged(long sequence, string route, FrameViewLayer layer,
		FrameViewState state, string error)
	{
		Sequence = sequence;
		Route = route ?? string.Empty;
		Layer = layer;
		State = state;
		Error = error ?? string.Empty;
		TimestampUtc = DateTimeOffset.UtcNow;
	}
}

/// <summary>
/// Adapter implemented by the host UI stack. The framework does not require UGUI, UI Toolkit,
/// a prefab naming convention, a Canvas hierarchy or a particular resource backend.
/// </summary>
public interface IFrameViewAdapter
{
	Task<FrameViewHandle> ShowAsync(FrameViewRequest request,
		CancellationToken cancellationToken = default);

	Task HideAsync(FrameViewHandle handle, CancellationToken cancellationToken = default);

	Task<bool> BackAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Common observable entry point for lobby pages, game HUDs, windows, popups and overlays.
/// Stack, pooling, animation and input-blocking behavior remain in the host adapter.
/// </summary>
public sealed class FrameViewRouter
{
	private readonly object mLock = new();
	private readonly string mOwner;
	private readonly FrameEventBus mEvents;
	private readonly IFrameLogSink mLogSink;
	private IFrameViewAdapter mAdapter;
	private long mSequence;
	private int mStopped;

	public FrameViewRouter(string owner, FrameEventBus events, IFrameLogSink logSink)
	{
		mOwner = string.IsNullOrWhiteSpace(owner) ? "Views" : owner;
		mEvents = events ?? throw new ArgumentNullException(nameof(events));
		mLogSink = logSink ?? throw new ArgumentNullException(nameof(logSink));
	}

	public IFrameViewAdapter Adapter
	{
		get
		{
			lock (mLock)
			{
				return mAdapter;
			}
		}
	}

	public void UseAdapter(IFrameViewAdapter adapter, bool replace = false)
	{
		throwIfStopped();
		if (adapter == null)
		{
			throw new ArgumentNullException(nameof(adapter));
		}
		lock (mLock)
		{
			if (!replace && mAdapter != null && !ReferenceEquals(mAdapter, adapter))
			{
				throw new InvalidOperationException("A frame view adapter is already installed.");
			}
			mAdapter = adapter;
		}
	}

	public async Task<FrameViewHandle> ShowAsync(FrameViewRequest request,
		CancellationToken cancellationToken = default)
	{
		throwIfStopped();
		if (request == null)
		{
			throw new ArgumentNullException(nameof(request));
		}
		IFrameViewAdapter adapter = Adapter ??
			throw new InvalidOperationException("No frame view adapter is installed.");
		publish(request.Route, request.Layer, FrameViewState.OPENING);
		try
		{
			cancellationToken.ThrowIfCancellationRequested();
			FrameViewHandle handle = await adapter.ShowAsync(request, cancellationToken);
			if (handle == null)
			{
				throw new InvalidOperationException($"View adapter returned no handle for '{request.Route}'.");
			}
			if (!string.Equals(handle.Route, request.Route, StringComparison.Ordinal) ||
				handle.Layer != request.Layer)
			{
				throw new InvalidOperationException(
					$"View adapter returned a mismatched handle for '{request.Route}'.");
			}
			publish(handle.Route, handle.Layer, FrameViewState.VISIBLE);
			return handle;
		}
		catch (OperationCanceledException)
		{
			publish(request.Route, request.Layer, FrameViewState.CANCELED);
			throw;
		}
		catch (Exception exception)
		{
			publish(request.Route, request.Layer, FrameViewState.FAILED, exception.Message);
			mLogSink.Write(new FrameLogRecord(FrameLogLevel.ERROR, mOwner,
				$"View show failed for '{request.Route}'.", exception));
			throw;
		}
	}

	public async Task HideAsync(FrameViewHandle handle,
		CancellationToken cancellationToken = default)
	{
		throwIfStopped();
		if (handle == null)
		{
			throw new ArgumentNullException(nameof(handle));
		}
		IFrameViewAdapter adapter = Adapter ??
			throw new InvalidOperationException("No frame view adapter is installed.");
		publish(handle.Route, handle.Layer, FrameViewState.CLOSING);
		try
		{
			cancellationToken.ThrowIfCancellationRequested();
			await adapter.HideAsync(handle, cancellationToken);
			publish(handle.Route, handle.Layer, FrameViewState.HIDDEN);
		}
		catch (OperationCanceledException)
		{
			publish(handle.Route, handle.Layer, FrameViewState.CANCELED);
			throw;
		}
		catch (Exception exception)
		{
			publish(handle.Route, handle.Layer, FrameViewState.FAILED, exception.Message);
			mLogSink.Write(new FrameLogRecord(FrameLogLevel.ERROR, mOwner,
				$"View hide failed for '{handle.Route}'.", exception));
			throw;
		}
	}

	public Task<bool> BackAsync(CancellationToken cancellationToken = default)
	{
		throwIfStopped();
		IFrameViewAdapter adapter = Adapter ??
			throw new InvalidOperationException("No frame view adapter is installed.");
		cancellationToken.ThrowIfCancellationRequested();
		return adapter.BackAsync(cancellationToken);
	}

	public void ClearAdapter(IFrameViewAdapter adapter = null)
	{
		lock (mLock)
		{
			if (adapter == null || ReferenceEquals(mAdapter, adapter))
			{
				mAdapter = null;
			}
		}
	}

	private void publish(string route, FrameViewLayer layer, FrameViewState state,
		string error = null)
	{
		if (Volatile.Read(ref mStopped) != 0)
		{
			return;
		}
		try
		{
			mEvents.Publish(new FrameViewStateChanged(
				Interlocked.Increment(ref mSequence), route, layer, state, error));
		}
		catch (ObjectDisposedException) when (Volatile.Read(ref mStopped) != 0)
		{
			// A host transition raced context shutdown; no further observation is required.
		}
	}

	public void Shutdown()
	{
		Interlocked.Exchange(ref mStopped, 1);
		ClearAdapter();
	}

	private void throwIfStopped()
	{
		if (Volatile.Read(ref mStopped) != 0)
		{
			throw new ObjectDisposedException(nameof(FrameViewRouter));
		}
	}
}
