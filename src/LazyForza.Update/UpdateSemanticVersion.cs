using System.Globalization;
using System.Text.RegularExpressions;

namespace LazyForza.Update;

public sealed partial class UpdateSemanticVersion : IComparable<UpdateSemanticVersion>
{
    private readonly string[] prereleaseIdentifiers;

    private UpdateSemanticVersion(
        int major,
        int minor,
        int patch,
        string? prereleaseLabel,
        string[] prereleaseIdentifiers)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        this.prereleaseIdentifiers = prereleaseIdentifiers;
        Value = prereleaseIdentifiers.Length == 0
            ? $"{major}.{minor}.{patch}"
            : $"{major}.{minor}.{patch}-{prereleaseLabel}";
    }

    public int Major { get; }

    public int Minor { get; }

    public int Patch { get; }

    public bool IsPrerelease => prereleaseIdentifiers.Length > 0;

    public string Value { get; }

    public Version NumericVersion => new(Major, Minor, Patch);

    public static UpdateSemanticVersion Parse(string value)
    {
        if (!TryParse(value, out var version))
            throw new FormatException($"Invalid semantic version: {value}");
        return version;
    }

    public static bool TryParse(string? value, out UpdateSemanticVersion version)
    {
        var match = SemanticVersionRegex().Match(value?.Trim() ?? string.Empty);
        if (!match.Success ||
            !int.TryParse(match.Groups["major"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var major) ||
            !int.TryParse(match.Groups["minor"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var minor) ||
            !int.TryParse(match.Groups["patch"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var patch))
        {
            version = null!;
            return false;
        }

        var prereleaseLabel = match.Groups["prerelease"].Success
            ? match.Groups["prerelease"].Value
            : null;
        var prerelease = prereleaseLabel?.Split(['.', '-'], StringSplitOptions.RemoveEmptyEntries) ?? [];
        if (prerelease.Any(identifier =>
                identifier.Length > 1 &&
                identifier[0] == '0' &&
                identifier.All(char.IsAsciiDigit)))
        {
            version = null!;
            return false;
        }

        version = new UpdateSemanticVersion(major, minor, patch, prereleaseLabel, prerelease);
        return true;
    }

    public int CompareTo(UpdateSemanticVersion? other)
    {
        if (other is null) return 1;
        var numeric = Major.CompareTo(other.Major);
        if (numeric != 0) return numeric;
        numeric = Minor.CompareTo(other.Minor);
        if (numeric != 0) return numeric;
        numeric = Patch.CompareTo(other.Patch);
        if (numeric != 0) return numeric;

        if (!IsPrerelease) return other.IsPrerelease ? 1 : 0;
        if (!other.IsPrerelease) return -1;

        var count = Math.Min(prereleaseIdentifiers.Length, other.prereleaseIdentifiers.Length);
        for (var index = 0; index < count; index++)
        {
            var comparison = CompareIdentifier(
                prereleaseIdentifiers[index],
                other.prereleaseIdentifiers[index]);
            if (comparison != 0) return comparison;
        }

        return prereleaseIdentifiers.Length.CompareTo(other.prereleaseIdentifiers.Length);
    }

    public override string ToString() => Value;

    private static int CompareIdentifier(string left, string right)
    {
        var leftNumeric = left.All(char.IsAsciiDigit);
        var rightNumeric = right.All(char.IsAsciiDigit);
        if (leftNumeric && rightNumeric)
        {
            var length = left.Length.CompareTo(right.Length);
            return length != 0 ? length : string.CompareOrdinal(left, right);
        }
        if (leftNumeric) return -1;
        if (rightNumeric) return 1;
        return string.CompareOrdinal(left, right);
    }

    [GeneratedRegex(
        @"^v?(?<major>0|[1-9]\d*)\.(?<minor>0|[1-9]\d*)\.(?<patch>0|[1-9]\d*)(?:-(?<prerelease>[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex SemanticVersionRegex();
}
