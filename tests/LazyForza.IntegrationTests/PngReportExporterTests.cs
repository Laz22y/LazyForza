using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LazyForza.App;

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
}
