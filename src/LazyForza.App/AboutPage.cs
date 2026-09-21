using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LazyForza.Update;
using VectorPath = System.Windows.Shapes.Path;

namespace LazyForza.App;

internal sealed class AboutPage : ScrollViewer
{
    private readonly Action<string> openLink;
    private readonly Func<CancellationToken, Task<ReleaseHistorySnapshot>> loadHistory;
    private readonly StackPanel announcements = new();
    private readonly TextBlock historyStatus;
    private readonly Button refresh;
    private ReleaseHistorySnapshot? history;
    private CancellationTokenSource? lifetime;
    private bool loading;

    internal AboutPage(Action<string> openLink, ReleaseHistorySnapshot? history,
        Func<CancellationToken, Task<ReleaseHistorySnapshot>> loadHistory)
    {
        this.openLink = openLink;
        this.history = history;
        this.loadHistory = loadHistory;
        Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/LazyForza.App;component/AboutPage.xaml", UriKind.Relative)
        });
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        PanningMode = PanningMode.VerticalOnly;
        FontFamily = new FontFamily("Microsoft YaHei UI");
        SetResourceReference(ForegroundProperty, "TextBrush");
        var page = new StackPanel { MaxWidth = 1120, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 0, 12, 8) };
        var heading = Text(T("nav.about", "关于"), 28, bold: true);
        heading.Margin = new Thickness(0, 0, 150, 22);
        page.Children.Add(heading);
        page.Children.Add(Brand());

        page.Children.Add(Section(T("about.explore", "探索 LazyForza")));
        page.Children.Add(Row(
            Link(T("about.website", "官方网站"), "laz22y.github.io/LazyForza", "https://laz22y.github.io/LazyForza/", "globe"),
            Link(T("about.guide", "使用文档"), T("about.guideHint", "安装、设置与功能指南"), "https://laz22y.github.io/LazyForza/docs/", "book")));
        page.Children.Add(Section(T("about.repositories", "开源仓库")));
        page.Children.Add(Row(
            Link("GitHub", T("about.client", "LazyForza 客户端"), "https://github.com/Laz22y/LazyForza", "github"),
            Link("GitCode", T("about.client", "LazyForza 客户端"), "https://gitcode.com/Laz22y/LazyForza", "gitcode"),
            Link("GitHub", T("about.server", "RaceServer 服务端"), "https://github.com/Laz22y/LazyForza.RaceServer", "github")));

        var newsHeader = new DockPanel { Margin = new Thickness(0, 26, 0, 12), LastChildFill = true };
        refresh = Link(T("about.refresh", "刷新"), null, null, "refresh");
        refresh.Padding = new Thickness(12, 8, 12, 8);
        refresh.Click += async (_, _) => await RefreshAsync();
        DockPanel.SetDock(refresh, Dock.Right);
        newsHeader.Children.Add(refresh);
        var newsLabels = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
        newsLabels.Children.Add(Text(T("about.announcements", "更新公告"), 18, bold: true));
        historyStatus = Text("", 12, muted: true);
        historyStatus.Margin = new Thickness(0, 4, 0, 0);
        newsLabels.Children.Add(historyStatus);
        newsHeader.Children.Add(newsLabels);
        page.Children.Add(newsHeader);
        page.Children.Add(announcements);
        RenderHistory();

        var footer = Text(T("about.credit", "由 Laz22y 与开源贡献者共同构建 · MIT License"), 12, muted: true);
        footer.Margin = new Thickness(0, 18, 0, 0);
        page.Children.Add(footer);
        Content = page;
        Loaded += async (_, _) =>
        {
            lifetime?.Cancel();
            lifetime?.Dispose();
            lifetime = new CancellationTokenSource();
            if (history is null || DateTimeOffset.UtcNow - history.FetchedAt > TimeSpan.FromHours(1))
                await RefreshAsync();
        };
        Unloaded += (_, _) => { lifetime?.Cancel(); };
    }

    private Border Brand()
    {
        var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 720 };
        var logo = new Image
        {
            Source = new BitmapImage(new Uri("pack://application:,,,/LazyForza.App;component/Assets/LazyForzaWordmark.png")),
            MaxWidth = 510, Height = 94, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 2, 0, 14)
        };
        AutomationProperties.SetName(logo, "LazyForza");
        panel.Children.Add(logo);
        var tagline = Text(T("about.tagline", "Forza Horizon 6 的本地遥测、驾驶分析与地产赛事工具"), 13, muted: true);
        tagline.TextAlignment = TextAlignment.Center;
        panel.Children.Add(tagline);
        var metadata = new WrapPanel { Margin = new Thickness(0, 20, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
        var version = Text($"v{ApplicationVersionInfo.Display}", 14, bold: true);
        version.ToolTip = T("about.currentVersion", "当前版本");
        metadata.Children.Add(version);
        if (!string.IsNullOrWhiteSpace(ApplicationVersionInfo.ReleaseName))
        {
            var release = new StackPanel { Orientation = Orientation.Horizontal };
            var dot = Text("/", 14, muted: true);
            dot.Margin = new Thickness(14, 0, 14, 0);
            release.Children.Add(dot);
            var name = Text(ApplicationVersionInfo.ReleaseName, 14);
            name.ToolTip = T("about.releaseName", "版本更新名");
            release.Children.Add(name);
            metadata.Children.Add(release);
        }
        panel.Children.Add(metadata);
        panel.Children.Add(new Border
        {
            Height = 2, Width = 76, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 22, 0, 0),
            Background = new LinearGradientBrush(Color.FromRgb(32, 184, 207), Color.FromRgb(166, 111, 237), 0)
        });
        return new Border
        {
            Child = panel, Padding = new Thickness(24, 4, 24, 12)
        };
    }

    private static TextBlock Section(string label)
    {
        var text = Text(label, 14, bold: true);
        text.Margin = new Thickness(0, 23, 0, 10);
        return text;
    }

    private static Grid Row(params Button[] buttons)
    {
        var grid = new Grid();
        for (var i = 0; i < buttons.Length; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            buttons[i].Margin = new Thickness(i == 0 ? 0 : 6, 0, i == buttons.Length - 1 ? 0 : 6, 0);
            Grid.SetColumn(buttons[i], i);
            grid.Children.Add(buttons[i]);
        }
        return grid;
    }

    private Button Link(string title, string? subtitle, string? url, string icon)
    {
        var button = new Button { Style = (Style)Resources["AboutLinkCard"], ToolTip = url };
        AutomationProperties.SetName(button, subtitle is null ? title : $"{title} · {subtitle}");
        if (url is not null) button.Click += (_, _) => openLink(url);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var symbol = Icon(icon);
        symbol.Margin = new Thickness(0, 0, 12, 0);
        grid.Children.Add(symbol);
        var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        labels.Children.Add(Text(title, 14, bold: true));
        if (subtitle is not null)
        {
            var detail = Text(subtitle, 11, muted: true);
            detail.Margin = new Thickness(0, 4, 0, 0);
            labels.Children.Add(detail);
        }
        Grid.SetColumn(labels, 1); grid.Children.Add(labels);
        if (url is not null)
        {
            var arrow = Text("↗", 16, muted: true);
            arrow.Margin = new Thickness(8, 0, 0, 0);
            arrow.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(arrow, 2); grid.Children.Add(arrow);
        }
        button.Content = grid;
        return button;
    }

    private static FrameworkElement Icon(string kind)
    {
        if (kind is "github" or "gitcode")
        {
            var image = new BitmapImage(new Uri($"pack://application:,,,/LazyForza.App;component/Assets/{kind}.png"));
            return kind == "github"
                ? new Border { Width = 26, Height = 26, Background = Brushes.White, OpacityMask = new ImageBrush(image), VerticalAlignment = VerticalAlignment.Center }
                : new Image { Source = image, Width = 28, Height = 28, VerticalAlignment = VerticalAlignment.Center };
        }
        var data = kind switch
        {
            "globe" => "M 12 2 A 10 10 0 1 0 12 22 A 10 10 0 1 0 12 2 M 2 12 H 22 M 12 2 C 5 7 5 17 12 22 C 19 17 19 7 12 2",
            "book" => "M 12 5 C 8 2 3 3 2 4 L 2 20 C 5 18 9 19 12 21 C 15 19 19 18 22 20 L 22 4 C 18 2 15 3 12 5 L 12 21",
            _ => "M 21 8 A 9 9 0 1 0 21 16 M 21 2 L 21 8 L 15 8"
        };
        var path = new VectorPath { Data = Geometry.Parse(data), Width = kind == "refresh" ? 16 : 26, Height = kind == "refresh" ? 16 : 26,
            Stretch = Stretch.Uniform, StrokeThickness = 1.5, VerticalAlignment = VerticalAlignment.Center };
        path.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "AccentBrush");
        return path;
    }

    private void RenderHistory()
    {
        announcements.Children.Clear();
        if (history is null || history.Releases.Length == 0)
        {
            announcements.Children.Add(Text(T("about.noAnnouncements", "尚无缓存的更新公告。联网后点击刷新即可查看。"), 13, muted: true));
            historyStatus.Text = T("about.recentHint", "最近 5 个版本 · 展开查看更新详情");
            return;
        }
        historyStatus.Text = AppLocalization.Format("about.cachedAt", "{0} · 更新于 {1:g}", history.Releases[0].Source, history.FetchedAt.ToLocalTime());
        foreach (var release in history.Releases)
        {
            var header = new StackPanel();
            header.Children.Add(Text(release.Title, 14, bold: true));
            var channel = release.IsPreview ? T("about.preview", "预览版") : T("about.stable", "正式版");
            var date = release.PublishedAt?.ToLocalTime().ToString("yyyy.MM.dd");
            var meta = Text($"{release.Tag}   ·   {channel}{(date is null ? "" : "   ·   " + date)}", 11, muted: true);
            meta.Margin = new Thickness(0, 5, 0, 0);
            header.Children.Add(meta);
            if (announcements.Children.Count == 0)
            {
                var summary = UpdateReleaseMetadata.ToDisplayText(release.Notes, AppLocalization.CurrentLanguage)
                    .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                var intro = Text(AppLocalization.Literal(summary ?? ""), 12, muted: true);
                intro.Margin = new Thickness(0, 10, 0, 0);
                intro.MaxHeight = 36;
                intro.TextTrimming = TextTrimming.WordEllipsis;
                header.Children.Add(intro);
            }
            var body = new StackPanel();
            var notes = Text(AppLocalization.Literal(UpdateReleaseMetadata.ToDisplayText(release.Notes, AppLocalization.CurrentLanguage)), 13);
            notes.LineHeight = 23;
            body.Children.Add(notes);
            var source = Link(T("about.viewRelease", "查看完整发行说明"), null, release.PageUri.AbsoluteUri, "book");
            source.HorizontalAlignment = HorizontalAlignment.Left;
            source.Padding = new Thickness(12, 9, 12, 9);
            source.Margin = new Thickness(0, 14, 0, 0);
            body.Children.Add(source);
            var item = new Expander { Header = header, Content = body,
                Style = (Style)Resources["AboutAnnouncement"] };
            AutomationProperties.SetName(item, release.Title);
            announcements.Children.Add(item);
        }
    }

    private async Task RefreshAsync()
    {
        if (loading || lifetime is null || lifetime.IsCancellationRequested) return;
        var token = lifetime.Token;
        loading = true;
        refresh.IsEnabled = false;
        historyStatus.Text = T("about.loading", "正在加载更新公告…");
        try
        {
            var result = await loadHistory(token);
            if (token.IsCancellationRequested) return;
            history = result;
            RenderHistory();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (UpdateException)
        {
            if (!token.IsCancellationRequested)
                historyStatus.Text = history is null ? T("about.unavailable", "暂时无法加载，请稍后刷新。")
                    : T("about.offline", "暂时无法联网 · 正在显示已缓存的公告");
        }
        finally { loading = false; refresh.IsEnabled = true; }
    }

    private static string T(string key, string fallback) => AppLocalization.Text(key, fallback);
    private static TextBlock Text(string text, double size, bool bold = false, bool muted = false)
    {
        var label = new TextBlock { Text = text, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
            TextWrapping = TextWrapping.Wrap };
        label.SetResourceReference(TextBlock.ForegroundProperty, muted ? "MutedBrush" : "TextBrush");
        return label;
    }
}
