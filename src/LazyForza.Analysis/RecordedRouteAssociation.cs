using LazyForza.Domain;

namespace LazyForza.Analysis;

public sealed record RecordedRouteMatch(double Coverage, double MeanDistanceMeters, double LengthDifference,
    double StartDistanceMeters, double FinishDistanceMeters, bool SameKind)
{
    public bool IsPartial { get; init; }
    public bool CanAutoMerge => !IsPartial && SameKind && Coverage >= .97 && MeanDistanceMeters <= 15 && LengthDifference <= .06 &&
        StartDistanceMeters <= 40 && FinishDistanceMeters <= 40;
    public bool IsSevereMismatch => !SameKind || Coverage < .8 || MeanDistanceMeters > 50 || !IsPartial && LengthDifference > .2 ||
        StartDistanceMeters > 120 || FinishDistanceMeters > 120;
}

/// <summary>Compare whole recorded routes and align retained samples without changing their telemetry or elapsed times.</summary>
public static class RecordedRouteAssociation
{
    public static RecordedRouteMatch Compare(TrackTemplate source, TrackTemplate target, bool partial = false)
    {
        if (source.Points.Count < 2 || target.Points.Count < 2 || source.LengthMeters <= 0 || target.LengthMeters <= 0)
            return new(0, double.PositiveInfinity, 1, double.PositiveInfinity, double.PositiveInfinity, false);
        if (partial) return CompareFragment(source, target);
        var sameKind = source.Source == target.Source && source.LayoutKind == target.LayoutKind &&
            source.Direction == target.Direction && source.TimingKind == TrackTimingKind.GameEvent && target.TimingKind == TrackTimingKind.GameEvent;
        var forward = Measure(source, target);
        var reverse = Measure(target, source);
        return new(Math.Min(forward.Coverage, reverse.Coverage), Math.Max(forward.Mean, reverse.Mean),
            Math.Abs(source.LengthMeters - target.LengthMeters) / Math.Max(source.LengthMeters, target.LengthMeters),
            Math.Sqrt(source.Points[0].DistanceSquaredTo(target.Points[0])),
            Math.Sqrt(source.Points[^1].DistanceSquaredTo(target.Points[^1])), sameKind);
    }

    private static RecordedRouteMatch CompareFragment(TrackTemplate source, TrackTemplate target)
    {
        var start = TrackAlgorithms.ProjectRange(target.Points, source.Points[0].X, source.Points[0].Y, source.Points[0].Z, 0, target.Points.Count - 2);
        var count = 0; var matched = 0; var distance = 0d; var finishDistance = double.PositiveInfinity;
        var stride = Math.Max(1, source.Points.Count / 128);
        for (var i = 0; i < source.Points.Count; i += stride)
        {
            var point = source.Points[i];
            var expected = (int)((start.S + point.S) / target.LengthMeters * (target.Points.Count - 1));
            var window = Math.Max(16, (int)(source.LengthMeters / target.LengthMeters * target.Points.Count / 12));
            var projected = TrackAlgorithms.ProjectRange(target.Points, point.X, point.Y, point.Z,
                Math.Max(0, expected - window), Math.Min(target.Points.Count - 2, expected + window));
            finishDistance = projected.DistanceMeters; distance += finishDistance; count++;
            if (finishDistance <= 35 && projected.ElevationErrorMeters <= 10) matched++;
        }
        return new((double)matched / count, distance / count,
            Math.Abs(source.LengthMeters - target.LengthMeters) / Math.Max(source.LengthMeters, target.LengthMeters),
            start.DistanceMeters, finishDistance, source.Source == target.Source && source.Direction == target.Direction &&
            source.TimingKind == TrackTimingKind.GameEvent && target.TimingKind == TrackTimingKind.GameEvent &&
            source.LengthMeters <= target.LengthMeters * 1.1) { IsPartial = true };
    }

    private static (double Coverage, double Mean) Measure(TrackTemplate source, TrackTemplate target)
    {
        var count = 0; var matched = 0; var total = 0d;
        var stride = Math.Max(1, source.Points.Count / 128);
        for (var i = 0; i < source.Points.Count; i += stride)
        {
            var point = source.Points[i];
            // A progress window distinguishes opposite directions and branches at crossings.
            var expected = (int)(point.S / source.LengthMeters * (target.Points.Count - 1));
            var window = Math.Max(16, target.Points.Count / 12);
            var projection = TrackAlgorithms.ProjectRange(target.Points, point.X, point.Y, point.Z,
                Math.Max(0, expected - window), Math.Min(target.Points.Count - 2, expected + window));
            var distance = projection.IsValid ? projection.DistanceMeters : 100_000;
            total += distance; count++;
            if (distance <= 35 && projection.ElevationErrorMeters <= 10) matched++;
        }
        return ((double)matched / count, total / count);
    }

