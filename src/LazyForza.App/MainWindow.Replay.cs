using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using System.IO;
using LazyForza.Analysis;
using LazyForza.Domain;
using LazyForza.Telemetry;
using Microsoft.Win32;

namespace LazyForza.App;

internal sealed partial class MainWindow
{
    private UIElement ReplayWorkbenchPage()
    {
        var stack = PageStack(
            "回放工作台",
            "打开 .lfztelemetry 录制文件或回看已保存单圈；工作台只读取本地数据，不影响实时监听和 HUD。");
        var tracks = store.ListTracks(CurrentTrackSource)
            .Where(track => track.Laps > 0)
            .OrderByDescending(track => track.Laps)
            .ThenBy(track => track.Name, StringComparer.CurrentCulture)
            .ToArray();
        var fileControls = new Grid();
        fileControls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        fileControls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var openRecording = new Button
        {
            Content = "打开 .lfztelemetry",
            Padding = new Thickness(15, 8, 15, 8),
            Margin = new Thickness(0, 0, 14, 0)
        };
        fileControls.Children.Add(openRecording);
        var fileStatus = Label(
            "支持自动/手动原始录制，以及由工作台导出的单圈 .lfztelemetry。",
            11,
            FontWeights.Normal,
            "MutedBrush");
        fileStatus.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(fileStatus, 1);
        fileControls.Children.Add(fileStatus);
        stack.Children.Add(Card(fileControls));

        var selectorGrid = new Grid();
        selectorGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        selectorGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        selectorGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var trackSelector = new ComboBox { MinWidth = 280, Margin = new Thickness(0, 0, 10, 0) };
        var lapSelector = new ComboBox { MinWidth = 330, Margin = new Thickness(0, 0, 10, 0) };
        var exportRecording = new Button
        {
            Content = "导出单圈 .lfztelemetry",
            Padding = new Thickness(13, 7, 13, 7),
            IsEnabled = false,
            ToolTip = "导出当前工作台单圈；不会伪造缺失的原始 FH6 UDP 字段"
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
        Grid.SetColumn(exportRecording, 2);
        selectorGrid.Children.Add(exportRecording);
        stack.Children.Add(Card(selectorGrid));
        if (tracks.Length == 0)
            stack.Children.Add(EmptyCard("暂无已保存圈速", "仍可从上方打开 .lfztelemetry 录制文件。"));

        var host = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        stack.Children.Add(host);
        var playbackSpeed = 1d;
        var playing = false;
        var elapsed = 0d;
        var previousTick = DateTimeOffset.UtcNow;
        LapRecord? currentLap = null;
        TrackTemplate? currentTrack = null;
        var currentSourceName = "未知赛道";
        TrackMapView? mapView = null;
        Slider? timeline = null;
        TextBlock? timeValue = null;
        TextBlock? speedValue = null;
        TextBlock? inputValue = null;
        TextBlock? dynamicsValue = null;
        Button? playPause = null;
        LapAnalysisCursor? replayCursor = null;
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
            currentSourceName = currentTrack?.Name ?? "未知赛道";
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
                exportRecording.IsEnabled = false;
                return;
            }
            currentLap = store.LoadLap(lapId);
            if (currentLap is null) return;
            currentTrack = store.LoadTrack(currentLap.TrackId)?.Track;
            currentSourceName = currentTrack?.Name ?? "未知赛道";
            elapsed = 0;
            previousTick = DateTimeOffset.UtcNow;
            exportRecording.IsEnabled = true;
            RenderReplay();
        };

