using System.Text.Json;
using System.IO;

namespace LazyForza.App;

internal enum MainWindowCloseBehavior
{
    MinimizeToTray,
    ExitApplication
}

internal sealed record StartupProfile(
    int SchemaVersion,
    bool InitializationCompleted,
    string Language,
    string DataDirectory,
    MainWindowCloseBehavior CloseBehavior,
    DateTimeOffset? InitializationCompletedAt)
{
    public const int CurrentSchemaVersion = 1;
    public const string DefaultLanguage = "zh-Hans";

    public static StartupProfile CreateDefault() => new(
        CurrentSchemaVersion,
        InitializationCompleted: false,
        DefaultLanguage,
        StartupProfileStore.DefaultDataDirectory,
        MainWindowCloseBehavior.MinimizeToTray,
        InitializationCompletedAt: null);

    public StartupProfile Normalize()
    {
        var language = AppLocalization.IsSupported(Language)
            ? Language
            : DefaultLanguage;
        string dataDirectory;
        try
        {
            dataDirectory = Path.GetFullPath(string.IsNullOrWhiteSpace(DataDirectory)
                ? StartupProfileStore.DefaultDataDirectory
                : Environment.ExpandEnvironmentVariables(DataDirectory.Trim()));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            dataDirectory = StartupProfileStore.DefaultDataDirectory;
        }

        return this with
        {
            SchemaVersion = CurrentSchemaVersion,
            Language = language,
            DataDirectory = dataDirectory
        };
    }
}

internal sealed class StartupProfileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string profilePath;

    public StartupProfileStore(string? profilePath = null)
    {
        this.profilePath = Path.GetFullPath(profilePath ?? DefaultProfilePath);
    }

    public static string DefaultDataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LazyForza");

    public static string ReleaseDataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LazyForza-Release");

    public static string ProgramDataDirectory => Path.Combine(
        AppContext.BaseDirectory,
        "Data");

    public static string DefaultProfilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LazyForza",
        "startup-profile.json");

    public string ProfilePath => profilePath;

    public StartupProfile Load()
    {
        if (!File.Exists(profilePath)) return StartupProfile.CreateDefault();
        try
        {
            var json = File.ReadAllText(profilePath);
            return (JsonSerializer.Deserialize<StartupProfile>(json, JsonOptions) ??
                    StartupProfile.CreateDefault())
                .Normalize();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return StartupProfile.CreateDefault();
        }
    }

    public void Save(StartupProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var normalized = profile.Normalize();
        var directory = Path.GetDirectoryName(profilePath) ??
                        throw new InvalidOperationException("Startup profile path has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(profilePath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(normalized, JsonOptions));
            File.Move(temporaryPath, profilePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    public string ResolveDataDirectory(string? explicitDataDirectory, StartupProfile profile)
    {
        if (!string.IsNullOrWhiteSpace(explicitDataDirectory))
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(explicitDataDirectory.Trim()));
        return profile.Normalize().DataDirectory;
    }
}
