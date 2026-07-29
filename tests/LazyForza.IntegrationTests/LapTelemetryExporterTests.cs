using System.Text;
using LazyForza.App;
using LazyForza.Domain;

namespace LazyForza.IntegrationTests;

[TestClass]
public sealed class LapTelemetryExporterTests
{
    [TestMethod]
    public void CsvExportContainsMetadataAndDynamicsWithoutGrowingUnbounded()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"lazyforza-telemetry-{Guid.NewGuid():N}.csv");
        try
        {
            var dynamics = new LapDynamics(
                -0.25,
                new WheelValues(0.1f, 0.2f, 0.3f, 0.4f),
                new WheelValues(0.01f, 0.02f, 0.03f, 0.04f),
                new WheelValues(0.2f, 0.3f, 0.4f, 0.5f));
            var samples = Enumerable.Range(0, 100)
                .Select(index => new LapSample(
                    index * 10,
                    index * 0.1,
                    20 + index / 10d,
                    4_000 + index,
                    3,
                    0.7,
                    0.1,
                    0,
                    index,
                    0,
                    index * 2,
                    dynamics))
                .ToArray();
            var lap = new LapRecord(
                Guid.NewGuid(),
                Guid.NewGuid(),
                1,
                1,
                Guid.NewGuid(),
                new VehicleProfileFingerprint(7, 5, 850, 1, 8, 8_000, "g", "c"),
                DateTimeOffset.Parse("2026-07-29T10:00:00+08:00"),
                10,
                true,
                null,
                [],
                samples);

            LapTelemetryExporter.WriteCsv(path, "测试,赛道", lap);

            var bytes = File.ReadAllBytes(path);
            Assert.IsTrue(
                bytes.AsSpan(0, 3).SequenceEqual(Encoding.UTF8.Preamble),
                "CSV should carry a UTF-8 BOM for reliable Chinese display in Excel.");
            var csv = File.ReadAllText(path, Encoding.UTF8);
            StringAssert.Contains(csv, "\"测试,赛道\"");
            StringAssert.Contains(csv, "steering,slip_ratio_fl");
            StringAssert.Contains(csv, "-0.25,0.1,0.2,0.3,0.4");
            Assert.IsTrue(bytes.Length < 40_000, "A 100-sample export should stay compact.");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [TestMethod]
    public void CsvExportLeavesNewDynamicsFieldsBlankForLegacyLap()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"lazyforza-telemetry-legacy-{Guid.NewGuid():N}.csv");
        try
        {
            var lap = new LapRecord(
                Guid.NewGuid(),
                Guid.NewGuid(),
                1,
                1,
                Guid.NewGuid(),
                new VehicleProfileFingerprint(7, 5, 850, 1, 8, 8_000, "g", "c"),
                DateTimeOffset.UtcNow,
                1,
                true,
                null,
                [],
                [new LapSample(0, 0, 20, 4_000, 3, 0.5, 0, 0, 0, 0, 0)]);

            LapTelemetryExporter.WriteCsv(path, "旧圈", lap);

            var lines = File.ReadAllLines(path, Encoding.UTF8);
            StringAssert.Contains(string.Join('\n', lines), "旧版圈速，不包含方向与轮胎滑移");
            var data = lines[^1].Split(',');
            Assert.HasCount(24, data);
            Assert.IsTrue(data.Skip(11).All(string.IsNullOrEmpty));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
