# AssetBundle Production

Schema 11 的 AssetBundle 生产核心位于 `HotUpd_Editor`。它使用显式 Group 配置，不扫描默认资源目录，也不依赖 YooAsset Runtime。

## 配置模型

- `AbCfg`：配置版本、压缩模式和 Group 列表。
- `AbGroup.id`：编辑器稳定身份，使用小写字母、数字、点、横线或下划线。
- `AbGroup.bundleName`：最终 Bundle 相对路径，必须以 `.unity3d` 结尾。
- `AbEntry.guid`：Unity 资源 GUID。资源移动或改名不会改变配置身份。
- `AbEntry.address`：运行时稳定逻辑地址，不随物理路径自动变化。

一个资源只能出现一次，一个逻辑地址只能映射一个资源。场景与普通资源不能放入同一 Bundle；所有可打包依赖都必须显式配置。SpriteAtlas 及其 Sprite 成员会额外检查归属边界。

默认配置保存在 `ProjectSettings/AbCfg.asset`，可通过 `AbCfg.init / load / save / revert` 管理。CI 或项目自有配置系统可以直接构造 `AbCfg`，调用带显式配置参数的重载，不需要修改 ProjectSettings。

## 独立构建

```csharp
AbCfg cfg = AbCfg.load();
string[] roots = null; // null构建全部；空数组只生成空索引

AbBuild.check(cfg, roots);
string inputHash = AbBuild.hash(target, cfg, roots);
bool built = AbBuild.run(target, absoluteOutput, cfg, roots);
bool ready = AbBuild.ready(absoluteOutput, cfg, roots);
```

生产器在项目 `Temp` 中调用 Unity Legacy AssetBundle Pipeline，回读实际 Bundle、内部地址、场景和图集，然后生成确定性的 `StreamingAssets.bytes`。输出与项目不在同一磁盘时，产物会先复制到目标同级候选目录，并逐文件进行长度和 SHA-256 校验；最后只在目标磁盘内原子提升。

## 接入 ProdFlow

```csharp
IProdStep ab = new AbProdStep(
    EditorUserBuildSettings.activeBuildTarget,
    cfg,
    roots);
```

`AbProdStep.check` 会冻结配置、Unity 版本、目标平台和资源依赖哈希；`run` 前重新计算，发现校验后资源变化会拒绝生产。目标平台必须与 `ProdCtx.platform` 一致。

## 迁移边界

通用包包含配置模型、计划、校验、图集边界、构建报告和生产适配器。ArcadeHub 的配置窗口、报告窗口、拖拽界面和项目菜单没有进入核心包，可由项目 Editor 程序集按需提供。

本实现参考并派生自 YooAsset 3.0.4 Editor 构建思想，归属说明与 Apache License 2.0 全文位于 `Editor/HotUpd/AssetBundle/NOTICE.md` 和 `YooAsset-LICENSE.md`。
