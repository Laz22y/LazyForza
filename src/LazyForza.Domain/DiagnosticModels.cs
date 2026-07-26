namespace LazyForza.Domain;

public sealed record DiagnosticSignal(
    string Code,
    string Summary,
    bool IsAnomaly,
    DateTimeOffset OccurredAt,
    IReadOnlyDictionary<string, string>? Data = null);
