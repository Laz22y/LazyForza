using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using LazyForza.Speech;

namespace LazyForza.App;

internal sealed partial class EngineerSpeechSettingsWindow
{
    private static readonly string[] ServiceIds = [EngineerSpeechSettings.Windows, EngineerSpeechSettings.ElevenLabs,
        EngineerSpeechSettings.Azure, EngineerSpeechSettings.Tencent, EngineerSpeechSettings.Alibaba, EngineerSpeechSettings.Qwen, EngineerSpeechSettings.MiniMax];
    private readonly Dictionary<int, CloudDraft> cloudDrafts = [];

    private sealed record SecretDraft(PasswordBox Input, string InitialValue, string Cipher)
    {
        public string Save() => Input.Password == InitialValue ? Cipher : EngineerCredentialProtection.Protect(Input.Password.Trim());
    }
    private sealed class CloudDraft(string id)
    {
        public string Id { get; } = id;
        public StackPanel Panel { get; } = new();
        public Dictionary<string, SecretDraft> Secrets { get; } = [];
        public TextBox Voice { get; } = new();
        public ComboBox Voices { get; } = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 12) };
        public ComboBox Model { get; } = new();
        public ComboBox Language { get; } = new();
        public ComboBox Endpoint { get; } = new();
        public bool Readable { get; set; } = true;
        public string Secret(string name) => Secrets[name].Input.Password.Trim();
        public string Protected(string name) => Secrets[name].Save();
        public string LanguageId => Language.SelectedIndex == 2 ? "en-US" : Language.SelectedIndex == 1 ? "zh-CN" : "auto";
        public string ModelId => (Model.SelectedItem as ModelChoice)?.Id ?? "";
        public string EndpointId => Endpoint.SelectedIndex == 1 ? "international" : "china";
    }

    private void BuildCloudPanels(EngineerSpeechSettings settings)
    {
        CloudDraft Add(int index, string note)
        {
            var draft = new CloudDraft(ServiceIds[index]);
            draft.Panel.Children.Add(Text(note, 12, muted: true));
            cloudDrafts[index] = draft; onlinePanel.Children.Add(draft.Panel);
            return draft;
        }
        void Secret(CloudDraft draft, string name, string caption, string cipher, int maximum = 512)
        {
            draft.Readable &= EngineerCredentialProtection.TryUnprotect(cipher, out var value);
            var input = new PasswordBox { Name = name, MaxLength = maximum, MinHeight = 44,
                Style = (Style)Application.Current.Resources["SecretEntry"], Password = value };
            draft.Secrets[name] = new(input, value, cipher);
            draft.Panel.Children.Add(Field(caption, input));
            input.PasswordChanged += (_, _) => { draft.Voices.ItemsSource = null; draft.Voices.Visibility = Visibility.Collapsed; };
        }
        void Choices(CloudDraft draft, IReadOnlyList<string> models, string selected)
        {
            foreach (var id in models) draft.Model.Items.Add(new ModelChoice(id, id));
            draft.Model.SelectedItem = draft.Model.Items.Cast<ModelChoice>().FirstOrDefault(item => item.Id == selected) ?? draft.Model.Items[0];
            draft.Panel.Children.Add(Field(T("合成模型", "Model"), draft.Model));
        }
        void Finish(CloudDraft draft, string voice, string languageId, string documentation)
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            draft.Voice.Name = draft.Id + "VoiceId"; draft.Voice.Text = voice; draft.Voice.MaxLength = 128;
            draft.Voice.MinHeight = 44; draft.Voice.VerticalContentAlignment = VerticalAlignment.Center;
            row.Children.Add(draft.Voice);
            var button = new Button { Content = draft.Id == EngineerSpeechSettings.MiniMax ? T("获取音色", "Load voices") : T("常用音色", "Voice presets"),
                Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(12, 7, 12, 7) };
            button.Click += async (_, _) => await LoadCloudVoicesAsync(draft);
            Grid.SetColumn(button, 1); row.Children.Add(button);
            draft.Panel.Children.Add(Field(T("音色 ID", "Voice ID"), row));
            draft.Voices.SelectionChanged += (_, _) => { if (draft.Voices.SelectedItem is SpeechVoiceInfo found) draft.Voice.Text = found.Id; };
            draft.Panel.Children.Add(draft.Voices);
            var docs = new Button { Content = T("查看官方音色与接入说明 ↗", "Official voices and setup guide ↗"),
                HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 12), Padding = new Thickness(8, 5, 8, 5) };
            docs.Click += (_, _) =>
            {
                var url = draft.Id == EngineerSpeechSettings.MiniMax && draft.EndpointId == "international"
                    ? "https://platform.minimax.io/docs/api-reference/speech-t2a-http" : documentation;
                try { _ = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
                catch (Exception) { status.Text = T("无法打开文档，请检查默认浏览器。", "Could not open the guide. Check your default browser."); }
            };
            draft.Panel.Children.Add(docs);
            foreach (var name in new[] { T("跟随界面语言", "Interface language"), "中文", "English" }) draft.Language.Items.Add(name);
            draft.Language.SelectedIndex = languageId == "en-US" ? 2 : languageId == "zh-CN" ? 1 : 0;
            draft.Panel.Children.Add(Field(T("播报语言", "Language"), draft.Language));
            draft.Panel.Children.Add(Text(T("凭据由当前 Windows 用户加密保存。音色需支持所选语言。", "Credentials are encrypted for this Windows user. Choose a voice that supports the language."), 12, muted: true));
        }

        var t = settings.TencentSettings ?? new();
        var tencent = Add(3, T("使用腾讯云语音合成服务的 SecretId / SecretKey。", "Use a SecretId / SecretKey with Tencent Cloud TTS access."));
        Secret(tencent, "TencentSecretId", "SecretId", t.ProtectedSecretId, 128);
        Secret(tencent, "TencentSecretKey", "SecretKey", t.ProtectedSecretKey);
        Finish(tencent, t.VoiceId, t.Language, "https://cloud.tencent.com/document/product/1073/92668");

        var a = settings.AlibabaSettings ?? new();
        var alibaba = Add(4, T("智能语音交互（上海），与千问 AI 平台独立。使用项目 AppKey 和 RAM AccessKey，Token 自动续期。", "Intelligent Speech Interaction (Shanghai), separate from Qianwen AI. Use a project AppKey and RAM AccessKey; tokens refresh automatically."));
        Secret(alibaba, "AlibabaAppKey", "AppKey", a.ProtectedAppKey, 256);
        Secret(alibaba, "AlibabaAccessKeyId", "AccessKey ID", a.ProtectedAccessKeyId, 256);
        Secret(alibaba, "AlibabaAccessKeySecret", "AccessKey Secret", a.ProtectedAccessKeySecret, 256);
        Finish(alibaba, a.VoiceId, a.Language, "https://help.aliyun.com/zh/isi/developer-reference/overview-of-speech-synthesis");

        var q = settings.QwenSettings ?? new();
        var qwen = Add(5, T("使用千问 AI 平台北京地域的按量 API Key 调用 Qwen-TTS，不能填写智能语音交互 AppKey 或 Token Plan 密钥。", "Use a Beijing pay-as-you-go API key from Qianwen AI for Qwen-TTS. NLS AppKeys and Token Plan keys are not interchangeable."));
        Secret(qwen, "QwenApiKey", "API Key", q.ProtectedApiKey);
        Choices(qwen, QwenSpeechProvider.Models, q.ModelId);
        Finish(qwen, q.VoiceId, q.Language, "https://platform.qianwenai.com/docs/developer-guides/speech/tts");

        var m = settings.MiniMaxSettings ?? new();
        var minimax = Add(6, T("直连 MiniMax 账户。站点须与密钥所属平台一致。", "Connect directly to MiniMax. Choose the site that issued your API key."));
        Secret(minimax, "MiniMaxApiKey", "API Key", m.ProtectedApiKey, 4096);
        minimax.Endpoint.Items.Add(T("中国站", "China")); minimax.Endpoint.Items.Add(T("国际站", "International"));
        minimax.Endpoint.SelectedIndex = m.Endpoint == "international" ? 1 : 0;
        minimax.Panel.Children.Add(Field(T("账户站点", "Account site"), minimax.Endpoint));
        minimax.Endpoint.SelectionChanged += (_, _) => { minimax.Voices.ItemsSource = null; minimax.Voices.Visibility = Visibility.Collapsed; };
        Choices(minimax, MiniMaxSpeechProvider.Models, m.ModelId);
        Finish(minimax, m.VoiceId, m.Language, "https://platform.minimax.cn/docs/api-reference/speech-t2a-http");
    }

    private static bool ValidCloudDraft(CloudDraft draft)
    {
        if (draft.Secrets.Values.Any(s => string.IsNullOrWhiteSpace(s.Input.Password) || s.Input.Password.Trim().Any(c => c < '!' || c > '~'))) return false;
        var voice = draft.Voice.Text.Trim();
        return draft.Id switch
        {
            EngineerSpeechSettings.Tencent => TencentSpeechProvider.IsValidVoiceId(voice) && TencentSpeechProvider.IsValidCredentials(draft.Secret("TencentSecretId"), draft.Secret("TencentSecretKey")),
            EngineerSpeechSettings.Alibaba => AlibabaSpeechProvider.IsValidVoiceId(voice) && draft.Secrets.Values.All(s => AlibabaSpeechProvider.IsValidCredential(s.Input.Password.Trim())),
            EngineerSpeechSettings.Qwen => QwenSpeechProvider.IsValidVoiceId(voice) && !draft.Secret("QwenApiKey").StartsWith("sk-sp-", StringComparison.Ordinal),
            EngineerSpeechSettings.MiniMax => MiniMaxSpeechProvider.IsValidVoiceId(voice),
            _ => false
        };
    }

    private EngineerSpeechSettings WithCloudSettings(EngineerSpeechSettings settings)
    {
        var t = cloudDrafts[3]; var a = cloudDrafts[4]; var q = cloudDrafts[5]; var m = cloudDrafts[6];
        return settings with
        {
            TencentSettings = new(t.Protected("TencentSecretId"), t.Protected("TencentSecretKey"), t.Voice.Text.Trim(), t.LanguageId),
            AlibabaSettings = new(a.Protected("AlibabaAppKey"), a.Protected("AlibabaAccessKeyId"), a.Protected("AlibabaAccessKeySecret"), a.Voice.Text.Trim(), a.LanguageId),
            QwenSettings = new(q.Protected("QwenApiKey"), q.Voice.Text.Trim(), q.ModelId, q.LanguageId),
            MiniMaxSettings = new(m.Protected("MiniMaxApiKey"), m.Voice.Text.Trim(), m.ModelId, m.EndpointId, m.LanguageId)
        };
    }

    private async Task LoadCloudVoicesAsync(CloudDraft draft)
    {
        if (busy) return;
        SetBusy(true);
        try
        {
            IReadOnlyList<SpeechVoiceInfo> found;
            if (draft.Id == EngineerSpeechSettings.MiniMax)
            {
                status.Text = T("正在读取账户音色…", "Loading account voices…");
                await using var provider = new MiniMaxSpeechProvider(draft.Secret("MiniMaxApiKey"), endpoint: draft.EndpointId);
                found = await provider.GetVoicesAsync(lifetime.Token);
            }
            else
            {
                found = draft.Id switch
                {
                    EngineerSpeechSettings.Tencent =>
                    [
                        new(TencentSpeechProvider.DefaultVoice, T("智瑜 · 中文女声", "Zhiyu · Chinese female")),
                        new("101004", T("智云 · 中文男声", "Zhiyun · Chinese male")),
                        new("101021", T("智瑞 · 中文新闻男声", "Zhirui · Chinese news male")),
                        new("101050", T("WeJack · 英文男声", "WeJack · English male"))
                    ],
                    EngineerSpeechSettings.Alibaba => [new("xiaoyun", "Xiaoyun"), new("xiaogang", "Xiaogang")],
                    _ => [new("Cherry", "Cherry"), new("Serena", "Serena"), new("Ethan", "Ethan"), new("Chelsie", "Chelsie")]
                };
            }
            if (lifetime.IsCancellationRequested) return;
            draft.Voices.ItemsSource = found;
            draft.Voices.Visibility = found.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            status.Text = draft.Id == EngineerSpeechSettings.MiniMax
                ? T($"已读取 {found.Count} 个音色，请选择或填写 ID。", $"Loaded {found.Count} voices. Select one or enter an ID.")
                : T("常用音色来自官方文档；更多音色可查看说明后填写 ID。", "Presets come from the official guide. You can enter another supported Voice ID.");
        }
        catch (OperationCanceledException) { }
        catch (SpeechServiceException error) { if (!lifetime.IsCancellationRequested) status.Text = FailureText(error.Failure, english, "MiniMax"); }
        catch (Exception) { if (!lifetime.IsCancellationRequested) status.Text = T("无法读取音色，请稍后重试。", "Could not load voices. Please retry later."); }
        finally { if (!lifetime.IsCancellationRequested) SetBusy(false); }
    }
}
