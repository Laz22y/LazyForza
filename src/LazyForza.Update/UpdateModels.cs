namespace LazyForza.Update;

public enum UpdateSourceKind
{
    GitCode,
    GitHub
}

public enum UpdateReleaseType
{
    MajorFeature,
    Feature,
    Fix
}

public static class UpdateReleaseTypeInfo
{
    public static string DisplayName(this UpdateReleaseType type) => type switch
    {
        UpdateReleaseType.MajorFeature => "重大功能更新",
        UpdateReleaseType.Feature => "功能更新",
        UpdateReleaseType.Fix => "修复更新",
        _ => type.ToString()
    };

    public static string MarkerValue(this UpdateReleaseType type) => type switch
    {
        UpdateReleaseType.MajorFeature => "major-feature",
        UpdateReleaseType.Feature => "feature",
        UpdateReleaseType.Fix => "fix",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
    };
}

public sealed record ParsedUpdateReleaseNotes(
    UpdateReleaseType Type,
    string Notes);

public static class UpdateReleaseMetadata
{
    private const string MarkerPrefix = "<!-- lazyforza-update-type:";
    private const string MarkerSuffix = "-->";

    public static ParsedUpdateReleaseNotes Parse(
        string? releaseBody,
        Version currentVersion,
        Version releaseVersion)
    {
        ArgumentNullException.ThrowIfNull(currentVersion);
        ArgumentNullException.ThrowIfNull(releaseVersion);

        var body = releaseBody ?? string.Empty;
        var markerStart = body.IndexOf(MarkerPrefix, StringComparison.OrdinalIgnoreCase);
        if (markerStart >= 0)
        {
            var valueStart = markerStart + MarkerPrefix.Length;
            var markerEnd = body.IndexOf(
                MarkerSuffix,
                valueStart,
                StringComparison.OrdinalIgnoreCase);
            if (markerEnd >= 0)
            {
                var value = body[valueStart..markerEnd].Trim();
                if (TryParseType(value, out var explicitType))
                {
                    var cleaned = string.Concat(
                        body.AsSpan(0, markerStart),
                        body.AsSpan(markerEnd + MarkerSuffix.Length)).Trim();
                    return new ParsedUpdateReleaseNotes(explicitType, cleaned);
                }
            }
        }

        return new ParsedUpdateReleaseNotes(
            InferType(currentVersion, releaseVersion),
            body.Trim());
    }

    public static string Marker(UpdateReleaseType type) =>
        $"{MarkerPrefix} {type.MarkerValue()} {MarkerSuffix}";

    public static string ToDisplayText(string? notes) =>
        ToDisplayText(notes, language: null);

    public static string ToDisplayText(string? notes, string? language)
    {
        if (string.IsNullOrWhiteSpace(notes))
            return "本次发行暂未提供更新说明。";

        var selectedNotes = SelectLocalizedNotes(notes, language);
        var displayLines = selectedNotes
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n')
            .Select(FormatMarkdownLine)
            .ToList();

        while (displayLines.Count > 0 && displayLines[0].Length == 0)
            displayLines.RemoveAt(0);
        while (displayLines.Count > 0 && displayLines[^1].Length == 0)
            displayLines.RemoveAt(displayLines.Count - 1);

        return displayLines.Count == 0
            ? "本次发行暂未提供更新说明。"
            : string.Join(Environment.NewLine, displayLines);
    }

    private static string SelectLocalizedNotes(string notes, string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
            return notes;

        var lines = notes
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
        var sections = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        string? currentSection = null;

        foreach (var line in lines)
        {
            var section = LocalizedSection(line);
            if (section is not null)
            {
                currentSection = section;
                sections.TryAdd(section, []);
                continue;
            }

            if (currentSection is not null)
                sections[currentSection].Add(line);
        }

        if (sections.Count == 0)
            return notes;

        var requestedSection = language.StartsWith("en", StringComparison.OrdinalIgnoreCase)
            ? "en"
            : "zh-Hans";
        if (!sections.TryGetValue(requestedSection, out var selectedLines) ||
            selectedLines.All(string.IsNullOrWhiteSpace))
        {
            selectedLines = sections.Values.FirstOrDefault(candidate =>
                candidate.Any(line => !string.IsNullOrWhiteSpace(line)));
        }

        return selectedLines is null ? string.Empty : string.Join('\n', selectedLines);
    }