    public static LapRecord Reproject(LapRecord lap, TrackTemplate source, TrackTemplate target,
        IReadOnlyList<SectorDefinition> sectors, RecordedRouteMatch match)
    {
        if (source.Source != target.Source || target.TimingKind != TrackTimingKind.GameEvent ||
            sectors.Count == 0 || sectors.Any(sector => sector.TrackId != target.Id || sector.SectorSchemaVersion != TrackAlgorithms.SectorSchemaVersion))
            throw new ArgumentException("Incompatible route destination.");
        if (match.IsSevereMismatch)
            return lap with
            {
                TrackId = target.Id, Direction = target.Direction, IsValid = false, InvalidReason = "recording-route-mismatch",
                // A forced assignment is organization only. Keep the original distance axis and revision for analysis.
                Segments = lap.Segments.Select(segment => segment with { IsValid = false }).ToArray(),
                Annotation = lap.Annotation with { IsReference = false }
            };
        var samples = new List<LapSample>(lap.Samples.Count);
        var index = lap.Samples.Count == 0 ? 0 : Math.Clamp((int)(lap.Samples[0].S / Math.Max(1, source.LengthMeters) *
            (target.Points.Count - 1)), 0, target.Points.Count - 2);
        if (match.IsPartial && lap.Samples.Count > 0)
        {
            var first = lap.Samples[0];
            index = TrackAlgorithms.ProjectRange(target.Points, first.X, first.Y, first.Z, 0, target.Points.Count - 2).SegmentIndex;
        }
        var last = 0d; var aligned = 0;
        foreach (var sample in lap.Samples)
        {
            var projection = TrackAlgorithms.ProjectConstrained(target.Points, sample.X, sample.Y, sample.Z, index, searchAhead: 64);
            if (projection.IsValid && projection.DistanceMeters <= Math.Max(35, target.MatchingToleranceMeters * 2) && projection.ElevationErrorMeters <= 10)
            {
                index = projection.SegmentIndex;
                last = Math.Max(last, projection.S);
                aligned++;
            }
            samples.Add(sample with { S = last, DeltaSeconds = 0 });
        }
        var valid = lap.IsValid && !match.IsSevereMismatch && samples.Count > 1 &&
            aligned >= samples.Count * .98 && samples[0].S <= 60 && samples[^1].S >= target.LengthMeters - 60;
        if (valid)
        {
            samples[0] = samples[0] with { S = 0 };
            samples[^1] = samples[^1] with { S = target.LengthMeters };
        }
        var segments = samples.Count < 2 ? [] : sectors
            .Where(sector => sector.StartS >= samples[0].S && sector.EndS <= samples[^1].S)
            .Select(sector => new LapSegment(sector.Index, TimeAt(sector.EndS) - TimeAt(sector.StartS), valid))
            .Where(segment => segment.TimeSeconds > 0).ToArray();
        if (valid && segments.Length != sectors.Count) valid = false;
        if (!valid) segments = segments.Select(segment => segment with { IsValid = false }).ToArray();
        return lap with
        {
            TrackId = target.Id, Direction = target.Direction, TrackRevision = LapTrackRevision.Create(target),
            SectorSchemaVersion = TrackAlgorithms.SectorSchemaVersion, Samples = samples, Segments = segments,
            IsValid = valid, InvalidReason = valid ? null : match.IsSevereMismatch ? "recording-route-mismatch" : lap.InvalidReason ?? "recording-route-gap",
            Annotation = lap.Annotation with { IsReference = valid && lap.Annotation.IsReference }
        };

        double TimeAt(double s)
        {
            if (s <= samples[0].S) return samples[0].ElapsedSeconds;
            for (var i = 1; i < samples.Count; i++)
            {
                if (samples[i].S < s) continue;
                var before = samples[i - 1]; var after = samples[i];
                var ratio = after.S > before.S ? (s - before.S) / (after.S - before.S) : 0;
                return before.ElapsedSeconds + (after.ElapsedSeconds - before.ElapsedSeconds) * ratio;
            }
            return samples[^1].ElapsedSeconds;
        }
    }
}
