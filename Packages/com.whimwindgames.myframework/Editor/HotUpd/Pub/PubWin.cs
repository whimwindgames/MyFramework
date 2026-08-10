using System;
using System.IO;
using UnityEditor;
using UnityEngine;

// 资源发布窗口。全部发布逻辑在PubFlow/PubCli，本窗口只是薄壳：
// SSH连接与主机密钥信任、发布目录与签名私钥路径、扫描/发布/远端诊断/回退。
public sealed class PubWin : EditorWindow
{
	const string PRE = "MyFramework.HotUpd.Pub.Win.";

	SshCfg mSsh;
	string mPubRoot;
	string mPrivKey;
	string mStatus = "就绪";
	string mKnownFp;
	SshHostKey mScanned;
	PubItem[] mItems = Array.Empty<PubItem>();
	Vector2 mScroll;

	[MenuItem("MyFramework/HotUpdate/资源发布")]
	static void open()
	{
		PubWin win = GetWindow<PubWin>(false, "资源发布", true);
		win.minSize = new Vector2(520, 480);
		win.Show();
	}

	void OnEnable()
	{
		mSsh = SshCfgStore.load();
		mPubRoot = EditorPrefs.GetString(PRE + "pubRoot", string.Empty);
		mPrivKey = EditorPrefs.GetString(PRE + "privKey", string.Empty);
		refreshKnown();
	}

	void OnGUI()
	{
		drawSsh();
		EditorGUILayout.Space();
		drawPub();
		EditorGUILayout.Space();
		drawItems();
		EditorGUILayout.Space();
		EditorGUILayout.LabelField("状态", mStatus, EditorStyles.helpBox);
	}

	void drawSsh()
	{
		EditorGUILayout.LabelField("SSH 发布连接", EditorStyles.boldLabel);
		mSsh.host = EditorGUILayout.TextField("服务器", mSsh.host);
		mSsh.port = EditorGUILayout.IntField("端口", mSsh.port);
		mSsh.user = EditorGUILayout.TextField("用户", mSsh.user);
		mSsh.key = pathField("SSH私钥", mSsh.key);
		mSsh.url = EditorGUILayout.TextField("客户端资源地址", mSsh.url);
		EditorGUILayout.LabelField("已信任主机指纹",
			string.IsNullOrEmpty(mKnownFp) ? "（未信任）" : mKnownFp,
			EditorStyles.miniLabel);
		using (new EditorGUILayout.HorizontalScope())
		{
			EditorGUI.BeginDisabledGroup(!mSsh.isReady);
			if (GUILayout.Button("保存设置"))
			{
				run("保存设置", () =>
				{
					SshCfgStore.save(mSsh);
					refreshKnown();
					return "SSH发布设置已保存";
				});
			}
			if (GUILayout.Button("检查连接"))
			{
				run("检查连接", () => mSsh.check());
			}
			if (GUILayout.Button("读取主机密钥"))
			{
				run("读取主机密钥", () =>
				{
					mScanned = SshStore.scanHost(mSsh);
					return "主机密钥指纹:" + mScanned.fingerprint +
						"\n请在服务器控制台核对后再信任";
				});
			}
			EditorGUI.EndDisabledGroup();
			EditorGUI.BeginDisabledGroup(mScanned == null);
			if (GUILayout.Button("信任该主机", GUILayout.Width(90)))
			{
				run("信任主机", () =>
				{
					SshStore.trustHost(mScanned);
					mScanned = null;
					refreshKnown();
					return "主机密钥已写入信任列表";
				});
			}
			EditorGUI.EndDisabledGroup();
		}
	}

	void drawPub()
	{
		EditorGUILayout.LabelField("发布配置", EditorStyles.boldLabel);
		string nextRoot = pathField("发布输出目录", mPubRoot);
		string nextKey = pathField("Latest签名私钥", mPrivKey);
		if (nextRoot != mPubRoot || nextKey != mPrivKey)
		{
			mPubRoot = nextRoot;
			mPrivKey = nextKey;
			EditorPrefs.SetString(PRE + "pubRoot", mPubRoot);
			EditorPrefs.SetString(PRE + "privKey", mPrivKey);
		}
		EditorGUILayout.LabelField("发布平台",
			EditorUserBuildSettings.activeBuildTarget.ToString(), EditorStyles.miniLabel);
		if (GUILayout.Button("扫描本地 Release"))
		{
			run("扫描本地 Release", () =>
			{
				mItems = PubFlow.scan(makeEnv(), platform());
				return "发现 " + mItems.Length + " 个待发布Release";
			});
		}
	}

