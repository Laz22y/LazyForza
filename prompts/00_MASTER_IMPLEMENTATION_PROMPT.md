# LazyForza 总控实施提示词

> **Status: Historical**
> 本文是项目初始构建指令，不代表当前架构或待办。不要重新执行。现行开发入口见 [`../AGENTS.md`](../AGENTS.md)。

```text
你是 LazyForza 的首席 Windows 桌面应用工程师、遥测算法工程师和 UI 实现工程师。请在当前仓库内直接工作，完成一个可编译、可运行、可测试的 MVP。不要只给计划、伪代码、静态图片或未接线的页面；在安全且不需要用户额外授权的范围内持续实现、构建和测试，直到下述验收标准满足。

产品目标
========

开发一款名为 “LazyForza” 的 Forza Horizon 6 Windows 遥测辅助工具。它只使用官方 FH6 Data Out 单向 UDP，不读取游戏内存、不注入 DLL、不修改游戏进程。程序必须模块化，每个功能模块可独立打开、关闭、启动、停止并持久化状态；后续可以添加新模块而不修改遥测核心。

本次必须完成两个业务模块：
1. Dashboard：透明游戏 HUD 仪表盘，并自动学习当前车辆配置的发动机曲线、挡位比例和最佳换挡提示点。
2. Lap Analysis：自动学习/识别赛道、自动分段、弧形分段 HUD、逐圈存储与程序本体内的完整圈速分析界面。

程序本体采用 Fluent UI 风格。两个 HUD 模块必须可以分别开关；Lap HUD 在 Dashboard 同时开启时位于仪表盘上方并跟随其弧度，Dashboard 关闭时也必须能独立显示和定位。

编码前必须完整阅读
==================

1. FH6_TELEMETRY_DEVELOPMENT_GUIDE.md
2. design/FH6_TELEMETRY_DASHBOARD_SPEC.md
3. design/FH6_TELEMETRY_DASHBOARD_DESIGN_V2_TRANSPARENT.png
4. design/FH6_TELEMETRY_DASHBOARD_PROMPT_V2.md
5. 官方文档：https://support.forza.net/hc/en-us/articles/51744149102611-Forza-Horizon-6-Data-Out-Documentation

仓库文档与官方文档冲突时，以官方协议事实为准；路线识别、分段、抓地指标和最佳换挡属于算法推导，不能伪装成游戏直接输出字段。

第一步：技术选型，但不要停在选型
==========================

先检查本机 SDK、现有仓库和构建环境，然后创建 docs/adr/ADR-0001-technology-stack.md。至少比较：
- C#/.NET；
- C++/WinUI 或 Qt；
- Rust + Windows UI 方案。

评价标准：官方 Windows 支持、透明置顶 HUD、Fluent UI、低延迟 UDP、SQLite、算法测试便利性、部署复杂度、长期可维护性和模块扩展成本。

默认结论应为 C# + .NET 10 LTS。主程序优先 WinUI 3 + 当前已安装/可用的最新稳定 Windows App SDK（2026-07-22 的参考版本为 2.2.x），禁止无理由使用 Preview/Experimental SDK。

在锁定 UI 技术前先实现 Overlay Spike：
- 透明根背景；
- 非矩形/逐像素 alpha；
- Always-on-top；
- 可切换鼠标穿透；
- 无标题栏；
- 60 Hz 更新简单弧线和数字；
- 多显示器、高 DPI、缩放和窗口位置持久化；
- 不抢占游戏焦点。

如果 WinUI 顶层透明窗口无法稳定满足这些条件，不要伪装成功。记录证据后，采用 WinUI 3 主程序 + 独立 Win32/Composition Overlay，或 C# WPF/Win32 Layered Window Overlay。语言仍保持 C#，业务核心不依赖具体 UI 框架。完成 ADR 后继续实现，不等待用户再次确认。

建议解决方案结构
================

允许根据 UI Spike 结果调整项目名，但必须保持同等边界：

LazyForza.sln
src/
  LazyForza.Domain/                 # 纯领域模型、无 UI/网络/数据库依赖
  LazyForza.Telemetry/              # UDP、324 字节校验、解析、归一化、流状态
  LazyForza.Analysis/               # 换挡、路线、投影、分段、Delta、圈速比较
  LazyForza.Storage/                # SQLite、二进制会话、迁移和仓储
  LazyForza.Modules.Abstractions/   # 模块契约和上下文接口
  LazyForza.Modules.Dashboard/      # Dashboard 模块逻辑与视图模型
  LazyForza.Modules.LapAnalysis/    # Lap 模块逻辑与视图模型
  LazyForza.Overlay/                # 透明 HUD 窗口与渲染；不得承载业务算法
  LazyForza.App/                    # Fluent 主程序、DI、导航、设置
tests/
  LazyForza.Telemetry.Tests/
  LazyForza.Analysis.Tests/
  LazyForza.Storage.Tests/
  LazyForza.IntegrationTests/
docs/adr/

禁止让 UI 直接解析 UDP，禁止让数据库实体成为所有层共享的领域模型，禁止模块之间通过全局静态变量通信。

模块系统
========

定义稳定模块契约，至少包括：
- Id、DisplayName、Version；
- 模块依赖声明；
- InitializeAsync、StartAsync、StopAsync；
- IsEnabled、运行状态、错误状态；
- 设置页/主页面/HUD contribution 的可选入口；
- CancellationToken；
- 模块设置持久化；
- 幂等 Start/Stop；
- 模块失败隔离与日志。

内置 Dashboard 和 LapAnalysis 必须通过同一模块契约注册，不能在 App 中写两个特殊 if。MVP 可以先使用编译期 ModuleCatalog，但接口和加载边界要允许以后从 Modules 目录发现受信任程序集；不要在本阶段执行未知 DLL。

即使所有业务模块关闭，主程序、诊断页和模块管理仍可运行。Telemetry Core 由启用模块的引用计数/订阅生命周期决定启动和停止。

遥测核心
========

严格实现 FH6 324 字节包：
- 只解析官方命名的偏移 0..322；偏移 323 保留为未定义；
- 初始按小端序实现，但必须通过范围合理性验证和测试记录为待实测假设；
- UDP 端口可配置，默认使用 5301，避开官方要求避开的 5200..5300；
- 支持 localhost 和指定监听地址；
- 发包率可变，不能假定 60 Hz；
- 处理丢包、重复、乱序、异常间隔、TimestampMS 回绕和断流；
- 菜单、暂停、回放、倒带和完赛会停止发包，不能只用断流原因猜状态；
- UI 不逐包 Dispatcher；接收/解析在线程后台运行，发布不可变快照，HUD 以显示刷新率或最多 60 Hz 拉取最新值；
- 保存原始值和标准化值；未确认单位不得擅自标注；
- 提供 packet replay，让没有运行 FH6 时也能回放测试会话。

使用 bounded Channel、ring buffer 或等价背压方案；实时 UI 优先最新帧，持久化通道不能无限增长。所有 Socket、Channel、后台 Task 和文件在模块停止/应用退出时必须可取消并正确释放。

存储
====

使用 SQLite 保存元数据、设置和派生结果；高频原始 UDP 包写入版本化的分块二进制会话文件，避免每帧同步写数据库。至少持久化：
- ModuleSettings；
- AppSettings/OverlayLayout；
- Sessions；
- TrackTemplates、TrackPoints、SectorDefinitions 及算法版本；
- Laps、LapSegments、用于图表的降采样 LapSamples；
- VehicleProfiles、EngineCurveBins、GearModels、ShiftTargets；
- 算法版本、置信度、有效性和失效原因。

数据库必须有迁移/SchemaVersion、外键和必要索引。写入使用事务；路径位于明确的应用数据目录；测试使用临时目录/临时数据库。

Dashboard 模块
==============

严格参考 design/FH6_TELEMETRY_DASHBOARD_SPEC.md 和透明 PNG，但最终界面必须用可缩放的矢量/XAML/Composition/Drawing 实现，不能把整张 PNG 当成实时仪表盘。

必须实现：
- 两条严格平行的弧：宽结构弧 + 细分段 RPM/红区弧；
- 左圆：Gear、Speed × 3.6 km/h；
- 右圆：CurrentEngineRpm、Power/1000 kW、Torque N·m，圆环随 RPM 向红色渐变；
- 左下四个无胎纹胶囊：TireTemp 与基于 TireCombinedSlip 的 UI 抓地指标；温度单位未实测前不写 C/F；
- 中下：左 Brake 深红 #8B1E2D，右 Accel 深绿 #0B6B43；
- 油门填充内 1.2 秒左右的循环动力渐变，Accel=0 暂停，支持减少动态；
- 右下 Class/PI 双段徽章；
- D/C/B/A/S1/S2/R/X 的范围和颜色 token 以设计规格为准；
- RGBA 透明背景、比例缩放、位置/不透明度/显示器设置、锁定与解锁移动模式；
- 数据断流时明显进入 Disconnected/Stale 状态，不能冻结成仍在驾驶的假象。

换挡自动学习
==============

不能把峰值扭矩、峰值功率或红线直接当成最佳换挡点。按开发指南实现：

1. VehicleProfile 不是只有 CarOrdinal。指纹至少综合 CarOrdinal、CarClass、PI、DrivetrainType、NumCylinders、EngineMaxRpm、实测 RPM/Speed 斜率和曲线摘要。
2. 采集高油门、稳定挡位、足够车速、低滑移的有效样本；过滤换挡过渡、离合器、跳跃、重置、碰撞、异常间隔和明显轮胎打滑。
3. 按 RPM 分桶保存 Power/Torque/Boost，多次样本使用中位数/鲁棒统计并平滑，记录每桶样本数和置信区间。
4. 对每挡拟合 K_i = RPM / Speed，或从换挡前后 RPM 跌落学习相邻挡位比。
5. 对每一对相邻挡位独立计算 n_after = n × G_next/G_i，并比较 T(n)×G_i 与 T(n_after)×G_next，或同速功率交点。
6. 若限制器前没有交点，目标位于经安全边界修正的限制器前。
7. 提示点考虑 rpmRiseRate 与玩家/执行延迟：cueRpm = targetRpm - rpmRiseRate × totalLatency。
8. 输出 Learning/Insufficient/Ready/Stale 状态、进度、置信度和每个相邻挡位的目标 RPM；数据不足时绝不伪造最佳点。
9. 检测配置变化并使旧模型 Stale，允许用户重新学习。
10. Dashboard RPM 弧在到达 cueRpm 时提供克制且清晰的换挡视觉提示。

使用合成发动机曲线和齿比编写确定性单元测试，验证交点、无交点、数据不足、滑移过滤和模型失效。

赛道学习、识别和自动分段
========================

FH6 不提供 TrackOrdinal、赛道名或官方检查点。必须实现自己的 TrackTemplate 与 LapRecord，二者分离。

首次环形路线学习：
- 不把进入赛事后的残缺首段当完整圈；
- 从一次可靠起终点穿越记录到下一次可靠穿越；
- 组合 LapNumber、CurrentLap、起终点穿越、方向和流状态；
- 排除/标记倒带、重置、传送、位置跳变、异常丢包和严重跑偏；
- 按空间距离重采样、平滑，保存 x/y/z、累计距离 s、切向、包围盒、起终点和方向；
- 一圈产生最低可用模板，2–3 圈更新路线走廊与置信度；
- 用户可以保存、命名、重命名、删除或重新学习模板；危险删除需要确认。

自动识别使用 Unknown -> Candidate -> Confirmed 状态机：
- 初始位置/包围盒/起点邻域筛候选；
- 方向、高度、点到折线距离、进度连续性共同评分；
- 最佳候选显著优于第二名才确认；
- 位置跳变或置信度下降后退回；
- 正反向为不同模板；
- 交叉、立交桥、EventLab 共享起点不能只用最近点；
- 只有开始收到驾驶数据并积累足够轨迹后才能识别，不承诺进赛道瞬间识别。

自动分段算法必须确定、版本化、可测试：
1. 将模板按约 5 m 间距重采样并平滑；
2. 计算航向变化/曲率，结合有效圈的稳定刹车特征识别弯道和制动区；
3. 合并很短的相邻区间，避免碎片化；
4. 目标分段数可用 clamp(round(routeLength / 350m), 4, 16) 起步；
5. 单段长度设置合理上下限，边界优先落在制动区入口、弯心/弯组结束或明显几何特征处；
6. 几何/采样不足时退化为按距离均分；
7. 保存 SectorSchemaVersion，算法变化时不要静默让旧圈与新分段错位；应迁移、重投影或标为不可直接比较。

实时位置投影到折线累计距离 s，搜索窗口受上一进度约束，处理起终点回绕。跑偏、传送或投影不可信时冻结 Delta/使当前圈无效，绝不跳到路线另一段。

### Lap HUD

在 Dashboard 上方渲染与其弧度同心/平行的 Sector Strip。各分段弧长按路线距离比例分配，间隔均匀，当前段有细描边或亮度提示。状态颜色：
- 灰色：未跑、未完成、无参考或无效；
- 黄色：有效完成但慢于个人最佳，阈值至少 max(0.15s, 1%) 以避免噪声误判；
- 绿色：个人最佳但不是当前数据集全场最佳；
- 紫色：当前数据集内的全场最佳，优先级高于绿色。

“全场最佳”只允许表示同一 TrackTemplateId、方向、SectorSchemaVersion 和当前比较范围中，分析器可见的所有有效 LapRecord 的最快时间。FH6 没有对手分段遥测，UI 不得声称在线世界纪录；使用提示文字解释“当前数据集全场最佳”。

当 Dashboard 关闭时，Lap HUD 使用自己的可拖动锚点继续显示；当 LapAnalysis 模块关闭时，Dashboard 不受影响。

程序本体的圈速分析页面
======================

使用 Fluent UI 风格，至少包含：
- 左侧/顶层 NavigationView：概览、模块、圈速分析、赛道、车辆与换挡学习、设置、诊断；
- Sessions/Laps 列表，按赛道、车辆、日期、有效性筛选；
- 每圈总时间、有效性、车辆配置、个人最佳标记；
- 分段表：当前圈、个人最佳、当前数据集全场最佳、Delta、颜色状态；
- 可选择 2–4 圈比较；
- 以路线距离为横轴的速度、RPM、挡位、油门、刹车、Delta 图；
- 简化赛道折线图，并能按分段/Delta 着色；
- 点击分段后显示该段刹车点、最低弯速、给油点和时间损失摘要；
- 空状态、首次学习引导、无数据/断流/无效圈解释；
- 大数据量下虚拟化列表和降采样图表，不把几十万帧一次性绑定 UI；
- 可删除/重命名/重新学习赛道，删除前显示关联圈数并确认；
- CSV 导出属于可选加分项，不得阻塞核心功能。

Fluent UI 和可访问性
==================

- 主窗口使用 Fluent Navigation、卡片、InfoBar、TeachingTip、ToggleSwitch、TabView/DataGrid 等合理控件；
- 支持深色主题，设计 token 集中管理；主界面可以使用 Mica，Overlay 不使用会降低可读性的桌面 Acrylic；
- 支持 Windows 文本缩放、高 DPI、多显示器、键盘导航和可访问名称；
- 数字使用等宽/Tabular Numerals；
- 动画遵循系统减少动态设置；
- 模块开关必须真实调用 Start/Stop，不是只隐藏页面；
- 设置页面提供监听 IP/端口、单位、Overlay 显示器/位置/缩放/不透明度/点击穿透、数据保留策略。

诊断与日志
==========

提供 Diagnostics 页面：
- 当前监听地址与端口；
- 包速率、有效/无效包数、丢包/乱序估计、最后包时间；
- 当前流状态、车辆指纹、路线候选/置信度、当前圈有效性；
- 模块状态与最近错误；
- 打开日志/数据目录；
- 开始/停止原始数据录制与选择会话回放。

日志不得记录无上限逐帧内容；使用结构化、分级、滚动日志，不写入敏感信息。

测试和验收
==========

必须有自动测试：
- 324 字节解析、所有关键偏移、长度拒绝、合理性验证；
- TimestampMS 回绕、丢包/重复/乱序/断流；
- 模块启停幂等、依赖和失败隔离；
- 车辆指纹、RPM 分桶、挡位比和每挡换挡点；
- 路线重采样、方向、高度、交叉点附近的受约束投影；
- 起终点穿越、圈有效性、自动分段确定性；
- 灰/黄/绿/紫状态机及紫色的本地数据集语义；
- SQLite migration、保存/读取和临时数据库隔离；
- replay 输入下 Dashboard/Lap 模块端到端集成。

提供一个 deterministic simulator/replay fixture，让应用在没有 FH6 时展示变化的 RPM、挡位、输入和一条模拟环形路线；必须明确标为 Demo/Replay，不能与实时数据混淆。

最终验收标准：
1. clean checkout 按 README 命令可以 restore/build/test/run；
2. 主程序能打开，模块页能独立启停 Dashboard 和 LapAnalysis；
3. Demo/Replay 模式下两个 HUD 有实时变化，透明、置顶、可穿透且可定位；
4. Dashboard 与设计规范关键布局一致；
5. 换挡学习显示真实进度/置信度，合成数据能得到可验证的每挡目标；
6. 能完成路线学习、生成分段、保存圈、自动匹配并显示分段颜色；
7. 主程序能浏览并比较保存的圈；
8. 所有测试通过；
9. README 包含 FH6 Data Out 配置步骤（127.0.0.1、默认 5301）、限制说明和真实游戏验证清单；
10. 不宣称不存在的赛道 ID、对手数据、温度单位或在线全场纪录。

工作方式
========

- 先检查现有文件和工具，不覆盖用户已有内容。
- 将工作拆为可运行的纵向切片；每完成一片就 build/test。
- 不以“需要真实游戏”为理由停止：协议、算法和 UI 使用合成 fixture/replay 先完成；把必须实测的项目明确列入 VALIDATION_WITH_FH6.md。
- 不添加无必要的大型依赖；新增 NuGet 包必须说明用途并固定兼容版本。
- 遇到阻塞先做只读诊断和安全替代；只有缺少授权或会改变任务方向时才询问用户。
- 每个实现结论以源码、测试或构建输出为证据。最终报告完成内容、验证命令、尚需真实 FH6 验证的事项以及关键文件路径。
```
