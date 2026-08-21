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
    private readonly TrackTemplate track;
    private readonly EstateTrackDefinition definition;
    private readonly Guid trackId;
    private readonly EstatePitEditScope editScope;
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
    private readonly EstateGeometryPreview preview = new();
    private ScrollViewer? contentScroll;
    private bool acceptedClose;

    public EstatePitEnrollmentWindow(
        EstateCircuitModule module,
        TrackTemplate track,
        EstateTrackDefinition definition,
        EstatePitEditScope editScope = EstatePitEditScope.All)
    {
        this.module = module;
        this.track = track;
        this.definition = definition;
        trackId = track.Id;
        this.editScope = editScope;
        Title = $"{ScopeTitle(editScope, definition.Pit is null)} · {track.Name}";
        Width = 940;
        Height = 780;
        MinWidth = 820;
        MinHeight = 680;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brush("WindowBrush");
        Foreground = Brush("TextBrush");
        FontFamily = UiFont;
        if (definition.Pit is { } existing)
        {
            laneWidth.Text = existing.LaneHalfWidthMeters.ToString("0.##");
            speedLimit.Text = existing.SpeedLimitKph.ToString("0.##");
            serviceSeconds.Text = existing.MinimumServiceSeconds.ToString("0.##");
        }
        Content = BuildContent(track, definition);
        ConfigureButtonLabels();

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
        Loaded += (_, _) =>
        {
            prepare.Focus();
            contentScroll?.ScrollToHome();
        };
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
        stack.Children.Add(Text(ScopeTitle(editScope, definition.Pit is null), 28, FontWeights.SemiBold));
        stack.Children.Add(Text(
            ScopeDescription(editScope),
            14, "MutedBrush"));

        var trackCard = new StackPanel();
        trackCard.Children.Add(Text(track.Name, 18, FontWeights.SemiBold));
        trackCard.Children.Add(Text(
            $"地产修订 {definition.MapRevision} · {track.LengthMeters / 1000:0.00} km · " +
            (definition.Pit is null ? "尚未配置维修区" : $"已有 {definition.Pit.CenterLine.Count} 个通道点 · 未选择的组件保持不变"),
            13, "MutedBrush"));
        stack.Children.Add(Card(trackCard));

        if (editScope != EstatePitEditScope.Settings)
        {
            preview.MinHeight = 280;
            preview.Update(track, definition);
            stack.Children.Add(Card(preview));
        }

        var parameters = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        for (var index = 0; index < 3; index++)
            parameters.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        AddField(parameters, 0, "通道半宽（米）", "从中心线向左右各扩出的范围，通常 3–5 米。", laneWidth);
        AddField(parameters, 1, "维修区限速（km/h）", "用于赛事规则与超速提示，不是游戏内强制限速。", speedLimit);
        AddField(parameters, 2, "最短服务时间（秒）", "车辆在换胎区以不高于 5 km/h 连续停留达到此时间，软件才记为一次维修停留。", serviceSeconds);
        var settingsEditable = editScope.HasFlag(EstatePitEditScope.Settings);
        laneWidth.IsEnabled = settingsEditable;
        speedLimit.IsEnabled = settingsEditable;
        serviceSeconds.IsEnabled = settingsEditable;
        if (settingsEditable) stack.Children.Add(Card(parameters));

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
            ScopeSteps(editScope),
            14));
        steps.Children.Add(Text(
            "LazyForza 记录车辆进入换胎区并完成设定停留；具体维修操作仍以游戏内实际操作为准。",
            13, FontWeights.Normal, "MutedBrush"));
        stack.Children.Add(Card(steps));

        contentScroll = new ScrollViewer
        {
            Content = stack,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        root.Children.Add(contentScroll);

        var buttons = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        lane.Visibility = editScope.HasFlag(EstatePitEditScope.Lane) ? Visibility.Visible : Visibility.Collapsed;
        entryGate.Visibility = editScope.HasFlag(EstatePitEditScope.EntryGate) ? Visibility.Visible : Visibility.Collapsed;
        exitGate.Visibility = editScope.HasFlag(EstatePitEditScope.ExitGate) ? Visibility.Visible : Visibility.Collapsed;
        corner.Visibility = editScope.HasFlag(EstatePitEditScope.ServiceZone) ? Visibility.Visible : Visibility.Collapsed;
        clearCorners.Visibility = corner.Visibility;
        var firstRow = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        var secondRow = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        var splitRows = editScope == EstatePitEditScope.All;
        foreach (var button in new[] { prepare, lane, entryGate, exitGate })
        {
            button.Margin = new Thickness(8, 0, 0, 0);
            firstRow.Children.Add(button);
        }
        foreach (var button in new[] { corner, clearCorners, save })
        {
            button.Margin = new Thickness(8, splitRows ? 8 : 0, 0, 0);
            (splitRows ? secondRow : firstRow).Children.Add(button);
        }
        var close = ActionButton("关闭");
        close.Margin = new Thickness(16, splitRows ? 8 : 0, 0, 0);
        close.Click += (_, _) => Close();
        (splitRows ? secondRow : firstRow).Children.Add(close);
        buttons.Children.Add(firstRow);
        if (splitRows) buttons.Children.Add(secondRow);
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
        Run(() => module.BeginPitEnrollment(new EstatePitEnrollmentRequest(trackId, width, limit, seconds, editScope)), "无法开始维修区录入");
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
            Close();
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
        if (state.IsActive) preview.Update(track, definition, module.PitEnrollmentPreview);
        if (!state.IsActive)
        {
            phase.Text = "当前阶段：准备";
            status.Text = editScope == EstatePitEditScope.Settings
                ? "已载入当前维修区设置。"
                : "点击“准备录入”后开始本次修改。";
            instruction.Text = editScope == EstatePitEditScope.Settings
                ? "确认参数后准备保存。"
                : ScopeSteps(editScope).Split('\n')[0];
            counts.Text = CountSummary(state);
        }
        else
        {
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
            counts.Text = CountSummary(state);
        }
        prepare.IsEnabled = !state.IsActive || state.Phase == EstatePitCapturePhase.Idle;
        lane.IsEnabled = editScope.HasFlag(EstatePitEditScope.Lane) && state.IsActive && state.Phase is EstatePitCapturePhase.Idle or EstatePitCapturePhase.CapturingLane or EstatePitCapturePhase.AwaitingServiceCorners or EstatePitCapturePhase.ReadyToSave;
        lane.Content = state.Phase == EstatePitCapturePhase.CapturingLane ? "2  结束通道录入" : "2  开始通道录入";
        entryGate.IsEnabled = editScope.HasFlag(EstatePitEditScope.EntryGate) && state.Phase is EstatePitCapturePhase.AwaitingServiceCorners or EstatePitCapturePhase.ReadyToSave;
        exitGate.IsEnabled = editScope.HasFlag(EstatePitEditScope.ExitGate) && state.Phase is EstatePitCapturePhase.AwaitingServiceCorners or EstatePitCapturePhase.ReadyToSave;
        var entryStep = editScope == EstatePitEditScope.All ? 3 : 2;
        var exitStep = editScope == EstatePitEditScope.All ? 4 : 2;
        entryGate.Content = state.EntryLineCaptured ? $"{entryStep}  重设入口线" : $"{entryStep}  确认入口线";
        exitGate.Content = state.ExitLineCaptured ? $"{exitStep}  重设出口线" : $"{exitStep}  确认出口线";
        corner.IsEnabled = editScope.HasFlag(EstatePitEditScope.ServiceZone) && state.Phase is EstatePitCapturePhase.AwaitingServiceCorners or EstatePitCapturePhase.ReadyToSave;
        clearCorners.IsEnabled = editScope.HasFlag(EstatePitEditScope.ServiceZone) && state.ServiceCorners > 0;
        save.IsEnabled = state.Phase == EstatePitCapturePhase.ReadyToSave &&
                         state.EntryLineCaptured && state.ExitLineCaptured;
    }

    private void ConfigureButtonLabels()
    {
        if (editScope == EstatePitEditScope.All) return;
        prepare.Content = editScope == EstatePitEditScope.Settings ? "1  准备保存" : "1  准备录入";
        lane.Content = "2  开始通道录入";
        corner.Content = "2  记录当前角点";
        save.Content = editScope == EstatePitEditScope.Settings ? "2  保存规则" : "3  保存";
    }

    private string CountSummary(EstatePitEnrollmentState state) => editScope switch
    {
        EstatePitEditScope.Lane => $"通道样本 {state.LaneSamples}",
        EstatePitEditScope.EntryGate => $"入口线{(state.EntryLineCaptured ? "已确认" : "待确认")}",
        EstatePitEditScope.ExitGate => $"出口线{(state.ExitLineCaptured ? "已确认" : "待确认")}",
        EstatePitEditScope.ServiceZone => $"换胎区边界点 {state.ServiceCorners}",
        EstatePitEditScope.Settings => "几何数据保持不变",
        _ => $"通道样本 {state.LaneSamples} · " +
             $"入口线{(state.EntryLineCaptured ? "已确认" : "未确认")} · " +
             $"出口线{(state.ExitLineCaptured ? "已确认" : "未确认")} · " +
             $"换胎区边界点 {state.ServiceCorners}"
    };

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

    private static string ScopeTitle(EstatePitEditScope scope, bool isNew) => isNew || scope == EstatePitEditScope.All
        ? "完整录入维修区"
        : scope switch
        {
            EstatePitEditScope.Lane => "重录维修区通道",
            EstatePitEditScope.EntryGate => "重设维修区入口线",
            EstatePitEditScope.ExitGate => "重设维修区出口线",
            EstatePitEditScope.ServiceZone => "重录换胎区",
            EstatePitEditScope.Settings => "修改维修区规则",
            _ => "编辑维修区"
        };

    private static string ScopeDescription(EstatePitEditScope scope) => scope switch
    {
        EstatePitEditScope.Lane => "只重新采集维修区中心通道。原入口线、出口线、换胎区和规则参数会保留，并在保存时重新校验。",
        EstatePitEditScope.EntryGate => "只重设入口线。把车停在新入口线中心约 1 秒后确认；其他维修区数据保持不变。",
        EstatePitEditScope.ExitGate => "只重设出口线。把车停在新出口线中心约 1 秒后确认；其他维修区数据保持不变。",
        EstatePitEditScope.ServiceZone => "只重录换胎区边界。依次在边界角点停车确认；通道、出入口和规则参数保持不变。",
        EstatePitEditScope.Settings => "只修改限速、通道宽度和最短服务时间，不需要重新驾驶录入。几何数据保持不变。",
        _ => "按比赛方向录入维修区通道，再分别确认入口线、出口线和换胎区边界。"
    };

    private static string ScopeSteps(EstatePitEditScope scope) => scope switch
    {
        EstatePitEditScope.Lane =>
            "点击准备后，从赛道分流点前开始，以 2–25 km/h 沿通道中心驶到并道点后。停车并结束录入，检查预览后保存。",
        EstatePitEditScope.EntryGate =>
            "点击准备，把车停在新入口线的通道中心约 1 秒，确认入口线。软件按通道局部方向生成门线；检查预览后保存。",
        EstatePitEditScope.ExitGate =>
            "点击准备，把车停在新出口线的通道中心约 1 秒，确认出口线。出口必须位于入口之后；检查预览后保存。",
        EstatePitEditScope.ServiceZone =>
            "点击准备，按顺时针或逆时针依次停在换胎区边界角点。每点停稳约 1 秒后记录，至少 4 点、最多 8 点；检查预览后保存。",
        EstatePitEditScope.Settings =>
            "修改参数后点击准备，再直接保存。限速是 LazyForza 赛事规则，不会改变游戏内车辆限速。",
        _ =>
            "1. 从分流点前开始，沿通道中心完整驶到并道点后。\n2. 在入口线和出口线中心分别停车确认。\n3. 按同一方向记录换胎区边界角点。\n4. 检查预览后保存。"
    };

    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];
}
