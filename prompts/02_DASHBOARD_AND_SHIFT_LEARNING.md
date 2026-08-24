# 阶段 2：仪表盘与自动换挡学习

> **Status: Historical**
> 本阶段已经结束，内容只用于追溯早期实现目标。不要把其中参数或任务清单当作当前规范。现行开发入口见 [`../AGENTS.md`](../AGENTS.md)。

```text
继续当前 LazyForza 工程，完成 Dashboard 模块和自动换挡学习。不要重新搭一套平行架构，不要破坏阶段 1 的模块契约、TelemetryFrame、录制回放与测试。

开始前完整阅读并核对实际代码：
- ../FH6_TELEMETRY_DEVELOPMENT_GUIDE.md，尤其第 3、4、5.2、5.4、8、9、10 节；
- ../design/FH6_TELEMETRY_DASHBOARD_SPEC.md；
- ../design/FH6_TELEMETRY_DASHBOARD_DESIGN_V2_TRANSPARENT.png；
- 00_MASTER_IMPLEMENTATION_PROMPT.md；
- 阶段 1 ADR、README、测试和 Overlay Spike 结果。

必须实现真实矢量 HUD，不得把 PNG 整张贴图当动态仪表盘。

Dashboard 视觉与数据绑定
======================

- 宽主结构弧与其上方严格平行的分段 RPM/红区细弧；
- 左圆：Gear + round(Speed × 3.6) km/h；
- 右圆：CurrentEngineRpm、Power/1000 kW、Torque N·m；圆环随 RPM 连续由石墨向红色过渡；
- 左下四个无胎纹胶囊，绑定四轮 TireTemp 与 gripUi = clamp(1 - abs(TireCombinedSlip), 0, 1)；明确 gripUi 是 UI 推导；温度单位未实测前只显示数值/°，不写 C/F；
- 中下左 Brake 深红 #8B1E2D，右 Accel 深绿 #0B6B43；填充高度 raw/255；
- 油门内部亮带循环约 1.2s，Accel=0 暂停，可按油门深度在 1.6s..0.65s 范围映射；遵循减少动态；
- Class/PI 双段徽章及 D/C/B/A/S1/S2/R/X token、范围全部来自设计规格；
- 透明、置顶、可切换鼠标穿透、无焦点；支持显示器、位置、缩放、不透明度、Lock Layout；
- 断流显示 Stale/Disconnected，不保留最后数值冒充实时；
- UI 以最多 60 Hz 使用最新快照，分析算法不使用平滑后的显示值。

自动学习状态机
==============

实现 VehicleProfileFingerprint 与：NotStarted -> Collecting -> Ready / Insufficient / Stale / Error。

采样门槛必须可配置且有保守默认值：
- Accel 高于约 90%；
- Speed 足以避免 RPM/Speed 数值病态；
- Gear 稳定且有效；
- 非离合器/换挡过渡；
- 驱动轮 TireSlipRatio/TireCombinedSlip 未明显失控；
- 无位置跳变、跳跃、碰撞和异常时间间隔；
- EngineMaxRpm 与当前车辆档案一致。

发动机曲线：
- 以 100–200 RPM 桶收集 Power、Torque、Boost；
- 同桶使用中位数或鲁棒统计；
- 保存样本数、覆盖范围、离散度和置信度；
- 只在连续覆盖足够 RPM 范围后进入 Ready；
- 支持多次拉转合并，但不能让旧车辆/旧调校污染。

挡位模型：
- 每挡在低滑移稳定区拟合 K_i = RPM / Speed；
- 结合真实换挡前后 RPM 跌落校验 G_next/G_i；
- 每个相邻挡位独立保存比值、样本数和置信度；
- 自动变速导致缺少可控样本时显示限制，不编造结果。

最佳点：
- n_after = n × G_next/G_i；
- 比较 T(n)×G_i 与 T(n_after)×G_next，或同速 P(n) 与 P(n_after)；
- 插值求每一对挡位的交点；
- 限制器前无交点时取安全红线前；
- cueRpm = targetRpm - rpmRiseRate × learnedLatency；
- 允许分别显示 Theory Target、Measured Confidence、Cue RPM；
- 当前处于 cue 区时 RPM 弧给出清晰但克制的换挡状态；
- 低附着/高滑移时可标记理论点不可靠，不自动把干地模型当全场景真值。

VehicleProfileFingerprint 至少考虑 CarOrdinal、Class、PI、Drivetrain、Cylinders、MaxRPM、各挡 K_i 和曲线摘要。显著变化使模型 Stale 并提示重新学习；不能只依赖 CarOrdinal。

主程序页面
==========

Vehicle/Shift Learning 页面显示：
- 当前车辆指纹与模型状态；
- 学习说明和安全提示；
- RPM 覆盖进度/桶样本热度；
- 功率/扭矩曲线；
- 每挡 K_i 与置信度；
- 1->2、2->3... 的 Target RPM、Cue RPM、换挡后 RPM；
- Reject reason 统计（滑移、换挡、低速、断流等）；
- Reset/Relearn，操作前确认并只删除当前配置模型；
- Live 与 Replay 明确区分。

测试与验收
==========

- 使用合成功率/扭矩曲线和已知齿比验证每挡交点；
- 验证红线前无交点、曲线缺口、数据不足；
- 验证滑移/离合/换挡/低速过滤；
- 验证车辆配置变化导致 Stale；
- 验证 rpmRiseRate/latency 对 Cue RPM；
- 使用 replay 贯通 Telemetry -> Learner -> Store -> Dashboard；
- 做 HUD 截图或渲染基准检查两圆并排、左右刹车/油门、四轮胶囊、弧线、等级颜色；
- build/test 全部通过；
- 更新 README 和 VALIDATION_WITH_FH6.md，列出需要真实拉转验证的项目。
```
