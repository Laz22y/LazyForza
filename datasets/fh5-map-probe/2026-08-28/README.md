# FH5 三地图原始坐标采集数据（2026-08-28）

本目录保存通过 [FH5 地图坐标采集工具](../../../tools/LazyForza.Fh5MapProbe/README.md) 采集的三份原始数据，分别对应墨西哥主地图、Hot Wheels Park 和 Sierra Nueva。来源为 Forza Horizon 5 的 Data Out UDP 输出；地图名称在采集时手动选择。三份文件均为“第 1 轮”，工具版本为 `0.1.0`，容器 schema 为 `1`。

文件按原始采集包归档，未重新压缩或修改内容，可用于坐标研究、地标对照、解析器回归及重新解析原始包。这些文件属于 FH5 研究数据；FH6 的字段及实机结论应使用对应游戏的数据另行核对。

## 文件与采集规模

以下起止时间为北京时间（UTC+8），日期均为 2026-08-28；包内 `receivedAtUtc`、`capturedAtUtc` 和 manifest 原始时间使用 UTC。

| 文件 | 地图 | 起止时间（UTC+8） | 采集时长 | 总包数 / CSV 数据行数 | 驾驶状态包数 | 地标数 | 文件大小（字节） |
| --- | --- | --- | --- | ---: | ---: | ---: | ---: |
| [FH5-Mexico.fh5mapcapture](FH5-Mexico.fh5mapcapture) | 墨西哥主地图 | 17:50:58–17:55:43 | 285.404 秒 | 21,632 | 7,845 | 10 | 2,766,504 |
| [FH5-Hot-Wheels.fh5mapcapture](FH5-Hot-Wheels.fh5mapcapture) | Hot Wheels Park | 17:59:48–18:02:24 | 155.800 秒 | 12,068 | 3,300 | 5 | 1,126,303 |
| [FH5-Sierra-Nueva.fh5mapcapture](FH5-Sierra-Nueva.fh5mapcapture) | Sierra Nueva | 18:04:01–18:05:59 | 117.479 秒 | 8,964 | 3,236 | 5 | 1,028,546 |

三份数据共 42,664 个包，其中驾驶状态包为 14,381 个，人工地标共 20 个。三份 manifest 均记录 `invalidPackets=0`，收到的包均为 324 字节；这里的“有效包”指通过工具解析检查，不等于全部处于驾驶状态。“驾驶状态包”仅按 `isRaceOn=1` 筛选，也包含车辆静止时的数据。

## 包内文件

`.fh5mapcapture` 是 ZIP 容器，可直接使用 ZIP 软件打开，或通过标准库读取，无需启动游戏。

| 文件 | 内容与用途 |
| --- | --- |
| `manifest.json` | 游戏、手动选择的地图、采集批次、起止时间、监听地址和端口、包长分布、解析统计、驾驶坐标包围盒及地标数。 |
| `frames.csv` | 每个成功解析的包对应一行，保存接收时间、游戏时间戳、状态、位置、速度、姿态、车辆及驾驶输入。包括暂停或菜单状态下的已解析包。 |
| `markers.csv` | 人工记录的地标名称、UTC 时间、三轴坐标中位数、采样数、离散范围与平均车速。 |
| `raw-packets.bin` | 完整原始 UDP 包及接收时间，用于重新解析、字段偏移核查和协议回归。 |

## 可直接提取的信息

| 信息 | 主要字段 | 用途 |
| --- | --- | --- |
| 三维位置与高度变化 | `positionX`、`positionY`、`positionZ` | 绘制 X/Z 平面点云、三维轨迹及 Y 高度曲线，统计已采样区域的坐标范围。 |
| 时间与驾驶状态 | `sequence`、`receivedAtUtc`、`timestampMs`、`isRaceOn` | 区分采集顺序、接收间隔、游戏时间变化与驾驶/非驾驶片段。序号代表成功解析包的顺序。 |
| 速度与运动方向 | `velocityX/Y/Z`、`speedMps`、`velocityMagnitudeMps`、`speedDeltaMps` | 查看速度变化、核对 Speed 与速度向量模长的一致性；`speedMps × 3.6` 可转换为 km/h。 |
| 车辆姿态 | `yaw`、`pitch`、`roll` | 对照车辆朝向、俯仰和侧倾与位置变化。 |
| 驾驶输入 | `accel`、`brake`、`gear`、`steer` | 对照油门、制动、挡位和转向与驾驶轨迹。CSV 保留原始输入值，油门/制动可除以 255，转向可按原始有符号字节归一化。 |
| 车辆身份与规格 | `carOrdinal`、`carClass`、`performanceIndex`、`drivetrainType`、`numCylinders` | 按车辆编号、性能等级、PI、驱动形式及气缸数分组。车辆名称需另用车辆编号名称库对应。 |
| 游戏计时与进度字段 | `distanceTraveledMeters`、`currentLapSeconds`、`currentRaceSeconds`、`lapNumber`、`racePosition` | 查看当时游戏输出的计时及进度值；菜单或自由驾驶中的零值应结合状态解释。 |
| 人工地标 | `name`、`x/y/z`、`capturedAtUtc` | 将前哨站、嘉年华和人工标记的方位与游戏坐标对照。 |
| 地标质量 | `spreadMeters`、`sampleCount`、`meanSpeedMps` | 判断该地标是否在停车状态采集，以及附近一秒样本的分散程度。 |

