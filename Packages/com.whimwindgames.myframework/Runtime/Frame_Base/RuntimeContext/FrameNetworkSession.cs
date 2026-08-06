using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

public readonly struct FrameNetworkProviderStateChanged
{
	public readonly FrameNetworkState State;
	public readonly string Reason;

	public FrameNetworkProviderStateChanged(FrameNetworkState state, string reason = null)
	{
		State = state;
		Reason = reason ?? string.Empty;
	}
}

public readonly struct FrameNetworkInterrupted
{
	public readonly string Reason;
	public readonly bool CanRetry;
	public readonly Exception Failure;
	public readonly DateTimeOffset TimestampUtc;

	public FrameNetworkInterrupted(string reason, bool canRetry, Exception failure = null)
	{
		Reason = reason ?? string.Empty;
		CanRetry = canRetry;
		Failure = failure;
		TimestampUtc = DateTimeOffset.UtcNow;
	}
}

public readonly struct FrameNetworkRecoveryResult
{
	public readonly bool Success;
	public readonly FrameNetworkState State;
	public readonly string Reason;

	public FrameNetworkRecoveryResult(bool success, FrameNetworkState state, string reason = null)
	{
		Success = success;
		State = state;
		Reason = reason ?? string.Empty;
	}
}

public readonly struct FrameNetworkRecoveryCompleted
{
	public readonly bool Success;
	public readonly int Attempts;
	public readonly string Reason;
	public readonly DateTimeOffset TimestampUtc;

	public FrameNetworkRecoveryCompleted(bool success, int attempts, string reason)
	{
		Success = success;
		Attempts = attempts;
		Reason = reason ?? string.Empty;
		TimestampUtc = DateTimeOffset.UtcNow;
	}
}

public enum FrameNetworkOperationState
{
	STARTED,
	SUCCEEDED,
	CANCELED,
	FAILED,
}

public readonly struct FrameNetworkOperationChanged
{
	public readonly long Sequence;
	public readonly string Operation;
	public readonly Type ResultType;
	public readonly FrameNetworkOperationState State;
	public readonly TimeSpan Duration;
	public readonly string Error;
	public readonly DateTimeOffset TimestampUtc;

	public FrameNetworkOperationChanged(long sequence, string operation, Type resultType,
		FrameNetworkOperationState state, TimeSpan duration, string error = null)
	{
		Sequence = sequence;
		Operation = operation ?? string.Empty;
		ResultType = resultType;
		State = state;
		Duration = duration;
		Error = error ?? string.Empty;
		TimestampUtc = DateTimeOffset.UtcNow;
	}
}

/// <summary>
/// Host-specific transport and protocol implementation owned by <see cref="FrameNetworkSession"/>.
/// Providers serialize their callbacks onto the thread that owns <see cref="Tick"/> when required by
/// the host engine. A provider may also implement one or more host APIs returned by GetApi.
/// </summary>
public interface IFrameNetworkProvider : IDisposable
{
	string Name { get; }
	FrameNetworkState State { get; }
	event Action<FrameNetworkProviderStateChanged> StateChanged;
	event Action<FrameNetworkInterrupted> Interrupted;
	void Tick();
	Task<FrameNetworkRecoveryResult> RecoverAsync(int attempt,
		CancellationToken cancellationToken = default);
	Task StopAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Canonical network owner for a runtime context. It owns the provider, request cancellation,
/// observable operation state, retry scheduling, recovery execution, ticking and teardown. Game
/// protocols remain in host providers, while gameplay code uses a single context-owned session.
/// </summary>
public sealed class FrameNetworkSession : IDisposable
{
	private readonly object mLock = new();
	private readonly string mOwner;
	private readonly FrameEventBus mEvents;
	private readonly IFrameLogSink mLogSink;
	private readonly CancellationTokenSource mLifetimeCts = new();
	private IFrameNetworkProvider mProvider;
	private CancellationTokenSource mProviderCts;
	private FrameRetryPolicy mRetryPolicy;
	private Task mCurrentRecovery = Task.CompletedTask;
	private long mOperationSequence;
	private int mActiveOperationCount;
	private int mStopped;

	public FrameNetworkLifecycle Lifecycle { get; }
	public FrameNetworkState State => Lifecycle.State;
	public string Reason => Lifecycle.Reason;
	public int ActiveOperationCount => Volatile.Read(ref mActiveOperationCount);

	public IFrameNetworkProvider Provider
	{
		get
		{
			lock (mLock)
			{
				return mProvider;
			}
		}
	}

	public FrameRetryPolicy RetryPolicy
	{
		get
		{
			lock (mLock)
			{
				return mRetryPolicy;
			}
		}
	}

	public Task CurrentRecovery
	{
		get
		{
			lock (mLock)
			{
				return mCurrentRecovery;
			}
		}
	}

