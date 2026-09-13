using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LazyForza.App;

namespace LazyForza.IntegrationTests;

[TestClass]
public sealed class EngineerSpeechSettingsWindowTests
{
    [TestMethod]
    [DoNotParallelize]
    public void ProviderSettingsStayInDraftAndFitChineseAndEnglishFloatingWindows()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var app = new LazyForza.App.App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.InitializeComponent();
            try
            {
                foreach (var english in new[] { false, true })
                foreach (var width in new[] { 480, 560 })
                {
                    AppLocalization.UseLanguage(english ? "en-US" : "zh-Hans");
                    var saves = 0;
                    var window = new EngineerSpeechSettingsWindow(new(), [], null, english,
                        (_, _) => { saves++; return Task.CompletedTask; });
                    var root = (Grid)window.Content;
                    var selectors = Descendants<ComboBox>(root).ToArray();
                    selectors[0].SelectedIndex = 1;
                    var password = Descendants<PasswordBox>(root).Single();
                    Assert.AreEqual("", password.Password);
                    var save = Descendants<Button>(root).Single(b => b.IsDefault);
                    save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.AreEqual(0, saves, "An incomplete API configuration must not replace saved settings.");
                    Assert.IsTrue(Descendants<TextBlock>(root).Any(t => t.Text.Contains(english ? "valid Voice ID" : "有效的音色")));
                    root.Background = (Brush)app.Resources["PanelBrush"];
                    root.Margin = new Thickness(0);
                    root.Width = width - 48; root.Height = 680;
                    root.Measure(new Size(root.Width, root.Height));
                    root.Arrange(new Rect(0, 0, root.Width, root.Height)); root.UpdateLayout();
                    foreach (var element in Descendants<Control>(root).Where(c => c.ActualWidth > 0 && c is not ScrollViewer))
                        Assert.IsTrue(element.ActualWidth <= root.Width + 1, $"{element.GetType().Name} exceeds the floating window width.");
                    var path = Environment.GetEnvironmentVariable("LAZYFORZA_ELEVENLABS_QA");
                    if (!string.IsNullOrEmpty(path))
                    {
                        Directory.CreateDirectory(path);
                        var bitmap = new RenderTargetBitmap((int)root.Width, (int)root.Height, 96, 96, PixelFormats.Pbgra32);
                        bitmap.Render(root);
                        using var file = File.Create(Path.Combine(path, $"speech-settings-{(english ? "en" : "zh")}-{width}.png"));
                        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(file);
                    }
                    window.Close();
                    Assert.AreEqual(0, saves, "Closing a draft must not change the active speech provider.");
                }
            }
            catch (Exception error) { failure = error; }
            finally { app.Shutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(30)));
        Assert.IsNull(failure, failure?.ToString());
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root)
    {
        if (root is T match) yield return match;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var element in Descendants<T>(child)) yield return element;
    }
}
