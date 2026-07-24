using LazyForza.Domain;

namespace LazyForza.Analysis;

public static class ShiftPointCalculator
{
    public static IReadOnlyList<ShiftTarget> Calculate(
        IReadOnlyList<EngineCurveBin> curve,
        IReadOnlyList<GearModel> gears,
        double engineMaxRpm,
        double rpmRiseRate = 2400,
        double totalLatencySeconds = 0.16)
    {
        if (curve.Count < 2 || gears.Count < 2 || engineMaxRpm <= 0)
        {
            return [];
        }

        var orderedCurve = curve.OrderBy(bin => bin.RpmCenter).ToArray();
        var orderedGears = gears.OrderBy(gear => gear.Gear).ToArray();
        var results = new List<ShiftTarget>();

        for (var index = 0; index < orderedGears.Length - 1; index++)
        {
            var current = orderedGears[index];
            var next = orderedGears[index + 1];
            if (next.Gear != current.Gear + 1 || current.RpmPerMeterPerSecond <= next.RpmPerMeterPerSecond)
            {
                continue;
            }

            var ratio = next.RpmPerMeterPerSecond / current.RpmPerMeterPerSecond;
            var minimum = Math.Max(orderedCurve[0].RpmCenter / ratio, orderedCurve[0].RpmCenter);
            var maximum = Math.Min(engineMaxRpm - 75, orderedCurve[^1].RpmCenter);
            double? target = null;
            var previousRpm = minimum;
            var previousDifference = Difference(previousRpm);

            for (var rpm = minimum + 25; rpm <= maximum; rpm += 25)
            {
                var difference = Difference(rpm);
                if (double.IsFinite(previousDifference) && double.IsFinite(difference) && previousDifference > 0 && difference <= 0)
                {
                    var fraction = previousDifference / (previousDifference - difference);
                    target = previousRpm + ((rpm - previousRpm) * fraction);
                    break;
                }

                previousRpm = rpm;
                previousDifference = difference;
            }

            var usedFallback = target is null;
            var targetRpm = target ?? Math.Max(minimum, Math.Min(engineMaxRpm - 100, maximum));
            var afterShiftRpm = targetRpm * ratio;
            var cue = Math.Clamp(targetRpm - (Math.Max(0, rpmRiseRate) * Math.Max(0, totalLatencySeconds)), minimum, targetRpm);
            results.Add(new ShiftTarget(
                current.Gear,
                next.Gear,
                targetRpm,
                cue,
                afterShiftRpm,
                Math.Min(Math.Min(current.Confidence, next.Confidence), CurveConfidence(targetRpm)),
                usedFallback));

            double Difference(double rpm)
            {
                var after = rpm * ratio;
                var currentTorque = InterpolateTorque(orderedCurve, rpm);
                var nextTorque = InterpolateTorque(orderedCurve, after);
                return currentTorque * current.RpmPerMeterPerSecond - nextTorque * next.RpmPerMeterPerSecond;
            }

            double CurveConfidence(double rpm)
            {
                var nearest = orderedCurve.MinBy(bin => Math.Abs(bin.RpmCenter - rpm));
                return nearest?.Confidence ?? 0;
            }
        }

        return results;
    }

    public static double InterpolateTorque(IReadOnlyList<EngineCurveBin> orderedCurve, double rpm)
    {
        if (orderedCurve.Count == 0 || rpm < orderedCurve[0].RpmCenter || rpm > orderedCurve[^1].RpmCenter)
        {
            return double.NaN;
        }

        for (var index = 1; index < orderedCurve.Count; index++)
        {
            var upper = orderedCurve[index];
            if (rpm > upper.RpmCenter) continue;
            var lower = orderedCurve[index - 1];
            var range = upper.RpmCenter - lower.RpmCenter;
            if (range <= 0) return lower.MedianTorqueNm;
            var fraction = (rpm - lower.RpmCenter) / range;
            return lower.MedianTorqueNm + ((upper.MedianTorqueNm - lower.MedianTorqueNm) * fraction);
        }

        return orderedCurve[^1].MedianTorqueNm;
    }
}

