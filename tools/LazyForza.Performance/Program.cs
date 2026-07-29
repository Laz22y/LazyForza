using System.Diagnostics;
using System.Text.Json;
using LazyForza.Analysis;
using LazyForza.Domain;

const int projectionOperations = 50_000;
const int downsampleOperations = 40;

var route = BuildRoute(3_000);
var index = new TrackSpatialIndex(route);
var samples = BuildSamples(120_000);

for (var warmup = 0; warmup < 2_000; warmup++)
{
    var point = route[warmup % route.Length];
    _ = index.ProjectNearest(point.X + 2, point.Y, point.Z - 1, 90);
}
_ = ChartInteractionAlgorithms.DownsampleSpeedEnvelope(samples, 1_000);

GC.Collect();
GC.WaitForPendingFinalizers();
GC.Collect();

var projectionAllocationStart = GC.GetAllocatedBytesForCurrentThread();
var projectionTimer = Stopwatch.StartNew();
var projectionChecksum = 0d;
for (var operation = 0; operation < projectionOperations; operation++)
{
    var point = route[(operation * 17) % route.Length];
    projectionChecksum += index.ProjectNearest(point.X + 2, point.Y, point.Z - 1, 90).S;
}
projectionTimer.Stop();
var projectionAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - projectionAllocationStart;

var downsampleAllocationStart = GC.GetAllocatedBytesForCurrentThread();
var downsampleTimer = Stopwatch.StartNew();
var downsampleChecksum = 0d;
for (var operation = 0; operation < downsampleOperations; operation++)
{
    var reduced = ChartInteractionAlgorithms.DownsampleSpeedEnvelope(samples, 1_000);
    downsampleChecksum += reduced[operation % reduced.Count].SpeedMps;
}
downsampleTimer.Stop();
var downsampleAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - downsampleAllocationStart;

var result = new PerformanceResult(
    DateTimeOffset.UtcNow,
    Environment.Version.ToString(),
    new MetricResult(
        projectionOperations,
        projectionTimer.Elapsed.TotalMilliseconds,
        projectionTimer.Elapsed.TotalMilliseconds * 1_000 / projectionOperations,
        projectionAllocatedBytes,
        projectionAllocatedBytes / (double)projectionOperations,
        projectionChecksum),
    new MetricResult(
        downsampleOperations,
        downsampleTimer.Elapsed.TotalMilliseconds,
        downsampleTimer.Elapsed.TotalMilliseconds * 1_000 / downsampleOperations,
        downsampleAllocatedBytes,
        downsampleAllocatedBytes / (double)downsampleOperations,
        downsampleChecksum));

Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));

var failed = false;
failed |= Check("spatial projection total", result.SpatialProjection.TotalMilliseconds, 1_500, "ms");
failed |= Check("spatial projection allocation", result.SpatialProjection.BytesPerOperation, 256, "B/op");
failed |= Check("speed envelope total", result.SpeedEnvelope.TotalMilliseconds, 600, "ms");
failed |= Check("speed envelope allocation", result.SpeedEnvelope.BytesPerOperation, 16_384, "B/op");
return failed ? 1 : 0;

static bool Check(string name, double actual, double maximum, string unit)
{
    var passed = actual <= maximum;
    Console.WriteLine($"{(passed ? "PASS" : "FAIL")} {name}: {actual:0.###} {unit} <= {maximum:0.###} {unit}");
    return !passed;
}

static TrackPoint[] BuildRoute(int count)
{
    var result = new TrackPoint[count];
    const double radius = 2_500;
    var distance = 0d;
    for (var index = 0; index < count; index++)
    {
        var angle = Math.PI * 2 * index / (count - 1);
        var nextAngle = Math.PI * 2 * Math.Min(index + 1, count - 1) / (count - 1);
        var point = new TrackPoint(
            Math.Cos(angle) * radius,
            Math.Sin(angle * 3) * 8,
            Math.Sin(angle) * radius,
            distance,
            -Math.Sin(angle),
            Math.Cos(angle));
        if (index > 0) distance += Math.Sqrt(point.DistanceSquaredTo(result[index - 1]));
        result[index] = point with { S = distance };
        _ = nextAngle;
    }
    return result;
}

static LapSample[] BuildSamples(int count)
{
    var result = new LapSample[count];
    for (var index = 0; index < count; index++)
    {
        result[index] = new LapSample(
            index * 0.5,
            index / 60d,
            45 + Math.Sin(index * 0.015) * 20,
            5_000,
            4,
            0.7,
            index % 900 is > 760 and < 810 ? 0.8 : 0,
            0,
            index * 0.5,
            0,
            Math.Sin(index * 0.01) * 100);
    }
    return result;
}

internal sealed record PerformanceResult(
    DateTimeOffset MeasuredAt,
    string Runtime,
    MetricResult SpatialProjection,
    MetricResult SpeedEnvelope);

internal sealed record MetricResult(
    int Operations,
    double TotalMilliseconds,
    double MicrosecondsPerOperation,
    long AllocatedBytes,
    double BytesPerOperation,
    double Checksum);
