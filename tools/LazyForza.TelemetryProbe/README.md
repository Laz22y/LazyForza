# TelemetryProbe：本机遥测开发测试工具

使用普通 Windows 电脑即可生成、接收和逐字节检查 FH6 格式的 UDP 包，不需要游戏、方向盘、仪表或其他遥测设备。工具独立运行，不进入客户端发行包。

客户端在「设置 → 遥测与录制 → 遥测转发」提供原始 324 字节转发。`verify` 可接到实际客户端的 Live 入口和转发目标，逐字节验证整条本机路径。

## 构建与快速自检

在仓库根目录的 PowerShell 中运行，使用仓库固定的 .NET SDK：

```powershell
dotnet build tools/LazyForza.TelemetryProbe/LazyForza.TelemetryProbe.csproj -c Release
dotnet tools/LazyForza.TelemetryProbe/bin/Release/net9.0/LazyForza.TelemetryProbe.dll self-test --count 120 --hz 60 --grace 1
```

`self-test` 使用随机本机回环端口直接收发，报告的 `Scope` 为 `tool-loopback-only`。它检查工具自身，不能证明客户端转发或真实 FH6 已通过验证。

## 模式

下列命令均在仓库根目录执行。地址为 IP 字面量，IPv6 使用 `[::1]:2300` 形式。

### 发包与独立收包

先在一个终端启动接收端，再在另一个终端发送：

```powershell
dotnet tools/LazyForza.TelemetryProbe/bin/Release/net9.0/LazyForza.TelemetryProbe.dll receive --listen 127.0.0.1:2300 --seconds 15
dotnet tools/LazyForza.TelemetryProbe/bin/Release/net9.0/LazyForza.TelemetryProbe.dll send --target 127.0.0.1:2300 --count 600 --hz 60
```

`receive` 统计收到的包数、错误长度和收包频率，不推断未知来源的丢包量，也不把收到 324 字节自动判为转发完整。`send` 成功只表示提交给本机 UDP socket，不证明对方收到了。

合成包使用现有 `Fh6PacketBuilder`，保持 324 字节布局。时间戳用于关联期望包，第 323 字节会变化以检查透明转发；它仍是保留字节，没有新增游戏字段语义。

### 验证实际转发路径

先用独立 `--data-dir` 启动客户端的 Live 模式，在「设置 → 遥测与录制」将监听配置为 `127.0.0.1:2299`，开启「遥测转发」并设目标为 `127.0.0.1:2300`，再运行（如果端口被占用，可同时修改设置和命令）：

```powershell
dotnet tools/LazyForza.TelemetryProbe/bin/Release/net9.0/LazyForza.TelemetryProbe.dll verify --target 127.0.0.1:2299 --listen 127.0.0.1:2300 --count 600 --hz 60 --grace 3 --report artifacts/forward-check.json
```

流程为“工具发包 → 被测程序入口 → 被测程序出口 → 工具收包”。工具先绑定接收端再发送；入口与出口不能使用同一端点，也不会暗中启动中间转发器。只收到包但内容变化、发生丢失、重复、乱序或混入其他包，都不会判为通过。

`Missing` 统计未收到完全一致副本的已发送包，因此被篡改的包也计入缺失；`Modified`、`Duplicates`、`OutOfOrder`、`Unexpected` 提供原因。`ReceivedHz` 按首末收到的包计算，不含结束等待时间。延迟使用同一进程的单调时钟，是整条本机测试路径的收发耗时，不是对外设或跨设备单向延迟的测量。

如果向 LazyForza 的 Live UDP 入口发合成数据，务必先使用独立的 `--data-dir` 启动测试实例，停止真实游戏 Data Out，并关闭不需要的计圈／学习功能，避免合成数据写入日常圈记录和车辆档案。不要与日常客户端争用同一监听端口。

### 回放原始录制

```powershell
dotnet tools/LazyForza.TelemetryProbe/bin/Release/net9.0/LazyForza.TelemetryProbe.dll send --target 127.0.0.1:2300 --recording C:/path/session.lfztelemetry
```

只接受原始遥测录制，按录制的到达时间以 1 倍速发送，保留全部原始字节。此模式发送整个录制，`--hz` 与 `--count` 不调整录制；单圈分析导出即使扩展名相同也会拒绝。`verify` 使用确定性合成数据，不接受 `--recording`。

## 输出与边界

- `--report` 写 JSON，已存在的文件不会覆盖；请为每次测试选择新文件名，父目录须已存在。
- 退出码 `0` 表示操作完成，校验模式还要求 `Passed: true`；`1` 表示校验发现问题；`2` 表示参数、执行错误或用户取消。发包／独立收包模式的 `Passed` 为 `null`。
- Ctrl+C 取消并在终端输出已有统计；取消时不写最终报告文件。
- 合成发送支持 1–500 Hz、1–100000 包；独立接收限时 1–3600 秒，结束等待 1–30 秒。计时受 Windows 调度影响，不用于严格实时性能认证。
- 报告不记录 IP、用户路径或包正文。网络测试需要进程正常的本机 UDP 权限；受限执行沙箱可能阻止回环收包。

工具不能替代真实 FH6、真实设备协议、设备反馈、电机／灯光／仪表显示及跨网络性能验证。开发验证记录见 [直连改进交接](../../docs/development/ESTATE_PEER_CONNECTIVITY_PLAN.md#9-本轮实现与验证记录)。
