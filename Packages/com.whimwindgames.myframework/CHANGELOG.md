# Changelog

本文件记录 `com.whimwindgames.myframework` Unity Package 的可见变化。仓库中的示例游戏或项目专用调整不应记录在这里。

## [1.1.0-preview.20] - 2026-08-07

### Fixed

- `GameEntryBase` 现在原子认领唯一进程宿主；重复入口会明确失败而不会覆盖现有实例，且只有实际所有者能清理全局生命周期状态。

### Added

- Schema 11 与发布流水线现在允许宿主通过 `entryDll` 使用唯一命名的业务热更入口；`HotFix` 仍是旧项目和旧编辑器启动 API 的默认值。

## [1.1.0-preview.19] - 2026-08-06

### Fixed

- AssetBundle 同步与异步泛型加载现在会从主资源和全部子资源中选择请求类型；PNG 等以 `Texture2D` 为主资源、`Sprite` 为子资源的资产可直接通过逻辑地址加载。
- 异步 AssetBundle 加载会登记全部子资源的包归属，使类型化资源租约能够按原有引用计数语义释放。
- 加载失败产生的空 `ResourceRef<T>` 可安静回收，不再追加误导性的二次错误日志。

## [1.1.0-preview.18] - 2026-08-06

### Fixed

- HotFix `AssetDataBaseLoader` 现在通过 Schema 11 `AbIndex` 把逻辑地址解析为实际 AssetDatabase 路径，与 Player 中的 AssetBundle 寻址一致；未配置映射时继续兼容原 `Assets/GameResources` 相对路径。
- `ResourceManager` 接受合法的无扩展名 Schema 11 逻辑地址，不再用旧文件路径规则产生误报。

## [1.1.0-preview.17] - 2026-08-06

### Added

- `FrameAssetGateway` 增加可选的同步单资源与批量资源租约入口，宿主可把 `Resources`、编辑器目录或已驻留内存的配置接入同一个可观察资源边界。
- 增加 `IFrameSynchronousAssetProvider`、`IFrameSynchronousAssetCatalog` 与幂等 `FrameAssetCollectionLease<T>`；远程后端无需实现同步能力，调用时会得到明确的“不支持”错误。
- 增加 `FrameResourceManagerAssetProvider`，把框架原有 `ResourceManager` 的 AssetDatabase/AssetBundle、异步加载、引用计数和子资源能力接入 `FrameAssetGateway`，并允许宿主解析逻辑地址而不改业务侧 API。
- 增加 `FrameAssetProviderHandoff`，让互不引用的 AOT 宿主与 HotFix 资源程序集安全交接唯一 Provider；网关替换和迁移期回退策略仍由宿主掌控。
- 增加 `FrameFallbackAssetProvider` 作为迁移期目录路由：主后端已登记的地址只走主后端，只有目录未命中才走旧后端，避免加载错误被旧资源静默掩盖。

## [1.1.0-preview.16] - 2026-08-06

### Fixed

- OpenUPM 与 Git URL 安装示例固定到本次发布版本，确保标签、包清单和文档一致。

## [1.1.0-preview.15] - 2026-08-06

### Added

- 增加可选 `IFrameAssetCatalog` 和 `FrameAssetGateway.ExistsAsync<T>()`，让宿主的 Addressables、AssetBundle 或编辑器资源目录查询继续经过统一资源网关边界。

### Changed

- 资源目录查询不会创建租约或改变活动租约计数；未安装 Provider、Provider 不支持目录或上下文已关闭时均给出明确错误。

## [1.1.0-preview.14] - 2026-08-06

### Fixed

- 不可恢复中断进入 `FAILED` 后，底层紧随其后的 `DISCONNECTED` 通知不再覆盖失败原因；用户显式发起新的连接仍可正常离开失败态。

## [1.1.0-preview.13] - 2026-08-06

### Added

