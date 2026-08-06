using System;

/// <summary>Deterministic capped exponential backoff shared by host networking implementations.</summary>
public sealed class FrameRetryPolicy
{
	public int MaxAttempts { get; }
	public TimeSpan BaseDelay { get; }
	public TimeSpan MaxDelay { get; }

	public FrameRetryPolicy(int maxAttempts, TimeSpan baseDelay, TimeSpan maxDelay)
	{
		if (maxAttempts <= 0)
		{
			throw new ArgumentOutOfRangeException(nameof(maxAttempts));
		}
		if (baseDelay < TimeSpan.Zero)
		{
			throw new ArgumentOutOfRangeException(nameof(baseDelay));
		}
		if (maxDelay < baseDelay)
		{
			throw new ArgumentOutOfRangeException(nameof(maxDelay));
		}

		MaxAttempts = maxAttempts;
		BaseDelay = baseDelay;
		MaxDelay = maxDelay;
	}

	public TimeSpan GetDelay(int attempt)
	{
		if (attempt < 1 || attempt > MaxAttempts)
		{
			throw new ArgumentOutOfRangeException(nameof(attempt));
		}

		long delayTicks = BaseDelay.Ticks;
		long maxTicks = MaxDelay.Ticks;
		for (int current = 1; current < attempt && delayTicks < maxTicks; ++current)
		{
			delayTicks = delayTicks > maxTicks / 2 ? maxTicks : delayTicks * 2;
		}
		return TimeSpan.FromTicks(Math.Min(delayTicks, maxTicks));
	}
}
