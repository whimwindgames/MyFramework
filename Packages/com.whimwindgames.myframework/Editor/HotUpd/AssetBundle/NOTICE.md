# YooAsset 派生移植说明

本目录的 AB Group 配置、构建计划、Legacy 构建、构建前后校验、报告及其编辑器界面，参考并派生改写自 YooAsset 3.0.4 的 Editor 实现：

- 来源：https://github.com/tuyoogame/YooAsset/tree/3.0.4
- 参考版本提交：`5d478a9a7f735934d9d82b32b8e6028d381ad5c6`
- Copyright 2018-2021 何冠峰
- Copyright 2021-2026 TuYoo Games
- 许可证：Apache License 2.0，全文见 `YooAsset-LICENSE.md`

本地修改包括：删除 YooAsset Runtime、Package/Manifest、SBP、程序集引用和命名空间；改为每个 Group 显式保存资源 GUID、稳定逻辑地址和目标 AB，不扫描默认目录；提供 `AbBuild.run` 与 `IProdStep` 生产入口；保留既有 `.unity3d`、`StreamingAssets.bytes`、Stage/Release、图集及回滚契约；图集构建只临时切换并恢复必要状态，不持久改写项目资源导入设置；配置和报告格式均为本框架本地格式。ArcadeHub 的配置窗口、报告窗口及项目专用菜单未迁入通用生产核心。

这些文件属于基于上述来源的修改实现，不声明为完全原创，也不代表 YooAsset 或 TuYoo Games 对本项目提供背书。
