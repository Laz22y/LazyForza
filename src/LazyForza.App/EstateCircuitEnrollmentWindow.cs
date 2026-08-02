using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using LazyForza.Analysis;
using LazyForza.Modules.LapAnalysis;

namespace LazyForza.App;

internal sealed class EstateCircuitEnrollmentWindow : Window
{
    private readonly EstateCircuitModule module;
    private readonly TextBox mapName = Input("我的地产环道");
    private readonly TextBox creator = Input(string.Empty);
    private readonly TextBox shareCode = Input(string.Empty);
    private readonly TextBox revision = Input("1");
    private readonly ComboBox sectorCount = new()
    {
        ItemsSource = Enumerable.Range(
            TrackAlgorithms.MinimumSectorCount,
            TrackAlgorithms.MaximumSectorCount - TrackAlgorithms.MinimumSectorCount + 1),
        SelectedItem = 4,
        MinHeight = 38,
        Padding = new Thickness(10, 6, 10, 6)
    };
    private readonly TextBlock phase = Text(string.Empty, 16, FontWeights.SemiBold);
    private readonly TextBlock status = Text(string.Empty, 14, FontWeights.SemiBold);
    private readonly TextBlock instruction = Text(string.Empty, 13, FontWeights.Normal, "MutedBrush");
    private readonly TextBlock metrics = Text(string.Empty, 12, FontWeights.Normal, "MutedBrush");
    private readonly Button prepare = new() { Content = "1  准备录入" };
    private readonly Button trace = new() { Content = "2  开始第一次描摹" };
    private readonly Button direction = new() { Content = "3  开始比赛方向采样" };
    private readonly Button reference = new() { Content = "4  开始参考圈录入" };
    private readonly Button retryValidation = new() { Content = "重试验证圈" };
    private readonly Button cancel = new() { Content = "取消当前录入" };
    private readonly DispatcherTimer refreshTimer;
    private bool acceptedClose;

