using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using MyFramework.BuildStudio;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace MyFramework.BuildStudio.Editor
{
	public static class MfBuildCli
	{
		const string EventPrefix = "MF_EVENT=";
		const string ReceiptPrefix = "MF_RECEIPT=";
		static readonly UTF8Encoding sUtf8 = new(false, true);

		public static void runCli()
		{
			string[] args = Environment.GetCommandLineArgs();
			string jobPath = option(args, "-mfJob");
			string receiptPath = option(args, "-mfReceipt");
			string eventPath = option(args, "-mfEventFile");
			int exitCode = run(jobPath, receiptPath, eventPath, out MfBuildReceipt receipt);
			Console.WriteLine(ReceiptPrefix + MfProjectStructureService.serialize(
				receipt, Formatting.None));
			if (Application.isBatchMode) EditorApplication.Exit(exitCode);
		}

		public static int run(string jobPath, string receiptPath, string eventPath,
			out MfBuildReceipt receipt)
		{
			Stopwatch total = Stopwatch.StartNew();
			receipt = new MfBuildReceipt
			{
				status = "running",
				startedAtUtc = DateTime.UtcNow.ToString("O"),
			};
			Action<MfBuildEvent> emit = value => writeEvent(value, eventPath);
			int code;
			try
			{
				string jobFile = requireInputFile(jobPath, "-mfJob");
				MfBuildJob job = MfProjectStructureService.deserialize<MfBuildJob>(
					File.ReadAllText(jobFile, sUtf8)) ?? throw new InvalidDataException(
					"Build job is empty.");
				validateJob(job);
				copyIdentity(job, receipt);

				string root = MfProjectStructureService.projectRoot();
				if (!string.IsNullOrWhiteSpace(job.projectRoot) &&
					!samePath(root, job.projectRoot))
					throw new InvalidDataException("Build job targets a different Unity project.");
				string structurePath = resolveInside(root, job.structurePath,
					"Project structure path");
				MfProjectStructure saved = MfProjectStructureService.read(structurePath, true);
				MfProjectStructure current = MfProjectStructureService.generate(structurePath);
				if (saved.structureHash != current.structureHash ||
					job.structureHash != saved.structureHash)
					throw new InvalidDataException("Project structure is stale; regenerate before building.");
				receipt.projectId = saved.project.id;
				receipt.structureHash = saved.structureHash;

				MfBuildProfile profile = saved.profiles.SingleOrDefault(value =>
					value.id == job.profileId) ?? throw new InvalidDataException(
					"Unknown build profile: " + job.profileId);
				validateProfileJob(profile, job);
				if (profile.requiresCleanGit && !gitClean(root))
					throw new InvalidOperationException("Build profile requires a clean Git worktree.");
				HashSet<string> declaredModules = new(saved.modules.Select(value => value.id),
					StringComparer.Ordinal);
				if (job.modules.Any(value => !declaredModules.Contains(value)))
					throw new InvalidDataException("Build job selected an unknown module.");
				IMfBuildProvider provider = MfBuildStudioRegistry.provider(saved.project.id);
				if (provider == null && job.action != "validate")
					throw new InvalidOperationException("Project has no Build Studio provider: " +
						saved.project.id);

				MfBuildContext context = new(job, saved, receipt, emit);
				context.stage("project-validate", "Validate project adapter", () =>
				{
					validateTarget(profile, job);
					provider?.validate(context);
				});

				MfBuildExecutionResult result = null;
				if (job.action != "validate")
					result = context.stage("execute", "Run project build provider", () =>
						provider.run(context));
				applyResult(receipt, result, root);
				receipt.ok = true;
				receipt.status = "succeeded";
				receipt.exitCode = 0;
				code = 0;
			}
			catch (Exception exception)
			{
				receipt.ok = false;
				receipt.status = "failed";
				receipt.error = exception.GetType().Name + ": " + exception.Message;
				receipt.exitCode = 2;
				Debug.LogError("Build Studio job failed: " + exception);
				code = 2;
			}
			finally
			{
				total.Stop();
				receipt.durationMs = total.ElapsedMilliseconds;
				receipt.finishedAtUtc = DateTime.UtcNow.ToString("O");
				if (!string.IsNullOrWhiteSpace(receiptPath))
				{
					try
					{
						MfProjectStructureService.writeAtomic(receiptPath,
							MfProjectStructureService.serialize(receipt, Formatting.Indented) +
							Environment.NewLine);
					}
					catch (Exception exception)
					{
						receipt.ok = false;
						receipt.status = "failed";
						receipt.exitCode = 3;
						code = 3;
						receipt.error = "ReceiptWriteFailed: " + exception.Message;
						Debug.LogError("Build Studio receipt write failed: " + exception);
					}
				}
			}
			return code;
		}

		static void validateJob(MfBuildJob job)
		{
			if (job.schema != MfBuildSchema.Job) throw new InvalidDataException(
				"Unsupported build job schema.");
			if (!id(job.jobId) || !id(job.profileId) || string.IsNullOrWhiteSpace(job.action) ||
				string.IsNullOrWhiteSpace(job.target)) throw new InvalidDataException(
				"Build job identity is incomplete.");
			if (job.environment != "test" && job.environment != "prod")
				throw new InvalidDataException("Build job environment must be test or prod.");
			if (!sha(job.structureHash)) throw new InvalidDataException(
				"Build job structure hash is invalid.");
			job.modules ??= new List<string>();
			job.arguments ??= new Dictionary<string, string>();
			foreach (string key in job.arguments.Keys)
			{
				string lowered = key.ToLowerInvariant();
				if (lowered.Contains("password") || lowered.Contains("secret") ||
					lowered.Contains("token") || lowered.Contains("privatekey"))
					throw new InvalidDataException("Secrets are forbidden in build jobs: " + key);
			}
		}

		static void validateProfileJob(MfBuildProfile profile, MfBuildJob job)
		{
			if (profile.action != job.action) throw new InvalidDataException(
				"Build job action does not match profile.");
			if (profile.allowedEnvironments.Count > 0 &&
				!profile.allowedEnvironments.Contains(job.environment))
				throw new InvalidDataException("Profile does not allow environment: " +
					job.environment);
			if (job.development && !profile.supportsDevelopment)
				throw new InvalidDataException("Profile does not support development builds.");
			if (job.clean && !profile.supportsCleanBuild)
				throw new InvalidDataException("Profile does not support clean builds.");
			if (profile.target != "Current" && profile.target != job.target)
				throw new InvalidDataException("Build job target does not match profile.");
		}

		static void validateTarget(MfBuildProfile profile, MfBuildJob job)
		{
			string active = EditorUserBuildSettings.activeBuildTarget.ToString();
			string wanted = job.target == "Current" ? active : job.target;
			if (profile.target != "Current" && wanted != active)
				throw new InvalidOperationException("Unity active target is " + active +
					" but profile requires " + wanted + ". Start Unity with -buildTarget.");
		}

		static bool gitClean(string root)
		{
			try
			{
				ProcessStartInfo start = new("git")
				{
					WorkingDirectory = root,
					RedirectStandardOutput = true,
					RedirectStandardError = true,
					UseShellExecute = false,
					CreateNoWindow = true,
				};
				start.ArgumentList.Add("status");
				start.ArgumentList.Add("--porcelain");
				using Process process = Process.Start(start) ?? throw new InvalidOperationException();
				string output = process.StandardOutput.ReadToEnd();
				process.WaitForExit();
				return process.ExitCode == 0 && string.IsNullOrWhiteSpace(output);
			}
			catch { return false; }
		}

		static void copyIdentity(MfBuildJob job, MfBuildReceipt receipt)
		{
			receipt.jobId = job.jobId;
			receipt.profileId = job.profileId;
			receipt.action = job.action;
			receipt.target = job.target;
			receipt.environment = job.environment;
			receipt.outputRoot = job.outputRoot;
		}

		static void applyResult(MfBuildReceipt receipt, MfBuildExecutionResult result,
			string projectRoot)
		{
			if (result == null) return;
			if (!string.IsNullOrWhiteSpace(result.outputRoot))
				receipt.outputRoot = absolute(result.outputRoot, projectRoot);
			foreach (KeyValuePair<string, string> item in result.values)
				receipt.values[item.Key] = item.Value;
			foreach (MfBuildArtifact artifact in result.artifacts)
			{
				string path = absolute(artifact.path, projectRoot);
				if (!File.Exists(path) && !Directory.Exists(path))
					throw new FileNotFoundException("Declared build artifact is missing.", path);
				receipt.artifacts.Add(inspectArtifact(artifact.kind, path));
			}
		}

		static MfArtifactReceipt inspectArtifact(string kind, string path)
		{
			if (File.Exists(path))
			{
				FileInfo file = new(path);
				return new MfArtifactReceipt
				{
					kind = kind,
					path = path,
					size = file.Length,
					sha256 = fileSha(path),
				};
			}
			long size = 0;
			StringBuilder aggregate = new();
			string root = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar,
				Path.AltDirectorySeparatorChar);
			ensureNoLinks(root);
			foreach (string file in Directory.GetFiles(root, "*", SearchOption.AllDirectories)
				.OrderBy(value => value, StringComparer.Ordinal))
			{
				FileInfo info = new(file);
				if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
					throw new InvalidDataException("Artifact contains a link: " + file);
				size += info.Length;
				aggregate.Append(Path.GetRelativePath(root, file).Replace('\\', '/')).Append('\n')
					.Append(info.Length).Append('\n').Append(fileSha(file)).Append('\n');
			}
			return new MfArtifactReceipt
			{
				kind = kind,
				path = path,
				size = size,
				sha256 = textSha(aggregate.ToString()),
			};
		}

		static void writeEvent(MfBuildEvent value, string path)
		{
			string json = MfProjectStructureService.serialize(value, Formatting.None);
			Console.WriteLine(EventPrefix + json);
			if (string.IsNullOrWhiteSpace(path)) return;
			if (!Path.IsPathRooted(path)) throw new InvalidDataException(
				"-mfEventFile must be an absolute path.");
			string full = Path.GetFullPath(path);
			Directory.CreateDirectory(Path.GetDirectoryName(full) ?? throw new
				InvalidDataException("Event file directory is invalid."));
			File.AppendAllText(full, json + Environment.NewLine, sUtf8);
		}

		static string requireInputFile(string value, string label)
		{
			if (string.IsNullOrWhiteSpace(value) || !Path.IsPathRooted(value))
				throw new InvalidDataException(label + " must be an absolute file path.");
			string full = Path.GetFullPath(value);
			if (!File.Exists(full)) throw new FileNotFoundException(label + " is missing.", full);
			ensureNoLinks(full);
			return full;
		}

		static string resolveInside(string root, string relative, string label)
		{
			if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative))
				throw new InvalidDataException(label + " must be relative.");
			string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar,
				Path.AltDirectorySeparatorChar);
			string full = Path.GetFullPath(Path.Combine(fullRoot, relative));
			if (!full.StartsWith(fullRoot + Path.DirectorySeparatorChar,
				StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException(
					label + " escapes the project.");
			return full;
		}

		static string absolute(string path, string projectRoot)
		{
			if (string.IsNullOrWhiteSpace(path)) throw new InvalidDataException(
				"Artifact path is required.");
			return Path.GetFullPath(Path.IsPathRooted(path) ? path :
				Path.Combine(projectRoot, path));
		}

		static bool samePath(string left, string right)
		{
			return string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar,
				Path.AltDirectorySeparatorChar), Path.GetFullPath(right).TrimEnd(
				Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
				StringComparison.OrdinalIgnoreCase);
		}

		static void ensureNoLinks(string path)
		{
			FileSystemInfo info = File.Exists(path) ? new FileInfo(path) : new DirectoryInfo(path);
			if (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0)
				throw new InvalidDataException("Path is a link: " + path);
			for (DirectoryInfo directory = info is FileInfo ? ((FileInfo)info).Directory :
				(DirectoryInfo)info; directory != null; directory = directory.Parent)
				if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
					throw new InvalidDataException("Path traverses a link: " + path);
		}

		static string fileSha(string path)
		{
			using SHA256 sha = SHA256.Create();
			using FileStream stream = File.OpenRead(path);
			return hex(sha.ComputeHash(stream));
		}

		static string textSha(string value)
		{
			using SHA256 sha = SHA256.Create();
			return hex(sha.ComputeHash(sUtf8.GetBytes(value ?? string.Empty)));
		}

		static string hex(byte[] value) => string.Concat(value.Select(item => item.ToString("x2")));
		static bool id(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 80 &&
			value.All(character => char.IsLetterOrDigit(character) || character == '-' ||
				character == '_' || character == '.');
		static bool sha(string value) => value != null && value.Length == 64 && value.All(
			character => character >= '0' && character <= '9' ||
				character >= 'a' && character <= 'f');

		static string option(string[] args, string name)
		{
			for (int i = 0; args != null && i < args.Length - 1; ++i)
				if (args[i] == name) return args[i + 1];
			return null;
		}
	}
}
