using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LazyForza.Domain;
using LazyForza.Modules.LapAnalysis;
using LazyForza.Storage;

namespace LazyForza.App;

internal sealed class EstateTrackEditorWindow : Window
{
    private readonly LazyForzaStore store;
    private readonly EstateCircuitModule module;
    private readonly Guid trackId;
    private readonly EstateGeometryPreview preview = new();
    private readonly TextBox mapName = Input();
    private readonly TextBox creator = Input();
    private readonly TextBox shareCode = Input();
    private readonly TextBox revision = Input();
    private readonly TextBlock metadataStatus = Text(string.Empty, 11, FontWeights.Normal, "AccentBrush");
    private readonly TextBlock summary = Text(string.Empty, 12, FontWeights.Normal, "MutedBrush");
    private readonly StackPanel componentList = new();
    private TrackTemplate track;
    private IReadOnlyList<SectorDefinition> sectors;
    private EstateTrackDefinition definition;

    public EstateTrackEditorWindow(
        LazyForzaStore store,
        EstateCircuitModule module,
        TrackTemplate track,
        IReadOnlyList<SectorDefinition> sectors,
        EstateTrackDefinition definition)
    {
        this.store = store;
        this.module = module;
        this.track = track;
        this.sectors = sectors;
        this.definition = definition;
        trackId = track.Id;
        Title = AppLocalization.Format("estate.editor.windowTitle", "编辑地产环道 · {0}", track.Name);
        Width = 1040;
        Height = 720;
        MinWidth = 900;
        MinHeight = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brush("WindowBrush");
        Foreground = Brush("TextBrush");
        FontFamily = new FontFamily("Microsoft YaHei UI");
        Content = BuildContent();
        RefreshData();
        AppLocalization.ApplyTo(this);
    }

    private UIElement BuildContent()
    {
        var root = new Grid { Margin = new Thickness(24) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
        header.Children.Add(Text("编辑地产环道", 26, FontWeights.SemiBold));
        header.Children.Add(Text(
            "每次只修改选中的组件，其他赛道数据保持不变。",
            13, FontWeights.Normal, "MutedBrush"));
        root.Children.Add(header);

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(410) });
        var left = new StackPanel();
        var previewHeader = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        previewHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        previewHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        previewHeader.Children.Add(Text("赛道定义预览", 17, FontWeights.SemiBold));
        var legend = Text("黄 起终点 · 紫 通道 · 绿/橙 出入口 · 绿色区域 换胎区", 11, FontWeights.Normal, "MutedBrush");
        Grid.SetColumn(legend, 1);
        previewHeader.Children.Add(legend);
        var previewWrap = new StackPanel();
        previewWrap.Children.Add(previewHeader);
        preview.MinHeight = 380;
        previewWrap.Children.Add(preview);
        left.Children.Add(Panel(previewWrap));
        summary.Margin = new Thickness(2, 0, 0, 0);
        left.Children.Add(summary);
        body.Children.Add(left);

        var right = new StackPanel();
        right.Children.Add(Panel(MetadataContent()));
        right.Children.Add(Panel(componentList));
        var rightScroll = new ScrollViewer
        {
            Content = right,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        Grid.SetColumn(rightScroll, 2);
        body.Children.Add(rightScroll);
        Grid.SetRow(body, 1);
        root.Children.Add(body);

        var close = ActionButton("完成");
        close.MinWidth = 100;
        close.HorizontalAlignment = HorizontalAlignment.Right;
        close.Margin = new Thickness(0, 16, 0, 0);
        close.Click += (_, _) => Close();
        Grid.SetRow(close, 2);
        root.Children.Add(close);
        return root;
    }

    private UIElement MetadataContent()
    {
        var stack = new StackPanel();
        stack.Children.Add(Text("地图信息", 17, FontWeights.SemiBold));
        AddField(stack, "地图名称", mapName);
        var pair = new Grid();
        pair.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        pair.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        pair.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        AddField(pair, "作者", creator, 0);
        AddField(pair, "修订号", revision, 2);
        stack.Children.Add(pair);
        AddField(stack, "分享代码或地图标识", shareCode);
        var save = ActionButton("保存地图信息");
        save.HorizontalAlignment = HorizontalAlignment.Stretch;
        save.Margin = new Thickness(0, 12, 0, 0);
        save.Click += (_, _) => SaveMetadata();
        stack.Children.Add(save);
        metadataStatus.Margin = new Thickness(0, 8, 0, 0);
        stack.Children.Add(metadataStatus);
        return stack;
    }

