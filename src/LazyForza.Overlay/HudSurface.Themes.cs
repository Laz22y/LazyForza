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
        new(new(EstateRaceHudThemeIds.Classic, "经典", "保留现有赛事 HUD 样式"), new ClassicThemeRenderer())
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
