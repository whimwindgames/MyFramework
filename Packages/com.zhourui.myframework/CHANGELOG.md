# Changelog

本文件记录 `com.zhourui.myframework` Unity Package 的可见变化。仓库中的示例游戏或项目专用调整不应记录在这里。

## [Unreleased]

### Added

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
- 未修改程序集名称、包名和初始化菜单路径。
- 旧 `AssetVersionSystem` 和 `launchHotFix(Action)` 保持可用；新热更新能力通过增量 API 并行迁入。

## [1.0.41] - 2026-08-02

- 以官方 MyFramework `1.0.41`、提交 `80dea994fade479d5af9eb4184e24a3d865ecc10` 作为独立优化基线。
