using LazyForza.Domain;
using LazyForza.Modules.LapAnalysis;

namespace LazyForza.Overlay;

public readonly record struct LapHudVisualState(double Opacity, bool IsSuppressedForCompetition);

/// <summary>
/// Owns the presentation-only no-match confirmation and fade. Once rejected, a competition
/// remains suppressed until the analysis module publishes a different session identifier.
/// </summary>
public sealed class LapHudDynamics
{
    private Guid competitionSessionId;
    private bool initialized;
    private bool suppressed;
    private double? rejectionEvidenceStartedSeconds;
    private double lastUpdateSeconds;
    private double opacity;

    public LapHudVisualState Update(
        LapHudState? lap,
        bool baseVisible,
        OverlayLayout layout,
        double nowSeconds)
    {
        if (!double.IsFinite(nowSeconds)) nowSeconds = initialized ? lastUpdateSeconds : 0;
        if (initialized && nowSeconds < lastUpdateSeconds) nowSeconds = lastUpdateSeconds;
        var deltaSeconds = initialized ? nowSeconds - lastUpdateSeconds : 0;
        lastUpdateSeconds = nowSeconds;

        if (!initialized || lap?.CompetitionSessionId != competitionSessionId)
        {
            initialized = true;
            competitionSessionId = lap?.CompetitionSessionId ?? Guid.Empty;
            suppressed = false;
            rejectionEvidenceStartedSeconds = null;
            opacity = baseVisible ? 1 : 0;
        }

        if (!baseVisible || lap is null)
        {
            opacity = 0;
            rejectionEvidenceStartedSeconds = null;
            return new LapHudVisualState(0, suppressed);
        }

        if (!suppressed && lap.MatchRejectionEligible)
        {
            rejectionEvidenceStartedSeconds ??= nowSeconds;
            var confirmationSeconds = Math.Clamp(layout.LapNoMatchConfirmationSeconds, 0.1, 60);
            if (nowSeconds - rejectionEvidenceStartedSeconds.Value >= confirmationSeconds) suppressed = true;
        }
        else if (!suppressed)
        {
            rejectionEvidenceStartedSeconds = null;
        }

        if (!suppressed)
        {
            opacity = 1;
            return new LapHudVisualState(opacity, false);
        }

        var fadeSeconds = Math.Clamp(layout.LapNoMatchFadeSeconds, 0.05, 10);
        opacity = MoveTowards(opacity, 0, deltaSeconds / fadeSeconds);
        return new LapHudVisualState(opacity, true);
    }

    private static double MoveTowards(double current, double target, double maximumDelta)
    {
        maximumDelta = Math.Max(0, maximumDelta);
        if (Math.Abs(target - current) <= maximumDelta) return target;
        return current + Math.Sign(target - current) * maximumDelta;
    }
}
