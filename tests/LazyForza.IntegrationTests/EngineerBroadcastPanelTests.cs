using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using LazyForza.App;
using LazyForza.Modules.EstateRace;

namespace LazyForza.IntegrationTests;

[TestClass]
public sealed class EngineerBroadcastPanelTests
{
    [TestMethod]
    public void BroadcastPreferencesKeepLegacyDefaultsAndRoundTripEachCategory()
    {
        foreach (var json in new string?[] { null, "", "{}", "invalid", "null" })
            Assert.AreEqual(new EngineerPreferences(), EngineerBroadcastPanel.Load(json));
        var preferences = new EngineerPreferences(EngineerDensity.Detailed, false, true, false, true);
        Assert.AreEqual(preferences, EngineerBroadcastPanel.Load(JsonSerializer.Serialize(preferences)));
        Assert.AreEqual(EngineerDensity.Balanced, EngineerBroadcastPanel.Load("{\"Density\":999,\"Flags\":false}").Density);
        Assert.IsFalse(EngineerBroadcastPanel.Load("{\"Flags\":false}").Flags);
        Assert.IsTrue(EngineerBroadcastPanel.Load("{\"Flags\":false}").Penalties);
    }

    [TestMethod]
    [DataRow(false, 420)]
    [DataRow(false, 740)]
    [DataRow(true, 420)]
    [DataRow(true, 740)]
    [DoNotParallelize]
    public void PreferencesStayCompactAndHistoryExplainsExpiredRepeats(bool english, int width) => WpfTestHost.Run(() =>
    {
        AppLocalization.UseLanguage(english ? "en" : "zh-Hans");
        EngineerPreferences? saved = null;
        var repeats = 0;
        var panel = new EngineerBroadcastPanel(new(), english, value => saved = value, () => repeats++);
        var sections = Descendants<Expander>(panel).ToArray();
        Assert.HasCount(2, sections);
        Assert.IsTrue(sections.All(section => !section.IsExpanded), "Categories and history should stay folded until requested.");
        var density = Descendants<ToggleButton>(panel).Where(button => button is not CheckBox).ToArray();
        Click(density[0]);
        Assert.AreEqual(EngineerDensity.Essential, saved!.Density);
        Assert.AreEqual(1, density.Count(button => button.IsChecked == true));
        Click(density[2]);
        Assert.AreEqual(EngineerDensity.Detailed, saved.Density);
        var categories = Descendants<CheckBox>(panel).ToArray();
        Assert.HasCount(4, categories);
        categories[0].IsChecked = false; Click(categories[0]);
        Assert.IsFalse(saved.Flags);
        Assert.IsTrue(saved.Penalties);
        categories[0].IsChecked = true; Click(categories[0]);
        Click(density[1]);
        var now = DateTimeOffset.Now;
        EngineerBroadcast[] history =
        [
            new(3, now, "flag", english ? "Green flag." : "绿旗，恢复比赛。", EngineerDelivery.Completed, false, EngineerRepeatState.Ready),
            new(2, now.AddSeconds(-8), "penalty:1", english ? "Time penalty, 5 seconds." : "罚时 5 秒。", EngineerDelivery.Completed, true, EngineerRepeatState.StateChanged),
            new(1, now.AddSeconds(-15), "pit", english ? "Limited evidence. A pit stop may be useful. Check the prediction." : "样本不足，可能适合进站，请核对预测。", EngineerDelivery.Interrupted, false, EngineerRepeatState.StateChanged)
        ];
        panel.Update(history, EngineerRepeatState.Ready);
        var repeat = Descendants<Button>(panel).Single();
        Assert.IsTrue(repeat.IsEnabled); Click(repeat); Assert.AreEqual(1, repeats);
        var root = new Border
        {
            Child = panel, Width = width, Padding = new Thickness(20), CornerRadius = new CornerRadius(12),
            Background = (Brush)Application.Current.Resources["CardBrush"]
        };
        root.SetValue(TextElement.FontFamilyProperty, new FontFamily("Microsoft YaHei UI"));
        Render("collapsed");
        foreach (var section in sections) section.IsExpanded = true;
        Assert.IsTrue(Descendants<TextBlock>(panel).Any(text => text.Text == history[1].Text));
        Assert.IsTrue(Descendants<TextBlock>(panel).Any(text => text.Text.Contains(english ? "Outdated" : "已失效")));
        panel.Update(history, EngineerRepeatState.Expired);
        Assert.IsFalse(repeat.IsEnabled);
        StringAssert.Contains((string)repeat.ToolTip, english ? "expired" : "过期");
        Render("expanded");

        void Render(string state)
        {
            root.Measure(new Size(width, double.PositiveInfinity));
            root.Arrange(new Rect(new Point(), root.DesiredSize)); root.UpdateLayout();
            Assert.IsTrue(root.ActualHeight < 760, "History must remain bounded and scroll rather than lengthen the page indefinitely.");
            foreach (var control in Descendants<Control>(panel).Where(control => control.ActualWidth > 0))
                Assert.IsTrue(control.ActualWidth <= width - 40, "A control exceeds the available content width.");
            if (Environment.GetEnvironmentVariable("LAZYFORZA_BROADCAST_QA") is { Length: > 0 } directory)
            {
                Directory.CreateDirectory(directory);
                PngReportExporter.Save(root, Path.Combine(directory, $"broadcast-{(english ? "en" : "zh")}-{width}-{state}.png"));
            }
        }
    });

    private static void Click(ButtonBase button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    private static IEnumerable<T> Descendants<T>(DependencyObject root)
    {
        if (root is T match) yield return match;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var item in Descendants<T>(child)) yield return item;
    }
}
