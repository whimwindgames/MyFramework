# Release 门禁插件

`IRelGate` 把项目检查统一成 Project、Plan、Candidate 三个阶段。每个插件读取同一份 `RelGateInput`：完整 Release DLL 计划、AssetBundle 计划和目标 Base 冻结记录；插件只能通过 `RelGateOutput` 返回结构化诊断。

框架不会硬编码任何业务、游戏或服务器配置名。项目适配器应包装已有校验逻辑，并把原字符串错误映射为稳定的 `code/message/path`。

## 注册插件

插件用稳定 ID、顺序和阶段显式注册。重复 ID、非法阶段和覆盖框架内置 ID 会立即失败。

```csharp
sealed class GameBoundaryGate : IRelGate
{
    public string id => "game.boundary";
    public int order => 300;
    public RelGatePhase phases => RelGatePhase.Project | RelGatePhase.Plan;

    public void check(RelGateInput input, RelGateOutput output)
    {
        foreach (string error in ExistingBoundaryCheck.collect())
            output.error("boundary.invalid", error);
    }
}

[InitializeOnLoadMethod]
static void registerGates()
{
    RelGateRegistry.register(new GameBoundaryGate());
    RelGateRegistry.register(new RelRequiredAssetGate(
        "game.required-config",
        new[] { new RelRequiredAsset("config/runtime.json",
            "Assets/GameContent/Config/runtime.json") }));
    RelGateRegistry.bindInput(makeGateInput);
}
```

项目只绑定一个输入适配器。它根据 CLI 的 `env/platform/baseId/releaseId/stage` 构造生产时使用的 `UpdCfg`、`HotPlan` 与 `AbPlan`，并从 `AotBase.info(...)` 取得完整冻结 AOT 列表：

```csharp
static RelGateInput makeGateInput(RelGateRequest request)
{
    UpdCfg cfg = ProjectReleaseConfig.create(request);
    AbPlan assets = ProjectReleaseConfig.assetPlan(request);
    AotBaseInfo frozen = AotBase.info(request.env, request.baseId);
    HotPlan hot = DllBuild.plan(cfg, frozen.cap, AbCheck.monoAsms(assets));
    return new RelGateInput(cfg, hot, assets, RelGateBase.from(cfg, frozen),
        request.stage, request.releaseId);
}
```

输入适配器返回不同的环境、平台、Base、Release 或 Candidate Stage 时，CLI 会拒绝结果。Runner 也会在每个插件后对输入做指纹校验；修改计划的插件会得到 `gate.input_mutated` 错误。

## 框架内置门禁

- `framework.mono-script`：AB 依赖的每个 MonoScript 程序集必须位于本次 Hot DLL 集合或目标 Base 完整 AOT 集合中，同时位于两边也会失败。
- `RelRequiredAssetGate`：项目以逻辑地址、可选固定源文件和适用环境声明必需资源。它适合替代散落的“某配置必须进入 Release”静态检查。

内容格式、业务 URL、安全策略和项目目录边界仍由项目插件负责。

## CLI 与回执

```bash
Unity -batchmode -quit -projectPath /abs/project \
  -executeMethod RelGateCli.runCli -- \
  -gateEnv prod -gatePlatform Android -gateBaseId base-1001 \
  -gateReleaseId prod-Android-base-1001-2 \
  -gateStage /abs/release.candidate \
  -gatePhase all -gateReceipt /abs/receipts/gate.json
```

`-gatePhase` 接受 `project/plan/candidate/all`。进程输出 `GATE_RECEIPT=<json>`；回执包含环境身份、阶段、耗时，以及按 gate/order/code/path 稳定组织的 info、warning、error 诊断。任何 error 或插件异常都返回非零退出码。

普通 `RelGateCli` JSON 是诊断回执，本身不能授权发布。生产发布应使用 `RelPipelineCli`：框架在生产完成后强制执行 `all`，把门禁报告与 Release 身份、Manifest SHA、文件数和操作者绑定，并使用目标 Base 对应环境的 P-256 私钥生成 `audit/<releaseId>/gate.json`。`PubFlow` 只接受该签名凭证；凭证缺失、验签失败、身份不一致、阶段不全或包含 error 时，在任何远端 Release/Latest 写入前失败。
