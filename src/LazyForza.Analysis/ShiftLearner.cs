using LazyForza.Domain;

namespace LazyForza.Analysis;

public sealed record ShiftLearnerOptions(
    double MinimumAccel = 0.90,
    double MinimumSpeedMps = 10,
    double MaximumSlip = 0.35,
    double MaximumClutch = 0.08,
    int RpmBinSize = 200,
    int MinimumSamplesPerBin = 4,
    int MinimumReadyBins = 10,
    int MinimumGearSamples = 10,
    double MaximumIntervalSeconds = 0.25,
    double GearSlopeChangeRatio = 0.06,
    double PowerCurveChangeRatio = 0.18,
    double MinimumPowerChangeWatts = 15_000,
    int TuneMismatchSamples = 12);

public sealed class ShiftLearner
{
    private readonly ShiftLearnerOptions options;
    private readonly Dictionary<int, List<Sample>> bins = [];
    private readonly Dictionary<int, List<double>> gearSlopes = [];
    private readonly Dictionary<string, int> rejected = new(StringComparer.OrdinalIgnoreCase);
    private TelemetryFrame? previous;
    private VehicleProfileFingerprint? fingerprint;
    private VehicleProfileFingerprint? resolvedFingerprint;
    private LearningState state = LearningState.NotStarted;
    private int acceptedSamples;
    private DateTimeOffset? firstAcceptedAt;
    private DateTimeOffset? lastAcceptedAt;
    private long configurationRevision;
    private int tuneMismatchStreak;

    public ShiftLearner(ShiftLearnerOptions? options = null) => this.options = options ?? new ShiftLearnerOptions();

    public ShiftLearningSnapshot Snapshot => BuildSnapshot();

    public void Observe(TelemetryFrame frame)
    {
        if (frame.Raw.IsRaceOn != 1)
        {
            Reject("not-driving");
            previous = frame;
            return;
        }

        var incoming = VehicleProfileFingerprint.FromFrame(frame);
        if (fingerprint is null)
        {
            fingerprint = incoming;
            state = LearningState.Collecting;
            configurationRevision++;
        }
        else if (!SameBaseConfiguration(fingerprint, incoming))
        {
            Reset();
            fingerprint = incoming;
            state = LearningState.Collecting;
            configurationRevision++;
            Reject("configuration-changed");
            previous = frame;
            return;
        }

        if (!Accept(frame, out var reason))
        {
            Reject(reason);
            previous = frame;
            return;
        }

        if (LooksLikeDifferentTune(frame))
        {
            tuneMismatchStreak++;
            if (tuneMismatchStreak >= Math.Max(2, options.TuneMismatchSamples))
            {
                Reset();
                fingerprint = incoming;
                state = LearningState.Collecting;
                configurationRevision++;
                Reject("tune-signature-changed");
            }
            else
            {
                Reject("tune-change-probe");
            }

            previous = frame;
            return;
        }

        tuneMismatchStreak = 0;
        var raw = frame.Raw;
        var binCenter = (int)(Math.Round(raw.CurrentEngineRpm / options.RpmBinSize) * options.RpmBinSize);
        if (!bins.TryGetValue(binCenter, out var bucket))
        {
            bucket = [];
            bins[binCenter] = bucket;
        }

        AddBounded(bucket, new Sample(raw.Power, raw.Torque, raw.Boost), 256);
        var forwardGear = ForzaGear.ForwardNumber(raw.Gear)!.Value;
        if (!gearSlopes.TryGetValue(forwardGear, out var slopes))
        {
            slopes = [];
            gearSlopes[forwardGear] = slopes;
        }

        AddBounded(slopes, raw.CurrentEngineRpm / raw.Speed, 512);
        acceptedSamples++;
        firstAcceptedAt ??= frame.ArrivalTime;
        lastAcceptedAt = frame.ArrivalTime;
        previous = frame;
        state = BuildCurve().Count(bin => bin.SampleCount >= options.MinimumSamplesPerBin) >= options.MinimumReadyBins &&
                BuildGears().Count >= 2
            ? LearningState.Ready
            : LearningState.Collecting;
    }

    public void Reset()
    {
        bins.Clear();
        gearSlopes.Clear();
        rejected.Clear();
        previous = null;
        fingerprint = null;
        resolvedFingerprint = null;
        state = LearningState.NotStarted;
        acceptedSamples = 0;
        firstAcceptedAt = null;
        lastAcceptedAt = null;
        tuneMismatchStreak = 0;
        configurationRevision++;
    }

