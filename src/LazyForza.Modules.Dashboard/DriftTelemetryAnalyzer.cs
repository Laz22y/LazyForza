using LazyForza.Domain;

namespace LazyForza.Modules.DriftDashboard;

public sealed class DriftTelemetryAnalyzer
{
    private const double MinimumPracticeSpeedKph = 20;
    private const double MinimumDriftAngleDegrees = 7;
    private const double StableMaximumAngleDegrees = 55;
    private const double MaximumUsefulAngleDegrees = 70;
    private const double SpinCautionThreshold = 0.32;
    private const double SpinCriticalThreshold = 0.68;
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
    private double spinRisk;
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

        var handBrake = Math.Clamp(frame.Normalized.HandBrakeRatio, 0, 1);
        var instantSpinRisk = isDrifting
            ? CalculateSpinRisk(
                speedKph,
                absoluteAngle,
                rearLongitudinalSlip,
                throttle,
                steeringCoordination,
                yawRate,
                handBrake)
            : 0;
        var spinRiskTimeConstant = instantSpinRisk > spinRisk ? 0.10 : 0.48;
        var spinRiskAlpha = 1 - Math.Exp(-deltaSeconds / spinRiskTimeConstant);
        spinRisk += (instantSpinRisk - spinRisk) * spinRiskAlpha;
        if (!isDrifting)
            spinRisk = Math.Max(0, spinRisk - deltaSeconds * 1.8);
        var spinRiskLevel = ResolveSpinRiskLevel(spinRisk);

        var instantStability = CalculateControlReserve(
            speedKph,
            absoluteAngle,
            rearSlip,
            rearLongitudinalSlip,
            throttleSmoothness,
            steeringCoordination,
            yawRate,
            spinRisk);
        var stabilityAlpha = 1 - Math.Exp(-deltaSeconds / 0.32);
        stabilityScore += (instantStability - stabilityScore) * stabilityAlpha;
        if (!isDrifting)
            stabilityScore = Math.Max(0, stabilityScore - deltaSeconds * 35);

