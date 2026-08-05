using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

public sealed class RelReq
{
	public string src;
	public string root;
	public string privateKey;
	public string releaseId;
	public string mapSrc;
	public UpdCfg cfg;
	public HotPlan plan;
	public bool newBase;
}

public sealed class RelCheck
{
	public string releaseId;
	public long seq;
	public string manifestSha;
	public int fileCount;
	public long totalSize;
}

public sealed class RelView
{
	public string releaseId;
	public string manifestSha;
	public string[] dlls;
	public string[] aots;
	public string[] bundles;
	public string[] scenes;
	public AbItem[] items;
	public UpdFile[] files;
}

public static class RelBuild
{
	[Serializable]
	sealed class RelBase
	{
		public int schema;
		public string env;
		public string platform;
		public string baseId;
		public string baseUrl;
		public string pubKey;
	}

	internal sealed class RelData
	{
		public UpdMan man;
		public byte[] raw;
		public string sha;
	}

	sealed class RelSelf
	{
		public UpdLatest head;
		public RelData rel;
	}

	static readonly UTF8Encoding sUtf8 = new(false, true);

	// 只校验发布身份、Base冻结状态、签名密钥和输出边界，不要求资源已生成。
	public static void validateConfig(RelReq req)
	{
		RelReq value = normalize(req, false);
		string root = envRoot(checkRoot(value.root), value.cfg.env);
		probeWritable(root);
		checkBase(root, value.cfg, value.newBase);
		checkKey(value);
	}

	public static RelView preview(RelReq req)
	{
		RelReq value = normalize(req, true);
		UpdFile[] files = scan(value.src);
		checkFiles(value.cfg, files, value.plan);
		string relId = nextId(value);
		UpdMan man = makeMan(value.cfg, relId, files);
		byte[] raw = json(man);
		checkMan(value.cfg, man, raw);
		string index = Path.Combine(value.src, osPath(value.cfg.resList));
		List<AbItem> items = AbIndex.decode(File.ReadAllBytes(index));
		HashSet<string> bundles = new(StringComparer.Ordinal);
		List<string> scenes = new();
		foreach (AbItem item in items)
		{
			bundles.Add(item.bundle);
			if (!string.IsNullOrEmpty(item.scene)) scenes.Add(item.scene);
		}
		string[] bundleVals = new string[bundles.Count];
		bundles.CopyTo(bundleVals);
		Array.Sort(bundleVals, StringComparer.Ordinal);
		scenes.Sort(StringComparer.Ordinal);
		return new RelView
		{
			releaseId = relId,
			manifestSha = UpdHash.data(raw),
			dlls = (string[])value.cfg.codeDlls.Clone(),
			aots = (string[])value.cfg.aotDlls.Clone(),
			bundles = bundleVals,
			scenes = scenes.ToArray(),
			items = items.ToArray(),
			files = files,
		};
	}

	public sealed class RelPending : IDisposable
	{
		readonly RelReq mReq;
		readonly string mRoot;
		readonly string mRelDir;
		readonly string mTmp;
		readonly string mMapDst;
		readonly string mMapTmp;
		readonly RelData mPlanned;
		readonly byte[] mLatestRaw;
		readonly FileStream mGate;
		bool mRelMoved;
		bool mMapMoved;
		bool mBaseMade;
		bool mPromoted;
		bool mPublished;
		bool mKeepRel;
		bool mDisposed;

		internal RelPending(RelReq req, string root, string relDir, string tmp,
			string mapDst, string mapTmp, RelData planned, byte[] latestRaw, FileStream gate)
		{
			mReq = req;
			mRoot = root;
			mRelDir = relDir;
			mTmp = tmp;
			mMapDst = mapDst;
			mMapTmp = mapTmp;
			mPlanned = planned;
			mLatestRaw = latestRaw;
			mGate = gate;
		}

		public string releaseId => mReq.releaseId;

