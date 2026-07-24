using LazyForza.Domain;
using LazyForza.Modules.Abstractions;
using LazyForza.Modules.Dashboard;
using LazyForza.Modules.LapAnalysis;
using LazyForza.Storage;
using LazyForza.Telemetry;

namespace LazyForza.IntegrationTests;

[TestClass]
public sealed class EndToEndTests
{
    [TestMethod]
    public void FreeRoamDoesNotStartTrackLearningAndCompetitionDoes()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"lazyforza-context-{Guid.NewGuid():N}.db");
        try
        {
            using var store = new LazyForzaStore(databasePath);
            var module = new LapAnalysisModule(store);
            var parser = new Fh6PacketParser();
            var freeRoamPacket = Fh6PacketBuilder.BuildDemoPacket(60);
            freeRoamPacket[314] = 0;
            Assert.IsTrue(parser.TryParse(freeRoamPacket, 0, DateTimeOffset.UtcNow, TelemetrySourceKind.Live, out var freeRoam, out _));
            module.Observe(freeRoam!);
            var hidden = module.Snapshot as LapHudState;
            Assert.IsNotNull(hidden);
            Assert.IsFalse(hidden.IsCompetitionActive);
            Assert.AreEqual(TrackLearningPhase.WaitingForCompetition, hidden.Phase);
            Assert.IsNull(module.CurrentTrack);
            Assert.AreEqual(0, store.CountTracks());

            var competitionPacket = Fh6PacketBuilder.BuildDemoPacket(120);
            competitionPacket[314] = 1;
            Fh6PacketBuilder.WriteFloat(competitionPacket, 308, 2);
            Fh6PacketBuilder.WriteFloat(competitionPacket, 304, 2);
            Assert.IsTrue(parser.TryParse(competitionPacket, 1, DateTimeOffset.UtcNow, TelemetrySourceKind.Live, out var competition, out _));
            module.Observe(competition!);
            var visible = module.Snapshot as LapHudState;
            Assert.IsNotNull(visible);
            Assert.IsTrue(visible.IsCompetitionActive);
            Assert.AreEqual(TrackLearningPhase.WaitingForStartLine, visible.Phase);
        }
        finally
        {
            foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
            {
                var file = databasePath + suffix;
                if (File.Exists(file)) File.Delete(file);
            }
        }
    }

    [TestMethod]
    public async Task SimulatorPipelineLearnsRouteSavesLapAndReleasesBothModules()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"lazyforza-e2e-{Guid.NewGuid():N}.db");
        try
        {
            using var store = new LazyForzaStore(databasePath);
            var source = new FastLapSource();
            // Keep the full deterministic six-lap burst so a busy parallel test
            // process cannot drop the opening frames that establish the start line.
            await using var hub = new TelemetryHub(source, new TelemetryOptions(SubscriberCapacity: 2048));
            var hud = new CapturingHud();
            var context = new TestContext(hub, hud, store, store);
            var dashboard = new DashboardModule();
            var lap = new LapAnalysisModule(store);
            var manager = new ModuleManager([dashboard, lap], context);
            await manager.InitializeAsync(CancellationToken.None);
            await manager.SetEnabledAsync(DashboardModule.ModuleId, true, CancellationToken.None);
            await manager.SetEnabledAsync(LapAnalysisModule.ModuleId, true, CancellationToken.None);
            source.Start();

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            while ((lap.CurrentTrack is null || store.CountLaps() == 0 || lap.VisibleLaps.Count == 0) && !timeout.IsCancellationRequested)
                await Task.Delay(25);

            Assert.IsNotNull(dashboard.Snapshot as DashboardHudState);
            Assert.IsNotNull(lap.CurrentTrack);
            Assert.AreEqual("simulator", lap.CurrentTrack.Source);
            Assert.IsTrue(store.CountTracks() >= 1, $"track count={store.CountTracks()}");
            Assert.IsTrue(store.CountLaps() >= 1, $"lap count={store.CountLaps()}");
            var lapSummary = string.Join(" | ", lap.VisibleLaps.Select(saved =>
                $"valid={saved.IsValid}, reason={saved.InvalidReason}, segments=[{string.Join(",", saved.Segments.Select(segment => $"{segment.TimeSeconds:0.000}/{segment.IsValid}"))}]"));
            Assert.IsTrue(lap.VisibleLaps.Any(saved => saved.IsValid && saved.Segments.All(segment => segment.IsValid && segment.TimeSeconds > 0)),
                $"visible={lap.VisibleLaps.Count}; {lapSummary}");
            Assert.AreEqual(2, hud.Contributions.Count);

            await manager.SetEnabledAsync(LapAnalysisModule.ModuleId, false, CancellationToken.None);
            Assert.AreEqual(1, hud.Contributions.Count);
            Assert.AreEqual(ModuleRuntimeState.Running, dashboard.Status.State);
            await manager.SetEnabledAsync(DashboardModule.ModuleId, false, CancellationToken.None);
            Assert.AreEqual(0, hud.Contributions.Count);
            await manager.DisposeAsync();
        }
        finally
        {
            foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
            {
                var file = databasePath + suffix;
                if (File.Exists(file)) File.Delete(file);
            }
        }
    }

    private sealed class FastLapSource : ITelemetrySource
    {
        private readonly Fh6PacketParser parser = new();
        private readonly TaskCompletionSource start =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TelemetrySourceKind Kind => TelemetrySourceKind.Simulator;
        public string Description => "E2E deterministic laps";

        public void Start() => start.TrySetResult();

        public async Task RunAsync(Func<TelemetryFrame, ValueTask> publish, Action<string> onInvalid, CancellationToken cancellationToken)
        {
            await start.Task.WaitAsync(cancellationToken);
            const int framesPerLap = 180;
            for (var index = 0; index < framesPerLap * 6 && !cancellationToken.IsCancellationRequested; index++)
            {
                var lapFrame = index % framesPerLap;
                var angle = lapFrame / (double)framesPerLap * Math.PI * 2;
                var packet = Fh6PacketBuilder.BuildDemoPacket(index);
                Fh6PacketBuilder.WriteUInt32(packet, 4, (uint)(index * 16));
                Fh6PacketBuilder.WriteFloat(packet, 244, (float)(150 * Math.Cos(angle)));
                Fh6PacketBuilder.WriteFloat(packet, 248, (float)(3 * Math.Sin(angle * 2)));
                Fh6PacketBuilder.WriteFloat(packet, 252, (float)(110 * Math.Sin(angle)));
                Fh6PacketBuilder.WriteFloat(packet, 304, lapFrame / 10f);
                packet[312] = (byte)(index / framesPerLap);
                packet[313] = 0;
                if (parser.TryParse(packet, index, DateTimeOffset.UtcNow, Kind, out var frame, out var error)) await publish(frame!);
                else onInvalid(error ?? "parse");
                // The production crossing debounce is two seconds of arrival time.
                // Keep each synthetic lap above that boundary instead of relying on
                // scheduler speed or treating several lap resets as one crossing.
                await Task.Delay(12, cancellationToken);
            }
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed record TestContext(ITelemetryFeed Telemetry, IHudHost Hud, IModuleSettingsStore Settings, IAnalysisStore AnalysisStore) : IModuleContext
    {
        public Action<string> Log => _ => { };
    }

    private sealed class CapturingHud : IHudHost
    {
        public Dictionary<string, IHudContribution> Contributions { get; } = [];
        public ValueTask AttachAsync(IHudContribution contribution, CancellationToken cancellationToken) { Contributions[contribution.Id] = contribution; return ValueTask.CompletedTask; }
        public ValueTask DetachAsync(string contributionId, CancellationToken cancellationToken) { Contributions.Remove(contributionId); return ValueTask.CompletedTask; }
        public ValueTask SetLayoutAsync(OverlayLayout layout, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}
