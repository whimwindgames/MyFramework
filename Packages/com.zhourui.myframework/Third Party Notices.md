# Third-Party Notices

MyFramework 主体采用 MIT License。包内包含或派生使用以下第三方组件；其版权与许可证仍归各自权利人所有。

## NativeWebSocket

- 来源：<https://github.com/endel/NativeWebSocket>
- Copyright 2019 Endel Dreyer
- Copyright 2018 Jiri Hybek
- 许可证：Apache License 2.0
- 本地文件：`Runtime/Frame_Base/WebSocket/NativeWebSocket.cs`

本框架对该文件进行了 Unity 版本兼容和项目集成调整。Apache License 2.0 全文随包保存在 `Editor/HotUpd/AssetBundle/YooAsset-LICENSE.md`。

## YooAsset

- 来源：<https://github.com/tuyoogame/YooAsset/tree/3.0.4>
- Copyright 2018-2021 何冠峰
- Copyright 2021-2026 TuYoo Games
- 许可证：Apache License 2.0

MyFramework 的 AssetBundle 编辑器生产链参考并派生改写自 YooAsset。详细修改范围见 `Editor/HotUpd/AssetBundle/NOTICE.md`，许可证全文见同目录的 `YooAsset-LICENSE.md`。

## Bouncy Castle C#

- 组件：BouncyCastle.Cryptography 2.6.2
- 来源：<https://www.bouncycastle.org/>
- Copyright 2000-2025 The Legion of the Bouncy Castle Inc.
- 许可证：MIT
- 本地文件：`Runtime/Frame_Game/UpdSystem/Core/Plugins/BouncyCastle.Cryptography.dll`

许可证全文见 `Runtime/Frame_Game/UpdSystem/Core/Plugins/LICENSE.BouncyCastle.md`。

## 外部包依赖

HybridCLR、Obfuz、UniTask、Unity UI、TextMeshPro、Newtonsoft.Json 和 URP 由 Unity Package Manager 安装，不作为 MyFramework 源码的一部分重新授权。请分别遵守各依赖包随附的许可证。
