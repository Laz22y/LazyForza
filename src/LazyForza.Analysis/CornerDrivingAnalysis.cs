using LazyForza.Domain;

namespace LazyForza.Analysis;

public enum SingleLapAnalysisMode
{
    CompareWithClassFastest,
    AnalyzePersonalBest,
    AnalyzeWithoutReference
}

public sealed record SingleLapAnalysisPlan(
    SingleLapAnalysisMode Mode,
    LapSummary SelectedLap,
    LapSummary? ReferenceLap);

public static class LapComparisonPlanner
{
    public static SingleLapAnalysisPlan Resolve(
        LapSummary selectedLap,
        IEnumerable<LapSummary> availableLaps)
    {
        ArgumentNullException.ThrowIfNull(selectedLap);
        ArgumentNullException.ThrowIfNull(availableLaps);

        var classFastest = availableLaps
            .Where(lap =>
                lap.TrackId == selectedLap.TrackId &&
                lap.Direction == selectedLap.Direction &&
                lap.SectorSchemaVersion == selectedLap.SectorSchemaVersion &&
                lap.Vehicle.CarClass == selectedLap.Vehicle.CarClass &&
                lap.IsValid &&
                double.IsFinite(lap.TotalSeconds) &&
                lap.TotalSeconds > 0)
            .OrderBy(lap => lap.TotalSeconds)
            .ThenBy(lap => lap.StartedAt)
            .ThenBy(lap => lap.Id)
            .FirstOrDefault();

        if (classFastest is null)
            return new SingleLapAnalysisPlan(
                SingleLapAnalysisMode.AnalyzeWithoutReference,
                selectedLap,
                null);

        return classFastest.Id == selectedLap.Id
            ? new SingleLapAnalysisPlan(
                SingleLapAnalysisMode.AnalyzePersonalBest,
                selectedLap,
                null)
            : new SingleLapAnalysisPlan(
                SingleLapAnalysisMode.CompareWithClassFastest,
                selectedLap,
                classFastest);
    }
}

public sealed record CornerWindow(
    int Number,
    double StartS,
    double ApexS,
    double EndS);

public sealed record CornerComparisonMetrics(
    CornerWindow Window,
    double? BrakePointDeltaMeters,
    double SelectedEntrySpeedKph,
    double ReferenceEntrySpeedKph,
    double SelectedMinimumSpeedKph,
    double ReferenceMinimumSpeedKph,
    double SelectedExitSpeedKph,
    double ReferenceExitSpeedKph,
    double? ThrottleRecoveryDeltaSeconds,
    double MeanLineDeviationMeters,
    double TimeLossSeconds,
    int SelectedApexGear,
    int ReferenceApexGear,
    int SelectedGearChanges,
    int ReferenceGearChanges);

public sealed record CornerOptimizationMetrics(
    CornerWindow Window,
    double EntrySpeedKph,
    double MinimumSpeedKph,
    double ExitSpeedKph,
    double? ThrottleRecoverySeconds,
    double CoastingSeconds,
    double BrakeThrottleOverlapSeconds,
    int ApexGear,
    int GearChanges,
    double OpportunityScore);

public static class CornerDrivingAnalyzer
{
    private const double BrakeThreshold = 0.25;
    private const double ThrottleThreshold = 0.70;
    private const double MinimumCornerSpeedDropMps = 3.0;
    private const int MaximumDetectedCorners = 32;

    public static IReadOnlyList<CornerWindow> DetectCorners(IReadOnlyList<LapSample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count < 8) return [];

        var ordered = samples
            .Where(IsUsable)
            .OrderBy(sample => sample.S)
            .ThenBy(sample => sample.ElapsedSeconds)
            .ToArray();
        if (ordered.Length < 8) return [];

        var candidates = new List<WindowCandidate>();
        for (var index = 1; index < ordered.Length - 2; index++)
        {
            if (ordered[index - 1].Brake >= BrakeThreshold ||
                ordered[index].Brake < BrakeThreshold ||
                ordered[index].SpeedMps < 5)
                continue;

            var maximumProgress = ordered[index].S + 280;
            var apexIndex = index;
            for (var scan = index + 1;
                 scan < ordered.Length && ordered[scan].S <= maximumProgress;
                 scan++)
            {
                if (ordered[scan].SpeedMps < ordered[apexIndex].SpeedMps)
                    apexIndex = scan;

                if (scan > apexIndex &&
                    ordered[scan].S - ordered[apexIndex].S >= 35 &&
                    ordered[scan].Accel >= ThrottleThreshold &&
                    ordered[scan].SpeedMps >= ordered[apexIndex].SpeedMps + 2)
                    break;
            }

            var speedDrop = ordered[index].SpeedMps - ordered[apexIndex].SpeedMps;
            if (speedDrop < MinimumCornerSpeedDropMps) continue;

            var exitIndex = FindExitIndex(ordered, apexIndex, Math.Min(maximumProgress + 100, ordered[^1].S));
            AddCandidate(
                candidates,
                new WindowCandidate(
                    Math.Max(ordered[0].S, ordered[index].S - 25),
                    ordered[apexIndex].S,
                    Math.Min(ordered[^1].S, ordered[exitIndex].S + 20),
                    speedDrop));
            index = Math.Max(index, apexIndex - 1);
        }

