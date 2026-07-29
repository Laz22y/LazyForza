using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using System.IO;
using LazyForza.Analysis;
using LazyForza.Domain;
using Microsoft.Win32;

namespace LazyForza.App;

internal sealed partial class MainWindow
{
    private UIElement ReplayWorkbenchPage()
    {
        var stack = PageStack(
            "回放工作台",
            "回看已保存的单圈走线与遥测；回放只读取本地数据，不会影响实时监听和 HUD。");
        var tracks = store.ListTracks(CurrentTrackSource)
            .Where(track => track.Laps > 0)
            .OrderByDescending(track => track.Laps)
            .ThenBy(track => track.Name, StringComparer.CurrentCulture)
            .ToArray();
        if (tracks.Length == 0)
        {
            stack.Children.Add(EmptyCard("暂无可回放圈速", "完成并保存至少一圈后，这里会显示回放与导出工具。"));
            return Scroll(stack);
        }

        var selectorGrid = new Grid();
        selectorGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        selectorGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        selectorGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var trackSelector = new ComboBox { MinWidth = 280, Margin = new Thickness(0, 0, 10, 0) };
        var lapSelector = new ComboBox { MinWidth = 330, Margin = new Thickness(0, 0, 10, 0) };
        var exportCsv = new Button
        {
            Content = "导出遥测 CSV",
            Padding = new Thickness(13, 7, 13, 7),
            IsEnabled = false,
            ToolTip = "导出当前圈的逐样本遥测；旧圈不具备的动态字段留空"
        };
        foreach (var track in tracks)
        {
            trackSelector.Items.Add(new ComboBoxItem
            {
                Content = $"{track.Name} · {track.Laps} 圈",
                Tag = track.Id
            });
        }
        selectorGrid.Children.Add(trackSelector);
        Grid.SetColumn(lapSelector, 1);
        selectorGrid.Children.Add(lapSelector);
        Grid.SetColumn(exportCsv, 2);
        selectorGrid.Children.Add(exportCsv);
        stack.Children.Add(Card(selectorGrid));

        var host = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        stack.Children.Add(host);
        var playbackSpeed = 1d;
        var playing = false;
        var elapsed = 0d;
        var previousTick = DateTimeOffset.UtcNow;
        LapRecord? currentLap = null;
        TrackTemplate? currentTrack = null;
        TrackMapView? mapView = null;
        Slider? timeline = null;
        TextBlock? timeValue = null;
        TextBlock? speedValue = null;
        TextBlock? inputValue = null;
        TextBlock? dynamicsValue = null;
        Button? playPause = null;
        DispatcherTimer timer = null!;
        timer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(40),
            DispatcherPriority.Render,
            (_, _) => Tick(),
            Dispatcher)
        {
            IsEnabled = false
        };

        trackSelector.SelectionChanged += (_, _) =>
        {
            timer.Stop();
            playing = false;
            lapSelector.Items.Clear();
            if (trackSelector.SelectedItem is not ComboBoxItem { Tag: Guid trackId }) return;
            currentTrack = store.LoadTrack(trackId)?.Track;
            foreach (var lap in store.LoadLapSummaries(trackId)
                         .OrderByDescending(lap => lap.StartedAt))
            {
                lapSelector.Items.Add(new ComboBoxItem
                {
                    Content =
                        $"{AnalysisTime(lap.TotalSeconds, currentTrack?.LayoutKind == TrackLayoutKind.PointToPoint)} · " +
                        $"{PerformanceClassName(lap.Vehicle.CarClass)} {lap.Vehicle.PerformanceIndex} · " +
                        $"{lap.StartedAt.ToLocalTime():MM-dd HH:mm:ss}",
                    Tag = lap.Id
                });
            }
            if (lapSelector.Items.Count > 0) lapSelector.SelectedIndex = 0;
        };

        lapSelector.SelectionChanged += (_, _) =>
        {
            timer.Stop();
            playing = false;
            if (lapSelector.SelectedItem is not ComboBoxItem { Tag: Guid lapId })
            {
                currentLap = null;
                host.Children.Clear();
                exportCsv.IsEnabled = false;
                return;
            }
            currentLap = store.LoadLap(lapId);
            if (currentLap is null) return;
            currentTrack = store.LoadTrack(currentLap.TrackId)?.Track;
            elapsed = 0;
            previousTick = DateTimeOffset.UtcNow;
            exportCsv.IsEnabled = true;
            RenderReplay();
        };

