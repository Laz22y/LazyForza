using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LazyForza.Domain;

namespace LazyForza.App;

internal sealed class LapBulkDeleteDialog : Window
{
    private readonly IReadOnlyList<LapSummary> laps;
    private readonly HashSet<int> selectedPerformanceClasses;
    private readonly CheckBox selectedClassesOnly;
    private readonly CheckBox deleteHistoricalBests;
    private readonly TextBlock preview;
    private readonly Button confirm;

    public LapBulkDeleteDialog(
        Window owner,
        string trackName,
        IReadOnlyList<LapSummary> laps,
        IReadOnlySet<int> selectedPerformanceClasses)
    {
        Owner = owner;
        this.laps = laps;
        this.selectedPerformanceClasses = selectedPerformanceClasses.ToHashSet();
        Title = "删除赛道圈速";
        Width = 540;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(14, 23, 33));
        Foreground = Brushes.White;
        FontFamily = new FontFamily("Microsoft YaHei UI");
        ShowInTaskbar = false;

        var root = new StackPanel { Margin = new Thickness(24) };
        root.Children.Add(Text("删除赛道记录", 21, FontWeights.SemiBold));
        root.Children.Add(Text($"{trackName} · {laps.Count}/50 圈", 12, FontWeights.Normal, Color.FromRgb(155, 170, 188), new Thickness(0, 4, 0, 18)));

        selectedClassesOnly = new CheckBox
        {
            Content = "仅删除已筛选的性能等级",
            IsChecked = false,
            IsEnabled = this.selectedPerformanceClasses.Count > 0,
            Foreground = Brushes.White,
            FontSize = 14,
            Margin = new Thickness(0, 0, 0, 4)
        };
        selectedClassesOnly.Click += (_, _) => UpdatePreview();
        root.Children.Add(selectedClassesOnly);
        root.Children.Add(Text(
            this.selectedPerformanceClasses.Count == 0
                ? "当前没有筛选性能等级。"
                : $"已筛选：{string.Join("、", this.selectedPerformanceClasses.Order().Select(PerformanceClassName))}；不勾选则删除全部等级。",
            11, FontWeights.Normal, Color.FromRgb(132, 150, 170), new Thickness(24, 0, 0, 14)));

        deleteHistoricalBests = new CheckBox
        {
            Content = "同时删除历史最快圈",
            IsChecked = false,
            Foreground = Brushes.White,
            FontSize = 14,
            Margin = new Thickness(0, 0, 0, 4)
        };
        deleteHistoricalBests.Click += (_, _) => UpdatePreview();
        root.Children.Add(deleteHistoricalBests);
        root.Children.Add(Text(
            "默认保留每个等级的最快有效圈。",
            11, FontWeights.Normal, Color.FromRgb(132, 150, 170), new Thickness(24, 0, 0, 16)));

        preview = Text(string.Empty, 13, FontWeights.Normal, Color.FromRgb(255, 190, 92), new Thickness(0, 0, 0, 18));
        preview.TextWrapping = TextWrapping.Wrap;
        root.Children.Add(preview);
        root.Children.Add(Text(
            "赛道模板会保留；圈速、分段和采样将永久删除。",
            11, FontWeights.Normal, Color.FromRgb(238, 142, 153), new Thickness(0, 0, 0, 20)));

        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button
        {
            Content = "取消",
            MinWidth = 88,
            Padding = new Thickness(14, 7, 14, 7),
            Margin = new Thickness(0, 0, 8, 0),
            IsCancel = true
        };
        confirm = new Button
        {
            Content = "确认删除",
            MinWidth = 104,
            Padding = new Thickness(14, 7, 14, 7),
            IsDefault = true,
            Background = new SolidColorBrush(Color.FromRgb(116, 35, 49)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(201, 80, 96)),
            Foreground = Brushes.White
        };
        confirm.Click += (_, _) => DialogResult = true;
        actions.Children.Add(cancel);
        actions.Children.Add(confirm);
        root.Children.Add(actions);

        Content = new Border
        {
            Margin = new Thickness(1),
            Padding = new Thickness(1),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromRgb(49, 69, 90)),
            Child = root
        };
        UpdatePreview();
    }

    public bool SelectedClassesOnly => selectedClassesOnly.IsChecked == true;
    public bool DeleteHistoricalBests => deleteHistoricalBests.IsChecked == true;

    private void UpdatePreview()
    {
        var candidates = SelectedClassesOnly
            ? laps.Where(lap => selectedPerformanceClasses.Contains(lap.Vehicle.CarClass)).ToArray()
            : laps.ToArray();
        var preserved = DeleteHistoricalBests
            ? []
            : candidates
                .Where(lap => lap.IsValid)
                .GroupBy(lap => lap.Vehicle.CarClass)
                .Select(group => group
                    .OrderBy(lap => lap.TotalSeconds)
                    .ThenBy(lap => lap.StartedAt)
                    .ThenBy(lap => lap.Id)
                    .First().Id)
                .ToArray();
        var deleteCount = candidates.Length - preserved.Length;
        var scope = SelectedClassesOnly
            ? string.Join("、", selectedPerformanceClasses.Order().Select(PerformanceClassName))
            : "全部性能等级";
        preview.Text = candidates.Length == 0
            ? $"范围：{scope} · 没有圈速记录"
            : $"范围：{scope} · 删除 {deleteCount} 圈 · 保留 {preserved.Length} 条最快圈";
        confirm.IsEnabled = deleteCount > 0;
    }

    private static TextBlock Text(
        string value,
        double size,
        FontWeight weight,
        Color? color = null,
        Thickness? margin = null) => new()
    {
        Text = value,
        FontSize = size <= 11 ? 13 : size,
        FontWeight = weight,
        Foreground = new SolidColorBrush(color ?? Colors.White),
        Margin = margin ?? new Thickness(0)
    };

    private static string PerformanceClassName(int value) => PerformanceClassCatalog.Name(value);
}
