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
    public void AlternationFadesOnOneBaselineWithoutOverlappingText()
    {
        var motion = new ReleaseLabelMotion();
        motion.Configure(100, 80, 75, 171, true, false);
        Assert.AreEqual(ReleaseLabelMode.Alternate, motion.Mode);
        Assert.AreEqual(5, motion.Frame.NextUpdateSeconds, 1e-6);
        motion.Advance(4.9);
        Assert.AreEqual(0, motion.Frame.Item);
        Assert.AreEqual(1, motion.Frame.Opacity);
        motion.Advance(.22);
        Assert.AreEqual(.5, motion.Frame.Opacity, 1e-6);
        Assert.AreEqual(0, motion.Frame.Item);
        Assert.AreEqual(0, motion.Frame.Offset);
        motion.Advance(.28);
        Assert.AreEqual(1, motion.Frame.Item);
        Assert.AreEqual(.5, motion.Frame.Opacity, 1e-6);
        motion.Advance(.16);
        Assert.AreEqual(1, motion.Frame.Opacity);
        Assert.AreEqual(0, motion.Frame.Offset);
        motion.Advance(5.56);
        Assert.AreEqual(0, motion.Frame.Item);
    }

    [TestMethod]
    public void OnlyTheOversizedItemScrollsOnceThenYieldsToTheOtherItem()
    {
        var motion = new ReleaseLabelMotion();
        motion.Configure(100, 172, 80, 268, true, false);
        Assert.AreEqual(ReleaseLabelMode.Scroll, motion.Mode);
        Assert.AreEqual(0, motion.Frame.Offset);
        motion.Advance(7.125);
        Assert.AreEqual(-36, motion.Frame.Offset, 1e-6);
        motion.Advance(2.125);
        Assert.AreEqual(-72, motion.Frame.Offset, 1e-6);
        motion.Advance(1);
        Assert.AreEqual(-72, motion.Frame.Offset, 1e-6);
        motion.Advance(1.37);
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
        motion.Advance(1.56);
        Assert.AreEqual(1, motion.Frame.Item);
        motion.Advance(8.325);
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
        Assert.AreEqual(paused.Offset - 10, motion.Frame.Offset, 1e-6);
    }

    [TestMethod]
    public void ScrollingStartsAndStopsAtRestWithoutOvershootEvenForTinyOverflow()
    {
        foreach (var overflow in new[] { .25, 12, 72, 300 })
        {
            var motion = new ReleaseLabelMotion();
            motion.Configure(100, 100 + overflow, 80, 300 + overflow, true, false);
            motion.Advance(5);
            var previous = motion.Frame;
            motion.Advance(.001);
            Assert.IsTrue(Math.Abs(motion.Frame.Offset) < .00001, "Scrolling must accelerate from rest.");
            while (motion.Frame.NextUpdateSeconds == 0)
            {
                motion.Advance(1d / 120);
                var current = motion.Frame;
                Assert.IsTrue(current.Offset <= previous.Offset && current.Offset >= -overflow);
                Assert.IsTrue(previous.Offset - current.Offset <= 20d / 120 + .00001, "Do not exceed the reading speed.");
                previous = current;
            }
            Assert.AreEqual(-overflow, motion.Frame.Offset, 1e-6);
            Assert.AreEqual(1, motion.Frame.Opacity);
            // The final sample must ease into the tail hold, rather than hitting a hard stop.
            var end = new ReleaseLabelMotion();
            end.Configure(100, 100 + overflow, 80, 300 + overflow, true, false);
            end.Advance(5 + Math.Max(1.3, overflow / 20 + .65) - .001);
            Assert.IsTrue(Math.Abs(end.Frame.Offset + overflow) < .00001);
        }
    }

    [TestMethod]
    public void FadesEaseAtBothEndsAndSwapTextOnlyWhileInvisible()
    {
        var motion = new ReleaseLabelMotion();
        motion.Configure(100, 80, 75, 171, true, false);
        motion.Advance(5.001);
        Assert.IsTrue(motion.Frame.Opacity is > .9999 and < 1);
        motion.Advance(.238);
        Assert.AreEqual(0, motion.Frame.Item);
        Assert.IsTrue(motion.Frame.Opacity is > 0 and < .0001);
        motion.Advance(.002);
        Assert.AreEqual(1, motion.Frame.Item);
        Assert.IsTrue(motion.Frame.Opacity is > 0 and < .0001);
        var previous = motion.Frame.Opacity;
        for (var index = 0; index < 31; index++)
        {
            Assert.AreEqual(0, motion.Frame.Offset);
            Assert.AreEqual(0, motion.Frame.NextUpdateSeconds);
            motion.Advance(.01);
            Assert.IsTrue(motion.Frame.Opacity > previous);
            previous = motion.Frame.Opacity;
        }
        motion.Advance(.008);
        Assert.IsTrue(motion.Frame.Opacity is > .9999 and < 1);
    }

    [TestMethod]
    public void FrameRateDoesNotChangeMotionAndResizeRestartsAtReadableText()
    {
        var slow = new ReleaseLabelMotion();
        var fast = new ReleaseLabelMotion();
        slow.Configure(100, 172, 80, 268, true, false);
        fast.Configure(100, 172, 80, 268, true, false);
        for (var index = 0; index < 420; index++) slow.Advance(1d / 60);
        for (var index = 0; index < 1008; index++) fast.Advance(1d / 144);
        Assert.AreEqual(slow.Frame.Offset, fast.Frame.Offset, 1e-6);
        var before = slow.Frame;
        slow.Configure(100, 172, 80, 268, true, false);
        Assert.AreEqual(before, slow.Frame, "Unchanged settings must not restart motion.");
        slow.Configure(110, 172, 80, 268, true, false);
        Assert.AreEqual(0, slow.Frame.Offset);
        Assert.AreEqual(5, slow.Frame.NextUpdateSeconds);
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
                var motion = new ReleaseLabelMotion();
                var label = new ReleaseBrandLine("v1.5.3", "Radio Check", motion);
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
                Assert.IsFalse(label.IsRenderingSubscribed, "The reading hold must not consume composition frames.");
                void StartMoving()
                {
                    label.Visibility = Visibility.Hidden;
                    motion.Advance(5.1);
                    label.Visibility = Visibility.Visible;
                    Assert.IsTrue(label.IsRenderingSubscribed);
                }
                StartMoving();
                label.SetReduceMotion(true);
                Assert.IsFalse(label.IsAnimationScheduled);
                Assert.IsFalse(label.IsRenderingSubscribed);
                label.SetReduceMotion(false);
                Assert.IsTrue(label.IsAnimationScheduled);
                StartMoving();
                label.Visibility = Visibility.Hidden;
                Assert.IsFalse(label.IsAnimationScheduled);
                label.Visibility = Visibility.Visible;
                Assert.IsTrue(label.IsRenderingSubscribed);
                host.RootVisual = null;
                FlushDispatcher();
                Assert.IsFalse(label.IsLoaded);
                Assert.IsFalse(label.IsAnimationScheduled);
                Assert.IsFalse(label.IsRenderingSubscribed);
                // An inactive logical owner exercises the same gate as an unfocused application window.
                // It is never shown or activated, so this test does not disturb desktop focus.
                var inactiveWindow = new Window { Content = panel };
                host.RootVisual = panel;
                FlushDispatcher();
                Layout(35);
                Assert.AreSame(inactiveWindow, Window.GetWindow(label));
                Assert.IsFalse(inactiveWindow.IsActive);
                Assert.IsTrue(label.IsLoaded);
                Assert.IsFalse(label.IsAnimationScheduled);
                label.SetReleaseName("Radio Check / 无线电测试");
                Assert.IsFalse(label.IsAnimationScheduled, "Refreshing text while unfocused must not wake animations.");
                Assert.IsFalse(label.IsRenderingSubscribed);
                host.RootVisual = null;
                inactiveWindow.Content = null;
                inactiveWindow.Close();
                FlushDispatcher();
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
                CaptureMotionFrames(path);
            }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(10)));
        Assert.IsNull(failure, failure?.ToString());
    }

    private static void CaptureMotionFrames(string path)
    {
        foreach (var (name, version, release, width, times) in new[]
        {
            ("alternate", "v1.5.3", "Radio Check", 90, new[] { 0d, 5.08, 5.16, 5.24, 5.32, 5.4, 5.57 }),
            ("scroll", "v1.5.3-alpha-3.local.20260914", "Radio Check", 120, new[] { 0d, 5.2, 5.5, 6.2, 7d, 8d, 10.6 }),
            ("chinese", "v1.5.3", "无线电测试 Radio Check", 90, new[] { 0d, 5.16, 5.4, 5.57, 10.7, 12d, 14d })
        })
        {
            var motion = new ReleaseLabelMotion();
            var label = new ReleaseBrandLine(version, release, motion) { Width = width };
            var panel = new StackPanel { Background = new SolidColorBrush(Color.FromRgb(10, 13, 18)) };
            panel.Children.Add(label);
            var inactiveWindow = new Window { Content = panel };
            using var host = new HwndSource(new HwndSourceParameters("Release motion frames")
            { Width = width, Height = 20, WindowStyle = unchecked((int)0x80000000) });
            host.RootVisual = panel;
            FlushDispatcher();
            var previous = 0d;
            foreach (var time in times)
            {
                motion.Advance(time - previous);
                previous = time;
                label.InvalidateVisual();
                FlushDispatcher();
                var bitmap = new RenderTargetBitmap(width, 20, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(panel);
                using var file = File.Create(Path.Combine(path, $"{name}-{time:F2}.png"));
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(file);
            }
            host.RootVisual = null;
            inactiveWindow.Content = null;
            inactiveWindow.Close();
        }
    }

    private static void FlushDispatcher()
    {
        var frame = new DispatcherFrame();
        _ = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}
