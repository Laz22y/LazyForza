using LazyForza.Domain;

namespace LazyForza.Analysis;

public sealed record RecordedRouteResult(TrackTemplate Track, IReadOnlyList<SectorDefinition> Sectors, LapRecord Lap);

public static class RecordedRouteAnalysis
{
    public const double MinimumDurationSeconds = 10;
    public const double MinimumTravelMeters = 100;

    public static RecordedRouteResult? Build(RecordedLapCapture capture, bool finishConfirmed)
    {
        var samples = capture.Samples.Where(sample => double.IsFinite(sample.ElapsedSeconds) &&
            double.IsFinite(sample.X) && double.IsFinite(sample.Y) && double.IsFinite(sample.Z)).ToArray();
        if (samples.Length < 40 || samples[^1].ElapsedSeconds - samples[0].ElapsedSeconds < MinimumDurationSeconds) return null;
        // Use a continuous spatial span for the reference. Retain all raw samples below,
        // including gaps, without resampling a teleport into an enormous artificial route.
        var spans = new List<List<TrackPoint>> { new() };
        var spanLength = 0d;
        foreach (var sample in samples)
        {
            var point = new TrackPoint(sample.X, sample.Y, sample.Z, 0, 0, 0);
            var span = spans[^1];
            var distance = span.Count > 0 ? Math.Sqrt(point.DistanceSquaredTo(span[^1])) : 0;
            if (distance > 1000 || spanLength + distance > 200_000)
            { spans.Add(span = []); spanLength = 0; }
            else spanLength += distance;
            span.Add(point);
        }
        var points = spans.MaxBy(span => span.Count)!.ToArray();
        if (points.Length < 4) return null;
        var layout = TrackAlgorithms.InferLayout(points, 30);
        if (points.Zip(points.Skip(1), (a, b) => Math.Sqrt(a.DistanceSquaredTo(b))).Sum() < MinimumTravelMeters) return null;
        var route = capture.ReferenceTrack ?? (TrackAlgorithms.BuildTemplate($"未命名路线 {capture.StartedAt.ToLocalTime():MM-dd HH:mm}", points,
            layoutKind: layout) with
        {
            Id = capture.Id, Source = TelemetryDataPartition.TrackSource(capture.Source), IsRecordOnly = true,
            CreatedAt = capture.StartedAt, UpdatedAt = capture.StartedAt
        });
        if (route.LengthMeters < 10) return null;
        var startConfirmed = capture.StartConfirmed || capture.StartedAtEventStart;
        var complete = finishConfirmed && startConfirmed && capture.Fault is null && spans.Count == 1 &&
            samples.Length >= 40 && samples[^1].ElapsedSeconds >= 10 && route.LengthMeters >= 100;
        var reason = capture.Fault ?? (spans.Count > 1 ? "recording-position-jump" : !startConfirmed ? "recording-missing-start" :
            !finishConfirmed ? "recording-missing-finish" : !complete ? "recording-insufficient-data" : null);
        var startTime = complete ? 0 : samples[0].ElapsedSeconds;
        var projected = new List<LapSample>(samples.Length);
        var segment = 0;
        var lastS = 0d;
        foreach (var sample in samples)
        {
            var projection = TrackAlgorithms.ProjectConstrained(route.Points, sample.X, sample.Y, sample.Z,
                segment, searchAhead: 64);
            if (!projection.IsValid || projection.DistanceMeters > route.MatchingToleranceMeters || projection.ElevationErrorMeters > 10)
            {
                complete = false;
                reason ??= "recording-route-gap";
                // Retain the telemetry even when it cannot contribute to route timing.
                projected.Add(sample with { S = lastS, ElapsedSeconds = Math.Max(0, sample.ElapsedSeconds - startTime) });
                continue;
            }
            segment = projection.SegmentIndex;
            lastS = Math.Max(lastS, projection.S);
            projected.Add(sample with { S = lastS, ElapsedSeconds = Math.Max(0, sample.ElapsedSeconds - startTime) });
        }
        if (projected.Count < 2 || projected[^1].ElapsedSeconds <= 0) return null;
        if (complete && (projected[0].S > 20 || projected[^1].S < route.LengthMeters - 20))
        { complete = false; reason ??= "recording-route-gap"; }
        if (complete) projected[^1] = projected[^1] with { S = route.LengthMeters };
        var sectors = capture.ReferenceSectors ?? TrackAlgorithms.CreateSectors(route);
        var segments = sectors.Where(sector => sector.StartS >= projected[0].S && sector.EndS <= projected[^1].S)
            .Select(sector => new LapSegment(sector.Index,
                TimeAt(sector.EndS) - TimeAt(sector.StartS), complete))
            .Where(segment => segment.TimeSeconds > 0).ToArray();
        if (complete && segments.Length != sectors.Count)
        { complete = false; reason ??= "recording-route-gap"; }
        if (!complete) segments = segments.Select(segment => segment with { IsValid = false }).ToArray();
        var lap = new LapRecord(capture.Id, route.Id, route.Direction, TrackAlgorithms.SectorSchemaVersion,
            capture.SessionId, capture.Vehicle, capture.StartedAt, projected[^1].ElapsedSeconds,
            complete, complete ? null : reason, segments, projected, capture.PlayerCode)
        {
            TrackRevision = LapTrackRevision.Create(route),
            SessionInfo = new(capture.SessionId, LapSessionKind.GameRace)
        };
        return new(route, sectors, lap);

        double TimeAt(double s)
        {
            if (s <= projected[0].S) return projected[0].ElapsedSeconds;
            for (var index = 1; index < projected.Count; index++)
            {
                var next = projected[index];
                if (next.S < s) continue;
                var previous = projected[index - 1];
                var ratio = next.S > previous.S ? (s - previous.S) / (next.S - previous.S) : 0;
                return previous.ElapsedSeconds + (next.ElapsedSeconds - previous.ElapsedSeconds) * ratio;
            }
            return projected[^1].ElapsedSeconds;
        }
    }
}