	public FrameNetworkSession(string owner, FrameEventBus events,
		IFrameLogSink logSink, FrameNetworkLifecycle lifecycle = null)
	{
		mOwner = string.IsNullOrWhiteSpace(owner) ? "Network" : owner;
		mEvents = events ?? throw new ArgumentNullException(nameof(events));
		mLogSink = logSink ?? throw new ArgumentNullException(nameof(logSink));
		Lifecycle = lifecycle ?? new FrameNetworkLifecycle(mOwner, mEvents, mLogSink);
	}

	public void UseProvider(IFrameNetworkProvider provider,
		FrameRetryPolicy retryPolicy = null, bool replace = false)
	{
		throwIfStopped();
		if (provider == null)
		{
			throw new ArgumentNullException(nameof(provider));
		}

		IFrameNetworkProvider previous = null;
		CancellationTokenSource previousCts = null;
		lock (mLock)
		{
			if (mProvider != null && !replace)
			{
				throw new InvalidOperationException("A network provider is already installed.");
			}
			if (ReferenceEquals(mProvider, provider))
			{
				mRetryPolicy = retryPolicy;
				return;
			}

			previous = mProvider;
			previousCts = mProviderCts;
			if (previous != null)
				detachProvider(previous);

			mProvider = provider;
			mRetryPolicy = retryPolicy;
			mProviderCts = CancellationTokenSource.CreateLinkedTokenSource(mLifetimeCts.Token);
			attachProvider(provider);
		}

		cancelAndDispose(previousCts);
		previous?.Dispose();
		Lifecycle.SetState(provider.State, $"provider.{provider.Name}.installed");
	}

	public T GetApi<T>() where T : class
	{
		throwIfStopped();
		IFrameNetworkProvider provider = Provider ??
			throw new InvalidOperationException("No network provider is installed.");
		return provider as T ?? throw new InvalidOperationException(
			$"Network provider '{provider.Name}' does not implement {typeof(T).FullName}.");
	}

	public async Task<TResult> ExecuteAsync<TResult>(string operation,
		Func<CancellationToken, Task<TResult>> execute,
		CancellationToken cancellationToken = default)
	{
		throwIfStopped();
		if (string.IsNullOrWhiteSpace(operation))
		{
			throw new ArgumentException("Network operation cannot be empty.", nameof(operation));
		}
		if (execute == null)
		{
			throw new ArgumentNullException(nameof(execute));
		}

		using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
			mLifetimeCts.Token, cancellationToken);
		long started = Stopwatch.GetTimestamp();
		Interlocked.Increment(ref mActiveOperationCount);
		publishOperation<TResult>(operation, FrameNetworkOperationState.STARTED, TimeSpan.Zero);
		try
		{
			linkedCts.Token.ThrowIfCancellationRequested();
			TResult result = await execute(linkedCts.Token);
			publishOperation<TResult>(operation, FrameNetworkOperationState.SUCCEEDED,
				elapsedSince(started));
			return result;
		}
		catch (OperationCanceledException)
		{
			publishOperation<TResult>(operation, FrameNetworkOperationState.CANCELED,
				elapsedSince(started));
			throw;
		}
		catch (Exception exception)
		{
			publishOperation<TResult>(operation, FrameNetworkOperationState.FAILED,
				elapsedSince(started), exception.Message);
			mLogSink.Write(new FrameLogRecord(FrameLogLevel.ERROR, mOwner,
				$"Network operation '{operation}' failed.", exception));
			throw;
		}
		finally
		{
			Interlocked.Decrement(ref mActiveOperationCount);
		}
	}

	public bool SetState(FrameNetworkState state, string reason = null)
	{
		return Lifecycle.SetState(state, reason);
	}

	public FrameNetworkRetryScheduled ReportRetryScheduled(int attempt,
		FrameRetryPolicy policy, string reason = null)
	{
		return Lifecycle.ReportRetryScheduled(attempt, policy, reason);
	}

	public FrameNetworkRetryScheduled ReportRetryScheduled(int attempt, int maxAttempts,
		TimeSpan delay, string reason = null)
	{
		return Lifecycle.ReportRetryScheduled(attempt, maxAttempts, delay, reason);
	}

	public void BeginRecovery(string reason = null)
	{
		Lifecycle.BeginRecovery(reason);
	}

	public void MarkReady(string reason = null)
	{
		Lifecycle.MarkReady(reason);
	}

	public void Tick()
	{
		throwIfStopped();
		Provider?.Tick();
	}

	public async Task StopAsync(CancellationToken cancellationToken = default)
	{
		if (Volatile.Read(ref mStopped) != 0)
		{
			return;
		}
		Lifecycle.SetState(FrameNetworkState.STOPPING, "network session stop requested");
		IFrameNetworkProvider provider = Provider;
		if (provider != null)
			await provider.StopAsync(cancellationToken);
		Shutdown();
	}

