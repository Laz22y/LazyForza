using System.Globalization;

namespace LazyForza.Domain;

public static class VehicleTuneCompatibility
{
    public const double MaximumCompatibleGearRatioDifference = 0.045;
    public const double MaximumCompatiblePowerDifference = 0.12;
    public const double MaximumCompatibleTorqueDifference = 0.12;
    public const int MaximumCompatiblePeakRpmDifference = 600;

    public static bool HasSameBaseConfiguration(
        VehicleProfileFingerprint left,
        VehicleProfileFingerprint right) =>
        left.CarOrdinal == right.CarOrdinal &&
        left.CarClass == right.CarClass &&
        left.PerformanceIndex == right.PerformanceIndex &&
        left.DrivetrainType == right.DrivetrainType &&
        left.NumCylinders == right.NumCylinders &&
        Math.Abs(left.RoundedMaxRpm - right.RoundedMaxRpm) <= 200;

    public static bool AreCompatible(
        VehicleProfileFingerprint left,
        VehicleProfileFingerprint right)
    {
        if (!HasSameBaseConfiguration(left, right)) return false;
        if (!CompatibleCurve(left.CurveSignature, right.CurveSignature)) return false;

        var leftGears = ParseGearSignature(left.GearSlopeSignature);
        var rightGears = ParseGearSignature(right.GearSlopeSignature);
        if (leftGears.Count == 0 || rightGears.Count == 0)
            return string.Equals(
                left.GearSlopeSignature,
                right.GearSlopeSignature,
                StringComparison.Ordinal);

        var overlap = leftGears.Keys.Intersect(rightGears.Keys).ToArray();
        if (overlap.Length == 0)
        {
            // With no common gear there is no transmission evidence either way.
            // Keep the profiles separate until a later sample bridges the subsets.
            return false;
        }

        return overlap.All(gear =>
            RelativeDifference(leftGears[gear], rightGears[gear]) <=
            MaximumCompatibleGearRatioDifference);
    }

    public static IReadOnlyDictionary<int, double> ParseGearSignature(string signature)
    {
        if (string.IsNullOrWhiteSpace(signature)) return new Dictionary<int, double>();
        var result = new Dictionary<int, double>();
        foreach (var part in signature.Split('-', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.Length < 4 || part[0] != 'g') continue;
            var separator = part.IndexOf('_');
            if (separator <= 1 ||
                !int.TryParse(
                    part.AsSpan(1, separator - 1),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var gear) ||
                !double.TryParse(
                    part.AsSpan(separator + 1),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var slope) ||
                gear <= 0 ||
                !double.IsFinite(slope) ||
                slope <= 0)
                continue;
            result[gear] = slope;
        }

        return result;
    }

    private static bool CompatibleCurve(string left, string right)
    {
        if (!TryParseCurve(left, out var leftCurve) ||
            !TryParseCurve(right, out var rightCurve))
            return string.Equals(left, right, StringComparison.Ordinal);

        return RelativeDifference(leftCurve.PeakPower, rightCurve.PeakPower) <=
               MaximumCompatiblePowerDifference &&
               RelativeDifference(leftCurve.PeakTorque, rightCurve.PeakTorque) <=
               MaximumCompatibleTorqueDifference &&
               Math.Abs(leftCurve.PeakPowerRpm - rightCurve.PeakPowerRpm) <=
               MaximumCompatiblePeakRpmDifference;
    }

    private static bool TryParseCurve(string signature, out CurveSignature curve)
    {
        curve = default;
        var parts = signature.Split('_', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3 ||
            !TryPrefixedNumber(parts[0], 'p', out var power) ||
            !TryPrefixedNumber(parts[1], 't', out var torque) ||
            !TryPrefixedNumber(parts[2], 'r', out var rpm))
            return false;
        curve = new CurveSignature(power, torque, rpm);
        return true;
    }

    private static bool TryPrefixedNumber(string value, char prefix, out double number)
    {
        number = 0;
        return value.Length > 1 &&
               value[0] == prefix &&
               double.TryParse(
                   value.AsSpan(1),
                   NumberStyles.Float,
                   CultureInfo.InvariantCulture,
                   out number) &&
               double.IsFinite(number);
    }

    private static double RelativeDifference(double left, double right)
    {
        var scale = Math.Max(Math.Abs(left), Math.Abs(right));
        return scale <= double.Epsilon ? 0 : Math.Abs(left - right) / scale;
    }

    private readonly record struct CurveSignature(
        double PeakPower,
        double PeakTorque,
        double PeakPowerRpm);
}
