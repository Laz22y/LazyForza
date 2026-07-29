using LazyForza.Domain;

namespace LazyForza.Analysis;

public enum DrivingDynamicsLayer
{
    Default,
    Throttle,
    Brake,
    Steering,
    TireSlip,
    HandlingBalance,
    ExitWheelspin,
    BrakingInstability
}

public enum HandlingBalanceState
{
    Neutral,
    SuspectedUndersteer,
    SuspectedOversteer
}

public readonly record struct DrivingDynamicsPoint(
    double Intensity,
    double SignedValue,
    HandlingBalanceState Balance,
    bool IsAvailable);

public static class DrivingDynamicsAnalyzer
{
    public static bool RequiresExtendedTelemetry(DrivingDynamicsLayer layer) =>
        layer is not DrivingDynamicsLayer.Default
            and not DrivingDynamicsLayer.Throttle
            and not DrivingDynamicsLayer.Brake;

    public static DrivingDynamicsPoint Evaluate(
        LapSample sample,
        VehicleProfileFingerprint vehicle,
        DrivingDynamicsLayer layer)
    {
        if (layer == DrivingDynamicsLayer.Default)
            return new DrivingDynamicsPoint(0, 0, HandlingBalanceState.Neutral, true);
        if (layer == DrivingDynamicsLayer.Throttle)
            return Scalar(sample.Accel);
        if (layer == DrivingDynamicsLayer.Brake)
            return Scalar(sample.Brake);
        if (sample.Dynamics is not { } dynamics)
            return new DrivingDynamicsPoint(0, 0, HandlingBalanceState.Neutral, false);

        return layer switch
        {
            DrivingDynamicsLayer.Steering => new DrivingDynamicsPoint(
                Math.Clamp(Math.Abs(dynamics.Steering), 0, 1),
                Math.Clamp(dynamics.Steering, -1, 1),
                HandlingBalanceState.Neutral,
                true),
            DrivingDynamicsLayer.TireSlip => Scalar(
                NormalizeSlip(dynamics.TireCombinedSlip.MaxAbsolute)),
            DrivingDynamicsLayer.HandlingBalance => EvaluateBalance(sample, dynamics),
            DrivingDynamicsLayer.ExitWheelspin => EvaluateWheelspin(sample, vehicle, dynamics),
            DrivingDynamicsLayer.BrakingInstability => EvaluateBrakingInstability(sample, dynamics),
            _ => new DrivingDynamicsPoint(0, 0, HandlingBalanceState.Neutral, true)
        };
    }

    public static string LayerName(DrivingDynamicsLayer layer) => layer switch
    {
        DrivingDynamicsLayer.Default => "默认走线",
        DrivingDynamicsLayer.Throttle => "油门输入",
        DrivingDynamicsLayer.Brake => "刹车输入",
        DrivingDynamicsLayer.Steering => "方向输入",
        DrivingDynamicsLayer.TireSlip => "轮胎滑移强度",
        DrivingDynamicsLayer.HandlingBalance => "疑似转向不足 / 过度",
        DrivingDynamicsLayer.ExitWheelspin => "出弯空转",
        DrivingDynamicsLayer.BrakingInstability => "制动轮胎失稳",
        _ => layer.ToString()
    };

    private static DrivingDynamicsPoint EvaluateBalance(
        LapSample sample,
        LapDynamics dynamics)
    {
        if (sample.SpeedMps < 8 || Math.Abs(dynamics.Steering) < 0.08)
            return new DrivingDynamicsPoint(0, 0, HandlingBalanceState.Neutral, true);

        var front = MeanAbsolute(
            dynamics.TireSlipAngle.FrontLeft,
            dynamics.TireSlipAngle.FrontRight);
        var rear = MeanAbsolute(
            dynamics.TireSlipAngle.RearLeft,
            dynamics.TireSlipAngle.RearRight);
        var difference = front - rear;
        const double threshold = 0.035;
        if (difference > threshold)
            return new DrivingDynamicsPoint(
                Math.Clamp((difference - threshold) / 0.22, 0, 1),
                difference,
                HandlingBalanceState.SuspectedUndersteer,
                true);
        if (difference < -threshold)
            return new DrivingDynamicsPoint(
                Math.Clamp((-difference - threshold) / 0.22, 0, 1),
                difference,
                HandlingBalanceState.SuspectedOversteer,
                true);
        return new DrivingDynamicsPoint(0, difference, HandlingBalanceState.Neutral, true);
    }

    private static DrivingDynamicsPoint EvaluateWheelspin(
        LapSample sample,
        VehicleProfileFingerprint vehicle,
        LapDynamics dynamics)
    {
        if (sample.SpeedMps < 4 || sample.Accel < 0.55 || sample.Brake > 0.12)
            return new DrivingDynamicsPoint(0, 0, HandlingBalanceState.Neutral, true);

        var slip = vehicle.DrivetrainType switch
        {
            0 => Math.Max(
                Math.Abs(dynamics.TireSlipRatio.FrontLeft),
                Math.Abs(dynamics.TireSlipRatio.FrontRight)),
            1 => Math.Max(
                Math.Abs(dynamics.TireSlipRatio.RearLeft),
                Math.Abs(dynamics.TireSlipRatio.RearRight)),
            _ => dynamics.TireSlipRatio.MaxAbsolute
        };
        var intensity = Math.Clamp((slip - 0.12) / 0.65, 0, 1);
        return new DrivingDynamicsPoint(
            intensity,
            slip,
            HandlingBalanceState.Neutral,
            true);
    }

    private static DrivingDynamicsPoint EvaluateBrakingInstability(
        LapSample sample,
        LapDynamics dynamics)
    {
        if (sample.Brake < 0.25 || sample.SpeedMps < 5)
            return new DrivingDynamicsPoint(0, 0, HandlingBalanceState.Neutral, true);

        var combined = dynamics.TireCombinedSlip.MaxAbsolute;
        var leftRightImbalance = Math.Max(
            Math.Abs(Math.Abs(dynamics.TireCombinedSlip.FrontLeft) -
                     Math.Abs(dynamics.TireCombinedSlip.FrontRight)),
            Math.Abs(Math.Abs(dynamics.TireCombinedSlip.RearLeft) -
                     Math.Abs(dynamics.TireCombinedSlip.RearRight)));
        var evidence = Math.Max(
            (combined - 0.22) / 0.8,
            (leftRightImbalance - 0.12) / 0.55);
        return new DrivingDynamicsPoint(
            Math.Clamp(evidence, 0, 1),
            Math.Max(combined, leftRightImbalance),
            HandlingBalanceState.Neutral,
            true);
    }

    private static DrivingDynamicsPoint Scalar(double value) =>
        new(Math.Clamp(value, 0, 1), value, HandlingBalanceState.Neutral, true);

    private static double NormalizeSlip(double combinedSlip) =>
        Math.Clamp((Math.Abs(combinedSlip) - 0.04) / 0.95, 0, 1);

    private static double MeanAbsolute(double left, double right) =>
        (Math.Abs(left) + Math.Abs(right)) / 2;
}
