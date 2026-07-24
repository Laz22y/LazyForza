# LazyForza 开发 Agent 提示词套装

本套提示词用于让编码 Agent 在当前目录直接开始开发 `LazyForza`。它不是需求摘要，而是带有技术边界、数据语义、实施顺序、验收标准和测试要求的执行指令。

## 快速使用

### 单 Agent 连续开发

将 [`prompts/00_MASTER_IMPLEMENTATION_PROMPT.md`](./prompts/00_MASTER_IMPLEMENTATION_PROMPT.md) 完整交给 Agent。该提示词要求 Agent 从技术选型、工程初始化一直工作到可运行 MVP，不允许只交付计划或静态原型。

### 分阶段开发或上下文有限

按顺序使用：

1. [`prompts/01_FOUNDATION_AND_TELEMETRY.md`](./prompts/01_FOUNDATION_AND_TELEMETRY.md)
2. [`prompts/02_DASHBOARD_AND_SHIFT_LEARNING.md`](./prompts/02_DASHBOARD_AND_SHIFT_LEARNING.md)
3. [`prompts/03_TRACK_AND_LAP_ANALYSIS.md`](./prompts/03_TRACK_AND_LAP_ANALYSIS.md)
4. [`prompts/04_FLUENT_UI_INTEGRATION_AND_QA.md`](./prompts/04_FLUENT_UI_INTEGRATION_AND_QA.md)

后续阶段开始前，Agent 必须先检查前一阶段的源代码、测试、ADR 和实际构建结果，不能假定它们已经正确完成。

## Agent 必读资料

以下资料已经位于仓库中，提示词会强制 Agent 在编码前阅读：

- [`FH6_TELEMETRY_DEVELOPMENT_GUIDE.md`](./FH6_TELEMETRY_DEVELOPMENT_GUIDE.md)：324 字节协议、完整字段、路线学习、自动识别、圈速分析与换挡算法。
- [`design/FH6_TELEMETRY_DASHBOARD_SPEC.md`](./design/FH6_TELEMETRY_DASHBOARD_SPEC.md)：仪表盘布局、数据绑定、颜色、动画、透明窗口要求。
- [`design/FH6_TELEMETRY_DASHBOARD_DESIGN_V2_TRANSPARENT.png`](./design/FH6_TELEMETRY_DASHBOARD_DESIGN_V2_TRANSPARENT.png)：视觉基准。
- [`design/FH6_TELEMETRY_DASHBOARD_PROMPT_V2.md`](./design/FH6_TELEMETRY_DASHBOARD_PROMPT_V2.md)：视觉设计意图补充。
- [Forza Horizon 6 官方 Data Out 文档](https://support.forza.net/hc/en-us/articles/51744149102611-Forza-Horizon-6-Data-Out-Documentation)：最终协议事实来源。

## 推荐技术判断

当前需求是 Windows 专用、需要低延迟 UDP、透明置顶 HUD、Fluent 风格本体、SQLite 持久化和可测试分析算法。默认推荐：

- 语言：`C#`。
- 运行时：`.NET 10 LTS`。
- 主程序 UI：优先 `WinUI 3 + Windows App SDK` 的最新稳定版；截至 2026-07-22 为 `Windows App SDK 2.2.x`。
- HUD：先做透明、置顶、鼠标穿透、60 Hz 刷新的最小技术验证，再锁定 WinUI/Win32 Composition 或 WPF/Win32 Layered Window 实现。
- 存储：`Microsoft.Data.Sqlite`；高频原始包写入分块二进制会话文件，不逐包同步写 SQLite。
- 测试：xUnit 或当前仓库已有的 .NET 测试框架。

Agent 必须先在 `docs/adr/ADR-0001-technology-stack.md` 记录 C#、C++、Rust 等候选方案的比较，以及 Overlay 技术验证结果，然后继续编码。若没有强证据，不要偏离 C#/.NET；若 WinUI 透明 Overlay 验证失败，可以保留 WinUI 3 主程序并为 Overlay 使用独立 Win32/WPF 宿主。

## “全场最佳”的准确语义

FH6 Data Out 不提供对手分段时间或对手遥测，因此紫色不能宣称为在线世界纪录或游戏内所有参赛车手最快。MVP 中定义为：

> 在当前分析器可见、且满足同一赛道模板、方向、分段版本和比较范围的全部有效 `LapRecord` 中最快的分段时间。

UI 应显示“当前数据集全场最佳”或提供同义提示。未来可以通过导入其他玩家圈记录扩展数据集，不需要改动颜色状态机。

颜色优先级：

1. 紫色：当前数据集全场最佳；
2. 绿色：个人最佳但不是全场最佳；
3. 黄色：有效完成但慢于个人最佳；
4. 灰色：未跑、无参考、无效或尚未完成。

## 官方技术依据

- Microsoft 当前将 WinUI 3/Windows App SDK 作为新 Windows 原生桌面应用的推荐方案，并支持 C# 与 C++。
- `.NET 10` 为 LTS，支持期至 2028-11。
- Windows 分层窗口支持逐像素 alpha、透明区域鼠标穿透以及 `WS_EX_TRANSPARENT`。

任何 Agent 都不得通过读游戏内存、DLL 注入或修改游戏进程取得数据；LazyForza 只使用官方单向 UDP Data Out 与用户主动导入的数据。
