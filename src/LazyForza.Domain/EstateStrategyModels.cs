namespace LazyForza.Domain;

public enum EstateStrategySampleKind
{
    Stint,
    PitStop,
    FlyingLap
}

public enum EstateStrategySampleSource
{
    PracticeLongRun,
    PracticePitSimulation,
    PracticeQualifyingSimulation,
    Race
}

public enum EstateStrategyMatchTier
{
    SameCarAndTune = 0,
    SameCar = 1,
    SamePerformanceIndex = 2,
    NearbyPerformanceIndex = 3,
    SamePerformanceClass = 4,
    NearbyPerformanceIndexAcrossClass = 5
}

public sealed record EstateStrategyTrackIdentity(
    string TrackId,
    string TrackRevision,
    string TrackFingerprint)
{
    public string Key => string.Join('|',
        TrackId.Trim(),
        TrackRevision.Trim(),
        TrackFingerprint.Trim().ToUpperInvariant());
}

/// <summary>
/// A compact strategy observation. A whole clean stint is reduced to one row;
/// no frame-by-frame telemetry is retained.
/// </summary>
public sealed record EstateStrategySample(
    Guid Id,
    EstateStrategyTrackIdentity Track,
    EstateStrategySampleKind Kind,
    EstateStrategySampleSource Source,
    DateTimeOffset CapturedAt,
    VehicleProfileFingerprint Vehicle,
    int LapCount,
    double? FreshLapSeconds,
    double? RepresentativeLapSeconds,
    double? DegradationPerLapSeconds,
    double? PaceSpreadSeconds,
    double? PitLaneElapsedSeconds);

public sealed record EstateStrategyMatchedSample(
    EstateStrategySample Sample,
    EstateStrategyMatchTier Tier,
    double Weight);

public static class EstateStrategySampleMatcher
{
    public const int NearbyPerformanceIndexRange = 25;
    public const int CrossClassPerformanceIndexRange = 40;

    public static IReadOnlyList<EstateStrategyMatchedSample> Select(
        IEnumerable<EstateStrategySample> source,
        VehicleProfileFingerprint vehicle,
        EstateStrategySampleKind kind,
        int minimumEvidence)
    {
        var candidates = source
            .Where(sample => sample.Kind == kind)
            .Select(sample => (Sample: sample, Tier: MatchTier(sample.Vehicle, vehicle)))
            .Where(candidate => candidate.Tier is not null)
            .OrderBy(candidate => candidate.Tier)
            .ThenByDescending(candidate => candidate.Sample.CapturedAt)
            .ToArray();
        if (candidates.Length == 0) return [];

        var selected = new List<EstateStrategyMatchedSample>();
        var evidence = 0;
        foreach (var tierGroup in candidates.GroupBy(candidate => candidate.Tier!.Value))
        {
            foreach (var candidate in tierGroup.Take(48))
            {
                var tier = candidate.Tier!.Value;
                selected.Add(new EstateStrategyMatchedSample(
                    candidate.Sample,
                    tier,
                    TierWeight(tier)));
                evidence += kind == EstateStrategySampleKind.Stint
                    ? Math.Max(1, candidate.Sample.LapCount)
                    : 1;
            }

            if (evidence >= Math.Max(1, minimumEvidence)) break;
        }

        return selected;
    }

    public static EstateStrategyMatchTier? MatchTier(
        VehicleProfileFingerprint sample,
        VehicleProfileFingerprint current)
    {
        if (sample.CarOrdinal == current.CarOrdinal &&
            VehicleProfileIdentity.IsResolved(sample) &&
            VehicleProfileIdentity.IsResolved(current) &&
            VehicleTuneCompatibility.AreCompatible(sample, current))
            return EstateStrategyMatchTier.SameCarAndTune;
        if (sample.CarOrdinal == current.CarOrdinal)
            return EstateStrategyMatchTier.SameCar;
        if (sample.PerformanceIndex > 0 &&
            sample.PerformanceIndex == current.PerformanceIndex)
            return EstateStrategyMatchTier.SamePerformanceIndex;

        var difference = sample.PerformanceIndex > 0 && current.PerformanceIndex > 0
            ? Math.Abs(sample.PerformanceIndex - current.PerformanceIndex)
            : int.MaxValue;
        if (sample.CarClass == current.CarClass && difference <= NearbyPerformanceIndexRange)
            return EstateStrategyMatchTier.NearbyPerformanceIndex;
        if (sample.CarClass == current.CarClass)
            return EstateStrategyMatchTier.SamePerformanceClass;
        if (difference <= CrossClassPerformanceIndexRange)
            return EstateStrategyMatchTier.NearbyPerformanceIndexAcrossClass;
        return null;
    }

    public static string TierLabel(EstateStrategyMatchTier tier) => tier switch
    {
        EstateStrategyMatchTier.SameCarAndTune => "同车同调校",
        EstateStrategyMatchTier.SameCar => "同车型",
        EstateStrategyMatchTier.SamePerformanceIndex => "同性能分",
        EstateStrategyMatchTier.NearbyPerformanceIndex => "性能分接近",
        EstateStrategyMatchTier.SamePerformanceClass => "同性能等级",
        EstateStrategyMatchTier.NearbyPerformanceIndexAcrossClass => "跨等级但性能分接近",
        _ => "历史样本"
    };

    private static double TierWeight(EstateStrategyMatchTier tier) => tier switch
    {
        EstateStrategyMatchTier.SameCarAndTune => 1.00,
        EstateStrategyMatchTier.SameCar => 0.84,
        EstateStrategyMatchTier.SamePerformanceIndex => 0.70,
        EstateStrategyMatchTier.NearbyPerformanceIndex => 0.54,
        EstateStrategyMatchTier.SamePerformanceClass => 0.38,
        EstateStrategyMatchTier.NearbyPerformanceIndexAcrossClass => 0.22,
        _ => 0.10
    };
}
