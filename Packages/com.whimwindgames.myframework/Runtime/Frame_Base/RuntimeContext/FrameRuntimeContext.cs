using System;

public sealed class FrameRuntimeContext : IDisposable
{
	private bool mDisposed;

	public string Name { get; }
	public IFrameLogSink LogSink { get; }
	public FrameServiceRegistry Services { get; }
	public FrameConfigurationStore Configuration { get; }
	public FrameEventBus Events { get; }
	public FrameLifecycle Lifecycle { get; }
	public FrameNetworkLifecycle Network { get; }

	public FrameRuntimeContext(string name, IFrameLogSink logSink = null)
	{
		Name = string.IsNullOrWhiteSpace(name) ? "Application" : name;
		LogSink = logSink ?? FrameUnityLogSink.Instance;
		Services = new FrameServiceRegistry();
		Configuration = new FrameConfigurationStore();
		Events = new FrameEventBus(LogSink);
		Lifecycle = new FrameLifecycle(Name, Events, LogSink);
		Network = new FrameNetworkLifecycle($"{Name}.Network", Events, LogSink);

		Services.Set(this);
		Services.Set(Services);
		Services.Set(Configuration);
		Services.Set(Events);
		Services.Set(Lifecycle);
		Services.Set(Network);
	}

	public void Log(FrameLogLevel level, string message, Exception exception = null, string category = null)
	{
		throwIfDisposed();
		LogSink.Write(new FrameLogRecord(level, category ?? Name, message, exception));
	}

	public void BeginStartup(string phase = "startup")
	{
		throwIfDisposed();
		Lifecycle.BeginStartup(phase);
	}

	public void ReportPhase(string phase)
	{
		throwIfDisposed();
		Lifecycle.ReportPhase(phase);
	}

	public void MarkRunning(string phase = "running")
	{
		throwIfDisposed();
		Lifecycle.MarkRunning(phase);
	}

	public void Fail(Exception failure, string phase = "failed")
	{
		throwIfDisposed();
		Lifecycle.Fail(failure, phase);
	}

	public void Shutdown(string phase = "shutdown")
	{
		throwIfDisposed();
		Lifecycle.Stop(phase);
	}

	public void Dispose()
	{
		if (mDisposed)
		{
			return;
		}

		try
		{
			Lifecycle.Stop("dispose");
		}
		finally
		{
			Services.Clear();
			Configuration.Clear();
			Events.Dispose();
			mDisposed = true;
		}
	}

	private void throwIfDisposed()
	{
		if (mDisposed)
		{
			throw new ObjectDisposedException(nameof(FrameRuntimeContext));
		}
	}
}
