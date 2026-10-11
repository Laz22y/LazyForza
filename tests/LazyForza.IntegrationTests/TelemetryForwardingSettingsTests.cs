using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LazyForza.App;
using LazyForza.Domain;
using LazyForza.Telemetry;

namespace LazyForza.IntegrationTests;

[TestClass]
public sealed class TelemetryForwardingSettingsTests
{
    [TestMethod]
    public void MissingSettingsDefaultOffAndConfiguredTargetSurvivesRoundTrip()
    {
        Assert.IsFalse(TelemetryForwardingSettings.Read(null).Enabled);
        Assert.IsFalse(TelemetryForwardingSettings.Read("invalid").Enabled);
        var settings = new TelemetryForwardingOptions(true, "::1", 5200);
        Assert.AreEqual(settings, TelemetryForwardingSettings.Read(TelemetryForwardingSettings.Write(settings)));
        Assert.AreEqual(settings with { Enabled = false },
            TelemetryForwardingSettings.Read(TelemetryForwardingSettings.Write(settings with { Enabled = false })));
    }

    [TestMethod]
    public void DismissedRecommendationStaysClosedAcrossRefreshAndPageRecreation() => WpfTestHost.Run(() =>
    {
        string? saved = null;
        var recommendation = new TelemetryPortRecommendation(null, value => saved = value);
        recommendation.Update("5250"); Assert.AreEqual(Visibility.Visible, recommendation.Visibility);
        Descendants<Button>(recommendation).Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        recommendation.Update("5250"); Assert.AreEqual(Visibility.Collapsed, recommendation.Visibility);
        var reopened = new TelemetryPortRecommendation(saved);
        reopened.Update("5250"); Assert.AreEqual(Visibility.Collapsed, reopened.Visibility);
        reopened.Update("2299"); Assert.AreEqual(Visibility.Collapsed, reopened.Visibility);
        reopened.Update("5250"); Assert.AreEqual(Visibility.Visible, reopened.Visibility);
    });

    [TestMethod]
    [DataRow("zh-Hans")]
    [DataRow("en")]
    public void PanelAppliesDiscouragedPortAndCanDisableAfterInvalidEdit(string language) => WpfTestHost.Run(() =>
    {
        AppLocalization.UseLanguage(language);
        var saved = new TelemetryForwardingOptions();
        var panel = new TelemetryForwardingPanel(saved, TelemetrySourceKind.Live,
            value => { saved = value; return Task.CompletedTask; }, () => new(saved.Enabled, 0, 0, 0, null));
        using var automatic = panel.Application;
        var toggle = Descendants<ToggleButton>(panel).Single();
        var address = Descendants<TextBox>(panel).Single(input => AutomationProperties.GetName(input) == AppLocalization.Literal("目标 IP"));
        var port = Descendants<TextBox>(panel).Single(input => AutomationProperties.GetName(input) == AppLocalization.Literal("目标 UDP 端口"));
        Assert.AreEqual(Visibility.Collapsed, ((FrameworkElement)address.Parent).Parent is UIElement parent ? parent.Visibility : Visibility.Visible);
        toggle.IsChecked = true; toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        port.Text = "5250"; automatic.FlushAsync().GetAwaiter().GetResult();
        Assert.AreEqual(new TelemetryForwardingOptions(true, "127.0.0.1", 5250), saved);
        Assert.AreEqual(Visibility.Visible, Descendants<TelemetryPortRecommendation>(panel).Single().Visibility);

        // Exercise the actual theme at a narrow settings-card width in both languages.
        var root = new Border { Padding = new Thickness(20), Child = panel, Width = 510,
            Background = (Brush)Application.Current.Resources["WindowBrush"] };
        using var host = new HwndSource(new HwndSourceParameters("Telemetry settings layout")
        { Width = 510, Height = 470, WindowStyle = unchecked((int)0x80000000) });
        host.RootVisual = root;
        var frame = new DispatcherFrame();
        _ = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
        root.Measure(new Size(510, 470)); root.Arrange(new Rect(0, 0, 510, 470)); root.UpdateLayout();
        Assert.IsTrue(panel.IsLoaded);
        foreach (var text in Descendants<TextBlock>(panel).Where(text => text.IsVisible))
        {
            var point = text.TranslatePoint(new Point(), root);
            Assert.IsTrue(point.X >= 0 && point.X + text.ActualWidth <= 510.5, $"Clipped text: {text.Text}");
        }
        var output = Environment.GetEnvironmentVariable("LAZYFORZA_FORWARDING_QA");
        if (!string.IsNullOrEmpty(output))
        {
            Directory.CreateDirectory(output);
            var image = new RenderTargetBitmap(510, 470, 96, 96, PixelFormats.Pbgra32); image.Render(root);
            using var stream = File.Create(Path.Combine(output, $"forwarding-{language}.png"));
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); encoder.Save(stream);
        }
        address.Text = "invalid"; automatic.FlushAsync().GetAwaiter().GetResult();
        Assert.IsTrue(saved.Enabled); Assert.AreEqual("127.0.0.1", saved.Address);
        toggle.IsChecked = false; toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        automatic.FlushAsync().GetAwaiter().GetResult();
        Assert.IsFalse(saved.Enabled); Assert.AreEqual(5250, saved.Port);
        host.RootVisual = null;
    });

    private static IEnumerable<T> Descendants<T>(DependencyObject root)
    {
        if (root is T match) yield return match;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var result in Descendants<T>(child)) yield return result;
    }
}
