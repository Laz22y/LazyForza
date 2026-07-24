# 阶段 3：赛道学习、分段 HUD 与逐圈分析

```text
继续当前 LazyForza 工程，完成 LapAnalysis 模块。先检查已有实现、阶段 1/2 的测试和数据库迁移；复用 Telemetry、Storage、Overlay 和模块契约，不创建第二套接收器或设置系统。

必须阅读：
- ../FH6_TELEMETRY_DEVELOPMENT_GUIDE.md 第 6、7、9、10、11 节；
- ../design/FH6_TELEMETRY_DASHBOARD_SPEC.md 的弧线、透明和布局要求；
- 00_MASTER_IMPLEMENTATION_PROMPT.md；
- 当前 ADR、Schema、Replay 和 Dashboard overlay layout contract。

数据事实限制
============

FH6 没有 TrackOrdinal、赛道名、官方检查点、对手位置或对手分段。不要发明字段。TrackTemplate 是由玩家有效圈学习出的参考路线/走廊，不是完整赛道边界。

领域模型与存储
==============

实现并持久化：
- TrackTemplate：Id、Name、Direction、Source、GameBuild、StartFinish、Tangent、BoundingBox、Length、Tolerance、Confidence、CaptureLapCount、Created/Updated；
- TrackPoint：x/y/z、s、tangent/heading、可选走廊统计；
- SectorDefinition：TrackId、SectorSchemaVersion、Index、StartS、EndS、FeatureType、算法版本；
- Session、LapRecord、LapSegment、LapSample；
- 有效性、失效原因、原始会话引用、车辆指纹、比较范围。

TrackTemplate 与 LapRecord 必须是一对多；更新个人最佳不能改变路线身份。数据库加索引和迁移，大量轨迹点/样本采用批量事务。

圈状态机
========

结合 LapNumber、CurrentLap、CurrentRaceTime、起终点平面穿越、移动方向、流连续性和位置连续性。

必须处理：
- 从发车格到第一次可靠过线的残缺圈；
- 正常起终点回绕；
- 暂停/菜单/回放/倒带导致断流；
- 重置/传送/位置跳变；
- 丢包、时间戳异常；
- 跑出路线/投影低置信；
- 自由漫游偶然经过起点；
- 正反向路线；
- 环形路线为 MVP 核心；点到点和 EventLab 可先正确标为受限/实验性，不得假装已可靠完成。

无效圈仍可保存用于诊断，但不参与 PB/全场最佳；记录具体 InvalidReason。

首次学习
========

- 提供 Start Track Learning；
- 等待一次可靠过线后才开始完整采集，再到下一次可靠过线结束；
- 按约 5 m 空间间距重采样，降低帧率差异；
- 平滑轨迹但保留原始引用；
- 计算 s、切向、曲率、起终点、包围盒；
- 一圈保存最低可用模板，2–3 圈更新走廊/置信度；
- 用户命名、重命名、删除、重新学习；
- UI 说明“学习的是参考路线，不是赛道边界”。

自动识别
========

Unknown -> Candidate -> Confirmed，并实现置信度：
- 包围盒/起点邻域预筛；
- 点到路线距离、方向夹角、Y 高度和进度连续性评分；
- 最佳候选需超过绝对阈值且显著优于第二名；
- 限制投影搜索在上次 s 附近并总体向前；
- 发卡弯、交叉点、立交桥不能无状态全局最近点；
- 位置跳变或置信度低时降级；
- 只有开始驾驶并积累轨迹后才确认。

自动分段
========

实现确定性、版本化算法：
1. 5 m 等距重采样/平滑；
2. 计算 heading delta / ds 曲率；
3. 从已有有效圈聚合稳定 Brake 上升和最低弯速特征；
4. 识别弯道/弯组/制动区，合并过短间隔；
5. 初始目标数 clamp(round(routeLength/350m), 4, 16)；
6. 边界优先几何/制动特征，同时约束最小/最大段长；
7. 特征不足则按距离均分；
8. 保存算法参数与 SectorSchemaVersion；
9. 新算法不能让旧圈静默错位：重投影、迁移或标为不可比。

实时 Delta 与分段
=================

- 将位置投影到 s；
- referenceTime=f(s) 插值；
- delta=currentLapElapsed-referenceTime(s)；
- 分段时间以边界交叉插值，不以最邻近帧粗切；
- 跑偏/低置信/传送时冻结或使圈无效；
- PB/Overall 比较限定同 TrackId、Direction、SectorSchemaVersion 和配置的比较范围。

颜色状态严格为：
- Gray：未跑、未完成、无参考或无效；
- Yellow：有效完成且比 PB 慢超过 max(0.15s,1%)；
- Green：个人最佳但不是当前数据集全场最佳；
- Purple：当前分析器可见的所有可比有效 LapRecord 中最快，优先级高于 Green。

紫色必须在 UI tooltip/说明中写“当前数据集全场最佳”。没有导入其他车手圈时，它只是本地数据集最快，不是在线世界纪录或 FH6 全场对手数据。

弧形 Lap HUD
============

- 位于 Dashboard 之上，与 Dashboard 主弧共享中心/曲率并保持平行；
- 每段沿弧分配，弧长与路线分段长度成比例；
- 分段间留一致细缝；
- 当前段用细描边/亮度提示，不破坏状态颜色；
- 未开始全部灰色，过段后即时落色；
- Dashboard 关闭时使用独立锚点/缩放继续工作；
- LapAnalysis 关闭时彻底停止 HUD contribution 和非共享后台工作；
- 透明、置顶、可穿透、60 Hz 以下刷新；状态更新按事件而非逐帧重建所有几何。

主程序 Lap Analysis 页面
=======================

实现可用而非静态的 Fluent 界面：
- Session/Lap 列表与赛道、车辆、日期、有效性筛选；
- 每圈总时间、PB、有效性、失效原因、车辆档案；
- 选择 2–4 圈比较；
- 分段表：时间、PB、Overall、Delta、状态色；
- 路线距离横轴图：Speed、RPM、Gear、Accel、Brake、Delta；
- 简化轨迹折线图，按分段/Delta 着色；
- 点击段显示刹车起点、最低速度、给油点、最大滑移和主要时间损失；
- First-run 引导、学习进度、候选路线和匹配置信度；
- Tracks 页面支持命名、方向、长度、圈数、置信度、重新学习和删除确认；
- 大列表虚拟化，图表使用降采样/LOD。

测试
====

- 完整圈与残缺首圈；
- 起终点有方向的穿越；
- 正反向；
- Timestamp/断流/重置导致无效；
- 5m 重采样和 s 单调；
- 发卡、交叉、立交桥高度的受约束投影；
- Candidate/Confirmed 阈值和第二候选差距；
- 自动分段输出确定、长度合规、fallback 可用；
- SectorSchemaVersion 不混比；
- 边界时间插值；
- Gray/Yellow/Green/Purple 及 Purple 优先级；
- SQLite 往返；
- replay 完成一次学习、第二次自动识别、两圈对比和 HUD 更新。

完成后运行 build/test，更新 README、架构文档和 VALIDATION_WITH_FH6.md，明确环形赛已实现范围以及点到点/EventLab 的实际状态。
```