- `FrameNetworkSession` 成为运行上下文的通用网络所有者，统一管理宿主 Provider、逐帧驱动、请求取消、操作观测、自动重试和销毁。
- 增加 `IFrameNetworkProvider`、`FrameNetworkInterrupted`、`FrameNetworkRecoveryResult` 与 `FrameNetworkRecoveryCompleted`，宿主可保留自己的传输、协议和鉴权实现，同时把恢复编排交给框架。
- 增加 `FrameNetworkOperationChanged`，为每个类型化网络操作发布开始、成功、取消、失败、耗时和结果类型信息。
- `FrameNetworkSession.GetApi<T>()` 支持从当前 Provider 获取宿主协议能力，业务项目无需依赖具体传输类型。

### Compatibility

- `FrameRuntimeContext.Network` 仍保留原 `SetState`、`ReportRetryScheduled`、`BeginRecovery` 和 `MarkReady` 调用面；旧的渐进接入代码可继续编译。
- 框架不实现 WebSocket、Protobuf/JSON、账号登录或房间协议；这些职责由宿主 Provider 实现并由网络会话统一拥有。

## [1.1.0-preview.12] - 2026-08-06

### Fixed

- 资源网关和视图路由在 `FrameRuntimeContext` 销毁后进入明确关闭态，拒绝新操作且不再向已关闭的事件总线发布。
- 宿主可在运行上下文销毁后安全释放已取得的 `FrameAssetLease<T>`，适配 Unity 不确定的 `OnDestroy` 顺序。

### Compatibility

- 资源租约所有权仍在宿主；关闭运行上下文不会隐式释放存活资源。

## [1.1.0-preview.11] - 2026-08-06

### Added

- `FrameRuntimeContext` 增加宿主可注入的 `FrameAssetGateway` 和 `FrameViewRouter`，为大厅、小游戏和独立项目提供统一资源/页面边界。
- 增加 `IFrameAssetProvider` 与幂等 `FrameAssetLease<T>`，同时覆盖资产加载和实例化，由宿主后端保留原有释放语义。
- 增加 `IFrameViewAdapter`、四层 `FrameViewLayer` 和可观测视图生命周期，支持通用路由、显示、关闭和返回导航。
- 增加资源加载/释放与视图开启/关闭/失败的序列事件，便于大厅加载页、诊断和热更健康检查共用。

### Compatibility

- 框架不引用 Addressables，不规定 UGUI/UI Toolkit、Canvas、prefab 命名、视图栈或资源生产方式；原 `LayoutManager` 与 `ResourceManager` 公开 API 保持不变。

## [1.1.0-preview.10] - 2026-08-06

### Added

- 增加 `FrameRetryPolicy`，提供带上限且不会发生整数溢出的确定性指数退避，宿主网络实现可复用同一重试预算。
- `FrameRuntimeContext` 增加 `FrameNetworkLifecycle`，统一观察连接、进房、就绪、重连、快照恢复、停止与失败状态。
- 增加 `FrameNetworkStateChanged` 与 `FrameNetworkRetryScheduled` 事件，记录严格递增顺序号、原因、尝试次数、预算和延迟。

### Compatibility

- 网络生命周期只描述状态和重试时机，不实现传输、协议、鉴权、房间恢复或业务请求；宿主既有网络所有权和公共 API 保持不变。

## [1.1.0-preview.9] - 2026-08-06

### Added

- `Frame_Base` 增加与具体游戏实现无关的 `FrameRuntimeContext`，统一承载运行时日志、类型事件、配置、服务和可观测生命周期。
- 增加严格注册与显式替换的 `FrameServiceRegistry`、支持默认/命名配置的 `FrameConfigurationStore`，成熟项目可渐进接入而不必替换现有 Manager。
- 增加故障隔离的 `FrameEventBus`；单个订阅者异常会记录日志且不会阻断其他订阅者。
- 增加带顺序号、阶段、UTC 时间和失败原因的 `FrameLifecycleChanged`，支持观察启动、运行、失败与退出过程。

### Compatibility

- 运行上下文位于 AOT 基础程序集，不依赖 `Frame_Game`、HotFix、UI、资源或网络实现；现有公共 API 和旧启动流程保持不变。

## [1.1.0-preview.8] - 2026-08-06

### Fixed

