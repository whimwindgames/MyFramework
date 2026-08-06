using System;
using System.Collections.Generic;
using NUnit.Framework;

public sealed class FrameNetworkLifecycleTests
{
	private sealed class CollectingLogSink : IFrameLogSink
	{
		public readonly List<FrameLogRecord> Records = new();
		public void Write(FrameLogRecord record) { Records.Add(record); }
	}

	[Test]
	public void RetryPolicyUsesCappedExponentialBackoff()
	{
		FrameRetryPolicy policy = new(6, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(8));
		Assert.That(policy.GetDelay(1), Is.EqualTo(TimeSpan.FromSeconds(1)));
		Assert.That(policy.GetDelay(2), Is.EqualTo(TimeSpan.FromSeconds(2)));
		Assert.That(policy.GetDelay(3), Is.EqualTo(TimeSpan.FromSeconds(4)));
		Assert.That(policy.GetDelay(4), Is.EqualTo(TimeSpan.FromSeconds(8)));
		Assert.That(policy.GetDelay(5), Is.EqualTo(TimeSpan.FromSeconds(8)));
		Assert.That(policy.GetDelay(6), Is.EqualTo(TimeSpan.FromSeconds(8)));
	}

	[Test]
	public void RetryPolicyRejectsAttemptsOutsideItsBudget()
	{
		FrameRetryPolicy policy = new(2, TimeSpan.Zero, TimeSpan.Zero);
		Assert.Throws<ArgumentOutOfRangeException>(() => policy.GetDelay(0));
		Assert.Throws<ArgumentOutOfRangeException>(() => policy.GetDelay(3));
	}

	[Test]
	public void NetworkStateChangesAreObservableAndDeduplicated()
	{
		CollectingLogSink log = new();
		using FrameRuntimeContext context = new("Test", log);
		List<FrameNetworkStateChanged> changes = new();
		context.Events.Subscribe<FrameNetworkStateChanged>(changes.Add);

		Assert.That(context.Network.SetState(FrameNetworkState.CONNECTING, "connect"), Is.True);
		Assert.That(context.Network.SetState(FrameNetworkState.CONNECTING, "connect"), Is.False);
		context.Network.MarkReady("room snapshot");

		Assert.That(changes, Has.Count.EqualTo(2));
		Assert.That(changes[0].PreviousState, Is.EqualTo(FrameNetworkState.DISCONNECTED));
		Assert.That(changes[1].State, Is.EqualTo(FrameNetworkState.READY));
	}

	[Test]
	public void RetrySchedulePublishesAttemptBudgetAndDelay()
	{
		using FrameRuntimeContext context = new("Test", new CollectingLogSink());
		FrameNetworkRetryScheduled observed = default;
		context.Events.Subscribe<FrameNetworkRetryScheduled>(value => observed = value);
		FrameRetryPolicy policy = new(6, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(8));

		context.Network.ReportRetryScheduled(4, policy, "socket closed");

		Assert.That(context.Network.State, Is.EqualTo(FrameNetworkState.RECONNECTING));
		Assert.That(observed.Attempt, Is.EqualTo(4));
		Assert.That(observed.MaxAttempts, Is.EqualTo(6));
		Assert.That(observed.Delay, Is.EqualTo(TimeSpan.FromSeconds(8)));
		Assert.That(observed.Reason, Is.EqualTo("socket closed"));
	}
}
