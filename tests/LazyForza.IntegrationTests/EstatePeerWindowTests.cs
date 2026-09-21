using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using LazyForza.App;
using LazyForza.EstatePeer;
using LazyForza.Modules.EstateRace;
using LazyForza.Update;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace LazyForza.IntegrationTests;

[TestClass]
public sealed class EstatePeerWindowTests
{
    [TestMethod]
    [DoNotParallelize]
    public void ReceiptAndInvitationCopySurviveClipboardContentionAndKeepManualFallback()
    {
        WpfTestHost.Run(() =>
        {
            AppLocalization.UseLanguage("zh-Hans");
            foreach (var permanentFailure in new[] { false, true })
            foreach (var caption in new[] { "复制连接回执", "复制邀请代码", "复制总控密码" })
            {
                var attempts = 0;
                string? copied = null;
                var window = new EstatePeerWindow(new("", "", "Test driver", "#42D7E8", null), [], [], null,
                    () => null, (_, _) => throw new AssertFailedException(), (_, _) => Task.CompletedTask,
                    () => Task.CompletedTask, () => false, () => "", clipboardWriter: value =>
                    {
                        attempts++;
                        if (permanentFailure || attempts < 3) throw new COMException("Clipboard busy", unchecked((int)0x800401D0));
                        copied = value;
                    });
                var root = (ScrollViewer)window.Content;
                var source = new TextBox { Text = "test-receipt-remains-available", IsReadOnly = true };
                var button = window.CopyButton(caption, source);
                var form = ((StackPanel)root.Content).Children.OfType<StackPanel>().Single();
                form.Children.Add(source);
                form.Children.Add(button);
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var frame = new DispatcherFrame();
                var started = DateTime.UtcNow;
                var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(20), DispatcherPriority.Background, (_, _) =>
                {
                    if (Descendants<TextBlock>(root).Any(text => text.Text == "已复制。" || text.Text.StartsWith("剪贴板暂时不可用", StringComparison.Ordinal)) ||
                        DateTime.UtcNow - started > TimeSpan.FromSeconds(3)) frame.Continue = false;
                }, Dispatcher.CurrentDispatcher);
                Dispatcher.PushFrame(frame); timer.Stop();
                if (permanentFailure)
                {
                    Assert.AreEqual(5, attempts);
                    Assert.IsNull(copied);
                    Assert.AreEqual(source.Text.Length, source.SelectionLength);
                    Assert.IsTrue(Descendants<TextBlock>(root).Any(text => text.Text.StartsWith("剪贴板暂时不可用", StringComparison.Ordinal)));
                }
                else { Assert.AreEqual(3, attempts); Assert.AreEqual(source.Text, copied); }
                Assert.IsTrue(Descendants<Button>(root).All(item => item.IsEnabled), "The form must remain usable after copying fails.");
                window.Close();
            }
        });
    }

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
            foreach (var mode in new[] { "join", "create", "open", "install", "updates" })
            {
                AppLocalization.UseLanguage(language);
                var calls = 0;
                var component = new PeerComponentStore(Path.GetTempPath(), new(1, 1, "0.1.0-experimental.1", "win-x64",
                    48000000, new string('A', 64), null, [new(PeerComponentStore.ExecutableName, 98000000, new string('A', 64))]));
                using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                var updates = mode == "updates" ? new PeerComponentUpdateManager(Path.GetTempPath(), Path.Combine(Path.GetTempPath(), "unused-room-fixture"),
                    component.Catalog, new(signingKey.ExportSubjectPublicKeyInfoPem(), "1.5.4-alpha-1", true), UpdateSourceKind.GitCode) : null;
                var trackId = Guid.NewGuid();
                var projects = mode == "open" ? new PeerProjectInfo[] { new(Guid.NewGuid(), trackId, "Saved race project", "Test estate circuit", "v1", new string('A', 64), 3, 10, DateTimeOffset.Now) } : [];
                var window = new EstatePeerWindow(new("", "", "Test driver", "#42D7E8", null),
                    [new(trackId, "Test estate circuit")], projects, component, () => null,
                    (_, _) => throw new AssertFailedException("An incomplete form must not start a host."),
                    (_, _) => { calls++; return Task.CompletedTask; }, () => Task.CompletedTask, () => false, () => "",
                    componentInstalled: mode is "create" or "open" or "updates", componentUpdates: updates);
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
                if (mode == "updates")
                {
                    var expander = Descendants<Expander>(root).Single(item => (string)item.Header == AppLocalization.Literal("房主组件 · 已安装"));
                    expander.IsExpanded = true;
                    Assert.IsTrue(Descendants<Button>(root).Any(button => (string)button.Content == AppLocalization.Literal("检查组件更新")));
                    Assert.IsTrue(Descendants<Button>(root).Any(button => (string)button.Content == AppLocalization.Literal("导入离线包")));
                }
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