    public EstateCircuitEnrollmentWindow(EstateCircuitModule module)
    {
        this.module = module;
        Title = "添加地产环道";
        Width = 820;
        Height = 720;
        MinWidth = 720;
        MinHeight = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brush("WindowBrush");
        Foreground = Brush("TextBrush");
        FontFamily = new FontFamily("Microsoft YaHei UI");
        Content = BuildContent();

        prepare.Click += (_, _) => Execute(() => module.BeginEnrollment(new EstateEnrollmentRequest(
            mapName.Text,
            creator.Text,
            shareCode.Text,
            revision.Text,
            sectorCount.SelectedItem is int count ? count : 4)));
        trace.Click += (_, _) => Execute(() =>
        {
            if (module.State.Phase is EstateCircuitPhase.CapturingFirstTrace or EstateCircuitPhase.CapturingSecondTrace)
            {
                var result = module.StopLineTrace();
                if (result.Gate is not null || result.SampleCount > 0) status.Text = result.Explanation;
            }
            else module.StartLineTrace();
        });
        direction.Click += (_, _) => Execute(() =>
        {
            if (module.State.Phase == EstateCircuitPhase.CapturingDirection) module.StopDirectionCapture();
            else module.StartDirectionCapture();
        });
        reference.Click += (_, _) => Execute(module.StartReferenceLapCapture);
        retryValidation.Click += (_, _) => Execute(module.RetryValidationLap);
        cancel.Click += (_, _) =>
        {
            module.CancelEnrollment();
            acceptedClose = true;
            Close();
        };
        Closing += OnClosing;
        refreshTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(250),
            DispatcherPriority.Background,
            (_, _) => Refresh(),
            Dispatcher);
        refreshTimer.Start();
        Closed += (_, _) => refreshTimer.Stop();
        Refresh();
    }

    private UIElement BuildContent()
    {
        var root = new Grid { Margin = new Thickness(24) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 18) };
        header.Children.Add(Text("地产环道录入", 26, FontWeights.SemiBold));
        header.Children.Add(Text(
            "先用两次低速横穿确定终点线，再跑一圈参考路线和一圈验证路线。整个过程只读取 FH6 官方 UDP，不使用游戏圈数或官方计时器。",
            13,
            FontWeights.Normal,
            "MutedBrush"));
        root.Children.Add(header);

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var content = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
        content.Children.Add(Panel(MetadataGrid()));
        content.Children.Add(Panel(StatusContent()));
        content.Children.Add(Panel(WorkflowContent()));
        content.Children.Add(Panel(Text(
            "录入合格线：两次终点线描摹的拟合 RMS 不高于 0.25 m、位置偏移不超过 0.30 m、角度差不超过 0.50°。验证圈必须按顺序通过全部检查点；任何一步不合格，赛道都不会写入赛道库。",
            12,
            FontWeights.Normal,
            "MutedBrush")));
        scroll.Content = content;
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);

        var footer = new Grid { Margin = new Thickness(0, 16, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var close = new Button { Content = "关闭", MinWidth = 100 };
        close.Click += (_, _) =>
        {
            acceptedClose = module.State.Phase == EstateCircuitPhase.Ready || !module.State.IsEnrollmentActive;
            Close();
        };
        Grid.SetColumn(close, 1);
        footer.Children.Add(close);
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);
        return root;
    }

    private UIElement MetadataGrid()
    {
        var stack = new StackPanel();
        stack.Children.Add(Text("地图信息", 17, FontWeights.SemiBold));
        stack.Children.Add(Text(
            "地图名称和修订号用于区分不同版本，必须填写。作者和分享代码可以留空，但准备多人比赛时建议填全，方便确认所有人加载的是同一张地图。",
            12,
            FontWeights.Normal,
            "MutedBrush"));
        var grid = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        AddField(grid, "地图名称", mapName, 0, 0);
        AddField(grid, "作者", creator, 0, 1);
        AddField(grid, "分享代码或地图标识", shareCode, 1, 0);
        AddField(grid, "地图修订号", revision, 1, 1);
        AddField(grid, "圈速分析分段数（2–16）", sectorCount, 2, 0);
        var sectorHelp = Text(
            "这里设置的是 LazyForza 的计时和 HUD 分段，不是游戏官方赛段。录入完成后会随赛道文件一起保存。",
            11,
            FontWeights.Normal,
            "MutedBrush");
        sectorHelp.Margin = new Thickness(8, 17, 0, 6);
        Grid.SetRow(sectorHelp, 2);
        Grid.SetColumn(sectorHelp, 1);
        grid.Children.Add(sectorHelp);
        stack.Children.Add(grid);
        prepare.Margin = new Thickness(0, 12, 0, 0);
        prepare.HorizontalAlignment = HorizontalAlignment.Left;
        stack.Children.Add(prepare);
        return stack;
    }

    private UIElement StatusContent()
    {
        var stack = new StackPanel();
        stack.Children.Add(phase);
        status.Margin = new Thickness(0, 8, 0, 0);
        stack.Children.Add(status);
        instruction.Margin = new Thickness(0, 5, 0, 0);
        stack.Children.Add(instruction);
        metrics.Margin = new Thickness(0, 9, 0, 0);
        stack.Children.Add(metrics);
        return stack;
    }

    private UIElement WorkflowContent()
    {
        var stack = new StackPanel();
        stack.Children.Add(Text("实机步骤", 17, FontWeights.SemiBold));
        var steps = Text(
            "1. 填好地图信息，点击“准备录入”。\n\n" +
            "2. 把车停在棋盘格一侧，车头沿着终点横线摆正。点击“开始第一次描摹”，切回游戏，以约 2–16 km/h 沿横线开到另一侧，完整覆盖可行驶路面宽度；再切回工具停止。\n\n" +
            "3. 原地掉头，沿同一条横线反向再走一次。第二次停止后，软件会自动拟合终点门；两条轨迹不要错开，也不要斜着穿线。\n\n" +
            "4. 把车开到正常比赛方向的终点线前约 10 米。开始方向采样后，保持直行穿过终点线，并继续开到线后约 10 米再停止。\n\n" +
            "5. 开始参考圈录入。第一次正向过线开始记录，绕完整一圈后再次正向过线结束。不要在途中暂停、倒带、传送或离开地产。\n\n" +
            "6. 参考圈结束后不要停车，紧接着的一圈就是验证圈。验证圈通过后赛道才会保存；如果失败，按页面提示重跑验证圈即可。",
            12,
            FontWeights.Normal,
            "MutedBrush");
        steps.Margin = new Thickness(0, 8, 0, 0);
        stack.Children.Add(steps);
        var interruption = Text(
            "暂停游戏、打开菜单导致遥测中断，或使用倒带、传送造成时间/位置回退时，当前参考圈或验证圈会直接取消，不会带着残余数据继续录入。恢复行驶后，再从终点线正向过线重新开始这一圈。",
            12,
            FontWeights.SemiBold);
        interruption.Margin = new Thickness(0, 10, 0, 0);
        stack.Children.Add(interruption);
        var buttons = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(trace);
        buttons.Children.Add(direction);
        buttons.Children.Add(reference);
        buttons.Children.Add(retryValidation);
        buttons.Children.Add(cancel);
        stack.Children.Add(buttons);
        return stack;
    }

    private void Refresh()
    {
        var current = module.State;
        phase.Text = $"当前阶段：{PhaseText(current.Phase)}";
        status.Text = current.Status;
        instruction.Text = current.Instruction;
        metrics.Text =
            $"描摹样本 {current.FirstTraceSamples} / {current.SecondTraceSamples}" +
            (current.GateWidthMeters is double width
                ? $"   ·   终点门 {width:0.00} m   ·   RMS {current.FitRmsMeters:0.00} m   ·   偏移 {current.TraceOffsetMeters:0.00} m   ·   角差 {current.TraceAngleDegrees:0.00}°"
                : string.Empty) +
            (current.TotalCheckpoints > 0
                ? $"\n路线有效率 {current.ProjectionRatio:P0}   ·   检查点 {current.PassedCheckpoints}/{current.TotalCheckpoints}   ·   当前 {current.CurrentLapSeconds:0.0} s"
                : string.Empty);
        prepare.IsEnabled = !current.IsEnrollmentActive && !current.IsTimingActive;
        SetMetadataEnabled(prepare.IsEnabled);
        trace.IsEnabled = current.IsEnrollmentActive && current.Phase is
            EstateCircuitPhase.Idle or EstateCircuitPhase.CapturingFirstTrace or
            EstateCircuitPhase.CapturingSecondTrace or EstateCircuitPhase.AwaitingDirection;
        trace.Content = current.Phase switch
        {
            EstateCircuitPhase.CapturingFirstTrace => "2  停止第一次描摹",
            EstateCircuitPhase.CapturingSecondTrace => "2  停止第二次描摹并拟合",
            _ when current.FirstTraceSamples > 0 => "2  开始第二次反向描摹",
            _ => "2  开始第一次描摹"
        };
        direction.IsEnabled = current.Phase is EstateCircuitPhase.AwaitingDirection or EstateCircuitPhase.CapturingDirection;
        direction.Content = current.Phase == EstateCircuitPhase.CapturingDirection
            ? "3  停止并确认比赛方向"
            : "3  开始比赛方向采样";
        reference.IsEnabled = current.Phase is EstateCircuitPhase.AwaitingReferenceLap or EstateCircuitPhase.ValidationFailed;
        retryValidation.Visibility = current.Phase == EstateCircuitPhase.ValidationFailed ? Visibility.Visible : Visibility.Collapsed;
        cancel.IsEnabled = current.IsEnrollmentActive;
    }

    private void Execute(Action action)
    {
        try { action(); }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "地产环道录入", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        Refresh();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (acceptedClose || !module.State.IsEnrollmentActive) return;
        var result = MessageBox.Show(
            this,
            "关闭窗口会取消尚未完成的地产环道录入。仍要关闭吗？",
            "取消录入",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes)
        {
            e.Cancel = true;
            return;
        }
        module.CancelEnrollment();
    }

    private void SetMetadataEnabled(bool enabled)
    {
        mapName.IsEnabled = enabled;
        creator.IsEnabled = enabled;
        shareCode.IsEnabled = enabled;
        revision.IsEnabled = enabled;
        sectorCount.IsEnabled = enabled;
    }

    private static Border Panel(UIElement child) => new()
    {
        Background = Brush("PanelBrush"),
        BorderBrush = Brush("BorderBrush"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(10),
        Padding = new Thickness(16),
        Margin = new Thickness(0, 0, 0, 12),
        Child = child
    };

    private static void AddField(Grid grid, string label, Control input, int row, int column)
    {
        var stack = new StackPanel { Margin = new Thickness(column == 0 ? 0 : 8, 6, column == 0 ? 8 : 0, 6) };
        stack.Children.Add(Text(label, 12, FontWeights.Normal, "MutedBrush"));
        input.Margin = new Thickness(0, 5, 0, 0);
        stack.Children.Add(input);
        Grid.SetRow(stack, row);
        Grid.SetColumn(stack, column);
        grid.Children.Add(stack);
    }

    private static TextBox Input(string value) => new()
    {
        Text = value,
        MinHeight = 38,
        Padding = new Thickness(10, 7, 10, 7),
        Background = Brush("CardBrush"),
        Foreground = Brush("TextBrush"),
        BorderBrush = Brush("BorderBrush"),
        CaretBrush = Brush("TextBrush")
    };

    private static TextBlock Text(string value, double size, FontWeight weight, string brush = "TextBrush") => new()
    {
        Text = value,
        FontSize = size,
        FontWeight = weight,
        Foreground = Brush(brush),
        TextWrapping = TextWrapping.Wrap
    };

    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];

    private static string PhaseText(EstateCircuitPhase value) => value switch
    {
        EstateCircuitPhase.Idle => "准备",
        EstateCircuitPhase.CapturingFirstTrace => "第一次终点线描摹",
        EstateCircuitPhase.CapturingSecondTrace => "第二次反向描摹",
        EstateCircuitPhase.AwaitingDirection => "等待比赛方向采样",
        EstateCircuitPhase.CapturingDirection => "比赛方向采样",
        EstateCircuitPhase.AwaitingReferenceLap => "等待参考圈",
        EstateCircuitPhase.WaitingForReferenceStart => "等待首次过线",
        EstateCircuitPhase.CapturingReferenceLap => "参考圈录入",
        EstateCircuitPhase.ValidatingLap => "验证圈",
        EstateCircuitPhase.ValidationFailed => "验证未通过",
        EstateCircuitPhase.Ready => "录入完成",
        EstateCircuitPhase.WaitingForTimingStart => "等待计时起点",
        EstateCircuitPhase.TimingLap => "地产圈速计时",
        EstateCircuitPhase.Faulted => "异常",
        _ => value.ToString()
    };
}
