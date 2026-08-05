using System;
using System.Collections.Generic;

// 基于YooAsset 3.0.4 Group/Collector思想改写并本地化，见本目录NOTICE.md。

public enum AbZip
{
	Lzma,
	Lz4,
	Raw,
}

[Serializable]
public sealed class AbEntry
{
	// 只保存Unity GUID；资源移动或改名不会改变配置身份。
	public string guid;
	// 运行时稳定逻辑地址，不随资源物理路径自动变化。
	public string address;
}

[Serializable]
public sealed class AbGroup
{
	// 编辑器稳定身份，与最终AB文件名分离。
	public string id;
	// 一个Group精确生成一个AssetBundle。
	public string bundleName;
	public List<AbEntry> entries = new();
}
