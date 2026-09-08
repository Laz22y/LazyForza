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
            if (selected.Content is null && selected.Tag is Func<UIElement> build) selected.Content = build();
        };
        if (tabs.Items.Count > 0)
        {
            tabs.SelectedIndex = 0;
            var first = (TabItem)tabs.Items[0];
            first.Content ??= ((Func<UIElement>)first.Tag)();
        }
        return tabs;
    }

    private static Border AnalysisCard(UIElement child) => new()
    {
        Background = Brush("PanelBrush"), BorderBrush = Brush("BorderBrush"), BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(12), Padding = new Thickness(20), Margin = new Thickness(0, 0, 0, 16), Child = child
    };

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
}
