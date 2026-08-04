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
