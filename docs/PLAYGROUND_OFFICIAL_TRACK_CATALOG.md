# Playground 官方赛事目录

LazyForza 将玩家通过 FH6 官方 UDP Data Out 实际录入的官方赛事轨迹作为程序必要数据内置。目录不包含表演赛、腕带赛，也不把模拟器或旧测试路线伪装成官方赛事。

## 当前目录

- 目录版本：`2026.11.03.1`
- 官方赛事：88 条
- 轨迹点：123,709 个
- 分段：1,153 个
- 类型：公路 24、街头 16、泥地 21、越野 19、山道 5、直线 3
- 内置资源：`src/LazyForza.Storage/Assets/PlaygroundOfficialTracks.json.gz`
- 资源 SHA-256：`463A49C49566820577BC4FDFE786C83ADFA4F8B9AEC589251D18D4F0D053BF14`

`2026.11.03.1` 新增以下赛事，名称和分类依据 [Playground Games 的 City Life 官方公告](https://forza.net/en-GB/news/forza-horizon-6-series-7)（2026-11-02）。轨迹来自 2026-11-03 的本机 Live UDP 录入，保留原始赛道 ID、几何和分段，不包含车辆、圈记录或个人设置。

| 中文名 | 游戏内英文名 | 分类 / 布局 | 稳定 ID |
| --- | --- | --- | --- |
| 江户环道赛 | Edo Circuit | 公路 / 环道 | `a93e1034-a926-4a38-a75e-a1d9550d7d6b` |
| 首都冲锋赛 | Shuto Charge | 街头 / 定点 | `42cdb456-a1f3-4125-9b08-f1e90b004e1f` |

前一目录 `2026.08.11.1` 的 86 条赛事几何及分段保持不变。已有这两个 ID 的个人赛道在导入目录时更新为官方分类，关联圈记录继续保留。

原始名称按 `类型 | 赛道名` 解析。类型存入 `Category`，界面只显示整理后的赛道名，避免重复前缀。

## 数据保护

- 内置赛事使用 `TrackCatalogKind.PlaygroundOfficial` 标记。
- 存储层拒绝重命名或删除官方赛事；界面也不提供这两个操作。
- 圈速记录仍属于用户数据，可以继续记录、筛选和清理。
- 启动时按稳定的原始赛道 ID 幂等导入，已有圈速记录不会被删除。
- 新学习的路线使用 `TrackCatalogKind.UserCustom`，只在“用户自定义赛道”区域显示。

## 可复现导出

目录由 `tools/LazyForza.TrackCatalogTool` 从离线数据库备份中筛选 `fh6_udp_live` 数据生成：

```powershell
dotnet run --project tools\LazyForza.TrackCatalogTool -- export <lazyforza.db> src\LazyForza.Storage\Assets\PlaygroundOfficialTracks.json.gz <YYYY.MM.DD.revision>
```

生成后应执行 `verify`，并在数据库副本上执行 `install`，确认导入前后的赛道数和圈速数保持一致。

追加明确选定的官方路线时使用 `append`，它保留已有内置目录，只读取给定 ID 对应的已收录 Live 普通赛道：

```powershell
dotnet run --project tools\LazyForza.TrackCatalogTool -- append <snapshot.db> <catalog.json.gz> <YYYY.MM.DD.revision> <track-id> [track-id...]
```
