namespace LazyForza.Domain;

/// <summary>
/// Resolves FH6's eight performance classes. The class value supplied by Data Out is
/// authoritative; PI inference is only a fallback for legacy or otherwise invalid data.
/// </summary>
public static class PerformanceClassCatalog
{
    public const int MinimumClass = 0;
    public const int MaximumClass = 7;

    public static bool IsKnown(int performanceClass) =>
        performanceClass is >= MinimumClass and <= MaximumClass;

    public static int Resolve(int performanceClass, int performanceIndex) =>
        IsKnown(performanceClass) ? performanceClass : InferFromPerformanceIndex(performanceIndex);

    public static int InferFromPerformanceIndex(int performanceIndex) => performanceIndex switch
    {
        <= 400 => 0,
        <= 500 => 1,
        <= 600 => 2,
        <= 700 => 3,
        <= 800 => 4,
        <= 900 => 5,
        <= 998 => 6,
        _ => 7
    };

    public static string Name(int performanceClass) => performanceClass switch
    {
        0 => "D",
        1 => "C",
        2 => "B",
        3 => "A",
        4 => "S1",
        5 => "S2",
        6 => "R",
        7 => "X",
        _ => Name(InferFromPerformanceIndex(0))
    };
}
