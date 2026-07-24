using LazyForza.Domain;

namespace LazyForza.Telemetry;

public sealed class StreamStatistics
{
    private uint? previousTimestamp;
    private double averageIntervalMs;
    private DateTimeOffset windowStarted = DateTimeOffset.UtcNow;
    private long windowPackets;

    public long ValidPackets { get; private set; }
    public long InvalidPackets { get; private set; }
    public long EstimatedDroppedPackets { get; private set; }
    public long DuplicatePackets { get; private set; }
    public long OutOfOrderPackets { get; private set; }
    public long TimestampWraps { get; private set; }
    public double PacketsPerSecond { get; private set; }
    public DateTimeOffset? LastPacketAt { get; private set; }

    public void OnInvalid() => InvalidPackets++;

    public void OnPacket(TelemetryFrame frame)
    {
        ValidPackets++;
        windowPackets++;
        LastPacketAt = frame.ArrivalTime;
        var current = frame.Raw.TimestampMS;
        if (frame.Raw.IsRaceOn != 1 || current == 0)
        {
            // FH6 can keep sending menu frames whose telemetry and timestamp are all zero.
            // They are valid datagrams, but repeated zero timestamps are not network duplicates.
            previousTimestamp = null;
            UpdatePacketRate(frame.ArrivalTime);
            return;
        }

        if (previousTimestamp is uint previous)
        {
            if (current == previous)
            {
                DuplicatePackets++;
            }
            else
            {
                uint delta;
                if (current < previous)
                {
                    if (previous > 0xF0000000u && current < 0x0FFFFFFFu)
                    {
                        TimestampWraps++;
                        delta = unchecked(current - previous);
                    }
                    else
                    {
                        OutOfOrderPackets++;
                        delta = 0;
                    }
                }
                else
                {
                    delta = current - previous;
                }

                if (delta is > 0 and < 1000)
                {
                    if (averageIntervalMs > 0 && delta > averageIntervalMs * 1.75)
                    {
                        EstimatedDroppedPackets += Math.Max(0, (long)Math.Round(delta / averageIntervalMs) - 1);
                    }

                    averageIntervalMs = averageIntervalMs == 0 ? delta : (averageIntervalMs * 0.95) + (delta * 0.05);
                }
            }
        }

        previousTimestamp = current;
        UpdatePacketRate(frame.ArrivalTime);
    }

    private void UpdatePacketRate(DateTimeOffset arrivalTime)
    {
        var elapsed = arrivalTime - windowStarted;
        if (elapsed >= TimeSpan.FromSeconds(1))
        {
            PacketsPerSecond = windowPackets / elapsed.TotalSeconds;
            windowStarted = arrivalTime;
            windowPackets = 0;
        }
    }
}
