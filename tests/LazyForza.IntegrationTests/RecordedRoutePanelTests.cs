using System.IO;
using System.Windows;
using System.Windows.Controls;
using LazyForza.App;

namespace LazyForza.IntegrationTests;

[TestClass]
public sealed class RecordedRoutePanelTests
{
    [TestMethod]
    [DataRow("zh-Hans", 440, true)]
    [DataRow("en", 620, true)]
    [DataRow("zh-Hans", 440, false)]
    [DataRow("en", 620, false)]
    [DoNotParallelize]
    public void EnrollmentUsesExistingCardAndButtonStylesAndIncompleteRecordsRemainAnalyzable(string language, int width, bool complete) => WpfTestHost.Run(() =>
    {
        AppLocalization.UseLanguage(language);
        var enrollments = 0;
        var card = MainWindow.BuildRecordedRouteActions(complete,
            () => { enrollments++; return Task.CompletedTask; }, exception => Assert.Fail(exception.Message));
        AppLocalization.ApplyTo(card);
        card.Measure(new Size(width, double.PositiveInfinity));
        card.Arrange(new Rect(0, 0, width, card.DesiredSize.Height));
        card.UpdateLayout();
        var content = (StackPanel)card.Child;
        var button = content.Children.OfType<WrapPanel>().Single().Children.OfType<Button>().Single();
        Assert.AreEqual(complete, button.IsEnabled);
        Assert.IsTrue(button.ActualWidth <= width);
        Assert.AreEqual(language == "en" ? "Add to personal track library" : "收录到个人赛道库", button.Content);
        if (Environment.GetEnvironmentVariable("LAZYFORZA_RECORDED_ROUTE_QA") is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
            PngReportExporter.Save(card, Path.Combine(directory, $"recorded-route-{language}-{complete}.png"));
        }
        if (complete)
        {
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.AreEqual(1, enrollments);
            Assert.IsTrue(button.IsEnabled);
        }
        Assert.AreEqual(language == "en" ? "Start data missing" : "缺少起点数据", RecordedLapText.Reason("recording-missing-start"));
    });
}
