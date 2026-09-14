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
                foreach (var providerIndex in new[] { 1, 2, 3, 4, 5, 6 })
                {
                    AppLocalization.UseLanguage(english ? "en" : "zh-Hans");
                    var saves = 0;
                    var window = new EngineerSpeechSettingsWindow(new(), [], null, english,
                        (_, _) => { saves++; return Task.CompletedTask; });
                    var root = (Grid)window.Content;
                    var selectors = Descendants<ComboBox>(root).ToArray();
                    selectors[0].SelectedIndex = providerIndex;
                    var password = Descendants<PasswordBox>(root).Single(p => p.Name == ProviderSecretName(providerIndex));
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
                    var path = Environment.GetEnvironmentVariable("LAZYFORZA_SPEECH_QA") ?? Environment.GetEnvironmentVariable("LAZYFORZA_ELEVENLABS_QA");
                    if (!string.IsNullOrEmpty(path))
                    {
                        Directory.CreateDirectory(path);
                        var bitmap = new RenderTargetBitmap((int)root.Width, (int)root.Height, 96, 96, PixelFormats.Pbgra32);
                        bitmap.Render(root);
                        using var file = File.Create(Path.Combine(path, $"speech-settings-{providerIndex}-{(english ? "en" : "zh")}-{width}.png"));
                        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(file);
                    }
                    password.Password = "unsaved-draft-key";
                    selectors[0].SelectedIndex = 0;
                    selectors[0].SelectedIndex = providerIndex;
                    Assert.AreEqual("unsaved-draft-key", password.Password, "Provider switching must preserve the unsaved draft.");
                    Assert.IsTrue(Descendants<PasswordBox>(root).Where(p => p != password).All(p => p.Password.Length == 0));
                    window.Close();
                    Assert.AreEqual("", password.Password);
                    Assert.AreEqual(0, saves, "Closing a draft must not change the active speech provider.");
                }
                SavedDraftRestoresBothProviders();
                SavedDraftRestoresNewProviders();
            }
            catch (Exception error) { failure = error; }
            finally { app.Shutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(30)));
        Assert.IsNull(failure, failure?.ToString());
    }

    private static string ProviderSecretName(int index) => index switch
    {
        1 => "ElevenLabsApiKey", 2 => "AzureApiKey", 3 => "TencentSecretId", 4 => "AlibabaAppKey", 5 => "QwenApiKey", _ => "MiniMaxApiKey"
    };

    private static void SavedDraftRestoresNewProviders()
    {
        var settings = new EngineerSpeechSettings();
        foreach (var index in new[] { 3, 4, 5, 6 })
        {
            EngineerSpeechSettings? saved = null;
            EngineerSpeechSettingsWindow? window = null;
            window = new(settings, [], null, false, (value, _) =>
            { saved = EngineerSpeechSettings.Load(value.Serialize()); window!.Close(); return Task.CompletedTask; });
            var root = (Grid)window.Content;
            Descendants<ComboBox>(root).First().SelectedIndex = index;
            foreach (var input in Descendants<PasswordBox>(root))
                if (input.Name.StartsWith(index switch { 3 => "Tencent", 4 => "Alibaba", 5 => "Qwen", _ => "MiniMax" }, StringComparison.Ordinal))
                    input.Password = "secret-for-" + input.Name;
            Descendants<Button>(root).Single(b => b.IsDefault).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.IsNotNull(saved, $"Provider {index} failed to save.");
            Assert.IsFalse(saved.UseElevenLabs);
            if (index > 3) Assert.AreEqual(settings.TencentSettings, saved.TencentSettings);
            if (index > 4) Assert.AreEqual(settings.AlibabaSettings, saved.AlibabaSettings);
            if (index > 5) Assert.AreEqual(settings.QwenSettings, saved.QwenSettings);
            settings = saved;
        }
        foreach (var index in new[] { 3, 4, 5, 6 })
        {
            var provider = index switch { 3 => "tencent", 4 => "alibaba", 5 => "qwen", _ => "minimax" };
            var window = new EngineerSpeechSettingsWindow(settings with { ProviderId = provider }, [], null, true, (_, _) => Task.CompletedTask);
            var root = (Grid)window.Content;
            Assert.AreEqual(index, Descendants<ComboBox>(root).First().SelectedIndex);
            Assert.AreEqual("secret-for-" + ProviderSecretName(index), Descendants<PasswordBox>(root).Single(p => p.Name == ProviderSecretName(index)).Password);
            window.Close();
        }
        // A ciphertext owned by another Windows account is preserved when editing a different service.
        var unavailable = settings with { AlibabaSettings = settings.AlibabaSettings! with { ProtectedAppKey = "unreadable-cipher" } };
        EngineerSpeechSettings? roundtrip = null;
        EngineerSpeechSettingsWindow? fallbackWindow = null;
        fallbackWindow = new(unavailable, [], null, false, (value, _) => { roundtrip = value; fallbackWindow!.Close(); return Task.CompletedTask; });
        Descendants<ComboBox>((Grid)fallbackWindow.Content).First().SelectedIndex = 0;
        Descendants<Button>((Grid)fallbackWindow.Content).Single(b => b.IsDefault).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.AreEqual("unreadable-cipher", roundtrip!.AlibabaSettings!.ProtectedAppKey);
    }

    private static void SavedDraftRestoresBothProviders()
    {
        var initial = new EngineerSpeechSettings(true, EngineerCredentialProtection.Protect("saved-eleven-key"), "eleven-voice", Language: "en-US");
        EngineerSpeechSettings? saved = null;
        EngineerSpeechSettingsWindow? window = null;
        window = new EngineerSpeechSettingsWindow(initial, [], null, false, (settings, _) =>
        {
            saved = EngineerSpeechSettings.Load(settings.Serialize());
            window!.Close();
            return Task.CompletedTask;
        });
        var root = (Grid)window.Content;
        Descendants<ComboBox>(root).First().SelectedIndex = 2;
        Descendants<PasswordBox>(root).Single(p => p.Name == "AzureApiKey").Password = "new-azure-key";
        Descendants<TextBox>(root).Single(p => p.Name == "AzureRegion").Text = " WestUS2 ";
        Descendants<TextBox>(root).Single(p => p.Name == "AzureVoiceId").Text = "zh-CN-XiaoxiaoNeural";
        Descendants<Button>(root).Single(b => b.IsDefault).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.IsNotNull(saved);
        Assert.AreEqual(EngineerSpeechSettings.Azure, saved.ActiveProvider);
        Assert.IsFalse(saved.UseElevenLabs);
        Assert.AreEqual(initial.ProtectedApiKey, saved.ProtectedApiKey);
        Assert.AreEqual(initial.VoiceId, saved.VoiceId);
        Assert.AreEqual(initial.Language, saved.Language);
        Assert.AreEqual("westus2", saved.AzureRegion);
        var azureCipher = saved.AzureProtectedApiKey;
        window = new EngineerSpeechSettingsWindow(saved, [], null, false, (settings, _) =>
        {
            saved = EngineerSpeechSettings.Load(settings.Serialize());
            window!.Close();
            return Task.CompletedTask;
        });
        root = (Grid)window.Content;
        Assert.AreEqual(2, Descendants<ComboBox>(root).First().SelectedIndex);
        Assert.AreEqual("new-azure-key", Descendants<PasswordBox>(root).Single(p => p.Name == "AzureApiKey").Password);
        Assert.AreEqual("zh-CN-XiaoxiaoNeural", Descendants<TextBox>(root).Single(p => p.Name == "AzureVoiceId").Text);
        Descendants<ComboBox>(root).First().SelectedIndex = 1;
        Descendants<PasswordBox>(root).Single(p => p.Name == "ElevenLabsApiKey").Password = "updated-eleven-key";
        Descendants<Button>(root).Single(b => b.IsDefault).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.AreEqual(EngineerSpeechSettings.ElevenLabs, saved.ActiveProvider);
        Assert.IsTrue(saved.UseElevenLabs);
        Assert.AreEqual(azureCipher, saved.AzureProtectedApiKey);
        Assert.AreEqual("zh-CN-XiaoxiaoNeural", saved.AzureVoiceId);
        Assert.IsTrue(EngineerCredentialProtection.TryUnprotect(saved.ProtectedApiKey, out var key));
        Assert.AreEqual("updated-eleven-key", key);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root)
    {
        if (root is T match) yield return match;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var element in Descendants<T>(child)) yield return element;
    }
}
