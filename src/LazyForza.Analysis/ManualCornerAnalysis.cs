using System.Globalization;
using LazyForza.Domain;

namespace LazyForza.Analysis;

public sealed record ManualCorner(string Name, double StartS, double EndS);
public sealed record CornerLapMetrics(double Seconds, double MinimumSpeedKph, double MinimumSpeedS,
    double? BrakeStartS, double? ThrottleRecoveryS);
public enum CornerEvidence { Sufficient, Partial, Insufficient, Incompatible }
public sealed record ManualCornerResult(ManualCorner Corner, CornerEvidence Evidence, string Message,
    CornerLapMetrics? Selected = null, CornerLapMetrics? Reference = null);
public sealed record CornerDifference(string Text, double ProgressMeters);

/// <summary>Distance-aligned observations of two recorded laps. Does not estimate potential time gains.</summary>
public static class ManualCornerAnalyzer
{
    public const double MaximumSampleGapMeters = 25;
    public const double MaximumSampleGapSeconds = .75;
    private const string MissingSamples = "样本不足或不连续：需要覆盖区间边界，至少 8 个样本，相邻不超过 25 米及 0.75 秒；不外推缺失数据。";

    /// <summary>Inspect a lap's own distance samples without requiring comparison metadata.</summary>
    public static ManualCornerResult Analyze(LapRecord lap, ManualCorner corner)
    {
        if (!double.IsFinite(lap.TotalSeconds) || lap.TotalSeconds <= 0)
            return new(corner, CornerEvidence.Insufficient, "本圈缺少有效的计时数据，无法计算弯道指标。");
        var length = lap.Samples.Where(sample => double.IsFinite(sample.S)).Select(sample => sample.S).DefaultIfEmpty(0).Max();
        if (!IsValid(corner, length))
            return new(corner, CornerEvidence.Insufficient, "区间需位于本圈已记录的距离范围内，且至少长 30 米；跨终点弯请分开标记。");
        var samples = Window(lap, corner);
        if (samples is null) return new(corner, CornerEvidence.Insufficient, MissingSamples);
        var grid = samples.Select(sample => sample.S).Where(s => s >= corner.StartS && s <= corner.EndS)
            .Append(corner.StartS).Append(corner.EndS).Distinct().Order().ToArray();
        var metrics = Metrics(samples, grid);
        return new(corner, metrics.BrakeStartS is not null && metrics.ThrottleRecoveryS is not null
            ? CornerEvidence.Sufficient : CornerEvidence.Partial, "按本圈已记录的距离与输入计算。", metrics);
    }

    public static string? ComparisonEligibility(TrackTemplate? track, LapSummary lap)
    {
        if (track is null) return "缺少保存的赛道信息，无法确认两圈使用同一路线。";
        if (track.Id == Guid.Empty || lap.TrackId != track.Id || lap.Direction != track.Direction || lap.SectorSchemaVersion <= 0)
            return "赛道、方向或分段版本不兼容。";
        if (string.IsNullOrWhiteSpace(lap.TrackRevision))
            return "缺少路线修订信息，无法确认两圈使用同一路线。";
        if (lap.TrackRevision != LapTrackRevision.Create(track))
            return "记录的路线修订与当前赛道不一致。";
        if (!lap.IsValid || !double.IsFinite(lap.TotalSeconds) || lap.TotalSeconds <= 0)
            return "仅比较已保存的有效完整圈。";
        if (lap.Vehicle.CarOrdinal <= 0 || lap.Vehicle.CarClass < 0 || lap.Vehicle.PerformanceIndex <= 0 ||
            lap.Vehicle.RoundedMaxRpm <= 0 || lap.Vehicle.DrivetrainType is < 0 or > 2 || lap.Vehicle.NumCylinders < 0 ||
            string.IsNullOrWhiteSpace(lap.Vehicle.GearSlopeSignature) || string.IsNullOrWhiteSpace(lap.Vehicle.CurveSignature))
            return "缺少完整车辆信息，无法确认车辆条件是否兼容。";
        return null;
    }

    public static string? Compatibility(TrackTemplate track, LapSummary selected, LapSummary reference)
    {
        if (selected.Id == reference.Id) return "请选择另一条已记录的完整参考圈。";
        if (ComparisonEligibility(track, selected) is { } selectedReason) return selectedReason;
        if (ComparisonEligibility(track, reference) is { } referenceReason) return referenceReason;
        if (selected.SectorSchemaVersion != reference.SectorSchemaVersion)
            return "赛道、方向或分段版本不兼容。";
        if (!VehicleTuneCompatibility.AreCompatible(selected.Vehicle, reference.Vehicle))
            return "车辆型号、性能等级、PI、驱动或可观察配置不兼容。";
        return null;
    }

