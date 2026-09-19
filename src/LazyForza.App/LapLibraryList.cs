using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using LazyForza.Domain;
using LazyForza.Storage;

namespace LazyForza.App;

internal sealed class LapLibraryList : StackPanel
{
    internal LapLibraryList(IReadOnlyList<LapSummary> laps, ISet<Guid> selected, bool approximate,
        Action selectionChanged, Action<LapSummary> edit)
    {
        var search = new TextBox { MinWidth = 160, Margin = new Thickness(0, 0, 12, 0), MaxLength = 100 };
        search.ToolTip = AppLocalization.Literal("搜索名称、备注或车型");
        AutomationProperties.SetName(search, AppLocalization.Literal("搜索圈记录"));
        var favorites = new CheckBox { Content = AppLocalization.Literal("仅收藏"), VerticalAlignment = VerticalAlignment.Center };
        var filters = new DockPanel { Margin = new Thickness(0, 12, 0, 8) };
        DockPanel.SetDock(favorites, Dock.Right); filters.Children.Add(favorites);
        var searchLabel = new TextBlock { Text = AppLocalization.Literal("搜索"), VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0), Foreground = (Brush)Application.Current.Resources["MutedBrush"] };
        DockPanel.SetDock(searchLabel, Dock.Left); filters.Children.Add(searchLabel); filters.Children.Add(search);
        Children.Add(filters);
        var bestIds = laps.Where(lap => lap.IsValid && double.IsFinite(lap.TotalSeconds) && lap.TotalSeconds > 0)
            .GroupBy(lap => lap.Vehicle.CarClass).Select(group => group.OrderBy(lap => lap.TotalSeconds)
                .ThenBy(lap => lap.StartedAt).ThenBy(lap => lap.Id).First().Id).ToHashSet();
        var rows = laps.Select(lap => new LapLibraryRow(lap, selected, approximate, selectionChanged, edit, bestIds.Contains(lap.Id))).ToArray();
        var view = CollectionViewSource.GetDefaultView(rows);
        view.Filter = item => item is LapLibraryRow row && (!favorites.IsChecked.GetValueOrDefault() || row.Lap.Annotation.IsFavorite) &&
            (string.IsNullOrWhiteSpace(search.Text) || row.Search.Contains(search.Text.Trim(), StringComparison.CurrentCultureIgnoreCase));
        search.TextChanged += (_, _) => view.Refresh();
        favorites.Click += (_, _) => view.Refresh();
        var list = new ListBox { ItemsSource = view, MaxHeight = 320, MinHeight = 70,
            ItemTemplate = (DataTemplate)new ResourceDictionary
                { Source = new Uri("/LazyForza.App;component/AnalysisWorkspace.xaml", UriKind.Relative) }["LapLibraryRow"] };
        VirtualizingPanel.SetIsVirtualizing(list, true);
        VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
        ScrollViewer.SetCanContentScroll(list, true);
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        AutomationProperties.SetName(list, AppLocalization.Literal("圈记录库"));
        Children.Add(list);
    }
}

internal sealed class LapLibraryRow : INotifyPropertyChanged
{
    private readonly ISet<Guid> selected;
    public LapSummary Lap { get; }
    public string Name { get; }
    public string Detail { get; }
    public string Time { get; }
    public string Badges { get; }
    public string Search { get; }
    public string Tooltip { get; }
    public string EditLabel => AppLocalization.Literal("管理圈记录");
    public string SelectLabel => AppLocalization.Literal("勾选对比圈") + " · " + Name;
    public Brush TimeBrush { get; }
    public bool IsChosen => selected.Contains(Lap.Id);
    public ICommand SelectCommand { get; }
    public ICommand EditCommand { get; }
    public event PropertyChangedEventHandler? PropertyChanged;

    internal LapLibraryRow(LapSummary lap, ISet<Guid> selected, bool approximate, Action selectionChanged, Action<LapSummary> edit, bool historicalBest = false)
    {
        Lap = lap; this.selected = selected;
        Name = lap.Annotation.Name ?? VehicleNameCatalog.TryGetName(lap.Vehicle.CarOrdinal) ?? $"Car {lap.Vehicle.CarOrdinal}";
        Detail = $"{PerformanceClassCatalog.Name(lap.Vehicle.CarClass)} {lap.Vehicle.PerformanceIndex} · {lap.StartedAt.ToLocalTime():MM-dd HH:mm}" +
            (lap.PlayerCode is { Length: > 0 } player ? $" · {player}" : "");
        if (double.IsFinite(lap.TotalSeconds) && lap.TotalSeconds >= 0 && lap.TotalSeconds < TimeSpan.MaxValue.TotalSeconds)
        {
            var time = TimeSpan.FromSeconds(lap.TotalSeconds);
            Time = (approximate ? "≈" : "") + $"{(long)time.TotalMinutes}:{time.Seconds:00}.{time.Milliseconds:000}";
        }
        else Time = "—";
        Badges = string.Join(" · ", new[] { lap.Annotation.IsFavorite ? AppLocalization.Literal("收藏") : null,
            lap.Annotation.IsReference ? AppLocalization.Literal("固定参考") : null,
            !lap.IsValid ? AppLocalization.Literal("无效") : null }.Where(value => value is not null));
        TimeBrush = (Brush)Application.Current.Resources[!lap.IsValid ? "DangerBrush" : historicalBest ? "PurpleBrush" : lap.Annotation.IsReference ? "AccentBrush" : "TextBrush"];
        Search = $"{Name} {Detail} {lap.Vehicle.CarOrdinal} {lap.Annotation.Notes} {VehicleNameCatalog.TryGetName(lap.Vehicle.CarOrdinal)}";
        Tooltip = string.Join("\n", new[] { Name, Detail, lap.Annotation.Notes, lap.InvalidReason }.Where(value => !string.IsNullOrEmpty(value)));
        Tooltip = string.Join("\n", new[] { Badges, historicalBest ? AppLocalization.Literal("同等级最快") : null, Tooltip,
            string.Join("  ", lap.Segments.Select(segment => $"S{segment.Index + 1} {segment.TimeSeconds:0.000} s")) }.Where(value => !string.IsNullOrEmpty(value)));
        if (AppLocalization.CurrentLanguage == "en")
            Badges = string.Join(" · ", new[] { lap.Annotation.IsFavorite ? "★" : null,
                lap.Annotation.IsReference ? "REF" : null, !lap.IsValid ? "Invalid" : null }.Where(value => value is not null));
        SelectCommand = new LapAction(() =>
        {
            if (IsChosen) selected.Remove(lap.Id);
            else if (selected.Count < 4) selected.Add(lap.Id);
            PropertyChanged?.Invoke(this, new(nameof(IsChosen)));
            selectionChanged();
        });
        EditCommand = new LapAction(() => edit(lap));
    }

    private sealed class LapAction(Action execute) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => execute();
    }
}
