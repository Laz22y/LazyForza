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
    bool LapHudAttachedToDashboard = true,
    double? DriftHudLeft = null,
    double? DriftHudTop = null,
    double? DriftHudScale = null,
    DashboardWidgetLayout? DashboardWidgets = null,
    EstateRaceHudLayout? EstateRaceWidgets = null,
    double? EstateRaceHudLeft = null,
    double? EstateRaceHudTop = null,
    double? EstateRaceHudWidth = null,
    double? EstateRaceHudHeight = null);

public enum OverlayHudKind
{
    Dashboard,
    Lap,
    Drift,
    EstateRace
}

public enum DashboardWidgetKind
{
    RpmArc,
    SpeedGear,
    EngineOutput,
    Tires,
    Pedals,
    Steering,
    ClassBadge
}

public enum EstateRaceHudWidgetKind
{
    Leaderboard,
    TrackMap,
    GripStatus,
    Banner,
    StartLights,
    PitStopInfo,
    PitLimiter,
    PenaltyStatus
}

public sealed record EstateRaceHudWidgetPlacement(
    bool IsVisible,
    double Left,
    double Top,
    double Scale = 1,
    double Opacity = 1);

public sealed record EstateRaceHudLayout(
    EstateRaceHudWidgetPlacement? Leaderboard = null,
    EstateRaceHudWidgetPlacement? TrackMap = null,
    EstateRaceHudWidgetPlacement? GripStatus = null,
    EstateRaceHudWidgetPlacement? Banner = null,
    EstateRaceHudWidgetPlacement? StartLights = null,
    EstateRaceHudWidgetPlacement? PitStopInfo = null,
    EstateRaceHudWidgetPlacement? PitLimiter = null,
    EstateRaceHudWidgetPlacement? PenaltyStatus = null)
{
    public EstateRaceHudWidgetPlacement Get(EstateRaceHudWidgetKind kind) => kind switch
    {
        EstateRaceHudWidgetKind.Leaderboard => Leaderboard ?? EstateRaceHudLayoutSettings.Default.Get(kind),
        EstateRaceHudWidgetKind.TrackMap => TrackMap ?? EstateRaceHudLayoutSettings.Default.Get(kind),
        EstateRaceHudWidgetKind.GripStatus => GripStatus ?? EstateRaceHudLayoutSettings.Default.Get(kind),
        EstateRaceHudWidgetKind.Banner => Banner ?? EstateRaceHudLayoutSettings.Default.Get(kind),
        EstateRaceHudWidgetKind.StartLights => StartLights ?? EstateRaceHudLayoutSettings.Default.Get(kind),
        EstateRaceHudWidgetKind.PitStopInfo => PitStopInfo ?? EstateRaceHudLayoutSettings.Default.Get(kind),
        EstateRaceHudWidgetKind.PitLimiter => PitLimiter ?? EstateRaceHudLayoutSettings.Default.Get(kind),
        _ => PenaltyStatus ?? EstateRaceHudLayoutSettings.Default.Get(kind)
    };

    public EstateRaceHudLayout Set(
        EstateRaceHudWidgetKind kind,
        EstateRaceHudWidgetPlacement placement) => kind switch
        {
            EstateRaceHudWidgetKind.Leaderboard => this with { Leaderboard = placement },
            EstateRaceHudWidgetKind.TrackMap => this with { TrackMap = placement },
            EstateRaceHudWidgetKind.GripStatus => this with { GripStatus = placement },
            EstateRaceHudWidgetKind.Banner => this with { Banner = placement },
            EstateRaceHudWidgetKind.StartLights => this with { StartLights = placement },
            EstateRaceHudWidgetKind.PitStopInfo => this with { PitStopInfo = placement },
            EstateRaceHudWidgetKind.PitLimiter => this with { PitLimiter = placement },
            _ => this with { PenaltyStatus = placement }
        };
}