	public void Shutdown()
	{
		if (Interlocked.Exchange(ref mStopped, 1) != 0)
		{
			return;
		}

		Lifecycle.SetState(FrameNetworkState.STOPPING, "network session shutdown");
		mLifetimeCts.Cancel();
		IFrameNetworkProvider provider;
		CancellationTokenSource providerCts;
		lock (mLock)
		{
			provider = mProvider;
			providerCts = mProviderCts;
			if (provider != null)
				detachProvider(provider);
			mProvider = null;
			mProviderCts = null;
			mRetryPolicy = null;
		}
		cancelAndDispose(providerCts);
		provider?.Dispose();
		mLifetimeCts.Dispose();
	}

	public void Dispose()
	{
		Shutdown();
	}

	private void attachProvider(IFrameNetworkProvider provider)
	{
		provider.StateChanged += onProviderStateChanged;
		provider.Interrupted += onProviderInterrupted;
	}

	private void detachProvider(IFrameNetworkProvider provider)
	{
		provider.StateChanged -= onProviderStateChanged;
		provider.Interrupted -= onProviderInterrupted;
	}

	private void onProviderStateChanged(FrameNetworkProviderStateChanged change)
	{
		// 顶号、冻结等不可恢复中断通常紧接一个底层 socket Disconnected 通知。
		// 该传输事实不能覆盖已经确定的 FAILED 业务终态；显式 Connecting/Reconnecting
		// 仍可在用户发起新会话时离开失败态。
		if (Lifecycle.State == FrameNetworkState.FAILED &&
			change.State == FrameNetworkState.DISCONNECTED)
		{
			return;
		}
		Lifecycle.SetState(change.State, change.Reason);
	}

	private void onProviderInterrupted(FrameNetworkInterrupted interruption)
	{
		mEvents.Publish(interruption);
		FrameRetryPolicy retryPolicy = RetryPolicy;
		if (!interruption.CanRetry || retryPolicy == null)
		{
			Lifecycle.SetState(FrameNetworkState.FAILED, interruption.Reason);
			return;
		}

		lock (mLock)
		{
			if (!mCurrentRecovery.IsCompleted)
			{
				return;
			}
			CancellationToken token = mProviderCts?.Token ?? mLifetimeCts.Token;
			mCurrentRecovery = recoverAsync(interruption, retryPolicy, token);
		}
	}

	private async Task recoverAsync(FrameNetworkInterrupted interruption,
		FrameRetryPolicy retryPolicy, CancellationToken cancellationToken)
	{
		string lastReason = interruption.Reason;
		int attempts = 0;
		try
		{
			for (int attempt = 1; attempt <= retryPolicy.MaxAttempts; ++attempt)
			{
				attempts = attempt;
				TimeSpan delay = retryPolicy.GetDelay(attempt);
				Lifecycle.ReportRetryScheduled(attempt, retryPolicy, lastReason);
				if (delay > TimeSpan.Zero)
					await Task.Delay(delay, cancellationToken);

				IFrameNetworkProvider provider = Provider;
				if (provider == null)
					throw new InvalidOperationException("Network provider was removed during recovery.");
				try
				{
					FrameNetworkRecoveryResult result =
						await provider.RecoverAsync(attempt, cancellationToken);
					lastReason = result.Reason;
					if (!result.Success)
						continue;

					Lifecycle.SetState(result.State, result.Reason);
					mEvents.Publish(new FrameNetworkRecoveryCompleted(true, attempt, result.Reason));
					return;
				}
				catch (OperationCanceledException)
				{
					throw;
				}
				catch (Exception exception)
				{
					lastReason = exception.Message;
					mLogSink.Write(new FrameLogRecord(FrameLogLevel.WARNING, mOwner,
						$"Network recovery attempt {attempt}/{retryPolicy.MaxAttempts} failed.",
						exception));
				}
			}

			Lifecycle.SetState(FrameNetworkState.FAILED, lastReason);
			mEvents.Publish(new FrameNetworkRecoveryCompleted(false, attempts, lastReason));
		}
		catch (OperationCanceledException)
		{
			if (Volatile.Read(ref mStopped) == 0)
				Lifecycle.SetState(FrameNetworkState.DISCONNECTED, "network recovery canceled");
		}
	}

	private void publishOperation<TResult>(string operation, FrameNetworkOperationState state,
		TimeSpan duration, string error = null)
	{
		if (Volatile.Read(ref mStopped) != 0)
		{
			return;
		}
		mEvents.Publish(new FrameNetworkOperationChanged(
			Interlocked.Increment(ref mOperationSequence), operation, typeof(TResult),
			state, duration, error));
	}

	private static void cancelAndDispose(CancellationTokenSource cancellation)
	{
		if (cancellation == null)
		{
			return;
		}
		try
		{
			cancellation.Cancel();
		}
		finally
		{
			cancellation.Dispose();
		}
	}

	private static TimeSpan elapsedSince(long startedTimestamp)
	{
		long elapsedTicks = Stopwatch.GetTimestamp() - startedTimestamp;
		return TimeSpan.FromSeconds((double)elapsedTicks / Stopwatch.Frequency);
	}

	private void throwIfStopped()
	{
		if (Volatile.Read(ref mStopped) != 0)
		{
			throw new ObjectDisposedException(nameof(FrameNetworkSession));
		}
	}
}
