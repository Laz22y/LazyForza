namespace LazyForza.Domain;

public enum LapDeltaComparison
{
    TrackBest,
    ClassBest,
    PerformanceIndexBest,
    CarBest
}

public sealed record LapDeltaOptions(bool Continuous = false, LapDeltaComparison Comparison = LapDeltaComparison.ClassBest)
{
    public const string ContinuousSetting = "lap.delta.continuous";
    public const string ComparisonSetting = "lap.delta.comparison";
}
