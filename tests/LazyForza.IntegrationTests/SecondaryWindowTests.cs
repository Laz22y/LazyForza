using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LazyForza.Analysis;
using LazyForza.App;
using LazyForza.Domain;
using LazyForza.Update;

namespace LazyForza.IntegrationTests;

[TestClass]
public sealed class SecondaryWindowTests
{
    [TestMethod]
    [DataRow("zh-Hans")]
    [DataRow("en")]
    public void OtherSecondaryWindowsKeepTheSharedThemeAndVisibleFooterActions(string language) => WpfTestHost.Run(() =>
    {
        AppLocalization.UseLanguage(language);
        var owner = new Window { Width = 1, Height = 1, Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
        owner.Show();
        var lap = new LapSummary(Guid.NewGuid(), Guid.NewGuid(), 1, 2, Guid.NewGuid(), new(1, 4, 800, 1, 6, 8000, "", ""),
            DateTimeOffset.UnixEpoch, 90, true, null, []);
        var release = new UpdateReleaseInfo(new Version(1, 5, 5), "v1.5.5", "1.5.5", "Release notes", new Uri("https://example.com/release"),
            new("package.zip", new Uri("https://example.com/package.zip"), 100, null), null, UpdateSourceKind.GitHub);
        var progress = new UpdateProgressWindow(owner, "1.5.4-dev.20261103.4");
        Window[] windows = [new TrackCorrectionWindow(owner, []), new LapBulkDeleteDialog(owner, "Track", [lap], new HashSet<int> { 4 }),
            new LapRecordEditor(owner, lap), new DriftDashboardIntroductionWindow(true), new UpdateOfferWindow(owner, release, false), progress,
            new EngineerSpeechSettingsWindow(new(), [], null, language == "en", (_, _) => Task.CompletedTask)];
        foreach (var window in windows)
        {
            AppLocalization.ApplyTo(window);
            Show(window);
            var root = (FrameworkElement)window.Content;
            Assert.AreSame(Application.Current.FindResource(typeof(Window)), window.Style);
            foreach (var button in Children<Button>(root).Where(button => button.IsVisible && (button.IsDefault || button.IsCancel)))
                AssertInside(root, button);
            Save(window, $"secondary-{window.GetType().Name}-{language}");
            if (window is UpdateProgressWindow update) update.Finish(); else window.Close();
        }
        owner.Close();
    });

    [TestMethod]
    [DataRow("zh-Hans", 14d)]
    [DataRow("en", 20d)]
    public void RenameWindowsUseAppThemeAndGrowWithoutClippingActions(string language, double fontSize) => WpfTestHost.Run(() =>
    {
        AppLocalization.UseLanguage(language);
        foreach (var (title, caption) in new[] { ("重命名赛道", "赛道名称"), ("重命名车辆配置", "配置名称") })
        {
            var window = new NameEntryWindow(null, title, caption, "江户环道赛") { FontSize = fontSize };
            Show(window);
            var root = (StackPanel)window.Content;
            Assert.AreSame(Application.Current.FindResource("WindowBrush"), window.Background);
            Assert.AreSame(Application.Current.FindResource("TextBrush"), window.Foreground);
            var text = root.Children.OfType<TextBox>().Single();
            var buttons = root.Children.OfType<StackPanel>().Single().Children.OfType<Button>().ToArray();
            Assert.AreEqual("江户环道赛", text.Text, "User-entered names must not be translated.");
            AssertInside(root, text); foreach (var button in buttons) AssertInside(root, button);
            text.Text = " "; Assert.IsFalse(buttons.Single(button => button.IsDefault).IsEnabled);
            text.Text = "New name"; Assert.AreEqual("New name", window.Value);
            Save(window, $"rename-{caption}-{language}"); window.Close();
        }
        var secondaries = typeof(MainWindow).Assembly.GetTypes().Where(type => type.IsSubclassOf(typeof(Window)) &&
            type != typeof(MainWindow) && type != typeof(InitializationWindow) && type != typeof(AppWindow)).ToArray();
        Assert.IsTrue(secondaries.All(type => type.IsSubclassOf(typeof(AppWindow))),
            "Every secondary app window must explicitly inherit the shared theme.");
        Assert.AreEqual(language == "en" ? "Edo Circuit" : "江户环道赛", AppLocalization.Literal("江户环道赛"));
        Assert.AreEqual(language == "en" ? "Shuto Charge" : "首都冲锋赛", AppLocalization.Literal("首都冲锋赛"));
    });

    [TestMethod]
    [DataRow("zh-Hans", 680, 590)]
    [DataRow("en", 520, 480)]
    public void AssignmentPreviewsGeometryAndRequiresExplicitForceForSevereMismatch(string language, int width, int height) => WpfTestHost.Run(() =>
    {
        AppLocalization.UseLanguage(language);
        var source = TrackAlgorithms.BuildTemplate("Recorded route", Enumerable.Range(0, 201)
            .Select(i => new TrackPoint(i * 5, 0, 40 * Math.Sin(i / 20d), 0, 0, 0)).ToArray(), layoutKind: TrackLayoutKind.PointToPoint)
            with { Source = "simulator", IsRecordOnly = true };
        var good = source with { Id = Guid.NewGuid(), Name = "Matching track", IsRecordOnly = false };
        var bad = source with { Id = Guid.NewGuid(), Name = "Another track", IsRecordOnly = false,
            Points = source.Points.Select(point => point with { Z = point.Z + 5000 }).ToArray() };
        var window = new RecordedRouteAssignmentWindow(null, source, 3, [bad, good]) { Width = width, Height = height };
        Show(window);
        var root = (Grid)window.Content;
        var selector = root.Children.OfType<ComboBox>().Single();
        var details = root.Children.OfType<StackPanel>().Single(panel => Grid.GetRow(panel) == 3);
        var force = details.Children.OfType<CheckBox>().Single();
        var apply = root.Children.OfType<StackPanel>().Single(panel => Grid.GetRow(panel) == 4).Children.OfType<Button>().Single(button => button.IsDefault);
        Assert.AreEqual(good.Id, window.TargetId); Assert.IsTrue(apply.IsEnabled); Assert.IsFalse(window.Force);
        Save(window, $"assignment-match-{language}");
        selector.SelectedIndex = 1; window.UpdateLayout();
        Assert.AreEqual(bad.Id, window.TargetId); Assert.IsFalse(apply.IsEnabled);
        Assert.AreEqual(Visibility.Visible, force.Visibility);
        AssertInside(root, apply); AssertInside(root, force);
        Save(window, $"assignment-mismatch-{language}");
        force.IsChecked = true; Assert.IsTrue(apply.IsEnabled);
        selector.SelectedIndex = 0; Assert.IsFalse(window.Force);
        window.Close();
    });

    private static void Show(Window window)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual; window.Left = -10000; window.Top = -10000;
        window.ShowActivated = false; window.Show(); window.UpdateLayout();
    }
    private static void AssertInside(FrameworkElement root, FrameworkElement element)
    {
        var point = element.TranslatePoint(new Point(), root);
        Assert.IsTrue(point.X >= 0 && point.Y >= 0 && point.X + element.ActualWidth <= root.ActualWidth + 1 &&
            point.Y + element.ActualHeight <= root.ActualHeight + 1, $"Clipped {element.GetType().Name}");
    }
    private static void Save(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("LAZYFORZA_RECORDED_ROUTE_QA") is not { Length: > 0 } output) return;
        Directory.CreateDirectory(output);
        VehicleNameUpdateWindowTests.Capture((FrameworkElement)window.Content, window.Background, Path.Combine(output, name + ".png"));
    }
    private static IEnumerable<T> Children<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var descendant in Children<T>(child)) yield return descendant;
        }
    }
}
