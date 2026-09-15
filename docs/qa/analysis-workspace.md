# 圈速分析与回放界面验证

`--capture-analysis-qa` 使用实际 WPF 应用主题生成确定性分析页面，在 1440×900 和 960×640 窗口下覆盖圈速对比、选圈列表、走线、弯道及区间编辑、赛后复盘和回放。回放检查先定位时间，再验证播放推进与暂停保持。中文和英文各生成 24 张截图；英文另生成 `en-han-audit.txt` 供检查未翻译的汉字。

该模式会向指定数据目录写入合成赛道、六条单圈记录和两段弯道标记，因此必须使用新的 QA 数据目录。开关仅在同时传入 `--capture-qa` 和显式 `--data-dir` 时生效。不要指向正式数据目录；运行结束后应用自动退出。

从仓库根目录使用 PowerShell，在 Windows Debug 构建完成后运行：

```powershell
$qaRun = Join-Path $PWD ('artifacts/analysis-qa-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
& ./src/LazyForza.App/bin/Debug/net9.0-windows/LazyForza.App.exe --data-dir "$qaRun/data-zh" --capture-qa "$qaRun/screens-zh" --capture-analysis-qa --language zh-Hans | Out-Null
& ./src/LazyForza.App/bin/Debug/net9.0-windows/LazyForza.App.exe --data-dir "$qaRun/data-en" --capture-qa "$qaRun/screens-en" --capture-analysis-qa --language en | Out-Null
```

`ManualCornerPanelTests` 加载完整应用资源，检查保存区间、参考圈、差异导航、页签懒加载与草稿保留，以及图表移出和重新进入可视树后的游标联动。相关验证命令：

```powershell
dotnet test tests/LazyForza.IntegrationTests/LazyForza.IntegrationTests.csproj --no-restore -c Debug --filter 'FullyQualifiedName~ManualCornerPanelTests|FullyQualifiedName~StartupProfileTests'
```

截图使用确定性数据检查文字溢出、布局密度、滚动范围和中英文呈现。触控、多 DPI、鼠标拖动和屏幕阅读器另行人工检查。
