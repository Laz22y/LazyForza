<p align="center">
  <img src="docs/assets/LazyForzaReadmeBanner.png" alt="LazyForza" width="720">
</p>

<p align="center">Forza Horizon 6 的本地遥测、驾驶分析与地产赛事工具</p>

<p align="center"><a href="#简体中文">简体中文</a> · <a href="#english">English</a></p>

## 简体中文

<p align="center">
  <a href="https://laz22y.github.io/LazyForza/">官网</a> ·
  <a href="https://laz22y.github.io/LazyForza/docs/">完整文档</a> ·
  <a href="https://github.com/Laz22y/LazyForza/releases/latest">下载</a> ·
  <a href="https://github.com/Laz22y/LazyForza.RaceServer">RaceServer</a>
</p>

LazyForza 通过 FH6 官方 UDP Data Out 获取数据，不读取游戏内存、不注入 DLL、不修改游戏进程。设置、圈速、车辆学习和录制默认保存在本机。

1.5.2 修复了在手动 + 离合设置下，进入车库、嘉年华等场景后仪表盘不会按预期隐藏的问题。

## 功能

| 模块 | 能力 |
| --- | --- |
| 实时 HUD | 速度、挡位、转速、踏板、方向、轮胎、动力和换挡提示；部件可独立移动、缩放和调节透明度 |
| 圈速分析 | 分段、实时 Delta、速度与驾驶输入、走线和距离游标联动 |
| 车辆学习 | 按车型、性能等级和可观测调校特征学习换挡目标，离线识别车辆名称 |
| 录制与回放 | 可选自动录制、容量保护、`.lfztelemetry` 导入导出和联动回放 |
| 地产赛事 | 可暂存和局部修订的环道录入、维修区轨迹校验、路线收益切弯证据、赛事 HUD 与弱网提醒，并可连接独立 RaceServer |
| 数据与更新 | 本地数据库、备份、诊断；GitCode/GitHub 更新回退及双层完整性校验 |

实验性漂移 HUD 默认关闭。它只根据本车 UDP 推导侧滑和控车趋势，不复刻游戏评分，也不代替玩家判断。

赛道识别支持后排发车位置；歌利亚与传奇岛径道赛等共用路段会等待分流证据再确认。比赛计时刚重置，且车辆低速出现在远离上一场的另一个已知起跑区时，即使上一场没有圈速记录，也可建立新的比赛会话。普通菜单暂停和倒带仍保留当前会话。

圈速对比、赛后复盘和回放工作台支持手动弯道分析：输入区间的起终点距离，或点击曲线标记，再选择另一条已记录的有效参考圈。按赛道距离比较区间耗时、制动起点、最低速度及恢复油门位置；最多显示三条中文差异说明，点击即可定位曲线。区间标记保存在本机，每个赛道版本最多 32 个；跨终点弯请拆成两段。

弯道比较要求路线修订、方向、分段版本及车辆条件兼容。旧圈缺少路线修订或完整车辆信息时仍可查看和回放，但不生成弯道结论。区间采样缺失、间隔过大或输入事件证据不足时会明确提示；差异说明不把相关性当成提速原因，也不代替对调校、天气等条件的核对。

地产赛事页提供默认关闭的「本地语音比赛工程师」，播报旗语变化、新处罚、个人最快圈和关键进站预测。可调整音量或立即静音；静音会停止当前语音及提示音并清空待播消息，恢复后不补播。红旗优先打断普通播报，重复事件与频繁预测会被去重和冷却。进站建议保留「预计」「可能」等不确定性表述。

语音使用 Windows 已安装的对应语言本地 SAPI 语音，无需联网；每次完整播报前后有本地合成的短促无线电双音。缺少语音或音频不可用时只停用播报，比赛照常进行。安装对应语音后，可关闭再启用工程师以重试。

## 快速开始

