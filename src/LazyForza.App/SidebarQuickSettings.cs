using System.Text.Json;

namespace LazyForza.App;

internal static class SidebarQuickSettings
{
    internal const string StoreKey = "ui.sidebarQuickSettings";
    internal const string Mute = "engineerMute", Volume = "engineerVolume", Motion = "reduceMotion";
    internal static readonly string[] Available = [Mute, Volume, Motion];

    internal static string[] Load(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [Mute];
        try
        {
            var ids = JsonSerializer.Deserialize<string[]>(json);
            return ids is null ? [Mute] : Normalize(ids);
        }
        catch (JsonException) { return [Mute]; }
    }

    internal static string[] Normalize(IEnumerable<string> ids) => Available.Where(ids.Contains).ToArray();
    internal static string Save(IEnumerable<string> ids) => JsonSerializer.Serialize(Normalize(ids));
}
