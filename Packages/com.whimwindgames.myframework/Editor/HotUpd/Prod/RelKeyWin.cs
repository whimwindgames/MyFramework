using System;
using System.IO;
using UnityEditor;
using UnityEngine;

// 密钥生命周期薄窗口：创建、旧配置迁移、pending轮换、过渡Latest和新Base确认。
public sealed class RelKeyWin : EditorWindow
{
	const string PRE = "MyFramework.HotUpd.Key.Win.";
	readonly string[] mEnvs = { "test", "prod" };
	string mProject;
	int mEnvAt;
	string mPassword;
	string mPubRoot;
	string mPlatform;
	string mOldBase;
	string mNewBase;
	string mStatus = "就绪";
	Vector2 mScroll;

	[MenuItem("MyFramework/HotUpdate/密钥与轮换")]
	public static void open()
	{
		RelKeyWin win = GetWindow<RelKeyWin>(false, "密钥与轮换", true);
		win.minSize = new Vector2(600, 560);
		win.Show();
	}

	void OnEnable()
	{
		mProject = EditorPrefs.GetString(PRE + "project", RelKeyStore.defaultProject());
		mPubRoot = EditorPrefs.GetString(PRE + "pubRoot", string.Empty);
		mPlatform = EditorPrefs.GetString(PRE + "platform",
			EditorUserBuildSettings.activeBuildTarget.ToString());
		mOldBase = EditorPrefs.GetString(PRE + "oldBase", string.Empty);
		mNewBase = EditorPrefs.GetString(PRE + "newBase", string.Empty);
	}

	void OnDisable()
	{
		mPassword = null;
	}

	void OnGUI()
	{
		mScroll = EditorGUILayout.BeginScrollView(mScroll);
		EditorGUILayout.LabelField("项目外签名密钥", EditorStyles.boldLabel);
		EditorGUILayout.HelpBox(
			"目录固定为 ~/.myframework-keys/{project}/{test|prod}/。窗口只保存路径和非秘密字段；密码只在当前窗口内存中存在。",
			MessageType.Info);
		string project = EditorGUILayout.TextField("项目密钥标识", mProject);
		int envAt = GUILayout.Toolbar(mEnvAt, mEnvs);
		if (project != mProject || envAt != mEnvAt)
		{
			mProject = project;
			mEnvAt = envAt;
			EditorPrefs.SetString(PRE + "project", mProject);
		}
		mPassword = EditorGUILayout.PasswordField("私钥密码（可选）", mPassword ?? string.Empty);
		drawState();
		EditorGUILayout.Space();
		drawLifecycle();
		EditorGUILayout.Space();
		drawRotation();
		EditorGUILayout.Space();
		EditorGUILayout.LabelField("状态", mStatus, EditorStyles.helpBox);
		EditorGUILayout.EndScrollView();
	}

	void drawState()
	{
		try
		{
			RelKeyStore store = current();
			EditorGUILayout.LabelField("目录", store.directory, EditorStyles.miniLabel);
			drawInfo("Active", store.active, store.activePrivateKeyPath);
			drawInfo("Pending", store.pending, store.pendingPrivateKeyPath);
			EditorGUILayout.LabelField("过渡Latest", store.hasTransition ? "已签发" : "未签发",
				EditorStyles.miniLabel);
		}
		catch (Exception ex)
		{
			EditorGUILayout.HelpBox(ex.Message, MessageType.Warning);
		}
	}

	static void drawInfo(string label, RelKeyInfo info, string path)
	{
		if (info == null)
		{
			EditorGUILayout.LabelField(label, "（无）", EditorStyles.miniLabel);
			return;
		}
		EditorGUILayout.LabelField(label, info.id + (info.encrypted ? "（加密）" : "（未加密）"),
			EditorStyles.miniLabel);
		EditorGUILayout.SelectableLabel(path, EditorStyles.textField, GUILayout.Height(20));
		EditorGUILayout.SelectableLabel(info.publicKey, EditorStyles.textArea,
			GUILayout.Height(42));
	}

	void drawLifecycle()
	{
		EditorGUILayout.LabelField("创建与旧配置迁移", EditorStyles.boldLabel);
		using (new EditorGUILayout.HorizontalScope())
		{
			if (GUILayout.Button("创建初始密钥"))
			{
				runSecret("创建初始密钥", password =>
				{
					RelKeyInfo info = current().create(password);
					return "已创建 " + info.id + "；请把公钥写入下一份Base配置";
				});
			}
			EditorGUI.BeginDisabledGroup(!RelKeyMigration.hasLegacy);
			if (GUILayout.Button("导出旧EditorPrefs"))
			{
				run("迁移旧私钥", () =>
				{
					RelKeyInfo info = RelKeyMigration.export(mProject, env(), passwordFactory());
					return "已导出 " + info.id + "；核验新路径后请手动点击清除旧EditorPrefs";
				});
			}
			if (GUILayout.Button("确认后清除旧EditorPrefs"))
			{
				if (EditorUtility.DisplayDialog("清除旧私钥配置",
					"仅在已经核验约定目录中的私钥可用后继续。此操作只清除旧EditorPrefs值。",
					"清除", "取消"))
				{
					RelKeyMigration.clearLegacy();
					mStatus = "旧EditorPrefs私钥配置已清除";
				}
			}
			EditorGUI.EndDisabledGroup();
		}
	}