1. 从 [Releases](https://github.com/Laz22y/LazyForza/releases/latest) 下载 Windows 安装包，或下载 `win-x64.zip` 便携版并完整解压；
2. 启动 LazyForza，按首次启动指引选择语言、玩家代号、数据目录和关闭方式；
3. 在指引中按提示开启 FH6 Data Out。收到有效遥测后会自动进入主窗口，也可暂时跳过连接。

安装版默认安装到 `C:\Program Files\LazyForza`，创建开始菜单入口；桌面快捷方式和 `.lfztelemetry`、`.lfzlap`、`.lfzestate` 文件关联可在安装时选择。便携版不写入这些系统项。每个便携版目录独立保存初始化状态；安装版卸载时保留数据库，并在重新安装后再次显示初始化指引。正式安装版默认启动检查更新，正式便携版默认关闭，两者都可在设置中修改。预览版使用独立初始化状态和 GitCode/GitHub 预发布更新通道，每次启动强制检查并自动安装更高预览版；正式版不参与该通道，预览包不会自动转为正式版。发行说明同时提供中文和英文，程序按当前界面语言显示对应内容。所有包都包含 .NET 运行时，且可在首次启动或设置页选择数据目录。需要通过命令行固定目录时可使用：

设置页提供默认蓝、暗夜紫、清新绿、鲜艳红、纯粹白和低调灰六种 UI 强调色；切换只影响界面高亮与选中状态，不改变 HUD、图表语义色或其他界面样式。

```powershell
LazyForza.App.exe --data-dir "D:\LazyForza_Data"
```

完整的安装、功能、数据、故障排查和开发说明见 [LazyForza 文档](https://laz22y.github.io/LazyForza/docs/)。

## RaceServer

[LazyForza.RaceServer](https://github.com/Laz22y/LazyForza.RaceServer) 是独立的地产赛事服务端，提供：

- 1–12 名车手和额外 OB 席位；
- 练习、多节排位、正赛、稳定秒差、旗语、处罚、碰撞调查和可选断线计圈恢复；
- 维修区、车队、赛道文件托管与阶段赛果归档；
- 客户端可收藏常用服务器并在进入房间前测试可达性和协议兼容性；
- Web 总控支持超管、管理员和裁判多账号分权；
- 发车前检查仅作警告，总控可确认后强制发车；
- 可复用赛事规则模板、可迁移赛事项目包和独立令牌保护的公开实时计时页；
- Windows、Linux、macOS 自托管包和 Cloudflare Durable Objects 版本。

只使用实时 HUD 和圈速分析时不需要部署服务端。部署前阅读[赛事服务端指引](https://laz22y.github.io/LazyForza/docs/#race-server)。

## 本地构建

需要 Windows 10/11 x64、.NET SDK 9 和 PowerShell 7：

```powershell
dotnet restore LazyForza.sln --configfile NuGet.Config
dotnet build LazyForza.sln --no-restore -c Debug
dotnet test LazyForza.sln --no-build --no-restore -c Debug
dotnet run --project src/LazyForza.App/LazyForza.App.csproj --no-build --no-restore -c Debug
```

模拟与回放：

```powershell
dotnet run --project src/LazyForza.App/LazyForza.App.csproj -- --demo
dotnet run --project src/LazyForza.App/LazyForza.App.csproj -- --replay "C:\path\session.lfztelemetry"
```

## 开发资料

- [Coding Agent 开发入口](AGENTS.md)
- [完整用户与开发者文档](docs/LazyForza-Documentation.md)
- [架构](ARCHITECTURE.md)
- [FH6 遥测开发参考](FH6_TELEMETRY_DEVELOPMENT_GUIDE.md)

FH6 UDP 不提供官方赛事 ID、对手遥测或调校 ID。推导数据会与官方字段明确区分。

## 致谢

感谢 [HDR 维护并提供 FH6 Car Ordinals 车辆标识符文档](https://gist.github.com/HDR/0659d1717bc61504bf83750628963f4f)。LazyForza 使用其内置快照完成离线车辆名称映射。

## License

[MIT](LICENSE)。LazyForza 是非官方社区项目，与 Microsoft、Xbox 或 Playground Games 无隶属关系；相关商标属于其各自权利人。

## English

LazyForza is a local telemetry, driving-analysis and estate-racing tool for Forza Horizon 6. It uses only official FH6 UDP Data Out: no game-memory access, DLL injection or game-process modification. Settings, laps, vehicle learning and recordings stay on your PC by default.

Version 1.5.2 fixes the dashboard not hiding as expected after entering the garage, Horizon Festival, or similar scenes while using Manual with Clutch.

### Features

| Area | What it provides |
| --- | --- |
| Live HUD | Speed, gear, RPM, pedals, steering, tires, power and shift guidance with independent layout, scale and opacity |
| Lap analysis | Sectors, live delta, speed and input comparison, racing lines and a linked distance cursor |
| Vehicle learning | Shift targets by vehicle, performance class and observable tune traits, with offline vehicle-name mapping |
| Recording and replay | Optional automatic recording, storage limits, `.lfztelemetry` exchange and linked replay |
| Estate racing | Pausable circuit capture, component-level revision, pit-route checks, shortcut evidence, race HUD and network warnings |
| Data and updates | Local database, backup and diagnostics, plus verified GitCode/GitHub update fallback |

The experimental drift HUD is disabled by default. It estimates slip and control trends from local UDP data; it does not reproduce the game's scoring system.

Track identification accommodates rear grid positions and waits for route divergence when events such as Goliath and Legend Island share an opening corridor. A newly reset race clock and a low vehicle speed at a different known grid far from the previous event can open a new competition session even without a saved result. Ordinary menu pauses and rewinds preserve the current session.

Lap comparison, post-race review and the replay workbench support manually marked corner intervals. Enter start/end distances or mark them on a curve, then choose another recorded valid reference lap. Compare interval time, brake onset, minimum speed and throttle recovery position on the same distance axis. Up to three Chinese observations link to the curves. Up to 32 intervals are saved locally per track revision; split intervals that cross the finish line.

Corner comparisons require compatible route revisions, directions, sector versions and vehicle conditions. Older laps remain viewable and replayable, but missing revision or vehicle evidence prevents corner conclusions. Sparse or invalid samples and uncertain input transitions are reported explicitly. Observations describe differences, without asserting causes or promised time gains.

The Estate racing page includes an optional local race engineer, disabled by default. It announces flag changes, new penalties, personal bests and important pit predictions, with priority, deduplication and cooldowns. Red flags interrupt routine speech. Volume and immediate mute controls apply to speech and the locally synthesized radio cues before and after each complete transmission. Mute clears pending messages; unmuting does not replay them. Pit advice explicitly remains an estimate.

Speech uses an installed Windows SAPI voice for the selected language, without a network service. Missing voices or audio failures disable speech without affecting the race. After installing a compatible voice, disable and enable the engineer to retry.

### Quick start

1. Download the Windows installer from [Releases](https://github.com/Laz22y/LazyForza/releases/latest), or download and fully extract the `win-x64.zip` portable build.
2. Start LazyForza and choose your language, player alias, data directory and close behavior in the first-run guide.
3. Follow the guide to enable FH6 Data Out. The main window opens after valid telemetry arrives, or you can skip the connection step.

The installer defaults to `C:\Program Files\LazyForza`, creates a Start Menu entry, and can optionally create a desktop shortcut and associate `.lfztelemetry`, `.lfzlap` and `.lfzestate` files. The portable build does not write those system entries. Stable installed builds check for updates by default; stable portable builds do not, and both settings can be changed later. Preview builds use separate initialization state and GitCode/GitHub prerelease channels; they check on every startup and install only newer previews automatically. Stable releases never enter the preview channel, so a preview build does not automatically become stable. Every package includes the .NET runtime.

Settings provides six UI accent colors: Default Blue, Midnight Purple, Fresh Green, Vivid Red, Pure White and Subtle Gray. The selection changes interface highlights and selected states without recoloring HUDs, chart semantics or other interface styling.

Use an explicit data directory from the command line when needed:

```powershell
LazyForza.App.exe --data-dir "D:\LazyForza_Data"
```

See the [complete documentation](https://laz22y.github.io/LazyForza/docs/) for setup, features, data storage, troubleshooting and development.

### RaceServer

[LazyForza.RaceServer](https://github.com/Laz22y/LazyForza.RaceServer) is the independent estate-racing server. It provides 1–12 driver slots plus observers, practice, multi-session qualifying, races, stable gaps, flags, penalties, collision investigations, optional disconnected-lap recovery, pit lanes, teams, hosted track files and archived session results. The client can save frequently used servers and test reachability and protocol compatibility before joining. Race Control supports separate super-admin, administrator and steward accounts, warning-only pre-race checks, reusable rule templates and event packages, and token-protected public live timing. Native Windows/Linux/macOS packages and a Cloudflare Durable Objects implementation are available.

RaceServer is not required for the live HUD or lap analysis. Read the [deployment and connection guide](https://laz22y.github.io/LazyForza/docs/#race-server) before hosting a race.

### Build locally

Requires Windows 10/11 x64, .NET SDK 9 and PowerShell 7:

```powershell
dotnet restore LazyForza.sln --configfile NuGet.Config
dotnet build LazyForza.sln --no-restore -c Debug
dotnet test LazyForza.sln --no-build --no-restore -c Debug
dotnet run --project src/LazyForza.App/LazyForza.App.csproj --no-build --no-restore -c Debug
```

Development references:

- [Coding Agent entry point](AGENTS.md)
- [User and developer documentation](docs/LazyForza-Documentation.md)
- [Architecture](ARCHITECTURE.md)
- [FH6 telemetry development reference](FH6_TELEMETRY_DEVELOPMENT_GUIDE.md)

FH6 UDP does not provide official race IDs, opponent telemetry or tune IDs. Derived data remains clearly separated from official fields.

### Acknowledgements

Thanks to [HDR for maintaining and sharing the FH6 Car Ordinals documentation](https://gist.github.com/HDR/0659d1717bc61504bf83750628963f4f). LazyForza uses a bundled snapshot for offline vehicle-name mapping.

### License

[MIT](LICENSE). LazyForza is an unofficial community project not affiliated with Microsoft, Xbox or Playground Games. All related trademarks belong to their respective owners.