    private void BuildComponents()
    {
        componentList.Children.Clear();
        componentList.Children.Add(Text("可单独重设", 17, FontWeights.SemiBold));
        componentList.Children.Add(Component(
            "起终点线",
            AppLocalization.Format(
                "estate.editor.startFinishSummary",
                "宽 {0:0.0} m · RMS {1:0.00} m",
                GateWidth(definition.StartFinishGate),
                definition.StartFinishGate.FitRmsMeters),
            "重设",
            () => OpenStartFinish()));
        if (definition.Pit is null)
        {
            componentList.Children.Add(Component(
                "维修区",
                "尚未配置通道、出入口、换胎区和规则参数",
                "完整录入",
                () => OpenPit(EstatePitEditScope.All)));
            return;
        }

        var pit = definition.Pit;
        componentList.Children.Add(Component("维修区通道", AppLocalization.Format(
                "estate.editor.laneSummary", "{0} 个点 · 半宽 {1:0.0} m", pit.CenterLine.Count, pit.LaneHalfWidthMeters), "重录",
            () => OpenPit(EstatePitEditScope.Lane)));
        componentList.Children.Add(Component("维修区入口线", "保留通道、出口线和换胎区", "重设",
            () => OpenPit(EstatePitEditScope.EntryGate)));
        componentList.Children.Add(Component("维修区出口线", "保留通道、入口线和换胎区", "重设",
            () => OpenPit(EstatePitEditScope.ExitGate)));
        componentList.Children.Add(Component("换胎区", pit.ServiceZoneBoundary is { Count: > 0 } boundary
                ? AppLocalization.Format("estate.editor.serviceBoundary", "{0} 个边界点", boundary.Count)
                : AppLocalization.Format(
                    "estate.editor.legacyServiceZone", "旧版圆形区域 · 半径 {0:0.0} m", pit.ServiceRadiusMeters), "重录",
            () => OpenPit(EstatePitEditScope.ServiceZone)));
        componentList.Children.Add(Component("维修区规则", AppLocalization.Format(
                "estate.editor.rulesSummary",
                "限速 {0:0} km/h · 最短停留 {1:0.#} s",
                pit.SpeedLimitKph,
                pit.MinimumServiceSeconds), "修改",
            () => OpenPit(EstatePitEditScope.Settings)));
    }