- 将框架贴图、音频和模型的自动导入规范限定到 `Assets/GameResources`，接入成熟宿主项目时不再改写其他目录的资源导入设置。
- 声明 `Frame_Base` 中 Android 桥接代码所需的 `com.unity.modules.androidjni` 内置模块依赖，宿主项目无需再临时补包。
- `GameEntryBase` 增加保留宿主3D物理与屏幕设置的兼容模式；旧场景仍默认沿用框架历史行为。
- UI根节点、UI相机、模糊相机和主相机支持名称配置及运行时对象绑定，不再只能依赖模板场景固定名称。

### Added

- 增加“安全接入成熟项目”初始化入口，只生成编辑器/运行时设置，不复制模板、不修改场景、输入和 Build Settings。
- 增加与UI实现无关的 `FrameScreenContext`，统一提供横竖屏、宽高比、安全区、四边 inset、归一化安全区和变化通知。
- `FrameSettings` 可选按横竖屏使用不同UI标准分辨率；默认关闭以保持旧项目布局行为。
- AOT 与 HotFix 启动流程增加可覆写的平台系统、内置 Manager、跨层参数恢复阶段；HotFix 支持传入宿主框架子类工厂，旧启动重载保持不变。

### Compatibility

- 保留 `AssetsImport` 类型、Unity 导入回调和既有公共 API 名称；仅收紧框架资源导入规则的生效范围。

## [1.1.0-preview.7] - 2026-08-06

### Fixed

- 恢复直接创建 `GameScene`、`SceneProcedure` 与 `SceneInstance` 时的 `ClassObject` 生命周期激活，避免延时命令将新对象误判为已销毁并丢弃场景切换任务。
- 本地文件读取统一生成并编码标准 `file:///` URI，修复 macOS/Linux 绝对路径被拼成四斜杠，以及中文、空格或加号 AssetBundle 路径触发 `Malformed URL`/404 的问题。

## [1.1.0-preview.6] - 2026-08-05

### Fixed

- AssetBundle 资源加载现在使用 Schema 11 索引记录的实际内部地址；历史索引继续自动补充 `Assets/GameResources/` 前缀，修复新生产器的 addressable name 被旧路径规则再次加前缀后返回空资源的问题。

## [1.1.0-preview.5] - 2026-08-05

### Fixed

- `AtlasManager` 优先从当前 Schema 11 `AbIndex` 初始化 SpriteAtlas 名称到逻辑地址的映射，AssetBundle 模式不再依赖手工生成的 `Misc/AtlasPathConfig.txt`。
- 编辑器 AssetDatabase 模式复用 `AbCfg` 的图集映射；历史索引仍保留 `AtlasPathConfig.txt` 回退路径。

## [1.1.0-preview.4] - 2026-08-05

### Fixed

- `AssetBundleLoader` 现在同时读取历史索引与 Schema 11 `AbIndex`，并正确区分逻辑资源地址和 AssetBundle 内部地址；修复新生产器产物被旧加载器解析成乱码与重复键的问题。
- Schema 11 索引拒绝仅大小写不同的逻辑地址或内部地址，和运行时不区分大小写的资源查找契约保持一致。

## [1.1.0-preview.3] - 2026-08-05

### Fixed

- 修复 `launchEdit(...)` 清空宿主在进入 Play Mode 前设置的本地资源读取桥，导致编辑器 AssetBundle 模式错误回退到 `Assets/StreamingAssets/<platform>` 的问题。

## [1.1.0-preview.2] - 2026-08-05

### Added

- 增加 `IHotEnt` 错误与取消感知入口，同时保留旧 `start(Action)` 启动签名。
- 增加 `IHotPre / HotPreReg` AOT 扩展边界以及 Obfuz 项目模板适配器，项目生成程序集不再成为通用框架程序集的反向依赖。
- 热更框架启动增加异常回传和首个可交互界面就绪确认，Schema 11 只在业务真正就绪后标记候选版本健康。
- 增加候选 AOT 基线分析与 `IPackCommitHook`，首包可将项目侧 Base 登记、Player、AOT 基线、Stage 和首个 Release 纳入同一回滚事务。

### Fixed

