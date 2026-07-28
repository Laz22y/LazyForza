using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace LazyForza.App;

internal static class PngReportExporter
{
    private const int MaximumPixelWidth = 1280;
    private const int MaximumPixelHeight = 1600;

    public static string? Export(
        Window owner,
        FrameworkElement report,
        string suggestedFileName)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(report);

        var dialog = new SaveFileDialog
        {
            Title = "导出 PNG",
            Filter = "PNG 图片 (*.png)|*.png",
            DefaultExt = ".png",
            AddExtension = true,
            FileName = SanitizeFileName(suggestedFileName),
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(owner) != true) return null;

        Save(report, dialog.FileName);
        return dialog.FileName;
    }

    internal static void Save(FrameworkElement report, string path)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        report.Measure(new Size(MaximumPixelWidth, double.PositiveInfinity));
        var logicalWidth = Math.Max(1, Math.Min(MaximumPixelWidth, report.DesiredSize.Width));
        var logicalHeight = Math.Max(1, report.DesiredSize.Height);
        report.Arrange(new Rect(0, 0, logicalWidth, logicalHeight));
        report.UpdateLayout();

        var scale = Math.Min(
            1,
            Math.Min(
                MaximumPixelWidth / logicalWidth,
                MaximumPixelHeight / logicalHeight));
        var pixelWidth = Math.Max(1, (int)Math.Ceiling(logicalWidth * scale));
        var pixelHeight = Math.Max(1, (int)Math.Ceiling(logicalHeight * scale));
        var target = new RenderTargetBitmap(
            pixelWidth,
            pixelHeight,
            96,
            96,
            PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(
                new VisualBrush(report)
                {
                    Stretch = Stretch.Fill,
                    AlignmentX = AlignmentX.Left,
                    AlignmentY = AlignmentY.Top
                },
                null,
                new Rect(0, 0, pixelWidth, pixelHeight));
        }
        target.Render(visual);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(target));
        using var output = File.Create(path);
        encoder.Save(output);
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var sanitized = new string(value
            .Select(character => invalid.Contains(character) ? '-' : character)
            .ToArray())
            .Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "LazyForza-report.png" : sanitized;
    }
}
