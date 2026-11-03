using LazyForza.Domain;

namespace LazyForza.Analysis;

public static class LapDeltaAnalysis
{
    public static LapSummary? SelectReference(TrackTemplate track, int sectorCount,
        IEnumerable<LapSummary> laps, VehicleProfileFingerprint vehicle, LapDeltaComparison comparison)
    {
        var revision = LapTrackRevision.Create(track);
        return laps.Where(lap => lap.IsValid && double.IsFinite(lap.TotalSeconds) && lap.TotalSeconds > 0 &&
                lap.TrackId == track.Id && lap.Direction == track.Direction &&
                (lap.TrackRevision is null || lap.TrackRevision == revision) &&
                lap.SectorSchemaVersion == TrackAlgorithms.SectorSchemaVersion &&
                lap.Segments.Count == sectorCount &&
                lap.Segments.Select(segment => segment.Index).Order().SequenceEqual(Enumerable.Range(0, sectorCount)) &&
                lap.Segments.All(segment => segment.IsValid && double.IsFinite(segment.TimeSeconds) && segment.TimeSeconds > 0) &&
                comparison switch
                {
                    LapDeltaComparison.TrackBest => true,
                    LapDeltaComparison.PerformanceIndexBest => vehicle.PerformanceIndex > 0 && lap.Vehicle.PerformanceIndex == vehicle.PerformanceIndex,
                    LapDeltaComparison.CarBest => vehicle.CarOrdinal > 0 && lap.Vehicle.CarOrdinal == vehicle.CarOrdinal,
                    _ => vehicle.CarClass >= 0 && lap.Vehicle.CarClass == vehicle.CarClass
                })
            .OrderBy(lap => lap.TotalSeconds).ThenBy(lap => lap.StartedAt).ThenBy(lap => lap.Id).FirstOrDefault();
    }
}

/// <summary>A distance/time curve from one complete lap. Missing intervals are never bridged.</summary>
public sealed class LapDeltaCurve
{
    private readonly LapSample[] samples;
    private LapDeltaCurve(LapSample[] samples) => this.samples = samples;

    public static LapDeltaCurve? Create(LapRecord lap, double length)
    {
        if (!lap.IsValid || lap.Samples.Count < 2 || !double.IsFinite(length) || length <= 0 ||
            !double.IsFinite(lap.TotalSeconds) || lap.TotalSeconds <= 0) return null;
        var points = new List<LapSample>(lap.Samples.Count);
        foreach (var sample in lap.Samples)
        {
            if (!double.IsFinite(sample.S) || !double.IsFinite(sample.ElapsedSeconds) ||
                sample.S < 0 || sample.S > length + 1 || sample.ElapsedSeconds < 0 || sample.ElapsedSeconds > lap.TotalSeconds + .1)
                return null;
            if (points.Count > 0)
            {
                var previous = points[^1];
                if (sample.ElapsedSeconds < previous.ElapsedSeconds || sample.S < previous.S - .5) return null;
                // Small projection jitter must not make the lookup curve run backwards.
                if (sample.S < previous.S) continue;
            }
            points.Add(sample);
        }
        if (points.Count < 2) return null;
        return new(points.ToArray());
    }

    public double? ElapsedAt(double progress)
    {
        if (!double.IsFinite(progress) || progress < samples[0].S || progress > samples[^1].S) return null;
        var low = 0; var high = samples.Length - 1;
        while (low < high)
        {
            var middle = (low + high) / 2;
            if (samples[middle].S < progress) low = middle + 1; else high = middle;
        }
        var right = samples[low];
        if (Math.Abs(right.S - progress) < .000001) return right.ElapsedSeconds;
        if (low == 0) return null;
        var left = samples[low - 1];
        var seconds = right.ElapsedSeconds - left.ElapsedSeconds;
        var distance = right.S - left.S;
        var speed = Math.Max(left.SpeedMps, right.SpeedMps);
        if (!double.IsFinite(speed) || seconds <= 0 || seconds > .75 || distance <= 0 ||
            distance > Math.Max(25, Math.Max(0, speed) * seconds * 2 + 5)) return null;
        return left.ElapsedSeconds + seconds * (progress - left.S) / distance;
    }
}