    private bool Accept(TelemetryFrame frame, out string reason)
    {
        var raw = frame.Raw;
        if (raw.IsRaceOn != 1) { reason = "not-driving"; return false; }
        if (frame.Normalized.AccelRatio < options.MinimumAccel) { reason = "low-throttle"; return false; }
        if (raw.Speed < options.MinimumSpeedMps) { reason = "low-speed"; return false; }
        if (ForzaGear.ForwardNumber(raw.Gear) is null) { reason = "unsupported-gear"; return false; }
        if (frame.Normalized.ClutchRatio > options.MaximumClutch) { reason = "clutch"; return false; }
        if (raw.TireSlipRatio.MaxAbsolute > options.MaximumSlip || raw.TireCombinedSlip.MaxAbsolute > options.MaximumSlip)
        { reason = "slip"; return false; }
        if (raw.SmashableMass > 0 || raw.SmashableVelDiff > 0.5f) { reason = "collision"; return false; }
        if (previous is null) { reason = "warmup"; return false; }
        var deltaMs = unchecked(raw.TimestampMS - previous.Raw.TimestampMS);
        if (deltaMs == 0 || deltaMs / 1000.0 > options.MaximumIntervalSeconds) { reason = "interval"; return false; }
        if (previous.Raw.Gear != raw.Gear) { reason = "shift-transition"; return false; }
        if (Math.Abs(previous.Raw.Position.X - raw.Position.X) > 25 || Math.Abs(previous.Raw.Position.Z - raw.Position.Z) > 25)
        { reason = "position-jump"; return false; }
        reason = string.Empty;
        return true;
    }

    private ShiftLearningSnapshot BuildSnapshot()
    {
        var curve = BuildCurve();
        var gears = BuildGears();
        var readyBins = curve.Count(bin => bin.SampleCount >= options.MinimumSamplesPerBin);
        var progress = Math.Clamp(0.75 * readyBins / options.MinimumReadyBins + 0.25 * Math.Min(1, gears.Count / 3d), 0, 1);
        var targets = ShiftPointCalculator.Calculate(curve, gears, fingerprint?.RoundedMaxRpm ?? 0);
        var confidence = targets.Count > 0 ? targets.Average(target => target.Confidence) : curve.Count > 0 ? curve.Average(bin => bin.Confidence) * 0.5 : 0;
        var requiredSamples = Math.Max(options.MinimumReadyBins * options.MinimumSamplesPerBin, options.MinimumGearSamples * 2);
        var elapsed = firstAcceptedAt is not null && lastAcceptedAt is not null ? (lastAcceptedAt.Value - firstAcceptedAt.Value).TotalSeconds : 0;
        var rate = elapsed > 1 ? acceptedSamples / elapsed : 0;
        var remaining = Math.Max(0, requiredSamples - acceptedSamples);
        double? estimatedSeconds = rate > 0.5 && remaining > 0 ? remaining / rate : null;
        var effectiveState = state == LearningState.Collecting && readyBins > 0 && progress < 0.2
            ? LearningState.Insufficient
            : state;
        return new ShiftLearningSnapshot(
            effectiveState,
            progress,
            confidence,
            BuildResolvedFingerprint(fingerprint, curve, gears, readyBins),
            curve,
            gears,
            targets,
            new Dictionary<string, int>(rejected),
            effectiveState switch
            {
                LearningState.Ready => "换挡目标已就绪。继续驾驶可提高稳定性。",
                LearningState.Insufficient => "样本不足，继续完成有效加速。",
                _ => "正在收集有效加速数据。"
            })
        {
            AcceptedSamples = acceptedSamples,
            ReadyBins = readyBins,
            RequiredBins = options.MinimumReadyBins,
            ReadyGears = gears.Count,
            EstimatedSecondsRemaining = estimatedSeconds,
            Guidance = effectiveState == LearningState.Ready
                ? "继续正常驾驶即可。更换车辆、发动机或传动配置后需要重新学习。"
                : "在平直道路上以 90% 以上油门，从中低转速连续加速并跨过至少两个挡位。通常完成 2–3 次即可；打滑、碰撞、离合和低油门数据不会计入。",
            ConfigurationRevision = configurationRevision
        };
    }

