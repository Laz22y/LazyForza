using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using LazyForza.App;
using LazyForza.EstatePeer;
using LazyForza.Modules.EstateRace;

namespace LazyForza.IntegrationTests;

[TestClass]
public sealed class EstatePeerWindowTests
{
    [TestMethod]
    [DoNotParallelize]
    public void DirectRoomFormsFitBothLanguagesAndValidateBeforeConnecting()
    {
        WpfTestHost.Run(() =>
        {
            // Use the application's actual ComboBox template without its code-behind event handlers.
            using var appXaml = typeof(EstatePeerWindowTests).Assembly.GetManifestResourceStream("LazyForzaApp.xaml")!;
            var xml = System.Xml.Linq.XDocument.Load(appXaml);
            var style = xml.Descendants().Single(element => element.Name.LocalName == "Style" && (string?)element.Attribute("TargetType") == "ComboBox");
            style.Elements().Where(element => element.Name.LocalName == "EventSetter").Remove();
            var comboStyle = (Style)System.Windows.Markup.XamlReader.Parse(style.ToString());
            Application.Current.Resources[typeof(ComboBox)] = comboStyle;
            foreach (var language in new[] { "zh-Hans", "en" })
            foreach (var width in new[] { 520, 680 })
            foreach (var mode in new[] { "join", "create", "open", "install" })
            {
                AppLocalization.UseLanguage(language);
                var calls = 0;
                var component = new PeerComponentStore(Path.GetTempPath(), new(1, 1, "0.1.0-experimental.1", "win-x64",
                    48000000, new string('A', 64), null, [new(PeerComponentStore.ExecutableName, 98000000, new string('A', 64))]));
                var trackId = Guid.NewGuid();
                var projects = mode == "open" ? new PeerProjectInfo[] { new(Guid.NewGuid(), trackId, "Saved race project", "Test estate circuit", "v1", new string('A', 64), 3, 10, DateTimeOffset.Now) } : [];
                var window = new EstatePeerWindow(new("", "", "Test driver", "#42D7E8", null),
                    [new(trackId, "Test estate circuit")], projects, component, () => null,
                    (_, _) => throw new AssertFailedException("An incomplete form must not start a host."),
                    (_, _) => { calls++; return Task.CompletedTask; }, () => Task.CompletedTask, () => false, () => "",
                    componentInstalled: mode is "create" or "open");
                var root = (ScrollViewer)window.Content;
                Assert.IsFalse(window.Topmost);
                Assert.IsNull(window.Owner, "An owned tool window would remain above the main window.");
                Assert.IsTrue(window.ShowInTaskbar);
                if (mode == "join")
                {
                    Descendants<Button>(root).Last(button => (string)button.Content == AppLocalization.Literal("加入房间"))
                        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.AreEqual(0, calls);
                    Assert.IsTrue(Descendants<TextBlock>(root).Any(text => text.Text.Contains("6–128", StringComparison.Ordinal)));
                }
                else
                    Descendants<Button>(root).First(button => (string)button.Content == AppLocalization.Literal("创建房间"))
                        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.IsTrue(Descendants<Expander>(root).All(expander => !expander.IsExpanded));
                if (mode == "open")
                    Assert.IsTrue(Descendants<Button>(root).Any(button => (string)button.Content == AppLocalization.Literal("打开并加入")));
                root.Background = (Brush)Application.Current.Resources["WindowBrush"];
                root.Width = width; root.Height = 700;
                root.Measure(new Size(width, 700)); root.Arrange(new Rect(0, 0, width, 700)); root.UpdateLayout();
                Assert.AreEqual(0, root.ScrollableWidth, .5, "The form must not require horizontal scrolling.");
                foreach (var control in Descendants<Control>(root).Where(control => control.ActualWidth > 0))
                    Assert.IsTrue(control.ActualWidth <= width, "A control exceeded the window width.");
                var output = Environment.GetEnvironmentVariable("LAZYFORZA_PEER_QA");
                if (!string.IsNullOrEmpty(output))
                {
                    Directory.CreateDirectory(output);
                    var bitmap = new RenderTargetBitmap(width, 700, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(root);
                    using var file = File.Create(Path.Combine(output, $"peer-{mode}-{language}-{width}.png"));
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(file);
                }
                window.Close();
            }
        });
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root)
    {
        if (root is T match) yield return match;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var element in Descendants<T>(child)) yield return element;
    }
}
