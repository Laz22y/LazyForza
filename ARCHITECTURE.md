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
- `LazyForza.Speech`：可替换的合成／播放契约、有界 PCM、原创无线电音和可取消输出；不依赖 WPF、网络 SDK 或赛事状态；
- `LazyForza.App`：WPF Fluent 风格主壳、编译期可信模块目录、导航、设置与诊断；
- `tools/*`：官方赛道目录生成、性能门禁和地产诊断工具，不参与产品运行时依赖。

模块之间不使用全局静态服务定位器。`BuiltInModuleCatalog` 只注册可信编译期模块，当前产品不执行未知 DLL。

## Overlay

WPF `AllowsTransparency=True` 提供逐像素 alpha；`WindowStyle=None` 去除标题栏；Win32 扩展样式添加 `WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW`，锁定布局时再添加 `WS_EX_TRANSPARENT`。`WM_MOUSEACTIVATE` 返回 `MA_NOACTIVATE`。渲染由 `CompositionTarget.Rendering` 驱动并以 `FrameRateLimiter` 限制到最多 60 Hz。

Dashboard 和 Lap 是两个独立 `IHudContribution`。两者同时存在时共享同一弧心；只有 Lap 时 Overlay 改用紧凑独立面板。位置、缩放、不透明度、显示器标识、点击穿透、锁定、减少动态、加速度跟随开关/强度及全部 HUD 等待与淡入淡出时间保存在 `OverlayLayout`。`DashboardHudDynamics` 负责可测试的静止判定、透明度过渡与加速度弹簧状态；`LapHudDynamics` 对持续无匹配证据计时，淡出后按 `CompetitionSessionId` 锁定到比赛结束。菜单、暂停和回转沿用同一会话 ID，不会解除锁定。窗口仍保持透明、置顶、穿透，业务模块不直接操纵窗口。

地产 HUD 的主题由 `HudSurface.Themes.cs` 中的编译期注册表管理，`EstateRaceHudThemes` 向设置页提供同一份主题目录。各 `EstateRaceHudWidgetPlacement.ThemeId` 保存稳定 ID，当前仅注册 `classic`。新增主题应注册新 ID 和渲染器，复用已有组件状态、可见性、赛事阶段隔离和动画控制；不加载外部主题 DLL，也不复制赛事规则。设置页按目录生成逐组件选择器；仅有经典且无未知主题配置时收起主题列。

主题只改变绘图，位置、缩放和不透明度继续由统一组件变换处理。`EstateRaceDrawingLayers` 区分底板与内容，底板只合成一次不透明度；限速牌等保证文字对比度的语义底色属于内容层。`HudTypography` 提供明确列宽、单行省略和共享基线。原始计时与排名不读取显示动画状态。`HudSurface.TimingPanels.cs` 保留 PIT WINDOW、PIT STOP 和处罚指示器的独立绘图样式，未注册为可选主题；复用方式见 [保留的 HUD 面板样式](docs/HUD_TIMING_PANEL_STYLES.md)。

布局仍作为原有 `overlay.layout` JSON 保存，不修改数据库结构。缺少 `ThemeId` 的旧布局默认使用经典主题；已撤下的 `broadcast` ID（不区分大小写）归一化为 `classic`，其他位置、缩放、不透明度和显示设置保持不变。其他未知 ID 以经典绘制并保留至再次保存。不要将已撤下的 ID 分配给新设计。早于主题功能的旧版可以读取布局，但再次保存时可能丢弃它不认识的主题字段。

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

手动弯道分析使用 `ManualCornerAnalyzer`，入口位于圈速对比、赛后复盘和回放工作台。手动区间按本地路线修订、方向及分段版本保存在 AppSettings；新圈在记录时携带 `LapTrackRevision`，身份包含模板几何、修订时间、方向、计时类型及游戏版本信息。几何按毫米、切向按 1e-6 精度规范化，避免 SQLite 数值往返改变身份。该标识属于 LazyForza，不是 FH6 官方赛事版本。旧圈缺少标识或完整车辆条件时不参与弯道比较，不补猜历史版本。

