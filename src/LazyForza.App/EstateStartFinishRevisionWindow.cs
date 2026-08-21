using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using LazyForza.Domain;
using LazyForza.Modules.LapAnalysis;

namespace LazyForza.App;

internal sealed class EstateStartFinishRevisionWindow : Window
{
    private readonly EstateCircuitModule module;
    private readonly EstateGeometryPreview preview = new();
    private readonly TextBlock status = Text(string.Empty, 14, FontWeights.SemiBold);
    private readonly TextBlock instruction = Text(string.Empty, 12, FontWeights.Normal, "MutedBrush");
    private readonly TextBlock metrics = Text(string.Empty, 12, FontWeights.Normal, "MutedBrush");
    private readonly Button trace = Button("开始第一次描摹");
    private readonly Button direction = Button("开始比赛方向采样");
    private readonly Button save = Button("保存起终点线");
    private readonly DispatcherTimer timer;
    private readonly int existingLapCount;
    private bool saved;

    public EstateStartFinishRevisionWindow(
        EstateCircuitModule module,
        TrackTemplate track,
        EstateTrackDefinition definition,
        int existingLapCount = 0)
    {
        this.module = module;
        this.existingLapCount = existingLapCount;
        Title = $"重设起终点线 · {track.Name}";
        Width = 900;
        Height = 660;
        MinWidth = 780;
        MinHeight = 580;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brush("WindowBrush");
        Foreground = Brush("TextBrush");
        FontFamily = new FontFamily("Microsoft YaHei UI");
        Content = BuildContent(track, definition);

        module.BeginStartFinishRevision(track.Id);
        trace.Click += (_, _) => Run(() =>
        {
            if (module.State.Phase is EstateCircuitPhase.CapturingFirstTrace or EstateCircuitPhase.CapturingSecondTrace)
                module.StopLineTrace();
            else module.StartLineTrace();
        });
        direction.Click += (_, _) => Run(() =>
        {
            if (module.State.Phase == EstateCircuitPhase.CapturingDirection) module.StopDirectionCapture();
            else module.StartDirectionCapture();
        });
        save.Click += (_, _) => Run(() =>
        {
            if (existingLapCount > 0 && MessageBox.Show(
                    this,
                    $"更新起终点线会删除这条赛道已有的 {existingLapCount} 圈本地成绩，其他赛道不受影响。确认保存吗？",
                    "保存起终点线",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
            module.SaveStartFinishRevision();
            saved = true;
            Close();
        });
        Closing += OnClosing;
        timer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background,
            (_, _) => Refresh(), Dispatcher);
        timer.Start();
        Closed += (_, _) => timer.Stop();
        Refresh();
    }

    private UIElement BuildContent(TrackTemplate track, EstateTrackDefinition definition)
    {
        var root = new Grid { Margin = new Thickness(24) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        header.Children.Add(Text("重设起终点线", 24, FontWeights.SemiBold));
        header.Children.Add(Text(
            $"{track.Name} · 修订 {definition.MapRevision}。只替换起终点门和比赛方向；路线、检查点、维修区与赛道标识不变。新线必须位于原路线起点附近。",
            12, FontWeights.Normal, "MutedBrush"));
        root.Children.Add(header);

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
        preview.MinHeight = 390;
        body.Children.Add(Panel(preview, new Thickness(0, 0, 14, 0)));
        var controls = new StackPanel();
        controls.Children.Add(Text("操作", 17, FontWeights.SemiBold));
        controls.Children.Add(Text(
            "1. 沿起终点横线低速走一次，停止采集。\n2. 掉头沿同一横线反向再走一次。\n3. 从线前按比赛方向直穿，并继续约 10 米。\n4. 检查预览后保存。",
            12, FontWeights.Normal, "MutedBrush"));
        if (existingLapCount > 0)
            controls.Children.Add(Text(
                $"保存后将清除旧起终点线下的 {existingLapCount} 圈本地成绩。",
                12, FontWeights.SemiBold, "WarningBrush"));
        trace.Margin = new Thickness(0, 14, 0, 0);
        direction.Margin = new Thickness(0, 8, 0, 0);
        save.Margin = new Thickness(0, 8, 0, 0);
        foreach (var button in new[] { trace, direction, save })
        {
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
            controls.Children.Add(button);
        }
        status.Margin = new Thickness(0, 18, 0, 0);
        instruction.Margin = new Thickness(0, 5, 0, 0);
        metrics.Margin = new Thickness(0, 9, 0, 0);
        controls.Children.Add(status);
        controls.Children.Add(instruction);
        controls.Children.Add(metrics);
        var controlsPanel = Panel(controls, new Thickness(0));
        Grid.SetColumn(controlsPanel, 1);
        body.Children.Add(controlsPanel);
        Grid.SetRow(body, 1);
        root.Children.Add(body);

        var close = Button("取消");
        close.MinWidth = 96;
        close.HorizontalAlignment = HorizontalAlignment.Right;
        close.Margin = new Thickness(0, 14, 0, 0);
        close.Click += (_, _) => Close();
        Grid.SetRow(close, 2);
        root.Children.Add(close);
        return root;
    }

    private void Refresh()
    {
        var state = module.State;
        status.Text = state.Status;
        instruction.Text = state.Instruction;
        metrics.Text = $"描摹样本 {state.FirstTraceSamples} / {state.SecondTraceSamples}" +
                       (state.GateWidthMeters is double width
                           ? $"\n线宽 {width:0.00} m · 拟合误差 {state.FitRmsMeters:0.00} m · 双向偏移 {state.TraceOffsetMeters:0.00} m · 角差 {state.TraceAngleDegrees:0.00}°"
                           : string.Empty);
        preview.Update(module.EnrollmentPreview);
        trace.IsEnabled = state.Phase is EstateCircuitPhase.Idle or EstateCircuitPhase.CapturingFirstTrace or
            EstateCircuitPhase.CapturingSecondTrace or EstateCircuitPhase.AwaitingDirection;
        trace.Content = state.Phase switch
        {
            EstateCircuitPhase.CapturingFirstTrace => "停止第一次描摹",
            EstateCircuitPhase.CapturingSecondTrace => "停止第二次描摹并拟合",
            _ when state.FirstTraceSamples > 0 => "开始第二次反向描摹",
            _ => "开始第一次描摹"
        };
        direction.IsEnabled = state.Phase is EstateCircuitPhase.AwaitingDirection or EstateCircuitPhase.CapturingDirection;
        direction.Content = state.Phase == EstateCircuitPhase.CapturingDirection ? "停止并确认比赛方向" : "开始比赛方向采样";
        save.IsEnabled = state.Phase == EstateCircuitPhase.StartFinishReadyToSave;
    }

    private void Run(Action action)
    {
        try { action(); }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "重设起终点线", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        Refresh();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (saved || !module.State.IsEnrollmentActive) return;
        if (MessageBox.Show(this, "放弃本次起终点线重设？原定义不会改变。", "取消重设",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            e.Cancel = true;
            return;
        }
        module.CancelEnrollment();
    }

    private static Border Panel(UIElement child, Thickness margin) => new()
    {
        Background = Brush("PanelBrush"), BorderBrush = Brush("BorderBrush"), BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(10), Padding = new Thickness(16), Margin = margin, Child = child
    };

    private static Button Button(string content) => new()
    {
        Content = content, MinHeight = 42, Padding = new Thickness(16, 8, 16, 8), FontWeight = FontWeights.SemiBold
    };

    private static TextBlock Text(string value, double size, FontWeight weight, string brush = "TextBrush") => new()
    {
        Text = value, FontSize = size, FontWeight = weight, Foreground = Brush(brush), TextWrapping = TextWrapping.Wrap
    };

    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];
}
