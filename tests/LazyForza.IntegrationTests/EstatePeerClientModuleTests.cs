using System.IO;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LazyForza.App;
using LazyForza.Analysis;
using LazyForza.EstatePeer;
using LazyForza.EstatePeer.Host;
using LazyForza.Modules.EstateRace;
using LazyForza.Storage;

namespace LazyForza.IntegrationTests;

public sealed partial class EstateRaceClientModuleTests
{
    [TestMethod]
    [DoNotParallelize]
    public async Task PeerHostWindowShowsCopyableControlPasswordInBothLanguages()
    {
        var root = Path.Combine(Path.GetTempPath(), "LazyForza-peer-window-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var track = CreateTrack();
            var definition = CreateDefinition(track);
            using var store = new LazyForzaStore(Path.Combine(root, "client.db"));
            store.SaveTrack(track, TrackAlgorithms.CreateSectors(track), definition);
            var packages = new EstateTrackPackageService(store, "1.5.4");
            var package = Path.Combine(root, "track.lfzestate");
            packages.Export(track.Id, package, default);
            var settings = new PeerHostStart(Path.Combine(root, "room"), "Window test", "test-password", 0, "192.0.2.10", null,
                track.Id.ToString("D"), track.Name, definition.MapRevision, packages.Identify(track.Id).TrackFingerprintSha256, package, 3, 3, false);
            await using var host = await PeerHostProcess.StartAsync(Path.Combine(AppContext.BaseDirectory, "LazyForza.EstatePeer.Host.exe"), settings, default);
            var expected = (await host.CommandAsync(new("controlAccess"), default)).ControlPassword;
            Assert.IsNotNull(expected);
            WpfTestHost.Run(() =>
            {
                foreach (var language in new[] { "zh-Hans", "en" })
                foreach (var width in new[] { 520, 680 })
                {
                    AppLocalization.UseLanguage(language);
                    string? copied = null;
                    var window = new EstatePeerWindow(new("", "", "Test driver", "#42D7E8", null), [], [], null,
                        () => host, (_, _) => throw new AssertFailedException(), (_, _) => Task.CompletedTask,
                        () => Task.CompletedTask, () => true, () => "Room ready", clipboardWriter: value => copied = value);
                    var scroll = (ScrollViewer)window.Content;
                    var access = Children<Expander>(scroll).Single(item => (string)item.Header == AppLocalization.Literal("总控登录"));
                    Assert.IsFalse(access.IsExpanded);
                    access.IsExpanded = true;
                    Children<Button>(access).Single(item => (string)item.Content == AppLocalization.Literal("查看总控密码"))
                        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    var password = Children<TextBox>(access).Single();
                    var frame = new DispatcherFrame();
                    var started = DateTime.UtcNow;
                    var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(20), DispatcherPriority.Background, (_, _) =>
                    {
                        if (password.Text.Length > 0 || DateTime.UtcNow - started > TimeSpan.FromSeconds(6)) frame.Continue = false;
                    }, Dispatcher.CurrentDispatcher);
                    Dispatcher.PushFrame(frame); timer.Stop();
                    Assert.AreEqual(expected, password.Text);
                    Assert.IsTrue(password.IsReadOnly);
                    Children<Button>(access).Single(item => (string)item.Content == AppLocalization.Literal("复制总控密码"))
                        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.AreEqual(expected, copied);
                    // Render a fixture value so no live credential appears in QA images.
                    password.Text = "0123456789ABCDEF0123456789ABCDEF";
                    foreach (var code in Children<TextBox>(scroll).Where(item => item.Text.StartsWith("LFZP", StringComparison.Ordinal)))
                        code.Text = new PeerInvitation
                        {
                            RoomId = Guid.Parse("00000000-0000-0000-0000-000000000001"), Generation = 1,
                            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1), PublicKeySha256 = new string('A', 64),
                            Candidates = [new(System.Net.IPAddress.Parse("192.0.2.10"), 24878)], SupportsUdp = true
                        }.Encode();
                    scroll.Background = (Brush)Application.Current.Resources["WindowBrush"];
                    scroll.Width = width; scroll.Height = 700;
                    scroll.Measure(new Size(width, 700)); scroll.Arrange(new Rect(0, 0, width, 700)); scroll.UpdateLayout();
                    Assert.AreEqual(0, scroll.ScrollableWidth, .5);
                    foreach (var control in Children<Control>(scroll).Where(item => item.ActualWidth > 0))
                        Assert.IsTrue(control.ActualWidth <= width);
                    var output = Environment.GetEnvironmentVariable("LAZYFORZA_PEER_QA");
                    if (!string.IsNullOrEmpty(output))
                    {
                        Directory.CreateDirectory(output);
                        var bitmap = new RenderTargetBitmap(width, 700, 96, 96, PixelFormats.Pbgra32); bitmap.Render(scroll);
                        using var file = File.Create(Path.Combine(output, $"peer-host-{language}-{width}.png"));
                        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(file);
                    }
                    window.Close();
                }
            });
        }
        finally { Directory.Delete(root, recursive: true); }

        static IEnumerable<T> Children<T>(DependencyObject parent)
        {
            if (parent is T item) yield return item;
            foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
                foreach (var element in Children<T>(child)) yield return element;
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PeerModuleUsesPinnedTransportAndKeepsRecoveryIdentitySeparateFromServerMode(bool useUdp)
    {
        var root = Path.Combine(Path.GetTempPath(), "LazyForza-peer-module-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var track = CreateTrack();
            var definition = CreateDefinition(track);
            using var store = new LazyForzaStore(Path.Combine(root, "client.db"));
            store.SaveTrack(track, TrackAlgorithms.CreateSectors(track), definition);
            var packages = new EstateTrackPackageService(store, "1.5.4");
            var package = Path.Combine(root, "track.lfzestate");
            packages.Export(track.Id, package, default);
            var hash = packages.Identify(track.Id).TrackFingerprintSha256;
            var settings = new PeerHostStart(Path.Combine(root, "room"), "Module test", "test-password", 0, "192.0.2.10", null,
                track.Id.ToString("D"), track.Name, definition.MapRevision, hash, package, 3, 3, false);
            await using var room = new PeerRoom(settings);
            await room.StartAsync(default);
            if (useUdp && !PeerQuicHost.IsSupported) { Assert.Inconclusive("QUIC unavailable."); return; }
            await using var udp = useUdp ? new PeerQuicJoin(room.Invitation) : null;
            PeerConnection connection;
            if (udp is not null)
            {
                Assert.IsTrue(room.Control(new("acceptReceipt", udp.Receipt.Encode())).Success);
                connection = await udp.ConnectAsync(default);
            }
            else connection = new PeerConnection(room.Invitation, new PeerEndpoint(IPAddress.Loopback, room.Port));
            var descriptor = await EstateRaceModule.ReadServerDescriptorAsync(connection.Origin.AbsoluteUri, default, connection, settings.Password);
            Assert.AreEqual(hash, descriptor.ActiveTrackPackageHash);
            await store.SetAsync(EstateRaceModule.ModuleId, "resumeToken", "legacy-server-token", default);
            await store.SetAsync(EstateRaceModule.ModuleId, "serverAddress", "https://existing.example", default);
            await using var feed = new TestFeed();
            await using var module = new EstateRaceModule(() => new EstateRaceTrackContext(track, definition, 0, 0, 0, true, null, SectorCount: 3));
            await module.InitializeAsync(new TestContext(feed, store), default);
            _ = await module.LoadSavedProfileAsync(default);
            await module.StartAsync(default);
            await module.ConnectAsync(new(connection.Origin.AbsoluteUri, settings.Password, "Peer driver", "#42D7E8", null, Peer: connection), default, hash);
            Assert.AreEqual(EstateRaceConnectionState.Connected, module.State.ConnectionState);
            Assert.AreEqual("legacy-server-token", await store.GetAsync(EstateRaceModule.ModuleId, "resumeToken", default));
            Assert.AreEqual("https://existing.example", await store.GetAsync(EstateRaceModule.ModuleId, "serverAddress", default));
            var key = $"peerResume.{connection.RecoveryScope}.driver";
            Assert.IsFalse(string.IsNullOrWhiteSpace(await store.GetAsync(EstateRaceModule.ModuleId, key, default)));
            await module.DisconnectAsync();
            Assert.AreEqual(string.Empty, await store.GetAsync(EstateRaceModule.ModuleId, key, default));
            Assert.AreEqual("legacy-server-token", await store.GetAsync(EstateRaceModule.ModuleId, "resumeToken", default));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
