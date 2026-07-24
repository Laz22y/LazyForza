# 阶段 4：Fluent UI、整体验收与交付

```text
对当前 LazyForza 实现进行最后集成、UI 完成度、性能、可靠性和交付验证。此阶段不是只做“美化”：必须检查所有开关、数据管线、存储、Replay 和算法是否真正接线。

先完整阅读：
- 00_MASTER_IMPLEMENTATION_PROMPT.md；
- ../FH6_TELEMETRY_DEVELOPMENT_GUIDE.md；
- ../design/FH6_TELEMETRY_DASHBOARD_SPEC.md 和 V2 PNG；
- 所有 ADR、README、VALIDATION_WITH_FH6.md；
- 阶段 1–3 的实际源码和测试结果。

1. 模块与生命周期验收
====================

- Dashboard 与 LapAnalysis 分别开/关；
- 四种组合全部运行：都关、仅 Dashboard、仅 Lap、都开；
- Lap HUD 在两者都开时准确位于 Dashboard 弧线上方；
- 仅 Lap 时独立锚点；
- 开关真实释放/恢复订阅、后台任务和 HUD contribution；
- 重复启停无泄漏、异常或重复事件；
- 应用重启保持设置；
- 模块异常显示 InfoBar/诊断，不导致整个应用崩溃。

2. Fluent 主程序
===============

- NavigationView 信息架构完整；
- Overview 显示连接状态、当前车、当前赛道/候选、当前圈、模块快捷开关；
- Modules 页面显示描述、版本、状态、错误和设置入口；
- Dashboard/Shift、Lap Analysis、Tracks、Settings、Diagnostics 页面均为真实数据；
- 深色主题为默认并支持系统主题；
- 主窗口可使用 Mica，卡片层级克制；
- 使用统一 Typography/Spacing/CornerRadius/Color tokens；
- 表格和图表大数据量虚拟化/降采样；
- 空状态、错误、首次学习、数据不足和 Stale 文案清楚；
- 键盘导航、焦点可见、屏幕阅读名称、文本缩放和高对比度基本可用；
- 所有动画遵循减少动态。

3. Dashboard 视觉核对
===================

逐项对照 design/FH6_TELEMETRY_DASHBOARD_SPEC.md：
- 两圆并排；
- 挡位/速度左、RPM/kW/N·m 右；
- 平行双弧；
- 四轮无胎纹胶囊；
- 左深红 Brake、右深绿 Throttle；
- 油门循环亮带；
- R/X 等完整等级 token；
- 透明外部、石墨面板、高级但极简；
- 1280×720、1920×1080、2560×1440 以及 100/125/150/200% DPI 无裁切；
- 数字宽度稳定；
- Stale/Disconnected 明显。

不要追求像素硬编码；优先保证几何比例、信息层级和缩放稳定。保存若干自动化/人工截图到 docs/qa/，注明分辨率和数据来源为 Demo。

4. Lap HUD 和分析页核对
======================

- 分段弧与仪表盘同心；
- 长度比例、缝隙、当前段提示正确；
- 灰/黄/绿/紫状态准确，紫色 tooltip 为“当前数据集全场最佳”；
- 无对手数据时不出现“在线世界纪录”措辞；
- 首次学习全部灰色并显示学习状态；
- 分析页能选择真实保存的 2–4 圈，图表/分段表/路线图联动；
- 无效圈原因可见且不参与纪录；
- SectorSchemaVersion 不一致时阻止误比较并解释原因。

5. 性能和可靠性
===============

- 在模拟 30/60/120/144 Hz 包率下持续运行；
- UI 最多 60 Hz，接收线程不被 UI/SQLite 阻塞；
- 记录 Channel 深度、丢弃策略和内存增长；
- 30 分钟 replay soak test 无持续增长的 Task、订阅、窗口或内存；
- 断网/端口占用/权限错误/数据库只读/磁盘写失败有可理解错误；
- 应用退出正确关闭 socket、flush 会话并取消后台任务；
- 日志滚动且不逐帧刷爆；
- 对损坏 replay/数据库提供安全失败，不崩溃。

6. 测试和交付
=============

- 运行 clean、restore、build、test；
- 运行 simulator/replay 的端到端场景；
- 若可用，运行 UI smoke test；
- 检查发布产物能在目标 Windows 环境启动；
- README 从零说明 SDK、构建、运行、FH6 配置、模块开关、Overlay 解锁、路线学习、换挡学习和数据目录；
- ARCHITECTURE.md 说明模块边界和数据流；
- VALIDATION_WITH_FH6.md 将已实测与未实测严格分开；
- THIRD_PARTY_NOTICES/许可证按依赖要求补齐；
- 不提交临时录屏、巨大原始会话、构建输出或本机路径。

最终报告必须给出：
- 已完成的可运行功能；
- 技术选型和 Overlay 实现；
- build/test/soak 的实际命令与结果；
- Demo/Replay 验证路径；
- 尚需用户在真实 FH6 中验证的具体清单；
- 关键文件链接；
- 不得把未运行的测试写成通过。
```