		public void promote()
		{
			ensureOpen();
			if (mPromoted) throw new InvalidOperationException("Release候选已经提升");
			Directory.CreateDirectory(Path.GetDirectoryName(mRelDir));
			Directory.Move(mTmp, mRelDir);
			mRelMoved = true;
			RelData rel = readMan(mRelDir, mReq.cfg, releaseId);
			if (rel.sha != mPlanned.sha || rel.raw.Length != mPlanned.raw.Length)
				throw new InvalidDataException("Release提升后Manifest不一致");
			if (!string.IsNullOrEmpty(mMapTmp))
			{
				Directory.CreateDirectory(Path.GetDirectoryName(mMapDst));
				File.Move(mMapTmp, mMapDst);
				mMapMoved = true;
			}
			mPromoted = true;
		}

		public string publish()
		{
			ensureOpen();
			if (!mPromoted || mPublished) throw new InvalidOperationException("Release尚未提升或已经发布");
			try
			{
				if (mReq.newBase)
				{
					writeBase(mRoot, mReq.cfg);
					mBaseMade = true;
				}
				else checkBase(mRoot, mReq.cfg, false);
				setLatest(mRoot, mReq.cfg, mPlanned, mLatestRaw, "Latest发布", () => mKeepRel = true);
				mPublished = true;
				return releaseId;
			}
			catch
			{
				if (mBaseMade && !mKeepRel)
				{
					try { File.Delete(basePath(mRoot, mReq.cfg)); mBaseMade = false; }
					catch (Exception ex)
					{
						mKeepRel = true;
						Debug.LogError("Base回滚失败，请立即停止发布:" + ex);
					}
				}
				throw;
			}
		}

		public void Dispose()
		{
			if (mDisposed) return;
			if (!mPublished && !mKeepRel)
			{
				cleanup(() => { if (File.Exists(mMapTmp)) File.Delete(mMapTmp); });
				cleanup(() => { if (Directory.Exists(mTmp)) Directory.Delete(mTmp, true); });
				cleanup(() => { if (mMapMoved && File.Exists(mMapDst)) File.Delete(mMapDst); });
				cleanup(() => { if (mRelMoved && Directory.Exists(mRelDir)) Directory.Delete(mRelDir, true); });
				cleanup(() => { if (mBaseMade && File.Exists(basePath(mRoot, mReq.cfg))) File.Delete(basePath(mRoot, mReq.cfg)); });
			}
			else if (mKeepRel)
			{
				Debug.LogError("Latest或Base状态未知，已保留Release与符号映射；请停止生产并人工核验");
			}
			mGate.Dispose();
			mDisposed = true;
		}

		void ensureOpen()
		{
			if (mDisposed) throw new ObjectDisposedException(nameof(RelPending));
		}

		static void cleanup(Action action)
		{
			try { action(); }
			catch (Exception ex) { Debug.LogError("未发布Release清理失败，请勿上传:" + ex); }
		}
	}