`carCategory`、`horizonUnknown1` 和 `horizonUnknown2` 也按当时解析器的字段名保存在 CSV 中。对这些 Horizon 扩展字段的进一步解释，应回到原始字节和 FH5 资料核对，不能直接当作地图或赛事标识。

`markers.csv` 的坐标来自记录时最近一秒、`isRaceOn=1` 的样本，分别计算 X/Y/Z 中位数；`spreadMeters` 为样本到该中位数坐标的最大三维距离。部分地标在车辆移动时记录：例如墨西哥“西 09”的 `spreadMeters` 约为 9.594 m、平均速度约为 18.651 m/s。研究固定坐标时应同时检查车速和离散范围，并按用途筛选地标。

## 本次采样的坐标范围

以下范围取自 manifest 的 `activeCoordinateBounds`，只统计 `isRaceOn=1` 的成功解析包，数值保留三位小数，单位为游戏坐标的米制数值。

| 地图 | X 范围 | Y 范围 | Z 范围 |
| --- | --- | --- | --- |
| 墨西哥 | -9,760.313 ～ 5,920.020 | 101.857 ～ 438.100 | -4,252.890 ～ 5,666.960 |
| Hot Wheels Park | -2,530.249 ～ 2,670.421 | 1,539.203 ～ 1,749.385 | -2,910.390 ～ 2,690.481 |
| Sierra Nueva | -1,859.120 ～ 1,824.990 | 125.188 ～ 382.412 | -2,481.944 ～ 2,417.340 |

本次采样中，Hot Wheels Park 的 Y 范围与另外两张地图明显分离；墨西哥与 Sierra Nueva 的三轴包围盒存在重叠。可据此研究地图候选筛选和高度特征，但包围盒重叠不证明实际轨迹点重合，单轮数据也不足以确定全地图范围或跨重启坐标稳定性。

地图名称属于采集时填写的标签。三份 manifest 的 `notes` 仍是工具默认提示，未填写游戏版本、进入方式及具体路线；当前数据不能据此确认版本或跨会话重复结果。地标中的东、西、南、北同样为人工名称。

绘制轨迹或估算行驶距离时，应先过滤非驾驶包，再按接收间隔、游戏时间戳及位置跳变分段，避免把暂停、切图或传送两侧的点连接为一条连续道路。此处包围盒表示采样覆盖范围，不能代替道路网络、赛道边界或游戏内检查点数据。

## 从原始包重新解析

`raw-packets.bin` 采用小端序，布局如下：

1. 文件头：8 字节 ASCII `LF5RAW01`。
2. 后续重复记录：`Int64` 接收时间 UTC ticks、`Int32` 包长度、对应长度的原始 UDP 字节。
3. ticks 使用 .NET `DateTime` 的单位与起点：从公元 0001-01-01 00:00:00 UTC 起的 100 ns 计数，不能直接当作 Unix 时间戳。

这批包的坐标是偏移 244/248/252 的三个 `Float32`，速度位于偏移 256。原始包还保留 CSV 未输出的字段，例如偏移 8 的最高转速与偏移 16 的当前转速，可通过 [FH5 解析器](../../../tools/LazyForza.Fh5MapProbe/Fh5DataOutParser.cs) 重新提取。FH5 Horizon 扩展、尾部及未知字段应按该游戏的布局核对。

## 读取示例

在仓库根目录执行以下 Python 3 代码即可读取包内摘要、地标和前五个驾驶坐标，使用的模块均属于标准库：

```python
import csv
import io
import json
from pathlib import Path
from zipfile import ZipFile

root = Path("datasets/fh5-map-probe/2026-08-28")
for path in sorted(root.glob("*.fh5mapcapture")):
    with ZipFile(path) as package:
        manifest = json.loads(package.read("manifest.json"))
        frames = csv.DictReader(io.StringIO(package.read("frames.csv").decode("utf-8-sig")))
        markers = list(csv.DictReader(io.StringIO(package.read("markers.csv").decode("utf-8-sig"))))
        coordinates = [
            (float(row["positionX"]), float(row["positionY"]), float(row["positionZ"]))
            for row in frames if row["isRaceOn"] == "1"
        ]
        print(path.name, manifest["region"], len(coordinates), "driving samples")
        print("bounds:", manifest["activeCoordinateBounds"])
        print("markers:", [row["name"] for row in markers])
        print("first coordinates:", coordinates[:5])
```

## 完整性校验

归档时已检查 ZIP CRC、manifest/CSV/原始包数量一致性、驾驶坐标范围，并逐包核对了 `raw-packets.bin` 与 CSV 中的时间戳、状态和三轴位置。复制前后的文件 SHA-256 一致：

```text
e92e4439bd3430ec36ea7063c744a356382ec6ff936f4f32bb4c47230bc6e79d  FH5-Mexico.fh5mapcapture
e93e7a6fd1306c1a25b1fc1df23d2a66bba91f423bfd57891407f3dda564db34  FH5-Hot-Wheels.fh5mapcapture
6eb6873b2af5baad900a6b6ed7c43a2ac55cf85242918e74a28a5bde941cd13a  FH5-Sierra-Nueva.fh5mapcapture
```
