using System.Windows;
using System.Windows.Controls;
using LazyForza.Domain;
using LazyForza.Modules.LapAnalysis;
using LazyForza.Storage;
using Microsoft.Win32;

namespace LazyForza.App;

internal sealed partial class MainWindow
{
    private (Border View, Action Refresh) EstateTrackSection(
        IReadOnlyList<TrackSummary> tracks,
        EstateCircuitModule estateModule)
    {
        var initialState = estateModule.State;
        var packageService = new EstateTrackPackageService(store, CurrentApplicationVersion());
        var timingButtons = new List<(Guid TrackId, Button Button)>();
        var exportButtons = new List<Button>();
        var deleteButtons = new List<(Guid TrackId, Button Button)>();
        TextBlock? activeStatus = null;
        TextBlock? activeInstruction = null;
        TextBlock? activeTiming = null;
        var content = new StackPanel();
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var heading = new StackPanel();
        heading.Children.Add(Label("地产环道", 20, FontWeights.SemiBold));
        heading.Children.Add(Label(
            "手动选择地图后才启用；使用终点门几何和 LazyForza 本地时钟，不冒充游戏官方计时。",
            12,
            FontWeights.Normal,
            "MutedBrush"));
        header.Children.Add(heading);
        var actions = new WrapPanel
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        var import = new Button
        {
            Content = "从文件导入",
            Padding = new Thickness(16, 8, 16, 8),
            IsEnabled = !initialState.IsTimingActive && !initialState.IsEnrollmentActive
        };
        import.Click += (_, _) =>
        {
            var dialog = new OpenFileDialog
            {
                Title = "导入地产环道",
                Filter = "LazyForza 地产环道 (*.lfzestate)|*.lfzestate|所有文件 (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false
            };
            if (dialog.ShowDialog(this) != true) return;
            try
            {
                var preview = packageService.Preview(dialog.FileName);
                var confirmation = MessageBox.Show(
                    this,
                    $"地图：{preview.Track.Name}\n" +
                    $"修订：{preview.Definition.MapRevision}\n" +
                    $"路线：{preview.Track.LengthMeters / 1000:0.00} km，{preview.Sectors.Count} 个圈速分段，{preview.Definition.Checkpoints.Count} 个检查点\n\n" +
                    "文件只会导入赛道路线、终点门、检查点和维修区定义，不包含圈速记录或个人配置。确认导入吗？",
                    "导入地产环道",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);
                if (confirmation != MessageBoxResult.Yes) return;

                var result = packageService.Import(dialog.FileName, CurrentTrackSource);
                MessageBox.Show(
                    this,
                    result.AlreadyExists
                        ? $"“{result.TrackName}”已经存在，未重复导入。"
                        : $"已导入“{result.TrackName}”。请确认游戏中加载的是对应地图和修订版本，再手动开始计时。",
                    "导入地产环道",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                trackPreviewCache.Clear();
                RenderSelectedPage();
            }
            catch (Exception exception)
            {
                MessageBox.Show(this, exception.Message, "导入地产环道", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        };
        actions.Children.Add(import);

        var add = new Button
        {
            Content = "添加地产环道",
            Margin = new Thickness(8, 0, 0, 0),
            Padding = new Thickness(16, 8, 16, 8),
            IsEnabled = !initialState.IsTimingActive && !initialState.IsEnrollmentActive
        };
        add.Click += (_, _) =>
        {
            if (moduleActivation.IsDriftActive)
            {
                MessageBox.Show(
                    this,
                    "请先关闭漂移仪表盘，再录入地产环道。",
                    "地产环道",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }
            var window = new EstateCircuitEnrollmentWindow(estateModule) { Owner = this };
            window.ShowDialog();
            trackPreviewCache.Clear();
            RenderSelectedPage();
        };
        actions.Children.Add(add);
        Grid.SetColumn(actions, 1);
        header.Children.Add(actions);
        content.Children.Add(header);

        var guidance = Label(
            "录入顺序：沿棋盘格横线低速往返各描摹一次 → 确认正常比赛方向 → 完成参考圈 → 连续完成验证圈。暂停、倒带或传送会取消当前圈；地产模式只支持环道。",
            12,
            FontWeights.Normal,
            "MutedBrush");
        guidance.Margin = new Thickness(0, 12, 0, 12);
        content.Children.Add(guidance);

        var state = initialState;
        if (state.IsTimingActive || state.IsEnrollmentActive)
        {
            var active = new StackPanel();
            active.Children.Add(Label(
                state.IsTimingActive ? "地产计时正在运行" : "地产赛道正在录入",
                15,
                FontWeights.SemiBold,
                "AccentBrush"));
            activeStatus = Label(state.Status, 13);
            activeInstruction = Label(state.Instruction, 12, FontWeights.Normal, "MutedBrush");
            active.Children.Add(activeStatus);
            active.Children.Add(activeInstruction);
            if (state.IsTimingActive)
            {
                activeTiming = Label(
                    TimingSummary(state),
                    12,
                    FontWeights.Normal,
                    "MutedBrush");
                active.Children.Add(activeTiming);
            }
            var activeCard = Card(active);
            activeCard.Margin = new Thickness(0, 0, 0, 12);
            content.Children.Add(activeCard);
        }

        if (tracks.Count == 0)
        {
            content.Children.Add(Label(
                "还没有已验证的地产环道。录入不会自动识别地图，也不会在大世界自行触发。",
                13,
                FontWeights.Normal,
                "MutedBrush"));
            return Complete();
        }

        foreach (var track in tracks.OrderBy(track => track.Name, StringComparer.Ordinal))
        {
            var definition = store.LoadEstateTrackDefinition(track.Id);
            if (definition is null) continue;
            var sectorCount = store.LoadTrack(track.Id)?.Sectors.Count ?? 0;
            var row = new Grid { Margin = new Thickness(0, 5, 0, 5) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var description = new StackPanel();
            description.Children.Add(Label(track.Name, 15, FontWeights.SemiBold));
            var identity = string.Join(" · ", new[]
            {
                string.IsNullOrWhiteSpace(definition.Creator) ? null : $"作者 {definition.Creator}",
                string.IsNullOrWhiteSpace(definition.ShareCode) ? null : $"标识 {definition.ShareCode}",
                $"修订 {definition.MapRevision}",
                $"{track.Length / 1000:0.00} km",
                $"{sectorCount} 个分段",
                $"{definition.Checkpoints.Count} 个检查点"
            }.Where(value => value is not null));
            description.Children.Add(Label(identity, 11, FontWeights.Normal, "MutedBrush"));
            description.Children.Add(Label(
                $"终点拟合 RMS {definition.StartFinishGate.FitRmsMeters:0.00} m · 验证路线有效率 {definition.ValidationProjectionRatio:P0}",
                11,
                FontWeights.Normal,
                "MutedBrush"));
            row.Children.Add(description);

            var rowActions = new WrapPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0)
            };
            var raceInfo = new Button
            {
                Content = "赛事信息",
                MinWidth = 86,
                Margin = new Thickness(0, 0, 8, 0),
                IsEnabled = !estateModule.State.IsEnrollmentActive
            };
            raceInfo.Click += (_, _) =>
            {
                try
                {
                    var packageIdentity = packageService.Identify(track.Id);
                    new EstateTrackIdentityWindow(packageIdentity) { Owner = this }.ShowDialog();
                }
                catch (Exception exception)
                {
                    MessageBox.Show(this, exception.Message, "无法读取赛事赛道信息", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            };
            rowActions.Children.Add(raceInfo);
            exportButtons.Add(raceInfo);
            var export = new Button
            {
                Content = "导出",
                MinWidth = 74,
                Margin = new Thickness(0, 0, 8, 0),
                IsEnabled = !estateModule.State.IsEnrollmentActive
            };
            export.Click += (_, _) =>
            {
                var dialog = new SaveFileDialog
                {
                    Title = "导出地产环道",
                    Filter = "LazyForza 地产环道 (*.lfzestate)|*.lfzestate",
                    DefaultExt = EstateTrackPackageService.FileExtension,
                    AddExtension = true,
                    FileName = $"{SafeFileName(track.Name)}-{SafeFileName(definition.MapRevision)}.lfzestate"
                };
                if (dialog.ShowDialog(this) != true) return;
                try
                {
                    var manifest = packageService.Export(track.Id, dialog.FileName);
                    var packageIdentity = new EstateTrackPackageIdentity(
                        manifest.TrackId,
                        manifest.TrackName,
                        manifest.MapRevision,
                        manifest.PayloadSha256,
                        sectorCount);
                    new EstateTrackIdentityWindow(packageIdentity, dialog.FileName) { Owner = this }.ShowDialog();
                }
                catch (Exception exception)
                {
                    MessageBox.Show(this, exception.Message, "导出地产环道", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            };
            rowActions.Children.Add(export);
            exportButtons.Add(export);

            var configurePit = new Button
            {
                Content = definition.Pit is null ? "配置维修区" : "重录维修区",
                MinWidth = 96,
                Margin = new Thickness(0, 0, 8, 0),
                IsEnabled = !estateModule.State.IsEnrollmentActive && !estateModule.State.IsTimingActive
            };
            configurePit.Click += (_, _) =>
            {
                var loadedTrack = store.LoadTrack(track.Id);
                var currentDefinition = store.LoadEstateTrackDefinition(track.Id);
                if (loadedTrack is null || currentDefinition is null)
                {
                    MessageBox.Show(this, "赛道定义已经不存在，请刷新列表。", "无法配置维修区", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                var window = new EstatePitEnrollmentWindow(estateModule, loadedTrack.Value.Track, currentDefinition) { Owner = this };
                window.ShowDialog();
                RenderSelectedPage(true);
            };
            rowActions.Children.Add(configurePit);

            var delete = new Button
            {
                Content = "删除",
                MinWidth = 74,
                Margin = new Thickness(0, 0, 8, 0),
                IsEnabled = !estateModule.State.IsEnrollmentActive &&
                            !(estateModule.State.IsTimingActive && estateModule.State.TrackId == track.Id)
            };
            delete.Click += (_, _) =>
            {
                var current = estateModule.State;
                if (current.IsEnrollmentActive)
                {
                    MessageBox.Show(
                        this,
                        "请先完成或取消当前地产环道录入，再删除已有赛道。",
                        "无法删除地产环道",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return;
                }
                if (current.IsTimingActive && current.TrackId == track.Id)
                {
                    MessageBox.Show(
                        this,
                        "这条地产环道正在计时。请先停止计时，再删除赛道。",
                        "无法删除地产环道",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return;
                }
                if (MessageBox.Show(
                        this,
                        $"确认删除“{track.Name}”？\n\n" +
                        $"赛道路线、终点门、检查点、维修区定义以及 {track.Laps} 圈成绩都会一并删除，且无法撤销。" +
                        "如需保留赛道，请先导出 .lfzestate 文件。",
                        "确认删除地产环道",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning) != MessageBoxResult.Yes)
                    return;

                try
                {
                    store.DeleteTrack(track.Id);
                    var lapModule = moduleManager.Modules.OfType<LapAnalysisModule>().Single();
                    if (lapModule.CurrentTrack?.Id == track.Id)
                        lapModule.ClearTrackSelection();
                    selectedLapIds.Clear();
                    displayedLapIds.Clear();
                    trackPreviewCache.Remove(track.Id);
                    RenderSelectedPage();
                }
                catch (Exception exception)
                {
                    MessageBox.Show(this, exception.Message, "删除地产环道", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            };
            rowActions.Children.Add(delete);
            deleteButtons.Add((track.Id, delete));

            var isActive = estateModule.State.IsTimingActive && estateModule.State.TrackId == track.Id;
            var timing = new Button
            {
                Content = isActive ? "停止计时" : "选择地图并开始计时",
                MinWidth = 150,
                VerticalAlignment = VerticalAlignment.Center,
                IsEnabled = !estateModule.State.IsEnrollmentActive
            };
            timing.Click += (_, _) =>
            {
                if (moduleActivation.IsDriftActive)
                {
                    MessageBox.Show(
                        this,
                        "请先关闭漂移仪表盘，再启用地产环道计时。",
                        "地产环道",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return;
                }
                try
                {
                    if (estateModule.State.IsTimingActive && estateModule.State.TrackId == track.Id)
                        estateModule.StopTiming();
                    else
                        estateModule.StartTiming(track.Id);
                }
                catch (Exception exception)
                {
                    MessageBox.Show(this, exception.Message, "地产环道计时", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                RenderSelectedPage();
            };
            rowActions.Children.Add(timing);
            Grid.SetColumn(rowActions, 1);
            row.Children.Add(rowActions);
            timingButtons.Add((track.Id, timing));
            content.Children.Add(Card(row));
        }

        return Complete();

        (Border View, Action Refresh) Complete()
        {
            var initiallyActive = initialState.IsTimingActive || initialState.IsEnrollmentActive;
            var view = TrackSectionContainer(content);
            return (view, Refresh);

            void Refresh()
            {
                var current = estateModule.State;
                var currentlyActive = current.IsTimingActive || current.IsEnrollmentActive;
                if (currentlyActive != initiallyActive ||
                    current.IsTimingActive != initialState.IsTimingActive ||
                    current.TrackId != initialState.TrackId)
                {
                    RenderSelectedPage(true);
                    return;
                }

                add.IsEnabled = !current.IsTimingActive && !current.IsEnrollmentActive;
                import.IsEnabled = !current.IsTimingActive && !current.IsEnrollmentActive;
                if (activeStatus is not null) activeStatus.Text = current.Status;
                if (activeInstruction is not null) activeInstruction.Text = current.Instruction;
                if (activeTiming is not null) activeTiming.Text = TimingSummary(current);
                foreach (var (trackId, button) in timingButtons)
                {
                    var isActive = current.IsTimingActive && current.TrackId == trackId;
                    button.Content = isActive ? "停止计时" : "选择地图并开始计时";
                    button.IsEnabled = !current.IsEnrollmentActive;
                }
                foreach (var button in exportButtons)
                    button.IsEnabled = !current.IsEnrollmentActive;
                foreach (var (trackId, button) in deleteButtons)
                    button.IsEnabled = !current.IsEnrollmentActive &&
                                       !(current.IsTimingActive && current.TrackId == trackId);
            }
        }

        static string TimingSummary(EstateCircuitState value) =>
            $"当前 {value.CurrentLapSeconds:0.0} s · 上一圈 " +
            $"{(value.LastLapSeconds is double last ? $"{last:0.000} s" : "—")} · 已完成 {value.CompletedLaps} 圈";
    }
}
