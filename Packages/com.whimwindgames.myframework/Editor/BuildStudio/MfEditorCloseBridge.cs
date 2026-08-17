using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace MyFramework.BuildStudio.Editor
{
	/// <summary>
	/// Lets Build Studio ask an interactive Editor to save and exit before a batch worker starts.
	/// The request lives under Temp, is token-bound, and is ignored by batchmode workers.
	/// </summary>
	[InitializeOnLoad]
	public static class MfEditorCloseBridge
	{
		const string ControlFolder = "MyFrameworkBuildStudio";
		const string RequestFile = "editor-close.request";
		const string AckFile = "editor-close.ack";
		static bool sExiting;

		static MfEditorCloseBridge()
		{
			EditorApplication.update += poll;
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
