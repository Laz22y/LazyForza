using System.Net;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using LazyForza.App;
using LazyForza.Domain;
using LazyForza.Telemetry;

namespace LazyForza.IntegrationTests;

[TestClass]
public sealed class InitializationEndpointTests
{
    [TestMethod]
    [DataRow("zh-Hans")]
    [DataRow("en")]
    public void EditingPortAndAddressMovesTheRealListenerAndSavesTheReceivedEndpoint(string language) =>
        Dialog(language, window =>
        {
            var port = Input(window, "wizard.telemetry.port", "UDP 端口");
            var address = Input(window, "wizard.telemetry.address", "监听 IP");
            var first = FreePort(); var second = FreePort();
            while (second == first) second = FreePort();
            port.Text = first.ToString();
            Until(() => Listening(window, "127.0.0.1", first));
            port.Text = second.ToString();
            Assert.IsTrue(HasText(window, AppLocalization.Format("wizard.telemetry.pendingEndpoint", "准备监听 {0}:{1}", "127.0.0.1", second)));
            Until(() => Listening(window, "127.0.0.1", second));
            using (Bind(first)) { } // No stale socket on the old port.
            address.Text = "0.0.0.0"; // Same-port rebind must await socket disposal.
            Until(() => Listening(window, "0.0.0.0", second));
            Send(second);
            Until(() => window.Result is not null);
            Assert.AreEqual(second, window.Result!.Port);
            Assert.AreEqual("0.0.0.0", window.Result.ListenAddress);
        });

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void LateResultOrFailureFromPreviousPortCannotCompleteOrOverwriteNewAttempt(bool failOld) => WpfTestHost.Run(() =>
    {
        var requests = new List<Request>();
        RunDialog("zh-Hans", window =>
        {
            var port = Input(window, "wizard.telemetry.port", "UDP 端口");
            Until(() => requests.Count == 1);
            port.Text = "5250";
            Assert.IsTrue(requests[0].Token.IsCancellationRequested);
            if (failOld) requests[0].Completion.SetException(new SocketException((int)SocketError.AddressAlreadyInUse));
            else requests[0].Completion.SetResult(Packet());
            Until(() => requests.Count == 2);
            Assert.IsNull(window.Result);
            Assert.IsTrue(port.IsEnabled);
            Assert.AreEqual(5250, requests[1].Port);
            Assert.IsTrue(Listening(window, "127.0.0.1", 5250));
            // A timer queued by an edit must not reopen the listener after leaving this step.
            port.Text = "5251";
            Click(window, "wizard.back", "上一步");
            requests[1].Completion.TrySetCanceled();
            PumpFor(TimeSpan.FromMilliseconds(500));
            Assert.AreEqual(2, requests.Count);
            Assert.IsNull(window.Result);
        }, (address, port, token) =>
        {
            var request = new Request(port, token); requests.Add(request);
            return request.Completion.Task;
        });
    });

    [TestMethod]
    public void InvalidEditsStopOldListenerAndOccupiedPortCanRecoverWithoutChangingPages() =>
        Dialog("zh-Hans", window =>
        {
            var port = Input(window, "wizard.telemetry.port", "UDP 端口");
            var first = FreePort(); port.Text = first.ToString();
            Until(() => Listening(window, "127.0.0.1", first));
            port.Text = "not-a-port";
            Assert.IsTrue(HasText(window, AppLocalization.Text("wizard.telemetry.invalid", "IP 或端口无效")));
            Until(() => CanBind(first));
            using var occupied = Bind(0);
            var target = ((IPEndPoint)occupied.Client.LocalEndPoint!).Port;
            port.Text = target.ToString();
            Until(() => HasText(window, AppLocalization.Text("wizard.telemetry.failed", "无法开始监听")));
            Assert.IsNull(window.Result);
            occupied.Dispose();
            Click(window, "wizard.telemetry.retry", "重新监听");
            Until(() => Listening(window, "127.0.0.1", target));
            Click(window, "wizard.telemetry.skip", "跳过连接并进入");
            Until(() => window.Result is not null);
            Assert.AreEqual(target, window.Result!.Port);
            using (Bind(target)) { }
        });

