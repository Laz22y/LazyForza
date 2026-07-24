# 阶段 1：技术选型、模块底座与遥测核心

```text
在当前 LazyForza 仓库中直接完成阶段 1。不要只输出计划；创建工程、代码、测试和文档并实际运行验证。

开始前完整阅读：
- ../FH6_TELEMETRY_DEVELOPMENT_GUIDE.md
- ../design/FH6_TELEMETRY_DASHBOARD_SPEC.md
- 00_MASTER_IMPLEMENTATION_PROMPT.md
- FH6 官方 Data Out 文档

先检查当前目录、dotnet/Visual Studio/Windows SDK 环境和已有修改。不要覆盖无关文件。

本阶段结果
==========

1. 完成 docs/adr/ADR-0001-technology-stack.md：
   - 比较 C#/.NET、C++、Rust；
   - 默认选择 C# + .NET 10 LTS；
   - 主 UI 优先 WinUI 3 + 稳定 Windows App SDK；
   - 实现透明 Overlay Spike 并记录逐像素透明、置顶、鼠标穿透、无焦点、60 Hz、高 DPI/多屏结果；
   - 若 WinUI Overlay 不可行，以证据选择 Win32/Composition 或 WPF Layered Window Overlay，但保持业务核心 UI 无关。

2. 创建可构建解决方案与分层项目：Domain、Telemetry、Analysis、Storage、Modules.Abstractions、Dashboard、LapAnalysis、Overlay、App 与测试项目。若模板/SDK限制导致名称调整，在 ADR 解释。

3. 模块底座：
   - ILazyForzaModule、ModuleDescriptor、ModuleState、IModuleContext；
   - 幂等 Initialize/Start/Stop、CancellationToken、依赖检查、失败隔离；
   - 设置持久化；
   - Dashboard 和 LapAnalysis 用相同 ModuleCatalog 注册，即使此阶段只有占位实现，也必须真实启停并有测试；
   - 禁止全局静态服务定位器。

4. FH6 遥测：
   - 严格 324 字节；解析官方偏移 0..322，保留 byte 323；
   - 使用显式 little-endian 读取和合理性验证；
   - 不通过不安全结构体强转依赖 CLR padding；
   - UdpReceiver 默认 5301，地址/端口可配置；
   - 可变帧率、超时、丢包/重复/乱序估计、TimestampMS 回绕；
   - 不可变 TelemetryFrame，保留 Raw 与 Normalized；
   - bounded channel/ring buffer，HUD latest-wins，录制通道有背压策略；
   - StreamStateMachine 不能把所有断流都解释为完赛。

5. 录制与回放：
   - 版本化 .lfztelemetry 文件头；
   - 保留包到达时间、原始 324 bytes 与会话元数据；
   - 可按原始时间或加速倍率回放；
   - replay 来源在 UI/状态中明确标记，绝不混同 Live。

6. SQLite 基础：
   - SchemaVersion/migration；
   - AppSettings、ModuleSettings、Sessions 基础表；
   - 数据目录服务；
   - 事务、外键和临时数据库测试。

7. Fluent 主程序骨架：
   - NavigationView：Overview、Modules、Lap Analysis、Tracks、Vehicle/Shift Learning、Settings、Diagnostics；
   - Modules 页面开关必须驱动真实生命周期；
   - Diagnostics 显示包速率、计数、监听地址、最后包时间、Live/Replay/Disconnected、模块状态；
   - 暂无业务内容的页面显示明确 empty state，不放假数据冒充完成。

8. 测试：
   - 建造固定 324 字节 fixture，覆盖关键偏移和类型；
   - 323/325 字节拒绝；
   - 端序/合理性；
   - TimestampMS 回绕；
   - 重复、乱序、断流；
   - 模块启停幂等与失败隔离；
   - replay 确定性；
   - SQLite 临时数据库隔离。

完成定义
========

- clean restore/build/test 成功；
- 主程序可启动并在 Module 页启停两个模块；
- Overlay Spike 可单独运行并记录验证结论；
- 可以用 simulator 产生合法包，经 Receiver/Replay/Parser 到 Diagnostics；
- README 写明 FH6 配置：127.0.0.1、默认 5301、避开 5200..5300；
- 建立 VALIDATION_WITH_FH6.md，列出字节序、温度单位、特殊 Gear、暂停/倒带/完赛等实测项；
- 报告实际执行的构建与测试命令，不声称未验证事项已完成。
```
