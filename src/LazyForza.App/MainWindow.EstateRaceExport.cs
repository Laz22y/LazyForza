using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using LazyForza.Modules.EstateRace;

namespace LazyForza.App;

internal sealed partial class MainWindow
{
    private FrameworkElement BuildEstateRacePersonalResultReport(
        EstateRaceSession session,
        EstateRaceParticipant participant)
    {
        if (session.Phase is not (RaceSessionPhase.Grid or RaceSessionPhase.Finished))
            throw new InvalidOperationException("只有排位赛冻结后或正赛结束后才能导出成绩。");

        var isQualifying = session.Phase == RaceSessionPhase.Grid;
        var accent = ParseResultBrush(participant.ThemeColor);
        var root = new Border
        {
            Width = 1120,
            Padding = new Thickness(48),
            Background = new SolidColorBrush(Color.FromRgb(8, 11, 17)),
            BorderBrush = accent,
            BorderThickness = new Thickness(8, 0, 0, 0)
        };
        var content = new StackPanel();
        root.Child = content;

        var brand = new TextBlock
        {
            Text = "LAZYFORZA · ESTATE RACING",
            Foreground = new SolidColorBrush(Color.FromRgb(56, 213, 232)),
            FontSize = 16,
            FontWeight = FontWeights.SemiBold
        };
        content.Children.Add(brand);
        content.Children.Add(new TextBlock
        {
            Text = isQualifying ? "排位赛成绩" : "正赛成绩",
            Foreground = Brushes.White,
            FontSize = 42,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 10, 0, 2)
        });
        content.Children.Add(new TextBlock
        {
            Text = $"{session.SessionName} · {session.TrackName ?? "地产环道"}",
            Foreground = new SolidColorBrush(Color.FromRgb(171, 184, 198)),
            FontSize = 18,
            Margin = new Thickness(0, 0, 0, 34)
        });

        var identity = new Grid { Margin = new Thickness(0, 0, 0, 34) };
        identity.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        identity.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var position = new Border
        {
            Width = 150,
            Height = 118,
            Background = accent,
            CornerRadius = new CornerRadius(8),
            Child = new TextBlock
            {
                Text = $"P{participant.Position}",
                Foreground = new SolidColorBrush(Color.FromRgb(5, 8, 12)),
                FontSize = 54,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        identity.Children.Add(position);
        var driver = new StackPanel { Margin = new Thickness(26, 10, 0, 0) };
        driver.Children.Add(new TextBlock
        {
            Text = participant.DisplayName,
            Foreground = Brushes.White,
            FontSize = 34,
            FontWeight = FontWeights.SemiBold
        });
        driver.Children.Add(new TextBlock
        {
            Text = session.AllowTeams && !string.IsNullOrWhiteSpace(participant.TeamName)
                ? participant.TeamName
                : "个人参赛",
            Foreground = accent,
            FontSize = 17,
            Margin = new Thickness(0, 6, 0, 0)
        });
        Grid.SetColumn(driver, 1);
        identity.Children.Add(driver);
        content.Children.Add(identity);

        var metrics = new UniformGrid { Columns = 4 };
        metrics.Children.Add(ResultMetric("最终名次", $"P{participant.Position} / {session.Participants.Count}"));
        metrics.Children.Add(ResultMetric(
            isQualifying ? "最快圈" : "正赛总时间",
            isQualifying
                ? FormatEstateRaceResultTime(participant.BestLapSeconds)
                : FormatEstateRaceResultTime(participant.AdjustedRaceTotalSeconds ?? participant.RaceTotalSeconds, raceTime: true)));
        metrics.Children.Add(ResultMetric("完成圈数", participant.CompletedLaps.ToString(CultureInfo.InvariantCulture)));
        metrics.Children.Add(ResultMetric(
            participant.Position == 1 ? "领先状态" : "与第一名",
            participant.Position == 1 ? "LEADER" : FormatEstateRaceResultDelta(participant.GapToLeaderSeconds)));
        content.Children.Add(metrics);

        var penalty = participant.TimePenaltySeconds > 0 || participant.PendingTimePenaltySeconds > 0
            ? $"判罚：+{participant.TimePenaltySeconds + participant.PendingTimePenaltySeconds:0} 秒"
            : "判罚：无计时罚时";
        content.Children.Add(new TextBlock
        {
            Text = $"{penalty}   ·   最佳单圈 {FormatEstateRaceResultTime(participant.BestLapSeconds)}   ·   导出于 {DateTime.Now:yyyy-MM-dd HH:mm}",
            Foreground = new SolidColorBrush(Color.FromRgb(142, 154, 168)),
            FontSize = 15,
            Margin = new Thickness(0, 34, 0, 0)
        });
        return root;
    }

    private static Border ResultMetric(string label, string value)
    {
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = new SolidColorBrush(Color.FromRgb(142, 154, 168)),
            FontSize = 14
        });
        stack.Children.Add(new TextBlock
        {
            Text = value,
            Foreground = Brushes.White,
            FontSize = 25,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 7, 0, 0)
        });
        return new Border
        {
            Margin = new Thickness(0, 0, 12, 0),
            Padding = new Thickness(18),
            MinHeight = 98,
            Background = new SolidColorBrush(Color.FromRgb(18, 25, 35)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(42, 54, 70)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Child = stack
        };
    }

    private static Brush ParseResultBrush(string value)
    {
        try { return (Brush)new BrushConverter().ConvertFromString(value)!; }
        catch { return new SolidColorBrush(Color.FromRgb(56, 213, 232)); }
    }

    private static string FormatEstateRaceResultDelta(double? seconds) =>
        seconds is double value && double.IsFinite(value)
            ? $"{(value >= 0 ? "+" : "−")}{Math.Abs(value):0.000}"
            : "—";

    private static string FormatEstateRaceResultTime(double? seconds, bool raceTime = false)
    {
        if (seconds is not double value || !double.IsFinite(value) || value < 0) return "—";
        var totalMilliseconds = (long)Math.Round(value * 1000);
        var hours = totalMilliseconds / 3_600_000;
        var minutes = totalMilliseconds % 3_600_000 / 60_000;
        var wholeSeconds = totalMilliseconds % 60_000 / 1000;
        var milliseconds = totalMilliseconds % 1000;
        return raceTime && hours > 0
            ? $"{hours}:{minutes:00}:{wholeSeconds:00}.{milliseconds:000}"
            : $"{(raceTime ? totalMilliseconds / 60_000 : minutes)}:{wholeSeconds:00}.{milliseconds:000}";
    }
}
