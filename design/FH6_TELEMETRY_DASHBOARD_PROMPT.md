# FH6 极简遥测仪表盘生成提示词

> **Status: Historical visual-generation material**
> 本文仅保留早期设计稿生成过程，不是当前代码修改指令。现行 HUD 以实现和测试为准。

以下为 `FH6_TELEMETRY_DASHBOARD_DESIGN.png` 使用的最终内置 ImageGen 提示词。

```text
Use case: ui-mockup
Asset type: implementation-ready automotive telemetry dashboard design reference, 16:9 landscape
Primary request: Create one extremely minimal, production-realistic dark telemetry dashboard for a Forza Horizon 6 companion app. This is a flat front-facing UI screenshot, not a dashboard photographed inside a car.

Composition and hierarchy:
1. A wide graceful thick arc spans most of the upper dashboard and acts as the primary divider. Directly above it, place one thin perfectly parallel segmented arc showing engine RPM progress. Segments are evenly spaced: off-white at low/mid RPM, warm amber near the limit, and a short red zone at the far right. No circular speedometer.
2. Immediately below the thick arc, center the primary readout: a very large current gear "4" and a very large speed "184" with small unit "km/h". Gear and speed must be unmistakably separate.
3. Below that, place a single clean circular telemetry pod. Inside it render exactly: "6,820 RPM", "318 kW", and "472 N·m". The pod has a restrained dark-charcoal-to-red radial gradient around its rim, visibly showing the circle becoming red as RPM approaches the red zone.
4. At the lower center, place two narrow fillable vertical rectangles. The LEFT rectangle is brake, red fill at 42 percent, with exact text "42%" above and "BRAKE" below. The RIGHT rectangle is throttle, lime fill at 76 percent, with exact text "76%" above and "THROTTLE" below. Make left/right order unambiguous.
5. To the left of those input bars, show a compact top-down four-tire visualization. Each tire is a small vertical rounded rectangle with a thin grip halo. Show temperature and normalized grip simultaneously using exact compact values: "91° 0.82", "94° 0.79", "88° 0.91", "89° 0.89". Heading exactly "TYRES". Use cool cyan for healthy grip, amber only for the warmest tire; no car illustration.
6. To the right of the input bars, show the vehicle class and PI using the authentic current FH6 visual grammar: one compact segmented badge with a magenta-purple LEFT block containing white "S1", immediately joined to a BLACK RIGHT block containing white "766", with a thin magenta-purple outline around the composite. Heading exactly "CLASS / PI". Do not use an old single-color Forza badge, no logos.

Style/medium: shippable vector-like product UI mockup, precision grid, geometric sans-serif typography, flat front elevation, crisp edges, minimalist telemetry visualization.
Color palette: nearly black #090B0E background, charcoal #15191F surfaces, off-white #F4F6F8 data, muted gray labels, restrained crimson red, lime throttle, cyan tire grip, FH6-style magenta class block.
Spacing: symmetrical, generous negative space, strong alignment, clear visual hierarchy, all elements contained within one dashboard panel.
Text (verbatim only): "4", "184", "km/h", "6,820 RPM", "318 kW", "472 N·m", "42%", "BRAKE", "76%", "THROTTLE", "TYRES", "91° 0.82", "94° 0.79", "88° 0.91", "89° 0.89", "CLASS / PI", "S1", "766".
Constraints: practical enough for an agent to reproduce in HTML/CSS, Canvas, Qt, or Flutter; readable typography; exact left brake/right throttle placement; arc geometry is the dominant structural idea; no perspective, no steering wheel, no cockpit, no car photo, no scenery, no map, no lap timer, no decorative gauges, no logos, no trademarks, no watermark, no extra text, no excessive glow, no cyberpunk clutter.
```
