using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

public sealed class FrameViewBridgeTests
{
	private sealed class CollectingLogSink : IFrameLogSink
	{
		public readonly List<FrameLogRecord> Records = new();
		public void Write(FrameLogRecord record) { Records.Add(record); }
	}

	private sealed class FakeViewAdapter : IFrameViewAdapter
	{
		public bool BackHandled = true;
		public Exception ShowFailure;
		public int ShowCount;
		public int HideCount;

		public Task<FrameViewHandle> ShowAsync(FrameViewRequest request,
			CancellationToken cancellationToken = default)
		{
			++ShowCount;
			if (ShowFailure != null)
			{
				return Task.FromException<FrameViewHandle>(ShowFailure);
			}
			return Task.FromResult(new FrameViewHandle(request.Route, request.Layer, new object()));
		}

		public Task HideAsync(FrameViewHandle handle,
			CancellationToken cancellationToken = default)
		{
			++HideCount;
			return Task.CompletedTask;
		}

		public Task<bool> BackAsync(CancellationToken cancellationToken = default)
		{
			return Task.FromResult(BackHandled);
		}
	}

	[Test]
	public async Task RouterDelegatesToHostAndPublishesViewLifecycle()
	{
		using FrameRuntimeContext context = new("Test", new CollectingLogSink());
		FakeViewAdapter adapter = new();
		List<FrameViewStateChanged> changes = new();
		context.Events.Subscribe<FrameViewStateChanged>(changes.Add);
		context.Views.UseAdapter(adapter);

		FrameViewHandle handle = await context.Views.ShowAsync(
			new FrameViewRequest("fishing.game-hud", FrameViewLayer.HUD));
		await context.Views.HideAsync(handle);

		Assert.That(adapter.ShowCount, Is.EqualTo(1));
		Assert.That(adapter.HideCount, Is.EqualTo(1));
		Assert.That(changes.ConvertAll(change => change.State), Is.EqualTo(new[]
		{
			FrameViewState.OPENING,
			FrameViewState.VISIBLE,
			FrameViewState.CLOSING,
			FrameViewState.HIDDEN,
		}));
	}

	[Test]
	public async Task RouterKeepsBackNavigationInTheHost()
	{
		using FrameRuntimeContext context = new("Test", new CollectingLogSink());
		FakeViewAdapter adapter = new() { BackHandled = true };
		context.Views.UseAdapter(adapter);

		Assert.That(await context.Views.BackAsync(), Is.True);
	}

	[Test]
	public void RouterPublishesFailureAndPreservesTheException()
	{
		CollectingLogSink log = new();
		using FrameRuntimeContext context = new("Test", log);
		FakeViewAdapter adapter = new() { ShowFailure = new InvalidOperationException("missing route") };
		FrameViewStateChanged observed = default;
		context.Events.Subscribe<FrameViewStateChanged>(change => observed = change);
		context.Views.UseAdapter(adapter);

		InvalidOperationException exception = Assert.ThrowsAsync<InvalidOperationException>(async () =>
			await context.Views.ShowAsync(new FrameViewRequest(
				"missing", FrameViewLayer.WINDOW)));

		Assert.That(exception.Message, Is.EqualTo("missing route"));
		Assert.That(observed.State, Is.EqualTo(FrameViewState.FAILED));
		Assert.That(observed.Error, Is.EqualTo("missing route"));
		Assert.That(log.Records, Has.Count.EqualTo(1));
	}
}