    [TestMethod]
    public void ClosingDuringSuccessAnimationCancelsCompletion() => Dialog("zh-Hans", window =>
    {
        var port = FreePort(); Input(window, "wizard.telemetry.port", "UDP 端口").Text = port.ToString();
        Until(() => Listening(window, "127.0.0.1", port)); Send(port);
        Until(() => HasText(window, AppLocalization.Text("wizard.telemetry.complete", "初始化完成")));
        Assert.IsFalse(Input(window, "wizard.telemetry.port", "UDP 端口").IsEnabled);
        window.Close();
        PumpFor(TimeSpan.FromMilliseconds(1350));
        Assert.IsNull(window.Result);
        using (Bind(port)) { }
    });

    private static void Dialog(string language, Action<InitializationWindow> action) =>
        WpfTestHost.Run(() => RunDialog(language, action));

    private static void RunDialog(string language, Action<InitializationWindow> action,
        Func<string, int, CancellationToken, Task<TelemetryProbeResult>>? probe = null)
    {
        var window = new InitializationWindow(StartupProfile.CreateDefault() with { Language = language }, probe: probe);
        Exception? error = null;
        _ = window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            try
            {
                Click(window, "wizard.start", "开始设置");
                for (var index = 0; index < 4; index++) Click(window, "wizard.next", "下一步");
                action(window);
            }
            catch (Exception failure) { error = failure; }
            finally { if (window.IsVisible) window.Close(); }
        }));
        window.ShowDialog();
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }

    private sealed record Request(int Port, CancellationToken Token)
    {
        public TaskCompletionSource<TelemetryProbeResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private static bool HasText(DependencyObject root, string value) => Descendants<TextBlock>(root).Any(text => text.Text == value);
    private static bool Listening(DependencyObject root, string address, int port) => HasText(root,
        AppLocalization.Format("wizard.telemetry.endpoint", "正在监听 {0}:{1}", address, port));
    private static TextBox Input(DependencyObject root, string key, string fallback) => Descendants<TextBox>(root)
        .Single(input => AutomationProperties.GetName(input) == AppLocalization.Text(key, fallback));
    private static void Click(DependencyObject root, string key, string fallback) => Descendants<Button>(root)
        .Single(button => button.Content as string == AppLocalization.Text(key, fallback)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static IEnumerable<T> Descendants<T>(DependencyObject root)
    {
        if (root is T match) yield return match;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var result in Descendants<T>(child)) yield return result;
    }
    private static void Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(5) };
        timer.Tick += (_, _) => { if (condition() || DateTime.UtcNow >= deadline) frame.Continue = false; };
        timer.Start(); try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
        Assert.IsTrue(condition(), "Timed out waiting for the initialization endpoint state.");
    }
    private static void PumpFor(TimeSpan duration)
    {
        var end = DateTime.UtcNow + duration; Until(() => DateTime.UtcNow >= end);
    }
    private static UdpClient Bind(int port)
    {
        var client = new UdpClient(AddressFamily.InterNetwork) { ExclusiveAddressUse = true };
        try { client.Client.Bind(new IPEndPoint(IPAddress.Loopback, port)); return client; }
        catch { client.Dispose(); throw; }
    }
    private static bool CanBind(int port)
    {
        try { using var client = Bind(port); return true; }
        catch (SocketException) { return false; }
    }
    private static int FreePort() { using var client = Bind(0); return ((IPEndPoint)client.Client.LocalEndPoint!).Port; }
    private static void Send(int port)
    {
        using var sender = new UdpClient(); var bytes = Fh6PacketBuilder.BuildDemoPacket(12);
        sender.Send(bytes, new IPEndPoint(IPAddress.Loopback, port));
    }
    private static TelemetryProbeResult Packet()
    {
        Assert.IsTrue(new Fh6PacketParser().TryParse(Fh6PacketBuilder.BuildDemoPacket(12), 12, DateTimeOffset.UtcNow,
            TelemetrySourceKind.Live, out var frame, out _));
        return new(new IPEndPoint(IPAddress.Loopback, 1), frame!, DateTimeOffset.UtcNow);
    }
}
