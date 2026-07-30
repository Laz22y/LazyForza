using LazyForza.Domain;

namespace LazyForza.Modules.DriftDashboard;

public sealed class DriftTelemetryAnalyzer
{
    private const double MinimumPracticeSpeedKph = 20;
    private const double MinimumDriftAngleDegrees = 7;
    private const double StableMinimumAngleDegrees = 13;
    private const double StableMaximumAngleDegrees = 48;
    private const double MaximumUsefulAngleDegrees = 70;
    private readonly GearDisplayStabilizer gearDisplay = new();
    private DateTimeOffset? previousAt;
    private double previousThrottle;
    private double throttleChange;
    private double driftEvidenceSeconds;
    private double driftLossSeconds;
    private double currentDriftSeconds;
    private double stableDriftSeconds;
    private double bestStableDriftSeconds;
    private double stabilityScore;
    private bool isDrifting;

    public DriftHudState Observe(TelemetryFrame frame)
    {
        var isDriving = TelemetryContextClassifier.IsDriving(frame.Raw);
        var deltaSeconds = DeltaSeconds(frame.ArrivalTime);
        var throttle = Math.Clamp(frame.Normalized.AccelRatio, 0, 1);
        UpdateThrottleChange(throttle, deltaSeconds);

        var localForward = Finite(frame.Raw.Velocity.Z);
        var localLateral = Finite(frame.Raw.Velocity.X);
        var forwardMotion = localForward > 1;
        var driftAngle = forwardMotion
            ? Math.Clamp(
                Math.Atan2(localLateral, Math.Max(0.1, localForward)) *
                180 / Math.PI,
                -MaximumUsefulAngleDegrees,
                MaximumUsefulAngleDegrees)
            : 0;
        var absoluteAngle = Math.Abs(driftAngle);
        var speedKph = Math.Max(0, frame.Normalized.SpeedKph);
        var yawRate = Finite(frame.Raw.AngularVelocity.Y) * 180 / Math.PI;
        var frontSlip = MeanAbsolute(
            frame.Raw.TireCombinedSlip.FrontLeft,
            frame.Raw.TireCombinedSlip.FrontRight);
        var rearSlip = MeanAbsolute(
            frame.Raw.TireCombinedSlip.RearLeft,
            frame.Raw.TireCombinedSlip.RearRight);
        var rearLateralSlip = MeanAbsolute(
            frame.Raw.TireSlipAngle.RearLeft,
            frame.Raw.TireSlipAngle.RearRight);
        var rearLongitudinalSlip = MeanAbsolute(
            frame.Raw.TireSlipRatio.RearLeft,
            frame.Raw.TireSlipRatio.RearRight);
        var steering = Math.Clamp(frame.Raw.Steer / 127d, -1, 1);
        var steeringCoordination = SteeringCoordination(
            driftAngle,
            steering);
        var throttleSmoothness = Math.Clamp(1 - throttleChange, 0, 1);

        var hasDriftEvidence =
            isDriving &&
            forwardMotion &&
            speedKph >= MinimumPracticeSpeedKph &&
            absoluteAngle >= MinimumDriftAngleDegrees &&
            (rearSlip >= 0.10 || rearLateralSlip >= 0.12) &&
            (Math.Abs(yawRate) >= 4 || absoluteAngle >= 14);
        UpdateDriftState(hasDriftEvidence, deltaSeconds);

        var instantStability = CalculateStability(
            speedKph,
            absoluteAngle,
            rearSlip,
            rearLongitudinalSlip,
            throttleSmoothness,
            steeringCoordination,
            yawRate);
        var stabilityAlpha = 1 - Math.Exp(-deltaSeconds / 0.32);
        stabilityScore += (instantStability - stabilityScore) * stabilityAlpha;
        if (!isDrifting)
            stabilityScore = Math.Max(0, stabilityScore - deltaSeconds * 35);

        var stable =
            isDrifting &&
            absoluteAngle is >= StableMinimumAngleDegrees and <= StableMaximumAngleDegrees &&
            speedKph >= 28 &&
            rearSlip is >= 0.12 and <= 1.35 &&
            throttleSmoothness >= 0.38 &&
            stabilityScore >= 58;
        if (stable)
        {
            stableDriftSeconds += deltaSeconds;
            bestStableDriftSeconds = Math.Max(
                bestStableDriftSeconds,
                stableDriftSeconds);
        }
        else if (isDrifting)
        {
            stableDriftSeconds = Math.Max(
                0,
                stableDriftSeconds - deltaSeconds * 1.8);
        }
        else
        {
            stableDriftSeconds = 0;
        }

        var phase = ResolvePhase(
            isDriving,
            speedKph,
            hasDriftEvidence,
            stable);
        var (guidance, tone) = Guidance(
            isDriving,
            speedKph,
            absoluteAngle,
            rearSlip,
            rearLongitudinalSlip,
            throttle,
            frame.Normalized.HandBrakeRatio,
            throttleSmoothness,
            steeringCoordination,
            stable);
        var resolvedGear = gearDisplay.Resolve(
            frame.Raw.Gear,
            frame.ArrivalTime,
            isDriving);

        if (!isDriving)
        {
            ResetLiveAttempt();
            gearDisplay.Reset();
        }

        return new DriftHudState(
            frame.ArrivalTime,
            frame.Source,
            SourceLabel(frame.Source),
            false,
            isDriving,
            phase,
            PhaseLabel(phase),
            guidance,
            tone,
            (int)Math.Round(speedKph),
            resolvedGear.ForwardGear,
            resolvedGear.Display,
            driftAngle,
            yawRate,
            steering,
            throttle,
            Math.Clamp(frame.Normalized.BrakeRatio, 0, 1),
            Math.Clamp(frame.Normalized.ClutchRatio, 0, 1),
            Math.Clamp(frame.Normalized.HandBrakeRatio, 0, 1),
            frontSlip,
            rearSlip,
            rearLongitudinalSlip,
            isDrifting,
            currentDriftSeconds,
            stableDriftSeconds,
            bestStableDriftSeconds,
            Math.Clamp(stabilityScore, 0, 100),
            throttleSmoothness * 100,
            steeringCoordination * 100);
    }

