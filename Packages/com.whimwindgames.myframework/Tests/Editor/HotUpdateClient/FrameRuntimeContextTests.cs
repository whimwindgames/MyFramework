using System;
using System.Collections.Generic;
using NUnit.Framework;

public sealed class FrameRuntimeContextTests
{
	private sealed class CollectingLogSink : IFrameLogSink
	{
		public readonly List<FrameLogRecord> Records = new();
		public void Write(FrameLogRecord record) { Records.Add(record); }
	}

	private sealed class TestService
	{
		public readonly int Value;
		public TestService(int value) { Value = value; }
	}

	private sealed class TestConfiguration
	{
		public readonly string Value;
		public TestConfiguration(string value) { Value = value; }
	}

	[Test]
	public void ServiceRegistrySupportsStrictRegistrationAndExplicitReplacement()
	{
		FrameServiceRegistry services = new();
		TestService first = new(1);
		TestService second = new(2);
		services.Register(first);

		Assert.That(services.Get<TestService>(), Is.SameAs(first));
		Assert.Throws<InvalidOperationException>(() => services.Register(second));
		services.Set(second);
		Assert.That(services.Get<TestService>().Value, Is.EqualTo(2));
	}

	[Test]
	public void ServiceRegistryRejectsMismatchedRuntimeTypes()
	{
		FrameServiceRegistry services = new();
		Assert.Throws<ArgumentException>(() => services.Register(typeof(IDisposable), new object()));
	}

	[Test]
	public void ConfigurationStoreSeparatesDefaultAndNamedValues()
	{
		FrameConfigurationStore configuration = new();
		configuration.Set(new TestConfiguration("default"));
		configuration.Set(new TestConfiguration("production"), "production");

		Assert.That(configuration.Get<TestConfiguration>().Value, Is.EqualTo("default"));
		Assert.That(configuration.Get<TestConfiguration>("production").Value, Is.EqualTo("production"));
		Assert.That(configuration.Version, Is.EqualTo(2));
	}

	[Test]
	public void EventBusIsolatesListenerFailuresAndSupportsUnsubscribe()
	{
		CollectingLogSink log = new();
		using FrameEventBus events = new(log);
		int received = 0;
		events.Subscribe<int>(_ => throw new InvalidOperationException("listener failed"));
		IDisposable subscription = events.Subscribe<int>(value => received += value);

		Assert.That(events.Publish(3), Is.EqualTo(1));
		Assert.That(received, Is.EqualTo(3));
		Assert.That(log.Records, Has.Count.EqualTo(1));
		subscription.Dispose();
		Assert.That(events.Publish(2), Is.EqualTo(0));
		Assert.That(received, Is.EqualTo(3));
	}

	[Test]
	public void LifecyclePublishesOrderedStartupPhases()
	{
		CollectingLogSink log = new();
		using FrameRuntimeContext context = new("FishingMobile", log);
		List<FrameLifecycleChanged> changes = new();
		context.Events.Subscribe<FrameLifecycleChanged>(changes.Add);

		context.BeginStartup("bootstrap");
		context.ReportPhase("network");
		context.MarkRunning("ready");

		Assert.That(context.Lifecycle.State, Is.EqualTo(FrameLifecycleState.RUNNING));
		Assert.That(changes, Has.Count.EqualTo(3));
		Assert.That(changes[0].Sequence, Is.EqualTo(1));
		Assert.That(changes[1].Phase, Is.EqualTo("network"));
		Assert.That(changes[2].State, Is.EqualTo(FrameLifecycleState.RUNNING));
	}

	[Test]
	public void LifecycleRejectsRunningBeforeStartup()
	{
		using FrameRuntimeContext context = new("Test", new CollectingLogSink());
		Assert.Throws<InvalidOperationException>(() => context.MarkRunning());
	}

	[Test]
	public void DisposeStopsLifecycleAndClearsOwnedStores()
	{
		FrameRuntimeContext context = new("Test", new CollectingLogSink());
		context.BeginStartup();
		context.Services.Set(new TestService(1));
		context.Configuration.Set(new TestConfiguration("value"));

		context.Dispose();

		Assert.That(context.Lifecycle.State, Is.EqualTo(FrameLifecycleState.STOPPED));
		Assert.That(context.Services.Count, Is.Zero);
		Assert.That(context.Configuration.Count, Is.Zero);
		Assert.Throws<ObjectDisposedException>(() => context.ReportPhase("late"));
	}
}
