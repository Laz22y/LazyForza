using System.Threading.Channels;
using LazyForza.Domain;

namespace LazyForza.Modules.Abstractions;

public enum ModuleRuntimeState
{
    Disabled,
    Initialized,
    Starting,
    Running,
    Stopping,
    Faulted
}

public sealed record ModuleDescriptor(
    string Id,
    string DisplayName,
    string Description,
    IReadOnlyList<string> Dependencies,
    string? MainPageKey,
    string? SettingsPageKey,
    bool HasHudContribution,
    bool DefaultEnabled = true);

public sealed record ModuleStatus(
    string Id,
    bool IsEnabled,
    ModuleRuntimeState State,
    string? LastError,
    DateTimeOffset UpdatedAt);

public interface ITelemetrySubscription : IAsyncDisposable
{
    ChannelReader<TelemetryFrame> Frames { get; }
}

public interface ITelemetryFeed : IAsyncDisposable
{
    TelemetryFrame? Latest { get; }
    TelemetryDiagnostics Diagnostics { get; }
    ValueTask<ITelemetrySubscription> SubscribeAsync(string consumerId, CancellationToken cancellationToken);
}

public enum HudContributionKind
{
    Dashboard,
    LapSectors,
    DriftDashboard,
    EstateRace
}

public interface IHudContribution
{
    string Id { get; }
    HudContributionKind Kind { get; }
    int ZIndex { get; }
    object? Snapshot { get; }
}

public interface IHudHost
{
    ValueTask AttachAsync(IHudContribution contribution, CancellationToken cancellationToken);
    ValueTask DetachAsync(string contributionId, CancellationToken cancellationToken);
    ValueTask SetLayoutAsync(OverlayLayout layout, CancellationToken cancellationToken);
}

public interface IModuleSettingsStore
{
    ValueTask<string?> GetAsync(string moduleId, string key, CancellationToken cancellationToken);
    ValueTask SetAsync(string moduleId, string key, string value, CancellationToken cancellationToken);
}

public interface IAnalysisStore
{
    ValueTask<string?> SaveShiftLearningAsync(
        ShiftLearningSnapshot snapshot,
        CancellationToken cancellationToken);
    ValueTask<bool> GetShiftRecommendationsEnabledAsync(string vehicleProfileId, CancellationToken cancellationToken);
}

public interface IModuleContext
{
    ITelemetryFeed Telemetry { get; }
    IHudHost Hud { get; }
    IModuleSettingsStore Settings { get; }
    IAnalysisStore AnalysisStore { get; }
    Action<string> Log { get; }
}

public interface ILazyForzaModule : IAsyncDisposable
{
    ModuleDescriptor Descriptor { get; }
    ModuleStatus Status { get; }
    ValueTask InitializeAsync(IModuleContext context, CancellationToken cancellationToken);
    ValueTask StartAsync(CancellationToken cancellationToken);
    ValueTask StopAsync(CancellationToken cancellationToken);
}