    private VehicleProfileFingerprint? BuildResolvedFingerprint(
        VehicleProfileFingerprint? baseFingerprint,
        IReadOnlyList<EngineCurveBin> curve,
        IReadOnlyList<GearModel> gears,
        int readyBins)
    {
        if (baseFingerprint is null) return null;
        if (resolvedFingerprint is not null) return resolvedFingerprint;
        if (readyBins < options.MinimumReadyBins || gears.Count < 2) return baseFingerprint;

        var gearSignature = string.Join(
            '-',
            gears.Select(gear =>
                $"g{gear.Gear}_{Math.Round(gear.RpmPerMeterPerSecond / 2d, MidpointRounding.AwayFromZero) * 2:0}"));
        var peakPower = curve.Count == 0 ? 0 : curve.Max(bin => Math.Max(0, bin.MedianPowerWatts));
        var peakTorque = curve.Count == 0 ? 0 : curve.Max(bin => Math.Max(0, bin.MedianTorqueNm));
        var peakPowerRpm = curve
            .OrderByDescending(bin => bin.MedianPowerWatts)
            .Select(bin => bin.RpmCenter)
            .FirstOrDefault();
        var curveSignature =
            $"p{Math.Round(peakPower / 5_000d, MidpointRounding.AwayFromZero):0}_" +
            $"t{Math.Round(peakTorque / 10d, MidpointRounding.AwayFromZero):0}_" +
            $"r{Math.Round(peakPowerRpm / 200d, MidpointRounding.AwayFromZero) * 200:0}";
        resolvedFingerprint = baseFingerprint with
        {
            GearSlopeSignature = gearSignature,
            CurveSignature = curveSignature
        };
        return resolvedFingerprint;
    }

    private IReadOnlyList<EngineCurveBin> BuildCurve() => bins
        .OrderBy(pair => pair.Key)
        .Select(pair =>
        {
            var power = pair.Value.Select(sample => sample.Power).ToArray();
            var torque = pair.Value.Select(sample => sample.Torque).ToArray();
            var countConfidence = Math.Clamp(pair.Value.Count / (options.MinimumSamplesPerBin * 2d), 0, 1);
            var spread = RobustStatistics.MedianAbsoluteDeviation(torque);
            var spreadConfidence = double.IsFinite(spread) ? 1 / (1 + (spread / 100)) : 0;
            return new EngineCurveBin(
                pair.Key,
                pair.Value.Count,
                RobustStatistics.Median(power),
                RobustStatistics.Median(torque),
                RobustStatistics.Median(pair.Value.Select(sample => sample.Boost)),
                spread,
                countConfidence * spreadConfidence);
        })
        .ToArray();

    private IReadOnlyList<GearModel> BuildGears() => gearSlopes
        .Where(pair => pair.Value.Count >= options.MinimumGearSamples)
        .OrderBy(pair => pair.Key)
        .Select(pair =>
        {
            var median = RobustStatistics.Median(pair.Value);
            var mad = RobustStatistics.MedianAbsoluteDeviation(pair.Value);
            var confidence = Math.Clamp(pair.Value.Count / 40d, 0, 1) * (1 / (1 + mad / Math.Max(1, median) * 20));
            return new GearModel(pair.Key, median, pair.Value.Count, confidence);
        })
        .ToArray();

    private bool LooksLikeDifferentTune(TelemetryFrame frame)
    {
        var raw = frame.Raw;
        var forwardGear = ForzaGear.ForwardNumber(raw.Gear);
        if (forwardGear is int gear &&
            gearSlopes.TryGetValue(gear, out var slopes) &&
            slopes.Count >= options.MinimumGearSamples)
        {
            var expected = RobustStatistics.Median(slopes);
            var observed = raw.CurrentEngineRpm / Math.Max(0.1, raw.Speed);
            if (expected > 1 &&
                Math.Abs(observed - expected) / expected >= options.GearSlopeChangeRatio)
                return true;
        }

        var binCenter = (int)(Math.Round(raw.CurrentEngineRpm / options.RpmBinSize) * options.RpmBinSize);
        if (!bins.TryGetValue(binCenter, out var samples) ||
            samples.Count < options.MinimumSamplesPerBin)
            return false;

        var expectedPower = RobustStatistics.Median(samples.Select(sample => sample.Power));
        var powerDifference = Math.Abs(raw.Power - expectedPower);
        return Math.Abs(expectedPower) >= options.MinimumPowerChangeWatts &&
               powerDifference >= options.MinimumPowerChangeWatts &&
               powerDifference / Math.Abs(expectedPower) >= options.PowerCurveChangeRatio;
    }

    private static bool SameBaseConfiguration(VehicleProfileFingerprint left, VehicleProfileFingerprint right) =>
        left.CarOrdinal == right.CarOrdinal && left.CarClass == right.CarClass &&
        left.PerformanceIndex == right.PerformanceIndex && left.DrivetrainType == right.DrivetrainType &&
        left.NumCylinders == right.NumCylinders && Math.Abs(left.RoundedMaxRpm - right.RoundedMaxRpm) <= 100;

    private void Reject(string reason) => rejected[reason] = rejected.GetValueOrDefault(reason) + 1;

    private static void AddBounded<T>(List<T> list, T value, int maximum)
    {
        list.Add(value);
        if (list.Count > maximum) list.RemoveRange(0, list.Count - maximum);
    }

    private readonly record struct Sample(double Power, double Torque, double Boost);
}
