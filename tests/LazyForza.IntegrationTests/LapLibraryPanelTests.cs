using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using LazyForza.App;
using LazyForza.Domain;
using LazyForza.Storage;

namespace LazyForza.IntegrationTests;

[TestClass]
public sealed class LapLibraryPanelTests
{
    [TestMethod]
    [DataRow("zh-Hans", 620)]
    [DataRow("en", 920)]
    [DoNotParallelize]
    public void TenThousandLapLibraryIsVirtualizedSearchableAndKeepsComparisonBounded(string language, int width) => WpfTestHost.Run(() =>
    {
        AppLocalization.UseLanguage(language);
        var first = new LapSummary(Guid.NewGuid(), Guid.NewGuid(), 1, 2, Guid.NewGuid(), new(1, 5, 850, 2, 8, 8000, "g", "c"),
            DateTimeOffset.UtcNow, 92.123, true, null, []) { Annotation = new("Evening practice", "Late braking into turn 3", true, true) };
        var laps = Enumerable.Range(0, 10000).Select(i => first with { Id = Guid.NewGuid(), StartedAt = first.StartedAt.AddMinutes(-i),
            Annotation = i == 0 ? first.Annotation : new(Name: $"Lap {i}") }).ToArray();
        var selected = new HashSet<Guid>(); LapSummary? edited = null;
        var panel = new LapLibraryList(laps, selected, false, () => { }, lap => edited = lap);
        var root = Frame(panel, width);
        Layout(root, width);
        var list = Descendants<ListBox>(panel).Single();
        Assert.AreEqual(10000, list.Items.Count);
        Assert.IsTrue(VirtualizingPanel.GetIsVirtualizing(list));
        Assert.IsNull(list.ItemContainerGenerator.ContainerFromIndex(9999), "Off-screen records must not create thousands of UI rows.");
        foreach (var row in list.Items.Cast<LapLibraryRow>().Take(5)) row.SelectCommand.Execute(null);
        Assert.AreEqual(4, selected.Count);
        var firstRow = (LapLibraryRow)list.Items[0]; firstRow.EditCommand.Execute(null);
        Assert.AreEqual(laps[0].Id, edited!.Id);
        Snapshot(root, $"library-{language}-{width}");
        var search = Descendants<TextBox>(panel).Single(); search.Text = "Late braking";
        Assert.AreEqual(1, list.Items.Count);
        search.Text = "";
        var favorite = Descendants<CheckBox>(panel).First(); favorite.IsChecked = true;
        favorite.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Assert.AreEqual(1, list.Items.Count);
        var editor = new LapRecordEditor(null, first);
        var content = (FrameworkElement)editor.Content;
        editor.Content = null;
        var editorFrame = Frame(content, 520); editorFrame.Padding = new Thickness(0);
        Layout(editorFrame, 520);
        Assert.AreEqual(first.Annotation, editor.Annotation);
        Assert.IsTrue(Descendants<TextBlock>(editorFrame).Any(text => text.Text == (language == "en" ? "Notes" : "备注")));
        Snapshot(editorFrame, $"editor-{language}");
        editor.Close();
    });

    [TestMethod]
    [DataRow(false, 440)]
    [DataRow(true, 740)]
    [DoNotParallelize]
    public void CapacitySettingsValidateSaveAndKeepSpaceDetailsFolded(bool english, int width)
    {
        var path = Path.Combine(Path.GetTempPath(), $"lap-capacity-ui-{Guid.NewGuid():N}.db");
        try
        {
            WpfTestHost.Run(() =>
            {
                AppLocalization.UseLanguage(english ? "en" : "zh-Hans");
                using var store = new LazyForzaStore(path);
                var panel = new LapStoragePanel(store, english);
                panel.SetUsage(new(324, 54_000_000, 80_000_000, 8_000_000));
                var field = Descendants<TextBox>(panel).Single();
                var save = Descendants<Button>(panel).Single();
                field.Text = "10001"; save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.AreEqual(500, store.LapCapacity);
                field.Text = "2000"; save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.AreEqual(2000, store.LapCapacity);
                var details = Descendants<Expander>(panel).Single(); Assert.IsFalse(details.IsExpanded);
                var root = Frame(panel, width); Layout(root, width); Snapshot(root, $"storage-{english}-{width}-collapsed");
                details.IsExpanded = true; Layout(root, width); Snapshot(root, $"storage-{english}-{width}-expanded");
                Assert.IsTrue(root.ActualHeight < 700);
                Assert.IsTrue(Descendants<TextBlock>(panel).Any(text => text.Text.Contains("MiB", StringComparison.Ordinal)));
            });
            using var reopened = new LazyForzaStore(path); Assert.AreEqual(2000, reopened.LapCapacity);
        }
        finally { foreach (var suffix in new[] { "", "-wal", "-shm" }) if (File.Exists(path + suffix)) File.Delete(path + suffix); }
    }

    private static Border Frame(UIElement content, int width)
    {
        var frame = new Border { Child = content, Width = width, Padding = new Thickness(20), CornerRadius = new CornerRadius(12),
            Background = (Brush)Application.Current.Resources["CardBrush"] };
        frame.SetValue(TextElement.FontFamilyProperty, new FontFamily("Microsoft YaHei UI")); return frame;
    }
    private static void Layout(FrameworkElement element, int width)
    { element.Measure(new Size(width, double.PositiveInfinity)); element.Arrange(new Rect(new Point(), element.DesiredSize)); element.UpdateLayout(); }
    private static void Snapshot(FrameworkElement element, string name)
    {
        if (Environment.GetEnvironmentVariable("LAZYFORZA_LAP_LIBRARY_QA") is not { Length: > 0 } directory) return;
        Directory.CreateDirectory(directory); PngReportExporter.Save(element, Path.Combine(directory, name + ".png"));
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject root)
    {
        if (root is T match) yield return match;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var item in Descendants<T>(child)) yield return item;
    }
}
