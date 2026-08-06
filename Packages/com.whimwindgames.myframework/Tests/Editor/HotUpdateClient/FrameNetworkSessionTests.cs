using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

public sealed class FrameNetworkSessionTests
{
	private interface ITestNetworkApi
	{
		int Value { get; }
	}

	private sealed class CollectingLogSink : IFrameLogSink
	{
		public readonly List<FrameLogRecord> Records = new();
		public void Write(FrameLogRecord record) { Records.Add(record); }
	}

	private sealed class TestProvider : IFrameNetworkProvider, ITestNetworkApi
	{
		private readonly Queue<bool> mRecoveryResults = new();

		public string Name => "test";
		public FrameNetworkState State { get; private set; } = FrameNetworkState.DISCONNECTED;
		public int Value => 7;
		public int TickCount { get; private set; }
		public int RecoveryCount { get; private set; }
		public int StopCount { get; private set; }
		public int DisposeCount { get; private set; }

		public event Action<FrameNetworkProviderStateChanged> StateChanged;
		public event Action<FrameNetworkInterrupted> Interrupted;

		public TestProvider(params bool[] recoveryResults)
		{
			foreach (bool result in recoveryResults)
				mRecoveryResults.Enqueue(result);
		}

		public void ChangeState(FrameNetworkState state, string reason = null)
		{
			State = state;
			StateChanged?.Invoke(new FrameNetworkProviderStateChanged(state, reason));
		}

		public void Interrupt(bool canRetry, string reason = "connection lost")
		{
			Interrupted?.Invoke(new FrameNetworkInterrupted(reason, canRetry));
		}

		public void Tick() { ++TickCount; }

		public Task<FrameNetworkRecoveryResult> RecoverAsync(int attempt,
			CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			++RecoveryCount;
			bool success = mRecoveryResults.Count > 0 && mRecoveryResults.Dequeue();
			if (success)
				ChangeState(FrameNetworkState.CONNECTED, $"recovered.{attempt}");
			return Task.FromResult(new FrameNetworkRecoveryResult(
				success, success ? FrameNetworkState.CONNECTED : FrameNetworkState.RECONNECTING,
				success ? $"recovered.{attempt}" : $"failed.{attempt}"));
		}

		public Task StopAsync(CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			++StopCount;
			ChangeState(FrameNetworkState.DISCONNECTED, "stopped");
			return Task.CompletedTask;
		}

		public void Dispose() { ++DisposeCount; }
	}

	[Test]
	public void ContextNetworkOwnsProviderApiTickAndDisposal()
	{
		TestProvider provider = new();
		FrameRuntimeContext context = new("Test", new CollectingLogSink());
		context.Network.UseProvider(provider);

		Assert.That(context.Network.GetApi<ITestNetworkApi>(), Is.SameAs(provider));
		Assert.That(context.Services.Get<FrameNetworkSession>(), Is.SameAs(context.Network));
		Assert.That(context.Services.Get<FrameNetworkLifecycle>(), Is.SameAs(context.Network.Lifecycle));

		context.Network.Tick();
		Assert.That(provider.TickCount, Is.EqualTo(1));

		context.Dispose();
		Assert.That(provider.DisposeCount, Is.EqualTo(1));
		Assert.Throws<ObjectDisposedException>(() => context.Network.Tick());
	}

	[Test]
	public async Task ExecutePublishesOperationLifecycleAndTracksActiveCount()
	{
		using FrameRuntimeContext context = new("Test", new CollectingLogSink());
		List<FrameNetworkOperationChanged> changes = new();
		context.Events.Subscribe<FrameNetworkOperationChanged>(changes.Add);

		int result = await context.Network.ExecuteAsync(
			"test.request", _ => Task.FromResult(42));

		Assert.That(result, Is.EqualTo(42));
		Assert.That(context.Network.ActiveOperationCount, Is.Zero);
		Assert.That(changes, Has.Count.EqualTo(2));
		Assert.That(changes[0].State, Is.EqualTo(FrameNetworkOperationState.STARTED));
		Assert.That(changes[1].State, Is.EqualTo(FrameNetworkOperationState.SUCCEEDED));
		Assert.That(changes[1].Operation, Is.EqualTo("test.request"));
		Assert.That(changes[1].ResultType, Is.EqualTo(typeof(int)));
	}

	[Test]
	public async Task RetryPolicyIsExecutedBySessionUntilProviderRecovers()
	{
		using FrameRuntimeContext context = new("Test", new CollectingLogSink());
		TestProvider provider = new(false, true);
		FrameRetryPolicy policy = new(3, TimeSpan.Zero, TimeSpan.Zero);
		FrameNetworkRecoveryCompleted completed = default;
		var retries = new List<FrameNetworkRetryScheduled>();
		context.Events.Subscribe<FrameNetworkRecoveryCompleted>(value => completed = value);
		context.Events.Subscribe<FrameNetworkRetryScheduled>(retries.Add);
		context.Network.UseProvider(provider, policy);

		provider.Interrupt(true);
		await context.Network.CurrentRecovery;

		Assert.That(provider.RecoveryCount, Is.EqualTo(2));
		Assert.That(retries, Has.Count.EqualTo(2));
		Assert.That(completed.Success, Is.True);
		Assert.That(completed.Attempts, Is.EqualTo(2));
		Assert.That(context.Network.State, Is.EqualTo(FrameNetworkState.CONNECTED));
	}

	[Test]
	public void NonRetryableInterruptionFailsWithoutCallingProviderRecovery()
	{
		using FrameRuntimeContext context = new("Test", new CollectingLogSink());
		TestProvider provider = new(true);
		context.Network.UseProvider(provider,
			new FrameRetryPolicy(1, TimeSpan.Zero, TimeSpan.Zero));

		provider.Interrupt(false, "session kicked");

		Assert.That(provider.RecoveryCount, Is.Zero);
		Assert.That(context.Network.State, Is.EqualTo(FrameNetworkState.FAILED));
		Assert.That(context.Network.Reason, Is.EqualTo("session kicked"));
	}

	[Test]
	public void DisconnectFollowingNonRetryableInterruptionDoesNotHideFailure()
	{
		using FrameRuntimeContext context = new("Test", new CollectingLogSink());
		TestProvider provider = new();
		context.Network.UseProvider(provider);

		provider.Interrupt(false, "account frozen");
		provider.ChangeState(FrameNetworkState.DISCONNECTED, "socket closed");

		Assert.That(context.Network.State, Is.EqualTo(FrameNetworkState.FAILED));
		Assert.That(context.Network.Reason, Is.EqualTo("account frozen"));

		provider.ChangeState(FrameNetworkState.CONNECTING, "user requested login");
		Assert.That(context.Network.State, Is.EqualTo(FrameNetworkState.CONNECTING));
	}

	[Test]
	public async Task StopAsyncUsesProviderThenPermanentlyStopsSession()
	{
		FrameRuntimeContext context = new("Test", new CollectingLogSink());
		TestProvider provider = new();
		context.Network.UseProvider(provider);

		await context.Network.StopAsync();

		Assert.That(provider.StopCount, Is.EqualTo(1));
		Assert.That(provider.DisposeCount, Is.EqualTo(1));
		Assert.Throws<ObjectDisposedException>(() => context.Network.GetApi<ITestNetworkApi>());
		context.Dispose();
		Assert.That(provider.DisposeCount, Is.EqualTo(1));
	}
}
