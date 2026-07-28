using System.Threading.Channels;
using System.Windows;
using LazyForza.Domain;
using LazyForza.Modules.Abstractions;
using LazyForza.Modules.Dashboard;
using LazyForza.Modules.LapAnalysis;
using LazyForza.Overlay;

namespace LazyForza.IntegrationTests;

[TestClass]
public sealed class ModuleAndOverlayTests
{
    [TestMethod]
    public void DashboardConvertsEmpiricallyVerifiedTireTemperatureAndNormalizesOutputGauges()
    {
        var temperatures = DashboardDisplayValues.TireTemperatureCelsius(
            new WheelValues(96, 97, 104, 104));

        Assert.AreEqual(35.6, temperatures.FrontLeft, 0.1);
        Assert.AreEqual(36.1, temperatures.FrontRight, 0.1);
        Assert.AreEqual(40, temperatures.RearLeft, 0.1);
        Assert.AreEqual(40, temperatures.RearRight, 0.1);
        Assert.AreEqual(0d, DashboardDisplayValues.NonNegativeOutput(-0d));
        Assert.AreEqual(0d, DashboardDisplayValues.NonNegativeOutput(-12.5));
        Assert.AreEqual(318.4, DashboardDisplayValues.NonNegativeOutput(318.4));
        Assert.AreEqual(0d, DashboardDisplayValues.TireHeatIntensityCelsius(42));
        var warmTire = DashboardDisplayValues.TireHeatIntensityCelsius(80);
        Assert.IsTrue(warmTire is > 0 and < 1);
        Assert.AreEqual(1d, DashboardDisplayValues.TireHeatIntensityCelsius(120));
    }

    [TestMethod]
    public void DefaultConfigurationMatchesTheAcceptedLiveSettings()
    {
        var layout = LazyForzaDefaults.CreateOverlayLayout();

        Assert.AreEqual("127.0.0.1", LazyForzaDefaults.TelemetryListenAddress);
        Assert.AreEqual(2299, LazyForzaDefaults.TelemetryPort);
        Assert.AreEqual(2299, new TelemetryOptions().Port);
        Assert.AreEqual(579, layout.Left);
        Assert.AreEqual(669, layout.Top);
        Assert.AreEqual(1338.3333333333335, layout.Width);
        Assert.AreEqual(753.3333333333334, layout.Height);
        Assert.AreEqual(0.6, layout.Scale);
        Assert.AreEqual(1, layout.Opacity);
        Assert.IsTrue(layout.ClickThrough);
        Assert.IsTrue(layout.IsLocked);
        Assert.IsFalse(layout.ReduceMotion);
        Assert.IsTrue(layout.DashboardMotionEnabled);
        Assert.AreEqual(0.5, layout.DashboardMotionIntensity);
        Assert.AreEqual(2, layout.DashboardIdleWaitSeconds);
        Assert.AreEqual(0.8, layout.DashboardVisibilityFadeSeconds);
        Assert.AreEqual(1, layout.LapCompletedHoldSeconds);
        Assert.AreEqual(8, layout.LapNoMatchConfirmationSeconds);
        Assert.AreEqual(0.5, layout.LapNoMatchFadeSeconds);
        Assert.AreEqual(0.8, layout.LiveHudStaleSeconds);
        Assert.AreEqual("1.3.0", LazyForza.App.ApplicationVersionInfo.Display);
    }

    [TestMethod]
    public void OverlayScaleSupportsTwentyPercentWithOnePercentPrecision()
    {
        var layout = LazyForzaDefaults.CreateOverlayLayout();

        Assert.AreEqual(0.20, OverlayScaleSettings.Minimum);
        Assert.AreEqual(0.01, OverlayScaleSettings.Step);
        Assert.AreEqual(0.20, OverlayScaleSettings.Normalize(0.10), 1e-9);
        Assert.AreEqual(0.20, OverlayScaleSettings.Normalize(0.204), 1e-9);
        Assert.AreEqual(0.21, OverlayScaleSettings.Normalize(0.206), 1e-9);
        Assert.AreEqual(1.50, OverlayScaleSettings.Normalize(1.80), 1e-9);
        Assert.AreEqual(layout.Width * 0.20, OverlayScaleSettings.ScaledDimension(layout.Width, 0.20), 1e-9);
        Assert.AreEqual(layout.Height * 0.20, OverlayScaleSettings.ScaledDimension(layout.Height, 0.20), 1e-9);

        using var coordinator = new OverlayCoordinator(layout with { Scale = 0.206 });
        Assert.AreEqual(0.21, coordinator.CurrentLayout.Scale, 1e-9);
    }