        exportRecording.Click += async (_, _) =>
        {
            if (currentLap is null) return;
            var dialog = new SaveFileDialog
            {
                Title = "导出单圈 LazyForza 遥测",
                Filter = "LazyForza 单圈遥测 (*.lfztelemetry)|*.lfztelemetry",
                DefaultExt = ".lfztelemetry",
                AddExtension = true,
                FileName =
                    $"LazyForza-{SafeFileName(currentSourceName)}-" +
                    $"{currentLap.StartedAt.ToLocalTime():yyyyMMdd-HHmmss}.lfztelemetry"
            };
            if (dialog.ShowDialog(this) != true) return;
            exportRecording.IsEnabled = false;
            try
            {
                await SingleLapTelemetryRecordingFile.WriteAsync(
                    dialog.FileName,
                    currentSourceName,
                    currentLap,
                    lifetimeCancellation.Token);
                MessageBox.Show(
                    $"已导出：\n{dialog.FileName}",
                    "导出完成",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception exception)
            {
                MessageBox.Show(
                    $"无法导出单圈遥测：{exception.Message}",
                    "导出失败",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                exportRecording.IsEnabled = currentLap is not null;
            }
        };

        openRecording.Click += async (_, _) =>
        {
            var dialog = new OpenFileDialog
            {
                Title = "打开 LazyForza 遥测录制",
                Filter = "LazyForza 遥测录制 (*.lfztelemetry)|*.lfztelemetry|所有文件 (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false
            };
            if (dialog.ShowDialog(this) != true) return;

            timer.Stop();
            playing = false;
            openRecording.IsEnabled = false;
            fileStatus.Text = $"正在读取 {Path.GetFileName(dialog.FileName)}…";
            try
            {
                var replay = await TelemetryRecordingAnalysis.LoadAsync(
                    dialog.FileName,
                    lifetimeCancellation.Token);
                trackSelector.SelectedIndex = -1;
                lapSelector.SelectedIndex = -1;
                currentLap = replay.Lap;
                currentTrack = null;
                currentSourceName = replay.TrackName ??
                                    Path.GetFileNameWithoutExtension(replay.SourcePath);
                elapsed = 0;
                previousTick = DateTimeOffset.UtcNow;
                exportRecording.IsEnabled = true;
                fileStatus.Text = replay.Metadata.ContentKind == TelemetryRecordingContentKind.SingleLap
                    ? $"{Path.GetFileName(replay.SourcePath)} · 单圈分析导出 · " +
                      $"{replay.Lap.Samples.Count:N0} 样本 · {AnalysisTime(replay.Lap.TotalSeconds, false)}"
                    : $"{Path.GetFileName(replay.SourcePath)} · 原始 {replay.FrameCount:N0} 帧 · " +
                      $"工作台 {replay.Lap.Samples.Count:N0} 样本 · {AnalysisTime(replay.Lap.TotalSeconds, false)}";
                RenderReplay();
            }
            catch (OperationCanceledException)
            {
                fileStatus.Text = "已取消读取录制文件。";
            }
            catch (Exception exception)
            {
                fileStatus.Text = "无法读取该录制文件。";
                MessageBox.Show(
                    exception.Message,
                    "回放文件无效",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            finally
            {
                openRecording.IsEnabled = true;
            }
        };

        if (tracks.Length > 0) trackSelector.SelectedIndex = 0;
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

            replayCursor = new LapAnalysisCursor();
            replayCursor.CommitRequested += (_, position) =>
            {
                if (currentLap is null ||
                    timeline is null)
                    return;
                var sampleIndex = ChartInteractionAlgorithms.FindNearestProgressSample(
                    currentLap.Samples,
                    position.ProgressMeters);
                elapsed = currentLap.Samples[sampleIndex].ElapsedSeconds;
                timeline.Value = elapsed;
            };
            LapSeriesLegendEntry[] replayLegend =
            [
                new LapSeriesLegendEntry(
                    AnalysisTime(currentLap.TotalSeconds, false),
                    $"{PerformanceClassName(currentLap.Vehicle.CarClass)} {currentLap.Vehicle.PerformanceIndex}")
            ];
            var speedChartPanel = new Grid { Height = 260 };
            speedChartPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            speedChartPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            var speedChartTitle = Label(
                "速度曲线 · 悬停联动查看，按下左键定位时间",
                13,
                FontWeights.SemiBold);
            speedChartTitle.Margin = new Thickness(0, 0, 0, 8);
            speedChartPanel.Children.Add(speedChartTitle);
            var speedChart = new LapTelemetryChart(
                [currentLap],
                currentLap.Samples.Max(sample => sample.S),
                replayLegend,
                replayCursor);
            Grid.SetRow(speedChart, 1);
            speedChartPanel.Children.Add(speedChart);
            panel.Children.Add(Card(speedChartPanel));

            var inputChartPanel = new Grid { Height = 230, Margin = new Thickness(0, 12, 0, 0) };
            inputChartPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            inputChartPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            var inputChartTitle = Label(
                "驾驶输入曲线 · 油门 / 制动 / 方向 · 按下左键定位时间",
                13,
                FontWeights.SemiBold);
            inputChartTitle.Margin = new Thickness(0, 0, 0, 8);
            inputChartPanel.Children.Add(inputChartTitle);
            var inputChart = new LapInputChart(
                currentLap,
                currentLap.Samples.Max(sample => sample.S),
                replayCursor);
            Grid.SetRow(inputChart, 1);
            inputChartPanel.Children.Add(inputChart);
            panel.Children.Add(Card(inputChartPanel));

            var mapPanel = new Grid
            {
                Margin = new Thickness(0, 12, 0, 0),
                Height = LapAnalysisVisualLayout.AdaptiveMapHeight(
                    ActualHeight > 0 ? ActualHeight : Height)
            };
            mapPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            mapPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            var header = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
            header.Children.Add(Label(
                $"{currentSourceName} · 回放走线 · 按下左键定位时间",
                13,
                FontWeights.SemiBold));
            mapView = new TrackMapView(
                [currentLap],
                currentTrack,
                [new LapSeriesLegendEntry(
                    AnalysisTime(currentLap.TotalSeconds, false),
                    $"{PerformanceClassName(currentLap.Vehicle.CarClass)} {currentLap.Vehicle.PerformanceIndex}")],
                dynamicsLapId: currentLap.Id,
                linkedCursor: replayCursor);
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
