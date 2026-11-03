using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LazyForza.App;
using LazyForza.Storage;
using LazyForza.Update;

namespace LazyForza.IntegrationTests;

[TestClass]
[DoNotParallelize]
public sealed class VehicleNameUpdateWindowTests
{
    [TestMethod]
    [DataRow("zh-Hans", 640, 550, 1)]
    [DataRow("en", 640, 550, 2)]
    [DataRow("zh-Hans", 460, 380, 0)]
    [DataRow("en", 460, 380, 0)]
    public void OfferShowsRevisionDetailsAndAllChoicesAtBothWindowSizes(string language, int width, int height, int choiceValue) => WpfTestHost.Run(() =>
    {
        var choice = (VehicleNameUpdateChoice)choiceValue;
        AppLocalization.UseLanguage(language);
        var owner = new Window { Left = -10000, Top = -10000, Width = 1, Height = 1, ShowInTaskbar = false, ShowActivated = false };
        owner.Show();
        var revision = new VehicleNameRevision(new string('a', 40), DateTimeOffset.UtcNow, 12, 3);
        var window = new VehicleNameUpdateWindow(owner, VehicleNameCatalog.EmbeddedSnapshot.Info, revision)
        { Width = width, Height = height, WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000 };
        Exception? failure = null;
        window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            try
            {
                window.UpdateLayout();
                var root = (Grid)window.Content;
                var details = root.Children.OfType<TextBox>().Single();
                StringAssert.Contains(details.Text, VehicleNameSnapshot.SourceUrl);
                StringAssert.Contains(details.Text, revision.Revision);
                StringAssert.Contains(details.Text, "+12 / −3");
                Assert.IsTrue(details.ActualHeight > 70);
                var buttons = root.Children.OfType<WrapPanel>().Single().Children.OfType<Button>().ToArray();
                Assert.AreEqual(3, buttons.Length);
                foreach (var button in buttons)
                {
                    var point = button.TranslatePoint(new Point(), root);
                    Assert.IsTrue(point.X >= 0 && point.X + button.ActualWidth <= root.ActualWidth + 1, "An action is clipped horizontally.");
                    Assert.IsTrue(point.Y >= 0 && point.Y + button.ActualHeight <= root.ActualHeight + 1, "An action is clipped vertically.");
                }
                if (Environment.GetEnvironmentVariable("LAZYFORZA_VEHICLE_NAMES_QA") is { Length: > 0 } output)
                {
                    Directory.CreateDirectory(output);
                    CaptureAccents(root, window.Background, output, $"vehicle-names-{language}-{width}");
                }
                buttons[choice switch { VehicleNameUpdateChoice.Ignore => 0, VehicleNameUpdateChoice.Later => 1, _ => 2 }]
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            catch (Exception error) { failure = error; window.Close(); }
        }));
        window.ShowDialog();
        if (failure is not null) throw failure;
        Assert.AreEqual(choice, window.Choice);
        owner.Close();
    });

    [TestMethod]
    [DataRow("zh-Hans")]
    [DataRow("en")]
    public void ClosingTheOfferMeansLaterAndClosingDownloadCancels(string language) => WpfTestHost.Run(() =>
    {
        AppLocalization.UseLanguage(language);
        var owner = new Window { Left = -10000, Top = -10000, Width = 1, Height = 1, ShowInTaskbar = false, ShowActivated = false };
        owner.Show();
        var offer = new VehicleNameUpdateWindow(owner, null, new(new string('b', 40), DateTimeOffset.UtcNow, 0, 0))
        { WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000 };
        offer.Dispatcher.BeginInvoke(new Action(offer.Close));
        offer.ShowDialog();
        Assert.AreEqual(VehicleNameUpdateChoice.Later, offer.Choice);
        using var cancellation = new CancellationTokenSource();
        var progress = new VehicleNameDownloadWindow(owner, cancellation)
        { WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000 };
        progress.Show();
        progress.UpdateLayout();
        var panel = (StackPanel)progress.Content;
        var cancel = panel.Children.OfType<Button>().Single();
        var point = cancel.TranslatePoint(new Point(), panel);
        Assert.IsTrue(point.Y + cancel.ActualHeight <= panel.ActualHeight + 1, "The cancel action is clipped.");
        if (Environment.GetEnvironmentVariable("LAZYFORZA_VEHICLE_NAMES_QA") is { Length: > 0 } output)
        {
            Directory.CreateDirectory(output);
            // Let the native indeterminate animation enter its visible phase before taking QA images.
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(350), DispatcherPriority.Background,
                (_, _) => frame.Continue = false, progress.Dispatcher);
            try { Dispatcher.PushFrame(frame); }
            finally { timer.Stop(); }
            CaptureAccents(panel, progress.Background, output, $"vehicle-names-download-{language}");
        }
        progress.Close();
        Assert.IsTrue(cancellation.IsCancellationRequested);
        owner.Close();
    });

    private static void CaptureAccents(FrameworkElement root, Brush background, string output, string name)
    {
        var keys = new[] { "AccentBrush", "AccentSoftBrush", "AccentFaintBrush" };
        var previous = keys.ToDictionary(key => key, key => ((SolidColorBrush)Application.Current.Resources[key]).Color);
        try
        {
            foreach (var accent in AppAccentColors.Definitions)
            {
                AppAccentColors.Apply(accent.Value);
                root.UpdateLayout();
                Capture(root, background, Path.Combine(output, $"{name}-{accent.Value}.png"));
            }
        }
        finally
        {
            foreach (var (key, color) in previous) Application.Current.Resources[key] = new SolidColorBrush(color);
        }
    }

    internal static void Capture(FrameworkElement root, Brush background, string path)
    {
        // Capture the actual window layout. The report exporter deliberately reflows to a different width.
        var width = (int)Math.Ceiling(root.ActualWidth);
        var height = (int)Math.Ceiling(root.ActualHeight);
        var bounds = new Rect(0, 0, width, height);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(background, null, bounds);
            drawing.DrawRectangle(new VisualBrush(root)
            {
                Stretch = Stretch.Uniform,
                AlignmentX = AlignmentX.Left,
                AlignmentY = AlignmentY.Top
            }, null, bounds);
        }
        var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        image.Render(visual);
        using var stream = File.Create(path);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); encoder.Save(stream);
    }
}