	public static RelPending prepare(RelReq req)
	{
		validateConfig(req);
		RelReq value = normalize(req, true);
		string root = envRoot(getRoot(value.src, value.root), value.cfg.env);
		FileStream gate = takeLock(root);
		string tmp = null;
		string mapTmp = null;
		try
		{
			checkBase(root, value.cfg, value.newBase);
			long seq = Math.Max(nextSeq(readHead(root, value.cfg)), nextRelSeq(root, value.cfg));
			value.releaseId = makeId(value.cfg.env, value.cfg.platform, value.cfg.baseId, seq);
			req.releaseId = value.releaseId;
			if (!UpdFmt.isId(value.releaseId)) throw new InvalidDataException("自动生成的Release标识非法");
			UpdFile[] files = scan(value.src);
			checkFiles(value.cfg, files, value.plan);
			UpdMan man = makeMan(value.cfg, value.releaseId, files);
			byte[] manRaw = json(man);
			checkMan(value.cfg, man, manRaw);
			RelData planned = new() { man = man, raw = manRaw, sha = UpdHash.data(manRaw) };
			byte[] latestRaw = makeLatest(value, planned, seq, new RelSign(value.privateKey));
			string relDir = Path.Combine(root, "releases", value.releaseId);
			if (Directory.Exists(relDir)) throw new IOException("Release已存在且不可覆盖:" + value.releaseId);
			string stageRoot = Path.Combine(root, ".staging");
			if (File.Exists(stageRoot)) throw new InvalidDataException("Release候选目录被文件占用");
			if (Directory.Exists(stageRoot)) ensureNoLinks(stageRoot);
			string token = Guid.NewGuid().ToString("N");
			tmp = Path.Combine(stageRoot, value.releaseId + "-" + token);
			string mapDst = null;
			if (!string.IsNullOrEmpty(value.mapSrc))
			{
				mapDst = Path.Combine(root, "symbols", value.releaseId + ".xml");
				mapTmp = Path.Combine(stageRoot, value.releaseId + "-" + token + ".xml");
				if (File.Exists(mapDst)) throw new IOException("Release符号映射已存在:" + value.releaseId);
				copyMap(value.mapSrc, mapTmp);
			}
			copyFiles(value.src, tmp, files);
			writeNew(Path.Combine(tmp, "manifest.json"), manRaw);
			RelData checkedRel = readRel(tmp, value.cfg, value.releaseId);
			if (checkedRel.sha != planned.sha || checkedRel.raw.Length != planned.raw.Length)
				throw new InvalidDataException("Release候选Manifest不一致");
			return new RelPending(value, root, relDir, tmp, mapDst, mapTmp, planned, latestRaw, gate);
		}
		catch (Exception sourceError)
		{
			List<Exception> errors = new() { sourceError };
			try { if (!string.IsNullOrEmpty(tmp) && Directory.Exists(tmp)) Directory.Delete(tmp, true); }
			catch (Exception ex) { errors.Add(ex); }
			try { if (!string.IsNullOrEmpty(mapTmp) && File.Exists(mapTmp)) File.Delete(mapTmp); }
			catch (Exception ex) { errors.Add(ex); }
			try { gate.Dispose(); }
			catch (Exception ex) { errors.Add(ex); }
			if (errors.Count > 1) throw new AggregateException("Release准备失败且候选或发布锁清理失败，请勿继续生产", errors);
			throw;
		}
	}

	public static string make(RelReq req)
	{
		using RelPending pending = prepare(req);
		pending.promote();
		return pending.publish();
	}

	public static long point(RelReq req)
	{
		RelReq value = normalize(req, false);
		if (!UpdFmt.isId(value.releaseId)) throw new InvalidDataException("回退Release标识错误");
		string root = envRoot(getRoot(null, value.root), value.cfg.env);
		checkBase(root, value.cfg, false);
		checkKey(value);
		using FileStream gate = takeLock(root);
		UpdLatest head = readHead(root, value.cfg) ?? throw new FileNotFoundException("本地Latest不存在");
		long seq = Math.Max(nextSeq(head), nextRelSeq(root, value.cfg));
		RelData rel = readRel(Path.Combine(root, "releases", value.releaseId), value.cfg, value.releaseId);
		byte[] raw = makeLatest(value, rel, seq, new RelSign(value.privateKey));
		setLatest(root, value.cfg, rel, raw, "Latest回退");
		return seq;
	}

	public static RelCheck verify(RelReq req)
	{
		RelReq value = normalize(req, false);
		string root = envRoot(getRoot(null, value.root), value.cfg.env);
		checkBase(root, value.cfg, false);
		using FileStream gate = takeLock(root);
		RelSelf self = readSelf(root, value.cfg, true);
		long total = 0;
		foreach (UpdFile file in self.rel.man.files) total = checked(total + file.size);
		return new RelCheck
		{
			releaseId = self.head.releaseId,
			seq = self.head.seq,
			manifestSha = self.rel.sha,
			fileCount = self.rel.man.files.Length,
			totalSize = total,
		};
	}

	public static string nextId(RelReq req)
	{
		RelReq value = normalize(req, false);
		string root = envRoot(getRoot(null, value.root), value.cfg.env);
		checkBase(root, value.cfg, value.newBase);
		long relSeq = nextRelSeq(root, value.cfg);
		UpdLatest head = readHead(root, value.cfg);
		if (head == null && relSeq != 1)
			throw new InvalidDataException("本地Latest缺失，已有Release无法确定Base身份");
		long seq = Math.Max(nextSeq(head), relSeq);
		string result = makeId(value.cfg.env, value.cfg.platform, value.cfg.baseId, seq);
		if (!UpdFmt.isId(result)) throw new InvalidDataException("自动生成的Release标识非法");
		return result;
	}

