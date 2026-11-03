using LazyForza.Domain;

namespace LazyForza.Modules.LapAnalysis;

internal sealed class UnmatchedLapCapture
{
    private readonly List<LapSample> samples = [];
    private readonly RecordedLapCapture metadata;
    private TelemetryFrame? previous;
    private string? fault;
    private DateTimeOffset checkpointAt;
    internal Guid Id => metadata.Id;
    internal int Count => samples.Count;
    internal double LastTime => samples.LastOrDefault()?.ElapsedSeconds ?? 0;
    internal bool HasReference => metadata.ReferenceTrack is not null;
    internal float StartingLastLap { get; }

    internal UnmatchedLapCapture(TelemetryFrame frame, Guid sessionId, string? player, bool startConfirmed,
        TrackTemplate? reference = null, IReadOnlyList<SectorDefinition>? sectors = null)
    {
        metadata = new(Guid.NewGuid(), sessionId, frame.Source, frame.ArrivalTime,
            VehicleProfileFingerprint.FromFrame(frame), player, startConfirmed, null, [])
        { StartedAtEventStart = frame.Raw.CurrentRaceTime <= 0.5 && frame.Raw.CurrentLap <= 0.5,
            ReferenceTrack = reference, ReferenceSectors = sectors };
        StartingLastLap = frame.Raw.LastLap;
        checkpointAt = frame.ArrivalTime;
    }

    internal bool Observe(TelemetryFrame frame, double? boundaryTime = null)
    {
        var raw = frame.Raw;
        var elapsed = boundaryTime ?? raw.CurrentLap;
        if (!double.IsFinite(elapsed) || elapsed < 0 || !float.IsFinite(raw.Speed) || !float.IsFinite(raw.CurrentEngineRpm) ||
            !double.IsFinite(frame.Normalized.AccelRatio) || !double.IsFinite(frame.Normalized.BrakeRatio) ||
            !float.IsFinite(raw.Position.X) || !float.IsFinite(raw.Position.Y) || !float.IsFinite(raw.Position.Z))
        { fault ??= "recording-invalid-sample"; return false; }
        if (previous is { } before && samples.Count > 0)
        {
            var delta = elapsed - samples[^1].ElapsedSeconds;
            var dx = (double)raw.Position.X - before.Raw.Position.X;
            var dy = (double)raw.Position.Y - before.Raw.Position.Y;
            var dz = (double)raw.Position.Z - before.Raw.Position.Z;
            var distance = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (delta > 1.5) fault ??= "recording-sample-gap";
            if (distance > Math.Max(100, Math.Max(0, delta) * Math.Max(raw.Speed, before.Raw.Speed) * 3 + 20))
                fault ??= "recording-position-jump";
            if (raw.CarOrdinal != metadata.Vehicle.CarOrdinal) fault ??= "recording-vehicle-changed";
            if (delta < -0.2) fault ??= "recording-rewind";
            if (boundaryTime is not null && delta < 0)
            { fault ??= "recording-invalid-sample"; return false; }
            if (boundaryTime is null && delta < 0.099) return false;
        }
        if (samples.Count >= 60_000) { fault ??= "recording-sample-limit"; return false; }
        var dynamics = float.IsFinite(raw.TireSlipRatio.MaxAbsolute) && float.IsFinite(raw.TireSlipAngle.MaxAbsolute) &&
            float.IsFinite(raw.TireCombinedSlip.MaxAbsolute)
            ? new LapDynamics(Math.Clamp(raw.Steer / 127d, -1, 1), raw.TireSlipRatio, raw.TireSlipAngle, raw.TireCombinedSlip)
            : null;
        samples.Add(new(0, elapsed, raw.Speed, raw.CurrentEngineRpm, raw.Gear,
            frame.Normalized.AccelRatio, frame.Normalized.BrakeRatio, 0,
            raw.Position.X, raw.Position.Y, raw.Position.Z,
            dynamics));
        previous = frame;
        if (frame.ArrivalTime - checkpointAt < TimeSpan.FromSeconds(5)) return false;
        checkpointAt = frame.ArrivalTime;
        return true;
    }

    internal RecordedLapCapture Snapshot(string? reason = null) => metadata with
    { Fault = fault ?? reason, Samples = samples.ToArray() };
}
