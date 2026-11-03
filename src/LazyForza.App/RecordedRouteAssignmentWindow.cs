using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using LazyForza.Analysis;
using LazyForza.Domain;

namespace LazyForza.App;

internal sealed class RecordedRouteAssignmentWindow : AppWindow
{
    internal Guid? TargetId { get; private set; }
    internal bool Force => force.IsChecked == true;
    private readonly CheckBox force;

    internal RecordedRouteAssignmentWindow(Window? owner, TrackTemplate source, int lapCount,
        IReadOnlyList<TrackTemplate> destinations, bool partial = false)
    {
        if (owner is not null) Owner = owner;
        Title = AppLocalization.Literal("归入已有赛道");
        Width = 680; Height = 590; MinWidth = 520; MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new Grid { Margin = new Thickness(24) };
        for (var i = 0; i < 5; i++) root.RowDefinitions.Add(new() { Height = i == 2 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
        var heading = Text(AppLocalization.Format("route.assign.source", "{0} · {1} 圈", AppLocalization.Literal(source.Name), lapCount), 16);
        heading.Margin = new Thickness(0, 0, 0, 16); root.Children.Add(heading);
        var selector = new ComboBox { HorizontalContentAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 0, 0, 14) };
        AutomationProperties.SetName(selector, AppLocalization.Literal("目标赛道"));
        var candidates = destinations.Select(target => (Target: target, Match: RecordedRouteAssociation.Compare(source, target, partial)))
            .OrderBy(candidate => candidate.Match.IsSevereMismatch).ThenBy(candidate => candidate.Match.MeanDistanceMeters)
            .ThenBy(candidate => candidate.Target.Name, StringComparer.CurrentCulture).ToArray();
        foreach (var candidate in candidates)
            selector.Items.Add(new ComboBoxItem { Tag = candidate.Target.Id, Content =
                $"{AppLocalization.Literal(candidate.Target.Category ?? "自定义赛道")} · {AppLocalization.Literal(candidate.Target.Name)}" });
        Grid.SetRow(selector, 1); root.Children.Add(selector);
        var map = new ContentControl { MinHeight = 100 };
        Grid.SetRow(map, 2); root.Children.Add(map);
        var details = new StackPanel { Margin = new Thickness(0, 12, 0, 16) };
        var legend = new WrapPanel();
        foreach (var (label, brush) in new[] { ("未收录路线", "MutedBrush"), ("目标赛道", "AccentBrush") })
        {
            var item = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 18, 0) };
            var line = new Border { Width = 18, Height = 3, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0) };
            line.SetResourceReference(BackgroundProperty, brush); item.Children.Add(line); item.Children.Add(Text(label, 12, "MutedBrush"));
            legend.Children.Add(item);
        }
        details.Children.Add(legend);
        var metrics = Text("", 12); metrics.Margin = new Thickness(0, 8, 0, 0); details.Children.Add(metrics);
        var warning = Text("路线明显不匹配。强制归入后不计入最快圈。", 13, "WarningBrush");
        warning.Margin = new Thickness(0, 10, 0, 0); details.Children.Add(warning);
        force = new CheckBox { Content = AppLocalization.Literal("强制归入"), Margin = new Thickness(0, 10, 0, 0) };
        details.Children.Add(force); Grid.SetRow(details, 3); root.Children.Add(details);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = AppLocalization.Literal("取消"), IsCancel = true, MinWidth = 88, Margin = new Thickness(0, 0, 10, 0) };
        var apply = new Button { Content = AppLocalization.Literal("归入"), IsDefault = true, MinWidth = 88, IsEnabled = false };
        apply.SetResourceReference(BackgroundProperty, "AccentBrush"); apply.SetResourceReference(ForegroundProperty, "WindowBrush");
        void RefreshApply() => apply.IsEnabled = TargetId is not null && (warning.Visibility != Visibility.Visible || Force);
        force.Checked += (_, _) => RefreshApply(); force.Unchecked += (_, _) => RefreshApply();
        selector.SelectionChanged += (_, _) =>
        {
            force.IsChecked = false;
            if (selector.SelectedIndex < 0) { TargetId = null; RefreshApply(); return; }
            var selected = candidates[selector.SelectedIndex]; TargetId = selected.Target.Id;
            metrics.Text = AppLocalization.Format("route.assign.metrics", "路线重合 {0:P0} · 平均偏差 {1:0.0} m · 长度差 {2:P0}",
                selected.Match.Coverage, selected.Match.MeanDistanceMeters, selected.Match.LengthDifference);
            warning.Visibility = force.Visibility = selected.Match.IsSevereMismatch ? Visibility.Visible : Visibility.Collapsed;
            map.Content = new RouteOverlay(source, selected.Target);
            RefreshApply();
        };
        apply.Click += (_, _) => { if (apply.IsEnabled) DialogResult = true; };
        actions.Children.Add(cancel); actions.Children.Add(apply); Grid.SetRow(actions, 4); root.Children.Add(actions);
        Content = root;
        if (candidates.Length > 0) selector.SelectedIndex = 0;
    }

    private static TextBlock Text(string text, double size, string brush = "TextBrush")
    {
        var result = new TextBlock { Text = AppLocalization.Literal(text), FontSize = size, TextWrapping = TextWrapping.Wrap };
        result.SetResourceReference(TextBlock.ForegroundProperty, brush); return result;
    }

    private sealed class RouteOverlay(TrackTemplate source, TrackTemplate target) : FrameworkElement
    {
        protected override void OnRender(DrawingContext dc)
        {
            if (ActualWidth < 1 || ActualHeight < 1) return;
            dc.DrawRoundedRectangle((Brush)FindResource("CardBrush"), new Pen((Brush)FindResource("BorderBrush"), 1),
                new Rect(0, 0, ActualWidth, ActualHeight), 8, 8);
            var all = source.Points.Concat(target.Points).ToArray();
            if (all.Length < 2) return;
            var minX = all.Min(point => point.X); var minZ = all.Min(point => point.Z);
            var width = Math.Max(1, all.Max(point => point.X) - minX); var height = Math.Max(1, all.Max(point => point.Z) - minZ);
            var scale = Math.Max(.0001, Math.Min((ActualWidth - 36) / width, (ActualHeight - 36) / height));
            Point Map(TrackPoint p) => new((ActualWidth - width * scale) / 2 + (p.X - minX) * scale,
                (ActualHeight + height * scale) / 2 - (p.Z - minZ) * scale);
            foreach (var (track, color, thickness) in new[] { (source, "MutedBrush", 4d), (target, "AccentBrush", 2d) })
            {
                if (track.Points.Count < 2) continue;
                var geometry = new StreamGeometry();
                using (var context = geometry.Open())
                {
                    context.BeginFigure(Map(track.Points[0]), false, false);
                    context.PolyLineTo(track.Points.Skip(1).Select(Map).ToArray(), true, true);
                }
                dc.DrawGeometry(null, new Pen((Brush)FindResource(color), thickness), geometry);
                dc.DrawEllipse((Brush)FindResource(color), null, Map(track.Points[0]), 4, 4);
            }
        }
    }
}
