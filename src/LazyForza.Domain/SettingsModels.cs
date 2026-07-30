namespace LazyForza.Domain;

public sealed record OverlayLayout(
    double Left = 579,
    double Top = 669,
    double Width = 1338.3333333333335,
    double Height = 753.3333333333334,
    double Scale = 0.6,
    double Opacity = 1,
    string MonitorId = "primary",
    bool ClickThrough = true,
    bool IsLocked = true,
    bool ReduceMotion = false,
    bool DashboardMotionEnabled = true,
    double DashboardMotionIntensity = 0.5,
    double DashboardIdleWaitSeconds = 2,
    double DashboardVisibilityFadeSeconds = 0.8,
    double LapCompletedHoldSeconds = 1,
    double LapNoMatchConfirmationSeconds = 8,
    double LapNoMatchFadeSeconds = 0.5,
    double LiveHudStaleSeconds = 0.8,
    double? LapHudLeft = null,
    double? LapHudTop = null,
    double? LapHudScale = null,
    bool LapHudAttachedToDashboard = true);

public enum OverlayHudKind
{
    Dashboard,
    Lap
}

public readonly record struct OverlayHudBounds(
    double Left,
    double Top,
    double Width,
    double Height)
{
    public double Right => Left + Width;
    public double Bottom => Top + Height;
    public double CenterX => Left + Width / 2;
    public double CenterY => Top + Height / 2;
}

public static class OverlayLayoutGeometry
{
    public static OverlayLayout Normalize(OverlayLayout value)
    {
        var width = FinitePositive(value.Width, 1338.3333333333335);
        var height = FinitePositive(value.Height, 753.3333333333334);
        var left = Finite(value.Left, 579);
        var top = Finite(value.Top, 669);
        var scale = OverlayScaleSettings.Normalize(value.Scale);
        var normalized = value with
        {
            Left = left,
            Top = top,
            Width = width,
            Height = height,
            Scale = scale
        };
        if (value.LapHudAttachedToDashboard)
            return AttachLapToDashboard(normalized);

        return normalized with
        {
            LapHudLeft = Finite(value.LapHudLeft, left),
            LapHudTop = Finite(value.LapHudTop, top),
            LapHudScale = OverlayScaleSettings.Normalize(value.LapHudScale ?? scale),
            LapHudAttachedToDashboard = false
        };
    }

    public static OverlayHudBounds Bounds(
        OverlayLayout value,
        OverlayHudKind kind)
    {
        var layout = Normalize(value);
        var scale = kind == OverlayHudKind.Dashboard
            ? layout.Scale
            : layout.LapHudScale ?? layout.Scale;
        return new OverlayHudBounds(
            kind == OverlayHudKind.Dashboard
                ? layout.Left
                : layout.LapHudLeft ?? layout.Left,
            kind == OverlayHudKind.Dashboard
                ? layout.Top
                : layout.LapHudTop ?? layout.Top,
            OverlayScaleSettings.ScaledDimension(layout.Width, scale),
            OverlayScaleSettings.ScaledDimension(layout.Height, scale));
    }

    public static OverlayHudBounds UnionBounds(OverlayLayout value)
    {
        var dashboard = Bounds(value, OverlayHudKind.Dashboard);
        var lap = Bounds(value, OverlayHudKind.Lap);
        var left = Math.Min(dashboard.Left, lap.Left);
        var top = Math.Min(dashboard.Top, lap.Top);
        var right = Math.Max(dashboard.Right, lap.Right);
        var bottom = Math.Max(dashboard.Bottom, lap.Bottom);
        return new OverlayHudBounds(left, top, right - left, bottom - top);
    }

    public static OverlayLayout AttachLapToDashboard(OverlayLayout value)
    {
        var scale = OverlayScaleSettings.Normalize(value.Scale);
        return value with
        {
            Scale = scale,
            LapHudLeft = Finite(value.Left, 579),
            LapHudTop = Finite(value.Top, 669),
            LapHudScale = scale,
            LapHudAttachedToDashboard = true
        };
    }

    public static OverlayLayout DetachLap(OverlayLayout value)
    {
        var normalized = Normalize(value);
        var lap = Bounds(normalized, OverlayHudKind.Lap);
        return normalized with
        {
            LapHudLeft = lap.Left,
            LapHudTop = lap.Top,
            LapHudScale = normalized.LapHudScale ?? normalized.Scale,
            LapHudAttachedToDashboard = false
        };
    }

    public static OverlayLayout Move(
        OverlayLayout value,
        OverlayHudKind kind,
        double left,
        double top)
    {
        var normalized = Normalize(value);
        left = Finite(left, normalized.Left);
        top = Finite(top, normalized.Top);
        if (kind == OverlayHudKind.Dashboard)
        {
            var moved = normalized with { Left = left, Top = top };
            return normalized.LapHudAttachedToDashboard
                ? AttachLapToDashboard(moved)
                : moved;
        }

        return DetachLap(normalized) with
        {
            LapHudLeft = left,
            LapHudTop = top
        };
    }

    public static OverlayLayout ScaleAroundCenter(
        OverlayLayout value,
        OverlayHudKind kind,
        double nextScale)
    {
        var normalized = Normalize(value);
        var current = Bounds(normalized, kind);
        nextScale = OverlayScaleSettings.Normalize(nextScale);
        var width = OverlayScaleSettings.ScaledDimension(normalized.Width, nextScale);
        var height = OverlayScaleSettings.ScaledDimension(normalized.Height, nextScale);
        var left = current.CenterX - width / 2;
        var top = current.CenterY - height / 2;

        if (kind == OverlayHudKind.Dashboard)
        {
            var scaled = normalized with
            {
                Left = left,
                Top = top,
                Scale = nextScale
            };
            return normalized.LapHudAttachedToDashboard
                ? AttachLapToDashboard(scaled)
                : scaled;
        }

        return DetachLap(normalized) with
        {
            LapHudLeft = left,
            LapHudTop = top,
            LapHudScale = nextScale
        };
    }

    private static double Finite(double? value, double fallback) =>
        value is double candidate && double.IsFinite(candidate)
            ? candidate
            : fallback;

    private static double FinitePositive(double value, double fallback) =>
        double.IsFinite(value) && value > 0
            ? value
            : fallback;
}

public static class OverlayScaleSettings
{
    public const double Minimum = 0.20;
    public const double Maximum = 1.50;
    public const double Step = 0.01;
    public const double Default = 0.60;

    public static double Normalize(double scale)
    {
        if (!double.IsFinite(scale)) return Default;
        return Math.Clamp(scale, Minimum, Maximum);
    }

    public static double SnapToStep(double scale)
    {
        var clamped = Normalize(scale);
        return Math.Clamp(
            Math.Round(clamped / Step, MidpointRounding.AwayFromZero) * Step,
            Minimum,
            Maximum);
    }

    public static double ScaledDimension(double baseDimension, double scale)
    {
        var safeBaseDimension = double.IsFinite(baseDimension)
            ? Math.Max(1, baseDimension)
            : 1;
        return Math.Max(1, safeBaseDimension * Normalize(scale));
    }
}

public sealed record TelemetryOptions(
    string ListenAddress = "127.0.0.1",
    int Port = 2299,
    TimeSpan? StaleAfter = null,
    int SubscriberCapacity = 1)
{
    public TimeSpan EffectiveStaleAfter => StaleAfter ?? TimeSpan.FromSeconds(1.5);
}
