using System;
using System.Collections.Generic;
using System.Threading;

public sealed class FrameEventBus : IDisposable
{
	private sealed class Subscription : IDisposable
	{
		private Action mUnsubscribe;

		public Subscription(Action unsubscribe)
		{
			mUnsubscribe = unsubscribe;
		}

		public void Dispose()
		{
			Interlocked.Exchange(ref mUnsubscribe, null)?.Invoke();
		}
	}

	private readonly object mLock = new();
	private readonly Dictionary<Type, Dictionary<long, Delegate>> mListeners = new();
	private readonly IFrameLogSink mLogSink;
	private long mNextListenerId;
	private bool mDisposed;

	public FrameEventBus(IFrameLogSink logSink = null)
	{
		mLogSink = logSink ?? FrameUnityLogSink.Instance;
	}

	public IDisposable Subscribe<T>(Action<T> listener)
	{
		if (listener == null)
		{
			throw new ArgumentNullException(nameof(listener));
		}

		long listenerId;
		lock (mLock)
		{
			throwIfDisposed();
			Type eventType = typeof(T);
			if (!mListeners.TryGetValue(eventType, out Dictionary<long, Delegate> listeners))
			{
				listeners = new Dictionary<long, Delegate>();
				mListeners.Add(eventType, listeners);
			}
			listenerId = ++mNextListenerId;
			listeners.Add(listenerId, listener);
		}

		return new Subscription(() => unsubscribe(typeof(T), listenerId));
	}

	public int Publish<T>(T message)
	{
		Delegate[] listeners;
		lock (mLock)
		{
			throwIfDisposed();
			if (!mListeners.TryGetValue(typeof(T), out Dictionary<long, Delegate> registered) || registered.Count == 0)
			{
				return 0;
			}
			listeners = new Delegate[registered.Count];
			registered.Values.CopyTo(listeners, 0);
		}

		int delivered = 0;
		foreach (Delegate listener in listeners)
		{
			try
			{
				((Action<T>)listener)(message);
				++delivered;
			}
			catch (Exception exception)
			{
				mLogSink.Write(new FrameLogRecord(FrameLogLevel.ERROR, nameof(FrameEventBus),
					$"Listener for {typeof(T).FullName} failed.", exception));
			}
		}
		return delivered;
	}

	public void Clear()
	{
		lock (mLock)
		{
			mListeners.Clear();
		}
	}

	public void Dispose()
	{
		lock (mLock)
		{
			if (mDisposed)
			{
				return;
			}
			mDisposed = true;
			mListeners.Clear();
		}
	}

	private void unsubscribe(Type eventType, long listenerId)
	{
		lock (mLock)
		{
			if (!mListeners.TryGetValue(eventType, out Dictionary<long, Delegate> listeners))
			{
				return;
			}
			listeners.Remove(listenerId);
			if (listeners.Count == 0)
			{
				mListeners.Remove(eventType);
			}
		}
	}

	private void throwIfDisposed()
	{
		if (mDisposed)
		{
			throw new ObjectDisposedException(nameof(FrameEventBus));
		}
	}
}
