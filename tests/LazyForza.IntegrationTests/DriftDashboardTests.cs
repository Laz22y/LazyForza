using LazyForza.App;
using LazyForza.Domain;
using LazyForza.Modules.Abstractions;
using LazyForza.Modules.Dashboard;
using LazyForza.Modules.DriftDashboard;
using LazyForza.Modules.LapAnalysis;
using LazyForza.Overlay;
using LazyForza.Storage;
using System.Threading.Channels;

namespace LazyForza.IntegrationTests;

[TestClass]
public sealed class DriftDashboardTests
{
    [TestMethod]
    public void DriftModuleIsDefaultOffAndHudStateHasNoRpmOrTorque()
    {
        var module = new DriftDashboardModule();

        Assert.IsFalse(module.Descriptor.DefaultEnabled);
        Assert.IsTrue(module.Descriptor.HasHudContribution);
        StringAssert.Contains(
            module.Descriptor.DisplayName,
            "Preview");
        StringAssert.Contains(
            module.Descriptor.Description,
            "开发预览");
        Assert.IsNull(module.Descriptor.MainPageKey);
        Assert.IsNull(module.Descriptor.SettingsPageKey);
        Assert.IsNull(typeof(DriftHudState).GetProperty("Rpm"));
        Assert.IsNull(typeof(DriftHudState).GetProperty("Torque"));
        Assert.AreEqual(
            HudContributionKind.DriftDashboard,
            module.Kind);
    }

    [TestMethod]
    public void AnalyzerBuildsStablePracticeFeedbackFromLocalVelocityAndSlip()
    {
        var analyzer = new DriftTelemetryAnalyzer();
        DriftHudState? state = null;
        for (var index = 0; index < 180; index++)
            state = analyzer.Observe(DriftFrame(index));

        Assert.IsNotNull(state);
        Assert.AreEqual(30, state.DriftAngleDegrees, 0.2);
        Assert.IsTrue(state.IsDrifting);
        Assert.AreEqual(DriftPracticePhase.Stable, state.Phase);
        Assert.IsTrue(state.StabilityScore >= 65);
        Assert.IsTrue(state.StableDriftSeconds > 1);
        Assert.IsTrue(
            state.BestStableDriftSeconds >= state.StableDriftSeconds);
        Assert.AreEqual(DriftGuidanceTone.Positive, state.GuidanceTone);
        StringAssert.Contains(state.Guidance, "稳定");
        Assert.IsTrue(OverlayVisibilityPolicy.ShouldShowDrift(
            state,
            state.UpdatedAt));
        var liveState = state with { Source = TelemetrySourceKind.Live };
        Assert.IsFalse(OverlayVisibilityPolicy.ShouldShowDrift(
            liveState,
            liveState.UpdatedAt.AddSeconds(1)));
    }

    [TestMethod]
    public void AnalyzerWarnsAboutExcessiveDrivenWheelSpin()
    {
        var analyzer = new DriftTelemetryAnalyzer();
        DriftHudState? state = null;
        for (var index = 0; index < 120; index++)
        {
            state = analyzer.Observe(DriftFrame(
                index,
                throttle: 0.86,
                rearLongitudinalSlip: 1.45));
        }

        Assert.IsNotNull(state);
        Assert.IsTrue(state.IsDrifting);
        Assert.AreEqual(DriftGuidanceTone.Warning, state.GuidanceTone);
        StringAssert.Contains(state.Guidance, "后轮空转");
    }

    [TestMethod]
    public async Task ActivationDefaultsToLapAndMainDashboardButNotDrift()
    {
        var setup = await CreateActivationAsync();
        await using var manager = setup.Manager;

        Assert.AreEqual(
            ModuleRuntimeState.Running,
            setup.Dashboard.Status.State);
        Assert.AreEqual(
            ModuleRuntimeState.Running,
            setup.Lap.Status.State);
        Assert.AreEqual(
            ModuleRuntimeState.Initialized,
            setup.Drift.Status.State);
        Assert.IsFalse(setup.Controller.IntroductionSeen);
        Assert.IsTrue(setup.Controller.AutoCloseDashboard);
    }

