using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LazyForza.App;

namespace LazyForza.IntegrationTests;

[TestClass]
public sealed class ReleaseBrandLineTests
{
    [TestMethod]
    public void FittingMetadataStaysOnOneStaticLine()
    {
        var motion = new ReleaseLabelMotion();
        motion.Configure(170, 70, 70, 156, true, false);
        motion.Advance(1000);
        Assert.AreEqual(ReleaseLabelMode.Combined, motion.Mode);
        Assert.AreEqual(-1, motion.Frame.Item);
        Assert.AreEqual(1, motion.Frame.Opacity);
        Assert.IsTrue(double.IsPositiveInfinity(motion.Frame.NextUpdateSeconds));
    }

    [TestMethod]
    public void AlternationHoldsEachItemAndFadesWithoutMovingIt()
    {
        var motion = new ReleaseLabelMotion();
        motion.Configure(100, 80, 75, 171, true, false);
        Assert.AreEqual(ReleaseLabelMode.Alternate, motion.Mode);
        Assert.AreEqual(5, motion.Frame.NextUpdateSeconds, 1e-6);
        motion.Advance(4.9);
        Assert.AreEqual(0, motion.Frame.Item);
        Assert.AreEqual(1, motion.Frame.Opacity);
        motion.Advance(.15);
        Assert.AreEqual(.5, motion.Frame.Opacity, 1e-6);
        Assert.AreEqual(0, motion.Frame.Offset);
        motion.Advance(.1);
        Assert.AreEqual(1, motion.Frame.Item);
        Assert.AreEqual(.5, motion.Frame.Opacity, 1e-6);
        motion.Advance(.1);
        Assert.AreEqual(1, motion.Frame.Opacity);
        Assert.AreEqual(0, motion.Frame.Offset);
        motion.Advance(5.15);
        Assert.AreEqual(0, motion.Frame.Item);
    }

    [TestMethod]
    public void OnlyTheOversizedItemScrollsOnceThenYieldsToTheOtherItem()
    {
        var motion = new ReleaseLabelMotion();
        motion.Configure(100, 172, 80, 268, true, false);
        Assert.AreEqual(ReleaseLabelMode.Scroll, motion.Mode);
        Assert.AreEqual(0, motion.Frame.Offset);
        motion.Advance(6.5);
        Assert.AreEqual(-36, motion.Frame.Offset, 1e-6);
        motion.Advance(1.5);
        Assert.AreEqual(-72, motion.Frame.Offset, 1e-6);
        motion.Advance(1);
        Assert.AreEqual(-72, motion.Frame.Offset, 1e-6);
        motion.Advance(.7);
        Assert.AreEqual(1, motion.Frame.Item);
        Assert.AreEqual(0, motion.Frame.Offset);
        Assert.AreEqual(1, motion.Frame.Opacity, 1e-6);
        motion.Advance(4);
        Assert.AreEqual(1, motion.Frame.Item);
        Assert.AreEqual(0, motion.Frame.Offset);
    }

    [TestMethod]
    public void LongReleaseNameScrollsAfterTheFittingVersion()
    {
        var motion = new ReleaseLabelMotion();
        motion.Configure(100, 70, 220, 306, true, false);
        motion.Advance(4);
        Assert.AreEqual(0, motion.Frame.Offset);
        motion.Advance(1.2);
        Assert.AreEqual(1, motion.Frame.Item);
        motion.Advance(7.5);
        Assert.AreEqual(-60, motion.Frame.Offset, 1e-6);
    }

    [TestMethod]
    public void HoverPauseFreezesTheCurrentFrameAndDoesNotAccumulateHiddenTime()
    {
        var motion = new ReleaseLabelMotion();
        motion.Configure(90, 170, 80, 266, true, false);
        motion.Advance(6);
        var paused = motion.Frame;
        motion.Advance(3600, paused: true);
        Assert.AreEqual(paused, motion.Frame);
        motion.Advance(.5);
        Assert.AreEqual(paused.Offset - 12, motion.Frame.Offset, 1e-6);
    }

    [TestMethod]
    public void ReducedMotionUsesStaticCombinedTextAndRestartsCleanlyWhenDisabled()
    {
        var motion = new ReleaseLabelMotion();
        motion.Configure(90, 170, 80, 266, true, false);
        motion.Advance(7);
        motion.Configure(90, 170, 80, 266, true, true);
        motion.Advance(100);
        Assert.AreEqual(-1, motion.Frame.Item);
        Assert.AreEqual(0, motion.Frame.Offset);
        Assert.IsTrue(double.IsPositiveInfinity(motion.Frame.NextUpdateSeconds));
        motion.Configure(90, 170, 80, 266, true, false);
        Assert.AreEqual(0, motion.Frame.Item);
        Assert.AreEqual(0, motion.Frame.Offset);
        Assert.AreEqual(5, motion.Frame.NextUpdateSeconds, 1e-6);
    }

