<p align="center">
  <img src="docs/assets/LazyForzaReadmeBanner.png" alt="LazyForza" width="720">
</p>

<p align="center">
  Forza Horizon 6 的透明遥测仪表盘与本地圈速分析工具
</p>

LazyForza 通过 FH6 官方 UDP Data Out 获取数据，不读取游戏内存、不注入 DLL，也不会修改游戏进程。默认监听 `127.0.0.1:2299`，启动后即可进入 Live 模式。

## 功能

- 透明、置顶、鼠标穿透的车辆仪表盘，支持缩放、透明度、惯性跟随和静止淡出；
- 轮胎温度/抓地力、动力、扭矩、踏板、挡位与升降挡提示；
- 按车型和调校特征分别学习换挡目标，车辆名称可离线识别；
- 环道与定点赛事学习、自动匹配、分段 HUD、圈速和走线对比；
- 仪表盘 HUD 与圈速 HUD 可独立移动和固定中心缩放，并支持一键吸附及靠近时的对齐辅助；
- 赛道识别纠错助手，以及速度、油门、制动、方向与走线的距离游标联动；
- 可选的比赛自动录制，包含赛前 15 秒与赛后 10 秒，并提供容量上限、保守停止和受保护轮换；
- 回放工作台可直接打开 `.lfztelemetry`，也可将数据库单圈导出为带完整性校验的单圈 `.lfztelemetry`，联动回放时间轴、速度、驾驶输入、走线与动态遥测；
- 内置 85 条 Playground 官方赛事模板，用户数据保存在本机；
- 优先从 GitCode 检查和下载稳定版更新；GitCode 不可用时自动回退 GitHub。用户确认后执行校验、替换和重启。

## 下载与使用

在 [Releases](https://github.com/Laz22y/LazyForza/releases/latest) 下载最新版 `win-x64.zip`，完整解压后运行 `LazyForza.App.exe`。发行包已包含 .NET 运行时。

在 FH6 的“设置 > HUD 与游戏玩法”中：

1. 开启 Data Out；
2. 地址填写 `127.0.0.1`；
3. 端口填写 `2299`。

FH6 的官方说明见 [Forza Horizon 6 Data Out Documentation](https://support.forza.net/hc/en-us/articles/51744149102611-Forza-Horizon-6-Data-Out-Documentation)。

如果同一台电脑也用于开发，可运行发行包里的 `Start-Isolated.cmd`。它会把发行版数据隔离到 `%LOCALAPPDATA%\LazyForza-Release`。

## 数据与更新

默认数据目录为 `%LOCALAPPDATA%\LazyForza`，其中包含设置、圈速、赛道、车辆学习、日志和录制。发行包不包含开发者的个人数据。

比赛自动录制默认关闭，可在设置中启用。达到容量上限时默认停止新录制而不删除文件；用户主动开启轮换后，程序仍会保留最近 5 场，并跳过手动固定、个人最佳圈和赛道识别异常样本。

启动检查更新默认开启，可在设置中关闭。发现新版本时 LazyForza 只会提示，不会强制更新；程序优先使用 GitCode，检查或下载失败时自动回退 GitHub。确认更新后会校验发行包 SHA-256 与包内清单，安装失败则恢复原版本。自定义 `--data-dir` 在更新后仍会保留。

## 本地构建

需要 Windows 10/11 x64 与 .NET SDK 9：

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

## 说明

- FH6 UDP 不提供赛事 ID、对手遥测或调校 ID；赛道与调校识别来自可观测遥测特征，因此仍需保守处理无法区分的情况；
- 紫色分段只表示 LazyForza 本地可见记录中的同性能等级最快，不代表在线世界纪录；
- 车型名称使用 [HDR 维护的 FH6 Car Ordinals](https://gist.github.com/HDR/0659d1717bc61504bf83750628963f4f) 的内置快照，运行时无需访问 GitHub；
- 项目由 OpenAI GPT-5.6 Sol 协助开发。

更多实现边界与实机验证项目见 [ARCHITECTURE.md](ARCHITECTURE.md) 和 [VALIDATION_WITH_FH6.md](VALIDATION_WITH_FH6.md)。

## License

[MIT](LICENSE)。LazyForza 是非官方社区项目，Forza、Forza Horizon、Xbox 及相关商标属于其各自权利人。
