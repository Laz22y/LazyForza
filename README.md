# LazyForza

> 本项目使用 OpenAI GPT-5.6 Sol 开发。

LazyForza 是面向 Forza Horizon 6 的 Windows 遥测与圈速辅助工具。它只接收 FH6 官方单向 UDP Data Out，不读取游戏内存、不注入 DLL、不修改游戏进程。

当前 MVP 包含两个可独立启停的业务模块：

- **Dashboard**：透明矢量 HUD、动态 RPM/动力/四轮/踏板/等级显示，以及按车辆配置学习的逐挡换挡目标；
- **Lap Analysis**：环道/点到点路线学习、已保存模板匹配、确定性分段、弧形分段 HUD、逐圈 SQLite 存储和程序内圈速分析。

默认启动 **Live UDP**，监听 `127.0.0.1:2299`。只有显式传入 `--demo`、`--replay` 或 QA 参数时才使用模拟/回放；主界面和 HUD 会明确标注来源，模拟结果不会冒充 Live 数据。

## 环境与技术基线

本仓库当前用本机可用的 **.NET SDK 9.0.316** 构建，`global.json` 已固定版本。技术决策的长期目标仍是 C# + .NET 10 LTS；本机没有 .NET 10，且缓存的 Windows App SDK 只有 1.5，因此 MVP 采用 WPF 主壳和 WPF/Win32 layered overlay。完整证据见 [ADR-0001](docs/adr/ADR-0001-technology-stack.md)。

要求：

- Windows 10/11 x64；
- .NET SDK 9.0.316；
- 可访问 NuGet.org（只用于固定版本的 MSTest 测试依赖）；
- Windows 自带 `winsqlite3.dll`。

## Restore、Build、Test、Run

在仓库根目录执行：

```powershell
dotnet restore LazyForza.sln --configfile NuGet.Config
dotnet build LazyForza.sln --no-restore -c Debug
dotnet test LazyForza.sln --no-build --no-restore -c Debug
dotnet run --project src/LazyForza.App/LazyForza.App.csproj --no-build --no-restore -c Debug
```

默认最后一条命令直接启动 Live 模式。确定性模拟器用于无 FH6 开发和自动验证：

```powershell
dotnet run --project src/LazyForza.App/LazyForza.App.csproj -c Debug -- --demo
```

回放已录制的原始会话：

```powershell
dotnet run --project src/LazyForza.App/LazyForza.App.csproj -c Debug -- --replay "C:\path\session.lfztelemetry"
```

发布 framework-dependent Windows x64 包：

```powershell
dotnet publish src/LazyForza.App/LazyForza.App.csproj -c Release -r win-x64 --self-contained false -o artifacts/publish/win-x64
```

## FH6 Data Out 配置

