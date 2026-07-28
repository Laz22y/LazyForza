using LazyForza.Domain;

namespace LazyForza.Analysis;

public sealed record RaceSectorStability(
    int Index,
    double BestSeconds,
    double MedianSeconds,
    double StandardDeviationSeconds,
    double OpportunitySeconds);

public sealed record RaceReview(
    int TotalLaps,
    int ValidLaps,
    double? BestLapSeconds,
    double? MedianLapSeconds,
    double? StandardDeviationSeconds,
    double? ConsistencyPercent,
    double? TheoreticalBestSeconds,
    double? TheoreticalGainSeconds,
    double? TrendSeconds,
    IReadOnlyList<RaceSectorStability> Sectors,
    IReadOnlyList<string> Findings);

public static class RaceReviewAnalyzer
{
    public static RaceReview Analyze(IReadOnlyList<LapSummary> laps)
    {
        ArgumentNullException.ThrowIfNull(laps);
        var valid = laps
            .Where(lap => lap.IsValid && double.IsFinite(lap.TotalSeconds) && lap.TotalSeconds > 0)
            .OrderBy(lap => lap.StartedAt)
            .ToArray();
        if (valid.Length == 0)
        {
            return new RaceReview(
                laps.Count,
                0,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                [],
                laps.Count == 0
                    ? ["完成有效圈后生成复盘。"]
                    : ["本场尚无有效圈，暂时无法计算稳定性。"]);
        }

        var lapTimes = valid.Select(lap => lap.TotalSeconds).ToArray();
        var best = lapTimes.Min();
        var median = Median(lapTimes);
        var standardDeviation = StandardDeviation(lapTimes);
        var consistency = Math.Clamp(
            100 - (standardDeviation / Math.Max(median, 0.001) * 1_000),
            0,
            100);
        var trend = valid.Length >= 2
            ? valid[^1].TotalSeconds - valid[0].TotalSeconds
            : (double?)null;

        var expectedSectorCount = valid
            .Where(lap => lap.Segments.Count > 0)
            .GroupBy(lap => lap.Segments.Count)
            .OrderByDescending(group => group.Count())
            .ThenByDescending(group => group.Key)
            .Select(group => group.Key)
            .FirstOrDefault();
        var sectorLaps = expectedSectorCount > 0
            ? valid.Where(lap =>
                    lap.Segments.Count == expectedSectorCount &&
                    lap.Segments.All(segment =>
                        segment.IsValid &&
                        double.IsFinite(segment.TimeSeconds) &&
                        segment.TimeSeconds > 0))
                .ToArray()
            : [];
        var sectors = Enumerable.Range(0, expectedSectorCount)
            .Select(index =>
            {
                var values = sectorLaps.Select(lap => lap.Segments[index].TimeSeconds).ToArray();
                if (values.Length == 0) return null;
                var sectorBest = values.Min();
                var sectorMedian = Median(values);
                return new RaceSectorStability(
                    index,
                    sectorBest,
                    sectorMedian,
                    StandardDeviation(values),
                    Math.Max(0, sectorMedian - sectorBest));
            })
            .Where(sector => sector is not null)
            .Cast<RaceSectorStability>()
            .ToArray();
        var theoreticalBest = sectors.Length == expectedSectorCount && expectedSectorCount > 0
            ? sectors.Sum(sector => sector.BestSeconds)
            : (double?)null;
        var theoreticalGain = theoreticalBest is double potential
            ? Math.Max(0, best - potential)
            : (double?)null;

        var findings = BuildFindings(
            laps.Count,
            valid.Length,
            consistency,
            standardDeviation,
            trend,
            theoreticalGain,
            sectors);
        return new RaceReview(
            laps.Count,
            valid.Length,
            best,
            median,
            standardDeviation,
            consistency,
            theoreticalBest,
            theoreticalGain,
            trend,
            sectors,
            findings);
    }

    private static IReadOnlyList<string> BuildFindings(
        int totalLaps,
        int validLaps,
        double consistency,
        double standardDeviation,
        double? trend,
        double? theoreticalGain,
        IReadOnlyList<RaceSectorStability> sectors)
    {
        var findings = new List<string>();
        if (validLaps < totalLaps)
            findings.Add($"{totalLaps - validLaps} 圈未计入稳定性统计，可结合无效原因复查。");

        if (validLaps < 3)
        {
            findings.Add("有效圈少于 3 圈，稳定性结论仅作初步参考。");
        }
        else if (consistency >= 92)
        {
            findings.Add($"圈速波动较小，标准差为 {standardDeviation:0.000} 秒。");
        }
        else if (consistency >= 80)
        {
            findings.Add($"整体节奏基本稳定，圈速标准差为 {standardDeviation:0.000} 秒。");
        }
        else
        {
            findings.Add($"圈速波动较明显，建议先稳定刹车点和出弯油门。");
        }

        if (trend is double trendSeconds && validLaps >= 2)
        {
            if (trendSeconds <= -0.15)
                findings.Add($"末圈比首个有效圈快 {-trendSeconds:0.000} 秒，比赛中仍在持续改善。");
            else if (trendSeconds >= 0.15)
                findings.Add($"末圈比首个有效圈慢 {trendSeconds:0.000} 秒，可留意后程失误或节奏下降。");
            else
                findings.Add("首尾有效圈差距较小，比赛前后节奏接近。");
        }

        if (theoreticalGain is double gain)
        {
            if (gain >= 0.05)
                findings.Add($"组合本场各段最快约还能缩短 {gain:0.000} 秒。");
            else
                findings.Add("本场最快圈已较完整地组合了各段表现。");
        }

        var unstableSector = sectors
            .OrderByDescending(sector => sector.StandardDeviationSeconds)
            .FirstOrDefault();
        if (unstableSector is not null && unstableSector.StandardDeviationSeconds >= 0.08)
            findings.Add($"第 {unstableSector.Index + 1} 段波动最大，标准差 {unstableSector.StandardDeviationSeconds:0.000} 秒。");

        return findings.Take(4).ToArray();
    }

    private static double Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return 0;
        var sorted = values.OrderBy(value => value).ToArray();
        var middle = sorted.Length / 2;
        return sorted.Length % 2 == 0
            ? (sorted[middle - 1] + sorted[middle]) / 2
            : sorted[middle];
    }

    private static double StandardDeviation(IReadOnlyList<double> values)
    {
        if (values.Count < 2) return 0;
        var average = values.Average();
        return Math.Sqrt(values.Sum(value => Math.Pow(value - average, 2)) / values.Count);
    }
}
