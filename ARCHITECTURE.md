# LazyForza 架构

## 数据流

```text
FH6 UDP / Deterministic Simulator / .lfztelemetry Replay
        -> explicit 324-byte little-endian parser + plausibility checks
        -> immutable TelemetryFrame (Raw + Normalized + source label)
        -> TelemetryHub (reference-counted source, bounded subscriber channels)
             -> DashboardModule -> ShiftLearner -> Dashboard HUD state
             -> LapAnalysisModule -> Track matcher/lap state -> Lap HUD state
             -> EstateRaceModule -> estate timing/network -> race HUD state
             -> Recorder -> versioned raw packet file
        -> WPF/Win32 Overlay (render only)
        -> winsqlite3 metadata/derived-data store

EstateRaceModule <-> LazyForza.RaceServer protocol v2
                 <-> ASP.NET native server / Cloudflare Durable Object
```

`TelemetryHub` 只有存在订阅者时运行数据源；HUD/实时分析订阅是 bounded、`DropOldest` 的 latest-wins 通道。模块停用会取消自己的 Task、释放订阅并移除 HUD contribution。所有业务模块关闭后主窗口、模块管理与诊断仍能运行。
地产赛事的赛道、维修区和碰撞状态计算会在遥测频率高于处理上限时合并过密帧，不让 FH6 帧率转化为待处理积压；计圈仍使用源时间戳和跨点插值，手动赛道录入不降采样。

## 项目边界

- `LazyForza.Domain`：不可变领域记录；无 UI、网络、数据库引用；
- `LazyForza.Telemetry`：324 字节解析、UDP、Simulator、流统计、录制/回放与订阅 Hub；
- `LazyForza.Analysis`：鲁棒 RPM 桶、逐挡换挡交点、路线重采样、受约束投影、确定性分段和颜色状态；
- `LazyForza.Storage`：`winsqlite3.dll` 薄封装、迁移、设置/学习/路线/圈仓储；
- `LazyForza.Modules.Abstractions`：模块、遥测订阅、HUD contribution 与持久化契约；
- `LazyForza.Modules.Dashboard`：Dashboard 生命周期、状态快照和学习器编排；
- `LazyForza.Modules.LapAnalysis`：路线/圈状态机、存储与 Lap HUD 状态；
- `LazyForza.Modules.EstateRace`：地产环道几何、计时、维修区、赛事网络客户端与赛事 HUD 状态；
- `LazyForza.Overlay`：WPF 矢量 HUD 和 Win32 窗口样式；不解析 UDP、不计算业务算法；
- `LazyForza.Update`：更新查询、下载和更新包完整性验证；
- `LazyForza.App`：WPF Fluent 风格主壳、编译期可信模块目录、导航、设置与诊断；
- `tools/*`：官方赛道目录生成、性能门禁和地产诊断工具，不参与产品运行时依赖。

模块之间不使用全局静态服务定位器。`BuiltInModuleCatalog` 只注册可信编译期模块，当前产品不执行未知 DLL。

## Overlay

WPF `AllowsTransparency=True` 提供逐像素 alpha；`WindowStyle=None` 去除标题栏；Win32 扩展样式添加 `WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW`，锁定布局时再添加 `WS_EX_TRANSPARENT`。`WM_MOUSEACTIVATE` 返回 `MA_NOACTIVATE`。渲染由 `CompositionTarget.Rendering` 驱动并以 `FrameRateLimiter` 限制到最多 60 Hz。

Dashboard 和 Lap 是两个独立 `IHudContribution`。两者同时存在时共享同一弧心；只有 Lap 时 Overlay 改用紧凑独立面板。位置、缩放、不透明度、显示器标识、点击穿透、锁定、减少动态、加速度跟随开关/强度及全部 HUD 等待与淡入淡出时间保存在 `OverlayLayout`。`DashboardHudDynamics` 负责可测试的静止判定、透明度过渡与加速度弹簧状态；`LapHudDynamics` 对持续无匹配证据计时，淡出后按 `CompetitionSessionId` 锁定到比赛结束。菜单、暂停和回转沿用同一会话 ID，不会解除锁定。窗口仍保持透明、置顶、穿透，业务模块不直接操纵窗口。

## 协议与状态

解析器只解释官方命名偏移 0..322；偏移 323 保留为 `UndefinedTailByte`，没有业务含义。所有字段显式小端读取，同时使用 `IsRaceOn`、RPM、速度、Class/PI、驱动形式与 Fuel 范围检查该假设。`TimestampMS` 处理重复、乱序、回绕和间隔估计；断流只进入 Stale/Disconnected，不推断暂停、倒带或完赛。

