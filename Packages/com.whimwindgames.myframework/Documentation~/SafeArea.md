# 安全区域适配

`FrameSafeArea` 位于 `Frame_Base`，可供 AOT、热更新 UI 和其他引用框架的项目共用。
它复用 `FrameScreenContext` 的采样与变化通知，不依赖大厅或捕鱼程序集。

## 接入已有页面

1. 在代表完整 Player 窗口的 `RectTransform` 根节点上添加 **MyFramework / UI / Safe Area**。
2. 将已有的内容容器填入 **Content Roots**，例如顶部信息区、活动区、底部导航区。
3. 背景、全屏遮罩不填入列表，继续覆盖整个窗口。
4. 需要防止固定宽度按钮栏挤压时，设置 **Minimum Content Size**。例如逻辑设计宽度为 1920 时填 `(1920, 0)`，只在安全区域宽度不足时等比缩小；默认 `(0, 0)` 只内缩。

也可在运行时配置：

```csharp
FrameSafeArea safeArea = root.gameObject.AddComponent<FrameSafeArea>();
safeArea.Configure(new[] { top, body, dock }, new Vector2(1920f, 0f));
```

容器必须是根节点的直接子节点，容器自身 XY 缩放为 1、旋转为 0。
组件保留容器原有 Anchor、Pivot、偏移和固定尺寸；其内部控件不重建、不改父节点。
底部固定高度容器仍然贴底，横向拉伸容器跟随安全区域宽度。
不要在目标容器上同时使用会写 Anchor/位置/缩放的 LayoutGroup、Animator 或其他布局控制器。

根节点必须与完整 Player 窗口对齐，适用于全屏 Overlay、Camera、World Space UI；
分屏相机、RenderTexture 面板及仅占屏幕一部分的根节点不适用这个组件。
组件不负责把任意单个越界控件钳制回安全区，容器内的装饰性出血仍保留。

## 生命周期与验证

- 编辑模式不自动运行，因此打开、保存 Prefab 不会写入当前电脑的安全区域。
- 首次显示、窗口尺寸/方向/安全区变化，以及逻辑画布尺寸变化都会重新布局。
- 每次都从首次记录的原始布局计算；禁用组件恢复原布局，重复开关不会累计偏移。
- 系统切换期间的零尺寸或无效安全区域保留上一份有效布局。
- 启动更新页尚未注册方向系统时也能工作：所有组件共用每帧一次的框架屏幕采样。
- `ApplySnapshot(FrameScreenSnapshot)` 供临时预览和测试注入设备数据；不要把应用了模拟数据的实例保存覆盖原 Prefab。

原有页面不会自动接入。项目选择要避让的内容，完整背景与既有绑定保持原样。
