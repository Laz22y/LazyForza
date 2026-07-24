using System.Collections.Concurrent;
using LazyForza.Analysis;
using LazyForza.Domain;
using LazyForza.Modules.Abstractions;

namespace LazyForza.Modules.Dashboard;

public sealed class DashboardModule : LazyForzaModuleBase, IHudContribution
{
    public const string ModuleId = "dashboard";
    private readonly ShiftLearner learner;
    private readonly GearDisplayStabilizer gearDisplay = new();
    private ITelemetrySubscription? subscription;
    private CancellationTokenSource? runCancellation;
    private Task? runTask;
    private DashboardHudState? snapshot;
    private long framesObserved;
    private string? activeVehicleProfileId;
    private bool shiftRecommendationsEnabled = true;
    private readonly ConcurrentDictionary<string, byte> profilesPendingForget = new(StringComparer.Ordinal);

    public DashboardModule(ShiftLearner? learner = null)
        : base(new ModuleDescriptor(
            ModuleId,
            "仪表盘",
            new Version(1, 0, 0),
            "显示车辆状态，并学习当前车辆的换挡区间。",
            [],
            "vehicle-shift",
            "dashboard-settings",
            true))
    {
        this.learner = learner ?? new ShiftLearner();
    }

    public string Id => "hud.dashboard";
    public HudContributionKind Kind => HudContributionKind.Dashboard;
    public int ZIndex => 10;
    public object? Snapshot
    {
        get
        {
            var current = Volatile.Read(ref snapshot);
            return current is null ? null : current with { IsStale = DateTimeOffset.UtcNow - current.UpdatedAt > TimeSpan.FromSeconds(0.8) };
        }
    }

    public ShiftLearningSnapshot Learning => learner.Snapshot;
    public string? ActiveVehicleProfileId => Volatile.Read(ref activeVehicleProfileId);

    public void SetShiftRecommendationsEnabled(string vehicleProfileId, bool enabled)
    {
        if (string.Equals(ActiveVehicleProfileId, vehicleProfileId, StringComparison.Ordinal))
            Volatile.Write(ref shiftRecommendationsEnabled, enabled);
    }

    public void ForgetVehicleProfile(string vehicleProfileId)
    {
        profilesPendingForget[vehicleProfileId] = 0;
        if (string.Equals(ActiveVehicleProfileId, vehicleProfileId, StringComparison.Ordinal))
            Volatile.Write(ref shiftRecommendationsEnabled, true);
    }

    protected override async ValueTask OnStartAsync(CancellationToken cancellationToken)
    {
        gearDisplay.Reset();
        framesObserved = 0;
        Volatile.Write(ref activeVehicleProfileId, null);
        Volatile.Write(ref shiftRecommendationsEnabled, true);
        profilesPendingForget.Clear();
        runCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        subscription = await Context.Telemetry.SubscribeAsync(ModuleId, runCancellation.Token).ConfigureAwait(false);
        await Context.Hud.AttachAsync(this, cancellationToken).ConfigureAwait(false);
        runTask = Task.Run(() => ConsumeAsync(subscription.Frames, runCancellation.Token), CancellationToken.None);
    }