    [TestMethod]
    public void CombinedFitUsesHysteresisAroundTheMeasuredBoundary()
    {
        var motion = new ReleaseLabelMotion();
        motion.Configure(199, 90, 90, 200, true, false);
        foreach (var width in new[] { 200, 201, 202, 203 })
        {
            motion.Configure(width, 90, 90, 200, true, false);
            Assert.AreEqual(ReleaseLabelMode.Alternate, motion.Mode);
        }
        motion.Configure(204, 90, 90, 200, true, false);
        Assert.AreEqual(ReleaseLabelMode.Combined, motion.Mode);
        motion.Configure(201, 90, 90, 200, true, false);
        Assert.AreEqual(ReleaseLabelMode.Combined, motion.Mode);
        motion.Configure(199, 90, 90, 200, true, false);
        Assert.AreEqual(ReleaseLabelMode.Alternate, motion.Mode);
    }

    [TestMethod]
    public void MissingReleaseNameNeverAlternatesToAnEmptyItem()
    {
        var motion = new ReleaseLabelMotion();
        motion.Configure(100, 200, 0, 200, false, false);
        for (var index = 0; index < 100; index++)
        {
            motion.Advance(.7);
            Assert.AreEqual(0, motion.Frame.Item);
            Assert.IsTrue(motion.Frame.Offset is >= -100 and <= 0);
        }
    }

    [TestMethod]
    public void BuildMetadataHasTheRequestedBilingualReleaseName()
    {
        Assert.AreEqual("Radio Check", ApplicationVersionInfo.ReleaseNameForLanguage("zh-Hans"));
        Assert.AreEqual("Radio Check", ApplicationVersionInfo.ReleaseNameForLanguage("en"));
    }

    [TestMethod]
    [DoNotParallelize]
    public void ActualFontWidthsChooseEachModeAndUnloadStopsAnimation()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var panel = new StackPanel { Background = new SolidColorBrush(Color.FromRgb(10, 13, 18)) };
                var label = new ReleaseBrandLine("v1.5.3", "Radio Check");
                panel.Children.Add(label);
                void Layout(double width)
                {
                    panel.Measure(new Size(width, 20));
                    panel.Arrange(new Rect(0, 0, width, 20)); panel.UpdateLayout();
                }
                Layout(200);
                Assert.AreEqual(ReleaseLabelMode.Combined, label.DisplayMode);
                Layout(90);
                Assert.AreEqual(ReleaseLabelMode.Alternate, label.DisplayMode);
                Layout(35);
                Assert.AreEqual(ReleaseLabelMode.Scroll, label.DisplayMode);
                Assert.AreEqual(20, label.ActualHeight);
                Assert.AreEqual("v1.5.3 · Radio Check", AutomationProperties.GetName(label));
                Assert.AreEqual("v1.5.3\nRadio Check", label.ToolTip);
                // A hidden native presentation source loads the WPF visual tree without taking desktop focus.
                using var host = new HwndSource(new HwndSourceParameters("Release label lifecycle test")
                { Width = 100, Height = 40, WindowStyle = unchecked((int)0x80000000) });
                host.RootVisual = panel;
                FlushDispatcher();
                Layout(35);
                Assert.IsTrue(label.IsLoaded);
                Assert.IsTrue(label.IsAnimationScheduled);
                label.SetReduceMotion(true);
                Assert.IsFalse(label.IsAnimationScheduled);
                label.SetReduceMotion(false);
                Assert.IsTrue(label.IsAnimationScheduled);
                host.RootVisual = null;
                FlushDispatcher();
                Assert.IsFalse(label.IsLoaded);
                Assert.IsFalse(label.IsAnimationScheduled);
                label.SetReduceMotion(true);
                var path = Environment.GetEnvironmentVariable("LAZYFORZA_BRAND_QA");
                if (string.IsNullOrEmpty(path)) return;
                Directory.CreateDirectory(path);
                foreach (var width in new[] { 90, 169, 260 })
                {
                    var preview = new ReleaseBrandLine("v1.5.3", "Radio Check") { Width = width };
                    preview.SetReduceMotion(true);
                    using var previewHost = new HwndSource(new HwndSourceParameters("Release label snapshot")
                    { Width = width, Height = 20, WindowStyle = unchecked((int)0x80000000) });
                    previewHost.RootVisual = preview;
                    FlushDispatcher();
                    var bitmap = new RenderTargetBitmap(width, 20, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(preview);
                    using var file = File.Create(Path.Combine(path, $"release-line-reduced-{width}.png"));
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(file);
                }
            }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(10)));
        Assert.IsNull(failure, failure?.ToString());
    }

    private static void FlushDispatcher()
    {
        var frame = new DispatcherFrame();
        _ = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}
