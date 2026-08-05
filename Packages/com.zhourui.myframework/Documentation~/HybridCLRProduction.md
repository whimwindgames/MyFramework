# HybridCLR Production

Schema 11 将 HybridCLR 生产明确分成两个事务，避免“补丁构建时顺手覆盖主包 AOT”导致已经在线的 Base 失去可复现性。

## 两个事务

1. **Player / Base 事务**：`PackFlow` 调用 HybridCLR GenerateAll 和 IL2CPP Player 构建，得到 `AssembliesPostIl2CppStrip`。Player 构建成功后，将 stripped AOT、Player 和可选首个 Release 作为联合事务提交。
2. **Release / Patch 事务**：`DllProd` 重新编译当前 Player 程序集，验证并复制 Hot DLL；AOT 元数据只能从目标 Base ID 的冻结基线中选择，最后由 `DllProdStep` 与 `AbProdStep` 一起交给 `ProdFlow` 发布。

补丁事务不会调用 GenerateAll，也不会创建或修改 AOT 基线。`buildHotFix(generateAll: true)` 会直接拒绝；完整 Player 必须通过 `PackFlow` 管理基线提交时机。

## 完整Player事务

```csharp
PackFlow pack = new(new PackReq
{
    cfg = cfg,
    plan = plan,
    target = EditorUserBuildSettings.activeBuildTarget,
    outputRoot = absoluteImmutableBaseOutput,
    playerPath = "Game.app", // Android可为Game.apk/Game.aab，Windows可为Game.exe
    scenes = enabledScenes,
    options = cfg.env == "test" ? BuildOptions.Development : BuildOptions.None,
    baselineRoot = null,
    embedStage = absoluteCompletedStage, // null表示首启下载完整Release
    release = new RelReq               // 可选：与Player一起发布首个Release
    {
        src = absoluteCompletedStage,
        root = absoluteReleaseRoot,
        privateKey = absolutePrivateKey,
        cfg = cfg,
        plan = plan,
        newBase = true,
    },
});

PackReport result = pack.build();
```

生产顺序固定为：

1. 校验输出不可覆盖、活动平台、场景、IL2CPP 和 HybridCLR 状态；
2. 临时把冻结 Base 的 Hot 能力写入 HybridCLR Settings；
3. 临时写入 `Assets/Resources/PlatRunSet.asset`；
4. 可选地把 Stage 临时复制到 `StreamingAssets/<platform>`；
5. GenerateAll 并构建候选 Player；
6. 回读 Player 中的全部内置文件，与 Stage 逐文件比对 SHA-256；
7. 从本次 Player 的 stripped AOT 准备可回滚基线；
8. 可选地准备首个签名 Release；
9. 恢复所有工程临时状态；
10. 依次提升 Release 候选、Player 和 AOT，最后发布 Latest 并确认事务。

任一步骤失败都会撤回本次创建的 Player、AOT 基线和未发布 Release。配置了 `ProjectSettings/HybridCLRSettings.asset` 后，Unity 直接 Build 只允许 Development 诊断包；非 Development 正式包必须走 `PackFlow`。

## 冻结基线

默认位置：

```text
HybridCLRData/AOTBaselines/<env>/<baseId>/<BuildTarget>/
  *.dll
  .aot-list
  .base-cap
  .obf-cap
  .boot-cap
  .baseline
```

- `.aot-list`：规范排序的 stripped AOT 文件名。
- `.base-cap`：主包允许作为 Hot 的程序集和显式可选 AOT 集合。
- `.obf-cap`：`none` 或 `obfuz-v1:<sha256>`，用于锁定 VM 能力。
- `.boot-cap`：冻结 `baseUrl` 和 ES256 公钥。
- `.baseline`：包含 env、BuildTarget、Base ID 以及其余所有文件 SHA-256 的完整身份。

任何文件被替换、增加、删除或改名都会令基线失效。同一个 `env/baseId/BuildTarget` 已存在时，只接受字节完全相同的再次冻结；内容变化必须使用新的 Base ID。

```csharp
HotPlan plan = HotList.fromCfg(cfg);

string baseline = AotBase.freeze(new AotBaseReq
{
    source = strippedAotDirectory,
    cfg = cfg,
    plan = plan,
    target = EditorUserBuildSettings.activeBuildTarget,
    useObf = false,
});
```

`source` 只允许普通顶层 DLL；符号链接、子目录、非 DLL 文件、空文件以及错误进入 AOT 的 Hot 程序集都会被拒绝。`netstandard.dll` 是分析用门面程序集，不进入冻结基线。

## 生产补丁 DLL

先让分析器从目标 Base 的冻结 AOT 和本次最终 Hot DLL 自动生成 `aotDlls`，不要人工长期维护这份清单：

