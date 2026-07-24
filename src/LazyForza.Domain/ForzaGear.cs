using System.Globalization;

namespace LazyForza.Domain;

/// <summary>
/// Converts the FH Data Out gear code to a driver-facing gear. The raw byte remains available
/// in diagnostics and recordings so mappings can be audited against the real game.
/// </summary>
public static class ForzaGear
{
    public static bool IsKnown(byte rawCode) => rawCode == 0 || rawCode is >= 1 and <= 10;

    public static int? ForwardNumber(byte rawCode) => rawCode is >= 1 and <= 10 ? rawCode : null;

    public static string Display(byte rawCode) => rawCode switch
    {
        0 => "R",
        >= 1 and <= 10 => rawCode.ToString(CultureInfo.InvariantCulture),
        _ => "—"
    };
}