    private Border Component(string title, string detail, string actionText, Action action)
    {
        var grid = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var copy = new StackPanel();
        copy.Children.Add(Text(title, 13, FontWeights.SemiBold));
        copy.Children.Add(Text(detail, 11, FontWeights.Normal, "MutedBrush"));
        grid.Children.Add(copy);
        var button = ActionButton(actionText);
        button.MinWidth = 74;
        button.Margin = new Thickness(10, 0, 0, 0);
        button.Click += (_, _) => action();
        Grid.SetColumn(button, 1);
        grid.Children.Add(button);
        return new Border
        {
            Background = Brush("CardBrush"), BorderBrush = Brush("BorderBrush"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(12), Child = grid
        };
    }

    private void SaveMetadata()
    {
        try
        {
            var name = mapName.Text.Trim();
            var nextRevision = revision.Text.Trim();
            if (name.Length is < 1 or > 80) throw new InvalidOperationException("地图名称长度应为 1–80 个字符。");
            if (nextRevision.Length is < 1 or > 32) throw new InvalidOperationException("修订号长度应为 1–32 个字符。");
            var nextCreator = NullIfWhiteSpace(creator.Text, 80, "作者");
            var nextShareCode = NullIfWhiteSpace(shareCode.Text, 80, "分享代码或地图标识");
            var revisionChanged = !string.Equals(nextRevision, definition.MapRevision, StringComparison.Ordinal);
            var removedLapCount = 0;
            if (revisionChanged)
            {
                removedLapCount = store.CountLaps(trackId);
                var message = removedLapCount > 0
                    ? AppLocalization.Format(
                        "estate.editor.revisionWithLaps",
                        "修订号将从“{0}”改为“{1}”。\n\n这会删除当前赛道旧修订下的 {2} 圈本地成绩，其他赛道不受影响。确认继续吗？",
                        definition.MapRevision,
                        nextRevision,
                        removedLapCount)
                    : AppLocalization.Format(
                        "estate.editor.revisionWithoutLaps",
                        "修订号将从“{0}”改为“{1}”。\n\n修订号应对应 FH6 地图的实际版本。确认继续吗？",
                        definition.MapRevision,
                        nextRevision);
                if (AppDialog.Show(this, message, AppLocalization.Literal("更新地图修订"),
                        MessageBoxButton.YesNo, MessageBoxImage.Warning) !=
                    MessageBoxResult.Yes)
                    return;
            }

            var now = DateTimeOffset.UtcNow;
            var nextTrack = track with { Name = name, UpdatedAt = now };
            var nextDefinition = definition with
            {
                MapName = name,
                Creator = nextCreator,
                ShareCode = nextShareCode,
                MapRevision = nextRevision,
                UpdatedAt = now
            };
            store.SaveTrack(nextTrack, sectors, nextDefinition, clearExistingLaps: revisionChanged);
            metadataStatus.Text = revisionChanged
                ? removedLapCount > 0
                    ? AppLocalization.Format(
                        "estate.editor.revisionSavedWithLaps",
                        "地图修订已更新，已清除 {0} 圈旧成绩。",
                        removedLapCount)
                    : AppLocalization.Literal("地图修订已更新。")
                : AppLocalization.Literal("地图信息已保存。");
            RefreshData();
        }
        catch (Exception exception)
        {
            AppDialog.Show(this, AppLocalization.Literal(exception.Message),
                AppLocalization.Literal("无法保存地图信息"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpenStartFinish()
    {
        try
        {
            new EstateStartFinishRevisionWindow(module, track, definition, store.CountLaps(trackId)) { Owner = this }.ShowDialog();
            RefreshData();
        }
        catch (Exception exception)
        {
            AppDialog.Show(this, AppLocalization.Literal(exception.Message),
                AppLocalization.Literal("无法重设起终点线"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpenPit(EstatePitEditScope scope)
    {
        try
        {
            new EstatePitEnrollmentWindow(module, track, definition, scope) { Owner = this }.ShowDialog();
            RefreshData();
        }
        catch (Exception exception)
        {
            AppDialog.Show(this, AppLocalization.Literal(exception.Message),
                AppLocalization.Literal("无法编辑维修区"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void RefreshData()
    {
        var loaded = store.LoadTrack(trackId) ?? throw new InvalidOperationException("赛道已经不存在。");
        track = loaded.Track;
        sectors = loaded.Sectors;
        definition = store.LoadEstateTrackDefinition(trackId) ?? throw new InvalidOperationException("地产赛道定义已经不存在。");
        mapName.Text = definition.MapName;
        creator.Text = definition.Creator ?? string.Empty;
        shareCode.Text = definition.ShareCode ?? string.Empty;
        revision.Text = definition.MapRevision;
        preview.Update(track, definition);
        var id = track.Id.ToString("N");
        summary.Text = AppLocalization.Format(
            "estate.editor.summary",
            "赛道 ID {0}…{1} · 修订 {2} · {3:0.00} km · {4} 个分段 · {5} 个检查点",
            id[..8],
            id[^5..],
            definition.MapRevision,
            track.LengthMeters / 1000,
            sectors.Count,
            definition.Checkpoints.Count);
        BuildComponents();
    }

    private static string? NullIfWhiteSpace(string value, int maximum, string name)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        if (normalized?.Length > maximum)
            throw new InvalidOperationException(AppLocalization.Format(
                "common.maximumCharacters",
                "{0}不能超过 {1} 个字符。",
                AppLocalization.Literal(name),
                maximum));
        return normalized;
    }

    private static double GateWidth(EstateTimingGate gate)
    {
        var dx = gate.Right.X - gate.Left.X;
        var dz = gate.Right.Z - gate.Left.Z;
        return Math.Sqrt(dx * dx + dz * dz);
    }

    private static Border Panel(UIElement child) => new()
    {
        Background = Brush("PanelBrush"), BorderBrush = Brush("BorderBrush"), BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(10), Padding = new Thickness(16), Margin = new Thickness(0, 0, 0, 12), Child = child
    };

    private static void AddField(StackPanel stack, string title, Control input)
    {
        var field = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        field.Children.Add(Text(title, 11, FontWeights.Normal, "MutedBrush"));
        input.Margin = new Thickness(0, 4, 0, 0);
        field.Children.Add(input);
        stack.Children.Add(field);
    }

    private static void AddField(Grid grid, string title, Control input, int column)
    {
        var field = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        field.Children.Add(Text(title, 11, FontWeights.Normal, "MutedBrush"));
        input.Margin = new Thickness(0, 4, 0, 0);
        field.Children.Add(input);
        Grid.SetColumn(field, column);
        grid.Children.Add(field);
    }

    private static TextBox Input() => new()
    {
        MinHeight = 38, Padding = new Thickness(9, 7, 9, 7), Background = Brush("CardBrush"),
        Foreground = Brush("TextBrush"), BorderBrush = Brush("BorderBrush"), CaretBrush = Brush("TextBrush")
    };

    private static Button ActionButton(string content) => new()
    {
        Content = AppLocalization.Literal(content), MinHeight = 38, Padding = new Thickness(14, 7, 14, 7), FontWeight = FontWeights.SemiBold
    };

    private static TextBlock Text(string value, double size, FontWeight weight, string brush = "TextBrush") => new()
    {
        Text = AppLocalization.Literal(value), FontSize = size, FontWeight = weight, Foreground = Brush(brush), TextWrapping = TextWrapping.Wrap
    };

    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];
}
