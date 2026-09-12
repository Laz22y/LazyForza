using System.Windows;
using System.Windows.Media;
using LazyForza.Domain;
using LazyForza.Modules.EstateRace;

namespace LazyForza.Overlay;

internal sealed partial class HudSurface
{
    // Adding a theme requires one registration and a renderer. Stored placements
    // refer only to the stable ID; visibility, geometry and animation stay shared.
    private static readonly ThemeRegistration[] RaceThemes =
    [
        new(new(EstateRaceHudThemeIds.Classic, "经典", "保留现有赛事 HUD 样式"), new ClassicThemeRenderer()),
        new(new(EstateRaceHudThemeIds.Broadcast, "转播", "高对比数字与紧凑的赛事转播排版"), new BroadcastThemeRenderer())
    ];

    internal static readonly IReadOnlyList<EstateRaceHudThemeDefinition> ThemeDefinitions =
        Array.AsReadOnly(RaceThemes.Select(theme => theme.Definition).ToArray());

    private static ThemeRegistration ResolveRaceTheme(string? id) =>
        RaceThemes.FirstOrDefault(theme => string.Equals(theme.Definition.Id, id, StringComparison.OrdinalIgnoreCase))
        ?? RaceThemes[0];

    private sealed record ThemeRegistration(EstateRaceHudThemeDefinition Definition, IRaceThemeRenderer Renderer);

    private interface IRaceThemeRenderer
    {
        void Draw(HudSurface surface, DrawingContext dc, RaceWidgetContent? content, Action<DrawingContext> classic);
    }

    private sealed class ClassicThemeRenderer : IRaceThemeRenderer
    {
        public void Draw(HudSurface surface, DrawingContext dc, RaceWidgetContent? content, Action<DrawingContext> classic) => classic(dc);
    }

    private sealed class BroadcastThemeRenderer : IRaceThemeRenderer
    {
        public void Draw(HudSurface s, DrawingContext dc, RaceWidgetContent? content, Action<DrawingContext> classic)
        {
            switch (content)
            {
                case LeaderboardContent c: s.DrawBroadcastLeaderboard(dc, c); break;
                case MapContent c: s.DrawBroadcastMap(dc, c); break;
                case GripContent c: s.DrawBroadcastGrip(dc, c.State); break;
                case BannerContent c: s.DrawBroadcastBanner(dc, c.Banner); break;
                case StartLightsContent c: s.DrawBroadcastStartLights(dc, c.Session); break;
                case PitStopContent c: s.DrawBroadcastPitStop(dc, c.Snapshot); break;
                case LimiterContent c: s.DrawBroadcastLimiter(dc, c.Pit); break;
                case PenaltyContent c: s.DrawBroadcastPenalty(dc, c.Participant); break;
                case PracticeContent c: s.DrawBroadcastPractice(dc, c.Item); break;
                case PitWindowContent c: s.DrawBroadcastPitWindow(dc, c.Snapshot); break;
                case StrategyContent c: s.DrawBroadcastStrategy(dc, c.Snapshot); break;
                default: classic(dc); break;
            }
        }
    }

    private abstract record RaceWidgetContent;
    private sealed record LeaderboardContent(EstateRaceHudState State, EstateRaceSession Session,
        DateTimeOffset ServerNow, EstateRaceNetworkQuality Network) : RaceWidgetContent;
    private sealed record MapContent(EstateRaceHudState State, EstateRaceSession Session) : RaceWidgetContent;
    private sealed record GripContent(EstateRaceHudState State) : RaceWidgetContent;
    private sealed record BannerContent(EstateRaceBanner Banner) : RaceWidgetContent;
    private sealed record StartLightsContent(EstateRaceSession Session) : RaceWidgetContent;
    private sealed record PitStopContent(PitHudSnapshot Snapshot) : RaceWidgetContent;
    private sealed record LimiterContent(EstatePitServiceState Pit) : RaceWidgetContent;
    private sealed record PenaltyContent(EstateRaceParticipant Participant) : RaceWidgetContent;
    private sealed record PracticeContent(EstatePracticeTestItemState Item) : RaceWidgetContent;
    private sealed record PitWindowContent(PitWindowHudSnapshot Snapshot) : RaceWidgetContent;
    private sealed record StrategyContent(FullRaceStrategyHudSnapshot Snapshot) : RaceWidgetContent;
}
