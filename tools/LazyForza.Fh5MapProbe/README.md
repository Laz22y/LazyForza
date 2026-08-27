# FH5 地图坐标采集工具

这个临时工具用于分别采集 Forza Horizon 5 墨西哥主地图、Hot Wheels Park 和 Sierra Nueva 的官方 Data Out 坐标，以判断不同地图的坐标范围是否重叠及其跨会话稳定性。

## 使用方法

1. 退出 LazyForza 主程序，启动本工具。
2. 在 FH5 的 HUD 与游戏设置中打开 Data Out，将目标 IP 设为工具窗口给出的本机地址，并使用相同端口。
3. 在工具中选择当前地图和采集批次，开始采集。
4. 完整驾驶一段覆盖地图不同区域的路线；停车后记录入口、中心、边缘、高点和低点等地标。
5. 点击“停止并保存”，每张地图和每次重启分别生成一份 `.fh5mapcapture` 文件。

建议三张地图各采集至少三轮，并在完全退出游戏后重复同一批地标。地图名称由测试者手动选择；Data Out 本身不提供可依赖的 FH5 地图标识。

## 输出内容

`.fh5mapcapture` 是 ZIP 容器，包含：

- `manifest.json`：地图、批次、包长分布、解析统计和三维坐标范围；
- `frames.csv`：每个有效包解析出的时间、位置、速度、姿态和车辆字段；
- `markers.csv`：人工记录的稳定地标；
- `raw-packets.bin`：所有原始 UDP 包，可在字段布局需要修正时重新解析。

工具接受 323 和 324 字节的 FH5 Horizon Dash 包。布局采用小端序：标准 Sled 段结束于偏移 231，Horizon 扩展位于 232–243，`PositionX/Y/Z` 位于 244/248/252。解析器会比较 `Speed` 与三轴 `Velocity` 的模长，防止错误偏移静默生成坐标。

协议基础参考 [Forza 官方 Sled/Dash Data Out 字段说明](https://support.forzamotorsport.net/hc/en-us/articles/21742934024211-Forza-Motorsport-Data-Out-Documentation)。FH5 的 12 字节 Horizon 扩展和 323 字节尾部长度另以 [Forza 官方社区中的 FH5 格式调查](https://forums.forza.net/t/data-out-telemetry-variables-and-structure/535984/4)及其列出的接收器实现交叉核对。由于 FH5 没有同等级的官方 Horizon 扩展字段表，原始包始终随结果保留。