    public void Reset()
    {
        previousAt = null;
        previousThrottle = 0;
        throttleChange = 0;
        bestStableDriftSeconds = 0;
        stabilityScore = 0;
        gearDisplay.Reset();
        ResetLiveAttempt();
    }

    private double DeltaSeconds(DateTimeOffset now)
    {
        var delta = previousAt is null
            ? 1d / 60
            : (now - previousAt.Value).TotalSeconds;
        previousAt = now;
        return double.IsFinite(delta)
            ? Math.Clamp(delta, 1d / 240, 0.2)
            : 1d / 60;
    }

    private void UpdateThrottleChange(double throttle, double deltaSeconds)
    {
        var changePerSecond = Math.Abs(throttle - previousThrottle) /
                              Math.Max(deltaSeconds, 1d / 240);
        previousThrottle = throttle;
        var normalizedChange = Math.Clamp(changePerSecond / 3.5, 0, 1);
        var alpha = 1 - Math.Exp(-deltaSeconds / 0.28);
        throttleChange += (normalizedChange - throttleChange) * alpha;
    }

    private void UpdateDriftState(bool hasEvidence, double deltaSeconds)
    {
        if (hasEvidence)
        {
            driftEvidenceSeconds += deltaSeconds;
            driftLossSeconds = 0;
            if (isDrifting || driftEvidenceSeconds >= 0.12)
            {
                isDrifting = true;
                currentDriftSeconds += deltaSeconds;
            }
            return;
        }

        driftEvidenceSeconds = 0;
        if (!isDrifting) return;
        driftLossSeconds += deltaSeconds;
        if (driftLossSeconds < 0.45)
        {
            currentDriftSeconds += deltaSeconds;
            return;
        }

        isDrifting = false;
        currentDriftSeconds = 0;
        stableDriftSeconds = 0;
        driftLossSeconds = 0;
    }

    private void ResetLiveAttempt()
    {
        driftEvidenceSeconds = 0;
        driftLossSeconds = 0;
        currentDriftSeconds = 0;
        stableDriftSeconds = 0;
        isDrifting = false;
    }

    private DriftPracticePhase ResolvePhase(
        bool isDriving,
        double speedKph,
        bool hasDriftEvidence,
        bool stable)
    {
        if (!isDriving || speedKph < MinimumPracticeSpeedKph)
            return DriftPracticePhase.Waiting;
        if (stable)
            return DriftPracticePhase.Stable;
        if (driftLossSeconds > 0)
            return DriftPracticePhase.Recovering;
        if (isDrifting || hasDriftEvidence)
            return DriftPracticePhase.Building;
        return DriftPracticePhase.Ready;
    }