```csharp
HotCap frozen = HotList.loadCap(AotBase.path(draft.env, draft.baseId));
HotPlan analysisPlan = HotList.fromCfg(draft, frozen);

DllMetaReport meta = DllBuild.analyzeAot(new DllMetaReq
{
    cfg = draft,
    plan = analysisPlan,
    target = EditorUserBuildSettings.activeBuildTarget,
    hotDir = absoluteCompiledOrFinalHotDirectory,
    baselineRoot = null,
    useObf = false,
});

// 返回独立副本，不会原地改写draft。
UpdCfg cfg = DllBuild.withAot(draft, meta);
HotPlan patchPlan = HotList.fromCfg(cfg, frozen);
```

`hotDir` 可以是 HybridCLR 编译目录中的 `*.dll`，也可以是最终 Stage 中的 `*.dll.bytes`；同一程序集不能同时出现两种格式。启用 Obfuz 时应分析混淆后的最终 DLL。

自动分析同时执行两道检查：

- 解析 HybridCLR 生成的 `AOTGenericReferences`，只允许选择目标 Base 冻结基线中已经存在的 AOT DLL；新增需求意味着必须发布新 Base ID 和主包。
- 将冻结 AOT 与 Unity 当前目标的 `netstandard.dll` 组成隔离解析目录，使用 `MissingMetadataChecker` 检查所有 Hot DLL；访问被主包裁掉的程序集、类型、字段或方法会终止生产。

分析过程会临时切换 HybridCLR Hot 清单、AOT 输入和编译输出目录，成功或异常都会恢复原设置及原输出目录。生成的临时引用文件只位于 `Library/MyFramework/HotUpd`，不会进入项目资产。

随后使用生成后的配置生产补丁：

```csharp
IProdStep dll = DllBuild.step(
    EditorUserBuildSettings.activeBuildTarget,
    cfg,
    patchPlan,
    compiledDir: null,
    baselineRoot: null,
    development: false,
    useObf: false);
```

`DllProd` 默认会对实际准备提交的最终 DLL 再执行一次完整分析，并要求结果与 `cfg.aotDlls` 精确一致。因此分析完成后即使源码或混淆结果又发生变化，也不会把清单和文件不一致的 Release 发布出去。`analyzeMetadata = false` 只提供给不含完整 AOT 基线的测试夹具或结构诊断，不应在正式生产中关闭。

`compiledDir = null` 时，生产器调用 HybridCLR 使用的 `CompilePlayerScripts` 入口现场编译，不复用 `Library/ScriptAssemblies` 作为正式产物。CI 测试可以传入一个明确的已编译目录。

每个 Hot DLL 都会验证：

- 文件存在、非空且不是符号链接；
- 文件名与程序集内部名称严格一致；
- 不含 P/Invoke、反向 P/Invoke 包装特性或 IL `calli`；这类代码必须进入 AOT 并发布新 Base；
- 程序集属于冻结 Base 的 Hot 能力；
- 与冻结 AOT 不同名。
- 自动 AOT 引用结果与 `UpdCfg.aotDlls` 完全一致，且没有访问冻结 Base 已裁剪的元数据。

Stage 中只会出现 `UpdCfg.codeDlls`、`UpdCfg.aotDlls` 以及启用 Obfuz 时声明的动态密钥。生产先在同级候选目录完成，验证后才替换 Stage 中旧的受管代码文件；失败会恢复旧文件。放入 `ProdFlow` 后，外层候选 Stage 和 Release 事务会再提供一次整体回滚。

## Obfuz 适配

通用包只定义 `IObfApi`，项目 Editor 程序集可以实现一次：

```csharp
public sealed class ProjectObfuzAdapter : IObfApi
{
    public string cap() { /* 返回 obfuz-v1:<64位小写hex> */ }
    public void chk(HotSet hot) { }
    public void run(string outDir, string aotDir, HotSet hot, bool isDebug) { }
    public string mapPath() { return absoluteMappingFile; }
}
```

只允许存在一个适配器。未安装适配器时能力为 `none`，普通未混淆 Release 不需要 Obfuz 包。启用混淆时，`UpdCfg.secret` 必须是 `DynamicSecretKey.bytes`，并且当前适配器能力必须与冻结 Base 完全一致。

## 网络配置为什么也属于 Base

客户端使用 Base 内置的 `baseUrl` 获取 Latest，并使用 Base 内置公钥验证服务端指针。两者任何一个在同一 Base ID 下被替换，都会改变该安装包信任的服务器边界。因此 `.boot-cap` 同时冻结 URL 和公钥：切换 CDN 根地址、环境或签名公钥时必须发布新 Base，不能只发一个补丁 Release。