	static RelReq normalize(RelReq req, bool needSource)
	{
		if (req?.cfg == null) throw new ArgumentNullException(nameof(req));
		UpdCfg cfg = copyCfg(req.cfg);
		UpdRule.cfg(cfg);
		HotPlan plan = req.plan ?? HotList.fromCfg(cfg);
		HotList.chk(plan);
		HotList.chkCfg(cfg, plan.hot);
		if (needSource && string.IsNullOrWhiteSpace(req.src)) throw new InvalidDataException("发布资源目录不能为空");
		if ((cfg.env != "test" && cfg.env != "prod") || !UpdFmt.isId(cfg.platform) || !UpdFmt.isId(cfg.baseId))
			throw new InvalidDataException("发布身份未配置");
		return new RelReq
		{
			src = req.src,
			root = req.root,
			privateKey = req.privateKey,
			releaseId = req.releaseId,
			mapSrc = req.mapSrc,
			cfg = cfg,
			plan = plan,
			newBase = req.newBase,
		};
	}

	static UpdCfg copyCfg(UpdCfg cfg)
	{
		return new UpdCfg
		{
			baseUrl = cfg.baseUrl,
			env = cfg.env,
			platform = cfg.platform,
			baseId = cfg.baseId,
			pubKey = cfg.pubKey,
			retry = cfg.retry,
			timeout = cfg.timeout,
			aotDlls = cfg.aotDlls == null ? null : (string[])cfg.aotDlls.Clone(),
			codeDlls = cfg.codeDlls == null ? null : (string[])cfg.codeDlls.Clone(),
			entryDll = cfg.entryDll,
			hotId = cfg.hotId,
			secret = cfg.secret,
			resList = cfg.resList,
		};
	}

	static UpdMan makeMan(UpdCfg cfg, string relId, UpdFile[] files)
	{
		return new UpdMan
		{
			schema = UpdLim.Schema,
			env = cfg.env,
			releaseId = relId,
			platform = cfg.platform,
			baseId = cfg.baseId,
			aotDlls = (string[])cfg.aotDlls.Clone(),
			codeDlls = (string[])cfg.codeDlls.Clone(),
			entryDll = cfg.entryDll,
			hotId = cfg.hotId,
			secret = cfg.secret,
			files = files,
		};
	}

	static void checkKey(RelReq req)
	{
		if (string.IsNullOrWhiteSpace(req.privateKey)) throw new InvalidDataException("发布签名私钥未配置");
		RelSign signer = new(req.privateKey);
		byte[] probe = sUtf8.GetBytes("MyFramework.Release.KeyCheck.v1");
		UpdBox box = new()
		{
			schema = UpdLim.Schema,
			alg = "ES256",
			data = Convert.ToBase64String(probe),
			sig = Convert.ToBase64String(signer.sign(probe)),
		};
		UpdRet<byte[]> opened = new UpdSign(req.cfg.pubKey).open(box);
		if (!opened.ok || !same(probe, opened.value))
			throw new InvalidDataException("Latest私钥与项目公钥不匹配:" + opened.err);
	}

	static void checkBase(string root, UpdCfg cfg, bool newBase)
	{
		string path = basePath(root, cfg);
		if (!File.Exists(path))
		{
			if (!newBase) throw new InvalidDataException("Base信任记录不存在，首次生产必须指定newBase");
			return;
		}
		if (newBase) throw new InvalidDataException("Base已经构建并冻结，请提升客户端构建号");
		byte[] raw = read(path, UpdLim.StateMax);
		RelBase value = JsonUtility.FromJson<RelBase>(UpdFmt.text(raw));
		if (value == null || value.schema != UpdLim.Schema || value.env != cfg.env ||
			value.platform != cfg.platform || value.baseId != cfg.baseId ||
			value.baseUrl != cfg.baseUrl || value.pubKey != cfg.pubKey || !same(raw, json(value)))
			throw new InvalidDataException("Base信任身份已冻结且与当前配置不一致");
	}