        var reduced = ReduceByDistance(ordered, 10);
        for (var index = 10; index < reduced.Count - 12; index++)
        {
            var current = reduced[index];
            if (current.SpeedMps > reduced[index - 1].SpeedMps ||
                current.SpeedMps > reduced[index + 1].SpeedMps)
                continue;

            var entry = reduced
                .Skip(index - 10)
                .Take(9)
                .OrderByDescending(sample => sample.SpeedMps)
                .First();
            var exit = reduced
                .Skip(index + 2)
                .Take(11)
                .OrderByDescending(sample => sample.SpeedMps)
                .First();
            var entryDrop = entry.SpeedMps - current.SpeedMps;
            var exitRecovery = exit.SpeedMps - current.SpeedMps;
            if (entryDrop < MinimumCornerSpeedDropMps ||
                exitRecovery < 2.0 ||
                exit.S - entry.S < 35)
                continue;

            AddCandidate(
                candidates,
                new WindowCandidate(entry.S, current.S, exit.S, entryDrop + exitRecovery));
            index += 4;
        }

        return candidates
            .OrderByDescending(candidate => candidate.Significance)
            .Take(MaximumDetectedCorners)
            .OrderBy(candidate => candidate.StartS)
            .Select((candidate, index) => new CornerWindow(
                index + 1,
                candidate.StartS,
                candidate.ApexS,
                candidate.EndS))
            .ToArray();
    }

    public static IReadOnlyList<CornerComparisonMetrics> Compare(
        LapRecord selectedLap,
        LapRecord referenceLap)
    {
        ArgumentNullException.ThrowIfNull(selectedLap);
        ArgumentNullException.ThrowIfNull(referenceLap);

        var windows = DetectCorners(referenceLap.Samples);
        return windows
            .Select(window => CompareWindow(window, selectedLap.Samples, referenceLap.Samples))
            .Where(result => result is not null)
            .Cast<CornerComparisonMetrics>()
            .ToArray();
    }

    public static IReadOnlyList<CornerOptimizationMetrics> AnalyzePersonalBest(LapRecord lap)
    {
        ArgumentNullException.ThrowIfNull(lap);
        var windows = DetectCorners(lap.Samples);
        return windows
            .Select(window => AnalyzeWindow(window, lap.Samples))
            .Where(result => result is not null)
            .Cast<CornerOptimizationMetrics>()
            .ToArray();
    }

    private static CornerComparisonMetrics? CompareWindow(
        CornerWindow window,
        IReadOnlyList<LapSample> selected,
        IReadOnlyList<LapSample> reference)
    {
        var selectedStartTime = ElapsedAtProgress(selected, window.StartS);
        var selectedEndTime = ElapsedAtProgress(selected, window.EndS);
        var referenceStartTime = ElapsedAtProgress(reference, window.StartS);
        var referenceEndTime = ElapsedAtProgress(reference, window.EndS);
        if (selectedStartTime is null ||
            selectedEndTime is null ||
            referenceStartTime is null ||
            referenceEndTime is null)
            return null;

        var selectedBrake = FirstThresholdProgress(
            selected,
            window.StartS,
            window.ApexS,
            sample => sample.Brake >= BrakeThreshold);
        var referenceBrake = FirstThresholdProgress(
            reference,
            window.StartS,
            window.ApexS,
            sample => sample.Brake >= BrakeThreshold);
        var selectedThrottle = FirstThresholdSample(
            selected,
            window.ApexS,
            window.EndS,
            sample => sample.Accel >= ThrottleThreshold);
        var referenceThrottle = FirstThresholdSample(
            reference,
            window.ApexS,
            window.EndS,
            sample => sample.Accel >= ThrottleThreshold);
        var selectedApexTime = ElapsedAtProgress(selected, window.ApexS);
        var referenceApexTime = ElapsedAtProgress(reference, window.ApexS);

        return new CornerComparisonMetrics(
            window,
            selectedBrake is not null && referenceBrake is not null
                ? selectedBrake.Value - referenceBrake.Value
                : null,
            SpeedAtProgress(selected, window.StartS) * 3.6,
            SpeedAtProgress(reference, window.StartS) * 3.6,
            MinimumSpeed(selected, window.StartS, window.EndS) * 3.6,
            MinimumSpeed(reference, window.StartS, window.EndS) * 3.6,
            SpeedAtProgress(selected, window.EndS) * 3.6,
            SpeedAtProgress(reference, window.EndS) * 3.6,
            selectedThrottle is not null &&
            referenceThrottle is not null &&
            selectedApexTime is not null &&
            referenceApexTime is not null
                ? (selectedThrottle.ElapsedSeconds - selectedApexTime.Value) -
                  (referenceThrottle.ElapsedSeconds - referenceApexTime.Value)
                : null,
            MeanLineDeviation(selected, reference, window.StartS, window.EndS),
            (selectedEndTime.Value - selectedStartTime.Value) -
            (referenceEndTime.Value - referenceStartTime.Value),
            GearAtProgress(selected, window.ApexS),
            GearAtProgress(reference, window.ApexS),
            CountGearChanges(selected, window.StartS, window.EndS),
            CountGearChanges(reference, window.StartS, window.EndS));
    }

    private static CornerOptimizationMetrics? AnalyzeWindow(
        CornerWindow window,
        IReadOnlyList<LapSample> samples)
    {
        var apexTime = ElapsedAtProgress(samples, window.ApexS);
        var throttle = FirstThresholdSample(
            samples,
            window.ApexS,
            window.EndS,
            sample => sample.Accel >= ThrottleThreshold);
        if (apexTime is null) return null;

        double? throttleRecovery = throttle is null
            ? null
            : Math.Max(0, throttle.ElapsedSeconds - apexTime.Value);
        var coasting = DurationMatching(
            samples,
            window.StartS,
            window.EndS,
            sample => sample.Accel < 0.10 && sample.Brake < 0.10);
        var overlap = DurationMatching(
            samples,
            window.StartS,
            window.EndS,
            sample => sample.Accel >= 0.20 && sample.Brake >= 0.20);
        var gearChanges = CountGearChanges(samples, window.StartS, window.EndS);
        var score =
            coasting +
            overlap * 1.25 +
            Math.Max(0, (throttleRecovery ?? 1.5) - 0.65) * 1.5 +
            Math.Max(0, gearChanges - 1) * 0.35;

        return new CornerOptimizationMetrics(
            window,
            SpeedAtProgress(samples, window.StartS) * 3.6,
            MinimumSpeed(samples, window.StartS, window.EndS) * 3.6,
            SpeedAtProgress(samples, window.EndS) * 3.6,
            throttleRecovery,
            coasting,
            overlap,
            GearAtProgress(samples, window.ApexS),
            gearChanges,
            score);
    }

    private static int FindExitIndex(
        IReadOnlyList<LapSample> samples,
        int apexIndex,
        double maximumProgress)
    {
        var apexSpeed = samples[apexIndex].SpeedMps;
        var result = apexIndex;
        for (var index = apexIndex + 1;
             index < samples.Count && samples[index].S <= maximumProgress;
             index++)
        {
            result = index;
            if (samples[index].Accel >= ThrottleThreshold &&
                samples[index].SpeedMps >= apexSpeed + 2)
                break;
        }

        return result;
    }

    private static void AddCandidate(List<WindowCandidate> candidates, WindowCandidate candidate)
    {
        if (candidate.EndS - candidate.StartS < 30) return;
        var overlappingIndex = candidates.FindIndex(existing =>
            candidate.StartS <= existing.EndS + 25 &&
            candidate.EndS >= existing.StartS - 25);
        if (overlappingIndex < 0)
        {
            candidates.Add(candidate);
            return;
        }

        var existing = candidates[overlappingIndex];
        candidates[overlappingIndex] = new WindowCandidate(
            Math.Min(existing.StartS, candidate.StartS),
            existing.Significance >= candidate.Significance ? existing.ApexS : candidate.ApexS,
            Math.Max(existing.EndS, candidate.EndS),
            Math.Max(existing.Significance, candidate.Significance));
    }

    private static IReadOnlyList<LapSample> ReduceByDistance(
        IReadOnlyList<LapSample> samples,
        double spacingMeters)
    {
        var result = new List<LapSample> { samples[0] };
        var nextProgress = samples[0].S + spacingMeters;
        foreach (var sample in samples.Skip(1))
        {
            if (sample.S < nextProgress) continue;
            result.Add(sample);
            nextProgress = sample.S + spacingMeters;
        }

        if (result[^1] != samples[^1]) result.Add(samples[^1]);
        return result;
    }

    private static double SpeedAtProgress(IReadOnlyList<LapSample> samples, double progress)
    {
        var index = ChartInteractionAlgorithms.FindNearestProgressSample(samples, progress);
        return index >= 0 ? samples[index].SpeedMps : 0;
    }

    private static int GearAtProgress(IReadOnlyList<LapSample> samples, double progress)
    {
        var index = ChartInteractionAlgorithms.FindNearestProgressSample(samples, progress);
        return index >= 0 ? samples[index].Gear : 0;
    }

    private static double MinimumSpeed(
        IReadOnlyList<LapSample> samples,
        double startS,
        double endS) =>
        samples
            .Where(sample => sample.S >= startS && sample.S <= endS)
            .Select(sample => sample.SpeedMps)
            .DefaultIfEmpty(SpeedAtProgress(samples, (startS + endS) / 2))
            .Min();

    private static double? FirstThresholdProgress(
        IReadOnlyList<LapSample> samples,
        double startS,
        double endS,
        Func<LapSample, bool> predicate) =>
        FirstThresholdSample(samples, startS, endS, predicate)?.S;

    private static LapSample? FirstThresholdSample(
        IReadOnlyList<LapSample> samples,
        double startS,
        double endS,
        Func<LapSample, bool> predicate) =>
        samples.FirstOrDefault(sample =>
            sample.S >= startS &&
            sample.S <= endS &&
            predicate(sample));

    private static double? ElapsedAtProgress(IReadOnlyList<LapSample> samples, double progress)
    {
        if (samples.Count == 0) return null;
        var nearest = ChartInteractionAlgorithms.FindNearestProgressSample(samples, progress);
        if (nearest < 0) return null;
        if (samples[nearest].S <= progress && nearest + 1 < samples.Count)
            return InterpolateTime(samples[nearest], samples[nearest + 1], progress);
        if (samples[nearest].S > progress && nearest > 0)
            return InterpolateTime(samples[nearest - 1], samples[nearest], progress);
        return samples[nearest].ElapsedSeconds;
    }

    private static double InterpolateTime(LapSample left, LapSample right, double progress)
    {
        var range = right.S - left.S;
        if (range <= 0.000_001) return left.ElapsedSeconds;
        var amount = Math.Clamp((progress - left.S) / range, 0, 1);
        return left.ElapsedSeconds + (right.ElapsedSeconds - left.ElapsedSeconds) * amount;
    }

    private static double MeanLineDeviation(
        IReadOnlyList<LapSample> selected,
        IReadOnlyList<LapSample> reference,
        double startS,
        double endS)
    {
        var inWindow = selected
            .Where(sample => sample.S >= startS && sample.S <= endS)
            .ToArray();
        if (inWindow.Length == 0 || reference.Count == 0) return 0;
        var step = Math.Max(1, inWindow.Length / 80);
        var distances = new List<double>();
        for (var index = 0; index < inWindow.Length; index += step)
        {
            var sample = inWindow[index];
            var referenceIndex = ChartInteractionAlgorithms.FindNearestProgressSample(reference, sample.S);
            if (referenceIndex < 0) continue;
            var referenceSample = reference[referenceIndex];
            var deltaX = sample.X - referenceSample.X;
            var deltaZ = sample.Z - referenceSample.Z;
            distances.Add(Math.Sqrt(deltaX * deltaX + deltaZ * deltaZ));
        }

        return distances.Count == 0 ? 0 : distances.Average();
    }

    private static int CountGearChanges(
        IReadOnlyList<LapSample> samples,
        double startS,
        double endS)
    {
        var changes = 0;
        byte? previous = null;
        foreach (var sample in samples.Where(sample => sample.S >= startS && sample.S <= endS))
        {
            if (sample.Gear == 0) continue;
            if (previous is not null && previous.Value != sample.Gear) changes++;
            previous = sample.Gear;
        }

        return changes;
    }

    private static double DurationMatching(
        IReadOnlyList<LapSample> samples,
        double startS,
        double endS,
        Func<LapSample, bool> predicate)
    {
        var duration = 0d;
        LapSample? previous = null;
        foreach (var sample in samples.Where(sample => sample.S >= startS && sample.S <= endS))
        {
            if (previous is not null && predicate(previous))
            {
                var delta = sample.ElapsedSeconds - previous.ElapsedSeconds;
                if (delta is > 0 and <= 0.5) duration += delta;
            }
            previous = sample;
        }

        return duration;
    }

    private static bool IsUsable(LapSample sample) =>
        double.IsFinite(sample.S) &&
        double.IsFinite(sample.ElapsedSeconds) &&
        double.IsFinite(sample.SpeedMps) &&
        sample.S >= 0 &&
        sample.ElapsedSeconds >= 0 &&
        sample.SpeedMps >= 0;

    private sealed record WindowCandidate(
        double StartS,
        double ApexS,
        double EndS,
        double Significance);
}
