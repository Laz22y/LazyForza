# ADR-0001: LazyForza 技术栈与透明 Overlay

- 状态：已接受
- 日期：2026-07-22

> 当前实现说明（2026-08-24）：WPF/Win32 Overlay 的决定仍有效；仓库当前固定使用 .NET SDK 9.0.316 和 `net9.0` / `net9.0-windows`。下文的 .NET 10 是早期目标，不是现行迁移计划。当前技术栈以 `global.json`、项目文件和根目录 `AGENTS.md` 为准。

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

本次决策采用 C#、`net9.0` / `net9.0-windows` 与 WPF/Win32。早期曾把 .NET 10 LTS 作为后续目标；该目标不构成当前任务，任何框架升级都必须根据当时的项目配置和明确需求单独评估。

本次采用 WPF 主程序和独立的 WPF/Win32 layered Overlay 项目。领域、遥测、算法、存储和模块契约不引用 WPF；Overlay 只消费 HUD 状态快照并渲染，不承载解析或业务算法。该分层也保留了当时评估其他主壳技术的边界，但不表示当前存在迁移计划。

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
- 代价：主程序不是 WinUI 3 原生控件，需要用集中 token 和 WPF 控件模板复刻 Fluent；更换主壳或 Overlay 技术不属于本 ADR 的现行工作项。
- 约束：只监听官方 UDP，不读取游戏内存、不注入 DLL、不修改 FH6 进程。