    protected override async ValueTask OnStopAsync(CancellationToken cancellationToken)
    {
        runCancellation?.Cancel();
        if (subscription is not null) await subscription.DisposeAsync().ConfigureAwait(false);
        if (runTask is not null)
        {
            try { await runTask.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }

        await Context.Hud.DetachAsync(Id, cancellationToken).ConfigureAwait(false);
        subscription = null;
        runTask = null;
        runCancellation?.Dispose();
        runCancellation = null;
        gearDisplay.Reset();
        Volatile.Write(ref activeVehicleProfileId, null);
        Volatile.Write(ref shiftRecommendationsEnabled, true);
        profilesPendingForget.Clear();
        Volatile.Write(ref snapshot, null);
    }

    private async Task ConsumeAsync(System.Threading.Channels.ChannelReader<TelemetryFrame> frames, CancellationToken cancellationToken)
    {
        await foreach (var frame in frames.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            var previousLearning = learner.Snapshot;
            learner.Observe(frame);
            var currentLearning = learner.Snapshot;
            if (previousLearning.Fingerprint is not null &&
                previousLearning.ConfigurationRevision != currentLearning.ConfigurationRevision)
            {
                await Context.AnalysisStore
                    .SaveShiftLearningAsync(previousLearning, cancellationToken)
                    .ConfigureAwait(false);
                Context.Log(
                    $"Vehicle configuration changed from {previousLearning.Fingerprint.CarOrdinal}/" +
                    $"{previousLearning.Fingerprint.PerformanceIndex} to " +
                    $"{currentLearning.Fingerprint?.CarOrdinal}/{currentLearning.Fingerprint?.PerformanceIndex}; " +
                    "saved the previous shift profile and started a new profile.");
            }

            var previousProfileId = VehicleProfileIdentity.TryCreate(previousLearning.Fingerprint);
            var currentProfileId = VehicleProfileIdentity.TryCreate(currentLearning.Fingerprint);
            if (currentProfileId is not null &&
                profilesPendingForget.TryRemove(currentProfileId, out _))
            {
                learner.Reset();
                currentLearning = learner.Snapshot;
                currentProfileId = null;
            }

            if (previousProfileId is null && currentProfileId is not null)
            {
                await Context.AnalysisStore
                    .SaveShiftLearningAsync(currentLearning, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (!string.Equals(ActiveVehicleProfileId, currentProfileId, StringComparison.Ordinal))
            {
                Volatile.Write(ref activeVehicleProfileId, currentProfileId);
                var enabled = currentProfileId is null ||
                              await Context.AnalysisStore
                                  .GetShiftRecommendationsEnabledAsync(currentProfileId, cancellationToken)
                                  .ConfigureAwait(false);
                Volatile.Write(ref shiftRecommendationsEnabled, enabled);
            }

            framesObserved++;
            var isDriving = TelemetryContextClassifier.IsDriving(frame.Raw);
            var resolvedGear = gearDisplay.Resolve(frame.Raw.Gear, frame.ArrivalTime, isDriving);
            var sourceLabel = frame.Source switch
            {
                TelemetrySourceKind.Live => "LIVE",
                TelemetrySourceKind.Replay => "REPLAY",
                _ => "DEMO / REPLAY"
            };
            var hud = new DashboardHudState(
                frame.ArrivalTime,
                frame.Source,
                sourceLabel,
                false,
                isDriving,
                frame.Raw.Gear,
                resolvedGear.ForwardGear,
                resolvedGear.Display,
                resolvedGear.IsHeld,
                (int)Math.Round(frame.Normalized.SpeedKph),
                frame.Raw.CurrentEngineRpm,
                frame.Raw.EngineMaxRpm,
                DashboardDisplayValues.NonNegativeOutput(frame.Normalized.PowerKw),
                DashboardDisplayValues.NonNegativeOutput(frame.Raw.Torque),
                DashboardDisplayValues.TireTemperatureCelsius(frame.Raw.TireTemperature),
                frame.Normalized.GripUi,
                frame.Normalized.BrakeRatio,
                frame.Normalized.AccelRatio,
                PerformanceClassCatalog.Resolve(frame.Raw.CarClass, frame.Raw.CarPerformanceIndex),
                frame.Raw.CarPerformanceIndex,
                currentLearning)
            {
                SpeedMps = frame.Raw.Speed,
                Acceleration = frame.Raw.Acceleration,
                Steering = Math.Clamp(frame.Raw.Steer / 127d, -1, 1),
                Clutch = frame.Normalized.ClutchRatio,
                HandBrake = frame.Normalized.HandBrakeRatio,
                ShiftRecommendationsEnabled = Volatile.Read(ref shiftRecommendationsEnabled)
            };
            Volatile.Write(ref snapshot, hud);
            if (framesObserved % 300 == 0 && hud.ShiftLearning.Fingerprint is not null)
            {
                await Context.AnalysisStore.SaveShiftLearningAsync(hud.ShiftLearning, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
