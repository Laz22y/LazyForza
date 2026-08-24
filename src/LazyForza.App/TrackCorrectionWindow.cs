using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LazyForza.Domain;
using LazyForza.Modules.LapAnalysis;

namespace LazyForza.App;

internal sealed class TrackCorrectionWindow : Window
{
    private readonly IReadOnlyList<TrackCorrectionCandidate> candidates;
    private readonly ListBox trackList;
    private readonly TextBlock evidence;
    private readonly Button apply;

    public TrackCorrectionWindow(
        Window owner,
        IReadOnlyList<TrackCorrectionCandidate> candidates)
    {
        this.candidates = candidates;
        Owner = owner;
        Title = "赛道识别纠错助手";
        Width = 680;
        Height = 560;
        MinWidth = 600;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = ResourceBrush("PanelBrush");
        Foreground = ResourceBrush("TextBrush");
        FontFamily = new FontFamily("Microsoft YaHei UI");

        var root = new Grid { Margin = new Thickness(24, 20, 24, 18) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var heading = new StackPanel();
        heading.Children.Add(new TextBlock
        {
            Text = "赛道识别纠错助手",
            FontSize = 20,
            FontWeight = FontWeights.SemiBold
        });
        heading.Children.Add(new TextBlock
        {
            Text = "前三候选优先排列；也可按官方赛事名称搜索全部兼容赛道。",
            FontSize = 12,
            Foreground = ResourceBrush("MutedBrush"),
            Margin = new Thickness(0, 5, 0, 0)
        });
        root.Children.Add(heading);

        var search = new TextBox
        {
            Margin = new Thickness(0, 15, 0, 10),
            Padding = new Thickness(10, 7, 10, 7),
            FontSize = 13,
            ToolTip = "输入赛道名称或类别"
        };
        Grid.SetRow(search, 1);
        root.Children.Add(search);

        trackList = new ListBox
        {
            Background = ResourceBrush("CardBrush"),
            Foreground = ResourceBrush("TextBrush"),
            BorderBrush = ResourceBrush("BorderBrush"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(5)
        };
        trackList.SelectionChanged += (_, _) => RefreshSelection();
        Grid.SetRow(trackList, 2);
        root.Children.Add(trackList);

        evidence = new TextBlock
        {
            Text = "请选择实际参加的官方赛事。",
            Margin = new Thickness(2, 11, 2, 8),
            FontSize = 12,
            Foreground = ResourceBrush("MutedBrush"),
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetRow(evidence, 3);
        root.Children.Add(evidence);

        var footer = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.Children.Add(new TextBlock
        {
            Text = "纠正只作用于当前比赛；为避免残缺圈速，将从下次经过起点后记录。",
            FontSize = 11,
            Foreground = ResourceBrush("MutedBrush"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 18, 0),
            VerticalAlignment = VerticalAlignment.Center
        });
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(new Button
        {
            Content = "取消",
            MinWidth = 76,
            IsCancel = true
        });
        apply = new Button
        {
            Content = "应用纠正",
            MinWidth = 104,
            IsDefault = true,
            IsEnabled = false,
            Background = ResourceBrush("AccentBrush"),
            BorderBrush = ResourceBrush("AccentBrush")
        };
        apply.Click += (_, _) => DialogResult = true;
        actions.Children.Add(apply);
        Grid.SetColumn(actions, 1);
        footer.Children.Add(actions);
        Grid.SetRow(footer, 4);
        root.Children.Add(footer);
        Content = root;

        search.TextChanged += (_, _) => RebuildList(search.Text);
        RebuildList(string.Empty);
        AppLocalization.ApplyTo(this);
    }

    public Guid? SelectedTrackId =>
        trackList.SelectedItem is ListBoxItem { Tag: TrackCorrectionCandidate candidate }
            ? candidate.TrackId
            : null;

    private void RebuildList(string query)
    {
        var normalized = query.Trim();
        var filtered = candidates.Where(candidate =>
                normalized.Length == 0 ||
                candidate.TrackName.Contains(normalized, StringComparison.CurrentCultureIgnoreCase) ||
                (candidate.Category?.Contains(normalized, StringComparison.CurrentCultureIgnoreCase) ?? false))
            .ToArray();
        trackList.Items.Clear();
        foreach (var candidate in filtered)
        {
            var layout = AppLocalization.Literal(
                candidate.LayoutKind == TrackLayoutKind.Circuit ? "环道" : "定点");
            var prefix = candidate.SuggestedRank is int rank
                ? AppLocalization.Format("track.correction.candidate", "候选 {0} · ", rank)
                : candidate.IsCurrentTrack ? AppLocalization.Literal("当前识别 · ") : string.Empty;
            trackList.Items.Add(new ListBoxItem
            {
                Tag = candidate,
                Padding = new Thickness(9, 7, 9, 7),
                Content = AppLocalization.Format(
                    "track.correction.item",
                    "{0}{1} · {2}/{3} · {4:0} m",
                    prefix,
                    AppLocalization.Literal(candidate.TrackName),
                    layout,
                    AppLocalization.Literal(candidate.Category ?? "未分类"),
                    candidate.LengthMeters)
            });
        }

        var preferred = trackList.Items
            .OfType<ListBoxItem>()
            .FirstOrDefault(item => item.Tag is TrackCorrectionCandidate { SuggestedRank: 1 }) ??
                        trackList.Items.OfType<ListBoxItem>()
                            .FirstOrDefault(item => item.Tag is TrackCorrectionCandidate { IsCurrentTrack: true });
        if (preferred is not null) trackList.SelectedItem = preferred;
        else if (trackList.Items.Count == 1) trackList.SelectedIndex = 0;
        RefreshSelection();
    }

    private void RefreshSelection()
    {
        if (trackList.SelectedItem is not ListBoxItem { Tag: TrackCorrectionCandidate selected })
        {
            evidence.Text = AppLocalization.Literal(candidates.Count == 0
                ? "当前没有与 Live 数据分区兼容的官方赛道。"
                : "请选择实际参加的官方赛事。");
            apply.IsEnabled = false;
            return;
        }
        evidence.Text = AppLocalization.Literal(selected.Evidence);
        apply.IsEnabled = true;
    }

    private static Brush ResourceBrush(string key) =>
        (Brush)Application.Current.FindResource(key);
}
