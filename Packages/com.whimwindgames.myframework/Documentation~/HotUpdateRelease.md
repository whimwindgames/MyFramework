# Schema 11 Release Production

`HotUpd_Editor` 是 Schema 11 的生产核心。`RelBuild` 把已生成的 Stage 转为可由 `UpdCore` 下载和校验的 Release；`ProdFlow` 负责在隔离候选目录中编排 Stage 生产步骤。

`HotUpd_Editor` 默认不自动引用；调用发布 API 的 Editor asmdef 需要显式引用它，避免运行时程序集意外依赖私钥和发布工具。

## 安全边界

- 首个 Release 必须设置 `newBase = true`。生产器会冻结 `env/platform/baseId/baseUrl/pubKey`，后续 Release 不允许修改这些值。
- ES256 私钥必须是 P-256 PEM 文件，使用项目外绝对路径，且路径不能经过符号链接。私钥不会写入 Unity 资产、ProjectSettings 或 Release。
- 生产器在加锁后将文件复制到 `.staging`，回读所有 SHA-256 和 Manifest，再以同父目录移动提升。
- Latest 写入失败时会恢复原 Latest；恢复状态不可确定时，保留 Release 并要求停止上传。
- `point` 只会使用更大的序列号签名新 Latest，不会修改历史 Release。
- `ProdFlow` 使用稳定的 `.prod.lock`、Stage 候选目录和旧 Stage 备份。步骤或 Release 失败时不会覆盖上一次可用 Stage。

## 输出结构

```text
<root>/<env>/
  base/<platform>/<baseId>.json
  latest/<platform>/<baseId>.json
  releases/<releaseId>/
    manifest.json
    files/...
  symbols/<releaseId>.xml       # 可选
  .pub.lock
```

`latest/*.json` 是 ES256 签名的 `UpdBox`。`manifest.json` 和 `files/` 均会在提升前回读校验。

## API 示例

首次部署前可以生成一对新的 P-256 密钥：

```csharp
RelKeyPair keys = RelKey.generate();
File.WriteAllText(absolutePrivateKeyOutsideProject, keys.privatePem);
string publicKeyDerBase64 = keys.publicKey;
```

私钥只能写入项目和 Git 工作区外的安全位置；`publicKey` 写入 `UpdCfg.pubKey` 并随 Base 冻结。正式环境应根据组织安全规范限制私钥文件权限和备份访问。

```csharp
string[] codeDlls =
{
    FrameBaseDefine.HOTFIX_FRAME_BYTES_FILE,
    FrameBaseDefine.HOTFIX_BYTES_FILE,
};

UpdCfg cfg = new()
{
    baseUrl = "https://cdn.example.com/",
    env = "test",
    platform = FrameBaseDefine.ANDROID,
    baseId = "base-1001",
    pubKey = publicKeyDerBase64,
    aotDlls = aotDlls,
    codeDlls = codeDlls,
    entryDll = FrameBaseDefine.HOTFIX_BYTES_FILE,
    hotId = UpdRule.hotId(codeDlls, FrameBaseDefine.HOTFIX_BYTES_FILE),
    secret = secretPath,
    resList = FrameBaseDefine.AB_INDEX_FILE,
};

RelReq req = new()
{
    src = absoluteStageDirectory,
    root = absoluteReleaseOutput,
    privateKey = absolutePrivateKeyOutsideProject,
    cfg = cfg,
    plan = HotList.fromCfg(cfg),
    newBase = true,
};

RelView preview = RelBuild.preview(req);
string releaseId = RelBuild.make(req);
RelCheck check = RelBuild.verify(new RelReq
{
    root = absoluteReleaseOutput,
    cfg = cfg,
    plan = HotList.fromCfg(cfg),
});
```

后续 Release 将 `newBase` 设为 `false`。如需回指历史版本，设置 `RelReq.releaseId` 后调用 `RelBuild.point(req)`。

## Stage 编排

项目侧把具体构建器包装为 `IProdStep`：AssetBundle 使用 `ProdOrder.ASSET_BUNDLE`，HybridCLR DLL/AOT 使用 `ProdOrder.MANAGED_CODE`，最终索引或项目检查使用 `ProdOrder.FINALIZE`。即使传入数组乱序，执行顺序也由 `order` 决定，并且每个顺序值和步骤名都必须唯一。