- 修复启用 `USE_OBFUZ` 时 `Frame_HotFix` 直接引用项目生成的 `GeneratedEncryptionVirtualMachine` 而无法独立编译的问题。
- 修复 ArcadeHub 已采用的热更入口契约未随首次 OpenUPM 预览包发布的问题。
- 修复未定义 `USE_URP` 的项目被框架内置渲染辅助测试阻断编译的问题，并恢复项目级安卓插件包名覆盖入口。
- 修复 `HotUpd_Core` 未列入固定 AOT 集合、可能被项目误选为 Hot 的问题。

## [1.1.0-preview.1] - 2026-08-05

### Added

- 首次公开包使用 `com.whimwindgames.myframework` 标识，并同步包目录、初始化器、OpenUPM 元数据和固定版本安装地址。
- 增加 OpenUPM 安装配置、公开 GitHub 发布元数据、包归档自动校验和版本标签约束。
- 声明 OpenUPM 已收录的 UniTask、HybridCLR、Obfuz 依赖，并声明 Unity 6 URP 依赖，使 Registry 安装能够自动解析完整工具链。
- 增加第三方组件归属说明，补齐 NativeWebSocket、YooAsset 派生实现和 Bouncy Castle 二进制许可证入口。
- 增加 `RelKey.generate()` P-256 发布密钥生成 API；生产私钥仍由调用方保存到项目与 Git 工作区外。
- 为通过 Git URL 安装的 UPM 包补齐包内许可证、说明文档和变更记录。
- 在 `package.json` 中声明许可证、文档、变更记录和自托管仓库地址。
- 建立公共 API 与 Unity 序列化兼容规则。
- `CustomAsyncOperation` 增加成功、失败、取消终态及错误信息查询，同时保留原有 `setFinish()` 调用方式。
- 增加独立的 `HotUpd_Core` 程序集，迁入 Schema 11 热更新协议、严格 JSON 校验、SHA-256、ES256、事务存储、候选版本回滚和只读资源映射。
- 增加热更新核心 EditMode 测试，覆盖路径安全、协议字段、版本防回退、签名篡改、单进程锁、内容提交和候选版本健康状态。
- 增加 `HotUpd_Client` 程序集，迁入 Latest/Manifest 获取、内置资源复用、四路并发下载、HTTP Range 续传、事务安装、离线 Active 回退和 `PlatRunSet` 配置。
- 示例工程使用固定提交的 UniTask 2.5.10；为保持 ArcadeHub 的热更启动 API 签名，Schema 11 的 `Frame_Game` 需要 UniTask 2.5.0 或更高版本。
- 增加客户端网络与离线启动测试，覆盖 Range 服务器不兼容、响应头不一致、HTTP 重试分类、内置文件哈希以及服务器不可用时本地启动。
- `HybridCLRSystem` 保留旧 `launchHotFix(Action)`，并增加 ArcadeHub Schema 11 启动入口、单次加载门、完整预读、动态密钥传递、本地 Release 资源映射和成功后健康标记。
- 增加 `HotUpd_Editor` Release 生产核心，保留 ArcadeHub 的 `RelBuild`、`RelReq`、`RelSign`、`HotPlan` 名称，支持 Base 身份冻结、Manifest/SHA-256、ES256 Latest、发布锁、候选目录原子提升、回读校验和历史 Release 回指。
- 增加 Schema 11 `AbIndex` 编解码和结构校验，为后续通用 AssetBundle 生产编排提供稳定索引契约。
- 增加与 ArcadeHub 同名的 `ProdFlow.check / preview / makeAll` Stage 编排入口和 `IProdStep` 扩展点；AB、受管代码与收尾步骤按规范顺序在候选目录执行，Release 发布失败会回滚正式 Stage。
- 迁入并优化 `AbCfg / AbPlan / AbCheck / AbPipe / AbBuild` 通用 AssetBundle 生产链，保留 ArcadeHub API 名称，增加显式配置入口、`AbProdStep`、构建回读和跨卷 SHA-256 候选复制。
- 增加 `AotBase / DllBuild / DllProd / DllProdStep` 通用 HybridCLR 生产链，冻结并验真 Base AOT、Hot 能力、启动配置与 Obfuz 能力；补丁 DLL 使用真实 Player 编译入口并拒绝内部名称伪装、原生调用和越权 AOT 元数据。
- 增加 `IObfApi` 可选代码保护适配接口；未安装适配器时保持 `none` 能力，不让通用包直接依赖 Obfuz 版本。
- 增加 HybridCLR 生产 EditMode 测试，覆盖真实 Player DLL 编译、基线篡改、Base ID 重绑定、Hot 能力越权、失败回滚和 `ProdFlow` 发布。
- 增加 `PackFlow` 完整 Player/Base 事务：构建期间临时切换 HybridCLR Hot 清单和 `PlatRunSet`，支持Stage内置回读，并在成功后统一提交Player、AOT基线及可选首个签名Release。
- 增加 `AotPending.prepare / promote / accept` 可回滚基线接口；已提升但尚未确认的基线会随外层事务失败撤回。
- 配置 HybridCLR 的项目不再允许通过 Unity 直接生成非 Development 正式包，避免绕过AOT基线冻结。
- 增加 Player生产失败注入测试，覆盖GenerateAll失败、Build失败、stripped AOT非法、工程配置恢复和Player/AOT/Release联合提交。
- 增加 `DllBuild.analyzeAot / withAot` 自动 AOT 元数据清单：从最终 Hot DLL 生成规范 `aotDlls`，约束为冻结 Base 子集，并以 Unity 目标系统引用运行 HybridCLR `MissingMetadataChecker`；正式 DLL 提交前会复算并拒绝配置漂移。

