namespace LazyForza.Analysis;

public static class RobustStatistics
{
    public static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        if (sorted.Length == 0) return double.NaN;
        var middle = sorted.Length / 2;
        return sorted.Length % 2 == 0 ? (sorted[middle - 1] + sorted[middle]) / 2 : sorted[middle];
    }

    public static double MedianAbsoluteDeviation(IEnumerable<double> values)
    {
        var array = values.ToArray();
        if (array.Length == 0) return double.NaN;
        var median = Median(array);
        return Median(array.Select(value => Math.Abs(value - median)));
    }
}