	static void writeBase(string root, UpdCfg cfg)
	{
		string path = basePath(root, cfg);
		if (File.Exists(path)) throw new IOException("Base信任记录已存在");
		RelBase value = new()
		{
			schema = UpdLim.Schema,
			env = cfg.env,
			platform = cfg.platform,
			baseId = cfg.baseId,
			baseUrl = cfg.baseUrl,
			pubKey = cfg.pubKey,
		};
		writeNew(path, json(value));
		checkBase(root, cfg, false);
	}

	static byte[] makeLatest(RelReq req, RelData rel, long seq, RelSign signer)
	{
		UpdCfg cfg = req.cfg;
		UpdLatest latest = new()
		{
			schema = UpdLim.Schema,
			env = cfg.env,
			platform = cfg.platform,
			baseId = cfg.baseId,
			seq = seq,
			releaseId = rel.man.releaseId,
			manifestSha = rel.sha,
			manifestSize = rel.raw.Length,
		};
		byte[] data = json(latest);
		UpdBox box = new()
		{
			schema = UpdLim.Schema,
			alg = "ES256",
			data = Convert.ToBase64String(data),
			sig = Convert.ToBase64String(signer.sign(data)),
		};
		byte[] raw = json(box);
		verifyBox(cfg, raw, rel);
		return raw;
	}

	static void setLatest(string root, UpdCfg cfg, RelData rel, byte[] raw, string action, Action onRisk = null)
	{
		string path = latestPath(root, cfg);
		bool hadLatest = File.Exists(path);
		byte[] previous = hadLatest ? read(path, UpdLim.LatestMax) : null;
		try { writeLatest(root, cfg, rel, raw); }
		catch (Exception sourceError)
		{
			try
			{
				if (hadLatest) { writeAtom(path, previous); _ = readLatest(root, cfg); }
				else if (File.Exists(path)) File.Delete(path);
			}
			catch (Exception undoError)
			{
				onRisk?.Invoke();
				throw new AggregateException(action + "失败且原Latest恢复失败，请立即停止上传", sourceError, undoError);
			}
			throw;
		}
	}

	static void writeLatest(string root, UpdCfg cfg, RelData rel, byte[] raw)
	{
		string path = latestPath(root, cfg);
		writeAtom(path, raw);
		byte[] saved = read(path, UpdLim.LatestMax);
		if (!same(raw, saved)) throw new IOException("Latest回读不一致");
		verifyBox(cfg, saved, rel);
	}

	static UpdLatest readLatest(string root, UpdCfg cfg)
	{
		UpdLatest latest = readHead(root, cfg);
		if (latest == null) return null;
		RelData rel = readRel(Path.Combine(root, "releases", latest.releaseId), cfg, latest.releaseId);
		checkLatest(latest, rel);
		return latest;
	}

	static RelSelf readSelf(string root, UpdCfg cfg, bool withFiles)
	{
		UpdLatest head = readHead(root, cfg) ?? throw new FileNotFoundException("本地Latest不存在", latestPath(root, cfg));
		RelData rel = readMan(Path.Combine(root, "releases", head.releaseId), cfg, head.releaseId);
		checkLatest(head, rel);
		if (withFiles) readFiles(Path.Combine(root, "releases", head.releaseId), rel);
		return new RelSelf { head = head, rel = rel };
	}

	static UpdLatest readHead(string root, UpdCfg cfg)
	{
		string path = latestPath(root, cfg);
		if (!File.Exists(path)) return null;
		UpdLatest latest = openBox(cfg, read(path, UpdLim.LatestMax));
		UpdRule.latest(cfg, latest);
		return latest;
	}

	static void checkLatest(UpdLatest latest, RelData rel)
	{
		if (rel.sha != latest.manifestSha || rel.raw.Length != latest.manifestSize)
			throw new InvalidDataException("现有Latest指向的Manifest不一致");
	}

	static void verifyBox(UpdCfg cfg, byte[] raw, RelData rel)
	{
		UpdLatest latest = openBox(cfg, raw);
		UpdRule.latest(cfg, latest);
		if (latest.releaseId != rel.man.releaseId || latest.manifestSha != rel.sha ||
			latest.manifestSize != rel.raw.Length)
			throw new InvalidDataException("Latest与Manifest不一致");
	}

