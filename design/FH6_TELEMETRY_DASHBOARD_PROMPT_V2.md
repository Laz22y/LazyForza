# FH6 极简遥测仪表盘 V2 ImageGen 提示词

> **Status: Historical visual-generation material**
> 本文仅保留早期设计稿生成过程，不是当前代码修改指令。现行 HUD 以实现和测试为准。

以下提示词用于生成/复现 V2 视觉稿。生成模式为内置 ImageGen 的参考图编辑模式；最终透明图通过纯色技术背景提取 alpha。

## 主设计提示词

```text
Use case: ui-mockup
Asset type: implementation-ready premium automotive telemetry dashboard reference, wide 16:9 landscape, flat front-facing UI

Edit the referenced FH6 telemetry dashboard into a more compact, expensive-looking minimalist layout while retaining the graceful wide arc as the dominant structure.

Required composition:
1. At the top, keep one wide thick graceful divider arc. Directly above it place one thin perfectly parallel segmented RPM arc with even spacing: off-white progress, amber near the limit, and a short red zone on the far right.
2. Reduce central empty space. Put TWO equal circular modules side by side immediately below the arc. Left circle: a huge gear “4”, a thin divider, then “184” and small “km/h”. Right circle: exactly “6,820 RPM”, “318 kW”, and “472 N·m”, divided by thin lines. Its rim transitions from neutral graphite into rich red as RPM approaches redline.
3. Lower left: a radically simplified tire temperature/grip visualization. Use only four small vertical rounded-outline capsules arranged FL/FR over RL/RR. No tire photographs, no tread, no tire pattern, no car silhouette. Show exact compact values “91° 0.82”, “94° 0.79”, “88° 0.91”, “89° 0.89” under heading “TYRE TEMP / GRIP”.
4. Lower center: two narrow fillable vertical rectangles. Left is brake, exactly “42%” above and “BRAKE” below, filled deep red #8B1E2D. Right is throttle, exactly “76%” above and “THROTTLE” below, filled deep green #0B6B43. Inside the throttle fill show a narrow brighter moving energy stripe and add a small implementation annotation beside it: a loop arrow plus exact text “1.2s LOOP GRADIENT”.
5. Lower right: heading “CLASS / PI” and a compact joined two-part FH6-style badge showing “R” in a hot-magenta left block and “917” in a black right block, outlined in magenta.
6. Along the bottom, show one precise full-width class legend with all eight entries and exact ranges: “D 100–400”, “C 401–500”, “B 501–600”, “A 601–700”, “S1 701–800”, “S2 801–900”, “R 901–998”, “X 999”. Use these approximate class colors: D light cyan #62B8E8, C gold #F2B827, B orange #ED7A1A, A red #E3314F, S1 violet #B43BDD, S2 blue #2472D4, R magenta #E62A83, X emerald #00B85A.

Style: premium restrained automotive product UI, matte near-black panel, graphite borders, subtle silver highlights, narrow geometric sans-serif, tabular numerals, exact grid alignment, crisp vector-like edges, no textures, no tire tread, no carbon fiber, no fake cockpit, no perspective, no logos, no watermark, no decorative clutter, no excessive bloom.

Background plate: place the entire rounded dashboard silhouette on a solid technical chroma-key background. Keep all dashboard content inside one opaque dark panel and keep the outside background visually separate for later alpha extraction.
```

## 最终键色替换提示词

```text
EDIT MODE — keep every dashboard element and every interior color/text/number/layout unchanged. Replace ONLY the exterior outside the rounded dashboard panel with one perfectly flat, uniform technical chroma-key color RGB #7A3200 (dark burnt brown). The entire exterior from canvas edges to the panel silhouette must be exactly this one solid color: no gradient, no vignette, no texture, no shadow, no glow, no reflection. Remove all previous key-color edge spill. Keep the outer dashboard silhouette clean, sharp, neutral graphite/black, with no colored rim. Do not modify any part inside the dashboard. This is only a chroma-key background plate for later RGBA alpha extraction.
```

## 透明输出说明

1. 使用内置 ImageGen 的参考图编辑模式生成设计；
2. 使用纯色技术背景生成最终键色版本；
3. 使用 `remove_chroma_key.py --auto-key border --tolerance 20 --edge-contract 2` 提取透明区域并输出 RGBA PNG；
4. 检查四角 alpha 为 0、内部 S2/油门/X 等彩色组件没有被误删；
5. 最终文件为 `FH6_TELEMETRY_DASHBOARD_DESIGN_V2_TRANSPARENT.png`。
