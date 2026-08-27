using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace LazyForza.Fh5MapProbe;

public sealed class CapturePackageWriter : IDisposable
{
    public const int SchemaVersion = 1;
    public const string Extension = ".fh5mapcapture";
    private static readonly byte[] RawMagic = "LF5RAW01"u8.ToArray();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private readonly string outputPath;
    private readonly string workDirectory;
    private readonly BinaryWriter rawWriter;
    private readonly StreamWriter frameWriter;
    private bool completed;
    private bool disposed;

    public CapturePackageWriter(string outputPath)
    {
        this.outputPath = Path.GetFullPath(outputPath);
        var parent = Path.GetDirectoryName(this.outputPath) ??
            throw new InvalidOperationException("输出路径没有父目录。");
        Directory.CreateDirectory(parent);
        if (File.Exists(this.outputPath))
            throw new IOException("输出文件已经存在，请选择新的文件名。");
        workDirectory = Path.Combine(
            parent,
            $".{Path.GetFileName(this.outputPath)}.work-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDirectory);
        try
        {
            rawWriter = new BinaryWriter(
                new FileStream(
                    Path.Combine(workDirectory, "raw-packets.bin"),
                    new FileStreamOptions
                    {
                        Mode = FileMode.CreateNew,
                        Access = FileAccess.Write,
                        Share = FileShare.Read,
                        BufferSize = 128 * 1024,
                        Options = FileOptions.SequentialScan
                    }),
                Encoding.UTF8,
                leaveOpen: false);
            rawWriter.Write(RawMagic);
            frameWriter = new StreamWriter(
                new FileStream(
                    Path.Combine(workDirectory, "frames.csv"),
                    new FileStreamOptions
                    {
                        Mode = FileMode.CreateNew,
                        Access = FileAccess.Write,
                        Share = FileShare.Read,
                        BufferSize = 128 * 1024,
                        Options = FileOptions.SequentialScan
                    }),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            frameWriter.WriteLine(
                "sequence,receivedAtUtc,packetLength,isRaceOn,timestampMs,positionX,positionY,positionZ," +
                "velocityX,velocityY,velocityZ,velocityMagnitudeMps,speedMps,speedDeltaMps,yaw,pitch,roll," +
                "distanceTraveledMeters,currentLapSeconds,currentRaceSeconds,lapNumber,racePosition," +
                "accel,brake,gear,steer,carOrdinal,carClass,performanceIndex,drivetrainType,numCylinders," +
                "carCategory,horizonUnknown1,horizonUnknown2");
        }
        catch
        {
            if (Directory.Exists(workDirectory)) Directory.Delete(workDirectory, recursive: true);
            throw;
        }
    }

    public string RecoveryDirectory => workDirectory;

    public void WriteRawPacket(DateTimeOffset receivedAt, ReadOnlySpan<byte> packet)
    {
        ThrowIfUnavailable();
        rawWriter.Write(receivedAt.UtcTicks);
        rawWriter.Write(packet.Length);
        rawWriter.Write(packet);
    }

    public void WriteFrame(long sequence, DateTimeOffset receivedAt, Fh5DataOutFrame frame)
    {
        ThrowIfUnavailable();
        var values = new object[]
        {
            sequence,
            receivedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            frame.PacketLength,
            frame.IsRaceOn,
            frame.TimestampMs,
            frame.PositionX,
            frame.PositionY,
            frame.PositionZ,
            frame.VelocityX,
            frame.VelocityY,
            frame.VelocityZ,
            frame.VelocityMagnitudeMps,
            frame.SpeedMps,
            frame.SpeedDeltaMps,
            frame.Yaw,
            frame.Pitch,
            frame.Roll,
            frame.DistanceTraveledMeters,
            frame.CurrentLapSeconds,
            frame.CurrentRaceSeconds,
            frame.LapNumber,
            frame.RacePosition,
            frame.Accel,
            frame.Brake,
            frame.Gear,
            frame.Steer,
            frame.CarOrdinal,
            frame.CarClass,
            frame.PerformanceIndex,
            frame.DrivetrainType,
            frame.NumCylinders,
            frame.CarCategory,
            frame.HorizonUnknown1,
            frame.HorizonUnknown2
        };
        frameWriter.WriteLine(string.Join(',', values.Select(Invariant)));
    }

    public async Task CompleteAsync(
        Fh5CaptureManifest manifest,
        IReadOnlyList<Fh5CoordinateMarker> markers,
        CancellationToken cancellationToken = default)
    {
        ThrowIfUnavailable();
        rawWriter.Dispose();
        frameWriter.Dispose();
        await WriteMarkersAsync(markers, cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(
                Path.Combine(workDirectory, "manifest.json"),
                JsonSerializer.Serialize(manifest, JsonOptions),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken)
            .ConfigureAwait(false);
        var temporaryPackage = $"{outputPath}.partial-{Guid.NewGuid():N}";
        try
        {
            ZipFile.CreateFromDirectory(
                workDirectory,
                temporaryPackage,
                CompressionLevel.Fastest,
                includeBaseDirectory: false);
            File.Move(temporaryPackage, outputPath);
        }
        finally
        {
            if (File.Exists(temporaryPackage)) File.Delete(temporaryPackage);
        }
        Directory.Delete(workDirectory, recursive: true);
        completed = true;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (!completed)
        {
            rawWriter.Dispose();
            frameWriter.Dispose();
        }
    }

    private async Task WriteMarkersAsync(
        IReadOnlyList<Fh5CoordinateMarker> markers,
        CancellationToken cancellationToken)
    {
        await using var stream = new StreamWriter(
            Path.Combine(workDirectory, "markers.csv"),
            append: false,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        await stream.WriteLineAsync(
            "id,name,capturedAtUtc,x,y,z,spreadMeters,sampleCount,meanSpeedMps".AsMemory(),
            cancellationToken);
        foreach (var marker in markers)
        {
            var line = string.Join(',',
                marker.Id,
                Csv(marker.Name),
                marker.CapturedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
                Invariant(marker.X),
                Invariant(marker.Y),
                Invariant(marker.Z),
                Invariant(marker.SpreadMeters),
                marker.SampleCount,
                Invariant(marker.MeanSpeedMps));
            await stream.WriteLineAsync(line.AsMemory(), cancellationToken);
        }
    }

    private static string Invariant(object value) =>
        Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;

    private static string Csv(string value) =>
        $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private void ThrowIfUnavailable()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (completed) throw new InvalidOperationException("采集包已经完成。");
    }
}