public static class EstateRaceHudLayoutSettings
{
    private static readonly EstateRaceHudLayout DefaultValue = new(
        new(true, 0.025, 0.12, 1),
        new(true, 0.80, 0.17, 0.96),
        new(true, 0.80, 0.47, 0.96),
        new(true, 0.25, 0.025, 1),
        new(true, 0.35, 0.12, 1),
        new(true, 0.025, 0.815, 1),
        new(true, 0.91, 0.62, 1),
        new(true, 0.39, 0.70, 1));

    public static EstateRaceHudLayout Default => DefaultValue;

    public static EstateRaceHudLayout Normalize(EstateRaceHudLayout? value)
    {
        var source = value ?? DefaultValue;
        var normalized = new EstateRaceHudLayout();
        foreach (var kind in Enum.GetValues<EstateRaceHudWidgetKind>())
            normalized = normalized.Set(kind, Normalize(kind, source.Get(kind)));
        return normalized;
    }

    public static double MinimumScale(EstateRaceHudWidgetKind kind) => kind switch
    {
        EstateRaceHudWidgetKind.TrackMap or EstateRaceHudWidgetKind.PitStopInfo => 0.80,
        EstateRaceHudWidgetKind.Leaderboard or EstateRaceHudWidgetKind.GripStatus or
            EstateRaceHudWidgetKind.PenaltyStatus => 0.75,
        EstateRaceHudWidgetKind.Banner => 0.70,
        _ => 0.60
    };

    public static double NormalizeScale(EstateRaceHudWidgetKind kind, double scale) =>
        double.IsFinite(scale)
            ? Math.Clamp(scale, MinimumScale(kind), 1.75)
            : 1;

    private static EstateRaceHudWidgetPlacement Normalize(
        EstateRaceHudWidgetKind kind,
        EstateRaceHudWidgetPlacement value) => value with
        {
            Left = Finite(value.Left, 0),
            Top = Finite(value.Top, 0),
            Scale = NormalizeScale(kind, value.Scale),
            Opacity = double.IsFinite(value.Opacity) ? Math.Clamp(value.Opacity, 0.15, 1) : 1
        };

    private static double Finite(double value, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, 0, 1) : fallback;
}

public sealed record DashboardWidgetPlacement(
    bool IsVisible = true,
    double OffsetX = 0,
    double OffsetY = 0);

public sealed record DashboardWidgetLayout(
    DashboardWidgetPlacement? RpmArc = null,
    DashboardWidgetPlacement? SpeedGear = null,
    DashboardWidgetPlacement? EngineOutput = null,
    DashboardWidgetPlacement? Tires = null,
    DashboardWidgetPlacement? Pedals = null,
    DashboardWidgetPlacement? Steering = null,
    DashboardWidgetPlacement? ClassBadge = null)
{
    public DashboardWidgetPlacement Get(DashboardWidgetKind kind) => kind switch
    {
        DashboardWidgetKind.RpmArc => RpmArc ?? new(),
        DashboardWidgetKind.SpeedGear => SpeedGear ?? new(),
        DashboardWidgetKind.EngineOutput => EngineOutput ?? new(),
        DashboardWidgetKind.Tires => Tires ?? new(),
        DashboardWidgetKind.Pedals => Pedals ?? new(),
        DashboardWidgetKind.Steering => Steering ?? new(),
        _ => ClassBadge ?? new()
    };

    public DashboardWidgetLayout Set(
        DashboardWidgetKind kind,
        DashboardWidgetPlacement placement) => kind switch
        {
            DashboardWidgetKind.RpmArc => this with { RpmArc = placement },
            DashboardWidgetKind.SpeedGear => this with { SpeedGear = placement },
            DashboardWidgetKind.EngineOutput => this with { EngineOutput = placement },
            DashboardWidgetKind.Tires => this with { Tires = placement },
            DashboardWidgetKind.Pedals => this with { Pedals = placement },
            DashboardWidgetKind.Steering => this with { Steering = placement },
            _ => this with { ClassBadge = placement }
        };
}

