using System;
using System.Collections.Generic;

namespace MyFramework.BuildStudio
{
	public static class MfBuildSchema
	{
		public const int Project = 1;
		public const int Job = 1;
		public const int Receipt = 1;
		public const int Event = 1;
		public const string ProjectFileName = "MyFrameworkProject.json";
	}

	[Serializable]
	public sealed class MfProjectStructure
	{
		public int schema = MfBuildSchema.Project;
		public string structureHash = string.Empty;
		public MfProjectIdentity project = new();
		public MfUnityInfo unity = new();
		public MfFrameworkInfo framework = new();
		public List<MfPackageInfo> packages = new();
		public List<MfSceneInfo> scenes = new();
		public MfManagedCodeInfo managedCode = new();
		public MfContentInfo content = new();
		public List<MfBuildProfile> profiles = new();
		public List<MfModuleInfo> modules = new();
		public List<string> contributors = new();
		public Dictionary<string, string> properties = new();
	}

	[Serializable]
	public sealed class MfProjectIdentity
	{
		public string id = string.Empty;
		public string displayName = string.Empty;
		public string kind = "application";
	}

	[Serializable]
	public sealed class MfUnityInfo
	{
		public string version = string.Empty;
		public string colorSpace = string.Empty;
		public string defaultRenderPipeline = string.Empty;
	}

	[Serializable]
	public sealed class MfFrameworkInfo
	{
		public string package = "com.whimwindgames.myframework";
		public string version = string.Empty;
		public int assetBundleSchema;
		public int releaseSchema;
	}

	[Serializable]
	public sealed class MfPackageInfo
	{
		public string name = string.Empty;
		public string version = string.Empty;
		public string source = string.Empty;
		public bool direct;
	}

	[Serializable]
	public sealed class MfSceneInfo
	{
		public string path = string.Empty;
		public string guid = string.Empty;
		public bool enabled;
		public string role = string.Empty;
	}

	[Serializable]
	public sealed class MfManagedCodeInfo
	{
		public bool hybridClrEnabled;
		public bool obfuzEnabled;
		public string entryAssembly = string.Empty;
		public List<string> hotAssemblies = new();
		public List<string> preservedHotAssemblies = new();
		public List<string> aotAssemblies = new();
	}

	[Serializable]
	public sealed class MfContentInfo
	{
		public string assetBundleConfig = string.Empty;
		public string compression = string.Empty;
		public string renderPipelineAddress = string.Empty;
		public List<string> bundleRoots = new();
		public List<string> requiredAddresses = new();
	}

	[Serializable]
	public sealed class MfBuildProfile
	{
		public string id = string.Empty;
		public string displayName = string.Empty;
		public string action = string.Empty;
		public string target = string.Empty;
		public string description = string.Empty;
		public string environmentPolicy = "select";
		public List<string> allowedEnvironments = new();
		public List<string> outputKinds = new();
		public bool requiresCleanGit;
		public bool requiresSigning;
		public bool supportsDevelopment;
		public bool supportsCleanBuild = true;
		public Dictionary<string, string> properties = new();
	}

	[Serializable]
	public sealed class MfModuleInfo
	{
		public string id = string.Empty;
		public string displayName = string.Empty;
		public string kind = string.Empty;
		public string manifest = string.Empty;
		public bool optional;
		public Dictionary<string, string> properties = new();
	}

	[Serializable]
	public sealed class MfBuildJob
	{
		public int schema = MfBuildSchema.Job;
		public string jobId = string.Empty;
		public string projectRoot = string.Empty;
		public string structurePath = MfBuildSchema.ProjectFileName;
		public string structureHash = string.Empty;
		public string profileId = string.Empty;
		public string action = string.Empty;
		public string target = string.Empty;
		public string environment = string.Empty;
		public string outputRoot = string.Empty;
		public string version = string.Empty;
		public long buildNumber;
		public bool clean;
		public bool development;
		public List<string> modules = new();
		public Dictionary<string, string> arguments = new();
	}

	[Serializable]
	public sealed class MfBuildReceipt
	{
		public int schema = MfBuildSchema.Receipt;
		public string jobId = string.Empty;
		public bool ok;
		public string status = string.Empty;
		public string error = string.Empty;
		public string startedAtUtc = string.Empty;
		public string finishedAtUtc = string.Empty;
		public long durationMs;
		public int exitCode;
		public string projectId = string.Empty;
		public string structureHash = string.Empty;
		public string profileId = string.Empty;
		public string action = string.Empty;
		public string target = string.Empty;
		public string environment = string.Empty;
		public string outputRoot = string.Empty;
		public List<MfBuildStageReceipt> stages = new();
		public List<MfArtifactReceipt> artifacts = new();
		public Dictionary<string, string> values = new();
	}

	[Serializable]
	public sealed class MfBuildStageReceipt
	{
		public string id = string.Empty;
		public string status = string.Empty;
		public string message = string.Empty;
		public long durationMs;
	}

	[Serializable]
	public sealed class MfArtifactReceipt
	{
		public string kind = string.Empty;
		public string path = string.Empty;
		public long size;
		public string sha256 = string.Empty;
	}

	[Serializable]
	public sealed class MfBuildEvent
	{
		public int schema = MfBuildSchema.Event;
		public string jobId = string.Empty;
		public string stage = string.Empty;
		public string state = string.Empty;
		public string message = string.Empty;
		public string timeUtc = string.Empty;
		public float progress;
	}
}
