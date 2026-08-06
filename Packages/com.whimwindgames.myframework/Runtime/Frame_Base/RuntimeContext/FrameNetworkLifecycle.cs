using System;

public enum FrameNetworkState
{
	DISCONNECTED,
	CONNECTING,
	CONNECTED,
	JOINING,
	READY,
	RECONNECTING,
	RECOVERING,
	STOPPING,
	FAILED,
}

public readonly struct FrameNetworkStateChanged
{
	public readonly long Sequence;
	public readonly FrameNetworkState PreviousState;
	public readonly FrameNetworkState State;
	public readonly string Reason;
	public readonly DateTimeOffset TimestampUtc;

	public FrameNetworkStateChanged(long sequence, FrameNetworkState previousState,
		FrameNetworkState state, string reason)
	{
		Sequence = sequence;
		PreviousState = previousState;
		State = state;
		Reason = reason ?? string.Empty;
		TimestampUtc = DateTimeOffset.UtcNow;
	}
}

public readonly struct FrameNetworkRetryScheduled
{
	public readonly long Sequence;
	public readonly int Attempt;
	public readonly int MaxAttempts;
	public readonly TimeSpan Delay;
	public readonly string Reason;
	public readonly DateTimeOffset TimestampUtc;

	public FrameNetworkRetryScheduled(long sequence, int attempt, int maxAttempts,
		TimeSpan delay, string reason)
	{
		Sequence = sequence;
		Attempt = attempt;
		MaxAttempts = maxAttempts;
		Delay = delay;
		Reason = reason ?? string.Empty;
		TimestampUtc = DateTimeOffset.UtcNow;
	}
}

/// <summary>
/// Observable network lifecycle for mature host projects. It describes state and retry timing only;
/// transport, protocol, authentication, room recovery and request ownership stay in the host.
/// </summary>
public sealed class FrameNetworkLifecycle
{
	private readonly object mLock = new();
	private readonly string mOwner;
	private readonly FrameEventBus mEvents;
	private readonly IFrameLogSink mLogSink;
	private FrameNetworkState mState = FrameNetworkState.DISCONNECTED;
	private string mReason = string.Empty;
	private long mSequence;

	public FrameNetworkLifecycle(string owner, FrameEventBus events, IFrameLogSink logSink)
	{
		mOwner = string.IsNullOrWhiteSpace(owner) ? "Network" : owner;
		mEvents = events ?? throw new ArgumentNullException(nameof(events));
		mLogSink = logSink ?? throw new ArgumentNullException(nameof(logSink));
	}

	public FrameNetworkState State
	{
		get
		{
			lock (mLock)
			{
				return mState;
			}
		}
	}

	public string Reason
	{
		get
		{
			lock (mLock)
			{
				return mReason;
			}
		}
	}

	public bool SetState(FrameNetworkState state, string reason = null)
	{
		FrameNetworkStateChanged change;
		lock (mLock)
		{
			string nextReason = reason ?? string.Empty;
			if (mState == state && string.Equals(mReason, nextReason, StringComparison.Ordinal))
			{
				return false;
			}
			FrameNetworkState previous = mState;
			mState = state;
			mReason = nextReason;
			change = new FrameNetworkStateChanged(++mSequence, previous, state, nextReason);
		}

		FrameLogLevel level = state == FrameNetworkState.FAILED ? FrameLogLevel.ERROR : FrameLogLevel.INFO;
		mLogSink.Write(new FrameLogRecord(level, mOwner,
			$"Network {change.PreviousState} -> {change.State}, reason '{change.Reason}'."));
		mEvents.Publish(change);
		return true;
	}

	public FrameNetworkRetryScheduled ReportRetryScheduled(int attempt,
		FrameRetryPolicy policy, string reason = null)
	{
		if (policy == null)
		{
			throw new ArgumentNullException(nameof(policy));
		}
		return ReportRetryScheduled(attempt, policy.MaxAttempts, policy.GetDelay(attempt), reason);
	}

	public FrameNetworkRetryScheduled ReportRetryScheduled(int attempt, int maxAttempts,
		TimeSpan delay, string reason = null)
	{
		if (maxAttempts <= 0)
		{
			throw new ArgumentOutOfRangeException(nameof(maxAttempts));
		}
		if (attempt < 1 || attempt > maxAttempts)
		{
			throw new ArgumentOutOfRangeException(nameof(attempt));
		}
		if (delay < TimeSpan.Zero)
		{
			throw new ArgumentOutOfRangeException(nameof(delay));
		}

		SetState(FrameNetworkState.RECONNECTING, reason);
		FrameNetworkRetryScheduled scheduled;
		lock (mLock)
		{
			scheduled = new FrameNetworkRetryScheduled(++mSequence, attempt, maxAttempts, delay, reason);
		}
		mLogSink.Write(new FrameLogRecord(FrameLogLevel.WARNING, mOwner,
			$"Network retry {attempt}/{maxAttempts} scheduled after {delay.TotalMilliseconds:0} ms, reason '{scheduled.Reason}'."));
		mEvents.Publish(scheduled);
		return scheduled;
	}

	public void BeginRecovery(string reason = null)
	{
		SetState(FrameNetworkState.RECOVERING, reason);
	}

	public void MarkReady(string reason = null)
	{
		SetState(FrameNetworkState.READY, reason);
	}
}