    public static bool IsValid(ManualCorner corner, double trackLength) =>
        !string.IsNullOrWhiteSpace(corner.Name) && corner.Name.Length <= 40 &&
        double.IsFinite(trackLength) && double.IsFinite(corner.StartS) && double.IsFinite(corner.EndS) &&
        corner.StartS >= 0 && corner.EndS <= trackLength && corner.EndS - corner.StartS >= 30;

    public static ManualCornerResult Compare(TrackTemplate track, LapRecord selected, LapRecord reference, ManualCorner corner)
    {
        if (Compatibility(track, LapSummary.FromRecord(selected), LapSummary.FromRecord(reference)) is { } reason)
            return new(corner, CornerEvidence.Incompatible, reason);
        if (!IsValid(corner, track.LengthMeters))
            return new(corner, CornerEvidence.Insufficient, "区间需位于赛道内、起点小于终点且至少长 30 米；跨终点弯请分开标记。");
        var left = Window(selected, corner);
        var right = Window(reference, corner);
        if (left is null || right is null)
            return new(corner, CornerEvidence.Insufficient, MissingSamples);

        // Both series are linearly interpolated on the same distance grid. Include original
        // knots so speed minima are preserved instead of being lost to uniform resampling.
        var grid = left.Concat(right).Select(sample => sample.S)
            .Where(s => s >= corner.StartS && s <= corner.EndS)
            .Append(corner.StartS).Append(corner.EndS).Distinct().Order().ToArray();
        var a = Metrics(left, grid);
        var b = Metrics(right, grid);
        var complete = a.BrakeStartS is not null && b.BrakeStartS is not null &&
                       a.ThrottleRecoveryS is not null && b.ThrottleRecoveryS is not null;
        return new(corner, complete ? CornerEvidence.Sufficient : CornerEvidence.Partial,
            complete ? "按同一赛道距离比较，仅描述差异，不推断提速原因。" :
                "部分输入事件证据不足；未识别到稳定阈值跨越的位置显示为 —，不据此推断未刹车或未补油。", a, b);
    }

    public static IReadOnlyList<CornerDifference> Describe(IEnumerable<ManualCornerResult> results)
    {
        var descriptions = new List<CornerDifference>();
        foreach (var result in results.Where(item => item.Selected is not null && item.Reference is not null)
                     .OrderByDescending(item => Math.Abs(item.Selected!.Seconds - item.Reference!.Seconds)))
        {
            var a = result.Selected!;
            var b = result.Reference!;
            var delta = a.Seconds - b.Seconds;
            if (Math.Abs(delta) >= .03)
                descriptions.Add(new($"{result.Corner.Name}：区间比参考圈{(delta > 0 ? "慢" : "快")} {F(Math.Abs(delta), "0.000")} 秒，最低速度{Signed(a.MinimumSpeedKph - b.MinimumSpeedKph, "0.0")} km/h。两项差异不代表因果关系。", a.MinimumSpeedS));
            if (a.BrakeStartS is double brake && b.BrakeStartS is double otherBrake && Math.Abs(brake - otherBrake) >= 3)
                descriptions.Add(new($"{result.Corner.Name}：制动达到 25% 的起点比参考圈{(brake > otherBrake ? "晚" : "早")} {F(Math.Abs(brake - otherBrake), "0.0")} 米；可结合曲线复查，不能据此断定更快。", brake));
            if (a.ThrottleRecoveryS is double throttle && b.ThrottleRecoveryS is double otherThrottle && Math.Abs(throttle - otherThrottle) >= 3)
                descriptions.Add(new($"{result.Corner.Name}：最低速度后油门恢复至 70% 的位置比参考圈{(throttle > otherThrottle ? "晚" : "早")} {F(Math.Abs(throttle - otherThrottle), "0.0")} 米；这只是本次输入差异。", throttle));
            if (Math.Abs(delta) < .03 && Math.Abs(a.MinimumSpeedKph - b.MinimumSpeedKph) >= 1)
                descriptions.Add(new($"{result.Corner.Name}：最低速度比参考圈{Signed(a.MinimumSpeedKph - b.MinimumSpeedKph, "0.0")} km/h，区间耗时差小于 0.03 秒，不据此评价跑法优劣。", a.MinimumSpeedS));
        }
        return descriptions.Take(3).ToArray();
    }

