using LazyForza.Domain;

namespace LazyForza.Overlay;

public sealed record EstateRaceHudThemeDefinition(string Id, string Name, string Description);

/// <summary>Built-in, version-stable theme identifiers; no runtime plug-in loading.</summary>
public static class EstateRaceHudThemes
{
    public static IReadOnlyList<EstateRaceHudThemeDefinition> Definitions => HudSurface.ThemeDefinitions;

    public static EstateRaceHudThemeDefinition Resolve(string? id) =>
        Definitions.FirstOrDefault(theme => string.Equals(theme.Id, id, StringComparison.OrdinalIgnoreCase))
        ?? Definitions.First(theme => theme.Id == EstateRaceHudThemeIds.Classic);
}