    [TestMethod]
    public async Task EnablingDriftSuspendsLapWithoutChangingItsPreference()
    {
        var setup = await CreateActivationAsync();
        await using var manager = setup.Manager;

        await setup.Controller.SetEnabledAsync(
            DriftDashboardModule.ModuleId,
            true,
            CancellationToken.None);

        Assert.AreEqual(
            ModuleRuntimeState.Running,
            setup.Drift.Status.State);
        Assert.AreEqual(
            ModuleRuntimeState.Initialized,
            setup.Lap.Status.State);
        Assert.AreEqual(
            ModuleRuntimeState.Initialized,
            setup.Dashboard.Status.State);
        Assert.IsNull(setup.Settings.Value(
            LapAnalysisModule.ModuleId,
            "enabled"));
        Assert.IsNull(setup.Settings.Value(
            DashboardModule.ModuleId,
            "enabled"));
        Assert.AreEqual(
            bool.TrueString,
            setup.Settings.Value(
                DriftDashboardModule.ModuleId,
                "enabled"));

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            async () => await setup.Controller.SetEnabledAsync(
                LapAnalysisModule.ModuleId,
                true,
                CancellationToken.None));

        await setup.Controller.SetEnabledAsync(
            DriftDashboardModule.ModuleId,
            false,
            CancellationToken.None);
        Assert.AreEqual(
            ModuleRuntimeState.Running,
            setup.Dashboard.Status.State);
        Assert.AreEqual(
            ModuleRuntimeState.Running,
            setup.Lap.Status.State);
    }

    [TestMethod]
    public async Task DriftCanRunBesideMainDashboardWhenAutoCloseIsOff()
    {
        var setup = await CreateActivationAsync();
        await using var manager = setup.Manager;

        await setup.Controller.SetAutoCloseDashboardAsync(
            false,
            CancellationToken.None);
        await setup.Controller.MarkIntroductionSeenAsync(
            CancellationToken.None);
        await setup.Controller.SetEnabledAsync(
            DriftDashboardModule.ModuleId,
            true,
            CancellationToken.None);

        Assert.IsTrue(setup.Controller.IntroductionSeen);
        Assert.IsFalse(setup.Controller.AutoCloseDashboard);
        Assert.AreEqual(
            ModuleRuntimeState.Running,
            setup.Dashboard.Status.State);
        Assert.AreEqual(
            ModuleRuntimeState.Running,
            setup.Drift.Status.State);
        Assert.AreEqual(
            ModuleRuntimeState.Initialized,
            setup.Lap.Status.State);
        Assert.AreEqual(
            bool.FalseString,
            setup.Settings.Value(
                DriftDashboardModule.ModuleId,
                DriftDashboardModule.AutoCloseDashboardSettingKey));
        Assert.AreEqual(
            bool.TrueString,
            setup.Settings.Value(
                DriftDashboardModule.ModuleId,
                DriftDashboardModule.IntroductionSeenSettingKey));
    }

    [TestMethod]
    public async Task QaModesCoverCombinedAndDriftOnlyLayouts()
    {
        var combined = await CreateActivationAsync(
            captureQa: true,
            captureDriftQa: true);
        await using (combined.Manager)
        {
            Assert.AreEqual(
                ModuleRuntimeState.Running,
                combined.Dashboard.Status.State);
            Assert.AreEqual(
                ModuleRuntimeState.Running,
                combined.Drift.Status.State);
            Assert.AreEqual(
                ModuleRuntimeState.Initialized,
                combined.Lap.Status.State);
        }

        var driftOnly = await CreateActivationAsync(
            captureQa: true,
            captureDriftOnlyQa: true);
        await using (driftOnly.Manager)
        {
            Assert.AreEqual(
                ModuleRuntimeState.Initialized,
                driftOnly.Dashboard.Status.State);
            Assert.AreEqual(
                ModuleRuntimeState.Running,
                driftOnly.Drift.Status.State);
            Assert.AreEqual(
                ModuleRuntimeState.Initialized,
                driftOnly.Lap.Status.State);
        }
    }

    [TestMethod]
    public async Task DriftActivationUnsubscribesTheRealLapModule()
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            $"lazyforza-drift-isolation-{Guid.NewGuid():N}.db");
        try
        {
            using var store = new LazyForzaStore(databasePath);
            await using var feed = new TrackingFeed();
            var lap = new LapAnalysisModule(
                store,
                TelemetrySourceKind.Simulator);
            var drift = new DriftDashboardModule();
            var dashboard = new FakeModule(
                DashboardModule.ModuleId,
                defaultEnabled: true);
            var manager = new ModuleManager(
                [dashboard, lap, drift],
                new StoreContext(feed, store));
            await manager.InitializeAsync(CancellationToken.None);
            var controller = new DriftDashboardActivationController(
                manager,
                store);
            await controller.InitializeAsync(
                captureQa: false,
                captureDriftQa: false,
                captureDriftOnlyQa: false,
                CancellationToken.None);

            Assert.IsTrue(feed.HasConsumer(LapAnalysisModule.ModuleId));
            await controller.SetEnabledAsync(
                DriftDashboardModule.ModuleId,
                true,
                CancellationToken.None);

            Assert.IsFalse(feed.HasConsumer(LapAnalysisModule.ModuleId));
            Assert.IsTrue(feed.HasConsumer(DriftDashboardModule.ModuleId));
            for (var index = 0; index < 240; index++)
                feed.Publish(DriftFrame(index));
            await Task.Delay(50);
            Assert.AreEqual(0, store.CountLaps());

            await manager.DisposeAsync();
        }
        finally
        {
            DeleteDatabaseFiles(databasePath);
        }
    }

    private static TelemetryFrame DriftFrame(
        int index,
        double throttle = 0.56,
        double rearLongitudinalSlip = 0.42)
    {
        const double forward = 30;
        var lateral = forward * Math.Tan(Math.PI / 6);
        var raw = new Fh6RawTelemetry
        {
            IsRaceOn = 1,
            TimestampMS = (uint)Math.Round(index * 1000d / 60),
            Velocity = new Vector3F((float)lateral, 0, (float)forward),
            AngularVelocity = new Vector3F(0, 0.55f, 0),
            Speed = (float)Math.Sqrt(
                forward * forward +
                lateral * lateral),
            TireSlipAngle = new WheelValues(
                0.20f,
                0.22f,
                0.48f,
                0.50f),
            TireCombinedSlip = new WheelValues(
                0.24f,
                0.25f,
                0.54f,
                0.56f),
            TireSlipRatio = new WheelValues(
                0.10f,
                0.10f,
                (float)rearLongitudinalSlip,
                (float)rearLongitudinalSlip),
            Gear = 3,
            Steer = 48,
            Accel = (byte)Math.Round(throttle * 255)
        };
        return new TelemetryFrame(
            index,
            DateTimeOffset.UnixEpoch.AddSeconds(index / 60d),
            TelemetrySourceKind.Simulator,
            raw,
            new NormalizedTelemetry(
                raw.Speed * 3.6,
                raw.Speed * 2.236936,
                0,
                throttle,
                0,
                0,
                0,
                0,
                default),
            ReadOnlyMemory<byte>.Empty);
    }

    private static async Task<ActivationSetup> CreateActivationAsync(
        bool captureQa = false,
        bool captureDriftQa = false,
        bool captureDriftOnlyQa = false)
    {
        var settings = new MemorySettings();
        var dashboard = new FakeModule(
            DashboardModule.ModuleId,
            defaultEnabled: true);
        var lap = new FakeModule(
            LapAnalysisModule.ModuleId,
            defaultEnabled: true);
        var drift = new FakeModule(
            DriftDashboardModule.ModuleId,
            defaultEnabled: false);
        var manager = new ModuleManager(
            [dashboard, lap, drift],
            new FakeContext(settings));
        await manager.InitializeAsync(CancellationToken.None);
        var controller = new DriftDashboardActivationController(
            manager,
            settings);
        await controller.InitializeAsync(
            captureQa,
            captureDriftQa,
            captureDriftOnlyQa,
            CancellationToken.None);
        return new ActivationSetup(
            manager,
            controller,
            settings,
            dashboard,
            lap,
            drift);
    }

    private sealed record ActivationSetup(
        ModuleManager Manager,
        DriftDashboardActivationController Controller,
        MemorySettings Settings,
        FakeModule Dashboard,
        FakeModule Lap,
        FakeModule Drift);

    private sealed class FakeModule(
        string id,
        bool defaultEnabled) : LazyForzaModuleBase(
        new ModuleDescriptor(
            id,
            id,
            "test",
            [],
            null,
            null,
            false,
            defaultEnabled))
    {
        protected override ValueTask OnStartAsync(
            CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        protected override ValueTask OnStopAsync(
            CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }

    private sealed class FakeContext(
        MemorySettings settings) : IModuleContext
    {
        public ITelemetryFeed Telemetry { get; } = new EmptyFeed();
        public IHudHost Hud { get; } = new EmptyHud();
        public IModuleSettingsStore Settings { get; } = settings;
        public IAnalysisStore AnalysisStore { get; } = new EmptyAnalysis();
        public Action<string> Log => _ => { };
    }

    private sealed class MemorySettings : IModuleSettingsStore
    {
        private readonly Dictionary<string, string> values = [];

        public ValueTask<string?> GetAsync(
            string moduleId,
            string key,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(Value(moduleId, key));

        public ValueTask SetAsync(
            string moduleId,
            string key,
            string value,
            CancellationToken cancellationToken)
        {
            values[$"{moduleId}:{key}"] = value;
            return ValueTask.CompletedTask;
        }

        public string? Value(string moduleId, string key) =>
            values.GetValueOrDefault($"{moduleId}:{key}");
    }

    private sealed class EmptyFeed : ITelemetryFeed
    {
        public TelemetryFrame? Latest => null;
        public TelemetryDiagnostics Diagnostics => new(
            "none",
            0,
            TelemetryStreamState.Disconnected,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            null,
            null);
        public ValueTask<ITelemetrySubscription> SubscribeAsync(
            string consumerId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class EmptyHud : IHudHost
    {
        public ValueTask AttachAsync(
            IHudContribution contribution,
            CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
        public ValueTask DetachAsync(
            string contributionId,
            CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
        public ValueTask SetLayoutAsync(
            OverlayLayout layout,
            CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }

    private sealed class EmptyAnalysis : IAnalysisStore
    {
        public ValueTask<string?> SaveShiftLearningAsync(
            ShiftLearningSnapshot snapshot,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<string?>(null);
        public ValueTask<bool> GetShiftRecommendationsEnabledAsync(
            string vehicleProfileId,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(true);
    }

    private sealed record StoreContext(
        ITelemetryFeed Telemetry,
        LazyForzaStore Store) : IModuleContext
    {
        public IHudHost Hud { get; } = new EmptyHud();
        public IModuleSettingsStore Settings => Store;
        public IAnalysisStore AnalysisStore => Store;
        public Action<string> Log => _ => { };
    }

    private sealed class TrackingFeed : ITelemetryFeed
    {
        private readonly object gate = new();
        private readonly Dictionary<
            string,
            List<Channel<TelemetryFrame>>> consumers =
            new(StringComparer.OrdinalIgnoreCase);

        public TelemetryFrame? Latest { get; private set; }

        public TelemetryDiagnostics Diagnostics => new(
            "drift isolation test",
            0,
            TelemetryStreamState.Replay,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            Latest?.ArrivalTime,
            null);

        public ValueTask<ITelemetrySubscription> SubscribeAsync(
            string consumerId,
            CancellationToken cancellationToken)
        {
            var channel = Channel.CreateUnbounded<TelemetryFrame>(
                new UnboundedChannelOptions
                {
                    SingleReader = true,
                    SingleWriter = true
                });
            lock (gate)
            {
                if (!consumers.TryGetValue(consumerId, out var channels))
                {
                    channels = [];
                    consumers[consumerId] = channels;
                }
                channels.Add(channel);
            }
            return ValueTask.FromResult<ITelemetrySubscription>(
                new TrackingSubscription(
                    channel.Reader,
                    () => Remove(consumerId, channel)));
        }

        public bool HasConsumer(string consumerId)
        {
            lock (gate)
                return consumers.TryGetValue(
                    consumerId,
                    out var channels) &&
                    channels.Count > 0;
        }

        public void Publish(TelemetryFrame frame)
        {
            Latest = frame;
            Channel<TelemetryFrame>[] targets;
            lock (gate)
            {
                targets = consumers.Values
                    .SelectMany(channels => channels)
                    .ToArray();
            }
            foreach (var target in targets)
                target.Writer.TryWrite(frame);
        }

        public ValueTask DisposeAsync()
        {
            Channel<TelemetryFrame>[] targets;
            lock (gate)
            {
                targets = consumers.Values
                    .SelectMany(channels => channels)
                    .ToArray();
                consumers.Clear();
            }
            foreach (var target in targets)
                target.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }

        private void Remove(
            string consumerId,
            Channel<TelemetryFrame> channel)
        {
            lock (gate)
            {
                if (!consumers.TryGetValue(
                        consumerId,
                        out var channels))
                    return;
                channels.Remove(channel);
                if (channels.Count == 0)
                    consumers.Remove(consumerId);
            }
            channel.Writer.TryComplete();
        }

        private sealed class TrackingSubscription(
            ChannelReader<TelemetryFrame> frames,
            Action remove) : ITelemetrySubscription
        {
            private int disposed;
            public ChannelReader<TelemetryFrame> Frames { get; } = frames;

            public ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(ref disposed, 1) == 0)
                    remove();
                return ValueTask.CompletedTask;
            }
        }
    }

    private static void DeleteDatabaseFiles(string databasePath)
    {
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            var file = databasePath + suffix;
            if (File.Exists(file)) File.Delete(file);
        }
    }
}
