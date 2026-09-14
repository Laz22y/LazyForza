using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LazyForza.Speech;

namespace LazyForza.App;

/// <summary>Owned settings window; draft changes and API requests end when the window closes.</summary>
internal sealed partial class EngineerSpeechSettingsWindow : Window
{
    private readonly bool english;
    private readonly CancellationTokenSource lifetime = new();
    private readonly ComboBox service = new(), windowsVoice = new(), model = new(), language = new(), voices = new();
    private readonly PasswordBox key = new();
    private readonly TextBox voiceId = new();
    private readonly PasswordBox azureKey = new();
    private readonly TextBox azureRegion = new(), azureVoiceId = new();
    private readonly ComboBox azureVoices = new(), azureLanguage = new();
    private readonly StackPanel elevenPanel = new(), azurePanel = new();
    private readonly TextBlock serviceNotice = new();
    private readonly EngineerSpeechSettings original;
    private readonly string originalKey, originalAzureKey;
    private readonly CheckBox fallback = new();
    private readonly TextBlock status = new();
    private readonly StackPanel onlinePanel = new();
    private readonly FrameworkElement localPanel;
    private readonly Button refresh, azureRefresh, save;
    private readonly Func<EngineerSpeechSettings, WindowsSpeechVoice?, Task> apply;
    private bool busy;

