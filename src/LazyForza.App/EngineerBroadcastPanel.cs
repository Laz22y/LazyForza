using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using LazyForza.Modules.EstateRace;

namespace LazyForza.App;

/// <summary>Compact broadcast preferences and an on-demand, bounded transmission log.</summary>
internal sealed class EngineerBroadcastPanel : StackPanel
{
    internal const string StoreKey = "raceEngineer.broadcast.v1";
    private readonly bool english;
    private readonly Action<EngineerPreferences> apply;
    private readonly TextBlock densityHint, categoryHeader, historyHeader, repeatHint;
    private readonly ToggleButton[] densityButtons;
    private readonly CheckBox[] categoryButtons;
    private readonly Button repeat;
    private readonly StackPanel historyRows = new();
    private readonly Expander historySection;
    private IReadOnlyList<EngineerBroadcast> history = [];
    private EngineerPreferences preferences;

    internal EngineerBroadcastPanel(EngineerPreferences preferences, bool english,
        Action<EngineerPreferences> apply, Action repeatLast)
    {
        this.preferences = preferences.Normalize(); this.english = english; this.apply = apply;
        Margin = new Thickness(0, 8, 0, 0);
        Children.Add(Text(T("播报密度", "Callout density"), strong: true));
        var densityRow = new UniformGrid { Columns = 3, Margin = new Thickness(0, 6, 0, 0) };
        densityButtons = Enum.GetValues<EngineerDensity>().Select(density =>
        {
            var button = new ToggleButton
            {
                Content = density switch { EngineerDensity.Essential => T("精简", "Essential"), EngineerDensity.Detailed => T("详细", "Detailed"), _ => T("均衡", "Balanced") },
                IsChecked = density == this.preferences.Density, MinHeight = 38,
                Style = (Style)Application.Current.Resources["SettingsCategoryTab"], Margin = new Thickness(0, 0, 4, 0)
            };
            AutomationProperties.SetName(button, T("播报密度 · ", "Callout density · ") + button.Content);
            button.Click += (_, _) => Save(this.preferences with { Density = density });
            densityRow.Children.Add(button);
            return button;
        }).ToArray();
        Children.Add(densityRow);
        densityHint = Text("", muted: true); densityHint.Margin = new Thickness(0, 6, 0, 12);
        Children.Add(densityHint);

        var categories = new UniformGrid { Columns = 2 };
        var names = new[] { T("旗语变化", "Flags"), T("处罚通知", "Penalties"), T("个人最快圈", "Personal bests"), T("进站建议", "Pit advice") };
        categoryButtons = names.Select((name, index) =>
        {
            var check = new CheckBox { Content = name, MinHeight = 36, Margin = new Thickness(0, 0, 12, 4) };
            check.Click += (_, _) => Save(index switch
            {
                0 => this.preferences with { Flags = check.IsChecked == true },
                1 => this.preferences with { Penalties = check.IsChecked == true },
                2 => this.preferences with { LapTimes = check.IsChecked == true },
                _ => this.preferences with { PitAdvice = check.IsChecked == true }
            });
            categories.Children.Add(check);
            return check;
        }).ToArray();
        categoryHeader = Text("", strong: true);
        Children.Add(Section(categoryHeader, categories));

        var historyBody = new StackPanel();
        historyBody.Children.Add(Text(T("最近 20 次播报 · 试听不计入", "Last 20 transmissions · previews excluded"), muted: true));
        historyBody.Children.Add(new ScrollViewer { Content = historyRows, MaxHeight = 260,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Margin = new Thickness(0, 8, 0, 0) });
        historyHeader = Text(T("最近播报", "Recent callouts"), strong: true);
        historySection = Section(historyHeader, historyBody);
        historySection.Expanded += (_, _) => RenderHistory();
        Children.Add(historySection);
        var repeatRow = new DockPanel { Margin = new Thickness(0, 2, 0, 8) };
        repeat = new Button { Content = T("重复上一条", "Repeat last"), IsEnabled = false, MinHeight = 36,
            Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 12, 0) };
        repeat.Click += (_, _) => repeatLast();
        DockPanel.SetDock(repeat, Dock.Left); repeatRow.Children.Add(repeat);
        repeatHint = Text("", muted: true); repeatHint.VerticalAlignment = VerticalAlignment.Center;
        repeatRow.Children.Add(repeatHint); Children.Add(repeatRow);
        RefreshPreferences();
        Update([], EngineerRepeatState.NoHistory);
    }

    internal static EngineerPreferences Load(string? json)
    {
        try { return (string.IsNullOrWhiteSpace(json) ? new() : JsonSerializer.Deserialize<EngineerPreferences>(json) ?? new()).Normalize(); }
        catch (JsonException) { return new(); }
    }

    internal void Update(IReadOnlyList<EngineerBroadcast> next, EngineerRepeatState repeatState)
    {
        repeat.IsEnabled = repeatState == EngineerRepeatState.Ready;
        repeatHint.Text = RepeatReason(repeatState);
        repeat.ToolTip = repeatHint.Text;
        if (history.SequenceEqual(next)) return;
        history = next;
        historyHeader.Text = T("最近播报", "Recent callouts") + (history.Count == 0 ? "" : $" · {history.Count}");
        if (historySection.IsExpanded) RenderHistory();
    }

    private void Save(EngineerPreferences next)
    {
        apply(next);
        preferences = next;
        RefreshPreferences();
    }

    private void RefreshPreferences()
    {
        for (var i = 0; i < densityButtons.Length; i++) densityButtons[i].IsChecked = (int)preferences.Density == i;
        var values = new[] { preferences.Flags, preferences.Penalties, preferences.LapTimes, preferences.PitAdvice };
        for (var i = 0; i < values.Length; i++) categoryButtons[i].IsChecked = values[i];
        categoryHeader.Text = T("播报类别", "Callout categories") + $" · {values.Count(value => value)}/4";
        densityHint.Text = preferences.Density switch
        {
            EngineerDensity.Essential => T("仅旗语和处罚；仍遵守下方类别开关。", "Flags and penalties only, subject to the category switches below."),
            EngineerDensity.Detailed => T("更频繁地提醒圈速与进站变化；旗语和处罚优先。", "More frequent personal best and pit updates. Flags and penalties take priority."),
            _ => T("常规圈速与进站提醒，减少重复；旗语和处罚优先。", "Regular personal best and pit updates with fewer repeats. Flags and penalties take priority.")
        };
    }

    private void RenderHistory()
    {
        historyRows.Children.Clear();
        if (history.Count == 0) { historyRows.Children.Add(Text(T("还没有播报记录。", "No transmissions yet."), muted: true)); return; }
        foreach (var entry in history)
        {
            var row = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
            var category = entry.Category switch
            {
                "flag" => T("旗语", "Flag"), "best" => T("圈速", "Lap time"), "pit" => T("进站", "Pit"),
                _ when entry.Category.StartsWith("penalty", StringComparison.Ordinal) => T("处罚", "Penalty"), _ => T("赛事", "Race")
            };
            var status = entry.Delivery switch
            {
                EngineerDelivery.Speaking => T("播报中", "Speaking"), EngineerDelivery.Interrupted => T("已中断", "Interrupted"),
                EngineerDelivery.Failed => T("未播出", "Failed"), _ => entry.Validity == EngineerRepeatState.Ready
                    ? T("已播报", "Completed") : T("已失效", "Outdated")
            };
            row.Children.Add(Text($"{entry.At.ToLocalTime():HH:mm:ss} · {category} · {status}" + (entry.IsRepeat ? T(" · 重播", " · Repeat") : ""), muted: true));
            row.Children.Add(Text(entry.Text));
            historyRows.Children.Add(row);
        }
    }

    private string RepeatReason(EngineerRepeatState state) => state switch
    {
        EngineerRepeatState.Ready => T("按当前音量重播，保留原有效期。", "Replay at current volume within the original expiry."),
        EngineerRepeatState.NoHistory => T("完成一次播报后可用。", "Available after a completed transmission."),
        EngineerRepeatState.Expired => T("上一条已过期，不能重播。", "The last callout has expired."),
        EngineerRepeatState.StateChanged => T("赛事状态已变化，不能重播旧消息。", "Race state has changed; the old callout cannot be repeated."),
        EngineerRepeatState.NoSession => T("未连接有效赛事，不能重播。", "Connect to an active race before repeating."),
        EngineerRepeatState.Disabled => T("启用语音后可用。", "Enable speech to repeat."),
        EngineerRepeatState.Muted => T("恢复声音并提高音量后可用。", "Unmute and raise the volume to repeat."),
        EngineerRepeatState.CategoryDisabled => T("上一条所属类别已关闭。", "The last callout's category is disabled."),
        EngineerRepeatState.Busy => T("赛事播报优先，请等待播放结束。", "Race callouts take priority. Wait for playback to finish."),
        EngineerRepeatState.Incomplete => T("上一条尚未完整播出。", "The last transmission has not completed."),
        _ => T("语音服务暂不可用。", "Speech is unavailable.")
    };

    private Expander Section(TextBlock header, UIElement content) => new()
    {
        Header = header, Content = content, IsExpanded = false, Margin = new Thickness(0, 0, 0, 8),
        Style = (Style)Application.Current.Resources["SettingsSectionExpander"]
    };

    private TextBlock Text(string text, bool strong = false, bool muted = false) => new()
    {
        Text = text, FontSize = strong ? 13 : 12, FontWeight = strong ? FontWeights.SemiBold : FontWeights.Normal,
        Foreground = (Brush)Application.Current.Resources[muted ? "MutedBrush" : "TextBrush"], TextWrapping = TextWrapping.Wrap
    };

    private string T(string zh, string en) => english ? en : zh;
}
