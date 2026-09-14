# 保留的 HUD 面板样式

`src/LazyForza.Overlay/HudSurface.TimingPanels.cs` 保留三个独立绘图组件。它们不在主题目录中，当前 HUD 继续使用经典样式。

| 组件 | 绘图入口 | 保留的设计 |
| --- | --- | --- |
| PIT WINDOW | `DrawTimingPanelPitWindow` | 窗口圈数、距离窗口圈数与近期退化信息；青色／黄色状态强调 |
| PIT STOP | `DrawTimingPanelPitStop` | 排名、车手名、服务状态和计时分列；共享基线、进度条与行过渡 |
| 处罚指示器 | `DrawTimingPanelPenalty` | 左侧处罚数值、右侧标题与说明；处罚强调色及执行进度 |

三个组件沿用原尺寸比例、色值、排版和动态逻辑，公共底板与文字绘制分别在 `TimingPanelBackground`、`TimingPanelText`。文字使用 `HudTypography` 共享基线、限定列宽并省略过长内容；底板使用 `EstateRaceDrawingLayers`，继续支持独立底板不透明度。

参考图来自保留代码的确定性离屏渲染：[PIT WINDOW](qa/hud-timing-panels/PitWindow.png)、[PIT STOP](qa/hud-timing-panels/PitStop.png)、[处罚指示器](qa/hud-timing-panels/Penalty.png)。

后续主题可以在 `HudSurface.Themes.cs` 注册新的稳定 ID 与 `IRaceThemeRenderer`，将 `PitWindowContent`、`PitStopContent`、`PenaltyContent` 分发到这些入口；未覆盖的组件可调用经典渲染委托。继续复用共享可见性、布局、赛事隔离和动画生命周期，不复制计时、处罚或策略计算。不要重新使用已撤下的 `broadcast` ID。

`EstateHudRenderingTests.SavedTimingPanelsStillRenderWithoutRegisteringATheme` 对三个保留样式执行独立离屏绘制。设置 `LAZYFORZA_HUD_QA` 可输出 `saved-PitWindow.png`、`saved-PitStop.png`、`saved-Penalty.png`；这些是示例数据渲染，不是 FH6 实机证据。重新用于产品主题前仍需检查中英文、长名称、多 DPI 和实际 Overlay 场景。