	static UpdLatest openBox(UpdCfg cfg, byte[] raw)
	{
		UpdBox box = UpdJson.box(raw);
		UpdRet<byte[]> opened = new UpdSign(cfg.pubKey).open(box);
		if (!opened.ok) throw new InvalidDataException("Latest验签失败:" + opened.err);
		return UpdJson.latest(opened.value);
	}

	static RelData readRel(string relDir, UpdCfg cfg, string relId)
	{
		RelData rel = readMan(relDir, cfg, relId);
		readFiles(relDir, rel);
		return rel;
	}

	static RelData readMan(string relDir, UpdCfg cfg, string relId)
	{
		ensureNoLinks(relDir);
		byte[] raw = read(Path.Combine(relDir, "manifest.json"), UpdLim.ManMax);
		UpdMan man = UpdJson.man(raw);
		if (man.releaseId != relId) throw new InvalidDataException("Manifest Release身份错误");
		checkMan(cfg, man, raw);
		return new RelData { man = man, raw = raw, sha = UpdHash.data(raw) };
	}

	static void readFiles(string relDir, RelData rel)
	{
		string fileDir = Path.Combine(relDir, "files");
		foreach (UpdFile file in rel.man.files) checkFile(Path.Combine(fileDir, osPath(file.path)), file);
	}

	static void checkMan(UpdCfg cfg, UpdMan man, byte[] raw)
	{
		UpdRule.man(cfg, new UpdLatest { releaseId = man.releaseId }, man);
		if (raw.Length < 1 || raw.Length > UpdLim.ManMax) throw new InvalidDataException("Manifest大小错误");
	}

	static UpdFile[] scan(string src)
	{
		string root = Path.GetFullPath(src);
		if (!Directory.Exists(root)) throw new DirectoryNotFoundException("发布资源目录不存在:" + root);
		ensureNoLinks(root);
		List<UpdFile> files = new();
		scanDir(new DirectoryInfo(root), root, files);
		files.Sort((left, right) => string.CompareOrdinal(left.path, right.path));
		if (files.Count == 0 || files.Count > UpdLim.FileMax) throw new InvalidDataException("发布文件数量错误");
		return files.ToArray();
	}