        exportCsv.Click += (_, _) =>
        {
            if (currentLap is null) return;
            var dialog = new SaveFileDialog
            {
                Title = "导出圈速遥测",
                Filter = "CSV 文件 (*.csv)|*.csv",
                DefaultExt = ".csv",
                AddExtension = true,
                FileName =
                    $"LazyForza-{SafeFileName(currentTrack?.Name ?? "圈速")}-" +
                    $"{currentLap.StartedAt.ToLocalTime():yyyyMMdd-HHmmss}.csv"
            };
            if (dialog.ShowDialog(this) != true) return;
            try
            {
                LapTelemetryExporter.WriteCsv(
                    dialog.FileName,
                    currentTrack?.Name ?? "未知赛道",
                    currentLap);
                MessageBox.Show(
                    $"已导出：\n{dialog.FileName}",
                    "导出完成",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception exception)
            {
                MessageBox.Show(
                    $"无法导出遥测：{exception.Message}",
                    "导出失败",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        };

        trackSelector.SelectedIndex = 0;
        stack.Unloaded += (_, _) => timer.Stop();
        return Scroll(stack);

        void RenderReplay()
        {
            if (currentLap is null) return;
            host.Children.Clear();
            var panel = new StackPanel();
            var controlCard = new Grid();
            controlCard.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            controlCard.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            controlCard.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            playPause = new Button
            {
                Content = "播放",
                Width = 72,
                Padding = new Thickness(10, 7, 10, 7),
                Margin = new Thickness(0, 0, 12, 0)
            };
            playPause.Click += (_, _) =>
            {
                if (currentLap is null) return;
                if (elapsed >= currentLap.TotalSeconds - 0.001) elapsed = 0;
                playing = !playing;
                playPause.Content = playing ? "暂停" : "播放";
                previousTick = DateTimeOffset.UtcNow;
                timer.IsEnabled = playing;
            };
            controlCard.Children.Add(playPause);
            timeline = new Slider
            {
                Minimum = 0,
                Maximum = Math.Max(0.01, currentLap.TotalSeconds),
                Value = 0,
                VerticalAlignment = VerticalAlignment.Center,
                IsMoveToPointEnabled = true
            };
            timeline.ValueChanged += (_, _) =>
            {
                if (timeline is null) return;
                elapsed = timeline.Value;
                UpdateFrame();
            };
            Grid.SetColumn(timeline, 1);
            controlCard.Children.Add(timeline);
            var speedSelector = new ComboBox
            {
                Width = 86,
                Margin = new Thickness(12, 0, 0, 0)
            };
            foreach (var speed in new[] { 0.5, 1d, 2d, 4d })
            {
                speedSelector.Items.Add(new ComboBoxItem
                {
                    Content = $"{speed:0.#}×",
                    Tag = speed
                });
            }
            speedSelector.SelectionChanged += (_, _) =>
            {
                if (speedSelector.SelectedItem is ComboBoxItem { Tag: double selected })
                    playbackSpeed = selected;
            };
            speedSelector.SelectedIndex = 1;
            Grid.SetColumn(speedSelector, 2);
            controlCard.Children.Add(speedSelector);
            panel.Children.Add(Card(controlCard));

            var metrics = new UniformGrid { Columns = 4, Margin = new Thickness(0, 12, 0, 12) };
            timeValue = Label("0:00.000", 20, FontWeights.SemiBold);
            speedValue = Label("0 km/h", 20, FontWeights.SemiBold);
            inputValue = Label("油门 0% · 制动 0%", 15, FontWeights.SemiBold);
            dynamicsValue = Label("动态遥测待载入", 13, FontWeights.SemiBold);
            metrics.Children.Add(MetricCard("回放时间", timeValue, Label($"总计 {AnalysisTime(currentLap.TotalSeconds, false)}", 11, FontWeights.Normal, "MutedBrush")));
            metrics.Children.Add(MetricCard("速度 / 挡位", speedValue, Label("逐样本回放", 11, FontWeights.Normal, "MutedBrush")));
            metrics.Children.Add(MetricCard("驾驶输入", inputValue, Label("保存圈速中的输入", 11, FontWeights.Normal, "MutedBrush")));
            metrics.Children.Add(MetricCard("轮胎动态", dynamicsValue, Label(
                currentLap.Samples.Any(sample => sample.Dynamics is not null)
                    ? "方向与滑移已记录"
                    : "旧圈：动态字段不可用",
                11,
                FontWeights.Normal,
                "MutedBrush")));
            panel.Children.Add(metrics);

            var mapPanel = new Grid
            {
                Height = LapAnalysisVisualLayout.AdaptiveMapHeight(
                    ActualHeight > 0 ? ActualHeight : Height)
            };
            mapPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            mapPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            var header = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
            header.Children.Add(Label(
                $"{currentTrack?.Name ?? "未知赛道"} · 回放走线",
                13,
                FontWeights.SemiBold));
            mapView = new TrackMapView(
                [currentLap],
                currentTrack,
                [new LapSeriesLegendEntry(
                    AnalysisTime(currentLap.TotalSeconds, false),
                    $"{PerformanceClassName(currentLap.Vehicle.CarClass)} {currentLap.Vehicle.PerformanceIndex}")],
                dynamicsLapId: currentLap.Id);
            var layerControls = DynamicsLayerControls(mapView, _ => { });
            layerControls.Margin = new Thickness(0, 7, 0, 0);
            header.Children.Add(layerControls);
            mapPanel.Children.Add(header);
            var mapSurface = new Grid();
            mapSurface.Children.Add(mapView);
            mapSurface.Children.Add(MapDisplayControls(mapView));
            Grid.SetRow(mapSurface, 1);
            mapPanel.Children.Add(mapSurface);
            panel.Children.Add(Card(mapPanel));
            host.Children.Add(panel);
            UpdateFrame();
        }

        void Tick()
        {
            if (!playing || currentLap is null || timeline is null) return;
            var now = DateTimeOffset.UtcNow;
            elapsed += (now - previousTick).TotalSeconds * playbackSpeed;
            previousTick = now;
            if (elapsed >= currentLap.TotalSeconds)
            {
                elapsed = currentLap.TotalSeconds;
                playing = false;
                timer.Stop();
                if (playPause is not null) playPause.Content = "播放";
            }
            timeline.Value = elapsed;
            UpdateFrame();
        }

        void UpdateFrame()
        {
            if (currentLap is null || currentLap.Samples.Count == 0) return;
            var sample = NearestReplaySample(currentLap.Samples, elapsed);
            if (mapView is not null) mapView.PlaybackElapsedSeconds = elapsed;
            if (timeValue is not null) timeValue.Text = AnalysisTime(elapsed, false);
            if (speedValue is not null)
                speedValue.Text = $"{sample.SpeedMps * 3.6:0} km/h · {ReplayGear(sample.Gear)} 挡";
            if (inputValue is not null)
                inputValue.Text = $"油门 {sample.Accel:P0} · 制动 {sample.Brake:P0}";
            if (dynamicsValue is not null)
            {
                dynamicsValue.Text = sample.Dynamics is { } dynamics
                    ? $"方向 {dynamics.Steering:+0.00;-0.00;0.00} · 滑移 {dynamics.TireCombinedSlip.MaxAbsolute:0.00}"
                    : "该圈未记录";
            }
        }
    }

    private static LapSample NearestReplaySample(
        IReadOnlyList<LapSample> samples,
        double elapsed)
    {
        var low = 0;
        var high = samples.Count - 1;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (samples[middle].ElapsedSeconds < elapsed) low = middle + 1;
            else high = middle;
        }
        if (low == 0) return samples[0];
        return Math.Abs(samples[low].ElapsedSeconds - elapsed) <
               Math.Abs(samples[low - 1].ElapsedSeconds - elapsed)
            ? samples[low]
            : samples[low - 1];
    }

    private static string ReplayGear(byte gear) => gear == 0 ? "R" : gear.ToString();

    private static string SafeFileName(string value)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars())
            value = value.Replace(invalid, '_');
        return string.IsNullOrWhiteSpace(value) ? "圈速" : value;
    }
}