    private static string? LocalizedSection(string line)
    {
        var value = line.Trim();
        var index = 0;
        while (index < value.Length && value[index] == '#')
            index++;
        if (index == 0 || index >= value.Length || !char.IsWhiteSpace(value[index]))
            return null;

        var heading = value[(index + 1)..].Trim();
        if (heading.Equals("English", StringComparison.OrdinalIgnoreCase))
            return "en";
        return heading is "简体中文" or "中文" ? "zh-Hans" : null;
    }

    private static string FormatMarkdownLine(string line)
    {
        var value = line.TrimEnd();
        var leadingTrimmed = value.TrimStart();

        if (leadingTrimmed.StartsWith('#'))
        {
            var index = 0;
            while (index < leadingTrimmed.Length && leadingTrimmed[index] == '#')
                index++;
            if (index < leadingTrimmed.Length && char.IsWhiteSpace(leadingTrimmed[index]))
                leadingTrimmed = leadingTrimmed[(index + 1)..].TrimStart();
            value = leadingTrimmed;
        }
        else if (leadingTrimmed.StartsWith("- ", StringComparison.Ordinal) ||
                 leadingTrimmed.StartsWith("* ", StringComparison.Ordinal))
        {
            value = $"• {leadingTrimmed[2..]}";
        }
        else if (leadingTrimmed.StartsWith("> ", StringComparison.Ordinal))
        {
            value = leadingTrimmed[2..];
        }

        return value
            .Replace("**", string.Empty, StringComparison.Ordinal)
            .Replace("__", string.Empty, StringComparison.Ordinal)
            .Replace("`", string.Empty, StringComparison.Ordinal);
    }

    private static bool TryParseType(string value, out UpdateReleaseType type)
    {
        if (string.Equals(value, "major-feature", StringComparison.OrdinalIgnoreCase))
        {
            type = UpdateReleaseType.MajorFeature;
            return true;
        }
        if (string.Equals(value, "feature", StringComparison.OrdinalIgnoreCase))
        {
            type = UpdateReleaseType.Feature;
            return true;
        }
        if (string.Equals(value, "fix", StringComparison.OrdinalIgnoreCase))
        {
            type = UpdateReleaseType.Fix;
            return true;
        }

        type = default;
        return false;
    }

    private static UpdateReleaseType InferType(
        Version currentVersion,
        Version releaseVersion)
    {
        if (releaseVersion.Major > currentVersion.Major)
            return UpdateReleaseType.MajorFeature;
        if (releaseVersion.Minor > currentVersion.Minor)
            return UpdateReleaseType.Feature;
        return UpdateReleaseType.Fix;
    }
}

public sealed record UpdateReleaseAsset(
    string Name,
    Uri DownloadUri,
    long? Size,
    string? Digest);

public sealed record UpdateReleaseInfo(
    Version Version,
    string Tag,
    string Name,
    string Notes,
    Uri PageUri,
    UpdateReleaseAsset Package,
    UpdateReleaseAsset? Checksum,
    UpdateSourceKind Source,
    UpdateReleaseType Type = UpdateReleaseType.Fix,
    UpdateReleaseAsset? Installer = null,
    UpdateReleaseAsset? InstallerChecksum = null,
    string? VersionLabel = null)
{
    public string ArtifactVersion => VersionLabel ?? Version.ToString(3);

    public string SourceName => Source switch
    {
        UpdateSourceKind.GitCode => "GitCode",
        UpdateSourceKind.GitHub => "GitHub",
        _ => Source.ToString()
    };
}

public sealed record UpdateProgress(
    string Stage,
    long BytesReceived = 0,
    long? TotalBytes = null)
{
    public double? Fraction => TotalBytes is > 0
        ? Math.Clamp((double)BytesReceived / TotalBytes.Value, 0, 1)
        : null;
}

public enum UpdatePackageKind
{
    Portable,
    Installer
}

public sealed record PreparedUpdate(
    Version Version,
    string WorkDirectory,
    string PackageRoot,
    string ArchivePath,
    UpdatePackageKind Kind = UpdatePackageKind.Portable,
    string? VersionLabel = null)
{
    public string ArtifactVersion => VersionLabel ?? Version.ToString(3);
}

public sealed class UpdateException : Exception
{
    public UpdateException(string message) : base(message)
    {
    }

    public UpdateException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
