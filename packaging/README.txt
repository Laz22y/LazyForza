LazyForza 开始使用
==================

系统要求
--------
Windows 10/11 x64。本发行包已经包含所需的 .NET 运行时，无须另外安装 .NET。

安装版与便携版
--------------
安装版会创建开始菜单入口，并关联 .lfztelemetry、.lfzlap 和 .lfzestate。
便携版需要完整解压 ZIP，不写入注册表、不创建开始菜单入口，也不注册文件关联。
两种版本功能和数据格式相同。

首次启动
--------
首次启动按指引选择语言、玩家代号、数据目录和关闭方式，再配置 FH6 Data Out。
收到有效 FH6 遥测后初始化完成，主窗口自动打开。完成后，后续更新不会重复显示初始化指引。

数据与隐私
----------
发行包不含制作者的设置、圈速、车辆学习、日志、录制或自定义赛道。
86 条 Playground 官方赛事属于程序内置只读数据，会在首次启动时自动建立。

数据目录可选择：
%LOCALAPPDATA%\LazyForza
%LOCALAPPDATA%\LazyForza-Release
程序目录下的 Data
自定义目录

LazyForza 只使用 FH6 官方 UDP Data Out，不读取游戏内存、不注入 DLL，也不修改游戏进程。

更新
----
“设置 > 应用更新”默认在每次启动时优先检查本项目 GitCode Release 的最新稳定版本。
GitCode 不可用或下载失败时，程序会自动回退到 GitHub 的同一正式版本。
发现新版后会先询问你，不会强制安装，也可关闭启动检查。
确认更新后，程序会自动下载、校验、替换并重启；失败时恢复原版本。
你的设置、圈速、车辆学习、日志和录制均保存在独立数据目录，不会被更新包覆盖。

开发机隔离
----------
普通用户无需使用 Start-Isolated.cmd。

如果这台电脑同时用于 LazyForza 开发，运行 Start-Isolated.cmd 可将发行版数据保存到：
%LOCALAPPDATA%\LazyForza-Release

这样发行版不会读取或修改开发版默认目录中的数据库、设置、日志和录制文件。

说明
----
本发行包目前未使用代码签名证书。Windows 首次运行时可能显示来源未知提示。
请只使用可信来源获得的压缩包，并核对随包提供的 SHA-256 校验值。

Forza、Forza Horizon、Xbox 及相关商标属于其各自权利人。
LazyForza 不是 Playground Games、Turn 10、Xbox 或 Microsoft 的官方产品。
