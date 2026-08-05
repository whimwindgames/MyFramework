# OpenUPM 发布说明

## 前提

OpenUPM 只收录托管在 GitHub 的开源 Unity Package。自建 Git 服务可以作为镜像，但不能作为 OpenUPM 的 `repoUrl`。

正式发布仓库：

```text
https://github.com/whimwindgames/MyFramework
```

包目录：

```text
Packages/com.whimwindgames.myframework
```

## 版本与标签

`package.json` 的 `version` 必须和 Git 标签中的版本完全一致。MyFramework 使用包名前缀隔离标签：

```text
com.whimwindgames.myframework/1.1.0-preview.3
```

OpenUPM 元数据应设置：

```yaml
name: com.whimwindgames.myframework
displayName: MyFramework
description: A reusable Unity game framework with UI, networking, resources, HybridCLR hot update and transactional release tooling.
repoUrl: 'https://github.com/whimwindgames/MyFramework'
parentRepoUrl: null
licenseSpdxId: MIT
licenseName: MIT License
topics:
  - frameworks
  - gui
  - network
  - asset-management
gitTagPrefix: 'com.whimwindgames.myframework/'
gitTagIgnore: ''
minVersion: '1.1.0-preview.3'
trackingMode: git
image: ''
readme: 'master:Packages/com.whimwindgames.myframework/README.md'
hunter: whimwindgames
```

实际提交时以 OpenUPM 添加页面当前提供的字段和 topics 列表为准。

## 发布顺序

1. 运行 `node Tools/validate-upm-package.mjs`。
2. 在 Unity 6000.3.11f1 中完成编译和 EditMode 测试。
3. 确认 `package.json.version` 已更新且 Changelog 已归档。
4. 把发布提交推送到公开 GitHub 仓库。
5. 创建并推送同版本标签。
6. 在 OpenUPM 添加页面提交 `com.whimwindgames.myframework` 元数据。
7. 等待 OpenUPM 构建完成，再用一个空白工程从 Registry 安装并验证初始化菜单。

仓库的 `OpenUPM Publish` 工作流会在包标签推送后使用官方 OIDC Action
主动触发扫描；需要重试已有标签时，可以手动运行工作流并填写完整标签名。

已经被 OpenUPM 发布的版本不可覆盖；发现问题必须递增版本号并创建新标签。
