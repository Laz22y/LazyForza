using System.Windows;
using System.Windows.Controls;
using LazyForza.Modules.EstateRace;

namespace LazyForza.App;

internal sealed partial class MainWindow
{
    private RaceEngineer? raceEngineer;
    private RaceEngineerObserver? engineerObserver;
    private bool engineerEnabled;
    private bool engineerMuted;
    private int engineerVolume = 70;
    private TextBlock? engineerStatus;

    private bool EngineerEnglish => AppLocalization.CurrentLanguage.StartsWith("en", StringComparison.OrdinalIgnoreCase);
    private string EngineerText(string chinese, string english) => EngineerEnglish ? english : chinese;

    private void InitializeRaceEngineer()
    {
        engineerEnabled = bool.TryParse(store.GetAppSetting("raceEngineer.enabled"), out var enabled) && enabled;
        if (int.TryParse(store.GetAppSetting("raceEngineer.volume"), out var volume)) engineerVolume = Math.Clamp(volume, 0, 100);
        // Persist immediate mute across restarts; enabling speech remains an explicit local choice.
        engineerMuted = bool.TryParse(store.GetAppSetting("raceEngineer.muted"), out var muted) && muted;
        raceEngineer = new RaceEngineer(new LocalRaceSpeech(EngineerEnglish));
        engineerObserver = new RaceEngineerObserver(raceEngineer, EngineerEnglish);
        raceEngineer.Configure(engineerEnabled, engineerMuted, engineerVolume);
    }

    private void UpdateRaceEngineer()
    {
        if (moduleManager.Modules.OfType<EstateRaceModule>().FirstOrDefault() is { } module)
            engineerObserver?.Observe(module.State);
        if (engineerStatus is not null)
            engineerStatus.Text = raceEngineer?.Error is not null
                ? EngineerText("本地语音不可用。请检查 Windows 已安装的语音，再关闭并重新启用。", "Local speech unavailable. Check installed Windows voices, then disable and enable again.")
                : EngineerText("仅播报重要变化；进站建议为预测。语音及无线电提示音均在本机生成。", "Important changes only. Pit advice is a prediction. Speech and radio cues are generated locally.");
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
        controls.Children.Add(Label(EngineerText("音量", "Volume"), 12));
        controls.Children.Add(volume); controls.Children.Add(value); controls.Children.Add(mute);
        void Apply()
        {
            raceEngineer?.Configure(engineerEnabled, engineerMuted, engineerVolume);
            store.SetAppSetting("raceEngineer.enabled", engineerEnabled.ToString());
            store.SetAppSetting("raceEngineer.muted", engineerMuted.ToString());
            store.SetAppSetting("raceEngineer.volume", engineerVolume.ToString(System.Globalization.CultureInfo.InvariantCulture));
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
        engineerStatus = Label("", 12, FontWeights.Normal, "MutedBrush");
        engineerStatus.TextWrapping = TextWrapping.Wrap;
        panel.Children.Add(engineerStatus);
        UpdateRaceEngineer();
        return Card(panel);
    }
}
