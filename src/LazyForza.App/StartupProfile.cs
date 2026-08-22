using System.IO;
using System.Text.Json;

namespace LazyForza.App;

internal enum MainWindowCloseBehavior
{
    MinimizeToTray,
    ExitApplication
}

internal enum ApplicationDistributionKind
{
    Installed,
    Portable,
    Development
}

internal sealed record ApplicationDistribution(
    ApplicationDistributionKind Kind,
    string ProfilePath,
    string InitializationStatePath)
{
    public const string InstalledMarkerFileName = "LazyForza.Installation";
    public const string DevelopmentMarkerFileName = "LazyForza.Development";

    public bool IsInstalled => Kind == ApplicationDistributionKind.Installed;

    public bool IsDevelopment => Kind == ApplicationDistributionKind.Development;

    public bool DefaultUpdateCheckEnabled => IsInstalled;

    public static ApplicationDistribution Detect(
        string baseDirectory,
        string? localApplicationData = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        var applicationRoot = Path.GetFullPath(baseDirectory);
        var localRoot = Path.GetFullPath(localApplicationData ??
                                         Environment.GetFolderPath(
                                             Environment.SpecialFolder.LocalApplicationData));
        var installed = File.Exists(Path.Combine(applicationRoot, InstalledMarkerFileName));
        var development = !installed &&
                          File.Exists(Path.Combine(applicationRoot, DevelopmentMarkerFileName));
        var kind = installed
            ? ApplicationDistributionKind.Installed
            : development
                ? ApplicationDistributionKind.Development
                : ApplicationDistributionKind.Portable;
        var stateRoot = installed
            ? Path.Combine(localRoot, "LazyForza")
            : Path.Combine(applicationRoot, "LazyForza_Data");
        return new ApplicationDistribution(
            kind,
            Path.Combine(stateRoot, "startup-profile.json"),
            Path.Combine(stateRoot, "initialization-state.json"));
    }
}

internal sealed record StartupProfile(
    int SchemaVersion,
    string Language,
    string DataDirectory,
    MainWindowCloseBehavior CloseBehavior)
{
    public const int CurrentSchemaVersion = 2;
    public const string DefaultLanguage = "zh-Hans";

    public static StartupProfile CreateDefault() => new(
        CurrentSchemaVersion,
        DefaultLanguage,
        StartupProfileStore.DefaultDataDirectory,
        MainWindowCloseBehavior.MinimizeToTray);

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

internal sealed record StartupProfileLoadResult(
    StartupProfile Profile,
    bool LegacyInitializationCompleted,
    DateTimeOffset? LegacyInitializationCompletedAt);

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

    public StartupProfile Load() => LoadWithMigration().Profile;

    public StartupProfileLoadResult LoadWithMigration()
    {
        if (!File.Exists(profilePath))
            return new StartupProfileLoadResult(StartupProfile.CreateDefault(), false, null);
        try
        {
            var json = File.ReadAllText(profilePath);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var schemaVersion = root.TryGetProperty("schemaVersion", out var schemaElement) &&
                                schemaElement.TryGetInt32(out var parsedSchema)
                ? parsedSchema
                : 0;
            var legacyCompleted = schemaVersion < StartupProfile.CurrentSchemaVersion &&
                                  root.TryGetProperty("initializationCompleted", out var completedElement) &&
                                  completedElement.ValueKind is JsonValueKind.True or JsonValueKind.False &&
                                  completedElement.GetBoolean();
            DateTimeOffset? legacyCompletedAt = null;
            if (legacyCompleted &&
                root.TryGetProperty("initializationCompletedAt", out var completedAtElement) &&
                completedAtElement.ValueKind == JsonValueKind.String &&
                completedAtElement.TryGetDateTimeOffset(out var parsedCompletedAt))
                legacyCompletedAt = parsedCompletedAt;
            var profile = (JsonSerializer.Deserialize<StartupProfile>(json, JsonOptions) ??
                           StartupProfile.CreateDefault())
                .Normalize();
            return new StartupProfileLoadResult(profile, legacyCompleted, legacyCompletedAt);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new StartupProfileLoadResult(StartupProfile.CreateDefault(), false, null);
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

internal sealed record InitializationStateSnapshot(
    bool Exists,
    bool Completed,
    DateTimeOffset? CompletedAt);

internal sealed class InitializationStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string statePath;

    public InitializationStateStore(string statePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(statePath);
        this.statePath = Path.GetFullPath(statePath);
    }

    public string StatePath => statePath;

    public InitializationStateSnapshot Load()
    {
        if (!File.Exists(statePath)) return new InitializationStateSnapshot(false, false, null);
        try
        {
            var state = JsonSerializer.Deserialize<InitializationStateDocument>(
                File.ReadAllText(statePath),
                JsonOptions);
            return new InitializationStateSnapshot(true, state?.Completed == true, state?.CompletedAt);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new InitializationStateSnapshot(true, false, null);
        }
    }

    public void MarkCompleted(DateTimeOffset? completedAt = null) =>
        Save(new InitializationStateDocument(1, true, completedAt ?? DateTimeOffset.UtcNow));

    public void Reset() => Save(new InitializationStateDocument(1, false, null));

    private void Save(InitializationStateDocument state)
    {
        var directory = Path.GetDirectoryName(statePath) ??
                        throw new InvalidOperationException("Initialization state path has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(statePath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(state, JsonOptions));
            File.Move(temporaryPath, statePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private sealed record InitializationStateDocument(
        int SchemaVersion,
        bool Completed,
        DateTimeOffset? CompletedAt);
}
