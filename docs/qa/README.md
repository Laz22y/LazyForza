# HUD QA

数据来源：LazyForza 确定性 `Demo / Replay`，不是 FH6 实录。

生成命令：

```powershell
dotnet run --project src/LazyForza.App/LazyForza.App.csproj -c Debug -- --capture-qa docs/qa
```

自动生成：

- `hud-1280x720-demo.png`
- `hud-1920x1080-demo.png`
- `hud-2560x1440-demo.png`

2026-07-22 结构检查：三张 PNG 实际像素尺寸与文件名一致；四角 alpha 均为 0。运行时 HUD 仍由 WPF 矢量绘制，图片只作为视觉回归基准。桌面 smoke 已观察到双圆、三条平行弧（Lap/RPM/结构弧）、四轮胶囊、左 Brake/右 Throttle、动态油门亮带、R/917 与完整 D–X 色条；Dashboard 关闭时 Lap HUD 切换为独立紧凑面板。

发布版 30 分钟循环 Replay soak 已完成，数据见 `SOAK-2026-07-22.md`。

尚未完成：真实 FH6 画面叠加、混合 DPI 多显示器和真实 FH6 Live UDP soak；详见根目录 `VALIDATION_WITH_FH6.md`。
