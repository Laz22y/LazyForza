using System.IO;
using LazyForza.Domain;
using LazyForza.Telemetry;

namespace LazyForza.App;

internal sealed record TelemetryRecordingReplay(
    RecordingMetadata Metadata,
    LapRecord Lap,
    long FrameCount,
    string SourcePath,
    string? TrackName);

internal static class TelemetryRecordingAnalysis
{
    private static readonly TimeSpan SamplingInterval = TimeSpan.FromMilliseconds(50);

    public static async Task<TelemetryRecordingReplay> LoadAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var singleLap = await SingleLapTelemetryRecordingFile.TryReadAsync(
            path,
            cancellationToken).ConfigureAwait(false);
        if (singleLap is not null)
        {
            return new TelemetryRecordingReplay(
                singleLap.Metadata,
                singleLap.Lap,
                singleLap.Lap.Samples.Count,
                path,
                singleLap.TrackName);
        }

        var samples = new List<LapSample>();
        TelemetryFrame? firstFrame = null;
        DateTimeOffset? firstArrival = null;
        DateTimeOffset? lastArrival = null;
        DateTimeOffset? lastAcceptedArrival = null;
        Vector3F? previousPosition = null;
        double distance = 0;
        long frameCount = 0;

        var metadata = await TelemetryRecordingReader.ReadAsync(
            path,
            frame =>
            {
                frameCount++;
                if (frame.Raw.IsRaceOn != 1 ||
                    !float.IsFinite(frame.Raw.Speed) ||
                    !float.IsFinite(frame.Raw.Position.X) ||
                    !float.IsFinite(frame.Raw.Position.Y) ||
                    !float.IsFinite(frame.Raw.Position.Z))
                    return ValueTask.CompletedTask;

                firstFrame ??= frame;
                firstArrival ??= frame.ArrivalTime;
                lastArrival = frame.ArrivalTime;
                if (lastAcceptedArrival is DateTimeOffset lastAccepted &&
                    frame.ArrivalTime - lastAccepted < SamplingInterval)
                    return ValueTask.CompletedTask;

                var position = frame.Raw.Position;
                if (previousPosition is Vector3F previous)
                {
                    var dx = position.X - previous.X;
                    var dy = position.Y - previous.Y;
                    var dz = position.Z - previous.Z;
                    var step = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                    if (step is >= 0.01 and <= 150)
                        distance += step;
                }

                samples.Add(new LapSample(
                    distance,
                    Math.Max(0, (frame.ArrivalTime - firstArrival.Value).TotalSeconds),
                    Math.Max(0, frame.Raw.Speed),
                    Math.Max(0, frame.Raw.CurrentEngineRpm),
                    frame.Raw.Gear,
                    frame.Normalized.AccelRatio,
                    frame.Normalized.BrakeRatio,
                    0,
                    position.X,
                    position.Y,
                    position.Z,
                    new LapDynamics(
                        Math.Clamp(frame.Raw.Steer / 127d, -1, 1),
                        frame.Raw.TireSlipRatio,
                        frame.Raw.TireSlipAngle,
                        frame.Raw.TireCombinedSlip)));
                previousPosition = position;
                lastAcceptedArrival = frame.ArrivalTime;
                return ValueTask.CompletedTask;
            },
            cancellationToken).ConfigureAwait(false);

        if (firstFrame is null || firstArrival is null || lastArrival is null || samples.Count == 0)
            throw new InvalidDataException("录制文件中没有可供工作台回放的驾驶帧。");

        var lap = new LapRecord(
            Guid.NewGuid(),
            Guid.Empty,
            1,
            0,
            Guid.NewGuid(),
            VehicleProfileFingerprint.FromFrame(firstFrame),
            firstArrival.Value,
            Math.Max(samples[^1].ElapsedSeconds, (lastArrival.Value - firstArrival.Value).TotalSeconds),
            true,
            null,
            [],
            samples);
        return new TelemetryRecordingReplay(metadata, lap, frameCount, path, null);
    }
}
