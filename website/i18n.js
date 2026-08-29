(function initializeWebsiteLocalization(globalObject) {
  "use strict";

  const STORAGE_KEY = "lazyforza.website.language";
  const ENGLISH = {
    "LazyForza：基于 Forza Horizon 6 官方 UDP 的实时 HUD、圈速分析、遥测回放与自托管赛事工具。": "LazyForza turns official Forza Horizon 6 UDP telemetry into live HUDs, lap analysis, replay and self-hosted racing tools.",
    "LazyForza · 读懂每一圈": "LazyForza · Understand every lap",
    "实时 HUD、圈速与走线分析、遥测回放和自托管地产赛事。只使用 FH6 官方 UDP。": "Live HUDs, lap and racing-line analysis, telemetry replay and self-hosted estate racing, powered only by official FH6 UDP data.",
    "LazyForza · FH6 遥测、分析与赛事工具": "LazyForza · FH6 telemetry, analysis and racing",
    "跳到主要内容": "Skip to main content",
    "主导航": "Main navigation",
    "LazyForza 首页": "LazyForza home",
    "页面导航": "Page navigation",
    "功能": "Features",
    "文档": "Docs",
    "语言": "Language",
    "选择语言": "Choose language",
    "读懂每一圈。": "Understand every lap.",
    "LazyForza 将 FH6 官方 Data Out 转成实时 HUD、圈速与走线分析、遥测回放和自托管赛事能力。个人数据默认只保存在本机。": "LazyForza turns official FH6 Data Out into live HUDs, lap and racing-line analysis, telemetry replay and self-hosted races. Personal data stays on your PC by default.",
    "下载 LazyForza 客户端": "Download the LazyForza client",
    "下载安装版": "Download installer",
    "GitCode · 中国大陆网络优先": "GitCode · Recommended in mainland China",
    "备用下载安装版": "Installer mirror",
    "GitHub · 全球下载源": "GitHub · Global download",
    "下载便携版 ZIP": "Download portable ZIP",
    "开始使用": "Get started",
    "查看客户端源码": "View client source",
    "Windows 10 / 11 x64 · 安装版与便携版均已包含 .NET 运行时": "Windows 10 / 11 x64 · Installer and portable builds include the .NET runtime",
    "LazyForza 数据流概览": "LazyForza data flow overview",
    "不读内存": "No memory access",
    "不注入 DLL": "No DLL injection",
    "本地存储": "Local storage",
    "项目特点": "Project highlights",
    "官方 UDP": "Official UDP",
    "唯一游戏数据入口": "Only game-data input",
    "本地优先": "Local first",
    "设置、圈速和录制留在本机": "Settings, laps and recordings stay local",
    "开源": "Open source",
    "MIT License，源码与发行记录公开": "MIT License with public source and releases",
    "可扩展赛事": "Flexible race hosting",
    "客户端与 RaceServer 分离部署": "Client and RaceServer deploy independently",
    "从实时信息，到可复盘的数据。": "From live feedback to useful review.",
    "日常驾驶、练习、单圈分析和地产赛事使用同一套本地数据链路。": "Daily driving, practice, lap analysis and estate races share one local telemetry pipeline.",
    "自由布局的实时 HUD": "Flexible live HUDs",
    "速度、挡位、转速、踏板、轮胎、动力和换挡提示可独立开关、移动、缩放与调节透明度。": "Toggle, move, scale and adjust opacity for speed, gear, RPM, pedals, tires, power and shift guidance.",
    "圈速与走线对比": "Lap and racing-line comparison",
    "按距离联动速度、油门、制动、方向和走线，定位每一段时间差的来源。": "Compare speed, throttle, braking, steering and lines by distance to find where time is gained or lost.",
    "录制与回放": "Recording and replay",
    "可选自动录制保留赛前与赛后片段；回放工作台联动时间轴、驾驶输入和动态遥测。": "Optional automatic recording keeps pre- and post-session context; replay links the timeline, inputs and live telemetry views.",
    "车辆与换挡学习": "Vehicle and shift learning",
    "按车型、性能等级和可观测调校特征保存学习结果，在信息不足时保持保守。": "Stores learned results by vehicle, performance class and observable tune traits, and stays conservative when data is incomplete.",
    "地产环道赛事": "Estate circuit racing",
    "可暂存和分项修订环道录入；提供维修区轨迹保护、路线收益切弯证据、弱网提醒与赛果归档。": "Pause and revise circuit capture by component, with pit-route safeguards, shortcut evidence, network warnings and archived results.",
    "可控的数据与更新": "Controlled data and updates",
    "数据目录、备份、录制容量和自动更新都由用户控制；更新包同时校验外部哈希与内部清单。": "You control data paths, backups, recording limits and updates; packages are checked against both external hashes and internal manifests.",
    "三步开始。": "Start in three steps.",
    "安装或完整解压": "Install or fully extract",
    "推荐安装版；便携版可解压到任意目录运行。": "Use the installer for the simplest setup, or extract the portable build anywhere.",
    "开启 FH6 Data Out": "Enable FH6 Data Out",
    "地址设为": "Set the address to",
    "，端口设为": ", port",
    "端口设为": "and the port to",
    "进入驾驶": "Start driving",
    "实时 HUD 自动连接；圈速、赛道与车辆数据按功能设置保存。": "The live HUD connects automatically; lap, track and vehicle data follow your storage settings.",
    "需要办赛时，再部署服务端。": "Deploy the server when you need to host races.",
    "RaceServer 是独立项目。它提供 1–12 名车手、OB、练习与多节排位、正赛控制、稳定秒差、处罚调查、维修区和阶段赛果归档。": "RaceServer is a separate project for 1–12 drivers, observers, practice, multi-session qualifying, race control, stable gaps, penalties, investigations, pit lanes and archived results.",
    "部署方式": "Deployment options",
    "协议 v2": "Protocol v2",
    "RaceServer 资源": "RaceServer resources",
    "先阅读": "Start here",
    "部署与连接指引": "Deployment and connection guide",
    "正式版本": "Stable release",
    "下载 RaceServer": "Download RaceServer",
    "源码与问题": "Source and issues",
    "服务端仓库": "Server repository",
    "一份文档，覆盖使用与开发。": "One guide for users and developers.",
    "安装、Data Out、功能说明、数据目录、RaceServer、常见问题、架构、构建与协议字段都集中在文档站。": "Installation, Data Out, features, storage, RaceServer, troubleshooting, architecture, builds and protocol fields are documented in one place.",
    "打开 LazyForza 文档": "Open LazyForza documentation",
    "项目边界": "Project boundaries",
    "只使用官方 Data Out": "Official Data Out only",
    "不读取游戏内存，不修改游戏进程，不注入 DLL。": "No game-memory access, process modification or DLL injection.",
    "不虚构缺失数据": "No invented telemetry",
    "官方 UDP 不提供的赛事 ID、车辆尺寸和轮胎位置不会被伪装成官方数据。": "Race IDs, vehicle dimensions and tire positions absent from official UDP data are never presented as official fields.",
    "发行可核验": "Verifiable releases",
    "正式包提供 SHA-256，源码与发行记录公开可查。": "Stable packages include SHA-256 files, with public source and release history.",
    "非官方社区项目，与 Microsoft、Xbox 或 Playground Games 无隶属关系。": "An unofficial community project not affiliated with Microsoft, Xbox or Playground Games.",
    "页脚链接": "Footer links",
    "客户端仓库": "Client repository",

    "LazyForza 用户与开发者文档：安装、配置、功能、RaceServer、架构、构建和验证边界。": "LazyForza user and developer documentation for installation, configuration, features, RaceServer, architecture, builds and data boundaries.",
    "LazyForza 文档": "LazyForza Documentation",
    "跳到文档正文": "Skip to documentation",
    "返回 LazyForza 官网": "Back to the LazyForza website",
    "文档外部链接": "Documentation links",
    "官网": "Website",
    "客户端仓库 ↗": "Client repository ↗",
    "服务端仓库 ↗": "Server repository ↗",
    "文档目录": "Contents",
    "概览": "Overview",
    "快速开始": "Quick start",
    "客户端功能": "Client features",
    "数据与更新": "Data and updates",
    "常见问题": "Troubleshooting",
    "开发者指南": "Developer guide",
    "协议字段": "Protocol fields",
    "参考资料": "References",
    "当前文档": "Current docs",
    "用户与开发者文档": "User and developer documentation",
    "从第一次接收 FH6 遥测，到部署赛事服务端、阅读架构和提交代码。只保留需要的信息。": "From receiving your first FH6 telemetry packet to deploying RaceServer, understanding the architecture and contributing code.",
    "客户端": "Client",
    "数据入口": "Data input",
    "不读取游戏内存，不注入 DLL，不修改游戏进程。官方 UDP 没有提供的数据不会被包装成官方数据。": "LazyForza does not read game memory, inject DLLs or modify the game process. Data absent from official UDP output is never presented as official telemetry.",
    "下载与安装": "Download and install",
    "从": "Download from",
    "或官网的 GitCode 入口下载安装版。需要免安装使用时，选择": "or the GitCode link on the homepage. For a no-install setup, choose the",
    "便携版并完整解压。两种版本都已包含 .NET 运行时。": "portable build and extract it fully. Both packages include the .NET runtime.",
    "不要在 ZIP 内直接运行": "Do not run from inside the ZIP",
    "更新、日志、数据库和录制需要正常的可写目录。": "Updates, logs, the database and recordings require a normal writable directory.",
    "配置 FH6 Data Out": "Configure FH6 Data Out",
    "打开设置": "Open settings",
    "进入 FH6“设置 > HUD 与游戏玩法”。": "Open Settings > HUD and Gameplay in FH6.",
    "HUD 与游戏玩法”。": "HUD and Gameplay in FH6.",
    "开启 Data Out": "Enable Data Out",
    "地址填写": "Set the address to",
    "设置端口": "Set the port",
    "端口填写": "Set the port to",
    "，返回驾驶。": "and return to driving.",
    "第一次使用": "First run",
    "只开启当前需要的 HUD 部件；": "Enable only the HUD components you need.",
    "用布局编辑器调整位置、缩放和透明度；": "Use the layout editor to adjust position, scale and opacity.",
    "正常完成几圈，让程序建立车辆与赛道记录；": "Complete a few normal laps so the app can build vehicle and track records.",
    "确实需要复盘时再开启自动录制。": "Enable automatic recording only when you need replay data.",
    "实时仪表盘": "Live dashboard",
    "速度、挡位、转速、踏板、方向、轮胎、动力和性能等级可独立开关、移动、缩放与调节透明度。窗口支持置顶和鼠标穿透。": "Speed, gear, RPM, pedals, steering, tires, power and performance class can be toggled, moved, scaled and adjusted independently. HUD windows support always-on-top and click-through modes.",
    "圈速与走线": "Lap times and racing lines",
    "按距离对齐单圈，联动速度、油门、制动、方向与走线。紫色分段只表示本地同等级最快，不代表在线世界纪录。": "Align laps by distance and compare speed, throttle, braking, steering and lines. Purple sectors mean the fastest local sector in the same class, not an online world record.",
    "按车型、性能等级和可观测调校特征保存结果。车辆名称映射使用 HDR 提供的 Car Ordinals 文档内置快照。": "Stores results by vehicle, performance class and observable tune traits. Vehicle names use a bundled snapshot of HDR's Car Ordinals documentation.",
    "自动录制默认关闭。可保留赛前 15 秒与赛后 10 秒；回放工作台联动时间轴、驾驶输入、走线和动态遥测。": "Automatic recording is off by default. It can retain 15 seconds before and 10 seconds after a session; replay links the timeline, driving inputs, line and telemetry.",
    "实验性漂移 HUD": "Experimental drift HUD",
    "用本车 UDP 推导侧滑角和控车趋势，优先降低 Spin 风险。换挡箭头不是最佳换挡点，积分趋势不复刻游戏分数。": "Uses local UDP data to estimate slip angle and control trends with spin prevention as the priority. Shift arrows are not optimal shift points, and score trends do not reproduce the game's score.",
    "地产环道与赛事": "Estate circuits and racing",
    "录入支持暂存、地图预览和分项修订；赛事 HUD 提示弱网状态，服务端可选开启断线计圈恢复。": "Capture can be paused, previewed on a map and revised by component. The race HUD reports network issues, and servers can optionally enable disconnected-lap recovery.",
    "模拟换胎": "Simulated tire change",
    "地产赛事的换胎流程使用游戏内设置模拟。依次进入维修区和换胎区，车辆停稳后暂停游戏，打开“设置 → 难度”，将“损坏与轮胎磨损”调为“外观”并保存；随后调回“拟真”，再次保存并返回游戏。LazyForza 只核对车辆位置和停留条件，不读取暂停菜单，也无法确认游戏内设置是否完成。": "Estate races simulate a tire change through the in-game settings. Enter the pit lane and tire-change zone, stop the car, pause, open Settings > Difficulty, set Damage & Tire Wear to Cosmetic and save. Set it back to Simulation, save again and return to the game. LazyForza checks only vehicle position and dwell conditions; it cannot read the pause menu or confirm that the in-game setting was changed.",
    "数据、隐私与更新": "Data, privacy and updates",
    "默认数据目录": "Default data directory",
    "路径": "Path",
    "内容": "Contents",
    "设置、车辆、赛道和圈速索引": "Settings, vehicles, tracks and lap index",
    "自动或手动遥测录制": "Automatic or manual telemetry recordings",
    "运行日志": "Runtime logs",
    "数据迁移备份": "Migration backups",
    "用户主动生成的诊断资料": "User-generated diagnostics",
    "更新下载和临时文件": "Update downloads and temporary files",
    "使用": "Use",
    "指定独立目录：": "to select a separate data directory:",
    "隐私边界": "Privacy boundaries",
    "默认不要求账号，个人数据保存在本机；": "No account is required by default; personal data stays on your PC.",
    "只有主动连接 RaceServer 时，赛事所需资料和遥测摘要才发送到该服务器；": "Race data and telemetry summaries are sent only when you connect to a RaceServer.",
    "不伪造官方 UDP 未提供的赛道 ID、调校 ID、车辆尺寸或轮胎位置；": "Track IDs, tune IDs, vehicle dimensions or tire positions absent from official UDP data are not fabricated.",
    "录制容量、轮换、备份和更新均由用户控制。": "Recording limits, rotation, backups and updates remain under user control.",
    "更新": "Updates",
    "正式安装版默认启动检查更新，正式便携版默认关闭，两者都可在设置中修改。正式版只提示、不强制安装，下载优先使用 GitCode，失败时回退 GitHub。预览版使用独立初始化状态和 GitHub 预发布更新通道，每次启动强制检查并自动安装可用更新；检查或安装失败时需重试或退出。安装前均校验发行包 SHA-256 和包内清单，失败时恢复原版本。": "Stable installed builds check for updates by default; stable portable builds do not, and both settings can be changed. Stable updates are offered rather than forced, with GitCode preferred and GitHub as fallback. Preview builds use separate initialization state and a GitHub prerelease channel, check on every startup, and install available updates automatically; a failed check or installation must be retried or the app exits. Every package verifies SHA-256 and its internal manifest before installation, with rollback on failure.",
    "RaceServer 是独立项目，只进行日常驾驶和圈速分析时无需安装。": "RaceServer is a separate project and is not required for daily driving or lap analysis.",
    "参赛车手": "Drivers",
    "额外 OB 席位": "Additional observer slots",
    "赛事协议": "Race protocol",
    "提供什么": "Features",
    "1–3 节练习与排位、正赛、五盏红灯和方格旗；": "One to three practice and qualifying sessions, races, five red lights and the checkered flag.",
    "车队、维修区、旗语、处罚、碰撞调查、路线收益切弯证据、OB 和可选断线计圈恢复；": "Teams, pit lanes, flags, penalties, collision investigations, shortcut evidence, observers and optional disconnected-lap recovery.",
    "阶段赛果归档，返回大厅后仍可回看并导出 PNG/CSV；": "Archived session results remain available after returning to the lobby and can be exported as PNG or CSV.",
    "超管、管理员和裁判多账号分权；同一角色可创建多个独立账号；": "Separate super-admin, administrator and steward accounts, with multiple users allowed for each role.",
    "适配电脑宽屏和 Pad 触控的浏览器总控。": "Browser Race Control designed for desktop widescreens and touch tablets.",
    "选择部署方式": "Choose a deployment",
    "方式": "Option",
    "适合场景": "Best for",
    "维护要求": "Operations",
    "原生自托管": "Native self-hosting",
    "本地联机、VPS、固定服务器": "LAN races, VPS or dedicated servers",
    "自行维护进程、端口、TLS 和备份": "Manage the process, port, TLS and backups",
    "不想维护 VPS": "No VPS maintenance",
    "Cloudflare 账号与 Workers 部署权限": "Cloudflare account with Workers deployment access",
    "原生服务端": "Native server",
    "下载对应平台 ZIP。支持": "and download the ZIP for your platform. Supported targets:",
    "和": "and",
    "默认监听": "Listens on",
    "。首次打开网页时设置房间密码、初始超管密码和赛事规则，不要求设置其他角色；超管之后可按需创建管理员、裁判或更多超管账号。公网部署应由 Caddy、Nginx 或同类反向代理终止 TLS，让客户端连接": "by default. On first launch, set the room password, initial super-admin password and race rules. Other roles are optional; the super admin can later add administrators, stewards or more super admins. For public hosting, terminate TLS with Caddy, Nginx or a similar reverse proxy so clients connect over",
    "。": ".",
    "Cloudflare 部署": "Cloudflare deployment",
    "在服务端仓库使用 Deploy to Cloudflare，或运行：": "Use Deploy to Cloudflare from the server repository, or run:",
    "需要 Node.js 20+、npm 和 PowerShell 7。部署后打开 Worker 域名完成首次设置，再上传": "Requires Node.js 20+, npm and PowerShell 7. After deployment, open the Worker domain to finish setup, then upload the",
    "赛道文件。": "track file.",
    "客户端连接": "Client connection",
    "车手需要服务端域名或 IP、房间密码、匹配的地产赛道、显示名和可选车队。客户端可收藏常用服务器，并在进入房间前测试服务端可达性、协议版本和房间信息；收藏不会保存赛事密码。OB 只接收赛事快照，不上传遥测、不参与排名和处罚。": "Drivers need the server domain or IP, room password, matching estate circuit, display name and optional team. The client can save frequently used servers and test reachability, protocol version and room information before joining; favorites never store the race password. Observers receive race snapshots only; they do not upload telemetry or participate in standings or penalties.",
    "兼容性": "Compatibility",
    "RaceServer": "RaceServer",
    "推荐搭配 LazyForza": "is recommended with LazyForza",
    "，并兼容": "and remains compatible with LazyForza",
    "的协议 v2 主要流程。断线计圈恢复需要客户端": "for the main protocol v2 race flow. Disconnected-lap recovery requires client",
    "支持 LazyForza": "supports the main protocol v2 flow in LazyForza",
    "的协议 v2 主要流程。路线收益切弯证据和本次碰撞识别改进需要客户端": ". Shortcut evidence and the latest collision detection require client",
    "；断线计圈恢复需要客户端": "; disconnected-lap recovery requires client",
    "或更高版本，并由服务端总控开启。": "or later and must be enabled from Race Control.",
    "客户端一直显示没有遥测": "The client shows no telemetry",
    "确认 FH6 已开启 Data Out，IP 为": "Confirm that FH6 Data Out is enabled, with IP",
    "，端口为": "and port",
    "。检查端口占用和本机防火墙；游戏与 LazyForza 的端口必须一致。": ". Check port conflicts and the local firewall; FH6 and LazyForza must use the same port.",
    "HUD 没有显示或位置错误": "The HUD is missing or misplaced",
    "确认对应 HUD 已开启。进入布局编辑器检查显示器、位置、缩放和透明度；多显示器或分辨率变化后可先恢复默认布局。": "Confirm that the HUD is enabled. Use the layout editor to check the display, position, scale and opacity. Restore the default layout after major display or resolution changes if needed.",
    "赛道没有自动识别": "The track is not recognized automatically",
    "先完成一段稳定驾驶，避免倒车、重置、暂停和大幅偏离路线。用户自定义路线需要单独录入；无法可靠区分时程序会要求人工确认。": "Drive a stable section without reversing, resetting, pausing or leaving the route. Custom routes must be captured separately; the app asks for confirmation when it cannot distinguish them reliably.",
    "自动录制停止": "Automatic recording stopped",
    "检查录制目录空间和容量上限。默认达到上限后停止，不自动删除；需要轮换时由用户明确开启。": "Check free space and the recording limit. Recording stops at the limit by default and does not delete files unless rotation is explicitly enabled.",
    "无法连接 RaceServer": "Cannot connect to RaceServer",
    "检查地址、房间密码和协议版本。公网环境需要 HTTPS/WSS，反向代理必须转发 WebSocket Upgrade。网页能打开不代表 WebSocket 一定可用。": "Check the address, room password and protocol version. Public hosting requires HTTPS/WSS and the reverse proxy must forward WebSocket upgrades. A working webpage does not guarantee a working WebSocket.",
    "更新失败": "Update failed",
    "保留提示和": "Keep the error message and logs in",
    "。重新下载完整 ZIP 并解压到新目录即可；个人数据默认不在程序目录中。": ". You can download a complete ZIP and extract it to a new folder; personal data is not stored in the program directory by default.",
    "技术栈": "Technology",
    "构建客户端": "Build the client",
    "开发和 QA 使用隔离目录或模拟输入：": "For development and QA, use an isolated data path or simulated input:",
    "客户端项目结构": "Client projects",
    "项目": "Project",
    "职责": "Responsibility",
    "UDP 接收、解析和遥测帧": "UDP receive, parsing and telemetry frames",
    "领域模型与公共类型": "Domain models and shared types",
    "圈速、路线、分段和驾驶分析": "Lap, route, sector and driving analysis",
    "SQLite、录制、备份与数据目录": "SQLite, recordings, backups and data paths",
    "仪表盘、圈速和地产赛事模块": "Dashboard, lap analysis and estate race modules",
    "透明 HUD、布局与格式化": "Transparent HUDs, layouts and formatting",
    "更新检查、校验与替换": "Update checks, verification and replacement",
    "WPF 主程序与模块装配": "WPF application shell and module composition",
    "构建 RaceServer": "Build RaceServer",
    "原生端与 Cloudflare 端共享协议语义。协议、总控接口和 Web 功能变更必须同步实现并补齐双端测试。": "The native and Cloudflare servers share one protocol contract. Protocol, Race Control API and web changes must be implemented and tested on both platforms.",
    "协议字段边界": "Protocol field boundaries",
    "FH6 官方 Data Out 固定为 324 字节。可以直接获得位置、速度、姿态、输入、动力、悬挂和轮胎状态等字段，但不提供：": "Official FH6 Data Out packets are 324 bytes. They provide position, speed, attitude, inputs, powertrain, suspension and tire state, but do not include:",
    "官方赛道 ID 和赛事 ID；": "Official track or race IDs.",
    "对手车辆遥测；": "Opponent vehicle telemetry.",
    "调校 ID、宽体或精确车辆几何。": "Tune IDs, wide-body state or exact vehicle geometry.",
    "开发时以官方字段定义为准。推导数据必须与官方字段明确区分。": "Use the official field definitions when developing. Derived data must remain clearly distinct from official fields.",
    "客户端架构": "Client architecture",
    "数据流、模块、Overlay 与存储": "Data flow, modules, Overlay and storage",
    "FH6 遥测开发参考": "FH6 telemetry development reference",
    "字段、推导和缺失数据": "Fields, derived values and unavailable data",
    "RaceServer 仓库": "RaceServer repository",
    "协议、总控与部署源码": "Protocol, Race Control and deployment source",
    "车辆标识符文档与离线名称映射来源": "Vehicle identifier documentation and offline name source",
    "客户端 Releases": "Client releases",
    "正式包与 SHA-256": "Stable packages and SHA-256 files",
    "全平台与 Cloudflare 发行包": "All platforms and Cloudflare package",
    "致谢": "Acknowledgements",
    "感谢": "Thanks to",
    "HDR 维护并提供 FH6 Car Ordinals 车辆标识符文档": "HDR for maintaining and sharing the FH6 Car Ordinals documentation",
    "。LazyForza 使用其内置快照完成离线车辆名称映射。": ". LazyForza uses a bundled snapshot for offline vehicle-name mapping.",
    "License 与商标": "License and trademarks",
    "LazyForza 和 RaceServer 使用 MIT License。项目为非官方社区作品，与 Microsoft、Xbox 或 Playground Games 无隶属关系；相关商标属于其各自权利人。": "LazyForza and RaceServer use the MIT License. They are unofficial community projects not affiliated with Microsoft, Xbox or Playground Games; all related trademarks belong to their respective owners.",
    "返回顶部 ↑": "Back to top ↑",
    "复制": "Copy",
    "复制代码": "Copy code",
    "已复制": "Copied",
    "复制失败": "Copy failed"
  };

  function chooseLanguage(languages) {
    for (const candidate of languages || []) {
      const value = String(candidate || "").toLowerCase();
      if (value.startsWith("zh")) return "zh-Hans";
      if (value.startsWith("en")) return "en";
    }
    return "en";
  }

  function storedLanguage() {
    if (typeof window === "undefined") return null;
    if (typeof globalObject.localStorage === "undefined") return null;
    try {
      const value = globalObject.localStorage.getItem(STORAGE_KEY);
      return value === "zh-Hans" || value === "en" ? value : null;
    } catch {
      return null;
    }
  }

  function browserLanguage() {
    if (typeof window === "undefined") return "en";
    if (typeof globalObject.navigator === "undefined") return "en";
    const languages = globalObject.navigator.languages?.length
      ? globalObject.navigator.languages
      : [globalObject.navigator.language];
    return chooseLanguage(languages);
  }

  const language = storedLanguage() || browserLanguage();

  function text(value) {
    if (language !== "en" || !value) return value;
    const dateMatch = value.match(/^(\d{4})\.(\d{2})\.(\d{2}) 更新$/u);
    if (dateMatch) return `Updated ${dateMatch[2]}/${dateMatch[3]}/${dateMatch[1]}`;
    return ENGLISH[value] || value;
  }

  function translateTextNode(node) {
    const value = node.nodeValue || "";
    const trimmed = value.trim();
    const translated = text(trimmed);
    if (!trimmed || translated === trimmed) return;
    const leading = value.slice(0, value.indexOf(trimmed));
    const trailing = value.slice(value.indexOf(trimmed) + trimmed.length);
    node.nodeValue = `${leading}${translated}${trailing}`;
  }

  function apply(root) {
    if (typeof document === "undefined") return;
    document.documentElement.lang = language === "en" ? "en" : "zh-CN";
    if (language === "en") {
      const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
      const nodes = [];
      while (walker.nextNode()) nodes.push(walker.currentNode);
      nodes.forEach(translateTextNode);
      for (const element of root.querySelectorAll("[aria-label], [title], [placeholder], meta[content]")) {
        for (const attribute of ["aria-label", "title", "placeholder", "content"]) {
          if (!element.hasAttribute(attribute)) continue;
          const value = element.getAttribute(attribute);
          if (ENGLISH[value]) element.setAttribute(attribute, ENGLISH[value]);
        }
      }
    }

    for (const selector of document.querySelectorAll("[data-language-select]")) {
      selector.value = language;
      selector.addEventListener("change", () => {
        try {
          globalObject.localStorage.setItem(STORAGE_KEY, selector.value);
        } catch {
          // Continue with the current page when storage is unavailable.
        }
        globalObject.location.reload();
      });
    }
  }

  const api = { language, text, apply, chooseLanguage, english: ENGLISH };
  globalObject.WebsiteI18n = api;
  if (typeof module !== "undefined" && module.exports) module.exports = api;

  if (typeof document !== "undefined") {
    if (document.readyState === "loading") {
      document.addEventListener("DOMContentLoaded", () => apply(document));
    } else {
      apply(document);
    }
  }
})(typeof window !== "undefined" ? window : globalThis);
