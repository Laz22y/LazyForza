# ADR-0001: LazyForza 技术栈与透明 Overlay

- 状态：已接受
- 日期：2026-07-22

## 背景

LazyForza 是只消费 FH6 官方单向 UDP Data Out 的 Windows 桌面工具。核心要求包括 324 字节低延迟解析、可测试的换挡/路线算法、SQLite 持久化、Fluent 风格主界面，以及逐像素透明、置顶、可点击穿透、不抢焦点的 60 Hz HUD。

本机实测环境为 Windows 10.0.26200、Visual Studio Community 18.8、Windows SDK 10.0.26100.0、.NET SDK 9.0.316。未安装 .NET 10 SDK；本机 NuGet 缓存中的 Windows App SDK 为 1.5.240311000，不是需求参考的稳定 2.2.x。仓库根目录的 `.git` 为空目录，Git 未识别为仓库；本 ADR 不擅自初始化或替换它。

## 选项比较

| 选项 | Windows/Fluent | 透明 HUD | UDP/SQLite/算法测试 | 部署与维护 | 结论 |
|---|---|---|---|---|---|
| C#/.NET + WinUI 3 | 官方现代 Windows UI、Fluent 最直接 | 顶层窗口逐像素透明、无焦点和点击穿透需要额外 Win32/Composition 工作，当前稳定 SDK 不可用 | `Span`/Channel/Task 与测试工具成熟 | 单一语言、模块边界清楚 | 目标主 UI 候选，但当前环境不能诚实验证 |
| C#/.NET + WPF/Win32 | WPF 控件可实现 Fluent 视觉 token | `AllowsTransparency` 提供逐像素 alpha；Win32 扩展样式提供 topmost/no-activate/click-through | 与上项相同；Windows 自带 `winsqlite3.dll` 可避免高频 ORM 开销 | 当前 SDK 可直接构建，部署简单 | **MVP 采用** |
| C++ + WinUI/Qt | 原生控制强，Qt 跨平台 | Win32 layered window 成熟 | UDP/SQLite 强，但领域算法测试与内存安全成本更高 | 双 UI/原生依赖、构建和长期维护成本高 | 不选 |
| Rust + windows-rs/Slint 等 | Win32 能力可达，Fluent 生态不统一 | 可直接控制 layered window | 安全且高效，但 UI、SQLite、异步与 Windows 打包组合复杂 | 团队学习和扩展成本最高 | 不选 |

## 决策

长期基线仍为 **C# + .NET 10 LTS**。由于本机没有 .NET 10，本次源码暂以 `net9.0` / `net9.0-windows` 构建，代码不使用阻碍升级的 .NET 9 专属 API；安装 .NET 10 后统一改 TFM 并复跑全部测试。

MVP 采用 WPF 主程序和独立的 WPF/Win32 layered Overlay 项目。领域、遥测、算法、存储和模块契约不引用 WPF；Overlay 只消费 HUD 状态快照并渲染，不承载解析或业务算法。未来切换 WinUI 3 主程序时这些层无需改动。

SQLite 通过 Windows 自带的 `winsqlite3.dll` 薄封装使用，避免给每帧绑定 ORM；原始高频包写入版本化 `.lfztelemetry` 文件，SQLite 只保存设置、元数据和派生结果。

## Overlay Spike 验证口径

实现位于 `LazyForza.Overlay`：

- `WindowStyle=None`、`AllowsTransparency=True`、根背景透明：逐像素 alpha 和非矩形窗口；
- `Topmost=True`：置顶；
- `WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW`：不进入 Alt-Tab、不抢游戏焦点；
- 可切换 `WS_EX_TRANSPARENT`：锁定布局时点击穿透；
- `CompositionTarget.Rendering` 限频到最多 60 Hz，绘制矢量弧线与数字；
- WPF 设备无关单位配合 `VisualTreeHelper.GetDpi`；位置、缩放、不透明度和显示器标识由 `OverlayLayout` 持久化；
- `WM_NCHITTEST` 在解锁模式允许拖动，锁定模式返回透明命中。

自动检查覆盖扩展样式计算、布局序列化和 60 Hz 限频。真正叠加到 FH6 的焦点行为、独占/无边框窗口模式、多显示器混合 DPI 与帧时间仍必须按 `VALIDATION_WITH_FH6.md` 实测；在完成实测前不声称这些项目已通过。

## 后果

- 优点：当前机器可构建；透明 HUD 的 Win32 行为明确；核心可测试且 UI 无关。
- 代价：主程序不是 WinUI 3 原生控件，需要用集中 token 和 WPF 控件模板复刻 Fluent；未来迁移主壳时保留 Overlay 或换成 Composition 均可。
- 约束：只监听官方 UDP，不读取游戏内存、不注入 DLL、不修改 FH6 进程。