        var stable =
            isDrifting &&
            absoluteAngle is >= MinimumDriftAngleDegrees and <= StableMaximumAngleDegrees &&
            speedKph >= 24 &&
            rearSlip is >= 0.10 and <= 1.45 &&
            throttleSmoothness >= 0.30 &&
            spinRiskLevel == DriftSpinRiskLevel.Safe &&
            stabilityScore >= 55;
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
            spinRiskLevel,
            stable);
        var resolvedGear = gearDisplay.Resolve(
            frame.Raw.Gear,
            frame.ArrivalTime,
            isDriving);
        var (steeringCue, steeringCueStrength) = SteeringRecommendation(
            driftAngle,
            steering,
            spinRisk,
            isDrifting);
        var gearCue = GearRecommendation(
            resolvedGear.ForwardGear,
            isDrifting,
            absoluteAngle,
            rearSlip,
            rearLongitudinalSlip,
            throttle,
            frame.Normalized.RpmRatio,
            spinRisk);
        var angleScorePotential = AngleScorePotential(
            absoluteAngle,
            isDrifting);
        var canBuildAngle =
            stable &&
            spinRisk < 0.22 &&
            absoluteAngle < 50 &&
            rearLongitudinalSlip < 0.9;

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
            spinRiskLevel,
            Math.Clamp(spinRisk, 0, 1),
            steeringCue,
            steeringCueStrength,
            gearCue,
            angleScorePotential,
            canBuildAngle,
            (int)Math.Round(speedKph),
            resolvedGear.ForwardGear,
            resolvedGear.Display,
            driftAngle,
            yawRate,
            steering,
            throttle,
            Math.Clamp(frame.Normalized.BrakeRatio, 0, 1),
            Math.Clamp(frame.Normalized.ClutchRatio, 0, 1),
            handBrake,
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
        spinRisk = 0;
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
        DriftSpinRiskLevel spinRiskLevel,
        bool stable)
    {
        if (!isDriving || speedKph < MinimumPracticeSpeedKph)
            return DriftPracticePhase.Waiting;
        if (spinRiskLevel == DriftSpinRiskLevel.Critical)
            return DriftPracticePhase.Recovering;
        if (stable)
            return DriftPracticePhase.Stable;
        if (driftLossSeconds > 0)
            return DriftPracticePhase.Recovering;
        if (isDrifting || hasDriftEvidence)
            return DriftPracticePhase.Building;
        return DriftPracticePhase.Ready;
    }

    private static double CalculateControlReserve(
        double speedKph,
        double absoluteAngle,
        double rearSlip,
        double rearLongitudinalSlip,
        double throttleSmoothness,
        double steeringCoordination,
        double yawRate,
        double spinRisk)
    {
        var angleControlScore = absoluteAngle <= 38
            ? 1
            : Math.Clamp(1 - (absoluteAngle - 38) / 28, 0, 1);
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
        var baseReserve = 100 * (
            angleControlScore * 0.16 +
            speedScore * 0.06 +
            rearSlipScore * 0.13 +
            wheelSpinScore * 0.18 +
            throttleSmoothness * 0.12 +
            steeringCoordination * 0.18 +
            yawScore * 0.17);
        var spinCeiling = 100 * (1 - spinRisk * 0.92);
        return Math.Min(baseReserve, spinCeiling);
    }

    private static double CalculateSpinRisk(
        double speedKph,
        double absoluteAngle,
        double rearLongitudinalSlip,
        double throttle,
        double steeringCoordination,
        double yawRate,
        double handBrake)
    {
        var angleRisk = Math.Clamp((absoluteAngle - 40) / 25, 0, 1);
        var allowedYawRate = Math.Clamp(58 + speedKph * 0.42, 68, 118);
        var yawRisk = Math.Clamp(
            (Math.Abs(yawRate) - allowedYawRate) / 95,
            0,
            1);
        var wheelSpinRisk = Math.Clamp(
            (rearLongitudinalSlip - 0.70) / 1.0,
            0,
            1) * (0.45 + throttle * 0.55);
        var steeringRisk = Math.Clamp(
            (0.58 - steeringCoordination) / 0.58,
            0,
            1);
        var handBrakeRisk = absoluteAngle >= 12
            ? Math.Clamp((handBrake - 0.28) / 0.62, 0, 1)
            : 0;
        var primaryRisk = Math.Max(
            Math.Max(angleRisk, yawRisk),
            Math.Max(wheelSpinRisk, steeringRisk));
        var supportingRisk =
            (angleRisk + yawRisk + wheelSpinRisk + steeringRisk + handBrakeRisk) / 5;
        var stackedRisk =
            angleRisk >= 0.45 && (yawRisk >= 0.35 || steeringRisk >= 0.45)
                ? 0.14
                : 0;
        return Math.Clamp(
            primaryRisk * 0.74 + supportingRisk * 0.26 + stackedRisk,
            0,
            1);
    }

    private static DriftSpinRiskLevel ResolveSpinRiskLevel(double risk) =>
        risk >= SpinCriticalThreshold
            ? DriftSpinRiskLevel.Critical
            : risk >= SpinCautionThreshold
                ? DriftSpinRiskLevel.Caution
                : DriftSpinRiskLevel.Safe;

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

    private static (DriftSteeringCue Cue, double Strength) SteeringRecommendation(
        double driftAngleDegrees,
        double steering,
        double spinRisk,
        bool isDrifting)
    {
        if (!isDrifting || Math.Abs(driftAngleDegrees) < MinimumDriftAngleDegrees)
            return (DriftSteeringCue.Hold, 0);

        var direction = Math.Sign(driftAngleDegrees);
        var targetMagnitude = Math.Clamp(
            0.14 + Math.Abs(driftAngleDegrees) / MaximumUsefulAngleDegrees * 0.58 +
            spinRisk * 0.12,
            0.18,
            0.82);
        var steeringDelta = direction * targetMagnitude - steering;
        if (Math.Abs(steeringDelta) < 0.11)
            return (DriftSteeringCue.Hold, 0);
        return (
            steeringDelta < 0 ? DriftSteeringCue.Left : DriftSteeringCue.Right,
            Math.Clamp(Math.Abs(steeringDelta) / 0.58, 0.25, 1));
    }

    private static DriftGearCue GearRecommendation(
        int? forwardGear,
        bool isDrifting,
        double absoluteAngle,
        double rearSlip,
        double rearLongitudinalSlip,
        double throttle,
        double rpmRatio,
        double spinRisk)
    {
        if (!isDrifting || forwardGear is not int gear)
            return DriftGearCue.Hold;

        var rpmSupportsUpshift = rpmRatio <= 0 || rpmRatio >= 0.55;
        if (gear < 10 && rpmSupportsUpshift &&
            ((rearLongitudinalSlip >= 1.0 && throttle >= 0.62) ||
             spinRisk >= 0.52))
            return DriftGearCue.ShiftUp;

        if (gear > 1 &&
            rpmRatio is > 0 and < 0.48 &&
            spinRisk < 0.20 &&
            absoluteAngle < 18 &&
            rearSlip < 0.32 &&
            rearLongitudinalSlip < 0.55 &&
            throttle >= 0.72)
            return DriftGearCue.ShiftDown;

        return DriftGearCue.Hold;
    }

    private static double AngleScorePotential(
        double absoluteAngle,
        bool isDrifting)
    {
        if (!isDrifting)
            return 0;
        var normalizedAngle = Math.Clamp(
            (absoluteAngle - MinimumDriftAngleDegrees) /
            (55 - MinimumDriftAngleDegrees),
            0,
            1);
        return 0.16 + Math.Pow(normalizedAngle, 0.85) * 0.84;
    }

    private static string PhaseLabel(DriftPracticePhase phase) => phase switch
    {
        DriftPracticePhase.Waiting => "等待起步",
        DriftPracticePhase.Ready => "准备起漂",
        DriftPracticePhase.Building => "保持漂移",
        DriftPracticePhase.Stable => "稳定漂移",
        DriftPracticePhase.Recovering => "防止 Spin",
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
