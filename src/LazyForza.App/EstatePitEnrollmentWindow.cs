using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using LazyForza.Domain;
using LazyForza.Modules.LapAnalysis;

namespace LazyForza.App;

internal sealed class EstatePitEnrollmentWindow : Window
{
    private static readonly FontFamily UiFont = new("Microsoft YaHei UI");
    private readonly EstateCircuitModule module;
    private readonly Guid trackId;
    private readonly DispatcherTimer refreshTimer;
    private readonly TextBox laneWidth = Input("3.5");
    private readonly TextBox speedLimit = Input("80");
    private readonly TextBox serviceSeconds = Input("3");
    private readonly TextBlock phase = Text("尚未开始", 20, FontWeights.SemiBold);
    private readonly TextBlock status = Text("先确认参数，再点击“准备录入”。", 14);
    private readonly TextBlock instruction = Text("", 14, "MutedBrush");
    private readonly TextBlock counts = Text("通道样本 0 · 入口线未确认 · 出口线未确认 · 换胎区角点 0", 13, "MutedBrush");
    private readonly Button prepare = ActionButton("1  准备录入");
    private readonly Button lane = ActionButton("2  开始通道录入");
    private readonly Button entryGate = ActionButton("3  确认入口线");
    private readonly Button exitGate = ActionButton("4  确认出口线");
    private readonly Button corner = ActionButton("5  记录当前角点");
    private readonly Button clearCorners = ActionButton("清空角点");
    private readonly Button save = ActionButton("6  保存维修区");
    private bool acceptedClose;

