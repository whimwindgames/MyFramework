using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MyFramework.BuildStudio.Editor
{
	/// <summary>
	/// Lets Build Studio ask an interactive Editor to save and exit before a batch worker starts.
	/// The request lives under Temp, is token-bound, and is ignored by batchmode workers.
	/// </summary>
	public static class MfEditorCloseBridge
	{
		const string ControlFolder = "MyFrameworkBuildStudio";
		const string RequestFile = "editor-close.request";
		const string AckFile = "editor-close.ack";
		static bool sExiting;

		[InitializeOnLoadMethod]
		static void bind()
		{
			EditorApplication.update -= poll;
			EditorApplication.update += poll;
			EditorApplication.delayCall += poll;
		}

		static void poll()
		{
			if (Application.isBatchMode) return;
			if (sExiting)
			{
				EditorApplication.Exit(0);
				return;
			}
			string requestPath = path(RequestFile);
			if (!File.Exists(requestPath)) return;
			if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
			if (EditorApplication.isPlayingOrWillChangePlaymode)
			{
				EditorApplication.isPlaying = false;
				return;
			}

			string token = string.Empty;
			try
			{
				token = File.ReadAllText(requestPath).Trim();
				if (!validToken(token))
					throw new InvalidDataException("Build Studio close token is invalid.");
				for (int i = 0; i < SceneManager.sceneCount; ++i)
				{
					Scene scene = SceneManager.GetSceneAt(i);
					if (scene.isDirty && string.IsNullOrWhiteSpace(scene.path))
						throw new OperationCanceledException(
							"An untitled scene has unsaved changes. Save or discard it before building.");
				}
				AssetDatabase.SaveAssets();
				if (!EditorSceneManager.SaveOpenScenes())
					throw new OperationCanceledException("Scene saving was canceled.");
				writeAck(token, "ok", string.Empty);
				File.Delete(requestPath);
				sExiting = true;
				Debug.Log("MyFramework Build Studio: project saved; closing Editor for worker.");
				EditorApplication.Exit(0);
			}
			catch (Exception exception)
			{
				if (validToken(token)) writeAck(token, "error", exception.Message);
				try { File.Delete(requestPath); }
				catch { /* The coordinator will time out with the original request visible. */ }
				Debug.LogError("MyFramework Build Studio close request failed: " + exception);
			}
		}

		static void writeAck(string token, string status, string message)
		{
			string ack = token + Environment.NewLine + status + Environment.NewLine +
				(message ?? string.Empty) + Environment.NewLine;
			MfProjectStructureService.writeAtomic(path(AckFile), ack);
		}

		static bool validToken(string value)
		{
			return value != null && value.Length == 32 && value.All(character =>
				character >= '0' && character <= '9' ||
				character >= 'a' && character <= 'f');
		}

		static string path(string file)
		{
			return Path.Combine(MfProjectStructureService.projectRoot(), "Temp",
				ControlFolder, file);
		}
	}
}
