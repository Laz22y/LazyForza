LazyForza 开始使用
==================

系统要求
--------
Windows 10/11 x64。本发行包已经包含所需的 .NET 运行时，无须另外安装 .NET。

启动
----
1. 完整解压 ZIP，不能只把 EXE 单独拖出来。
2. 双击 LazyForza.App.exe。
3. 在 FH6 的“设置 > HUD 与游戏玩法”中启用 Data Out：
   地址：127.0.0.1
   端口：2299
4. 启动比赛后，LazyForza 会自动接收官方 UDP 遥测。

数据与隐私
----------
发行包不含制作者的设置、圈速、车辆学习、日志、录制或自定义赛道。
85 条 Playground 官方赛事属于程序内置只读数据，会在首次启动时自动建立。

你自己的数据默认保存在：
%LOCALAPPDATA%\LazyForza

LazyForza 只使用 FH6 官方 UDP Data Out，不读取游戏内存、不注入 DLL，也不修改游戏进程。

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