    public EngineerSpeechSettingsWindow(EngineerSpeechSettings settings, IReadOnlyList<WindowsSpeechVoice> installed,
        WindowsSpeechVoice? selected, bool english, Func<EngineerSpeechSettings, WindowsSpeechVoice?, Task> apply)
    {
        this.english = english;
        this.apply = apply;
        original = settings;
        Title = T("语音服务", "Speech service");
        Width = 560; Height = 660; MinWidth = 480; MinHeight = 460;
        MaxHeight = SystemParameters.WorkArea.Height;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brush("PanelBrush"); Foreground = Brush("TextBrush");
        FontFamily = new FontFamily("Microsoft YaHei UI");
        UseLayoutRounding = true; SnapsToDevicePixels = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        var root = new Grid { Margin = new Thickness(24, 20, 24, 20) };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var heading = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
        heading.Children.Add(Text(Title, 22, bold: true));
        heading.Children.Add(Text(T("选择工程师的声音，沿用当前无线电提示音与音量。", "Choose the engineer's voice. Radio cues and volume stay shared."), 12, muted: true));
        root.Children.Add(heading);
        var body = new StackPanel();
        service.Items.Add(T("Windows 本地语音", "Windows local speech"));
        service.Items.Add("ElevenLabs");
        service.Items.Add("Azure Speech");
        service.Items.Add(T("腾讯云", "Tencent Cloud"));
        service.Items.Add(T("阿里云 · 智能语音交互", "Alibaba Cloud · NLS"));
        service.Items.Add(T("千问 AI 平台", "Qianwen AI"));
        service.Items.Add("MiniMax");
        service.SelectedIndex = Array.IndexOf(ServiceIds, settings.ActiveProvider);
        body.Children.Add(Field(T("语音来源", "Provider"), service));
        windowsVoice.Items.Add(T("默认音色（跟随界面语言）", "Default voice (interface language)"));
        foreach (var voice in installed) windowsVoice.Items.Add(voice);
        windowsVoice.SelectedItem = (object?)selected ?? windowsVoice.Items[0];
        localPanel = Field(T("Windows 音色", "Windows voice"), windowsVoice);
        body.Children.Add(localPanel);

        key.MaxLength = 512; key.MinHeight = 44;
        key.Style = (Style)Application.Current.Resources["SecretEntry"];
        var readable = EngineerCredentialProtection.TryUnprotect(settings.ProtectedApiKey, out var secret);
        key.Password = originalKey = secret;
        key.Name = "ElevenLabsApiKey";
        serviceNotice.FontSize = 12; serviceNotice.Foreground = Brush("MutedBrush");
        serviceNotice.TextWrapping = TextWrapping.Wrap; serviceNotice.Margin = new Thickness(0, 0, 0, 10);
        onlinePanel.Children.Add(serviceNotice);
        elevenPanel.Children.Add(Field("API Key", key));
        elevenPanel.Children.Add(Text(T("密钥由当前 Windows 用户加密保存；更换电脑或用户后需重新填写。", "The key is encrypted for this Windows user. Re-enter it on another computer or account."), 12, muted: true));
        var voiceRow = new Grid();
        voiceRow.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        voiceRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        voiceId.Text = settings.VoiceId; voiceId.MaxLength = 128;
        voiceId.MinHeight = 44;
        voiceId.VerticalContentAlignment = VerticalAlignment.Center;
        voiceRow.Children.Add(voiceId);
        refresh = new Button { Content = T("获取音色", "Load voices"), Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(12, 7, 12, 7) };
        refresh.Click += async (_, _) => await RefreshVoicesAsync();
        Grid.SetColumn(refresh, 1); voiceRow.Children.Add(refresh);
        elevenPanel.Children.Add(Field(T("音色 ID", "Voice ID"), voiceRow));
        voices.Visibility = Visibility.Collapsed; voices.Margin = new Thickness(0, 0, 0, 12);
        voices.SelectionChanged += (_, _) => { if (voices.SelectedItem is SpeechVoiceInfo voice) voiceId.Text = voice.Id; };
        elevenPanel.Children.Add(voices);
        model.Items.Add(new ModelChoice(ElevenLabsSpeechProvider.DefaultModel, T("Flash v2.5 · 快速播报", "Flash v2.5 · Fast response")));
        model.Items.Add(new ModelChoice("eleven_multilingual_v2", T("Multilingual v2 · 自然稳定", "Multilingual v2 · Consistent quality")));
        model.Items.Add(new ModelChoice("eleven_v3", T("Eleven v3 · 表现力", "Eleven v3 · Expressive")));
        model.SelectedItem = model.Items.Cast<ModelChoice>().FirstOrDefault(item => item.Id == settings.ModelId) ?? model.Items[0];
        elevenPanel.Children.Add(Field(T("合成模型", "Model"), model));
        foreach (var name in new[] { T("跟随界面语言", "Interface language"), "中文", "English" }) language.Items.Add(name);
        language.SelectedIndex = settings.Language == "en-US" ? 2 : settings.Language == "zh-CN" ? 1 : 0;
        elevenPanel.Children.Add(Field(T("播报语言", "Language"), language));
        onlinePanel.Children.Add(elevenPanel);

        var azureReadable = EngineerCredentialProtection.TryUnprotect(settings.AzureProtectedApiKey, out var azureSecret);
        azureKey.Password = originalAzureKey = azureSecret;
        azureKey.Name = "AzureApiKey"; azureKey.MaxLength = 512; azureKey.MinHeight = 44;
        azureKey.Style = key.Style;
        azurePanel.Children.Add(Field(T("Speech 资源密钥", "Speech resource key"), azureKey));
        azurePanel.Children.Add(Text(T("密钥由当前 Windows 用户加密保存；更换电脑或用户后需重新填写。", "The key is encrypted for this Windows user. Re-enter it on another computer or account."), 12, muted: true));
        azureRegion.Text = settings.AzureRegion; azureRegion.Name = "AzureRegion"; azureRegion.MaxLength = 40;
        azureRegion.MinHeight = 44; azureRegion.VerticalContentAlignment = VerticalAlignment.Center;
        azurePanel.Children.Add(Field(T("资源区域", "Resource region"), azureRegion));
        azurePanel.Children.Add(Text(T("填写 Azure 全球资源的区域代码，如 eastasia，须与密钥所属资源一致。", "Use your Azure global resource's region code, such as eastasia. It must match the resource key."), 12, muted: true));
        var azureVoiceRow = new Grid();
        azureVoiceRow.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        azureVoiceRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        azureVoiceId.Text = settings.AzureVoiceId; azureVoiceId.Name = "AzureVoiceId"; azureVoiceId.MaxLength = 128;
        azureVoiceId.MinHeight = 44; azureVoiceId.VerticalContentAlignment = VerticalAlignment.Center;
        azureVoiceId.ToolTip = T("例如 zh-CN-XiaoxiaoNeural；仅列出中英文音色。", "For example en-US-JennyNeural; the list includes Chinese/English voices.");
        azureVoiceRow.Children.Add(azureVoiceId);
        azureRefresh = new Button { Content = T("获取音色", "Load voices"), Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(12, 7, 12, 7) };
        azureRefresh.Click += async (_, _) => await RefreshVoicesAsync();
        Grid.SetColumn(azureRefresh, 1); azureVoiceRow.Children.Add(azureRefresh);
        azurePanel.Children.Add(Field(T("音色 ID", "Voice ID"), azureVoiceRow));
        azureVoices.Visibility = Visibility.Collapsed; azureVoices.Margin = new Thickness(0, 0, 0, 12);
        azureVoices.SelectionChanged += (_, _) => { if (azureVoices.SelectedItem is SpeechVoiceInfo voice) azureVoiceId.Text = voice.Id; };
        azurePanel.Children.Add(azureVoices);
        void ClearAzureVoices() { azureVoices.ItemsSource = null; azureVoices.Visibility = Visibility.Collapsed; }
        azureRegion.TextChanged += (_, _) => ClearAzureVoices();
        azureKey.PasswordChanged += (_, _) => ClearAzureVoices();
        foreach (var name in new[] { T("跟随音色语言", "Voice language"), "中文", "English" }) azureLanguage.Items.Add(name);
        azureLanguage.SelectedIndex = settings.AzureLanguage == "en-US" ? 2 : settings.AzureLanguage == "zh-CN" ? 1 : 0;
        azurePanel.Children.Add(Field(T("播报语言", "Language"), azureLanguage));
        onlinePanel.Children.Add(azurePanel);
        BuildCloudPanels(settings);
        fallback.Content = new TextBlock { Text = T("服务不可用时使用 Windows 本地语音", "Use Windows speech when the service is unavailable"), TextWrapping = TextWrapping.Wrap };
        fallback.IsChecked = settings.FallbackToWindows; fallback.Margin = new Thickness(0, 0, 0, 10);
        onlinePanel.Children.Add(fallback);
        body.Children.Add(onlinePanel);
        var scroll = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(0, 0, 8, 0) };
        Grid.SetRow(scroll, 1); root.Children.Add(scroll);
        var footer = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
        status.Foreground = Brush("MutedBrush"); status.FontSize = 12; status.TextWrapping = TextWrapping.Wrap;
        status.Text = (cloudDrafts.TryGetValue(service.SelectedIndex, out var activeDraft) ? activeDraft.Readable : service.SelectedIndex == 2 ? azureReadable : readable) ? T("保存后，使用工程师旁的「试听」检查实际效果。", "Save, then use the engineer's Preview button to hear the result.")
            : T("保存的密钥无法解密，请重新填写。", "The saved key cannot be decrypted. Please enter it again.");
        footer.Children.Add(status);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var cancel = new Button { Content = T("取消", "Cancel"), IsCancel = true, MinWidth = 80, Padding = new Thickness(12, 7, 12, 7) };
        save = new Button { Content = T("保存", "Save"), IsDefault = true, MinWidth = 88, Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(8, 0, 0, 0), Background = Brush("AccentBrush"), Foreground = Brushes.Black };
        save.Click += async (_, _) => await SaveAsync();
        actions.Children.Add(cancel); actions.Children.Add(save); footer.Children.Add(actions);
        Grid.SetRow(footer, 2); root.Children.Add(footer);
        Content = root;
        service.SelectionChanged += (_, _) => UpdateVisibility();
        UpdateVisibility();
        Closed += (_, _) =>
        {
            lifetime.Cancel(); key.Clear(); azureKey.Clear();
            foreach (var draft in cloudDrafts.Values) foreach (var secretField in draft.Secrets.Values) secretField.Input.Clear();
        };
    }

    private void UpdateVisibility()
    {
        onlinePanel.Visibility = service.SelectedIndex != 0 ? Visibility.Visible : Visibility.Collapsed;
        elevenPanel.Visibility = service.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        azurePanel.Visibility = service.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var (index, draft) in cloudDrafts) draft.Panel.Visibility = service.SelectedIndex == index ? Visibility.Visible : Visibility.Collapsed;
        localPanel.Visibility = service.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        var providerName = service.SelectedItem?.ToString() ?? "";
        serviceNotice.Text = T($"播报文本将发送至 {providerName}；合成与试听会消耗账户额度。", $"Speech text is sent to {providerName}. Synthesis and previews use account credits.");
        Height = Math.Min(service.SelectedIndex != 0 ? 740 : 460, MaxHeight);
    }

    private async Task RefreshVoicesAsync()
    {
        if (busy) return;
        var azure = service.SelectedIndex == 2;
        SetBusy(true);
        status.Text = T("正在读取账户音色…", "Loading account voices…");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            IReadOnlyList<SpeechVoiceInfo> found;
            if (azure)
            {
                await using var provider = new AzureSpeechProvider(azureKey.Password, azureRegion.Text);
                found = await provider.GetVoicesAsync(deadline.Token);
            }
            else
            {
                await using var provider = new ElevenLabsSpeechProvider(key.Password);
                found = await provider.GetVoicesAsync(deadline.Token);
            }
            if (lifetime.IsCancellationRequested) return;
            var choices = azure ? azureVoices : voices;
            choices.ItemsSource = found;
            choices.Visibility = found.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            status.Text = found.Count == 0 ? T("账户没有可用音色，可填写 Voice ID。", "No voices found. You can enter a Voice ID.")
                : T($"已读取 {found.Count} 个音色，请选择或填写 ID。", $"Loaded {found.Count} voices. Select one or enter an ID.");
        }
        catch (OperationCanceledException) { if (!lifetime.IsCancellationRequested) status.Text = T("读取超时，请重试。", "Loading timed out. Please retry."); }
        catch (SpeechServiceException error) { status.Text = FailureText(error.Failure, english, azure ? "Azure Speech" : "ElevenLabs"); }
        catch (Exception) { status.Text = T("无法读取音色，请稍后重试。", "Could not load voices. Please retry later."); }
        finally { if (!lifetime.IsCancellationRequested) SetBusy(false); }
    }

    private async Task SaveAsync()
    {
        if (busy) return;
        var eleven = service.SelectedIndex == 1;
        var azure = service.SelectedIndex == 2;
        if (eleven && (string.IsNullOrWhiteSpace(key.Password) || !ElevenLabsSpeechProvider.IsValidVoiceId(voiceId.Text.Trim())))
        { status.Text = T("请填写 API Key 和有效的音色 ID。", "Enter an API key and a valid Voice ID."); return; }
        if (azure && (string.IsNullOrWhiteSpace(azureKey.Password) || !AzureSpeechProvider.IsValidVoiceId(azureVoiceId.Text.Trim()) ||
            !AzureSpeechProvider.IsValidRegion(azureRegion.Text.Trim().ToLowerInvariant())))
        { status.Text = T("请填写资源密钥、全球资源区域和有效的音色 ID。", "Enter a resource key, a global resource region and a valid Voice ID."); return; }
        if (cloudDrafts.TryGetValue(service.SelectedIndex, out var draft) && !ValidCloudDraft(draft))
        {
            status.Text = T("请填写该服务的凭据和有效的音色 ID。", "Enter this provider's credentials and a valid Voice ID.") +
                (draft.Id == EngineerSpeechSettings.Qwen ? T(" 千问 TTS 不能使用 Token Plan 密钥。", " Token Plan keys cannot be used for Qianwen TTS.") : "");
            return;
        }
        SetBusy(true);
        try
        {
            // Preserve untouched ciphertext, including keys belonging to a different Windows account.
            var settings = new EngineerSpeechSettings(eleven,
                key.Password == originalKey ? original.ProtectedApiKey : EngineerCredentialProtection.Protect(key.Password.Trim()),
                voiceId.Text.Trim(), ((ModelChoice)model.SelectedItem).Id,
                language.SelectedIndex == 2 ? "en-US" : language.SelectedIndex == 1 ? "zh-CN" : "auto", fallback.IsChecked == true,
                (windowsVoice.SelectedItem as WindowsSpeechVoice)?.Id ?? "",
                ServiceIds[service.SelectedIndex],
                azureKey.Password == originalAzureKey ? original.AzureProtectedApiKey : EngineerCredentialProtection.Protect(azureKey.Password.Trim()),
                azureRegion.Text.Trim().ToLowerInvariant(), azureVoiceId.Text.Trim(),
                azureLanguage.SelectedIndex == 2 ? "en-US" : azureLanguage.SelectedIndex == 1 ? "zh-CN" : "auto");
            settings = WithCloudSettings(settings);
            await apply(settings, windowsVoice.SelectedItem as WindowsSpeechVoice);
            if (!lifetime.IsCancellationRequested) DialogResult = true;
        }
        catch (Exception) { if (!lifetime.IsCancellationRequested) status.Text = T("无法保存语音设置，请重试。", "Could not save speech settings. Please retry."); }
        finally { if (!lifetime.IsCancellationRequested) SetBusy(false); }
    }

    private void SetBusy(bool value) { busy = value; onlinePanel.IsEnabled = save.IsEnabled = service.IsEnabled = !value; }
    private string T(string chinese, string en) => english ? en : chinese;
    private Brush Brush(string key) => (Brush)Application.Current.Resources[key];
    private TextBlock Text(string value, double size, bool bold = false, bool muted = false) => new()
    { Text = value, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        Foreground = Brush(muted ? "MutedBrush" : "TextBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
    private FrameworkElement Field(string label, UIElement input)
    {
        var field = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        field.Children.Add(Text(label, 12, muted: true)); field.Children.Add(input); return field;
    }
    private sealed record ModelChoice(string Id, string Label) { public override string ToString() => Label; }
    internal static string FailureText(SpeechServiceFailure failure, bool english, string providerName = "ElevenLabs") => (failure, english) switch
    {
        (SpeechServiceFailure.Authentication, false) => $"{providerName} 拒绝访问，请检查密钥、{(providerName == "Azure Speech" ? "区域、" : "")}权限和账户额度。",
        (SpeechServiceFailure.Authentication, true) => $"{providerName} denied access. Check the key, {(providerName == "Azure Speech" ? "region, " : "")}permissions and credits.",
        (SpeechServiceFailure.RateLimited, false) => $"{providerName} 请求受限，请稍后重试。",
        (SpeechServiceFailure.RateLimited, true) => $"{providerName} rate limit reached. Please retry later.",
        (SpeechServiceFailure.Configuration, false) => $"请检查 {providerName} 密钥、音色 ID 和服务设置。",
        (SpeechServiceFailure.Configuration, true) => $"Check the {providerName} key, Voice ID and service settings.",
        (_, false) => $"{providerName} 暂时不可用，请检查网络或稍后重试。",
        _ => $"{providerName} is unavailable. Check the network or retry later."
    };
}
