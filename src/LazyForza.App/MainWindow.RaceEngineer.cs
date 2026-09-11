using System.Windows;
using System.Windows.Controls;
using LazyForza.Modules.EstateRace;
using LazyForza.Speech;
using Microsoft.Win32;

namespace LazyForza.App;

internal sealed partial class MainWindow
{
    private RaceEngineer? raceEngineer;
    private RaceEngineerObserver? engineerObserver;
    private bool engineerEnabled;
    private bool engineerMuted;
    private int engineerVolume = 70;
    private TextBlock? engineerStatus;
    private TextBlock? engineerPreviewText;
    private Button? engineerPreviewButton;
    private readonly RaceEngineerPreviewPhrases engineerSamples = new();
    private RadioTransmission engineerTransmission = RadioTransmission.Default;
    private CustomRadioCue? engineerConnectCue, engineerDisconnectCue;
    private string? engineerCueNotice;
    private bool engineerCueImporting;
    private bool engineerCueSettingsExpanded;

    private bool EngineerEnglish => AppLocalization.CurrentLanguage.StartsWith("en", StringComparison.OrdinalIgnoreCase);
    private string EngineerText(string chinese, string english) => EngineerEnglish ? english : chinese;

    private void InitializeRaceEngineer()
    {
        engineerEnabled = bool.TryParse(store.GetAppSetting("raceEngineer.enabled"), out var enabled) && enabled;
        if (int.TryParse(store.GetAppSetting("raceEngineer.volume"), out var volume)) engineerVolume = Math.Clamp(volume, 0, 100);
        // Persist immediate mute across restarts; enabling speech remains an explicit local choice.
        engineerMuted = bool.TryParse(store.GetAppSetting("raceEngineer.muted"), out var muted) && muted;
        engineerConnectCue = LoadEngineerCue(true);
        engineerDisconnectCue = LoadEngineerCue(false);
        ApplyEngineerCues();
        raceEngineer = new RaceEngineer(new LocalRaceSpeech(EngineerEnglish, () => Volatile.Read(ref engineerTransmission)));
        engineerObserver = new RaceEngineerObserver(raceEngineer, EngineerEnglish);
        raceEngineer.Configure(engineerEnabled, engineerMuted, engineerVolume);
    }

    private void UpdateRaceEngineer()
    {
        if (moduleManager.Modules.OfType<EstateRaceModule>().FirstOrDefault() is { } module)
            engineerObserver?.Observe(module.State);
        if (engineerStatus is not null)
            engineerStatus.Text = raceEngineer?.Error is not null
                ? EngineerText("本地语音不可用。请检查 Windows 语音与音频设备，再关闭并重新启用。", "Local speech unavailable. Check Windows voices and audio devices, then disable and enable again.")
                : engineerCueNotice ?? EngineerText("仅播报重要变化；进站建议为预测。语音和提示音均在本机播放。", "Important changes only. Pit advice is a prediction. All audio plays locally.");
        if (engineerPreviewButton is not null)
        {
            engineerPreviewButton.Content = raceEngineer?.IsPreviewing == true
                ? EngineerText("停止试听", "Stop preview") : EngineerText("试听", "Preview");
            engineerPreviewButton.IsEnabled = !engineerCueImporting && !engineerMuted && engineerVolume > 0 && raceEngineer?.Error is null;
            engineerPreviewButton.ToolTip = EngineerText("按当前音量和提示音随机播放一句示例，无需连接赛事。", "Play a random sample with current volume and radio cues. No race connection required.");
        }
    }

    private static string EngineerCueKey(bool connect) => connect ? "raceEngineer.connectCue" : "raceEngineer.disconnectCue";

    private CustomRadioCue? LoadEngineerCue(bool connect)
    {
        var value = store.GetAppSetting(EngineerCueKey(connect));
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (CustomRadioCue.TryDeserialize(value, out var cue)) return cue;
        engineerCueNotice = EngineerText("自定义提示音无法读取，已使用默认音；可重新导入或恢复默认。", "Custom cue could not be read. Using the default; import again or reset it.");
        return null;
    }

    private void ApplyEngineerCues() => Volatile.Write(ref engineerTransmission, RadioTransmission.Default with
    {
        Connect = engineerConnectCue?.Audio ?? RadioCues.Connect,
        Disconnect = engineerDisconnectCue?.Audio ?? RadioCues.Disconnect
    });