public readonly record struct DashboardWidgetNormalizedBounds(
    double Left,
    double Top,
    double Width,
    double Height)
{
    public double Right => Left + Width;
    public double Bottom => Top + Height;
}

public static class DashboardWidgetLayoutSettings
{
    private static readonly DashboardWidgetLayout DefaultValue =
        new DashboardWidgetLayout(
            new(),
            new(),
            new(),
            new(),
            new(),
            new(),
            new());

    public static DashboardWidgetLayout Default => DefaultValue;

    public static DashboardWidgetLayout CreateDefault() => DefaultValue;

    public static DashboardWidgetLayout Normalize(DashboardWidgetLayout? value)
    {
        if (value is null) return DefaultValue;
        var source = value;
        var normalized = new DashboardWidgetLayout();
        foreach (var kind in Enum.GetValues<DashboardWidgetKind>())
            normalized = normalized.Set(
                kind,
                ClampToCanvas(kind, source.Get(kind)));
        return normalized;
    }

    public static DashboardWidgetNormalizedBounds DefaultBounds(
        DashboardWidgetKind kind) => kind switch
        {
            DashboardWidgetKind.RpmArc => new(0.055, 0.035, 0.89, 0.39),
            DashboardWidgetKind.SpeedGear => new(0.245, 0.195, 0.25, 0.43),
            DashboardWidgetKind.EngineOutput => new(0.505, 0.195, 0.25, 0.43),
            DashboardWidgetKind.Tires => new(0.075, 0.60, 0.23, 0.275),
            DashboardWidgetKind.Pedals => new(0.395, 0.61, 0.21, 0.275),
            DashboardWidgetKind.Steering => new(0.365, 0.875, 0.27, 0.105),
            _ => new(0.69, 0.61, 0.20, 0.225)
        };

    public static DashboardWidgetPlacement ClampToCanvas(
        DashboardWidgetKind kind,
        DashboardWidgetPlacement placement)
    {
        var bounds = DefaultBounds(kind);
        var normalized = NormalizePlacement(placement);
        return normalized with
        {
            OffsetX = Math.Clamp(
                normalized.OffsetX,
                -bounds.Left,
                1 - bounds.Right),
            OffsetY = Math.Clamp(
                normalized.OffsetY,
                -bounds.Top,
                1 - bounds.Bottom)
        };
    }

    private static DashboardWidgetPlacement NormalizePlacement(
        DashboardWidgetPlacement value) => value with
        {
            OffsetX = Finite(value.OffsetX),
            OffsetY = Finite(value.OffsetY)
        };

