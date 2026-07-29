using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LazyForza.Analysis;
using LazyForza.App;
using LazyForza.Domain;

namespace LazyForza.IntegrationTests;

[TestClass]
public sealed class PngReportExporterTests
{
    [TestMethod]
    public void ExportCapsLargeReportDimensionsAndWritesReadablePng()
    {
        var root = Path.Combine(Path.GetTempPath(), $"lazyforza-png-{Guid.NewGuid():N}");
        var path = Path.Combine(root, "report.png");
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var report = new Border
                {
                    Width = 2_000,
                    Height = 3_000,
                    Background = new SolidColorBrush(Color.FromRgb(11, 15, 20)),
                    Child = new TextBlock
                    {
                        Text = "LazyForza PNG report",
                        Foreground = Brushes.White,
                        FontSize = 24
                    }
                };
                PngReportExporter.Save(report, path);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        try
        {
            Assert.IsNull(failure);
            Assert.IsTrue(File.Exists(path));
            using var stream = File.OpenRead(path);
            var decoder = new PngBitmapDecoder(
                stream,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            Assert.AreEqual(1, decoder.Frames.Count);
            Assert.IsTrue(decoder.Frames[0].PixelWidth <= 1280);
            Assert.IsTrue(decoder.Frames[0].PixelHeight <= 1600);
            Assert.IsTrue(decoder.Frames[0].PixelWidth > 0);
            Assert.IsTrue(decoder.Frames[0].PixelHeight > 0);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public void DynamicsMapLayerRendersIntoPngExport()
    {
        var root = Path.Combine(Path.GetTempPath(), $"lazyforza-map-png-{Guid.NewGuid():N}");
        var path = Path.Combine(root, "dynamics-map.png");
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var samples = Enumerable.Range(0, 180)
                    .Select(index =>
                    {
                        var angle = index / 179d * Math.PI * 2;
                        return new LapSample(
                            index * 5,
                            index * 0.1,
                            30,
                            5_000,
                            4,
                            (Math.Sin(angle) + 1) / 2,
                            0,
                            0,
                            Math.Cos(angle) * 100,
                            0,
                            Math.Sin(angle) * 100,
                            new LapDynamics(
                                Math.Sin(angle),
                                new WheelValues(0.1f, 0.2f, 0.3f, 0.4f),
                                default,
                                new WheelValues(0.1f, 0.2f, 0.3f, 0.4f)));
                    })
                    .ToArray();
                var lap = new LapRecord(
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    1,
                    1,
                    Guid.NewGuid(),
                    new VehicleProfileFingerprint(1, 5, 850, 1, 8, 8_000, "g", "c"),
                    DateTimeOffset.UtcNow,
                    18,
                    true,
                    null,
                    [],
                    samples);
                var map = new TrackMapView(
                    [lap],
                    null,
                    [new LapSeriesLegendEntry("0:18.000", "测试圈")],
                    dynamicsLapId: lap.Id)
                {
                    Width = 900,
                    Height = 520,
                    DynamicsLayer = DrivingDynamicsLayer.Throttle
                };
                PngReportExporter.Save(map, path);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        try
        {
            Assert.IsNull(failure);
            Assert.IsTrue(File.Exists(path));
            using var stream = File.OpenRead(path);
            var decoder = new PngBitmapDecoder(
                stream,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            Assert.IsTrue(decoder.Frames[0].PixelWidth > 0);
            Assert.IsTrue(decoder.Frames[0].PixelHeight > 0);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