    private async Task PreviewEngineerAsync()
    {
        var token = lifetimeCancellation.Token;
        var engineer = raceEngineer;
        if (engineer is null) return;
        if (engineer.IsPreviewing) { engineer.StopPreview(); UpdateRaceEngineer(); return; }
        var sample = engineerSamples.Next(EngineerEnglish);
        if (engineerPreviewText is not null) engineerPreviewText.Text = EngineerText("试听示例：", "Sample: ") + sample;
        var pending = engineer.PreviewAsync(sample);
        UpdateRaceEngineer();
        var played = await pending;
        if (token.IsCancellationRequested) return;
        if (!played && engineerPreviewText is not null)
            engineerPreviewText.Text = engineer.Error is not null
                ? EngineerText("试听失败，请检查语音与音频设备。", "Preview failed. Check speech and audio devices.")
                : EngineerText("试听已停止，或正在优先播放赛事消息。", "Preview stopped, or race messages have priority.");
        UpdateRaceEngineer();
    }

    private UIElement BuildRaceEngineerControls()
    {
        var panel = new StackPanel();
        panel.Children.Add(Label(EngineerText("本地语音比赛工程师", "Local race engineer"), 17, FontWeights.SemiBold));
        var controls = new WrapPanel { Margin = new Thickness(0, 8, 0, 8) };
        var enabled = new CheckBox { Content = EngineerText("启用语音", "Enable speech"), IsChecked = engineerEnabled, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 18, 0) };
        var mute = new Button { Content = EngineerText(engineerMuted ? "恢复声音" : "立即静音", engineerMuted ? "Unmute" : "Mute now"), Padding = new Thickness(12, 5, 12, 5) };
        var volume = new Slider { Minimum = 0, Maximum = 100, Value = engineerVolume, Width = 150, TickFrequency = 1, IsSnapToTickEnabled = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 10, 0) };
        var value = Label($"{engineerVolume}%", 12);
        controls.Children.Add(enabled);
        var volumeLabel = Label(EngineerText("音量", "Volume"), 12);
        volumeLabel.VerticalAlignment = VerticalAlignment.Center;
        value.VerticalAlignment = VerticalAlignment.Center;
        controls.Children.Add(volumeLabel);
        controls.Children.Add(volume); controls.Children.Add(value); controls.Children.Add(mute);
        engineerPreviewButton = new Button
        {
            Content = EngineerText("试听", "Preview"), Padding = new Thickness(12, 5, 12, 5),
            Margin = new Thickness(8, 0, 0, 0)
        };
        engineerPreviewButton.Click += async (_, _) => await PreviewEngineerAsync();
        controls.Children.Add(engineerPreviewButton);
        void Apply()
        {
            raceEngineer?.Configure(engineerEnabled, engineerMuted, engineerVolume);
            store.SetAppSetting("raceEngineer.enabled", engineerEnabled.ToString());
            store.SetAppSetting("raceEngineer.muted", engineerMuted.ToString());
            store.SetAppSetting("raceEngineer.volume", engineerVolume.ToString(System.Globalization.CultureInfo.InvariantCulture));
            UpdateRaceEngineer();
        }
        enabled.Click += (_, _) => { engineerEnabled = enabled.IsChecked == true; Apply(); };
        mute.Click += (_, _) =>
        {
            engineerMuted = !engineerMuted;
            Apply();
            mute.Content = EngineerText(engineerMuted ? "恢复声音" : "立即静音", engineerMuted ? "Unmute" : "Mute now");
        };
        volume.ValueChanged += (_, _) => { engineerVolume = (int)volume.Value; value.Text = $"{engineerVolume}%"; Apply(); };
        panel.Children.Add(controls);
        engineerPreviewText = Label(EngineerText("试听随机示例，无需连接赛事；使用当前音量和提示音。", "Preview a random sample offline with your current volume and radio cues."), 12, FontWeights.Normal, "MutedBrush");
        engineerPreviewText.TextWrapping = TextWrapping.Wrap;
        panel.Children.Add(engineerPreviewText);
        var cueSettings = new StackPanel { Margin = new Thickness(0, 8, 0, 8) };
        cueSettings.Children.Add(BuildEngineerCueRow(true));
        cueSettings.Children.Add(BuildEngineerCueRow(false));
        var cueHint = Label(EngineerText("WAV / MP3 / M4A / FLAC · 每段不超过 5 秒、10 MB。导入后保存副本，可随时恢复默认。", "WAV / MP3 / M4A / FLAC · Up to 5 seconds and 10 MB each. Imported copies are saved; defaults can be restored."), 12, FontWeights.Normal, "MutedBrush");
        cueHint.TextWrapping = TextWrapping.Wrap;
        cueHint.Margin = new Thickness(0, 6, 0, 0);
        cueSettings.Children.Add(cueHint);
        var expander = new Expander
        {
            Header = Label(EngineerText("自定义接通/断开音", "Custom connect/disconnect sounds"), 13, FontWeights.SemiBold),
            Style = (Style)Application.Current.Resources["SettingsSectionExpander"],
            Content = cueSettings, IsExpanded = engineerCueSettingsExpanded, Margin = new Thickness(0, 12, 0, 8)
        };
        expander.Expanded += (_, _) => engineerCueSettingsExpanded = true;
        expander.Collapsed += (_, _) => engineerCueSettingsExpanded = false;
        panel.Children.Add(expander);
        engineerStatus = Label("", 12, FontWeights.Normal, "MutedBrush");
        engineerStatus.TextWrapping = TextWrapping.Wrap;
        panel.Children.Add(engineerStatus);
        UpdateRaceEngineer();
        return Card(panel);
    }

    private UIElement BuildEngineerCueRow(bool connect)
    {
        var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = Label(connect ? EngineerText("接通音", "Connect") : EngineerText("断开音", "Disconnect"), 13);
        title.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(title);
        var name = Label("", 12, FontWeights.Normal, "MutedBrush");
        name.VerticalAlignment = VerticalAlignment.Center;
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        name.TextWrapping = TextWrapping.NoWrap;
        name.Margin = new Thickness(0, 0, 12, 0);
        Grid.SetColumn(name, 1); row.Children.Add(name);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        var choose = new Button { Content = EngineerText("选择音频…", "Choose audio…"), Padding = new Thickness(10, 4, 10, 4) };
        var reset = new Button { Content = EngineerText("恢复默认", "Reset"), Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(6, 0, 0, 0) };
        buttons.Children.Add(choose); buttons.Children.Add(reset);
        Grid.SetColumn(buttons, 2); row.Children.Add(buttons);
        void Refresh()
        {
            var cue = connect ? engineerConnectCue : engineerDisconnectCue;
            name.Text = cue is null ? EngineerText("默认无线电音", "Default radio cue") : cue.Name;
            name.ToolTip = name.Text;
            reset.IsEnabled = cue is not null || !string.IsNullOrWhiteSpace(store.GetAppSetting(EngineerCueKey(connect)));
        }
        void Save(CustomRadioCue? cue)
        {
            store.SetAppSetting(EngineerCueKey(connect), cue?.Serialize() ?? "");
            raceEngineer?.StopPreview();
            if (connect) engineerConnectCue = cue; else engineerDisconnectCue = cue;
            engineerCueNotice = null;
            ApplyEngineerCues(); Refresh(); UpdateRaceEngineer();
        }
        choose.Click += async (_, _) =>
        {
            if (engineerCueImporting) return;
            var dialog = new OpenFileDialog
            {
                Title = connect ? EngineerText("选择接通音", "Choose connect sound") : EngineerText("选择断开音", "Choose disconnect sound"),
                Filter = "Audio|*.wav;*.mp3;*.m4a;*.flac", CheckFileExists = true, Multiselect = false
            };
            if (dialog.ShowDialog(this) != true) return;
            raceEngineer?.StopPreview();
            engineerCueImporting = true; buttons.IsEnabled = false; UpdateRaceEngineer();
            var token = lifetimeCancellation.Token;
            try
            {
                var cue = await Task.Run(() => RadioCueImporter.Import(dialog.FileName, token), token);
                token.ThrowIfCancellationRequested();
                Save(cue);
            }
            catch (OperationCanceledException) { }
            catch (Exception)
            {
                engineerCueNotice = EngineerText("无法导入。请使用不超过 5 秒、10 MB 的有效音频；当前提示音已保留。", "Import failed. Use valid audio up to 5 seconds and 10 MB; the current cue is unchanged.");
            }
            finally
            {
                engineerCueImporting = false;
                if (!token.IsCancellationRequested) { buttons.IsEnabled = true; UpdateRaceEngineer(); }
            }
        };
        reset.Click += (_, _) =>
        {
            try { Save(null); }
            catch (Exception) { engineerCueNotice = EngineerText("无法保存设置，当前提示音已保留。", "Could not save settings; the current cue is unchanged."); UpdateRaceEngineer(); }
        };
        Refresh();
        return row;
    }
}
