namespace LazyForza.Modules.Abstractions;

/// <summary>
/// Bounds expensive latest-state processing without tying it to a fixed source frame rate.
/// Lower-rate streams pass through unchanged; only samples arriving above the configured
/// cadence are coalesced.
/// </summary>
public sealed class TelemetryProcessingCadence
{
    public static readonly TimeSpan HighRateMinimumInterval = TimeSpan.FromMilliseconds(11);

    private readonly object sync = new();
    private readonly TimeSpan minimumInterval;
    private DateTimeOffset? lastProcessedAt;

    public TelemetryProcessingCadence(TimeSpan minimumInterval)
    {
        if (minimumInterval < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(minimumInterval));
        this.minimumInterval = minimumInterval;
    }

    public bool ShouldProcess(DateTimeOffset arrivalTime, bool force = false)
    {
        lock (sync)
        {
            if (force || lastProcessedAt is not DateTimeOffset last || arrivalTime < last)
            {
                lastProcessedAt = arrivalTime;
                return true;
            }
            if (arrivalTime - last < minimumInterval) return false;
            lastProcessedAt = arrivalTime;
            return true;
        }
    }

    public void Reset()
    {
        lock (sync) lastProcessedAt = null;
    }
}
