namespace LazyForza.Update;

public enum UpdateSourceKind
{
    GitCode,
    GitHub
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
    UpdateSourceKind Source)
{
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

public sealed record PreparedUpdate(
    Version Version,
    string WorkDirectory,
    string PackageRoot,
    string ArchivePath);

public sealed class UpdateException : Exception
{
    public UpdateException(string message) : base(message)
    {
    }

    public UpdateException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