	void drawRotation()
	{
		EditorGUILayout.LabelField("轮换（严格顺序）", EditorStyles.boldLabel);
		EditorGUILayout.LabelField("1. 生成pending → 2. 旧私钥签发过渡Latest → 3. 下一Base冻结新公钥 → 4. 归档禁用旧私钥",
			EditorStyles.wordWrappedMiniLabel);
		string root = pathField("Release输出根目录", mPubRoot);
		string platform = EditorGUILayout.TextField("平台", mPlatform);
		string oldBase = EditorGUILayout.TextField("旧Base", mOldBase);
		string newBase = EditorGUILayout.TextField("下一Base", mNewBase);
		if (root != mPubRoot || platform != mPlatform || oldBase != mOldBase ||
			newBase != mNewBase)
		{
			mPubRoot = root;
			mPlatform = platform;
			mOldBase = oldBase;
			mNewBase = newBase;
			EditorPrefs.SetString(PRE + "pubRoot", mPubRoot);
			EditorPrefs.SetString(PRE + "platform", mPlatform);
			EditorPrefs.SetString(PRE + "oldBase", mOldBase);
			EditorPrefs.SetString(PRE + "newBase", mNewBase);
		}
		using (new EditorGUILayout.HorizontalScope())
		{
			if (GUILayout.Button("1 生成 Pending"))
			{
				runSecret("开始密钥轮换", password =>
				{
					RelKeyInfo info = current().beginRotation(password);
					return "Pending " + info.id + " 已生成；下一份Base必须使用其公钥";
				});
			}
			if (GUILayout.Button("2 签发过渡 Latest"))
			{
				run("签发过渡Latest", signTransition);
			}
			if (GUILayout.Button("4 完成并归档旧私钥"))
			{
				if (EditorUtility.DisplayDialog("完成密钥轮换",
					"确认下一份Base已冻结Pending公钥？完成后旧私钥会改为.disabled并设为只读。",
					"完成轮换", "取消"))
				{
					run("完成密钥轮换", completeRotation);
				}
			}
		}
	}

	string signTransition()
	{
		UpdCfg cfg = RelBuild.loadBase(mPubRoot, env(), mPlatform, mOldBase);
		string latestPath = Path.Combine(Path.GetFullPath(mPubRoot), env(), "latest",
			mPlatform, mOldBase + ".json");
		byte[] signed = File.ReadAllBytes(latestPath);
		UpdBox oldBox = UpdJson.box(signed);
		UpdRet<byte[]> opened = new UpdSign(cfg.pubKey).open(oldBox);
		if (!opened.ok) throw new InvalidDataException("现有Latest验签失败:" + opened.err);
		UpdLatest latest = UpdJson.latest(opened.value);
		byte[] transition = current().signTransition(cfg, latest, passwordFactory());
		string dir = Path.Combine(Path.GetFullPath(mPubRoot), env(), "rotation", mPlatform);
		Directory.CreateDirectory(dir);
		string path = Path.Combine(dir, mOldBase + "-transition-latest.json");
		using FileStream output = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
		output.Write(transition, 0, transition.Length);
		output.Flush(true);
		return "过渡Latest已输出并记录:" + path;
	}

	string completeRotation()
	{
		string archived = current().completeRotation(mPubRoot, mPlatform, mNewBase);
		return "轮换完成；旧私钥已归档禁用:" + archived;
	}

	RelKeyStore current()
	{
		return RelKeyStore.open(mProject, env());
	}

	string env()
	{
		return mEnvs[Mathf.Clamp(mEnvAt, 0, mEnvs.Length - 1)];
	}

	Func<char[]> passwordFactory()
	{
		string value = mPassword;
		return string.IsNullOrEmpty(value) ? null : () => value.ToCharArray();
	}

	void runSecret(string name, Func<char[], string> action)
	{
		char[] password = string.IsNullOrEmpty(mPassword) ? null : mPassword.ToCharArray();
		try
		{
			run(name, () => action(password));
		}
		finally
		{
			if (password != null) Array.Clear(password, 0, password.Length);
		}
	}

	void run(string name, Func<string> action)
	{
		try
		{
			mStatus = action();
			Debug.Log("[RelKeyWin] " + name + ": " + mStatus);
		}
		catch (Exception ex)
		{
			mStatus = name + "失败: " + ex.Message;
			Debug.LogError("[RelKeyWin] " + name + "失败\n" + ex);
		}
		Repaint();
	}

	static string pathField(string label, string value)
	{
		using (new EditorGUILayout.HorizontalScope())
		{
			string next = EditorGUILayout.TextField(label, value);
			if (GUILayout.Button("...", GUILayout.Width(28)))
			{
				string start = string.IsNullOrWhiteSpace(next) ?
					Path.GetFullPath(Path.Combine(Application.dataPath, "..")) : next;
				string picked = EditorUtility.OpenFolderPanel(label, start, string.Empty);
				if (!string.IsNullOrEmpty(picked)) next = picked;
			}
			return next;
		}
	}
}
