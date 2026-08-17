using System;
using System.Collections.Generic;
using System.Diagnostics;
using MyFramework.BuildStudio;

namespace MyFramework.BuildStudio.Editor
{
	public interface IMfProjectStructureContributor
	{
		string id { get; }
		void contribute(MfProjectStructureContext context, MfProjectStructure structure);
	}

	public interface IMfBuildProvider
	{
		string projectId { get; }
		void validate(MfBuildContext context);
		MfBuildExecutionResult run(MfBuildContext context);
	}

	public sealed class MfProjectStructureContext
	{
		public string projectRoot { get; internal set; }
		public string outputPath { get; internal set; }
	}

	public sealed class MfBuildArtifact
	{
		public string kind;
		public string path;

		public MfBuildArtifact() { }
		public MfBuildArtifact(string kind, string path)
		{
			this.kind = kind;
			this.path = path;
		}
	}

	public sealed class MfBuildExecutionResult
	{
		public string outputRoot;
		public readonly List<MfBuildArtifact> artifacts = new();
		public readonly Dictionary<string, string> values = new(StringComparer.Ordinal);
	}

	public sealed class MfBuildContext
	{
		readonly Action<MfBuildEvent> mEmit;
		readonly MfBuildReceipt mReceipt;

		public MfBuildJob job { get; }
		public MfProjectStructure structure { get; }

		internal MfBuildContext(MfBuildJob job, MfProjectStructure structure,
			MfBuildReceipt receipt, Action<MfBuildEvent> emit)
		{
			this.job = job;
			this.structure = structure;
			mReceipt = receipt;
			mEmit = emit;
		}

		public void stage(string id, string message, Action action)
		{
			if (action == null) throw new ArgumentNullException(nameof(action));
			stage<object>(id, message, () => { action(); return null; });
		}

		public T stage<T>(string id, string message, Func<T> action)
		{
			if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException(
				"Stage id is required.", nameof(id));
			if (action == null) throw new ArgumentNullException(nameof(action));
			Stopwatch watch = Stopwatch.StartNew();
			emit(id, "started", message, -1f);
			try
			{
				T result = action();
				watch.Stop();
				mReceipt.stages.Add(new MfBuildStageReceipt
				{
					id = id,
					status = "succeeded",
					message = message,
					durationMs = watch.ElapsedMilliseconds,
				});
				emit(id, "succeeded", message, 1f);
				return result;
			}
			catch (Exception exception)
			{
				watch.Stop();
				mReceipt.stages.Add(new MfBuildStageReceipt
				{
					id = id,
					status = "failed",
					message = exception.GetType().Name + ": " + exception.Message,
					durationMs = watch.ElapsedMilliseconds,
				});
				emit(id, "failed", exception.Message, -1f);
				throw;
			}
		}

		public void progress(string stageId, string message, float value)
		{
			emit(stageId, "progress", message, value);
		}

		void emit(string stageId, string state, string message, float progress)
		{
			mEmit?.Invoke(new MfBuildEvent
			{
				jobId = job.jobId,
				stage = stageId,
				state = state,
				message = message,
				timeUtc = DateTime.UtcNow.ToString("O"),
				progress = progress,
			});
		}
	}

	public static class MfBuildStudioRegistry
	{
		static readonly Dictionary<string, IMfProjectStructureContributor> sContributors =
			new(StringComparer.Ordinal);
		static readonly Dictionary<string, IMfBuildProvider> sProviders =
			new(StringComparer.Ordinal);

		public static void register(IMfProjectStructureContributor contributor)
		{
			if (contributor == null) throw new ArgumentNullException(nameof(contributor));
			string id = requireId(contributor.id, "Contributor");
			if (sContributors.TryGetValue(id, out var current) &&
				current.GetType() != contributor.GetType())
				throw new InvalidOperationException("Build Studio contributor id conflict: " + id);
			sContributors[id] = contributor;
		}

		public static void register(IMfBuildProvider provider)
		{
			if (provider == null) throw new ArgumentNullException(nameof(provider));
			string id = requireId(provider.projectId, "Build provider project");
			if (sProviders.TryGetValue(id, out var current) &&
				current.GetType() != provider.GetType())
				throw new InvalidOperationException("Build Studio provider id conflict: " + id);
			sProviders[id] = provider;
		}

		internal static IMfProjectStructureContributor[] contributors()
		{
			List<IMfProjectStructureContributor> result = new(sContributors.Values);
			result.Sort((left, right) => string.CompareOrdinal(left.id, right.id));
			return result.ToArray();
		}

		internal static IMfBuildProvider provider(string projectId)
		{
			return projectId != null && sProviders.TryGetValue(projectId, out var value) ?
				value : null;
		}

		static string requireId(string value, string label)
		{
			value = value?.Trim();
			if (string.IsNullOrEmpty(value)) throw new InvalidOperationException(label +
				" id is required.");
			return value;
		}
	}
}
