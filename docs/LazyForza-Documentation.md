# LazyForza 文档

LazyForza 是面向 Forza Horizon 6 的本地遥测、驾驶分析与地产赛事工具。客户端只使用 FH6 官方 UDP Data Out，不读取游戏内存，不注入 DLL，不修改游戏进程。

- 客户端：Windows 10/11 x64
- 当前客户端：`1.5.0`
- 当前 RaceServer：`0.4.3`
- 官网：<https://laz22y.github.io/LazyForza/>
- 客户端仓库：<https://github.com/Laz22y/LazyForza>
- 服务端仓库：<https://github.com/Laz22y/LazyForza.RaceServer>

参与开发或使用 Coding Agent 时，从仓库根目录的 [`AGENTS.md`](../AGENTS.md) 开始；它列出当前模块边界、跨仓库同步范围和验证矩阵。

## 1. 快速开始

### 1.1 下载与安装

从 [GitHub Releases](https://github.com/Laz22y/LazyForza/releases/latest) 或官网的 GitCode 下载入口获取最新版：

- 安装版：运行 `win-x64-setup.exe`，默认安装到 `C:\Program Files\LazyForza`。安装器创建开始菜单入口；桌面快捷方式和 `.lfztelemetry`、`.lfzlap`、`.lfzestate` 文件关联可在安装时选择；
- 便携版：下载 `win-x64.zip`，完整解压后运行 `LazyForza.App.exe`。便携版不写入注册表、不创建开始菜单入口，也不注册文件关联。

两种版本都包含 .NET 运行时，主程序功能与数据格式相同。

不要直接在 ZIP 压缩包内运行。更新、日志、录制和本地数据库都需要正常的可写目录。

### 1.2 首次启动

首次启动不会直接显示主窗口。初始化指引依次设置界面语言、可选玩家代号、数据目录、关闭窗口行为和 FH6 Data Out。最后一步收到有效遥测后会自动完成，也可跳过连接直接进入主窗口。跳过只省略本次连接确认，之前选择的设置仍会保存，后续启动不会重复显示指引。

直接关闭初始化窗口会退出整个程序且不写入完成标记；下次启动会重新打开初始化指引。

旧版升级后会执行一次初始化。便携版将完成状态保存在当前软件目录中：覆盖更新原目录不会重复初始化，解压到新目录则视为新的便携实例。安装版正常升级保留完成状态；卸载会重置该状态但保留数据库，重新安装后再次显示指引。语言、UI 强调色、数据目录和关闭行为可在设置页修改。强调色提供默认蓝、暗夜紫、清新绿、鲜艳红、纯粹白和低调灰六种选择，只改变界面高亮与选中状态。

### 1.3 配置 FH6 Data Out

在 FH6 的“设置 > HUD 与游戏玩法”中：

1. 开启 `Data Out`；
2. 地址填写 `127.0.0.1`；
3. 端口填写 `2299`。

返回驾驶后，LazyForza 会自动接收数据。主界面显示 Live 状态即表示连接成功。

### 1.4 第一次使用建议

1. 在 HUD 设置中只开启当前需要的部件；
2. 打开布局编辑器，调整位置、缩放和透明度；
3. 正常完成几圈，让程序建立车辆与赛道记录；
4. 需要复盘时再启用自动录制，避免无目的地长期积累文件。

## 2. 客户端功能

### 2.1 实时 HUD

主仪表盘可显示速度、挡位、转速灯带、踏板、方向输入、轮胎、动力与车辆性能等级。各部件可独立开关、移动、缩放和调整透明度。窗口支持置顶和鼠标穿透，不占用游戏操作焦点。

圈速 HUD 与地产赛事 HUD 使用独立布局。布局编辑时可以暂时隐藏部件，便于处理重叠区域。

### 2.2 圈速与走线分析

LazyForza 按赛道距离对齐单圈，联动显示：

- 当前圈、个人参考圈与分段时间；
- 速度、油门、制动和方向输入；
- 走线及距离游标；
- 单圈稳定性、分段趋势和组合最佳。

紫色分段只表示本地可见记录中、同一性能等级下的最快数据，不代表在线世界纪录。

### 2.3 车辆与换挡学习

程序按车型、性能等级和可观测调校特征保存换挡学习结果。FH6 UDP 不提供调校 ID，因此无法可靠区分的配置会保持保守，不会伪造精确身份。

车辆名称映射使用内置的 Car Ordinals 快照，运行时不需要访问外部服务。感谢 [HDR 维护并提供 FH6 Car Ordinals 车辆标识符文档](https://gist.github.com/HDR/0659d1717bc61504bf83750628963f4f)。

### 2.4 自动录制与回放

自动录制默认关闭。启用后可保留赛前 15 秒和赛后 10 秒，并设置容量上限。默认策略是在达到上限时停止新录制，不主动删除文件；用户开启轮换后，程序仍会保护手动固定、个人最佳圈和需要诊断的样本。

回放工作台支持 `.lfztelemetry` 文件，并联动时间轴、驾驶输入、走线和动态遥测。

### 2.5 实验性漂移 HUD

漂移 HUD 默认关闭。它根据本车 UDP 遥测推导侧滑角、控车余量和趋势提示，优先降低 Spin 风险。换挡箭头不是最佳换挡点，积分速度也不复刻游戏分数。

启用漂移 HUD 时，圈速分析停止订阅且不写入新圈速；关闭后恢复原有设置。

### 2.6 地产环道与赛事

地产环道录入可随时暂存并恢复，地图预览会同步显示已采集的主路线和维修区数据。已保存赛道可分别修订起跑线、维修区入口、维修区路线、换胎区、出口和限速设置，不必重录整条赛道。普通赛道手动录入会先进入准备状态，并可在正式采集前取消。

地产赛事会结合参考路线弧长、实走距离和关键弯门生成切弯证据，并排除维修支路、倒车、瞬移和采样中断。维修区轨迹存在明显断点时会拒绝保存或启用，避免生成不可靠的计圈门。

连接 RaceServer 后可参加练习、排位和正赛，并接收旗语、处罚、调查、进站与发车状态。赛事 HUD 会提示高延迟、网络波动和重连状态；服务端开启断线计圈恢复后，客户端可在短时断线重连后补交本地完成的单圈。

碰撞调查结合短时冲击、双车接近轨迹、相对运动和可破坏物字段形成待审证据，只供总控复核，不自动判断责任。

## 3. 数据、隐私与更新

### 3.1 本地数据目录

首次启动可选择以下目录：

- `%LOCALAPPDATA%\LazyForza`；
- `%LOCALAPPDATA%\LazyForza-Release`；
- 程序目录下的 `Data`；
- 任意自定义目录。

选择新目录不会自动移动旧目录中的数据。设置页保存后重启生效。

主要内容：

| 路径 | 内容 |
| --- | --- |
| `lazyforza.db` | 设置、车辆、赛道和圈速索引 |
| `Recordings` | 自动或手动遥测录制 |
| `Logs` | 运行日志 |
| `Backups` | 数据迁移备份 |
| `Diagnostics` | 用户主动生成的诊断资料 |
| `Updates` | 更新下载与临时文件 |

使用 `--data-dir` 可以指定独立目录：

```powershell
LazyForza.App.exe --data-dir "D:\LazyForza_Data"
```

### 3.2 隐私边界

- 默认不要求账号；
- 客户端个人数据保存在本机；
- 不读取游戏内存，不注入 DLL；
- 不伪造官方 UDP 未提供的赛道 ID、调校 ID、车辆尺寸或轮胎位置；
- 只有用户主动连接 RaceServer 时，赛事所需的车手资料和遥测摘要才会发送到该服务器。

### 3.3 更新

安装版默认启动检查更新，便携版默认关闭，两者都可在设置中修改。发现新版时先由用户确认，不强制安装；更新说明按当前界面语言显示中文或英文。程序优先使用 GitCode，失败时回退 GitHub：安装版下载并校验新版安装程序，完成系统安装信息和卸载清单升级；便携版校验 ZIP 的 SHA-256 与包内清单后原地更新，失败时恢复原版本。正常升级不会改变已选择的数据目录或重复执行初始化。

## 4. RaceServer

RaceServer 是独立部署的赛事服务端，不是客户端运行所必需的组件。只进行日常驾驶和圈速分析时无需安装。

### 4.1 能力与规模

- 1–12 名车手，可单人发车；
- 最多 12 个只读 OB 席位，不占车手名额；
- 1–3 节练习与排位、正赛、五盏红灯和方格旗；
- 车队、维修区、进站规则、旗语、处罚、碰撞调查、路线收益切弯证据和可选断线计圈恢复；
- 阶段赛果归档，返回大厅后仍可回看，并支持 PNG/CSV 导出；
- 浏览器总控适配电脑宽屏和 Pad 触控；
- 原生 ASP.NET 与 Cloudflare Durable Objects 两种部署。

### 4.2 模拟换胎

地产赛事使用游戏内设置模拟换胎。按以下顺序操作：

1. 进入维修区；
2. 进入换胎区并将车辆停稳；
3. 暂停游戏，打开“设置 → 难度”；
4. 将“损坏与轮胎磨损”调为“外观”并保存；
5. 将“损坏与轮胎磨损”调回“拟真”，再次保存并返回游戏。

LazyForza 只核对车辆是否进入换胎区并满足停留条件，不读取暂停菜单，也无法确认游戏内设置是否完成。

### 4.3 选择部署方式

| 方式 | 适合场景 | 维护要求 |
| --- | --- | --- |
| 原生自托管 | 本地联机、VPS、长期固定服务器 | 自行维护进程、端口、TLS 和备份 |
| Cloudflare Durable Objects | 不想维护 VPS，接受 Cloudflare 平台 | 需要 Cloudflare 账号和 Workers 部署权限 |

两种实现使用同一协议和同一套 Web 总控功能。不要同时把两套服务指向同一个赛事房间。

### 4.4 原生服务端

从 [RaceServer Releases](https://github.com/Laz22y/LazyForza.RaceServer/releases/latest) 下载对应平台 ZIP：

- `win-x64`：Windows x64；
- `linux-x64` / `linux-arm64`：Linux；
- `osx-x64` / `osx-arm64`：macOS。

解压后启动：

```powershell
# Windows
./LazyForza.RaceServer.Web.exe
```

```bash
# Linux / macOS
chmod +x ./LazyForza.RaceServer.Web
./LazyForza.RaceServer.Web
```

默认监听 `http://0.0.0.0:24876`。首次打开网页时设置房间密码、总控密码和赛事基础规则。总控密码为 8–128 个字符，不能与房间密码相同。

公网部署应由 Caddy、Nginx 或同类反向代理终止 TLS。车手应连接 `wss://` 地址，不要把明文 `ws://` 暴露到互联网。

### 4.5 Cloudflare 部署

在服务端仓库使用“Deploy to Cloudflare”按钮，或在仓库根目录运行：

```powershell
./scripts/Deploy-Cloudflare.ps1
```

脚本要求 Node.js 20+、npm 和 PowerShell 7。部署完成后打开 Worker 域名完成首次设置，再上传 `.lfzestate` 赛道文件。

### 4.6 客户端连接

车手需要：

1. 服务端域名或 IP；
2. 房间密码；
3. 与房间匹配的地产赛道；
4. 显示名、主题色和可选车队。

OB 使用 OB 身份登录，只接收赛事快照，不上传遥测、不参与排名和处罚。

### 4.7 兼容性

RaceServer `0.4.3` 推荐搭配 LazyForza `1.5.0`，并兼容 `1.4.2–1.4.9` 的协议 v2 主要赛事流程。断线计圈恢复需要客户端 `1.4.8` 或更高版本，并由服务端总控主动开启。旧版仍可连接，但不会拥有后续新增的全部能力。

## 5. 常见问题

### 客户端一直显示没有遥测

确认 FH6 已开启 Data Out，IP 为 `127.0.0.1`，端口为 `2299`。检查是否有其他程序占用端口，以及防火墙是否阻止本机 UDP。修改端口后，游戏和 LazyForza 必须一致。

### HUD 没有显示或挡住游戏界面

在设置中确认对应 HUD 已开启。进入布局编辑器检查显示器、位置、缩放和透明度；多显示器或分辨率变化后可恢复默认布局再调整。

### 赛道没有自动识别

先完成一段稳定驾驶，避免倒车、重置、暂停和大幅偏离路线。官方赛道目录只提供模板，用户自定义路线需要单独录入。无法可靠区分时，程序会要求人工确认。

### 自动录制停止

检查录制目录可用空间和容量上限。默认达到上限后停止，不自动删除。需要轮换时由用户明确开启，并确认重要录制已固定或备份。

### 无法连接 RaceServer

检查地址、房间密码和协议版本。公网环境必须允许 HTTPS/WSS；反向代理需要转发 WebSocket Upgrade。客户端和浏览器能打开网页，不代表 WebSocket 一定可用。

### 更新失败

保留提示中的错误信息和 `%LOCALAPPDATA%\LazyForza\Logs`。不要手动覆盖正在运行的程序；可以重新下载完整 ZIP，解压到新目录后启动。个人数据默认不在程序目录中。

## 6. 开发者指南

### 6.1 技术栈

- .NET 9；
- WPF 客户端与透明 Overlay；
- SQLite 本地存储；
- UDP 324 字节 FH6 Data Out；
- ASP.NET Core RaceServer；
- TypeScript + Cloudflare Workers / Durable Objects。

### 6.2 客户端构建

需要 Windows 10/11 x64、.NET SDK 9 和 PowerShell 7：

```powershell
git clone https://github.com/Laz22y/LazyForza.git
cd LazyForza
dotnet restore LazyForza.sln --configfile NuGet.Config
dotnet build LazyForza.sln --no-restore -c Debug
dotnet test LazyForza.sln --no-build --no-restore -c Debug
dotnet run --project src/LazyForza.App/LazyForza.App.csproj --no-build --no-restore -c Debug
```

开发和 QA 可使用隔离数据目录：

```powershell
dotnet run --project src/LazyForza.App/LazyForza.App.csproj -- --data-dir ".\.dev-data"
dotnet run --project src/LazyForza.App/LazyForza.App.csproj -- --demo
dotnet run --project src/LazyForza.App/LazyForza.App.csproj -- --replay "C:\path\session.lfztelemetry"
```

### 6.3 客户端项目结构

| 项目 | 职责 |
| --- | --- |
| `LazyForza.Telemetry` | UDP 接收、解析和遥测帧 |
| `LazyForza.Domain` | 领域模型与公共类型 |
| `LazyForza.Analysis` | 圈速、路线、分段与驾驶分析 |
| `LazyForza.Storage` | SQLite、录制、备份与数据目录 |
| `LazyForza.Modules.*` | 仪表盘、圈速和地产赛事模块 |
| `LazyForza.Overlay` | 透明 HUD、布局与格式化 |
| `LazyForza.Update` | 更新检查、校验与替换 |
| `LazyForza.App` | WPF 主程序和模块装配 |

主数据流：

```text
FH6 UDP → Telemetry → Domain Frame → Modules → Analysis / Storage → App / Overlay
```

### 6.4 RaceServer 构建

```powershell
git clone https://github.com/Laz22y/LazyForza.RaceServer.git
cd LazyForza.RaceServer
dotnet restore LazyForza.RaceServer.sln
dotnet build LazyForza.RaceServer.sln -c Release --no-restore
dotnet test LazyForza.RaceServer.sln -c Release --no-build --no-restore

cd cloudflare
npm ci
npm run check
npm test
```

原生端与 Cloudflare 端共享协议语义，但各自维护状态实现。任何赛事协议、总控接口或 Web 功能变更都必须同步修改并补齐双端测试。

### 6.5 协议字段边界

FH6 官方 Data Out 固定为 324 字节。可以直接获得位置、速度、姿态、输入、动力、悬挂和轮胎状态等字段，但不提供：

- 官方赛道 ID 和赛事 ID；
- 对手车辆遥测；
- 调校 ID、宽体或精确车辆几何。

开发时以官方字段定义为准。推导数据必须与官方字段明确区分。

## 7. 参考资料

- [客户端架构](../ARCHITECTURE.md)
- [FH6 遥测开发参考](../FH6_TELEMETRY_DEVELOPMENT_GUIDE.md)
- [地产坐标验证](ESTATE_COORDINATE_VALIDATION.md)
- [地产赛事进站策略](ESTATE_RACE_PIT_STRATEGY.md)
- [HDR 维护的 FH6 Car Ordinals](https://gist.github.com/HDR/0659d1717bc61504bf83750628963f4f)
- [客户端 Releases](https://github.com/Laz22y/LazyForza/releases/latest)
- [RaceServer Releases](https://github.com/Laz22y/LazyForza.RaceServer/releases/latest)

## 8. License 与商标

LazyForza 和 RaceServer 使用 MIT License。项目为非官方社区作品，与 Microsoft、Xbox 或 Playground Games 无隶属关系；Forza、Forza Horizon、Xbox 及相关商标属于其各自权利人。