    [TestMethod]
    public void LapAnalysisMapHeightAdaptsWithoutExceedingTheWindow()
    {
        foreach (var windowHeight in new[] { 640d, 800d, 1_080d, 1_440d })
        {
            var previewHeight = LazyForza.App.LapAnalysisVisualLayout.AdaptiveMapHeight(windowHeight);
            Assert.IsTrue(previewHeight >= 320);
            Assert.IsTrue(
                previewHeight + 48 <= windowHeight + 0.001,
                $"Preview {previewHeight:0.0}px must fit inside a {windowHeight:0.0}px window.");
        }

        Assert.AreEqual(460.8, LazyForza.App.LapAnalysisVisualLayout.AdaptiveMapHeight(640), 0.01);
        Assert.AreEqual(576, LazyForza.App.LapAnalysisVisualLayout.AdaptiveMapHeight(800), 0.01);
        Assert.AreEqual(777.6, LazyForza.App.LapAnalysisVisualLayout.AdaptiveMapHeight(1_080), 0.01);
        Assert.AreEqual(900, LazyForza.App.LapAnalysisVisualLayout.AdaptiveMapHeight(1_440), 0.01);
    }

    [TestMethod]
    public void LapAnalysisLegendIsCompactAndMovesAwayFromTheCurve()
    {
        var renderBounds = new Rect(20, 20, 840, 260);
        var lowerLeftCurve = Enumerable.Range(0, 40)
            .Select(index => new Point(32 + index * 5, 238))
            .ToArray();

        var legend = LazyForza.App.AnalysisOverlayDrawing.SelectSeriesLegendBounds(
            renderBounds,
            1,
            lowerLeftCurve,
            reservedBounds: null,
            LazyForza.App.AnalysisLegendCorner.BottomLeft,
            LazyForza.App.AnalysisLegendCorner.BottomRight);

        Assert.IsTrue(legend.Width <= 258);
        Assert.AreEqual(38, legend.Height, 0.001);
        Assert.IsTrue(legend.Left > renderBounds.Left + renderBounds.Width / 2);
        Assert.IsTrue(renderBounds.Contains(legend));
    }