    public EstatePitEnrollmentWindow(
        EstateCircuitModule module,
        TrackTemplate track,
        EstateTrackDefinition definition)
    {
        this.module = module;
        trackId = track.Id;
        Title = $"配置维修区 · {track.Name}";
        Width = 940;
        Height = 780;
        MinWidth = 820;
        MinHeight = 680;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brush("WindowBrush");
        Foreground = Brush("TextBrush");
        FontFamily = UiFont;
        Content = BuildContent(track, definition);

        prepare.Click += (_, _) => PrepareEnrollment();
        lane.Click += (_, _) => ToggleLaneCapture();
        entryGate.Click += (_, _) => CaptureEntryGate();
        exitGate.Click += (_, _) => CaptureExitGate();
        corner.Click += (_, _) => CaptureCorner();
        clearCorners.Click += (_, _) => Run(module.ClearServiceZoneCorners, "无法清空角点");
        save.Click += (_, _) => SaveEnrollment();
        refreshTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(200),
            DispatcherPriority.Background,
            (_, _) => Refresh(),
            Dispatcher);
        refreshTimer.Start();
        Closing += OnClosing;
        Closed += (_, _) => refreshTimer.Stop();
        Refresh();
    }

    private UIElement BuildContent(TrackTemplate track, EstateTrackDefinition definition)
    {
        var root = new Grid { Margin = new Thickness(30, 24, 30, 24) };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var stack = new StackPanel();
        stack.Children.Add(Text("维修区确定性录入", 30, FontWeights.SemiBold));
        stack.Children.Add(Text(
            "软件会用实际行驶轨迹建立维修区通道。通道录完后，你再把车停到希望设置的入口线和出口线中心分别确认；门线会贴合该处弯道的局部方向，不再由整段轨迹的首尾位置推测。维修停留区由停车记录的边界点围成。",
            14, "MutedBrush"));

        var trackCard = new StackPanel();
        trackCard.Children.Add(Text(track.Name, 18, FontWeights.SemiBold));
        trackCard.Children.Add(Text(
            $"地产修订 {definition.MapRevision} · {track.LengthMeters / 1000:0.00} km · " +
            (definition.Pit is null ? "尚未配置维修区" : $"已有 {definition.Pit.CenterLine.Count} 个通道点，将被本次录入替换"),
            13, "MutedBrush"));
        stack.Children.Add(Card(trackCard));

        var parameters = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        for (var index = 0; index < 3; index++)
            parameters.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        AddField(parameters, 0, "通道半宽（米）", "从中心线向左右各扩出的范围，通常 3–5 米。", laneWidth);
        AddField(parameters, 1, "维修区限速（km/h）", "用于赛事规则与超速提示，不是游戏内强制限速。", speedLimit);
        AddField(parameters, 2, "最短服务时间（秒）", "车辆在换胎区以不高于 5 km/h 连续停留达到此时间，软件才记为一次维修停留。", serviceSeconds);
        stack.Children.Add(Card(parameters));

        var live = new StackPanel();
        live.Children.Add(phase);
        status.Margin = new Thickness(0, 7, 0, 0);
        instruction.Margin = new Thickness(0, 4, 0, 0);
        counts.Margin = new Thickness(0, 10, 0, 0);
        live.Children.Add(status);
        live.Children.Add(instruction);
        live.Children.Add(counts);
        stack.Children.Add(Card(live));

        var steps = new StackPanel();
        steps.Children.Add(Text("实机步骤", 18, FontWeights.SemiBold));
        steps.Children.Add(Text(
            "1. 把车停在维修区入口前，确认车头朝正常进站方向。点击“准备录入”，再点击“开始通道录入”。\n" +
            "2. 以 2–25 km/h 沿通道中心完整驶到出口后。弯道要放慢并尽量贴着中心走；录入会保留约 0.75 米一级的折线细节。不要倒车、横穿或抄近路。驶出维修区后停车，再结束通道录入。\n" +
            "3. 回到想设为入口线的位置，把车停在通道中心约 1 秒，点击“确认入口线”；再到出口线位置用同样方法确认出口线。入口可以设在弯道上，软件会按附近通道的实际切线生成门线。\n" +
            "4. 回到换胎区，把车依次停在边界的四个角。每个位置停稳约 1 秒，再点“记录当前角点”。按顺时针或逆时针连续记录；不规则区域最多可记 8 个点。\n" +
            "5. 保存时会检查入口在出口之前、两条线间距以及通道是否正向穿过起终点平面。通过后，所有维修区几何会随 .lfzestate 文件导出。",
            14));
        steps.Children.Add(Text(
            "“正在维修区服务”只说明车辆位于换胎区；“维修停留完成”只说明连续低速停车达到设置时长。FH6 UDP 不提供轮胎磨损、车损或换胎完成字段，LazyForza 不会把这两个状态写成已验证换胎。",
            14, FontWeights.SemiBold, "WarningBrush"));
        stack.Children.Add(Card(steps));

        var scroll = new ScrollViewer
        {
            Content = stack,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        root.Children.Add(scroll);

        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        foreach (var button in new[] { prepare, lane, entryGate, exitGate, corner, clearCorners, save })
        {
            button.Margin = new Thickness(8, 0, 0, 0);
            buttons.Children.Add(button);
        }
        var close = ActionButton("关闭");
        close.Margin = new Thickness(16, 0, 0, 0);
        close.Click += (_, _) => Close();
        buttons.Children.Add(close);
        Grid.SetRow(buttons, 1);
        root.Children.Add(buttons);
        return root;
    }

    private void PrepareEnrollment()
    {
        acceptedClose = false;
        if (!double.TryParse(laneWidth.Text, out var width) ||
            !double.TryParse(speedLimit.Text, out var limit) ||
            !double.TryParse(serviceSeconds.Text, out var seconds))
        {
            MessageBox.Show(this, "请输入有效的通道半宽、限速和最短服务时间。", "参数无效", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Run(() => module.BeginPitEnrollment(new EstatePitEnrollmentRequest(trackId, width, limit, seconds)), "无法开始维修区录入");
    }

    private void ToggleLaneCapture()
    {
        Run(() =>
        {
            if (module.PitState.Phase == EstatePitCapturePhase.CapturingLane)
                module.StopPitLaneCapture();
            else
                module.StartPitLaneCapture();
        }, "维修区通道录入失败");
    }

    private void CaptureCorner() => Run(
        () => _ = module.CaptureServiceZoneCorner(),
        "无法记录换胎区角点");

    private void CaptureEntryGate() => Run(
        () => _ = module.CapturePitEntryGate(),
        "无法确认维修区入口线");

    private void CaptureExitGate() => Run(
        () => _ = module.CapturePitExitGate(),
        "无法确认维修区出口线");

    private void SaveEnrollment()
    {
        Run(() =>
        {
            _ = module.SavePitEnrollment();
            acceptedClose = true;
        }, "无法保存维修区");
    }

    private void Run(Action action, string title)
    {
        try { action(); }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        Refresh();
    }

    private void Refresh()
    {
        var state = module.PitState;
        phase.Text = state.Phase switch
        {
            EstatePitCapturePhase.CapturingLane => "当前阶段：维修区通道录入",
            EstatePitCapturePhase.AwaitingServiceCorners => "当前阶段：换胎区边界录入",
            EstatePitCapturePhase.ReadyToSave => "当前阶段：可以保存",
            EstatePitCapturePhase.Saved => "维修区录入完成",
            _ => "当前阶段：准备"
        };
        status.Text = state.Status;
        instruction.Text = state.Instruction;
        counts.Text = $"通道样本 {state.LaneSamples} · " +
                      $"入口线{(state.EntryLineCaptured ? "已确认" : "未确认")} · " +
                      $"出口线{(state.ExitLineCaptured ? "已确认" : "未确认")} · " +
                      $"换胎区边界点 {state.ServiceCorners}";
        prepare.IsEnabled = !state.IsActive || state.Phase == EstatePitCapturePhase.Idle;
        lane.IsEnabled = state.IsActive && state.Phase is EstatePitCapturePhase.Idle or EstatePitCapturePhase.CapturingLane or EstatePitCapturePhase.AwaitingServiceCorners;
        lane.Content = state.Phase == EstatePitCapturePhase.CapturingLane ? "2  结束通道录入" : "2  开始通道录入";
        entryGate.IsEnabled = state.Phase is EstatePitCapturePhase.AwaitingServiceCorners or EstatePitCapturePhase.ReadyToSave;
        exitGate.IsEnabled = entryGate.IsEnabled;
        entryGate.Content = state.EntryLineCaptured ? "3  重设入口线" : "3  确认入口线";
        exitGate.Content = state.ExitLineCaptured ? "4  重设出口线" : "4  确认出口线";
        corner.IsEnabled = state.Phase is EstatePitCapturePhase.AwaitingServiceCorners or EstatePitCapturePhase.ReadyToSave;
        clearCorners.IsEnabled = state.ServiceCorners > 0;
        save.IsEnabled = state.Phase == EstatePitCapturePhase.ReadyToSave &&
                         state.EntryLineCaptured && state.ExitLineCaptured;
    }

    private void OnClosing(object? sender, CancelEventArgs eventArgs)
    {
        if (acceptedClose || !module.PitState.IsActive) return;
        if (MessageBox.Show(this, "当前维修区录入尚未保存。确认放弃吗？", "放弃维修区录入",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            eventArgs.Cancel = true;
            return;
        }
        module.CancelPitEnrollment();
    }

    private static void AddField(Grid grid, int column, string title, string detail, TextBox input)
    {
        var stack = new StackPanel { Margin = new Thickness(8) };
        stack.Children.Add(Text(title, 14, FontWeights.SemiBold));
        stack.Children.Add(Text(detail, 12, "MutedBrush"));
        input.Margin = new Thickness(0, 8, 0, 0);
        stack.Children.Add(input);
        Grid.SetColumn(stack, column);
        grid.Children.Add(stack);
    }

    private static Border Card(UIElement content) => new()
    {
        Background = Brush("CardBrush"),
        BorderBrush = Brush("BorderBrush"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(9),
        Padding = new Thickness(18),
        Margin = new Thickness(0, 12, 0, 0),
        Child = content
    };

    private static TextBox Input(string value) => new()
    {
        Text = value,
        MinHeight = 40,
        Padding = new Thickness(10, 7, 10, 7),
        FontSize = 14,
        Background = Brush("InputBrush"),
        Foreground = Brush("TextBrush"),
        BorderBrush = Brush("BorderBrush")
    };

    private static Button ActionButton(string label) => new()
    {
        Content = label,
        MinHeight = 42,
        Padding = new Thickness(16, 8, 16, 8),
        FontSize = 14,
        FontWeight = FontWeights.SemiBold
    };

    private static TextBlock Text(string value, double size, FontWeight? weight = null, string? brush = null) => new()
    {
        Text = value,
        FontFamily = UiFont,
        FontSize = Math.Max(13, size),
        FontWeight = weight ?? FontWeights.Normal,
        Foreground = Brush(brush ?? "TextBrush"),
        TextWrapping = TextWrapping.Wrap,
        LineHeight = Math.Max(20, size * 1.55)
    };

    private static TextBlock Text(string value, double size, string brush) =>
        Text(value, size, null, brush);

    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];
}
