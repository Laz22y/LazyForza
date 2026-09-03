using LazyForza.Domain;
using LazyForza.Modules.Dashboard;

namespace LazyForza.Overlay;

public readonly record struct DashboardHudVisualState(
    double Opacity,
    double HorizontalOffset,
    double VerticalOffset,
    bool IsIdleHidden);

/// <summary>
/// Testable presentation state for dashboard inertia and visibility. Window opacity remains the
/// user's configured ceiling; this controller supplies the animated 0..1 dashboard multiplier.
/// </summary>
public sealed class DashboardHudDynamics
{
    private const double GravityMetersPerSecondSquared = 9.80665;
    private const double ActivityInputThreshold = 0.02;
    private const double ActivitySpeedThresholdMps = 0.2;
    private const double AccelerationDeadZoneG = 0.035;
    private const double MotionIntensityRange = 2;
    private const double MaximumMotionOffset = 2.15;
    private bool initialized;
    private bool wasBaseVisible;
    private double lastUpdateSeconds;
    private double lastActivitySeconds;
    private double opacity;
    private double horizontalOffset;
    private double verticalOffset;
    private double horizontalVelocity;
    private double verticalVelocity;
    private byte? lastGear;
    private double? lastClutch;
    private double? lastHandBrake;

    public DashboardHudVisualState Update(
        DashboardHudState? dashboard,
        bool baseVisible,
        OverlayLayout layout,
        double nowSeconds)
    {
        if (!double.IsFinite(nowSeconds)) nowSeconds = initialized ? lastUpdateSeconds : 0;
        if (initialized && nowSeconds < lastUpdateSeconds) nowSeconds = lastUpdateSeconds;
        var deltaSeconds = initialized ? nowSeconds - lastUpdateSeconds : 0;
        lastUpdateSeconds = nowSeconds;
        initialized = true;

        if (!baseVisible || dashboard is null)
        {
            opacity = 0;
            lastActivitySeconds = nowSeconds;
            lastGear = dashboard?.RawGear;
            lastClutch = dashboard?.Clutch;
            lastHandBrake = dashboard?.HandBrake;
            wasBaseVisible = false;
            ResetMotion();
            return new DashboardHudVisualState(0, 0, 0, false);
        }

        if (!wasBaseVisible)
        {
            opacity = 0;
            lastActivitySeconds = nowSeconds;
            ResetMotion();
        }

        var gearChanged = lastGear is byte previousGear && previousGear != dashboard.RawGear;
        var clutchChanged = lastClutch is double previousClutch &&
                            Math.Abs(previousClutch - dashboard.Clutch) > ActivityInputThreshold;
        var handBrakeChanged = lastHandBrake is double previousHandBrake &&
                               Math.Abs(previousHandBrake - dashboard.HandBrake) > ActivityInputThreshold;
        lastGear = dashboard.RawGear;
        lastClutch = dashboard.Clutch;
        lastHandBrake = dashboard.HandBrake;
        if (HasDriverActivity(dashboard) || gearChanged || clutchChanged || handBrakeChanged)
            lastActivitySeconds = nowSeconds;

        var idleWaitSeconds = Math.Clamp(layout.DashboardIdleWaitSeconds, 0, 60);
        var visibilityFadeSeconds = Math.Clamp(layout.DashboardVisibilityFadeSeconds, 0.05, 10);
        var isIdleHidden = nowSeconds - lastActivitySeconds >= idleWaitSeconds;
        opacity = MoveTowards(
            opacity,
            isIdleHidden ? 0 : 1,
            deltaSeconds / visibilityFadeSeconds);

        UpdateMotion(dashboard, layout, deltaSeconds);
        wasBaseVisible = true;
        return new DashboardHudVisualState(opacity, horizontalOffset, verticalOffset, isIdleHidden);
    }

    public static bool HasDriverActivity(DashboardHudState dashboard) =>
        Math.Abs(dashboard.SpeedMps) > ActivitySpeedThresholdMps ||
        dashboard.Throttle > ActivityInputThreshold ||
        dashboard.Brake > ActivityInputThreshold ||
        Math.Abs(dashboard.Steering) > ActivityInputThreshold;

    private void UpdateMotion(DashboardHudState dashboard, OverlayLayout layout, double deltaSeconds)
    {
        if (layout.ReduceMotion)
        {
            ResetMotion();
            return;
        }

        var intensity = layout.DashboardMotionEnabled
            ? Math.Clamp(layout.DashboardMotionIntensity, 0, 1) * MotionIntensityRange
            : 0;
        var targetX = intensity * -NormalizeAcceleration(dashboard.Acceleration.X);
        var targetY = intensity * NormalizeAcceleration(dashboard.Acceleration.Z);
        IntegrateSpring(ref horizontalOffset, ref horizontalVelocity, targetX, deltaSeconds);
        IntegrateSpring(ref verticalOffset, ref verticalVelocity, targetY, deltaSeconds);
    }

    private static double NormalizeAcceleration(float acceleration)
    {
        if (!float.IsFinite(acceleration)) return 0;
        var value = Math.Clamp(acceleration / GravityMetersPerSecondSquared, -1, 1);
        var magnitude = Math.Abs(value);
        if (magnitude <= AccelerationDeadZoneG) return 0;
        return Math.Sign(value) * (magnitude - AccelerationDeadZoneG) / (1 - AccelerationDeadZoneG);
    }

    private static void IntegrateSpring(ref double position, ref double velocity, double target, double deltaSeconds)
    {
        var remaining = Math.Clamp(deltaSeconds, 0, 0.25);
        const double angularFrequency = 10;
        const double dampingRatio = 0.82;
        while (remaining > 0)
        {
            var step = Math.Min(remaining, 1d / 60);
            var acceleration = (target - position) * angularFrequency * angularFrequency -
                               2 * dampingRatio * angularFrequency * velocity;
            velocity += acceleration * step;
            position += velocity * step;
            remaining -= step;
        }

        position = Math.Clamp(position, -MaximumMotionOffset, MaximumMotionOffset);
    }

    private static double MoveTowards(double current, double target, double maximumDelta)
    {
        maximumDelta = Math.Max(0, maximumDelta);
        if (Math.Abs(target - current) <= maximumDelta) return target;
        return current + Math.Sign(target - current) * maximumDelta;
    }

    private void ResetMotion()
    {
        horizontalOffset = 0;
        verticalOffset = 0;
        horizontalVelocity = 0;
        verticalVelocity = 0;
    }
}