    [TestMethod]
    public async Task ModuleLifecycleIsIdempotentAndFailureIsIsolated()
    {
        var good = new CountingModule("good", false);
        var bad = new CountingModule("bad", true);
        var context = new FakeContext();
        await good.InitializeAsync(context, CancellationToken.None);
        await bad.InitializeAsync(context, CancellationToken.None);
        await good.StartAsync(CancellationToken.None);
        await good.StartAsync(CancellationToken.None);
        Assert.AreEqual(1, good.Starts);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () => await bad.StartAsync(CancellationToken.None));
        Assert.AreEqual(ModuleRuntimeState.Running, good.Status.State);
        Assert.AreEqual(ModuleRuntimeState.Faulted, bad.Status.State);
        await good.StopAsync(CancellationToken.None);
        await good.StopAsync(CancellationToken.None);
        Assert.AreEqual(1, good.Stops);
        await good.DisposeAsync();
        await bad.DisposeAsync();
    }

    [TestMethod]
    public void OverlayStylesAndFrameLimiterMeetSpikeContract()
    {
        const long original = 0x100;
        var locked = OverlayNativeStyles.Apply(original, true);
        Assert.AreNotEqual(0, locked & OverlayNativeStyles.WsExTransparent);
        Assert.AreNotEqual(0, locked & OverlayNativeStyles.WsExNoActivate);
        Assert.AreNotEqual(0, locked & OverlayNativeStyles.WsExToolWindow);
        var unlocked = OverlayNativeStyles.Apply(locked, false);
        Assert.AreEqual(0, unlocked & OverlayNativeStyles.WsExTransparent);
        var limiter = new FrameRateLimiter(60);
        Assert.IsTrue(limiter.ShouldRender(0));
        Assert.IsFalse(limiter.ShouldRender(0.010));
        Assert.IsTrue(limiter.ShouldRender(0.017));
    }

    [TestMethod]
    public void OverlayVisibilityHidesFreeRoamPauseMenuAndStaleCompetition()
    {
        var now = DateTimeOffset.UtcNow;
        var learning = new ShiftLearningSnapshot(
            LearningState.Collecting, 0, 0, null, [], [], [], new Dictionary<string, int>(), "collecting");
        var dashboard = new DashboardHudState(
            now, TelemetrySourceKind.Live, "LIVE", false, true, 3, 3, "3", false, 120,
            5000, 8000, 200, 400, default, default, 0, 1, 2, 800, learning);
        Assert.IsTrue(OverlayVisibilityPolicy.ShouldShowDashboard(dashboard, now));
        Assert.IsFalse(OverlayVisibilityPolicy.ShouldShowDashboard(dashboard with { IsDriving = false }, now));
        Assert.IsTrue(OverlayVisibilityPolicy.ShouldShowDashboard(dashboard with { IsStale = true }, now));
        Assert.IsFalse(OverlayVisibilityPolicy.ShouldShowDashboard(dashboard with { UpdatedAt = now - TimeSpan.FromSeconds(1) }, now));

        var lap = new LapHudState(
            now, TelemetrySourceKind.Live, true, TrackLearningPhase.MatchingTrack,
            "matching", "drive", TrackMatchState.Candidate, 0.5, "track", 0, [], 1, 0, true);
        Assert.IsTrue(OverlayVisibilityPolicy.ShouldShowLap(lap, now));
        Assert.IsFalse(OverlayVisibilityPolicy.ShouldShowLap(lap with { IsCompetitionActive = false }, now));
        Assert.IsFalse(OverlayVisibilityPolicy.ShouldShowLap(lap with { UpdatedAt = now - TimeSpan.FromSeconds(1) }, now));
        Assert.IsTrue(OverlayVisibilityPolicy.ShouldShowLap(
            lap with { UpdatedAt = now - TimeSpan.FromSeconds(1) }, now, 1.5));
        Assert.IsTrue(OverlayVisibilityPolicy.ShouldShowDashboard(
            dashboard with { UpdatedAt = now - TimeSpan.FromSeconds(1), IsStale = true }, now, 1.5));
    }

    [TestMethod]
    public void HeldHandBrakeDoesNotKeepDashboardAwakeAndTransitionsWakeItOnce()
    {
        var dynamics = new DashboardHudDynamics();
        var dashboard = DashboardState() with
        {
            SpeedMps = 0,
            HandBrake = 1
        };
        var layout = new OverlayLayout(
            DashboardIdleWaitSeconds: 3,
            DashboardVisibilityFadeSeconds: 1);

        Assert.AreEqual(0, dynamics.Update(dashboard, true, layout, 0).Opacity, 1e-9);
        Assert.AreEqual(1, dynamics.Update(dashboard, true, layout, 1).Opacity, 1e-9);
        Assert.IsFalse(dynamics.Update(dashboard, true, layout, 2.99).IsIdleHidden);
        var fading = dynamics.Update(dashboard, true, layout, 3.25);
        Assert.IsTrue(fading.IsIdleHidden);
        Assert.AreEqual(0, dynamics.Update(dashboard, true, layout, 4).Opacity, 1e-9);

        var released = dashboard with { HandBrake = 0 };
        Assert.AreEqual(0, dynamics.Update(released, true, layout, 4).Opacity, 1e-9);
        Assert.AreEqual(0.5, dynamics.Update(released, true, layout, 4.5).Opacity, 1e-9);
        Assert.AreEqual(1, dynamics.Update(released, true, layout, 5).Opacity, 1e-9);

        Assert.AreEqual(1, dynamics.Update(dashboard, true, layout, 6).Opacity, 1e-9);
        Assert.IsFalse(dynamics.Update(dashboard, true, layout, 8.99).IsIdleHidden);
        Assert.IsTrue(dynamics.Update(dashboard, true, layout, 9.25).IsIdleHidden);
        Assert.IsFalse(DashboardHudDynamics.HasDriverActivity(dashboard));
    }

    [TestMethod]
    public void DashboardFadesAfterThreeIdleSecondsAndWakesOnAnyInput()
    {
        var dynamics = new DashboardHudDynamics();
        var dashboard = DashboardState() with
        {
            SpeedMps = 0,
            Steering = 0,
            Clutch = 0,
            HandBrake = 0
        };
        var layout = new OverlayLayout(
            DashboardIdleWaitSeconds: 3,
            DashboardVisibilityFadeSeconds: 1);

        Assert.AreEqual(0, dynamics.Update(dashboard, true, layout, 0).Opacity, 1e-9);
        Assert.AreEqual(1, dynamics.Update(dashboard, true, layout, 1).Opacity, 1e-9);
        Assert.IsFalse(dynamics.Update(dashboard, true, layout, 2.99).IsIdleHidden);
        var fading = dynamics.Update(dashboard, true, layout, 3.25);
        Assert.IsTrue(fading.IsIdleHidden);
        Assert.IsTrue(fading.Opacity > 0);
        Assert.IsTrue(fading.Opacity < 1);
        Assert.AreEqual(0, dynamics.Update(dashboard, true, layout, 4).Opacity, 1e-9);

        var input = dashboard with { Throttle = 0.5 };
        Assert.AreEqual(0, dynamics.Update(input, true, layout, 4).Opacity, 1e-9);
        Assert.AreEqual(0.5, dynamics.Update(input, true, layout, 4.5).Opacity, 1e-9);
        Assert.AreEqual(1, dynamics.Update(input, true, layout, 5).Opacity, 1e-9);
        Assert.AreEqual(0, dynamics.Update(input, false, layout, 5.1).Opacity, 1e-9);
        Assert.AreEqual(0.1, dynamics.Update(input, true, layout, 5.2).Opacity, 1e-9);

        Assert.IsTrue(DashboardHudDynamics.HasDriverActivity(dashboard with { Steering = 0.1 }));
        Assert.IsTrue(DashboardHudDynamics.HasDriverActivity(dashboard with { Clutch = 0.1 }));
        Assert.IsFalse(
            DashboardHudDynamics.HasDriverActivity(dashboard with { HandBrake = 0.1 }),
            "A held handbrake is static state; the dynamics controller wakes only when it changes.");
    }

    [TestMethod]
    public void DashboardIdleWaitAndFadeDurationsComeFromOverlaySettings()
    {
        var dynamics = new DashboardHudDynamics();
        var dashboard = DashboardState() with { SpeedMps = 0 };
        var layout = new OverlayLayout(
            DashboardIdleWaitSeconds: 5,
            DashboardVisibilityFadeSeconds: 2);

        Assert.AreEqual(0, dynamics.Update(dashboard, true, layout, 0).Opacity, 1e-9);
        Assert.AreEqual(0.5, dynamics.Update(dashboard, true, layout, 1).Opacity, 1e-9);
        Assert.AreEqual(1, dynamics.Update(dashboard, true, layout, 2).Opacity, 1e-9);
        Assert.IsFalse(dynamics.Update(dashboard, true, layout, 4.99).IsIdleHidden);
        var fading = dynamics.Update(dashboard, true, layout, 5.49);
        Assert.IsTrue(fading.IsIdleHidden);
        Assert.AreEqual(0.75, fading.Opacity, 1e-9);
        Assert.AreEqual(0, dynamics.Update(dashboard, true, layout, 7).Opacity, 1e-9);
    }

    [TestMethod]
    public void LapHudNoMatchFadeLocksToCompetitionSession()
    {
        var now = DateTimeOffset.UtcNow;
        var firstSession = Guid.NewGuid();
        var lap = new LapHudState(
            now, TelemetrySourceKind.Simulator, true, TrackLearningPhase.WaitingForStartLine,
            "waiting", "drive", TrackMatchState.Unknown, 0, "track", 0, [], 0, 0, false)
        {
            CompetitionSessionId = firstSession,
            MatchRejectionEligible = true
        };
        var layout = new OverlayLayout(
            LapNoMatchConfirmationSeconds: 1,
            LapNoMatchFadeSeconds: 0.5);
        var dynamics = new LapHudDynamics();

        Assert.AreEqual(1, dynamics.Update(lap, true, layout, 0).Opacity, 1e-9);
        Assert.AreEqual(1, dynamics.Update(lap, true, layout, 0.9).Opacity, 1e-9);
        var fading = dynamics.Update(lap, true, layout, 1.1);
        Assert.IsTrue(fading.IsSuppressedForCompetition);
        Assert.IsTrue(fading.Opacity is > 0 and < 1);
        Assert.AreEqual(0, dynamics.Update(lap, true, layout, 1.6).Opacity, 1e-9);

        var recoveredEvidence = lap with { MatchRejectionEligible = false };
        Assert.AreEqual(0, dynamics.Update(recoveredEvidence, true, layout, 2).Opacity, 1e-9);
        var pausedSameCompetition = recoveredEvidence with { IsCompetitionActive = false };
        Assert.IsTrue(dynamics.Update(pausedSameCompetition, false, layout, 2.05).IsSuppressedForCompetition);
        Assert.AreEqual(0, dynamics.Update(recoveredEvidence, true, layout, 2.08).Opacity, 1e-9);

        var nextCompetition = recoveredEvidence with { CompetitionSessionId = Guid.NewGuid() };
        var shownAgain = dynamics.Update(nextCompetition, true, layout, 2.1);
        Assert.AreEqual(1, shownAgain.Opacity, 1e-9);
        Assert.IsFalse(shownAgain.IsSuppressedForCompetition);
    }

    [TestMethod]
    public void DashboardMotionUsesAccelerationAndRespectsMotionSettings()
    {
        var dynamics = new DashboardHudDynamics();
        var dashboard = DashboardState() with
        {
            SpeedMps = 20,
            Acceleration = new Vector3F(9.80665f, 0, 4.903325f)
        };
        var layout = new OverlayLayout(DashboardMotionEnabled: true, DashboardMotionIntensity: 1);
        DashboardHudVisualState visual = default;
        for (var frame = 0; frame <= 30; frame++)
            visual = dynamics.Update(dashboard, true, layout, frame / 60d);

        Assert.IsTrue(visual.HorizontalOffset < -0.5);
        Assert.IsTrue(visual.VerticalOffset > 0.2);

        var disabled = layout with { DashboardMotionEnabled = false };
        for (var frame = 31; frame <= 120; frame++)
            visual = dynamics.Update(dashboard, true, disabled, frame / 60d);
        Assert.AreEqual(0, visual.HorizontalOffset, 0.001);
        Assert.AreEqual(0, visual.VerticalOffset, 0.001);

        visual = dynamics.Update(dashboard, true, layout, 121 / 60d);
        visual = dynamics.Update(dashboard, true, layout with { ReduceMotion = true }, 122 / 60d);
        Assert.AreEqual(0, visual.HorizontalOffset, 1e-9);
        Assert.AreEqual(0, visual.VerticalOffset, 1e-9);
    }

    [TestMethod]
    public void DashboardMotionHalfIntensityMatchesPreviousFullRangeAndFullIntensityDoublesIt()
    {
        var dashboard = DashboardState() with
        {
            SpeedMps = 20,
            Acceleration = new Vector3F(9.80665f, 0, 0)
        };

        static DashboardHudVisualState Settle(
            DashboardHudState state,
            double intensity)
        {
            var dynamics = new DashboardHudDynamics();
            var layout = new OverlayLayout(
                DashboardMotionEnabled: true,
                DashboardMotionIntensity: intensity);
            DashboardHudVisualState visual = default;
            for (var frame = 0; frame <= 120; frame++)
                visual = dynamics.Update(state, true, layout, frame / 60d);
            return visual;
        }

        var half = Settle(dashboard, 0.5);
        var full = Settle(dashboard, 1);

        Assert.AreEqual(-1, half.HorizontalOffset, 0.02,
            "新版 50% 应约等于旧版 100% 的一个位移单位。");
        Assert.AreEqual(-2, full.HorizontalOffset, 0.04,
            "新版 100% 应提供约两倍于旧版上限的位移。");
        Assert.AreEqual(2, full.HorizontalOffset / half.HorizontalOffset, 0.04);
    }

    [TestMethod]
    public void DashboardRecommendsAdjacentUpshiftAndDownshiftWithHysteresis()
    {
        var learning = new ShiftLearningSnapshot(
            LearningState.Ready, 1, 0.9, null, [], [],
            [new ShiftTarget(3, 4, 7800, 7400, 5600, 0.9, false)],
            new Dictionary<string, int>(), "ready");
        var state = new DashboardHudState(
            DateTimeOffset.UtcNow, TelemetrySourceKind.Live, "LIVE", false, true,
            3, 3, "3", false, 120, 7500, 8000, 200, 400,
            default, default, 0, 1, 2, 800, learning);

        Assert.AreEqual(4, state.RecommendedGear);
        Assert.IsTrue(state.UpshiftCueActive);
        Assert.IsFalse(state.DownshiftCueActive);

        var needsDownshift = state with { RawGear = 4, ForwardGear = 4, GearDisplay = "4", Rpm = 4800 };
        Assert.AreEqual(3, needsDownshift.RecommendedGear);
        Assert.IsFalse(needsDownshift.UpshiftCueActive);
        Assert.IsTrue(needsDownshift.DownshiftCueActive);

        var insideHysteresis = needsDownshift with { Rpm = 5200 };
        Assert.AreEqual(4, insideHysteresis.RecommendedGear);
        Assert.IsFalse(insideHysteresis.DownshiftCueActive);
        var held = needsDownshift with { IsGearDisplayHeld = true };
        Assert.IsNull(held.RecommendedGear);
        var disabledForProfile = state with { ShiftRecommendationsEnabled = false };
        Assert.IsNull(disabledForProfile.RecommendedGear);
        Assert.IsFalse(disabledForProfile.UpshiftCueActive);
        Assert.IsFalse(disabledForProfile.DownshiftCueActive);
    }

    private static DashboardHudState DashboardState()
    {
        var learning = new ShiftLearningSnapshot(
            LearningState.Collecting, 0, 0, null, [], [], [], new Dictionary<string, int>(), "collecting");
        return new DashboardHudState(
            DateTimeOffset.UtcNow, TelemetrySourceKind.Live, "LIVE", false, true,
            3, 3, "3", false, 0, 1000, 8000, 0, 0,
            default, default, 0, 0, 2, 800, learning);
    }

    private sealed class CountingModule(string id, bool throwOnStart) : LazyForzaModuleBase(
        new ModuleDescriptor(id, id, "test", [], null, null, false))
    {
        public int Starts { get; private set; }
        public int Stops { get; private set; }
        protected override ValueTask OnStartAsync(CancellationToken cancellationToken)
        {
            Starts++;
            return throwOnStart ? ValueTask.FromException(new InvalidOperationException("isolated")) : ValueTask.CompletedTask;
        }
        protected override ValueTask OnStopAsync(CancellationToken cancellationToken) { Stops++; return ValueTask.CompletedTask; }
    }

    private sealed class FakeContext : IModuleContext
    {
        public ITelemetryFeed Telemetry { get; } = new EmptyFeed();
        public IHudHost Hud { get; } = new EmptyHud();
        public IModuleSettingsStore Settings { get; } = new MemorySettings();
        public IAnalysisStore AnalysisStore { get; } = new EmptyAnalysisStore();
        public Action<string> Log => _ => { };
    }

    private sealed class EmptyFeed : ITelemetryFeed
    {
        public TelemetryFrame? Latest => null;
        public TelemetryDiagnostics Diagnostics => new("none", 0, TelemetryStreamState.Disconnected, 0, 0, 0, 0, 0, 0, 0, null, null);
        public ValueTask<ITelemetrySubscription> SubscribeAsync(string consumerId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class EmptyHud : IHudHost
    {
        public ValueTask AttachAsync(IHudContribution contribution, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask DetachAsync(string contributionId, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask SetLayoutAsync(OverlayLayout layout, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
    private sealed class MemorySettings : IModuleSettingsStore
    {
        private readonly Dictionary<string, string> values = [];
        public ValueTask<string?> GetAsync(string moduleId, string key, CancellationToken cancellationToken) => ValueTask.FromResult(values.GetValueOrDefault(moduleId + ":" + key));
        public ValueTask SetAsync(string moduleId, string key, string value, CancellationToken cancellationToken) { values[moduleId + ":" + key] = value; return ValueTask.CompletedTask; }
    }
    private sealed class EmptyAnalysisStore : IAnalysisStore
    {
        public ValueTask<string?> SaveShiftLearningAsync(
            ShiftLearningSnapshot snapshot,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(VehicleProfileIdentity.TryCreate(snapshot.Fingerprint));
        public ValueTask<bool> GetShiftRecommendationsEnabledAsync(string vehicleProfileId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(true);
    }
}