分析只选择另一条真实有效 `LapRecord`；要求相同路线修订、方向、分段版本，以及兼容的车型、Class/PI、驱动、气缸、最大转速和已有配置签名。尚未观察到的调校、天气等条件不被视为已确认相同。两条圈的区间边界必须被采样覆盖，每侧至少 8 点，相邻不超过 25 m / 0.75 s，距离与时间必须严格递增；拒绝异常、乱序与缺口，不外推。按共同距离轴线性插值耗时和速度，原始输入上以 25% 制动、70% 油门阈值及至少 0.15 s / 3 m 的持续证据定位操作；恢复油门需位于最低速度之后且制动不超过 10%。未捕获阈值跨越时显示缺失而非断言没有操作。最多生成三条中文差异说明，只描述观测关系，不估算潜在提速收益。

当前 `LazyForzaStore.CurrentSchemaVersion` 为 13。基础表包含 AppSettings、ModuleSettings、Sessions、VehicleProfiles、EngineCurveBins、GearModels、ShiftTargets、TrackTemplates、TrackPoints、SectorDefinitions、Laps、LapSegments 与 LapSamples；后续迁移加入动态遥测、地产赛道定义/检查点/维修区/计时类型、地产策略样本和圈速玩家代号。圈速显式保存官方 CarClass/PI；升级前缺失的 CarClass 会先从旧车辆指纹恢复 PI，再按 D 100–400、C 401–500、B 501–600、A 601–700、S1 701–800、S2 801–900、R 901–998、X 999 的规范区间补齐。

Schema 13 以事务追加可空的 `Laps.TrackRevision` 和 `VehicleSnapshot`，新圈同时保存本地路线修订与完整车辆指纹。原有简化指纹继续保留；旧行的新字段保持空，不能从当前配置反填历史条件。便携备份同时保存新增列，并能为旧备份补空列；数据库快照仍按现有迁移备份流程处理。`.lfzlap` 与单圈 `.lfztelemetry` 的圈对象新增可选 `TrackRevision`，容器版本不变，新读取器接受旧文件中的缺失字段，旧读取器忽略新增字段；旧应用会拒绝更高版本的数据库，降级应使用升级前备份。赛事网络协议不变。

圈速列表使用两次批量查询读取圈元数据与所有分段，不再逐圈查询；只有图表确认显示的圈才批量读取 `LapSamples`。兼容用的 `LoadLaps` 也改为固定次数的批量查询。写入使用事务，开启外键与 WAL；高频原始包不写 SQLite，而是顺序写入版本化 `.lfztelemetry`。

迁移必须按版本顺序追加并能直接升级旧用户库。备份、导入导出和兼容读取属于结构变更的同一影响面；详细修改检查见 [`AGENTS.md`](AGENTS.md)。

## 地产赛事跨仓库边界

维修计时几何由 Analysis 的 `EstatePitTimingTracker` 管理：通道计时门与换胎区在同一终点平面上的有限交线共同构成合法过线范围；一次维修通行的过线消费状态独立于每圈重置和维修停留计时。`EstateCircuitModule` 只在接受过线后消费它，网络层继续使用既有可靠圈事件。换胎停留、处罚执行和策略预测不控制是否生成圈事件。

服务端 `RaceProgressTimeline` 只处理有效归一化进度、遥测顺序和共同距离的通过时刻，不读取进站或换胎标记。合法维修通路上的有效进度与主路线使用同一时间线；暂停期间不采样，恢复后保留上一位置作为跨线顺序依据。圈完成事件提供距离下界，接收时刻不进入通过时刻历史；权威计圈仍只接受可靠事件。时间线有界、仅存于内存，赛事切换或进程重建后重新建立，恢复身份、成绩、处罚和去重记录仍由原有持久化负责。

语音工程师只消费现有 `EstateRaceHudState` 权威快照及进站预测，不重新计算成绩或处罚。`RaceEngineerObserver` 提取重要变化，`RaceEngineer` 负责有界队列（16 条）、阶段隔离、最近 4096 个事件去重、优先级、过期和分类冷却，仅依赖 `ISpeechOutput`。消息到期也会取消已开始的合成／播放。静音、关闭、退出和赛事切换取消当前输出并清空队列；语音故障仅停用输出。开关、静音与音量沿用 AppSettings 保存，无数据库迁移或网络协议变化。