```csharp
ProdFlow flow = new(new ProdReq
{
    stage = absoluteStageDirectory,
    release = req,
    steps = new IProdStep[]
    {
        new AbProdStep(EditorUserBuildSettings.activeBuildTarget, bundleRoots),
        DllBuild.step(
            EditorUserBuildSettings.activeBuildTarget,
            cfg,
            plan,
            compiledDir: null,       // null表示现场重新编译Player程序集
            baselineRoot: null),     // null表示项目默认HybridCLRData目录
    },
});

flow.check();
string releaseId = flow.makeAll();
```

`IProdStep.check` 只能做只读前置校验；`run` 收到的 `ProdCtx.stage` 是候选目录。生产步骤不得直接写正式 Stage 或 Release 输出目录。

`UpdCfg.aotDlls` 应先通过 `DllBuild.analyzeAot` 从冻结 Base 与最终 Hot DLL 自动生成，再使用 `DllBuild.withAot` 得到新的配置副本。`DllProdStep` 提交前还会复算并做精确一致性校验，同时运行 HybridCLR `MissingMetadataChecker`；补丁若引用了主包已裁剪的程序集、类型或成员，将不会进入 Release。完整流程见 [HybridCLR Production](HybridCLRProduction.md)。

## SSH 远端发布

`PubFlow` 会从 `<root>/<env>/base/<platform>/<baseId>.json` 读取 `RelBuild` 冻结的 Base 信任记录，因此同一份 Release 输出可在新 checkout 或 CI 节点发布，不依赖当前 Unity 项目的 AOT 缓存。窗口入口为 `MyFramework/HotUpdate/资源发布`；首次连接必须在服务器控制台核对窗口显示的 SHA-256 主机指纹后手动信任，后续指纹变化会直接中止。

无头入口与窗口共用同一状态机：

```bash
Unity -batchmode -quit -projectPath /abs/project \
  -executeMethod PubCli.runCli -- \
  -pubAction pub -pubPlatform Android \
  -pubRoot /abs/release-output -pubRelId test-Android-base-1001-2 \
  -sshHost 47.243.79.140 -sshUser hotdeploy \
  -sshKey ~/.myframework-keys/hotdeploy/openssh \
  -sshUrl https://47.243.79.140/ \
  -pubReceipt /abs/receipts/publish.json
```

`scan` 只需要 `-pubRoot/-pubPlatform`；`check` 与 `pub` 还需要 `-pubRelId`；`remote` 需要 `-pubEnv/-pubBaseId`；`rollback` 在此基础上还需要 `-pubPrivKey`，并且只使用远端 `Previous`、Manifest 与文件回读签发更高序号的 Latest。JSON 回执包含 `releaseId/fileCount/totalSize/manSha/durationMs/ok/error`。真实服务器 `PubSmokeTests` 只能在隔离 batchmode/CI 中显式运行。

test/prod 必须配置不同私钥路径。加密 PEM 的 rollback 额外传 `-pubPrivKeyPasswordEnv <变量名>`，密码从 CI 秘密环境变量读取，不接受明文命令行参数。项目外目录、旧 EditorPrefs 迁移和轮换流程见 [Hot Update Signing Keys](HotUpdateKeys.md)。

hot-store v2 的服务器模板、协议测试和线上位置说明位于仓库 `Deploy/HotUpdate/`。服务端或客户端协议版本不一致时，`SshStore` 会在任何上传前拒绝会话。

## 当前分层

框架现已负责 AssetBundle、HybridCLR DLL/AOT、Stage 步骤事务和“Stage 到签名 Release”的确定性生产。Obfuz 通过 `IObfApi` 项目适配器接入。完整 Player 构建由更外层的 `PackFlow` 负责：它调用 HybridCLR GenerateAll、产生 stripped AOT，并且只在 Player 和可选首个 Release 都验证成功后提交新 Base。具体规则见 [HybridCLR Production](HybridCLRProduction.md)。
