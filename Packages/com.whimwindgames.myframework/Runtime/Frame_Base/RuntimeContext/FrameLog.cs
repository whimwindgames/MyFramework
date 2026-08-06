using System;
using UnityEngine;

public enum FrameLogLevel
{
	TRACE,
	DEBUG,
	INFO,
	WARNING,
	ERROR,
	EXCEPTION,
}

public readonly struct FrameLogRecord
{
	public readonly DateTimeOffset TimestampUtc;
	public readonly FrameLogLevel Level;
	public readonly string Category;
	public readonly string Message;
	public readonly Exception Exception;

	public FrameLogRecord(FrameLogLevel level, string category, string message, Exception exception = null)
	{
		TimestampUtc = DateTimeOffset.UtcNow;
		Level = level;
		Category = category ?? string.Empty;
		Message = message ?? string.Empty;
		Exception = exception;
	}

	public override string ToString()
	{
		string prefix = string.IsNullOrEmpty(Category) ? string.Empty : $"[{Category}] ";
		return prefix + Message;
	}
}

public interface IFrameLogSink
{
	void Write(FrameLogRecord record);
}

public sealed class FrameUnityLogSink : IFrameLogSink
{
	public static readonly FrameUnityLogSink Instance = new();

	private FrameUnityLogSink() { }

	public void Write(FrameLogRecord record)
	{
		string message = record.ToString();
		switch (record.Level)
		{
			case FrameLogLevel.WARNING:
				Debug.LogWarning(message);
				break;
			case FrameLogLevel.ERROR:
				Debug.LogError(record.Exception == null ? message : $"{message}\n{record.Exception}");
				break;
			case FrameLogLevel.EXCEPTION:
				if (record.Exception != null)
				{
					Debug.LogError(message);
					Debug.LogException(record.Exception);
				}
				else
				{
					Debug.LogError(message);
				}
				break;
			default:
				Debug.Log(message);
				break;
		}
	}
}
