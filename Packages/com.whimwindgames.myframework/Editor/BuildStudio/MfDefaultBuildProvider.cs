using System;
using System.IO;
using MyFramework.BuildStudio;
using UnityEditor;
using UnityEditor.Build;

namespace MyFramework.BuildStudio.Editor
{
	internal static class MfDefaultBuildProvider
	{
		static readonly IMfBuildProvider sAssetBundles = new AssetBundleProvider();

		internal static IMfBuildProvider forJob(MfBuildJob job)
		{
			return job != null && job.action == "assets" ? sAssetBundles : null;
		}

		sealed class AssetBundleProvider : IMfBuildProvider
		{
			public string projectId => "myframework-default-assets";

			public void validate(MfBuildContext context)
			{
				if (context.job.action != "assets") throw new InvalidOperationException(
					"The default MyFramework provider only supports AssetBundle builds.");
				_ = output(context.job);
				AbBuild.check(roots: null);
			}

			public MfBuildExecutionResult run(MfBuildContext context)
			{
				string root = output(context.job);
				BuildTarget target = EditorUserBuildSettings.activeBuildTarget;
				if (!AbBuild.run(target, root, roots: null) ||
					!AbBuild.ready(root, roots: null))
					throw new BuildFailedException(
						"MyFramework AssetBundle build or verification failed: " + root);

				MfBuildExecutionResult result = new() { outputRoot = root };
				result.artifacts.Add(new MfBuildArtifact("asset-bundles", root));
				return result;
			}

			static string output(MfBuildJob job)
			{
				string value = job.outputRoot;
				if (string.IsNullOrWhiteSpace(value))
					value = Path.Combine(MfProjectStructureService.projectRoot(), "BuildOutput",
						"BuildStudio", job.profileId, job.jobId);
				if (!Path.IsPathRooted(value)) throw new InvalidDataException(
					"AssetBundle output must be an absolute directory.");
				return Path.GetFullPath(value);
			}
		}
	}
}
