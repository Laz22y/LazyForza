using LazyForza.Analysis;
using LazyForza.Domain;

namespace LazyForza.Modules.LapAnalysis;

public sealed partial class LapAnalysisModule
{
    private LapDeltaOptions deltaOptions = new();
    private LapDeltaComparison activeDeltaComparison = LapDeltaComparison.ClassBest;
    private LapSummary? deltaReference;
    private Guid? deltaTrackId;
    private VehicleProfileFingerprint? deltaVehicle;
    private bool deltaReferenceSelected;
    private Guid? deltaCurveId;
    private LapDeltaCurve? deltaCurve;
    private double? deltaPreviousTime, deltaPreviousProgress;
    private bool deltaDiscontinuous;

    public LapDeltaOptions DeltaOptions => Volatile.Read(ref deltaOptions);
    public bool DeltaComparisonPending => CurrentCompetitionSnapshot is { } current &&
        current.ActiveDeltaComparison != DeltaOptions.Comparison && competitionActive && lapArmed;

    public void SetDeltaOptions(LapDeltaOptions options)
    {
        if (!Enum.IsDefined(options.Comparison)) throw new ArgumentOutOfRangeException(nameof(options));
        store.SetAppSettings(new Dictionary<string, string>
        {
            [LapDeltaOptions.ContinuousSetting] = options.Continuous.ToString(),
            [LapDeltaOptions.ComparisonSetting] = options.Comparison.ToString()
        });
        Volatile.Write(ref deltaOptions, options);
        // Display-only changes are immediate, including while telemetry is paused.
        var current = Volatile.Read(ref snapshot);
        if (current is not null)
            Interlocked.CompareExchange(ref snapshot, current with { ContinuousDeltaEnabled = options.Continuous }, current);
    }

    private void LoadDeltaOptions()
    {
        var continuous = bool.TryParse(store.GetAppSetting(LapDeltaOptions.ContinuousSetting), out var enabled) && enabled;
        var comparison = Enum.TryParse<LapDeltaComparison>(store.GetAppSetting(LapDeltaOptions.ComparisonSetting), out var saved) &&
            Enum.IsDefined(saved) ? saved : LapDeltaComparison.ClassBest;
        deltaOptions = new(continuous, comparison);
        activeDeltaComparison = comparison;
    }

    private void BeginDeltaLap()
    {
        activeDeltaComparison = DeltaOptions.Comparison;
        deltaReference = null; deltaVehicle = null; deltaTrackId = null; deltaReferenceSelected = false;
        deltaPreviousTime = null; deltaPreviousProgress = null; deltaDiscontinuous = false;
        if (lastCompetitionFrame is { } frame) EnsureDeltaReference(frame);
    }

    private LapSummary? EnsureDeltaReference(TelemetryFrame frame)
    {
        if (track is null) return null;
        if (deltaReferenceSelected && deltaTrackId == track.Id) return deltaReference;
        activeDeltaComparison = DeltaOptions.Comparison;
        deltaVehicle = VehicleProfileFingerprint.FromFrame(frame);
        deltaTrackId = track.Id;
        lock (lapGate)
            deltaReference = LapDeltaAnalysis.SelectReference(track, sectors.Count, visibleLaps, deltaVehicle, activeDeltaComparison);
        deltaReferenceSelected = true;
        return deltaReference;
    }

    private double? ContinuousDelta(TelemetryFrame frame, bool validProjection)
    {
        var reference = EnsureDeltaReference(frame);
        var elapsed = (double)frame.Raw.CurrentLap;
        if (deltaPreviousTime is { } previousTime && deltaPreviousProgress is { } previousProgress)
        {
            var dt = elapsed - previousTime;
            var ds = lastS - previousProgress;
            if (!double.IsFinite(elapsed) || dt < -.2 || dt > 1.5 || ds < -5 ||
                ds > Math.Max(100, Math.Max(0, dt) * Math.Max(0, frame.Raw.Speed) * 3 + 20))
                deltaDiscontinuous = true;
        }
        deltaPreviousTime = elapsed; deltaPreviousProgress = lastS;
        if (!DeltaOptions.Continuous || reference is null || track is null || !lapArmed ||
            !validProjection || currentLapInvalidated || deltaDiscontinuous ||
            deltaVehicle is not { } vehicle || frame.Raw.CarOrdinal != vehicle.CarOrdinal ||
            frame.Raw.CarPerformanceIndex != vehicle.PerformanceIndex ||
            PerformanceClassCatalog.Resolve(frame.Raw.CarClass, frame.Raw.CarPerformanceIndex) != vehicle.CarClass)
            return null;
        if (deltaCurveId != reference.Id)
        {
            deltaCurveId = reference.Id;
            deltaCurve = null;
            try
            {
                deltaCurve = LoadLapDetails([reference.Id]).FirstOrDefault() is { } lap ? LapDeltaCurve.Create(lap, track.LengthMeters) : null;
            }
            catch (Exception exception) when (exception is InvalidOperationException or IOException or FormatException or OverflowException or System.Text.Json.JsonException)
            {
                // An optional comparison must not interrupt recording or retry a broken reference on every frame.
                LogIfInitialized($"Live delta reference {reference.Id} could not be loaded: {exception.Message}");
            }
        }
        var referenceTime = deltaCurve?.ElapsedAt(lastS);
        return referenceTime is { } time && double.IsFinite(elapsed) ? elapsed - time : null;
    }
}