每个 `TelemetryFrame` 同时保留原始包、原始字段和已确认的换算值。`TireTemp` 不附温标；`GripUi = clamp(1 - abs(TireCombinedSlip), 0, 1)` 明确属于 UI 推导。

## 换挡

有效样本按 RPM 桶收集 Power/Torque/Boost，使用中位数和中位绝对偏差；各挡以 `RPM / Speed` 拟合可观测传动比例。每对相邻挡位独立计算：

```text
n_after = n * K_next / K_current
T(n) * K_current <= T(n_after) * K_next
cueRpm = targetRpm - rpmRiseRate * totalLatency
```

无交点时才使用限制器前安全 fallback。车辆指纹至少含 CarOrdinal、Class、PI、Drivetrain、Cylinders、MaxRPM，并预留曲线/挡位摘要；可观察配置改变会使模型 Stale。

## 路线与圈

环形模板显式闭合，定点模板保留独立起终点；5 m 重采样后保存三维点、累计 `s` 和切向。投影只在上一段附近搜索，同时使用高度，避免交叉/立交桥跳段。路线状态从 Unknown → Candidate → Confirmed；置信度下降会退回。分段算法版本为 `sector-v1.1.0-start-line`、SchemaVersion 2，目标数为 `clamp(round(length/350m), 4, 16)`，优先已有有效圈的稳定制动入口，特征不足时按距离均分。

有效圈要求整圈至少 95% 投影可信且每个分段覆盖完整。起终点回绕帧先结束上一圈，再进入新圈，防止 `CurrentLap=0` 污染最后一段时间。

累计分段 Delta 与分段颜色使用不同基准：颜色可按单个分段比较本场/全数据集最优；Dashboard Delta 必须先选出同赛道、同性能等级的真实历史最快完整 `LapRecord`，再累计该圈从 S1 到已通过分段的时间。当前圈边界时间在首次进入下一分段时锁定，因此显示的是从圈起点到分段终点的累计差，不是单段差，也不是拼接的理论圈。

历史圈从 SQLite 恢复后按路线、方向和 `SectorSchemaVersion` 限定比较范围。模块常驻内存的最多 50 圈只保存 `LapSummary`（元数据和分段），不加载高密度 `LapSamples`。UI 勾选 0–4 圈后，由用户点击“显示勾选圈数据”一次性批量加载采样；模块只缓存最近使用的 8 个完整圈。速度曲线使用保留峰谷的包络降采样和冻结几何，悬停命中继续使用按进度二分；走线图缓存绘制和屏幕空间命中网格。每条赛道在 SQLite 中最多保留 50 圈，并为每个性能等级保护一条历史最快有效圈。

## 存储

当前 `LazyForzaStore.CurrentSchemaVersion` 为 12。基础表包含 AppSettings、ModuleSettings、Sessions、VehicleProfiles、EngineCurveBins、GearModels、ShiftTargets、TrackTemplates、TrackPoints、SectorDefinitions、Laps、LapSegments 与 LapSamples；后续迁移加入动态遥测、地产赛道定义/检查点/维修区/计时类型、地产策略样本和圈速玩家代号。圈速显式保存官方 CarClass/PI；升级前缺失的 CarClass 会先从旧车辆指纹恢复 PI，再按 D 100–400、C 401–500、B 501–600、A 601–700、S1 701–800、S2 801–900、R 901–998、X 999 的规范区间补齐。

圈速列表使用两次批量查询读取圈元数据与所有分段，不再逐圈查询；只有图表确认显示的圈才批量读取 `LapSamples`。兼容用的 `LoadLaps` 也改为固定次数的批量查询。写入使用事务，开启外键与 WAL；高频原始包不写 SQLite，而是顺序写入版本化 `.lfztelemetry`。

迁移必须按版本顺序追加并能直接升级旧用户库。备份、导入导出和兼容读取属于结构变更的同一影响面；详细修改检查见 [`AGENTS.md`](AGENTS.md)。

## 地产赛事跨仓库边界

客户端在 `LazyForza.Modules.EstateRace` 内维护协议 v2 的本地 DTO 和赛事快照模型；服务端仓库在 .NET Protocol 与 Cloudflare TypeScript 中各有一份协议实现。三份模型没有代码生成器，字段、枚举、默认值、可空性和 JSON 名称必须人工保持兼容。

客户端上传本机轨迹和可靠圈/维修事件；服务端权威管理圈数、排名、阶段、旗语、处罚、调查和结果。连续遥测可以 latest-wins，圈完成与维修完成事件必须通过事件 ID、确认和去重可靠传递。完整同步文件和验证命令见客户端与 RaceServer 两边的 `AGENTS.md`。
