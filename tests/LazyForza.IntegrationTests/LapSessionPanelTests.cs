using System.Windows;
using System.Windows.Controls;
using LazyForza.Analysis;
using LazyForza.App;
using LazyForza.Domain;

namespace LazyForza.IntegrationTests;

[TestClass]
public sealed class LapSessionPanelTests
{
    [TestMethod]
    [DoNotParallelize]
    public void LongRaceShowsAllLapsAndOpensTheSelectedInvalidLapInBothLanguages()
    {
        WpfTestHost.Run(() =>
        {
            var sessionId = Guid.NewGuid();
            var trackId = Guid.NewGuid();
            var laps = Enumerable.Range(0, 65).Select(i => new LapSummary(Guid.NewGuid(), trackId, 1, 2, sessionId,
                new VehicleProfileFingerprint(1, 5, 850, 2, 8, 8000, "g", "c"),
                DateTimeOffset.UnixEpoch.AddMinutes(i * 2), 90 + i * .1, i != 64, i == 64 ? "invalid" : null, [])).ToArray();
            foreach (var language in new[] { "zh-Hans", "en" })
            {
                AppLocalization.UseLanguage(language);
                Assert.AreEqual("1:40:58.000", MainWindow.SessionDuration(laps.Sum(lap => lap.TotalSeconds), false));
                LapSummary? selected = null;
                var panel = MainWindow.BuildLapSessionDetail(LapSessionAnalyzer.Group(laps).Single(), false, lap => selected = lap);
                AppLocalization.ApplyTo(panel);
                panel.Measure(new Size(620, double.PositiveInfinity));
                panel.Arrange(new Rect(0, 0, 620, panel.DesiredSize.Height));
                var list = Descendants<ListBox>(panel).Single();
                Assert.AreEqual(65, list.Items.Count);
                Assert.IsTrue(VirtualizingPanel.GetIsVirtualizing(list));
                var open = Descendants<Button>(panel).Single();
                Assert.IsFalse(open.IsEnabled);
                list.SelectedIndex = 64;
                open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.AreEqual(laps[64].Id, selected!.Id);
                Assert.AreEqual(language == "en" ? "Invalid" : "无效", ((SessionLapRow)list.SelectedItem).State);
                Assert.IsTrue(Descendants<TextBlock>(panel).Any(text => text.Text == AppLocalization.Literal("记录总用时")));
            }
        });
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) yield return match;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
    }
}
