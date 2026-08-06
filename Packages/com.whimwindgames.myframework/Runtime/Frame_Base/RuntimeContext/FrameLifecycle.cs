using System;

public enum FrameLifecycleState
{
	CREATED,
	STARTING,
	RUNNING,
	STOPPING,
	STOPPED,
	FAILED,
}

public readonly struct FrameLifecycleChanged
{
	public readonly long Sequence;
	public readonly FrameLifecycleState PreviousState;
	public readonly FrameLifecycleState State;
	public readonly string Phase;
	public readonly DateTimeOffset TimestampUtc;
	public readonly Exception Failure;

	public FrameLifecycleChanged(long sequence, FrameLifecycleState previousState,
		FrameLifecycleState state, string phase, Exception failure)
	{
		Sequence = sequence;
		PreviousState = previousState;
		State = state;
		Phase = phase ?? string.Empty;
		TimestampUtc = DateTimeOffset.UtcNow;
		Failure = failure;
	}
}

public sealed class FrameLifecycle
{
	private readonly object mLock = new();
	private readonly string mOwner;
	private readonly FrameEventBus mEvents;
	private readonly IFrameLogSink mLogSink;
	private FrameLifecycleState mState = FrameLifecycleState.CREATED;
	private string mPhase = string.Empty;
	private long mSequence;

	public FrameLifecycle(string owner, FrameEventBus events, IFrameLogSink logSink)
	{
		mOwner = string.IsNullOrWhiteSpace(owner) ? "Application" : owner;
		mEvents = events ?? throw new ArgumentNullException(nameof(events));
		mLogSink = logSink ?? throw new ArgumentNullException(nameof(logSink));
	}

	public FrameLifecycleState State
	{
		get
		{
			lock (mLock)
			{
				return mState;
			}
		}
	}

	public string Phase
	{
		get
		{
			lock (mLock)
			{
				return mPhase;
			}
		}
	}

	public void BeginStartup(string phase = "startup")
	{
		transition(FrameLifecycleState.STARTING, phase, null);
	}

	public void ReportPhase(string phase)
	{
		if (string.IsNullOrWhiteSpace(phase))
		{
			throw new ArgumentException("Lifecycle phase cannot be empty.", nameof(phase));
		}
		FrameLifecycleState state = State;
		if (state != FrameLifecycleState.STARTING && state != FrameLifecycleState.RUNNING)
		{
			throw new InvalidOperationException($"Cannot report phase while lifecycle is {state}.");
		}
		transition(state, phase, null);
	}

	public void MarkRunning(string phase = "running")
	{
		transition(FrameLifecycleState.RUNNING, phase, null);
	}

	public void Fail(Exception failure, string phase = "failed")
	{
		if (failure == null)
		{
			throw new ArgumentNullException(nameof(failure));
		}
		transition(FrameLifecycleState.FAILED, phase, failure);
	}

	public void Stop(string phase = "shutdown")
	{
		FrameLifecycleState state = State;
		if (state == FrameLifecycleState.STOPPED)
		{
			return;
		}
		if (state != FrameLifecycleState.STOPPING)
		{
			transition(FrameLifecycleState.STOPPING, phase, null);
		}
		transition(FrameLifecycleState.STOPPED, phase, null);
	}

	private void transition(FrameLifecycleState nextState, string phase, Exception failure)
	{
		FrameLifecycleChanged change;
		lock (mLock)
		{
			if (!isValidTransition(mState, nextState))
			{
				throw new InvalidOperationException($"Invalid lifecycle transition {mState} -> {nextState}.");
			}
			FrameLifecycleState previousState = mState;
			mState = nextState;
			mPhase = phase ?? string.Empty;
			change = new FrameLifecycleChanged(++mSequence, previousState, nextState, mPhase, failure);
		}

		FrameLogLevel level = failure == null ? FrameLogLevel.INFO : FrameLogLevel.ERROR;
		mLogSink.Write(new FrameLogRecord(level, mOwner,
			$"Lifecycle {change.PreviousState} -> {change.State}, phase '{change.Phase}'.", failure));
		mEvents.Publish(change);
	}

	private static bool isValidTransition(FrameLifecycleState current, FrameLifecycleState next)
	{
		switch (current)
		{
			case FrameLifecycleState.CREATED:
				return next == FrameLifecycleState.STARTING || next == FrameLifecycleState.STOPPING || next == FrameLifecycleState.FAILED;
			case FrameLifecycleState.STARTING:
				return next == FrameLifecycleState.STARTING || next == FrameLifecycleState.RUNNING ||
					next == FrameLifecycleState.STOPPING || next == FrameLifecycleState.FAILED;
			case FrameLifecycleState.RUNNING:
				return next == FrameLifecycleState.RUNNING || next == FrameLifecycleState.STOPPING || next == FrameLifecycleState.FAILED;
			case FrameLifecycleState.FAILED:
				return next == FrameLifecycleState.STOPPING;
			case FrameLifecycleState.STOPPING:
				return next == FrameLifecycleState.STOPPING || next == FrameLifecycleState.STOPPED || next == FrameLifecycleState.FAILED;
			default:
				return false;
		}
	}
}
