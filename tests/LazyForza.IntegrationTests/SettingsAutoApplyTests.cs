using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LazyForza.App;

namespace LazyForza.IntegrationTests;

[TestClass]
public sealed class SettingsAutoApplyTests
{
    [TestMethod]
    public void RapidEditsAndLeavingPageSaveOnlyLatestValue() => EstateHudRenderingTests.Sta(() =>
    {
        var edits = 0; var current = 0; var saved = 0;
        using var automatic = new SettingsAutoApply(() => { edits++; saved = current; return Task.CompletedTask; }, _ => Assert.Fail());
        for (current = 1; current <= 10; current++) automatic.Request();
        current = 10;
        automatic.FlushAsync().GetAwaiter().GetResult();
        Assert.AreEqual(1, edits); Assert.AreEqual(10, saved);
        automatic.FlushAsync().GetAwaiter().GetResult();
        Assert.AreEqual(1, edits);
    });

    [TestMethod]
    public void EditWhileApplyingIsSerializedAndIncludedInFinalFlush() => EstateHudRenderingTests.Sta(() =>
    {
        var gate = new TaskCompletionSource();
        var edits = new List<int>(); var current = 1;
        using var automatic = new SettingsAutoApply(async () =>
        {
            edits.Add(current);
            if (edits.Count == 1) await gate.Task;
        }, _ => Assert.Fail());
        automatic.Request(); var pending = automatic.FlushAsync();
        current = 2; automatic.Request(); current = 3; automatic.Request();
        Assert.AreSame(pending, automatic.FlushAsync());
        gate.SetResult(); PumpUntil(pending);
        CollectionAssert.AreEqual(new[] { 1, 3 }, edits);
    });

    [TestMethod]
    public void ApplyFailureIsReportedAndNextEditCanRecover() => EstateHudRenderingTests.Sta(() =>
    {
        var attempts = 0; var errors = 0;
        using var automatic = new SettingsAutoApply(() =>
        {
            if (++attempts == 1) throw new InvalidOperationException("failed");
            return Task.CompletedTask;
        }, _ => errors++);
        automatic.Request(); automatic.FlushAsync().GetAwaiter().GetResult();
        automatic.Request(); automatic.FlushAsync().GetAwaiter().GetResult();
        Assert.AreEqual(2, attempts); Assert.AreEqual(1, errors);
        automatic.Dispose(); automatic.Request(); automatic.FlushAsync().GetAwaiter().GetResult();
        Assert.AreEqual(2, attempts);
    });

    [TestMethod]
    public void InputAppliesWithoutClickOrFocusChange() => EstateHudRenderingTests.Sta(() =>
    {
        var saved = new TaskCompletionSource();
        using var automatic = new SettingsAutoApply(() => { saved.SetResult(); return Task.CompletedTask; }, _ => Assert.Fail(), TimeSpan.FromMilliseconds(5));
        automatic.Request(); PumpUntil(saved.Task);
    });

    [TestMethod]
    [DataRow("监听地址")]
    [DataRow("UDP 端口")]
    [DataRow("Listen address")]
    public void NetworkLabelAndInputUseSameVerticalCenter(string title) => EstateHudRenderingTests.Sta(() =>
    {
        var input = new TextBox { Text = "127.0.0.1" };
        var row = MainWindow.NetworkSettingRow(title, input);
        row.Measure(new Size(500, 100)); row.Arrange(new Rect(0, 0, 500, row.DesiredSize.Height)); row.UpdateLayout();
        var label = (TextBlock)row.Children[0];
        var labelCenter = label.TranslatePoint(new Point(0, label.ActualHeight / 2), row).Y;
        var inputCenter = input.TranslatePoint(new Point(0, input.ActualHeight / 2), row).Y;
        Assert.AreEqual(labelCenter, inputCenter, 0.5);
        Assert.AreEqual(VerticalAlignment.Center, input.VerticalContentAlignment);
    });

    private static void PumpUntil(Task task)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(5) };
        var deadline = DateTime.UtcNow.AddSeconds(5);
        timer.Tick += (_, _) => { if (task.IsCompleted || DateTime.UtcNow > deadline) frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame); timer.Stop();
        Assert.IsTrue(task.IsCompleted, "Timed out waiting for dispatcher work.");
        task.GetAwaiter().GetResult();
    }
}
