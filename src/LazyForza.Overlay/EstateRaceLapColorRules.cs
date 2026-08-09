using LazyForza.Domain;

namespace LazyForza.Overlay;

public static class EstateRaceLapColorRules
{
    public static SectorColorState Resolve(
        double current,
        double? sessionBest,
        double? personalBest)
    {
        if (sessionBest is double fastest && current <= fastest + 0.0005)
            return SectorColorState.Purple;
        // Before anybody has completed a valid lap, the current benchmark is
        // provisionally the session best. The first timed sectors must therefore
        // be purple rather than personal-best green.
        if (sessionBest is null && personalBest is null)
            return SectorColorState.Purple;
        if (personalBest is null || current <= personalBest.Value + 0.0005)
            return SectorColorState.Green;
        return SectorColorState.Yellow;
    }
}

public static class EstateRaceLapDeltaRules
{
    public static double? CumulativeToPhaseFastest(
        IReadOnlyList<double?> currentSectorSeconds,
        IReadOnlyList<double?> phaseFastestLapSectorSeconds,
        int completedSectorCount)
    {
        var count = Math.Min(
            Math.Max(0, completedSectorCount),
            Math.Min(currentSectorSeconds.Count, phaseFastestLapSectorSeconds.Count));
        if (count == 0) return null;

        double currentTotal = 0;
        double referenceTotal = 0;
        for (var index = 0; index < count; index++)
        {
            if (currentSectorSeconds[index] is not double current || !double.IsFinite(current) || current <= 0 ||
                phaseFastestLapSectorSeconds[index] is not double reference ||
                !double.IsFinite(reference) || reference <= 0)
                return null;
            currentTotal += current;
            referenceTotal += reference;
        }
        return currentTotal - referenceTotal;
    }
}
