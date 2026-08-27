using System.ComponentModel;
using System.IO;
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
    private readonly EstateEnrollmentDraftStore draftStore;
    private readonly TextBox mapName = Input(AppLocalization.Literal("我的地产环道"));
    private readonly TextBox creator = Input(string.Empty);
    private readonly TextBox shareCode = Input(string.Empty);
    private readonly TextBox revision = Input("1");
    private readonly ComboBox sectorCount = new()
    {
        ItemsSource = Enumerable.Range(TrackAlgorithms.MinimumSectorCount,
            TrackAlgorithms.MaximumSectorCount - TrackAlgorithms.MinimumSectorCount + 1),
        SelectedItem = 4,
        MinHeight = 40,
        Padding = new Thickness(10, 7, 10, 7)
    };
    private readonly TextBlock phase = Text(string.Empty, 17, FontWeights.SemiBold);
    private readonly TextBlock status = Text(string.Empty, 14, FontWeights.SemiBold);
    private readonly TextBlock instruction = Text(string.Empty, 13, FontWeights.Normal, "MutedBrush");
    private readonly TextBlock metrics = Text(string.Empty, 12, FontWeights.Normal, "MutedBrush");
    private readonly TextBlock draftNotice = Text(string.Empty, 12, FontWeights.Normal, "AccentBrush");
    private readonly EstateGeometryPreview preview = new();
    private readonly Button prepare = ActionButton("准备录入");
    private readonly Button trace = ActionButton("开始第一次描摹");
    private readonly Button direction = ActionButton("开始比赛方向采样");
    private readonly Button reference = ActionButton("开始参考圈录入");
    private readonly Button retryValidation = ActionButton("重试验证圈");
    private readonly Button pause = ActionButton("暂存并关闭");
    private readonly Button cancel = ActionButton("放弃录入");
    private readonly Button resumeDraft = ActionButton("恢复暂存");
    private readonly Button discardDraft = ActionButton("删除暂存");
    private readonly DispatcherTimer refreshTimer;
    private Border? draftCard;
    private bool acceptedClose;

    public EstateCircuitEnrollmentWindow(EstateCircuitModule module, EstateEnrollmentDraftStore draftStore)
    {
        this.module = module;
        this.draftStore = draftStore;
        Title = "添加地产环道";
        Width = 1060;
        Height = 780;
        MinWidth = 900;
        MinHeight = 660;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brush("WindowBrush");
        Foreground = Brush("TextBrush");
        FontFamily = new FontFamily("Microsoft YaHei UI");
        Content = BuildContent();

        prepare.Click += (_, _) => PrepareEnrollment();
        trace.Click += (_, _) => Execute(() =>
        {
            if (module.State.Phase is EstateCircuitPhase.CapturingFirstTrace or EstateCircuitPhase.CapturingSecondTrace)
            {
                var result = module.StopLineTrace();
                if (result.Gate is not null || result.SampleCount > 0)
                    status.Text = AppLocalization.Literal(result.Explanation);
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
        pause.Click += (_, _) => PauseAndClose();
        cancel.Click += (_, _) => CancelEnrollment();
        resumeDraft.Click += (_, _) => ResumeSavedDraft();
        discardDraft.Click += (_, _) => DiscardSavedDraft();
        Closing += OnClosing;
        refreshTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background,
            (_, _) => Refresh(), Dispatcher);
        refreshTimer.Start();
        Closed += (_, _) => refreshTimer.Stop();
        RefreshDraftCard();
        Refresh();
        AppLocalization.ApplyTo(this);
    }

    private UIElement BuildContent()
    {
        var root = new Grid { Margin = new Thickness(24, 20, 24, 20) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
        header.Children.Add(Text("地产环道录入", 26, FontWeights.SemiBold));
        header.Children.Add(Text(
            "先沿起终点横线低速往返描摹，再直穿终点线确认比赛方向；随后连续完成参考圈和验证圈。每一步的实时轨迹都会显示在右侧。",
            13, FontWeights.Normal, "MutedBrush"));
        root.Children.Add(header);

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(390) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var leftStack = new StackPanel();
        draftCard = Panel(DraftContent());
        leftStack.Children.Add(draftCard);
        leftStack.Children.Add(Panel(MetadataContent()));
        leftStack.Children.Add(Panel(WorkflowContent()));
        body.Children.Add(new ScrollViewer
        {
            Content = leftStack,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        });

        var right = new StackPanel();
        var previewPanel = new Grid();
        previewPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        previewPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var previewHeader = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        previewHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        previewHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        previewHeader.Children.Add(Text("录入预览", 17, FontWeights.SemiBold));
        var legend = Text("青/紫 描摹 · 绿 方向 · 黄 起终点 · 白 车辆", 11, FontWeights.Normal, "MutedBrush");
        legend.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(legend, 1);
        previewHeader.Children.Add(legend);
        previewPanel.Children.Add(previewHeader);
        preview.MinHeight = 350;
        Grid.SetRow(preview, 1);
        previewPanel.Children.Add(preview);
        right.Children.Add(Panel(previewPanel));
        right.Children.Add(Panel(StatusContent()));
        Grid.SetColumn(right, 2);
        body.Children.Add(right);
        Grid.SetRow(body, 1);
        root.Children.Add(body);

        var footer = new Grid { Margin = new Thickness(0, 16, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        pause.Margin = new Thickness(0, 0, 8, 0);
        cancel.Margin = new Thickness(0, 0, 16, 0);
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(pause);
        actions.Children.Add(cancel);
        var close = ActionButton("关闭");
        close.MinWidth = 92;
        close.Click += (_, _) => Close();
        actions.Children.Add(close);
        Grid.SetColumn(actions, 1);
        footer.Children.Add(actions);
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);
        return root;
    }

    private UIElement DraftContent()
    {
        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var copy = new StackPanel();
        copy.Children.Add(Text("有未完成的录入", 15, FontWeights.SemiBold));
        draftNotice.Margin = new Thickness(0, 4, 12, 0);
        copy.Children.Add(draftNotice);
        root.Children.Add(copy);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        discardDraft.Margin = new Thickness(0, 0, 8, 0);
        actions.Children.Add(discardDraft);
        actions.Children.Add(resumeDraft);
        Grid.SetColumn(actions, 1);
        root.Children.Add(actions);
        return root;
    }

    private UIElement MetadataContent()
    {
        var stack = new StackPanel();
        stack.Children.Add(Text("地图信息", 17, FontWeights.SemiBold));
        stack.Children.Add(Text(
            "名称与修订号用于确认地图版本。作者、分享代码可留空；赛道文件另有稳定赛道标识，无需手工填写。",
            12, FontWeights.Normal, "MutedBrush"));
        AddField(stack, "地图名称", mapName);
        var pair = new Grid();
        pair.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        pair.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        pair.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        AddField(pair, "作者（可选）", creator, 0);
        AddField(pair, "修订号", revision, 2);
        stack.Children.Add(pair);
        AddField(stack, "分享代码或地图标识（可选）", shareCode);
        AddField(stack, "HUD 分段数", sectorCount);
        prepare.Margin = new Thickness(0, 12, 0, 0);
        prepare.HorizontalAlignment = HorizontalAlignment.Stretch;
        stack.Children.Add(prepare);
        return stack;
    }

    private UIElement WorkflowContent()
    {
        var stack = new StackPanel();
        stack.Children.Add(Text("操作顺序", 17, FontWeights.SemiBold));
        stack.Children.Add(Step("1", "描摹起终点横线", "车头沿横线摆正，以 2–12 km/h 从一侧驶到另一侧；掉头后沿同一条线反向再走一次。不是直穿终点线。"));
        stack.Children.Add(Step("2", "确认比赛方向", "停到终点线前约 10 米，开始采样后按正常比赛方向直穿终点线，再继续约 10 米。"));
        stack.Children.Add(Step("3", "参考圈与验证圈", "首次正向过线开始参考圈，第二次过线结束；紧接着完整跑一圈验证。暂停、倒带或传送会取消当前圈。"));
        foreach (var button in new[] { trace, direction, reference, retryValidation })
        {
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
            button.Margin = new Thickness(0, button == trace ? 12 : 8, 0, 0);
            stack.Children.Add(button);
        }
        return stack;
    }

    private UIElement StatusContent()
    {
        var stack = new StackPanel();
        stack.Children.Add(phase);
        status.Margin = new Thickness(0, 8, 0, 0);
        instruction.Margin = new Thickness(0, 5, 0, 0);
        metrics.Margin = new Thickness(0, 10, 0, 0);
        stack.Children.Add(status);
        stack.Children.Add(instruction);
        stack.Children.Add(metrics);
        return stack;
    }

    private void PrepareEnrollment()
    {
        if (draftStore.Exists && !module.State.IsEnrollmentActive)
        {
            if (AppDialog.Show(this,
                    AppLocalization.Literal("开始新的录入会删除现有暂存。继续吗？"),
                    AppLocalization.Literal("开始新录入"),
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            draftStore.Delete();
        }
        Execute(() => module.BeginEnrollment(new EstateEnrollmentRequest(
            mapName.Text, creator.Text, shareCode.Text, revision.Text,
            sectorCount.SelectedItem is int count ? count : 4)));
        RefreshDraftCard();
    }

    private void ResumeSavedDraft()
    {
        try
        {
            var draft = draftStore.Load() ?? throw new InvalidOperationException("暂存已经不存在。");
            module.ResumeEnrollment(draft);
            mapName.Text = draft.Enrollment.MapName;
            creator.Text = draft.Enrollment.Creator ?? string.Empty;
            shareCode.Text = draft.Enrollment.ShareCode ?? string.Empty;
            revision.Text = draft.Enrollment.MapRevision;
            sectorCount.SelectedItem = draft.Enrollment.SectorCount;
        }
        catch (Exception exception)
        {
            AppDialog.Show(this, AppLocalization.Literal(exception.Message),
                AppLocalization.Literal("无法恢复暂存"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        Refresh();
    }

    private void DiscardSavedDraft()
    {
        if (AppDialog.Show(this,
                AppLocalization.Literal("确认删除这份未完成的录入暂存？"),
                AppLocalization.Literal("删除暂存"),
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        draftStore.Delete();
        RefreshDraftCard();
    }

    private void PauseAndClose()
    {
        try
        {
            draftStore.Save(module.PauseEnrollmentForDraft());
            acceptedClose = true;
            Close();
        }
        catch (Exception exception)
        {
            AppDialog.Show(this, AppLocalization.Literal(exception.Message),
                AppLocalization.Literal("无法暂存录入"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void CancelEnrollment()
    {
        if (AppDialog.Show(this,
                AppLocalization.Literal("确认放弃当前录入？已完成但尚未保存的步骤会丢失。"),
                AppLocalization.Literal("放弃录入"),
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        module.CancelEnrollment();
        draftStore.Delete();
        acceptedClose = true;
        Close();
    }

    private void Refresh()
    {
        var current = module.State;
        phase.Text = AppLocalization.Format(
            "estate.enrollment.phase", "当前阶段 · {0}", PhaseText(current.Phase));
        status.Text = AppLocalization.Literal(current.Status);
        instruction.Text = AppLocalization.Literal(current.Instruction);
        metrics.Text = AppLocalization.Format(
            "estate.enrollment.traceSamples",
            "描摹样本  {0} / {1}",
            current.FirstTraceSamples,
            current.SecondTraceSamples) +
            (current.GateWidthMeters is double width
                ? AppLocalization.Format(
                    "estate.enrollment.gateMetrics",
                    "    起终点线 {0:0.00} m    拟合误差 {1:0.00} m",
                    width,
                    current.FitRmsMeters)
                : string.Empty) +
            (current.TotalCheckpoints > 0
                ? AppLocalization.Format(
                    "estate.enrollment.routeMetrics",
                    "\n路线覆盖 {0:P0}    检查点 {1}/{2}    当前 {3:0.0} s",
                    current.ProjectionRatio,
                    current.PassedCheckpoints,
                    current.TotalCheckpoints,
                    current.CurrentLapSeconds)
                : string.Empty);
        preview.Update(module.EnrollmentPreview);
        prepare.IsEnabled = !current.IsEnrollmentActive && !current.IsTimingActive;
        SetMetadataEnabled(prepare.IsEnabled);
        trace.IsEnabled = current.IsEnrollmentActive && current.Phase is EstateCircuitPhase.Idle or
            EstateCircuitPhase.CapturingFirstTrace or EstateCircuitPhase.CapturingSecondTrace or EstateCircuitPhase.AwaitingDirection;
        trace.Content = AppLocalization.Literal(current.Phase switch
        {
            EstateCircuitPhase.CapturingFirstTrace => "停止第一次描摹",
            EstateCircuitPhase.CapturingSecondTrace => "停止第二次描摹并拟合",
            _ when current.FirstTraceSamples > 0 => "开始第二次反向描摹",
            _ => "开始第一次描摹"
        });
        direction.IsEnabled = current.Phase is EstateCircuitPhase.AwaitingDirection or EstateCircuitPhase.CapturingDirection;
        direction.Content = AppLocalization.Literal(
            current.Phase == EstateCircuitPhase.CapturingDirection
                ? "停止并确认比赛方向"
                : "开始比赛方向采样");
        reference.IsEnabled = current.Phase == EstateCircuitPhase.AwaitingReferenceLap;
        retryValidation.Visibility = current.Phase == EstateCircuitPhase.ValidationFailed ? Visibility.Visible : Visibility.Collapsed;
        pause.IsEnabled = current.IsEnrollmentActive;
        cancel.IsEnabled = current.IsEnrollmentActive;
        if (current.Phase == EstateCircuitPhase.Ready)
        {
            draftStore.Delete();
            acceptedClose = true;
            RefreshDraftCard();
        }
    }

    private void RefreshDraftCard()
    {
        if (draftCard is null) return;
        EstateEnrollmentDraft? draft = null;
        try { draft = draftStore.Load(); }
        catch (InvalidDataException exception) { draftNotice.Text = AppLocalization.Literal(exception.Message); }
        draftCard.Visibility = draftStore.Exists && !module.State.IsEnrollmentActive ? Visibility.Visible : Visibility.Collapsed;
        if (draft is not null)
            draftNotice.Text = AppLocalization.Format(
                "estate.enrollment.draft",
                "{0} · 修订 {1} · 暂存于 {2:MM-dd HH:mm}",
                draft.Enrollment.MapName,
                draft.Enrollment.MapRevision,
                draft.SavedAt.ToLocalTime());
    }

    private void Execute(Action action)
    {
        try { action(); }
        catch (Exception exception)
        {
            AppDialog.Show(this, AppLocalization.Literal(exception.Message),
                AppLocalization.Literal("地产环道录入"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        Refresh();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (acceptedClose || !module.State.IsEnrollmentActive) return;
        var result = AppDialog.Show(this,
            AppLocalization.Literal("录入尚未完成。\n\n选择“是”暂存并关闭；选择“否”放弃录入；选择“取消”返回向导。"),
            AppLocalization.Literal("关闭录入向导"), MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (result == MessageBoxResult.Cancel) { e.Cancel = true; return; }
        if (result == MessageBoxResult.Yes)
        {
            try { draftStore.Save(module.PauseEnrollmentForDraft()); }
            catch (Exception exception)
            {
                AppDialog.Show(this, AppLocalization.Literal(exception.Message),
                    AppLocalization.Literal("无法暂存录入"), MessageBoxButton.OK, MessageBoxImage.Warning);
                e.Cancel = true;
            }
            return;
        }
        module.CancelEnrollment();
        draftStore.Delete();
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
        Background = Brush("PanelBrush"), BorderBrush = Brush("BorderBrush"), BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(10), Padding = new Thickness(16), Margin = new Thickness(0, 0, 0, 12), Child = child
    };

    private static Border Step(string number, string title, string detail)
    {
        var grid = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(new Border
        {
            Width = 26, Height = 26, CornerRadius = new CornerRadius(13), Background = Brush("AccentBrush"),
            Child = new TextBlock { Text = number, Foreground = Brush("WindowBrush"), FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
        });
        var copy = new StackPanel();
        copy.Children.Add(Text(title, 13, FontWeights.SemiBold));
        copy.Children.Add(Text(detail, 12, FontWeights.Normal, "MutedBrush"));
        Grid.SetColumn(copy, 1);
        grid.Children.Add(copy);
        return new Border { Child = grid };
    }

    private static void AddField(StackPanel stack, string label, Control input)
    {
        var field = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        field.Children.Add(Text(label, 12, FontWeights.Normal, "MutedBrush"));
        input.Margin = new Thickness(0, 5, 0, 0);
        field.Children.Add(input);
        stack.Children.Add(field);
    }

    private static void AddField(Grid grid, string label, Control input, int column)
    {
        var field = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        field.Children.Add(Text(label, 12, FontWeights.Normal, "MutedBrush"));
        input.Margin = new Thickness(0, 5, 0, 0);
        field.Children.Add(input);
        Grid.SetColumn(field, column);
        grid.Children.Add(field);
    }

    private static TextBox Input(string value) => new()
    {
        Text = value, MinHeight = 40, Padding = new Thickness(10, 8, 10, 8),
        Background = Brush("CardBrush"), Foreground = Brush("TextBrush"), BorderBrush = Brush("BorderBrush"), CaretBrush = Brush("TextBrush")
    };

    private static Button ActionButton(string content) => new()
    {
        Content = AppLocalization.Literal(content), MinHeight = 42, Padding = new Thickness(16, 8, 16, 8), FontWeight = FontWeights.SemiBold
    };

    private static TextBlock Text(string value, double size, FontWeight weight, string brush = "TextBrush") => new()
    {
        Text = AppLocalization.Literal(value), FontSize = size, FontWeight = weight, Foreground = Brush(brush), TextWrapping = TextWrapping.Wrap
    };

    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];

    private static string PhaseText(EstateCircuitPhase value) => AppLocalization.Literal(value switch
    {
        EstateCircuitPhase.Idle => "准备",
        EstateCircuitPhase.CapturingFirstTrace => "第一次横线描摹",
        EstateCircuitPhase.CapturingSecondTrace => "第二次反向描摹",
        EstateCircuitPhase.AwaitingDirection => "等待比赛方向采样",
        EstateCircuitPhase.CapturingDirection => "比赛方向采样",
        EstateCircuitPhase.AwaitingReferenceLap => "等待参考圈",
        EstateCircuitPhase.WaitingForReferenceStart => "等待首次过线",
        EstateCircuitPhase.CapturingReferenceLap => "参考圈录入",
        EstateCircuitPhase.ValidatingLap => "验证圈",
        EstateCircuitPhase.ValidationFailed => "验证未通过",
        EstateCircuitPhase.Ready => "录入完成",
        EstateCircuitPhase.Faulted => "异常",
        _ => value.ToString()
    });
}