依据 [Forza Horizon 6 官方 Data Out 文档](https://support.forza.net/hc/en-us/articles/51744149102611-Forza-Horizon-6-Data-Out-Documentation)：

1. 在 FH6 中打开 `SETTINGS > HUD AND GAMEPLAY`；
2. 启用 Data Out；
3. 地址填写 `127.0.0.1`；
4. 端口填写 `2299`；
5. 直接启动 LazyForza；默认就是 Live 模式。

FH6 要求避开 `5200..5300`，所以 LazyForza 默认选择 2299。发包率等于游戏帧率，不固定为 60 Hz。当前真实会话确认菜单会继续发送 `IsRaceOn=0` 的全零帧；其他暂停、回放、倒带和完赛状态也可能停止发包。LazyForza 同时依据驾驶标志和新鲜度隐藏 HUD，不会把菜单零时间戳计为网络重复包。

## 使用流程

### 模块与 HUD

“模块”页的两个开关真实调用 `StartAsync/StopAsync`：取消后台任务、释放遥测订阅并挂载/移除各自 HUD。四种组合都受支持：全关、仅 Dashboard、仅 Lap、两者都开。仅 Lap 时会显示自己的紧凑弧形锚点。

“设置”页可编辑 Live 监听 IP/端口并持久化（下次启动生效），也可锁定/解锁 HUD：锁定时置顶、点击穿透且不抢焦点；解锁时可拖动。缩放、不透明度、显示器标识、点击穿透、减少动态、加速度跟随开关与强度保存为 `OverlayLayout`。加速度跟随使用纵横向加速度驱动带阻尼的惯性位移；新版 50% 约等于原 100% 的位移，新版 100% 提供约两倍原上限的加强效果。Overlay 设置板块右上角的“重置 Overlay”带二次确认，会恢复全部 Overlay 参数并立即应用 HUD，但不会修改监听 IP/端口，也不会删除赛道、圈速或车辆学习数据。当前默认值取自已接受的 Live 配置：60% 缩放、100% 不透明度、50% 动态强度、2 秒静止等待、0.8 秒显隐渐变和 1 秒完成圈保留。Dashboard 静止等待/淡入淡出、完成圈分段保留、无匹配确认/淡出以及 Live 断流隐藏时间均可在同页配置。

### 换挡学习

保持 90% 以上油门、稳定挡位、足够车速和低轮胎滑移，通常连续完成 2–3 次跨至少两个挡位的有效加速、约 30–90 秒会生成第一批稳定目标；打滑、碰撞、低油门或换挡过渡时间不计入。学习页显示有效样本、RPM 桶、挡位覆盖与剩余时间估算。学习器会过滤离合、低速、位置跳变和异常时间间隔；按 RPM 桶保存鲁棒中位数曲线，拟合各挡 `RPM / Speed`，再逐挡比较换挡前后同速驱动力。数据不足时不会把峰值功率或红线伪造成最佳点。

`CarOrdinal` 是车型编号，不是调校编号；FH6 Data Out 也不提供独立的调校 ID。LazyForza 因此使用车型、等级/PI、驱动形式、气缸数、最大转速、实测挡位比和动力曲线摘要组成车辆配置指纹。基础字段变化会立即切换配置；同车型、同 PI 下若挡位比或动力曲线持续显著变化，也会保留旧档案并开始新的调校档案。仅改变悬架、胎压等官方 UDP 无法观察且不影响上述特征的调校，无法保证自动区分。

“车辆与换挡”页会列出已存配置，展示学习状态、置信度、转速区间、挡位模型、换挡目标和更新时间。每份配置都可重命名、经确认后删除，并可独立关闭推荐挡位；关闭只影响该配置的升降挡箭头，不影响仪表盘其他数据或继续学习。

### 路线与圈速

Lap HUD 只在检测到正式比赛时显示，漫游、菜单和暂停时隐藏；顶行仅显示当前匹配的已保存赛事名称和当前圈时间。学习新路线时，程序先保留临时轨迹；如果 `CurrentLap` 在真实起点清零而 `CurrentRaceTime` 继续前进，会丢弃发车位轨迹并从起点重新采集。环道记录到再次过线；点到点路线优先在官方 `LastLap` 完赛成绩出现时结束。实测赛事若没有更新 `LastLap`，程序只会在轨迹从比赛开局连续采集、没有回转、开放路线长度至少 300 米、起终点明显分离且随后确认回到漫游时采用受约束的退出兜底。程序在比赛中途启动或回转后的半条轨迹不会触发该兜底。点到点终点不会强制闭合回起点。两种布局都按约 5 m 重采样并生成 4–16 个版本化分段。学习的是玩家参考路线/走廊，不是完整赛道边界，也不是 FH6 Track ID。再次进入已保存的点到点路线时，分析器可在起点附近预备采样，即使游戏不产生第二圈或圈数重置，也会在路线终点与官方完赛信号一致时保存本次成绩。若比赛开始后车辆持续不接近所选模板且从未形成可信候选，Lap HUD 会在可配置确认时间后淡出，并锁定隐藏至本场比赛结束；菜单、暂停和回转不会重置这个比赛会话。

冲线检测会组合 `CurrentLap` 清零、`LapNumber`、比赛总计时和起终点位置，并对相邻帧的重复信号去重；这兼容 FH6 先清零圈计时、下一帧才增加圈数的情况。保存总圈速优先使用官方 `LastLap`，不会用冲线前最后一个 UDP 包的时间提前截断一圈。

Lap HUD 的颜色按当前车辆性能等级独立计算：灰色表示未跑或无效，黄色表示比本场同等级最快分段慢，绿色表示当前比赛同等级最快，紫色表示程序为该赛道保留的同性能等级历史最快。紫色不是在线世界纪录，也不是对手遥测。

Dashboard 转速弧下方会在每次通过分段终点后显示累计 Delta：当前圈从起点到该分段终点的时间，减去同赛道、同性能等级真实历史最快完整圈到同一分段终点的累计时间。负值为绿色、正值为黄色；它不是单独分段耗时，也不会把不同圈的最优分段拼成一条不存在的“理论最快圈”。

每圈会保存车辆性能等级与 PI。每条赛道最多保留 50 圈完整分段/采样（不是每等级 50 圈）；超过上限时先保护每个性能等级各自的历史最快，再自动删除最旧的其他记录。Lap Analysis 提供 D/C/B/A/S1/S2/R/X 多选筛选，全部取消时不显示任何保存圈；圈速表会显示每圈等级/PI。批量删除的二次确认可选择仅删除当前筛选等级，以及是否连同各等级历史最快一起删除，赛道模板始终保留。用户也可以在确认后手动删除任意记录、选择 1–4 圈联动比较圈速表/速度曲线/路线图，并区分当前或最近一次比赛与更早的历史比赛；“当前比赛”页在比赛进行中显示实时内容，比赛结束后保留最近一场已完成圈 5 分钟。

SchemaVersion 7 会保存车辆配置名称和每份配置的推荐挡位开关；旧车辆档案默认保持启用。SchemaVersion 5 的路线布局迁移仍会将旧模板安全迁移为环道；此前的性能等级迁移会先从旧 `VehicleFingerprint` 恢复 PI，再按设计规范中的 PI 区间推断 D/C/B/A/S1/S2/R/X。新 Live 圈仍以官方 UDP Data Out 的 `CarClass` 为准，仅在等级缺失或越界时用 PI 兜底，因此圈速记录不会再出现“?”等级。

### 录制与诊断

诊断页显示包率、有效/无效包、估计丢包、重复/乱序/回绕、最后包时间、模块状态和路径；可开始/停止版本化 `.lfztelemetry` 原始包录制。实时 UI 使用 bounded latest-wins 通道，录制文件保存到应用数据目录。

## 数据目录

默认：`%LOCALAPPDATA%\LazyForza`

同一台开发机需要把发行版与开发版完全隔离时，可给发行版传入独立目录：

```powershell
LazyForza.App.exe --data-dir "%LOCALAPPDATA%\LazyForza-Release"
```

也可设置环境变量 `LAZYFORZA_DATA_DIR`。命令行参数优先于环境变量。发行包附带的
`Start-Isolated.cmd` 已按上述方式配置；它不会读取或修改开发版默认目录中的数据库、
设置、日志或录制文件。

- `lazyforza.db`：设置、模块状态、会话、车辆学习、路线、分段、圈与降采样样本；
- `lazyforza-sandbox.db`：仅供显式 Demo/Replay/QA 使用的隔离数据，绝不参与默认 Live 路线恢复；
- `Recordings\*.lfztelemetry`：版本化原始 324 字节包；
- `Logs\lazyforza.log`：2 MiB 滚动结构化事件日志，不逐帧刷写。

SQLite 启用外键、WAL、事务、SchemaVersion 和比较/距离索引。测试使用独立临时数据库。

## 已知限制

- 点到点路线的确定性学习、持久化、再次套用与终点保存已实现；FH6 不提供布局/赛事 ID，且已观察到部分点到点赛事完赛时不更新 `LastLap`，因此首次学习包含严格约束的比赛退出兜底，仍需按验证清单覆盖更多真实赛事；EventLab 仍为受限/实验性；
- FH6 不提供赛道 ID、官方检查点、对手遥测、轮胎磨损或调校唯一 ID；车辆调校只能按可观察到的 PI、发动机和传动特征区分；
- 当前真实会话确认 Gear raw `0=R`、`1=1 挡`，尚未观察到独立空挡码；轮胎温度单位、官方字节序与坐标跨版本稳定性仍待进一步真实 FH6 验证，温度 UI 不写 C/F；
- 30 分钟发布版循环 Replay 桌面 soak 已完成；真实 FH6 Live UDP、多显示器混合 DPI和独占全屏焦点行为仍待验证，见 [VALIDATION_WITH_FH6.md](VALIDATION_WITH_FH6.md)；
- 主 UI 为 WPF Fluent 风格，不是 WinUI 3；迁移边界见 [ARCHITECTURE.md](ARCHITECTURE.md)。

## QA 资产

自动离屏渲染的 Demo HUD 位于 `docs/qa/`，包含 1280×720、1920×1080、2560×1440；四角 alpha 已检查为 0。图片只用于视觉回归，运行时仍是矢量绘制，不会把 PNG 当仪表盘。

30 分钟资源采样与退出结果见 [Replay soak 报告](docs/qa/SOAK-2026-07-22.md)。

测试依赖版本和许可证见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。

## 许可证

LazyForza 使用 [MIT License](LICENSE) 开源。Forza、Forza Horizon、Xbox 及相关商标属于其各自权利人；LazyForza 是非官方社区项目。