    private static double CalculateStability(
        double speedKph,
        double absoluteAngle,
        double rearSlip,
        double rearLongitudinalSlip,
        double throttleSmoothness,
        double steeringCoordination,
        double yawRate)
    {
        var angleScore = Math.Clamp(
            1 - Math.Abs(absoluteAngle - 28) / 28,
            0,
            1);
        var speedScore = Math.Clamp((speedKph - 20) / 35, 0, 1);
        var rearSlipScore = rearSlip switch
        {
            < 0.12 => rearSlip / 0.12,
            <= 0.9 => 1,
            _ => Math.Clamp(1 - (rearSlip - 0.9) / 1.1, 0, 1)
        };
        var wheelSpinScore = Math.Clamp(
            1 - Math.Max(0, rearLongitudinalSlip - 0.65) / 1.2,
            0,
            1);
        var yawScore = Math.Clamp(
            1 - Math.Max(0, Math.Abs(yawRate) - 80) / 110,
            0,
            1);
        return 100 * (
            angleScore * 0.27 +
            speedScore * 0.12 +
            rearSlipScore * 0.18 +
            wheelSpinScore * 0.10 +
            throttleSmoothness * 0.16 +
            steeringCoordination * 0.10 +
            yawScore * 0.07);
    }

    private static double SteeringCoordination(
        double driftAngleDegrees,
        double steering)
    {
        if (Math.Abs(driftAngleDegrees) < 8)
            return Math.Abs(steering) <= 0.25 ? 1 : 0.55;
        if (Math.Abs(steering) < 0.06)
            return 0.58;
        return Math.Sign(driftAngleDegrees) == Math.Sign(steering)
            ? 1
            : 0.2;
    }

    private static (string Text, DriftGuidanceTone Tone) Guidance(
        bool isDriving,
        double speedKph,
        double absoluteAngle,
        double rearSlip,
        double rearLongitudinalSlip,
        double throttle,
        double handBrake,
        double throttleSmoothness,
        double steeringCoordination,
        bool stable)
    {
        if (!isDriving)
            return ("等待车辆行驶；所有提示均由本车遥测推导", DriftGuidanceTone.Neutral);
        if (speedKph < 25)
            return ("先把车速稳定在 30 km/h 左右，再练习起漂", DriftGuidanceTone.Neutral);
        if (handBrake > 0.35 && absoluteAngle >= 10)
            return ("角度已经建立，及时松开手刹并用油门维持", DriftGuidanceTone.Warning);
        if (absoluteAngle > 55)
            return ("侧滑角偏大，轻收油并顺势修正方向", DriftGuidanceTone.Warning);
        if (rearLongitudinalSlip > 1.05 && throttle > 0.68)
            return ("后轮空转偏多，稍缓油门让车辆继续向前", DriftGuidanceTone.Warning);
        if (rearSlip < 0.10 || absoluteAngle < MinimumDriftAngleDegrees)
            return ("逐步建立侧滑角，避免一次性大幅打方向", DriftGuidanceTone.Neutral);
        if (throttleSmoothness < 0.42)
            return ("油门变化偏快，尝试更连续地补油", DriftGuidanceTone.Warning);
        if (steeringCoordination < 0.45)
            return ("方向修正与侧滑变化不同步，减少来回修正", DriftGuidanceTone.Warning);
        if (stable)
            return ("角度、车速和油门较稳定，继续保持", DriftGuidanceTone.Positive);
        return ("保持视线看向出弯方向，平顺修正角度", DriftGuidanceTone.Neutral);
    }

    private static string PhaseLabel(DriftPracticePhase phase) => phase switch
    {
        DriftPracticePhase.Waiting => "等待起步",
        DriftPracticePhase.Ready => "准备起漂",
        DriftPracticePhase.Building => "建立与保持",
        DriftPracticePhase.Stable => "稳定漂移",
        DriftPracticePhase.Recovering => "衔接恢复",
        _ => "漂移练习"
    };

    private static string SourceLabel(TelemetrySourceKind source) => source switch
    {
        TelemetrySourceKind.Live => "LIVE",
        TelemetrySourceKind.Replay => "REPLAY",
        _ => "DEMO / REPLAY"
    };

    private static double MeanAbsolute(float first, float second) =>
        (Math.Abs(Finite(first)) + Math.Abs(Finite(second))) / 2;

    private static double Finite(float value) =>
        float.IsFinite(value) ? value : 0;
}