`LazyForza.Speech` 的 `RadioSpeechOutput` 通过 `ISpeechSynthesisProvider` 获得 PCM，由 `ISpeechAudioPlayer` 按接通音、语音、断开音顺序播放。合成默认超时 10 秒，内存缓存上限为 32 条／4 MiB，最多允许两次合成重叠以容纳取消中的请求；迟到结果不能播放。App 的 `LocalRaceSpeech` 装配 Windows SAPI 内存合成（独立 STA、24 kHz 单声道）与播放器独占并复用的 waveOut 句柄；取消只停止本次音频，退出时关闭设备。原创起止音由 `RadioCues` 合成。用户可在同一配置浮窗中选择 ElevenLabs、Azure Speech、腾讯云、阿里云 NLS、千问 AI 平台或 MiniMax：适配器使用官方 HTTPS 地址，共用有界 HTTP 传输、错误分类和可取消请求，输出统一为 16 或 24 kHz 单声道 PCM16。阿里云 NLS 与千问模型服务分别配置凭据和音色。凭据由 App 分别通过当前用户 DPAPI 加密持久保存，服务切换重建输出；可选 Windows 故障回退带请求冷却且不缓存回退声音。接口、资源所有权与适配要求见 [语音输出说明](docs/RACE_ENGINEER_SPEECH.md)。

起止音与语音之间分别保留 180／220 ms 可取消停顿。自定义音频在 App 中通过 NAudio／Windows Media Foundation 解码为受限 PCM 副本，作为版本 1 的独立 AppSettings 值原子保存；不存在外部文件路径依赖。每次输出固定 `RadioTransmission` 快照，设置变化只作用于后续播报。手动试听通过 `RaceEngineer.PreviewAsync` 使用同一输出并服从静音，支持离线及自动播报关闭场景；试听不进入赛事去重或冷却，真实赛事消息优先取消试听。

协议 v2 的三端模型由 RaceServer 仓库的 `protocol/race-protocol.schema.json` 统一生成。客户端使用已提交的 `EstateRaceProtocol.g.cs`，独立构建无需服务端仓库或 Node.js；生成文件不手工修改。

客户端上传本机轨迹和可靠圈/维修事件；服务端权威管理圈数、排名、阶段、旗语、处罚、调查和结果。连续遥测可以 latest-wins，圈完成与维修完成事件必须通过事件 ID、确认和去重可靠传递。完整同步文件和验证命令见客户端与 RaceServer 两边的 `AGENTS.md`。

圈完成事件由 `LapEventSendQueue` 保留至服务端接受或拒绝回执，在线产生的事件不受 `DisconnectedLapRecoveryEnabled` 控制，重连重试保留原始事件 ID 和在线属性；离线产生的圈仍受补圈开关及服务端恢复窗口限制。重试间隔至少 2 秒，由遥测处理和心跳驱动，无遥测时也继续等待确认。重复或未知回执不改变后续圈的发送状态。待确认队列上限为 12 条，满时保留已有事件、拒绝新事件并持续提示本阶段存在未上传成绩，同时记录日志。

待发圈按赛道、有效阶段、练习／排位节次隔离，并将快照的可选 `stageId` 固定在原始上传事件中。稳定阶段 ID 使服务端重启后的计时调整不会清空待确认队列；旧服务端未提供该字段时仍以开始／截止时间推断阶段。新阶段、新车手或主动重新连接清理旧队列，同阶段自动重连、暂停恢复及结束后的回执等待保留队列。发送前再次检查事件是否仍待确认。

协议仍为 v2。Schema 新增的 `stageId` 与回执 `validationStatus` 默认为空，旧端忽略新属性，新客户端仍可读取旧回执。新服务端拒绝明确错误的阶段、圈序、分段数量和时间，区分待审核与证据不足；待审核圈沿用原有计圈规则并创建人工调查，不自动判罚。充分遥测才按原始客户端时间窗核对进度，进站或断续样本不会直接判作弊。旧客户端未提供阶段 ID 时无法保证跨阶段消息归属；旧服务端也不提供新增校验保证。具体规则见 RaceServer README。

RaceServer 原生与 Cloudflare 入口可配置登录失败窗口、未认证 WebSocket 名额和每连接消息／字节预算。HTTP 超限返回 429 与 `Retry-After`，WebSocket 返回 `rateLimited` 错误后以 1013 关闭；登录拒绝和错误载荷的可选 `retryAfterSeconds` 由 Schema 生成。旧载荷缺少该字段时仍可读取，文字提示也包含重试秒数；当前客户端不保证自动按新字段退避。限速错误不代表圈事件已被接受，事件仍由原有回执机制处理。代理信任、共享出口配置及部署限制见 RaceServer README。
