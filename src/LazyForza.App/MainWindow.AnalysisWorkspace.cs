using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace LazyForza.App;

internal sealed partial class MainWindow
{
    private static void ApplyAnalysisTheme(FrameworkElement element) =>
        element.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/LazyForza.App;component/AnalysisWorkspace.xaml", UriKind.Relative)
        });

    internal static TabControl AnalysisTabs(params (string Title, Func<UIElement> Build)[] pages)
    {
        var tabs = new TabControl();
        ApplyAnalysisTheme(tabs);
        foreach (var (title, build) in pages)
        {
            var tab = new TabItem { Header = AppLocalization.Literal(title), Tag = build };
            AutomationProperties.SetName(tab, AppLocalization.Literal(title));
            tabs.Items.Add(tab);
        }
        tabs.SelectionChanged += (_, args) =>
        {
            if (!ReferenceEquals(args.Source, tabs) || tabs.SelectedItem is not TabItem selected) return;
            if (selected.Content is null && selected.Tag is Func<UIElement> build) selected.Content = Build(build);
        };
        if (tabs.Items.Count > 0)
        {
            tabs.SelectedIndex = 0;
            var first = (TabItem)tabs.Items[0];
            first.Content ??= Build((Func<UIElement>)first.Tag);
        }
        return tabs;
        static UIElement Build(Func<UIElement> factory)
        {
            var content = factory();
            AppLocalization.ApplyTo(content);
            return content;
        }
    }

    private static Border AnalysisCard(UIElement child) => new()
    {
        Background = Brush("PanelBrush"), BorderBrush = Brush("BorderBrush"), BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(12), Padding = new Thickness(20), Margin = new Thickness(0, 0, 0, 16), Child = child
    };

    private static Border AnalysisEmptyState(string title, string message)
    {
        var content = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 520, Margin = new Thickness(12, 16, 12, 16)
        };
        content.Children.Add(new Border
        {
            Width = 64, Height = 64, CornerRadius = new CornerRadius(16),
            Background = Brush("AccentSoftBrush"), Padding = new Thickness(10),
            Margin = new Thickness(0, 0, 0, 18), Child = TrackSelectionIcon()
        });
        var heading = Label(title, 20, FontWeights.SemiBold);
        heading.TextAlignment = TextAlignment.Center;
        content.Children.Add(heading);
        var description = Label(message, 13, FontWeights.Normal, "MutedBrush");
        description.TextAlignment = TextAlignment.Center;
        description.TextWrapping = TextWrapping.Wrap;
        description.Margin = new Thickness(0, 10, 0, 0);
        content.Children.Add(description);
        var card = AnalysisCard(content);
        card.MinHeight = 220;
        card.Margin = new Thickness(0, 8, 0, 24);
        return card;
    }

    private static Viewbox TrackSelectionIcon()
    {
        var canvas = new Canvas { Width = 48, Height = 48, SnapsToDevicePixels = true };
        // Two edges define a road: a straight, a chicane and a hairpin, with a separate finish flag.
        Path("M 11,17 H 18 C 22,17 25,20 25,24 V 26 C 25,29 27,31 30,31 H 33 C 38,31 41,34 41,38 C 41,42 38,45 33,45 H 14 C 7,45 3,41 3,34 V 25 C 3,20 6,17 11,17 Z", 1.6);
        Path("M 11,22 H 18 C 19,22 20,23 20,24 V 26 C 20,32 24,36 30,36 H 33 C 35,36 36,37 36,38 C 36,39 35,40 33,40 H 14 C 10,40 8,38 8,34 V 25 C 8,23 9,22 11,22 Z", 1.3).Opacity = .65;
        Path("M 17,39 V 46 M 30,5 V 27", 1.8);
        var flag = Path("M 30,6 H 46 V 18 H 30 Z", 1.5);
        flag.Fill = Brush("PanelBrush");
        var checks = Path("M 30,6 H 34 V 10 H 30 Z M 38,6 H 42 V 10 H 38 Z M 34,10 H 38 V 14 H 34 Z M 42,10 H 46 V 14 H 42 Z M 30,14 H 34 V 18 H 30 Z M 38,14 H 42 V 18 H 38 Z", 0);
        checks.Fill = Brush("AccentBrush");
        return new Viewbox { Child = canvas, Stretch = System.Windows.Media.Stretch.Uniform };

        System.Windows.Shapes.Path Path(string data, double thickness)
        {
            var path = new System.Windows.Shapes.Path
            {
                Data = System.Windows.Media.Geometry.Parse(data), Stroke = Brush("AccentBrush"),
                StrokeThickness = thickness, StrokeLineJoin = System.Windows.Media.PenLineJoin.Round,
                StrokeStartLineCap = System.Windows.Media.PenLineCap.Round,
                StrokeEndLineCap = System.Windows.Media.PenLineCap.Round
            };
            canvas.Children.Add(path);
            return path;
        }
    }

    private static Border AnalysisBody(Border card)
    {
        card.BorderThickness = new Thickness(0);
        card.Padding = new Thickness(0);
        card.Margin = new Thickness(0);
        return card;
    }

    private static Expander AnalysisDisclosure(string title, UIElement content, bool expanded = false) => new()
    {
        Header = Label(title, 14, FontWeights.SemiBold), Content = content, IsExpanded = expanded,
        Style = (Style)Application.Current.FindResource("SettingsSectionExpander"), Margin = new Thickness(0, 0, 0, 12)
    };

    private static StackPanel AnalysisField(string title, FrameworkElement control, double width = double.NaN)
    {
        AutomationProperties.SetName(control, AppLocalization.Literal(title));
        var field = new StackPanel { Width = width, Margin = new Thickness(0, 0, 12, 12) };
        var caption = Label(title, 12, FontWeights.Normal, "MutedBrush");
        caption.Margin = new Thickness(0, 0, 0, 6);
        field.Children.Add(caption);
        control.MinHeight = 36;
        control.Margin = new Thickness(0);
        field.Children.Add(control);
        return field;
    }

    private static Button AnalysisButton(string title, bool primary = false)
    {
        var button = new Button
        {
            Content = AppLocalization.Literal(title), Padding = new Thickness(14, 8, 14, 8),
            MinHeight = 36, Margin = new Thickness(0, 0, 8, 8)
        };
        if (primary) { button.Background = Brush("AccentSoftBrush"); button.BorderBrush = Brush("AccentBrush"); }
        return button;
    }

    private static StackPanel PlaybackMetric(string title, TextBlock value, TextBlock detail)
    {
        var metric = new StackPanel { Margin = new Thickness(0, 4, 16, 0) };
        metric.Children.Add(Label(title, 12, FontWeights.Normal, "MutedBrush"));
        value.Margin = new Thickness(0, 5, 0, 0);
        value.ToolTip = detail.Text;
        metric.Children.Add(value);
        return metric;
    }
}