    private static LapSample[]? Window(LapRecord lap, ManualCorner corner)
    {
        var samples = lap.Samples;
        if (samples.Count < 8) return null;
        var start = -1;
        var end = -1;
        for (var i = 0; i < samples.Count; i++)
        {
            var sample = samples[i];
            if (!double.IsFinite(sample.S) || !double.IsFinite(sample.ElapsedSeconds) ||
                (i > 0 && (sample.S <= samples[i - 1].S || sample.ElapsedSeconds <= samples[i - 1].ElapsedSeconds))) return null;
            if (sample.S <= corner.StartS) start = i;
            if (sample.S >= corner.EndS && end < 0) end = i;
        }
        if (start < 0 || end < 0 || end - start + 1 < 8) return null;
        var window = samples.Skip(start).Take(end - start + 1).ToArray();
        for (var i = 0; i < window.Length; i++)
        {
            var sample = window[i];
            if (!double.IsFinite(sample.SpeedMps) || sample.SpeedMps < 0 ||
                !double.IsFinite(sample.Brake) || sample.Brake is < 0 or > 1 ||
                !double.IsFinite(sample.Accel) || sample.Accel is < 0 or > 1 ||
                sample.ElapsedSeconds < 0 || sample.ElapsedSeconds > lap.TotalSeconds + .5) return null;
            if (i > 0 && (sample.S - window[i - 1].S > MaximumSampleGapMeters ||
                          sample.ElapsedSeconds - window[i - 1].ElapsedSeconds > MaximumSampleGapSeconds)) return null;
        }
        return window;
    }

    private static CornerLapMetrics Metrics(LapSample[] samples, double[] grid)
    {
        var aligned = new LapSample[grid.Length];
        var index = 0;
        for (var i = 0; i < grid.Length; i++)
        {
            while (index + 1 < samples.Length - 1 && samples[index + 1].S < grid[i]) index++;
            var a = samples[index];
            var b = samples[index + 1];
            var ratio = (grid[i] - a.S) / (b.S - a.S);
            double Mix(double x, double y) => x + (y - x) * ratio;
            aligned[i] = a with { S = grid[i], ElapsedSeconds = Mix(a.ElapsedSeconds, b.ElapsedSeconds),
                SpeedMps = Mix(a.SpeedMps, b.SpeedMps), Brake = Mix(a.Brake, b.Brake), Accel = Mix(a.Accel, b.Accel) };
        }
        var minimum = aligned.MinBy(sample => sample.SpeedMps)!;
        // Derive input transitions from original samples, so denser data in the other lap
        // cannot manufacture evidence of sustained brake/throttle input.
        return new(aligned[^1].ElapsedSeconds - aligned[0].ElapsedSeconds, minimum.SpeedMps * 3.6, minimum.S,
            Crossing(samples, grid[0], minimum.S, grid[^1], false),
            Crossing(samples, minimum.S, grid[^1], grid[^1], true));
    }

    private static double? Crossing(LapSample[] samples, double start, double latest, double end, bool throttle)
    {
        var threshold = throttle ? .7 : .25;
        double Input(LapSample sample) => throttle ? sample.Accel : sample.Brake;
        for (var i = 1; i < samples.Length; i++)
        {
            var a = samples[i - 1];
            var b = samples[i];
            if (Input(a) >= threshold || Input(b) < threshold) continue;
            var fraction = (threshold - Input(a)) / (Input(b) - Input(a));
            var s = a.S + fraction * (b.S - a.S);
            if (s < start || s > latest) continue;
            var time = a.ElapsedSeconds + fraction * (b.ElapsedSeconds - a.ElapsedSeconds);
            for (var j = i; j < samples.Length && samples[j].S <= end; j++)
            {
                var hold = samples[j];
                if (Input(hold) < threshold || (throttle && hold.Brake > .1)) break;
                if (j > i && hold.S - s >= 3 && hold.ElapsedSeconds - time >= .15) return s;
            }
        }
        return null;
    }

    private static string F(double value, string format) => value.ToString(format, CultureInfo.GetCultureInfo("zh-CN"));
    private static string Signed(double value, string format) => (value >= 0 ? "+" : "−") + F(Math.Abs(value), format);
}