	static void scanDir(DirectoryInfo dir, string root, List<UpdFile> files)
	{
		if ((dir.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("发布目录不能包含符号链接:" + dir.FullName);
		FileInfo[] local = dir.GetFiles();
		Array.Sort(local, (left, right) => string.CompareOrdinal(left.Name, right.Name));
		foreach (FileInfo info in local)
		{
			if ((info.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("发布目录不能包含符号链接文件:" + info.FullName);
			if (info.Extension.Equals(".meta", StringComparison.OrdinalIgnoreCase) ||
				info.Name.EndsWith(".manifest", StringComparison.OrdinalIgnoreCase)) continue;
			if (info.Name.Equals("Version", StringComparison.OrdinalIgnoreCase) ||
				info.Name.Equals("FileList", StringComparison.OrdinalIgnoreCase))
				throw new InvalidDataException("发现废弃发布文件:" + info.FullName);
			string path = info.FullName.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar,
				Path.AltDirectorySeparatorChar).Replace('\\', '/');
			if (!UpdFmt.isPath(path) || info.Length <= 0 || info.Length > UpdLim.FileSize)
				throw new InvalidDataException("发布文件非法:" + path);
			files.Add(new UpdFile { path = path, sha256 = UpdHash.file(info.FullName), size = info.Length });
		}
		DirectoryInfo[] dirs = dir.GetDirectories();
		Array.Sort(dirs, (left, right) => string.CompareOrdinal(left.Name, right.Name));
		foreach (DirectoryInfo child in dirs) scanDir(child, root, files);
	}

	static void checkFiles(UpdCfg cfg, UpdFile[] files, HotPlan plan)
	{
		HotList.chk(plan);
		HotList.chkCfg(cfg, plan.hot);
		HashSet<string> paths = new(StringComparer.Ordinal);
		HashSet<string> managed = new(StringComparer.Ordinal);
		foreach (UpdFile file in files)
		{
			paths.Add(file.path);
			if (!file.path.Contains('/') && file.path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
				throw new InvalidDataException("Stage顶层禁止保留原始DLL:" + file.path);
			if (!file.path.Contains('/') && file.path.EndsWith(".dll.bytes", StringComparison.OrdinalIgnoreCase))
				managed.Add(file.path);
		}
		HashSet<string> expected = new(StringComparer.Ordinal);
		foreach (string dll in cfg.codeDlls) { need(paths, dll); expected.Add(dll); }
		foreach (string dll in cfg.aotDlls)
		{
			if (!UpdFmt.isPath(dll) || !dll.EndsWith(".dll.bytes", StringComparison.Ordinal) || !expected.Add(dll))
				throw new InvalidDataException("AOT元数据清单非法:" + dll);
			need(paths, dll);
		}
		if (!managed.SetEquals(expected)) throw new InvalidDataException("Stage顶层受管DLL与本次热更/AOT全集不一致");
		if (!string.IsNullOrEmpty(cfg.secret)) need(paths, cfg.secret);
		need(paths, cfg.resList);
	}

	static void need(HashSet<string> paths, string path)
	{
		if (!paths.Contains(path)) throw new FileNotFoundException("发布必需文件缺失", path);
	}

	static void copyFiles(string src, string dst, UpdFile[] files)
	{
		string fileDir = Path.Combine(dst, "files");
		foreach (UpdFile file in files)
		{
			string from = Path.Combine(src, osPath(file.path));
			string to = Path.Combine(fileDir, osPath(file.path));
			Directory.CreateDirectory(Path.GetDirectoryName(to));
			using FileStream input = new(from, FileMode.Open, FileAccess.Read, FileShare.Read);
			using FileStream output = new(to, FileMode.CreateNew, FileAccess.Write, FileShare.None);
			input.CopyTo(output);
			output.Flush(true);
		}
	}

	static void copyMap(string src, string dst)
	{
		FileInfo input = new(src ?? string.Empty);
		if (!input.Exists || input.Length <= 0 || (input.Attributes & FileAttributes.ReparsePoint) != 0)
			throw new FileNotFoundException("Obfuz符号映射不存在或非法", src);
		Directory.CreateDirectory(Path.GetDirectoryName(dst));
		File.Copy(input.FullName, dst, false);
		FileInfo output = new(dst);
		if (!output.Exists || output.Length != input.Length || UpdHash.file(output.FullName) != UpdHash.file(input.FullName))
			throw new IOException("Obfuz符号映射复制校验失败:" + src);
	}

	static void checkFile(string path, UpdFile file)
	{
		FileInfo info = new(path);
		if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0 ||
			info.Length != file.size || UpdHash.file(path) != file.sha256)
			throw new InvalidDataException("发布文件回读失败:" + file.path);
	}

	static long nextRelSeq(string root, UpdCfg cfg)
	{
		string releases = Path.Combine(root, "releases");
		if (!Directory.Exists(releases)) return 1;
		string prefix = cfg.env + "-" + cfg.platform + "-" + cfg.baseId + "-";
		long max = 0;
		foreach (string dir in Directory.GetDirectories(releases, "*", SearchOption.TopDirectoryOnly))
		{
			string name = Path.GetFileName(dir);
			if (!name.StartsWith(prefix, StringComparison.Ordinal)) continue;
			string tail = name.Substring(prefix.Length);
			if (long.TryParse(tail, NumberStyles.None, CultureInfo.InvariantCulture, out long value) && value > max) max = value;
		}
		return checked(max + 1);
	}

	static long nextSeq(UpdLatest latest)
	{
		return latest == null ? 1 : checked(latest.seq + 1);
	}

	static string makeId(string env, string platform, string baseId, long seq)
	{
		return env + "-" + platform + "-" + baseId + "-" + seq.ToString(CultureInfo.InvariantCulture);
	}

	static FileStream takeLock(string root)
	{
		try { return new FileStream(Path.Combine(root, ".pub.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
		catch (IOException ex) { throw new IOException("已有发布进程持有输出目录锁", ex); }
	}

	static string getRoot(string src, string value)
	{
		string root = checkRoot(value);
		if (!string.IsNullOrEmpty(src))
		{
			string source = trimPath(Path.GetFullPath(src)) + Path.DirectorySeparatorChar;
			string target = root + Path.DirectorySeparatorChar;
			if (target.StartsWith(source, StringComparison.OrdinalIgnoreCase) ||
				source.StartsWith(target, StringComparison.OrdinalIgnoreCase))
				throw new InvalidDataException("发布输出目录与资源目录不能相互包含");
		}
		Directory.CreateDirectory(root);
		ensureNoLinks(root);
		return root;
	}

	static string envRoot(string root, string env)
	{
		string path = Path.Combine(root, env);
		Directory.CreateDirectory(path);
		ensureNoLinks(path);
		return path;
	}

	static string checkRoot(string value)
	{
		if (string.IsNullOrWhiteSpace(value) || !Path.IsPathRooted(value))
			throw new InvalidDataException("请配置绝对发布输出目录");
		string root = trimPath(Path.GetFullPath(value));
		ensureNoLinks(root, true);
		return root;
	}

	static string trimPath(string path)
	{
		string root = Path.GetPathRoot(path);
		return path.Length > root.Length ? path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : path;
	}

	static void ensureNoLinks(string path, bool allowMissing = false)
	{
		string full = Path.GetFullPath(path);
		if (File.Exists(full))
		{
			if ((new FileInfo(full).Attributes & FileAttributes.ReparsePoint) != 0)
				throw new InvalidDataException("发布路径不能经过符号链接:" + full);
			return;
		}
		DirectoryInfo dir = new(full);
		if (!dir.Exists && !allowMissing) throw new DirectoryNotFoundException("发布目录不存在:" + full);
		for (DirectoryInfo cur = dir; cur != null; cur = cur.Parent)
			if (cur.Exists && (cur.Attributes & FileAttributes.ReparsePoint) != 0)
				throw new InvalidDataException("发布路径不能经过符号链接:" + cur.FullName);
	}

	static string latestPath(string root, UpdCfg cfg)
	{
		return Path.Combine(root, "latest", cfg.platform, cfg.baseId + ".json");
	}

	static string basePath(string root, UpdCfg cfg)
	{
		return Path.Combine(root, "base", cfg.platform, cfg.baseId + ".json");
	}

	static byte[] json(object value)
	{
		return sUtf8.GetBytes(JsonUtility.ToJson(value, false));
	}

	static byte[] read(string path, int max)
	{
		FileInfo info = new(path);
		if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0 || info.Length < 1 || info.Length > max)
			throw new InvalidDataException("发布文件大小错误:" + path);
		return File.ReadAllBytes(path);
	}

	static void writeNew(string path, byte[] data)
	{
		string dir = Path.GetDirectoryName(path);
		Directory.CreateDirectory(dir);
		ensureNoLinks(dir);
		using FileStream output = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
		output.Write(data, 0, data.Length);
		output.Flush(true);
	}

	static void writeAtom(string path, byte[] data)
	{
		string dir = Path.GetDirectoryName(path);
		Directory.CreateDirectory(dir);
		ensureNoLinks(dir);
		string tmp = path + ".tmp-" + Guid.NewGuid().ToString("N");
		try
		{
			writeNew(tmp, data);
			if (File.Exists(path)) File.Replace(tmp, path, null);
			else File.Move(tmp, path);
		}
		finally { if (File.Exists(tmp)) File.Delete(tmp); }
	}

	static void probeWritable(string root)
	{
		DirectoryInfo dir = new(Path.GetFullPath(root));
		while (dir != null && !dir.Exists) dir = dir.Parent;
		if (dir == null || (dir.Attributes & FileAttributes.ReparsePoint) != 0)
			throw new DirectoryNotFoundException("发布路径没有可写的已存在父目录:" + root);
		string path = Path.Combine(dir.FullName, ".write-probe-" + Guid.NewGuid().ToString("N"));
		try
		{
			using FileStream output = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
			output.WriteByte(1);
			output.Flush(true);
		}
		finally { if (File.Exists(path)) File.Delete(path); }
	}

	static bool same(byte[] left, byte[] right)
	{
		if (left == null || right == null || left.Length != right.Length) return false;
		for (int i = 0; i < left.Length; ++i) if (left[i] != right[i]) return false;
		return true;
	}

	static string osPath(string path)
	{
		return path.Replace('/', Path.DirectorySeparatorChar);
	}
}
