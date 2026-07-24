# Playground 官方赛事目录

LazyForza 将玩家通过 FH6 官方 UDP Data Out 实际录入的官方赛事轨迹作为程序必要数据内置。目录不包含表演赛、腕带赛，也不把模拟器或旧测试路线伪装成官方赛事。

## 当前目录

- 目录版本：`2026.07.23.1`
- 官方赛事：85 条
- 轨迹点：121,055 个
- 分段：1,122 个
- 类型：公路 22、街头 15、泥地 21、越野 19、山道 5、直线 3
- 内置资源：`src/LazyForza.Storage/Assets/PlaygroundOfficialTracks.json.gz`
- 资源 SHA-256：`1854595AD2D20E83CA3E4913F950A786DB157373FBEAA70B75C020C8949C08DD`

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
dotnet run --project tools\LazyForza.TrackCatalogTool -- export <lazyforza.db> src\LazyForza.Storage\Assets\PlaygroundOfficialTracks.json.gz
```

生成后应执行 `verify`，并在数据库副本上执行 `install`，确认导入前后的赛道数和圈速数保持一致。