    private static double Finite(double value) =>
        double.IsFinite(value) ? Math.Clamp(value, -1, 1) : 0;
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
            Scale = scale,
            DashboardWidgets = DashboardWidgetLayoutSettings.Normalize(
                value.DashboardWidgets),
            EstateRaceWidgets = EstateRaceHudLayoutSettings.Normalize(
                value.EstateRaceWidgets),
            EstateRaceHudLeft = Finite(value.EstateRaceHudLeft, left),
            EstateRaceHudTop = Finite(value.EstateRaceHudTop, top),
            EstateRaceHudWidth = FinitePositive(
                value.EstateRaceHudWidth ?? OverlayScaleSettings.ScaledDimension(width, scale),
                OverlayScaleSettings.ScaledDimension(width, scale)),
            EstateRaceHudHeight = FinitePositive(
                value.EstateRaceHudHeight ?? OverlayScaleSettings.ScaledDimension(height, scale),
                OverlayScaleSettings.ScaledDimension(height, scale))
        };
        var withLap = value.LapHudAttachedToDashboard
            ? AttachLapToDashboard(normalized)
            : normalized with
            {
                LapHudLeft = Finite(value.LapHudLeft, left),
                LapHudTop = Finite(value.LapHudTop, top),
                LapHudScale = OverlayScaleSettings.Normalize(
                    value.LapHudScale ?? scale),
                LapHudAttachedToDashboard = false
            };
        var driftScale = OverlayScaleSettings.Normalize(
            value.DriftHudScale ?? scale);
        var defaultDriftLeft =
            left +
            OverlayScaleSettings.ScaledDimension(width, scale) +
            24;
        return withLap with
        {
            DriftHudLeft = Finite(
                value.DriftHudLeft,
                defaultDriftLeft),
            DriftHudTop = Finite(value.DriftHudTop, top),
            DriftHudScale = driftScale
        };
    }

    public static OverlayHudBounds Bounds(
        OverlayLayout value,
        OverlayHudKind kind)
    {
        var layout = Normalize(value);
        var scale = kind switch
        {
            OverlayHudKind.Dashboard => layout.Scale,
            OverlayHudKind.Lap => layout.LapHudScale ?? layout.Scale,
            OverlayHudKind.Drift => layout.DriftHudScale ?? layout.Scale,
            _ => 1
        };
        return new OverlayHudBounds(
            kind switch
            {
                OverlayHudKind.Dashboard => layout.Left,
                OverlayHudKind.Lap => layout.LapHudLeft ?? layout.Left,
                OverlayHudKind.Drift => layout.DriftHudLeft ?? layout.Left,
                _ => layout.EstateRaceHudLeft ?? layout.Left
            },
            kind switch
            {
                OverlayHudKind.Dashboard => layout.Top,
                OverlayHudKind.Lap => layout.LapHudTop ?? layout.Top,
                OverlayHudKind.Drift => layout.DriftHudTop ?? layout.Top,
                _ => layout.EstateRaceHudTop ?? layout.Top
            },
            kind == OverlayHudKind.EstateRace
                ? layout.EstateRaceHudWidth ?? OverlayScaleSettings.ScaledDimension(layout.Width, layout.Scale)
                : OverlayScaleSettings.ScaledDimension(layout.Width, scale),
            kind == OverlayHudKind.EstateRace
                ? layout.EstateRaceHudHeight ?? OverlayScaleSettings.ScaledDimension(layout.Height, layout.Scale)
                : OverlayScaleSettings.ScaledDimension(layout.Height, scale));
    }

    public static OverlayHudBounds UnionBounds(OverlayLayout value)
    {
        var dashboard = Bounds(value, OverlayHudKind.Dashboard);
        var lap = Bounds(value, OverlayHudKind.Lap);
        var drift = Bounds(value, OverlayHudKind.Drift);
        var estateRace = Bounds(value, OverlayHudKind.EstateRace);
        var left = Math.Min(estateRace.Left, Math.Min(dashboard.Left, Math.Min(lap.Left, drift.Left)));
        var top = Math.Min(estateRace.Top, Math.Min(dashboard.Top, Math.Min(lap.Top, drift.Top)));
        var right = Math.Max(estateRace.Right, Math.Max(dashboard.Right, Math.Max(lap.Right, drift.Right)));
        var bottom = Math.Max(
            estateRace.Bottom,
            Math.Max(dashboard.Bottom, Math.Max(lap.Bottom, drift.Bottom)));
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

        if (kind == OverlayHudKind.Lap)
        {
            return DetachLap(normalized) with
            {
                LapHudLeft = left,
                LapHudTop = top
            };
        }

        if (kind == OverlayHudKind.EstateRace)
        {
            return normalized with
            {
                EstateRaceHudLeft = left,
                EstateRaceHudTop = top
            };
        }

        return normalized with
        {
            DriftHudLeft = left,
            DriftHudTop = top
        };
    }

    public static OverlayLayout ScaleAroundCenter(
        OverlayLayout value,
        OverlayHudKind kind,
        double nextScale)
    {
        var normalized = Normalize(value);
        if (kind == OverlayHudKind.EstateRace)
            return normalized;
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

        if (kind == OverlayHudKind.Lap)
        {
            return DetachLap(normalized) with
            {
                LapHudLeft = left,
                LapHudTop = top,
                LapHudScale = nextScale
            };
        }
        return normalized with
        {
            DriftHudLeft = left,
            DriftHudTop = top,
            DriftHudScale = nextScale
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