### Fixed

- 删除 `Frame_Base`、`Frame_Game`、`Frame_HotFix` 和 `Editor_Frame` 对大厅或小游戏项目程序集 `AutoGenerated`、`TTWebGL`、`QiNiu` 的无条件引用。
- HybridCLR 旧生产入口改为反射读取同名 `AOTGenericReferences.PatchedAOTAssemblyList`，保留生成代码契约且不再要求通用程序集直接引用项目生成程序集。
- `AbCfg` 与 `PlatRunSet` 恢复 ArcadeHub 旧包的 Unity GUID，替换内嵌包时保留现有序列化资产引用。
- Release 生产测试不再提交固定 PEM 私钥，改为每次测试随机生成密钥对。
- 框架销毁改为幂等操作，并隔离各系统的销毁异常，避免单个回调失败中断其余系统清理。
- 应用退出回调即使抛出异常也会继续执行框架销毁。
- 框架销毁后清空内部容器、应用生命周期回调和静态框架引用，避免重进场景或热更切换时残留旧引用。
- HybridCLR 与 Obfuz 使用精确提交，初始化时支持官方 GitHub 失败后回退到同提交的 Gitee 镜像。
- 示例工程不再依赖未固定提交的 Git Package，避免不同机器解析到不同插件版本。
- WebSocket 连接增加连接与关闭超时、取消令牌和连接代次隔离，避免旧连接的异步回调污染重连后的状态。
- WebSocket 正确处理分片消息、关闭帧、消息类型不匹配和输入缓冲区溢出，并补齐上一次网络状态。
- WebSocket 断开、销毁和单包解析异常时会完整释放待收发数据，单个坏包不再丢弃后续已接收消息。
- JSON WebSocket 从累计输入缓冲区复制消息，修复分片消息误读最后一次接收缓冲区的问题。
- WebGL WebSocket 同步采用连接代次、幂等连接回调、收发队列释放和解析长度校验，避免切换大厅与小游戏时旧事件回写新连接。
- 无效 ES256 公钥统一返回 `Config/public_key`，不再错误归类为协议 Schema 错误。
- `WavSoundTest` 与 `UnityUtilityTest` 现在遵守被测 Windows 专用 API 的条件编译边界，非 Windows Player 构建不再因框架自测试代码失败。

### Compatibility

- 未修改既有运行时或编辑器公共 API 的名称与签名。
- 未修改程序集名称和初始化菜单路径；首次 OpenUPM 发布前仅将包标识调整为 `com.whimwindgames.myframework`。
- 旧 `AssetVersionSystem` 和 `launchHotFix(Action)` 保持可用；新热更新能力通过增量 API 并行迁入。

## [1.0.41] - 2026-08-02

- 以官方 MyFramework `1.0.41`、提交 `80dea994fade479d5af9eb4184e24a3d865ecc10` 作为独立优化基线。
