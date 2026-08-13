using LazyForza.Domain;

namespace LazyForza.Overlay;

internal readonly record struct EstateRaceWidgetVisual(
    bool ShouldDraw,
    double Opacity,
    double OffsetXFactor,
    double OffsetYFactor,
    double Scale,
    bool IsAnimating);

internal sealed class EstateRaceHudAnimationController
{
    private readonly Dictionary<EstateRaceHudWidgetKind, WidgetState> states = [];

    public bool AnyAnimating => states.Values.Any(state => state.IsAnimating);

    public EstateRaceWidgetVisual Update(
        EstateRaceHudWidgetKind kind,
        bool visible,
        double nowSeconds,
        bool reduceMotion,
        bool instant = false)
    {
        if (!states.TryGetValue(kind, out var state))
        {
            state = new WidgetState
            {
                Progress = visible && instant ? 1 : 0,
                LastSeconds = nowSeconds
            };
            states[kind] = state;
        }

        var deltaSeconds = Math.Clamp(nowSeconds - state.LastSeconds, 0, 0.25);
        state.LastSeconds = nowSeconds;
        var spec = Spec(kind);
        if (instant)
        {
            state.Progress = visible ? 1 : 0;
        }
        else
        {
            var duration = reduceMotion
                ? 0.10
                : visible ? spec.EnterSeconds : spec.ExitSeconds;
            state.Progress = MoveTowards(
                state.Progress,
                visible ? 1 : 0,
                deltaSeconds / Math.Max(0.01, duration));
        }

        state.IsAnimating = Math.Abs(state.Progress - (visible ? 1 : 0)) > 0.001;
        var eased = SmoothStep(state.Progress);
        var motion = reduceMotion ? 0 : 1 - eased;
        var scale = reduceMotion
            ? 1
            : 1 - (1 - spec.StartScale) * motion;
        return new EstateRaceWidgetVisual(
            visible || state.Progress > 0.001,
            eased,
            spec.OffsetXFactor * motion,
            spec.OffsetYFactor * motion,
            scale,
            state.IsAnimating);
    }

    private static WidgetSpec Spec(EstateRaceHudWidgetKind kind) => kind switch
    {
        EstateRaceHudWidgetKind.Leaderboard => new(-0.007, 0, 1, 0.18, 0.18),
        EstateRaceHudWidgetKind.TrackMap => new(0, 0, 0.97, 0.18, 0.18),
        EstateRaceHudWidgetKind.GripStatus => new(0.006, 0, 1, 0.22, 0.18),
        EstateRaceHudWidgetKind.Banner => new(0, -0.013, 1, 0.22, 0.18),
        EstateRaceHudWidgetKind.StartLights => new(0, 0, 0.94, 0.15, 0.22),
        EstateRaceHudWidgetKind.PitStopInfo => new(0.006, 0, 1, 0.22, 0.20),
        EstateRaceHudWidgetKind.PitLimiter => new(0, 0, 0.92, 0.16, 0.14),
        EstateRaceHudWidgetKind.PenaltyStatus => new(0, -0.008, 1, 0.18, 0.18),
        EstateRaceHudWidgetKind.PracticeProgram => new(0, 0.009, 1, 0.22, 0.20),
        EstateRaceHudWidgetKind.PitWindowSuggestion => new(0.006, 0, 1, 0.22, 0.18),
        EstateRaceHudWidgetKind.FullRaceStrategy => new(0, -0.010, 0.97, 0.26, 0.22),
        _ => new(0, 0, 1, 0.18, 0.18)
    };

    private static double MoveTowards(double current, double target, double maximumDelta)
    {
        maximumDelta = Math.Max(0, maximumDelta);
        if (Math.Abs(target - current) <= maximumDelta) return target;
        return current + Math.Sign(target - current) * maximumDelta;
    }

    private static double SmoothStep(double value)
    {
        value = Math.Clamp(value, 0, 1);
        return value * value * (3 - 2 * value);
    }

    private sealed class WidgetState
    {
        public double Progress { get; set; }
        public double LastSeconds { get; set; }
        public bool IsAnimating { get; set; }
    }

    private readonly record struct WidgetSpec(
        double OffsetXFactor,
        double OffsetYFactor,
        double StartScale,
        double EnterSeconds,
        double ExitSeconds);
}