	void drawItems()
	{
		EditorGUILayout.LabelField("本地 Release", EditorStyles.boldLabel);
		mScroll = EditorGUILayout.BeginScrollView(mScroll);
		foreach (PubItem item in mItems)
		{
			using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
			{
				EditorGUILayout.LabelField(
					item.env + " / " + item.baseId + " / seq " + item.seq,
					item.relId, EditorStyles.miniLabel);
				EditorGUILayout.LabelField(item.fileCnt + " 个文件, " +
					(item.totalSize / (1024.0 * 1024.0)).ToString("F1") + " MB",
					EditorStyles.miniLabel);
				using (new EditorGUILayout.HorizontalScope())
				{
					if (GUILayout.Button("发布"))
					{
						if (EditorUtility.DisplayDialog("发布 Release",
							"确定发布 " + item.relId + " 到 " + item.env + " ?",
							"发布", "取消"))
						{
							runFlow("发布", flow => flow.pubRel(item.platform, item.relId));
						}
					}
					if (GUILayout.Button("远端诊断"))
					{
						runFlow("远端诊断", flow =>
						{
							PubHead head = flow.remote(item);
							return "远端Latest:" + (head.has ? head.relId + " seq " + head.seq : "无") +
								"\n上一版:" + (head.hasPrevious ?
									head.previousRelId + " seq " + head.previousSeq : "无");
						});
					}
					if (GUILayout.Button("回退到上一版"))
					{
						if (EditorUtility.DisplayDialog("回退",
							"确定把 " + item.env + " / " + item.baseId +
							" 回退到远端上一版吗?", "回退", "取消"))
						{
							runFlow("回退", flow => flow.rollback(item));
						}
					}
				}
			}
		}
		EditorGUILayout.EndScrollView();
	}

	PubEnv makeEnv()
	{
		return new PubEnv
		{
			pubRoot = mPubRoot,
			privateKeyPath = mPrivKey,
		};
	}

	static string platform()
	{
		return EditorUserBuildSettings.activeBuildTarget.ToString();
	}

	void runFlow<T>(string name, Func<PubFlow, T> action)
	{
		run(name, () =>
		{
			SshCfg cfg = SshCfgStore.load();
			using PubFlow flow = new(new SshStore(cfg), makeEnv(),
				(text, done, total) => EditorUtility.DisplayProgressBar(
					name, text, total > 0 ? (float)done / total : 0f));
			try
			{
				T result = action(flow);
				return name + "完成" + (result is long seq ? " seq " + seq : "");
			}
			finally
			{
				EditorUtility.ClearProgressBar();
			}
		});
	}

	void run(string name, Func<string> action)
	{
		try
		{
			mStatus = action();
			Debug.Log("[PubWin] " + name + ": " + mStatus);
		}
		catch (Exception ex)
		{
			mStatus = name + "失败: " + ex.Message;
			Debug.LogError("[PubWin] " + name + "失败\n" + ex);
		}
		Repaint();
	}

	void refreshKnown()
	{
		try
		{
			mKnownFp = mSsh.isReady ? SshStore.knownFingerprint(mSsh) : string.Empty;
		}
		catch
		{
			mKnownFp = string.Empty;
		}
	}

	static string pathField(string label, string value)
	{
		using (new EditorGUILayout.HorizontalScope())
		{
			string next = EditorGUILayout.TextField(label, value);
			if (GUILayout.Button("...", GUILayout.Width(28)))
			{
				string dir = string.IsNullOrWhiteSpace(next) ?
					Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) :
					Path.GetDirectoryName(next);
				string picked = label.Contains("目录") ?
					EditorUtility.OpenFolderPanel(label, dir, string.Empty) :
					EditorUtility.OpenFilePanel(label, dir, string.Empty);
				if (!string.IsNullOrEmpty(picked)) next = picked;
			}
			return next;
		}
	}
}
