using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LazyForza.App;
using LazyForza.Update;

namespace LazyForza.IntegrationTests;

[TestClass]
public sealed class AboutPageTests
{
    [TestMethod]
    [DataRow("zh-Hans", 680)]
    [DataRow("en", 680)]
    [DataRow("zh-Hans", 1120)]
    [DataRow("en", 1120)]
    public void PageKeepsLinksAndLocalizedHistoryReadableAtMinimumAndWideSizes(string language, int width) => WpfTestHost.Run(() =>
    {
        AppLocalization.UseLanguage(language);
        var output = Environment.GetEnvironmentVariable("LAZYFORZA_ABOUT_QA");
        var fixture = string.IsNullOrEmpty(output) ? null : Path.Combine(output, "releases.json");
        var releases = fixture is not null && File.Exists(fixture)
            ? ReleaseHistoryClient.Parse(File.ReadAllText(fixture), UpdateSourceKind.GitHub, false)
            : new[] { new ReleaseAnnouncement("v1.5.3", "LazyForza 1.5.3 · Radio Check", "## 简体中文\n- 语音比赛工程师\n- 弯道分析\n## English\n- Race engineer\n- Corner analysis", DateTimeOffset.UtcNow, false, UpdateSourceKind.GitHub) };
        var opened = new List<string>();
        var page = new AboutPage(opened.Add, new(DateTimeOffset.UtcNow, releases), _ => throw new AssertFailedException("Fresh cached history must not trigger another request."))
        { Background = (Brush)Application.Current.Resources["WindowBrush"], Width = width, Height = 1050 };
        using var host = new HwndSource(new HwndSourceParameters("About page layout")
        { Width = width, Height = 1050, WindowStyle = unchecked((int)0x80000000) });
        host.RootVisual = page;
        Flush();
        page.Measure(new Size(width, 1050)); page.Arrange(new Rect(0, 0, width, 1050)); page.UpdateLayout();
        Assert.IsTrue(page.IsLoaded);
        Assert.AreEqual(0, page.ScrollableWidth, .5);
        var buttons = Descendants<Button>(page).ToArray();
        foreach (var button in buttons.Where(button => button.ToolTip is string))
        {
            Assert.IsFalse(string.IsNullOrEmpty(AutomationProperties.GetName(button)));
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
        CollectionAssert.IsSubsetOf(new[]
        {
            "https://laz22y.github.io/LazyForza/", "https://laz22y.github.io/LazyForza/docs/",
            "https://github.com/Laz22y/LazyForza", "https://gitcode.com/Laz22y/LazyForza",
            "https://github.com/Laz22y/LazyForza.RaceServer"
        }, opened);
        Assert.IsFalse(opened.Any(url => url.Contains("gitcode.com/Laz22y/LazyForza.RaceServer", StringComparison.Ordinal)));
        foreach (var text in Descendants<TextBlock>(page).Where(text => text.IsVisible && text.ActualWidth > 0))
        {
            var point = text.TranslatePoint(new Point(), page);
            Assert.IsTrue(point.X >= 0 && point.X + text.ActualWidth <= width + .5, $"Clipped text: {text.Text}");
        }
        var items = Descendants<Expander>(page).ToArray();
        Assert.IsTrue(items.All(item => !item.IsExpanded));
        if (!string.IsNullOrEmpty(output))
        {
            Directory.CreateDirectory(output);
            Capture(page, Path.Combine(output, $"about-{language}-{width}.png"), width, 1050);
            items[0].IsExpanded = true;
            page.UpdateLayout();
            Capture(page, Path.Combine(output, $"about-expanded-{language}-{width}.png"), width, 1050);
        }
        host.RootVisual = null;
    });

    [TestMethod]
    public void FailedRefreshRetainsReadableCacheAndCanBeRetried() => WpfTestHost.Run(() =>
    {
        AppLocalization.UseLanguage("en");
        var calls = 0;
        var snapshot = new ReleaseHistorySnapshot(DateTimeOffset.UtcNow,
            [new("v1.5.3", "Cached release", "Saved notes", null, false, UpdateSourceKind.GitHub)]);
        var page = new AboutPage(_ => { }, snapshot, _ =>
        {
            calls++;
            return calls == 1 ? Task.FromException<ReleaseHistorySnapshot>(new UpdateException("offline"))
                : Task.FromResult(snapshot with { Releases = [snapshot.Releases[0] with { Title = "Fresh release" }] });
        });
        using var host = new HwndSource(new HwndSourceParameters("About history refresh")
        { Width = 700, Height = 800, WindowStyle = unchecked((int)0x80000000) });
        host.RootVisual = page; Flush();
        var refresh = Descendants<Button>(page).Single(button => AutomationProperties.GetName(button) == "Refresh");
        refresh.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Flush();
        Assert.IsTrue(Descendants<TextBlock>(page).Any(text => text.Text == "Saved notes"));
        Assert.IsTrue(Descendants<TextBlock>(page).Any(text => text.Text.Contains("Showing saved", StringComparison.Ordinal)));
        Assert.IsTrue(refresh.IsEnabled);
        refresh.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Flush();
        Assert.IsTrue(Descendants<TextBlock>(page).Any(text => text.Text == "Fresh release"));
        Assert.AreEqual(2, calls);
        host.RootVisual = null;
    });

    [TestMethod]
    public void AboutAndSettingsRemainAtBottomAndKeyboardMovesThroughBoth() => WpfTestHost.Run(() =>
    {
        var sidebar = new SidebarNavigation();
        sidebar.AddPage(7, new TextBlock { Text = "Cars" });
        var about = new TextBlock { Text = "About" };
        var settings = new TextBlock { Text = "Settings" };
        sidebar.AddPage(9, about, true); sidebar.AddPage(8, settings, true);
        sidebar.SetFooter(new Border { Height = 24 }, new Border { Height = 36 });
        using var host = new HwndSource(new HwndSourceParameters("About navigation")
        { Width = 208, Height = 420, WindowStyle = unchecked((int)0x80000000) });
        host.RootVisual = sidebar; Flush();
        sidebar.Measure(new Size(208, 420)); sidebar.Arrange(new Rect(0, 0, 208, 420)); sidebar.UpdateLayout();
        Assert.IsTrue(about.TranslatePoint(new Point(), sidebar).Y < settings.TranslatePoint(new Point(), sidebar).Y);
        Assert.IsTrue(settings.TranslatePoint(new Point(), sidebar).Y > 330);
        var bottom = Descendants<ListBox>(sidebar).Single(list => list.Items.Count == 2);
        sidebar.SelectedIndex = 8;
        var upFromSettings = new KeyEventArgs(Keyboard.PrimaryDevice, host, 0, Key.Up) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        bottom.RaiseEvent(upFromSettings);
        Assert.IsFalse(upFromSettings.Handled, "Up from Settings must reach About through normal list navigation.");
        sidebar.SelectedIndex = 9;
        var upFromAbout = new KeyEventArgs(Keyboard.PrimaryDevice, host, 0, Key.Up) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        bottom.RaiseEvent(upFromAbout);
        Assert.IsTrue(upFromAbout.Handled);
        Assert.AreEqual(7, sidebar.SelectedIndex);
        host.RootVisual = null;
    });

    private static IEnumerable<T> Descendants<T>(DependencyObject root)
    {
        if (root is T match) yield return match;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var item in Descendants<T>(child)) yield return item;
    }

    private static void Flush()
    {
        var frame = new DispatcherFrame();
        _ = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static void Capture(UIElement root, string path, int width, int height)
    {
        var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        image.Render(root);
        using var stream = File.Create(path);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); encoder.Save(stream);
    }
}
