# Third-party notices

SQLite 使用 Windows 自带 `winsqlite3.dll`。自定义无线电音频的本地解码与重采样使用
`NAudio.Wasapi` 3.1.0 及其依赖 `NAudio.Core` 3.1.0，通过 Windows Media Foundation
处理用户选择的音频文件。两者采用 MIT License；未修改其源码。

项目来源：https://github.com/naudio/NAudio
许可证来源：https://github.com/naudio/NAudio/blob/0aaef29d04bec9567bdf2f669036fabecc33a2e2/LICENSE

Copyright 2008-2026 Mark Heath

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

自包含发行包会再分发 Microsoft .NET 和 Windows Desktop Runtime。发行包同时包含
`DOTNET_LICENSE.txt` 与 `DOTNET_THIRD_PARTY_NOTICES.txt`，内容直接取自用于发布的本机
.NET SDK，不作修改。

自动测试固定使用以下 Microsoft 包：

- `Microsoft.NET.Test.Sdk` 17.14.1；
- `MSTest.TestFramework` 3.9.3；
- `MSTest.TestAdapter` 3.9.3。

这些包由 Microsoft 发布，采用其各自包内随附的许可证/通知；NuGet 还原后的许可证位于本仓库忽略的 `.packages` 缓存中。LazyForza 不复制或再分发这些包的源码。

车辆编号与英文车型名的内置快照来源于 HDR 维护的社区数据：

- `https://gist.github.com/HDR/0659d1717bc61504bf83750628963f4f`
- 文件：`Forza Horizon 6 Car Ordinals.json`
- 当前内置修订：`edd5ac8dbb000c024cd2c6359140feb21d609ba9`
- 快照只包含车辆编号与名称映射，并附带来源、作者、修订和更新时间元数据。

该数据不是 Playground Games 或 Xbox 的官方资料。LazyForza 在运行时不会联网抓取此
Gist；维护者仅通过 `scripts/Update-VehicleNameCatalog.ps1` 显式生成、审核和提交新的离线
快照。本项目的 MIT License 不改变该社区数据来源及其作者所保留的权利。

Forza、Forza Horizon、Xbox 和相关商标属于其各自权利人。LazyForza 不是官方 Forza 产品，HUD 参考不包含 Forza/Xbox 商标。
