using LazyForza.Domain;

namespace LazyForza.Modules.EstateRace;

internal sealed class EstateRaceGripEstimator
{
    private readonly Queue<double> samples = new();
    private readonly List<double> baselineLaps = [];
    private int lastCompletedLaps = -1;
    private DateTimeOffset? alertUntil;

    public RaceGripCondition Current { get; private set; }

    public void Observe(TelemetryFrame frame, int completedLaps, bool valid)
    {
        if (lastCompletedLaps < 0)
            lastCompletedLaps = completedLaps;
        else if (completedLaps != lastCompletedLaps)
        {
            CompleteLap(frame.ArrivalTime);
            samples.Clear();
            lastCompletedLaps = completedLaps;
        }
        if (alertUntil is DateTimeOffset expiry && frame.ArrivalTime >= expiry)
        {
            Current = RaceGripCondition.Unknown;
            alertUntil = null;
        }
        if (!valid || frame.Normalized.SpeedKph < 35 || frame.Normalized.HandBrakeRatio > 0.1)
            return;

        var combined = frame.Raw.TireCombinedSlip.MaxAbsolute;
        var ratio = frame.Raw.TireSlipRatio.MaxAbsolute;
        if (!float.IsFinite(combined) || !float.IsFinite(ratio)) return;
        var evidence = Math.Max(
            Math.Clamp((combined - 0.06) / 0.90, 0, 1),
            Math.Clamp((ratio - 0.10) / 1.15, 0, 1) * 0.72);
        samples.Enqueue(evidence);
        while (samples.Count > 240) samples.Dequeue();
    }

    private void CompleteLap(DateTimeOffset now)
    {
        if (samples.Count < 30) return;
        var ordered = samples.OrderBy(value => value).ToArray();
        var score = ordered[(int)Math.Clamp(Math.Round((ordered.Length - 1) * 0.85), 0, ordered.Length - 1)];
        if (baselineLaps.Count < 3)
        {
            baselineLaps.Add(score);
            Current = RaceGripCondition.Unknown;
            return;
        }

        var baseline = baselineLaps.Average();
        var decline = score - baseline;
        Current = decline switch
        {
            < 0.08 => RaceGripCondition.Unknown,
            < 0.16 => RaceGripCondition.SlightlyReduced,
            < 0.28 => RaceGripCondition.ModeratelyReduced,
            < 0.42 => RaceGripCondition.SeverelyReduced,
            _ => RaceGripCondition.AtLimit
        };
        alertUntil = Current == RaceGripCondition.Unknown ? null : now.AddSeconds(5);
    }

    public void Reset()
    {
        samples.Clear();
        baselineLaps.Clear();
        lastCompletedLaps = -1;
        alertUntil = null;
        Current = RaceGripCondition.Unknown;
    }
}
