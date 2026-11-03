using System.Text.Json;

namespace LazyForza.App;

internal static class SidebarQuickSettings
{
    internal const string StoreKey = "ui.sidebarQuickSettings";
    internal const string Mute = "engineerMute", Volume = "engineerVolume", Motion = "reduceMotion";
    internal const string ShiftRecommendations = "shiftRecommendations";
    internal const string ShiftIndicators = "shiftIndicators";
    internal const string HudOpacity = "hudOpacity", EstateBackdropOpacity = "estateBackdropOpacity";
    internal const string AutomaticRecording = "automaticRecording";
    internal const string HudProfiles = "hudProfiles";
    internal const string ContinuousDelta = "continuousDelta", DeltaComparison = "deltaComparison";
    internal const string RecordUnmatchedTracks = "recordUnmatchedTracks";
    internal static readonly string[] Available =
        [Mute, ShiftIndicators, HudProfiles, ContinuousDelta, DeltaComparison, RecordUnmatchedTracks, ShiftRecommendations, Volume, HudOpacity, EstateBackdropOpacity, AutomaticRecording, Motion];
    internal static string[] Default => [Mute, ShiftIndicators];

    internal static string[] Load(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Default;
        try
        {
            var ids = JsonSerializer.Deserialize<string[]>(json);
            return ids is null ? Default : Normalize(ids);
        }
        catch (JsonException) { return Default; }
    }

    internal static string[] Normalize(IEnumerable<string> ids) => Available.Where(ids.Contains).ToArray();
    internal static string Save(IEnumerable<string> ids) => JsonSerializer.Serialize(Normalize(ids));
}
