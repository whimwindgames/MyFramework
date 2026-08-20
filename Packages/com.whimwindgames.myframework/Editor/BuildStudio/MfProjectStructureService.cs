using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using HybridCLR.Editor;
using MyFramework.BuildStudio;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.PackageManager;
using UnityEngine;
using UnityEngine.Rendering;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace MyFramework.BuildStudio.Editor
{
	public static class MfProjectStructureService
	{
		const string MenuRoot = "MyFramework/Build Studio/";
		static readonly UTF8Encoding sUtf8 = new(false, true);

		[MenuItem(MenuRoot + "生成项目结构文件", false, 0)]
		public static void generateMenu()
		{
			string output = writeDefault();
			Debug.Log("MyFramework Build Studio structure generated: " + output);
			EditorUtility.RevealInFinder(output);
		}

		[MenuItem(MenuRoot + "校验项目结构文件", false, 1)]
		public static void validateMenu()
		{
			MfProjectStructure saved = read(defaultPath(), true);
			MfProjectStructure current = generate(defaultPath());
			if (!string.Equals(saved.structureHash, current.structureHash,
				StringComparison.Ordinal))
				throw new InvalidDataException("项目结构文件已过期，请重新生成。saved=" +
					saved.structureHash + ", current=" + current.structureHash);
			Debug.Log("MyFramework Build Studio structure is valid: " + saved.structureHash);
		}

		public static string projectRoot()
		{
			return Path.GetFullPath(Path.Combine(Application.dataPath, ".."))
				.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		}

		public static string defaultPath()
		{
			return Path.Combine(projectRoot(), MfBuildSchema.ProjectFileName);
		}

		public static string writeDefault()
		{
			return write(defaultPath());
		}

		public static string write(string output)
		{
			string path = requireAbsoluteFile(output, "Structure output");
			MfProjectStructure structure = generate(path);
			writeAtomic(path, serialize(structure, Formatting.Indented) + Environment.NewLine);
			MfProjectStructure saved = read(path, true);
			if (saved.structureHash != structure.structureHash)
				throw new InvalidDataException("项目结构写入后哈希不一致。");
			return path;
		}

		public static MfProjectStructure generate(string outputPath = null)
		{
			string root = projectRoot();
			MfProjectStructure result = makeBase(root);
			MfProjectStructureContext context = new()
			{
				projectRoot = root,
				outputPath = outputPath ?? defaultPath(),
			};
			foreach (IMfProjectStructureContributor contributor in
				MfBuildStudioRegistry.contributors())
			{
				contributor.contribute(context, result);
				result.contributors.Add(contributor.id);
			}
			addFrameworkProfiles(result);
			normalize(result);
			validate(result, false);
			result.structureHash = computeHash(result);
			validate(result, true);
			return result;
		}

		static void addFrameworkProfiles(MfProjectStructure structure)
		{
			if (string.IsNullOrWhiteSpace(structure.content.assetBundleConfig) ||
				structure.profiles.Any(value => value != null && value.action == "assets"))
				return;

			foreach ((string id, BuildTarget target) in new[]
			{
				("windows", BuildTarget.StandaloneWindows64),
				("macos", BuildTarget.StandaloneOSX),
				("android", BuildTarget.Android),
				("ios", BuildTarget.iOS),
			})
			{
				structure.profiles.Add(new MfBuildProfile
				{
					id = "assets-" + id,
					displayName = id + " AssetBundle",
					action = "assets",
					target = target.ToString(),
					description = "Build only the MyFramework AssetBundle content for " +
						target + "; no Player, HybridCLR baseline, release or signing.",
					allowedEnvironments = new List<string> { "test", "prod" },
					outputKinds = new List<string> { "asset-bundles", "receipt" },
				});
			}
		}

		public static MfProjectStructure read(string path, bool verifyHash)
		{
			string full = requireAbsoluteFile(path, "Structure path");
			if (!File.Exists(full)) throw new FileNotFoundException(
				"项目结构文件不存在。", full);
			MfProjectStructure value;
			try
			{
				value = JsonConvert.DeserializeObject<MfProjectStructure>(
					File.ReadAllText(full, sUtf8), strictSettings());
			}
			catch (JsonException exception)
			{
				throw new InvalidDataException("项目结构JSON无效: " + full, exception);
			}
			if (value == null) throw new InvalidDataException("项目结构为空: " + full);
			normalize(value);
			validate(value, verifyHash);
			if (verifyHash && !string.Equals(value.structureHash, computeHash(value),
				StringComparison.Ordinal))
				throw new InvalidDataException("项目结构哈希不匹配: " + full);
			return value;
		}

		public static string computeHash(MfProjectStructure value)
		{
			if (value == null) throw new ArgumentNullException(nameof(value));
			string previous = value.structureHash;
			try
			{
				value.structureHash = string.Empty;
				JsonSerializer serializer = JsonSerializer.Create(strictSettings());
				JToken token = JToken.FromObject(value, serializer);
				byte[] bytes = sUtf8.GetBytes(canonical(token).ToString(Formatting.None));
				using SHA256 sha = SHA256.Create();
				return string.Concat(sha.ComputeHash(bytes).Select(item => item.ToString("x2")));
			}
			finally { value.structureHash = previous; }
		}

		static JToken canonical(JToken token)
		{
			if (token is JObject obj)
			{
				JObject result = new();
				foreach (JProperty property in obj.Properties().OrderBy(value =>
					value.Name, StringComparer.Ordinal))
					result.Add(property.Name, canonical(property.Value));
				return result;
			}
			if (token is JArray array)
				return new JArray(array.Select(canonical));
			return token.DeepClone();
		}

		public static string serialize(object value, Formatting formatting)
		{
			return JsonConvert.SerializeObject(value, formatting, strictSettings());
		}

		public static T deserialize<T>(string json)
		{
			try { return JsonConvert.DeserializeObject<T>(json, strictSettings()); }
			catch (JsonException exception)
			{
				throw new InvalidDataException(typeof(T).Name + " JSON无效。", exception);
			}
		}

		public static void writeAtomic(string path, string content)
		{
			string full = requireAbsoluteFile(path, "Output path");
			string parent = Path.GetDirectoryName(full) ?? throw new InvalidDataException(
				"输出目录无效: " + full);
			Directory.CreateDirectory(parent);
			ensureNoLinks(parent);
			string temporary = full + ".tmp-" + Guid.NewGuid().ToString("N");
			try
			{
				using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write,
					FileShare.None))
				{
					byte[] bytes = sUtf8.GetBytes(content ?? string.Empty);
					stream.Write(bytes, 0, bytes.Length);
					stream.Flush(true);
				}
				if (File.Exists(full)) File.Replace(temporary, full, null);
				else File.Move(temporary, full);
			}
			finally { if (File.Exists(temporary)) File.Delete(temporary); }
		}

		static MfProjectStructure makeBase(string root)
		{
			MfProjectStructure result = new();
			result.project.id = safeId(Path.GetFileName(root));
			result.project.displayName = string.IsNullOrWhiteSpace(PlayerSettings.productName) ?
				Path.GetFileName(root) : PlayerSettings.productName.Trim();
			result.unity.version = Application.unityVersion;
			result.unity.colorSpace = PlayerSettings.colorSpace.ToString();
			RenderPipelineAsset pipeline = GraphicsSettings.defaultRenderPipeline;
			result.unity.defaultRenderPipeline = pipeline == null ? string.Empty :
				AssetDatabase.GetAssetPath(pipeline).Replace('\\', '/');
			result.framework.assetBundleSchema = AbCfg.SCHEMA;
			result.framework.releaseSchema = 11;

			HashSet<string> direct = directPackages(root);
			foreach (PackageInfo package in PackageInfo.GetAllRegisteredPackages()
				.OrderBy(value => value.name, StringComparer.Ordinal))
			{
				result.packages.Add(new MfPackageInfo
				{
					name = package.name,
					version = package.version,
					source = package.source.ToString(),
					direct = direct.Contains(package.name),
				});
				if (package.name == result.framework.package)
					result.framework.version = package.version;
			}

			foreach (EditorBuildSettingsScene scene in EditorBuildSettings.scenes ??
				Array.Empty<EditorBuildSettingsScene>())
			{
				if (string.IsNullOrWhiteSpace(scene.path)) continue;
				result.scenes.Add(new MfSceneInfo
				{
					path = scene.path.Replace('\\', '/'),
					guid = AssetDatabase.AssetPathToGUID(scene.path),
					enabled = scene.enabled,
					role = scene.enabled ? "player" : "disabled",
				});
			}

			result.managedCode.hybridClrEnabled = SettingsUtil.Enable;
			var hybrid = SettingsUtil.HybridCLRSettings;
			result.managedCode.hotAssemblies.AddRange(hybrid.hotUpdateAssemblies ??
				Array.Empty<string>());
			result.managedCode.preservedHotAssemblies.AddRange(
				hybrid.preserveHotUpdateAssemblies ?? Array.Empty<string>());
			BuildTargetGroup group = BuildPipeline.GetBuildTargetGroup(
				EditorUserBuildSettings.activeBuildTarget);
			string defines = PlayerSettings.GetScriptingDefineSymbols(
				NamedBuildTarget.FromBuildTargetGroup(group));
			result.managedCode.obfuzEnabled = (defines ?? string.Empty).Split(';')
				.Any(value => value.Trim() == "USE_OBFUZ");

			if (File.Exists(Path.Combine(root, "ProjectSettings", "AbCfg.asset")))
			{
				AbCfg config = AbCfg.load();
				result.content.assetBundleConfig = "ProjectSettings/AbCfg.asset";
				result.content.compression = config.zip.ToString();
				result.content.bundleRoots.AddRange((config.groups ?? new List<AbGroup>())
					.Select(value => value?.bundleName).Where(value =>
						!string.IsNullOrWhiteSpace(value)));
			}

			result.profiles.Add(new MfBuildProfile
			{
				id = "validate",
				displayName = "Validate",
				action = "validate",
				target = "Current",
				description = "Validate the generated project structure and project adapter.",
				allowedEnvironments = new List<string> { "test", "prod" },
				outputKinds = new List<string> { "receipt" },
			});
			return result;
		}

		static HashSet<string> directPackages(string root)
		{
			HashSet<string> result = new(StringComparer.Ordinal);
			string path = Path.Combine(root, "Packages", "manifest.json");
			if (!File.Exists(path)) return result;
			try
			{
				JObject manifest = JObject.Parse(File.ReadAllText(path, sUtf8));
				if (manifest["dependencies"] is JObject dependencies)
					foreach (JProperty property in dependencies.Properties()) result.Add(property.Name);
			}
			catch (JsonException exception)
			{
				throw new InvalidDataException("Packages/manifest.json无效。", exception);
			}
			return result;
		}

		static void normalize(MfProjectStructure value)
		{
			if (value == null) return;
			value.packages ??= new List<MfPackageInfo>();
			value.scenes ??= new List<MfSceneInfo>();
			value.profiles ??= new List<MfBuildProfile>();
			value.modules ??= new List<MfModuleInfo>();
			value.contributors ??= new List<string>();
			value.properties = sorted(value.properties);
			value.project ??= new MfProjectIdentity();
			value.unity ??= new MfUnityInfo();
			value.framework ??= new MfFrameworkInfo();
			value.managedCode ??= new MfManagedCodeInfo();
			value.content ??= new MfContentInfo();
			value.managedCode.hotAssemblies ??= new List<string>();
			value.managedCode.preservedHotAssemblies ??= new List<string>();
			value.managedCode.aotAssemblies ??= new List<string>();
			value.content.bundleRoots ??= new List<string>();
			value.content.requiredAddresses ??= new List<string>();
			value.packages.Sort((left, right) => string.CompareOrdinal(left?.name, right?.name));
			value.scenes.Sort((left, right) => string.CompareOrdinal(left?.path, right?.path));
			value.profiles.Sort((left, right) => string.CompareOrdinal(left?.id, right?.id));
			value.modules.Sort((left, right) => string.CompareOrdinal(left?.id, right?.id));
			value.contributors = value.contributors.Where(item =>
				!string.IsNullOrWhiteSpace(item)).Select(item => item.Trim())
				.Distinct(StringComparer.Ordinal).OrderBy(item => item, StringComparer.Ordinal).ToList();
			value.managedCode.hotAssemblies = values(value.managedCode.hotAssemblies);
			value.managedCode.preservedHotAssemblies = values(
				value.managedCode.preservedHotAssemblies);
			value.managedCode.aotAssemblies = values(value.managedCode.aotAssemblies);
			value.content.bundleRoots = values(value.content.bundleRoots);
			value.content.requiredAddresses = values(value.content.requiredAddresses);
			foreach (MfBuildProfile profile in value.profiles)
			{
				if (profile == null) continue;
				profile.allowedEnvironments = values(profile.allowedEnvironments);
				profile.outputKinds = values(profile.outputKinds);
				profile.properties = sorted(profile.properties);
			}
			foreach (MfModuleInfo module in value.modules)
				if (module != null) module.properties = sorted(module.properties);
		}

		static List<string> values(IEnumerable<string> input)
		{
			return (input ?? Array.Empty<string>()).Where(value =>
					!string.IsNullOrWhiteSpace(value)).Select(value => value.Trim())
				.Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToList();
		}

		static Dictionary<string, string> sorted(Dictionary<string, string> input)
		{
			Dictionary<string, string> result = new(StringComparer.Ordinal);
			foreach (KeyValuePair<string, string> item in (input ??
				new Dictionary<string, string>()).OrderBy(item => item.Key, StringComparer.Ordinal))
				result[item.Key] = item.Value;
			return result;
		}

		static void validate(MfProjectStructure value, bool requireHash)
		{
			if (value.schema != MfBuildSchema.Project) fail("Unsupported project schema.");
			if (!safe(value.project?.id)) fail("Project id is invalid.");
			if (string.IsNullOrWhiteSpace(value.project.displayName) ||
				string.IsNullOrWhiteSpace(value.project.kind)) fail("Project identity is incomplete.");
			if (string.IsNullOrWhiteSpace(value.unity?.version)) fail("Unity version is required.");
			if (string.IsNullOrWhiteSpace(value.framework?.version)) fail(
				"MyFramework package is not installed or has no version.");
			if (requireHash && !sha(value.structureHash)) fail("Structure hash is invalid.");
			unique(value.packages.Select(item => item?.name), "package");
			unique(value.profiles.Select(item => item?.id), "profile");
			unique(value.modules.Select(item => item?.id), "module");
			foreach (MfPackageInfo package in value.packages)
				if (!safePackage(package?.name) || string.IsNullOrWhiteSpace(package.version))
					fail("Package declaration is invalid.");
			foreach (MfSceneInfo scene in value.scenes)
				if (!relative(scene?.path)) fail("Scene path is invalid: " + scene?.path);
			HashSet<string> actions = new(new[]
			{
				"validate", "assets", "base", "release", "code", "integrated",
				"hosted-runtime",
			}, StringComparer.Ordinal);
			foreach (MfBuildProfile profile in value.profiles)
			{
				if (!safe(profile?.id) || !actions.Contains(profile.action) ||
					string.IsNullOrWhiteSpace(profile.target)) fail("Build profile is invalid.");
				if (profile.environmentPolicy != "select" && profile.environmentPolicy != "fixed")
					fail("Build profile environment policy is invalid: " + profile.id);
				if (profile.allowedEnvironments.Any(env => env != "test" && env != "prod"))
					fail("Build profile environment is invalid: " + profile.id);
				checkSecrets(profile.properties, "profile " + profile.id);
			}
			foreach (MfModuleInfo module in value.modules)
			{
				if (!safe(module?.id) || string.IsNullOrWhiteSpace(module.kind) ||
					!relative(module.manifest)) fail("Module declaration is invalid.");
				checkSecrets(module.properties, "module " + module.id);
			}
			checkSecrets(value.properties, "project");
		}

		static void checkSecrets(Dictionary<string, string> values, string owner)
		{
			foreach (string key in (values ?? new Dictionary<string, string>()).Keys)
			{
				string lowered = key.ToLowerInvariant();
				if (lowered.Contains("password") || lowered.Contains("secret") ||
					lowered.Contains("token") || lowered.Contains("privatekey"))
					fail(owner + " property must not contain secret material: " + key);
			}
		}

		static void unique(IEnumerable<string> values, string label)
		{
			HashSet<string> seen = new(StringComparer.Ordinal);
			foreach (string value in values)
				if (string.IsNullOrWhiteSpace(value) || !seen.Add(value))
					fail("Duplicate or empty " + label + " id: " + value);
		}

		static string safeId(string value)
		{
			value = (value ?? string.Empty).Trim().ToLowerInvariant();
			StringBuilder output = new();
			bool dash = false;
			foreach (char character in value)
			{
				bool accepted = character >= 'a' && character <= 'z' ||
					character >= '0' && character <= '9';
				if (accepted)
				{
					output.Append(character);
					dash = false;
				}
				else if (!dash && output.Length > 0)
				{
					output.Append('-');
					dash = true;
				}
			}
			return output.ToString().Trim('-');
		}

		static bool safe(string value)
		{
			return !string.IsNullOrWhiteSpace(value) && value.Length <= 80 &&
				value.All(character => character >= 'a' && character <= 'z' ||
					character >= 'A' && character <= 'Z' || char.IsDigit(character) ||
					character == '-' || character == '_' || character == '.');
		}

		static bool safePackage(string value)
		{
			return !string.IsNullOrWhiteSpace(value) && value.Length <= 214 &&
				value.All(character => character >= 'a' && character <= 'z' ||
					char.IsDigit(character) || character == '.' || character == '-');
		}

		static bool relative(string value)
		{
			if (string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value)) return false;
			string normalized = value.Replace('\\', '/');
			return normalized.Split('/').All(part => part.Length > 0 && part != "." && part != "..");
		}

		static bool sha(string value)
		{
			return value != null && value.Length == 64 && value.All(character =>
				character >= '0' && character <= '9' || character >= 'a' && character <= 'f');
		}

		static JsonSerializerSettings strictSettings()
		{
			return new JsonSerializerSettings
			{
				MissingMemberHandling = MissingMemberHandling.Error,
				NullValueHandling = NullValueHandling.Include,
				Formatting = Formatting.None,
			};
		}

		static string requireAbsoluteFile(string value, string label)
		{
			if (string.IsNullOrWhiteSpace(value) || !Path.IsPathRooted(value))
				throw new InvalidDataException(label + " must be an absolute path.");
			string full = Path.GetFullPath(value);
			if (Directory.Exists(full)) throw new InvalidDataException(label +
				" points to a directory: " + full);
			return full;
		}

		static void ensureNoLinks(string path)
		{
			for (DirectoryInfo directory = new(path); directory != null;
				directory = directory.Parent)
				if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
					throw new InvalidDataException("Path must not traverse a link: " + path);
		}

		static void fail(string message) { throw new InvalidDataException(message); }
	}

	public static class MfProjectStructureCli
	{
		public static void runCli()
		{
			string[] args = Environment.GetCommandLineArgs();
			string output = option(args, "-mfStructureOutput") ??
				MfProjectStructureService.defaultPath();
			int code = 0;
			try
			{
				string path = MfProjectStructureService.write(output);
				Console.WriteLine("MF_STRUCTURE=" + path);
			}
			catch (Exception exception)
			{
				code = 2;
				Debug.LogError("Build Studio structure generation failed: " + exception);
			}
			if (Application.isBatchMode) EditorApplication.Exit(code);
		}

		static string option(string[] args, string name)
		{
			for (int i = 0; args != null && i < args.Length - 1; ++i)
				if (args[i] == name) return args[i + 1];
			return null;
		}
	}
}
