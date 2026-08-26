using System.Threading.Channels;
using System.Text.Json;
using System.Windows;
using LazyForza.Domain;
using LazyForza.Modules.Abstractions;
using LazyForza.Modules.Dashboard;
using LazyForza.Modules.DriftDashboard;
using LazyForza.Modules.LapAnalysis;
using LazyForza.Overlay;

namespace LazyForza.IntegrationTests;

[TestClass]
public sealed class ModuleAndOverlayTests
{
    [TestMethod]
    public void EstateRaceDefaultLayoutMatchesTheLatestVerifiedDevelopmentLayout()
    {
        var layout = EstateRaceHudLayoutSettings.Default;
        AssertPlacement(EstateRaceHudWidgetKind.Leaderboard, .011370009059528202, .010416666666666666, .939803114386994);
        AssertPlacement(EstateRaceHudWidgetKind.TrackMap, .024218749999999956, .6561111111111111, .96);
        AssertPlacement(EstateRaceHudWidgetKind.GripStatus, .808, .5859722222222222, .96);
        AssertPlacement(EstateRaceHudWidgetKind.Banner, .25, .010416666666666666, 1);
        AssertPlacement(EstateRaceHudWidgetKind.StartLights, .35, .12, 1);
        AssertPlacement(EstateRaceHudWidgetKind.PitStopInfo, .785, .39980555555555547, 1);
        AssertPlacement(EstateRaceHudWidgetKind.PitLimiter, .46906250000000005, .3713888888888889, 1);
        AssertPlacement(EstateRaceHudWidgetKind.PenaltyStatus, .365, .5055555555555555, 1);
        AssertPlacement(EstateRaceHudWidgetKind.PracticeProgram, .34, .1026388888888889, 1);
        AssertPlacement(EstateRaceHudWidgetKind.PitWindowSuggestion, .790, .160, 1);
        AssertPlacement(EstateRaceHudWidgetKind.FullRaceStrategy, .296, .7735833333333334, .60);
        return;

        void AssertPlacement(EstateRaceHudWidgetKind kind, double left, double top, double scale)
        {
            var placement = layout.Get(kind);
            Assert.IsTrue(placement.IsVisible);
            Assert.AreEqual(left, placement.Left, 1e-12, kind.ToString());
            Assert.AreEqual(top, placement.Top, 1e-12, kind.ToString());
            Assert.AreEqual(scale, placement.Scale, 1e-12, kind.ToString());
            Assert.AreEqual(1, placement.Opacity, 1e-12, kind.ToString());
        }
    }

    [TestMethod]
    public void EstateRaceLayoutPreviewUsesStableIdentityAndSharedWidgetGeometry()
    {
        var now = new DateTimeOffset(2026, 8, 13, 12, 0, 0, TimeSpan.Zero);
        var first = OverlayLayoutPreviewState.EstateRace(now);
        var second = OverlayLayoutPreviewState.EstateRace(now.AddSeconds(1));

        CollectionAssert.AreEqual(
            first.Session!.Participants.Select(item => item.Id).ToArray(),
            second.Session!.Participants.Select(item => item.Id).ToArray(),
            "布局预览刷新时不应把相同示例车手当成全新车手。");
        Assert.AreEqual(first.Session.Banner!.Id, second.Session.Banner!.Id);

        const double width = 1920;
        const double height = 1080;
        var leaderboard = HudSurface.EstateRaceWidgetNominalSize(
            EstateRaceHudWidgetKind.Leaderboard,
            width,
            height);
        Assert.AreEqual(width * .235, leaderboard.Width, 1e-9);
        Assert.AreEqual(
            height * (.053 + .026) + Math.Max(36, height * .045) * 12,
            leaderboard.Height,
            1e-9);

        var pitStop = HudSurface.EstateRaceWidgetNominalSize(
            EstateRaceHudWidgetKind.PitStopInfo,
            width,
            height);
        Assert.AreEqual(width * .215, pitStop.Width, 1e-9);
        Assert.AreEqual(height * (.041 + .0665 * 2), pitStop.Height, 1e-9);

        var pitWindow = HudSurface.EstateRaceWidgetNominalSize(
            EstateRaceHudWidgetKind.PitWindowSuggestion,
            width,
            height);
        Assert.AreEqual(width * .19, pitWindow.Width, 1e-9);
        Assert.AreEqual(height * .14, pitWindow.Height, 1e-9);
    }

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
        Assert.AreEqual(622.5, layout.Left);
        Assert.AreEqual(688, layout.Top);
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
        Assert.AreEqual(622.5, layout.LapHudLeft);
        Assert.AreEqual(688, layout.LapHudTop);
        Assert.AreEqual(0.6, layout.LapHudScale);
        Assert.IsTrue(layout.LapHudAttachedToDashboard);
        Assert.AreEqual(1245, layout.DriftHudLeft);
        Assert.AreEqual(669, layout.DriftHudTop);
        Assert.AreEqual(0.6, layout.DriftHudScale);
        Assert.AreEqual(0, layout.EstateRaceHudLeft);
        Assert.AreEqual(0, layout.EstateRaceHudTop);
        Assert.AreEqual(2048, layout.EstateRaceHudWidth);
        Assert.AreEqual(1152, layout.EstateRaceHudHeight);
        var widgets = DashboardWidgetLayoutSettings.Normalize(
            layout.DashboardWidgets);
        foreach (var kind in Enum.GetValues<DashboardWidgetKind>())
        {
            var placement = widgets.Get(kind);
            Assert.IsTrue(placement.IsVisible, $"{kind} should be visible by default.");
            Assert.AreEqual(0, placement.OffsetX, 1e-9);
            Assert.AreEqual(0, placement.OffsetY, 1e-9);
        }
        Assert.AreEqual("1.5.0", LazyForza.App.ApplicationVersionInfo.Display);
    }

    [TestMethod]
    public void OverlayScaleSupportsContinuousEditorAndOnePercentSettingsSlider()
    {
        var layout = LazyForzaDefaults.CreateOverlayLayout();

        Assert.AreEqual(0.20, OverlayScaleSettings.Minimum);
        Assert.AreEqual(0.01, OverlayScaleSettings.Step);
        Assert.AreEqual(0.20, OverlayScaleSettings.Normalize(0.10), 1e-9);
        Assert.AreEqual(0.204, OverlayScaleSettings.Normalize(0.204), 1e-9);
        Assert.AreEqual(0.206, OverlayScaleSettings.Normalize(0.206), 1e-9);
        Assert.AreEqual(0.20, OverlayScaleSettings.SnapToStep(0.204), 1e-9);
        Assert.AreEqual(0.21, OverlayScaleSettings.SnapToStep(0.206), 1e-9);
        Assert.AreEqual(1.50, OverlayScaleSettings.Normalize(1.80), 1e-9);
        Assert.AreEqual(layout.Width * 0.20, OverlayScaleSettings.ScaledDimension(layout.Width, 0.20), 1e-9);
        Assert.AreEqual(layout.Height * 0.20, OverlayScaleSettings.ScaledDimension(layout.Height, 0.20), 1e-9);

        using var coordinator = new OverlayCoordinator(layout with
        {
            Scale = 0.206,
            ClickThrough = false,
            IsLocked = false
        });
        Assert.AreEqual(0.206, coordinator.CurrentLayout.Scale, 1e-9);
        Assert.IsTrue(coordinator.CurrentLayout.ClickThrough);
        Assert.IsTrue(coordinator.CurrentLayout.IsLocked);
    }

    [TestMethod]
    public void LegacyOverlayJsonKeepsLapHudAttachedToDashboard()
    {
        const string json =
            """
            {
              "Left": 120,
              "Top": 240,
              "Width": 1000,
              "Height": 500,
              "Scale": 0.75
            }
            """;
        var legacy = JsonSerializer.Deserialize<OverlayLayout>(json);
        Assert.IsNotNull(legacy);

        var normalized = OverlayLayoutGeometry.Normalize(legacy);
        var dashboard = OverlayLayoutGeometry.Bounds(
            normalized,
            OverlayHudKind.Dashboard);
        var lap = OverlayLayoutGeometry.Bounds(
            normalized,
            OverlayHudKind.Lap);
        var drift = OverlayLayoutGeometry.Bounds(
            normalized,
            OverlayHudKind.Drift);

        Assert.IsTrue(normalized.LapHudAttachedToDashboard);
        Assert.AreEqual(dashboard, lap);
        Assert.AreEqual(dashboard.Right + 24, drift.Left, 1e-9);
        Assert.AreEqual(dashboard.Top, drift.Top, 1e-9);
        Assert.AreEqual(dashboard.Width, drift.Width, 1e-9);
        var widgets = normalized.DashboardWidgets!;
        foreach (var kind in Enum.GetValues<DashboardWidgetKind>())
        {
            var placement = widgets.Get(kind);
            Assert.IsTrue(placement.IsVisible);
            Assert.AreEqual(0, placement.OffsetX, 1e-9);
            Assert.AreEqual(0, placement.OffsetY, 1e-9);
        }
    }

    [TestMethod]
    public void DashboardWidgetsMoveAndHideIndependentlyAndCanReset()
    {
        var defaults = DashboardWidgetLayoutSettings.CreateDefault();
        var steering = defaults.Get(DashboardWidgetKind.Steering) with
        {
            IsVisible = false,
            OffsetX = -0.12,
            OffsetY = -0.08
        };
        var customized = DashboardWidgetLayoutSettings.Normalize(
            defaults.Set(DashboardWidgetKind.Steering, steering));

        Assert.IsFalse(customized.Get(DashboardWidgetKind.Steering).IsVisible);
        Assert.AreEqual(
            -0.12,
            customized.Get(DashboardWidgetKind.Steering).OffsetX,
            1e-9);
        Assert.AreEqual(
            -0.08,
            customized.Get(DashboardWidgetKind.Steering).OffsetY,
            1e-9);
        foreach (var kind in Enum.GetValues<DashboardWidgetKind>()
                     .Where(kind => kind != DashboardWidgetKind.Steering))
            Assert.AreEqual(defaults.Get(kind), customized.Get(kind));

        var reset = DashboardWidgetLayoutSettings.CreateDefault();
        foreach (var kind in Enum.GetValues<DashboardWidgetKind>())
            Assert.AreEqual(defaults.Get(kind), reset.Get(kind));
    }

    [TestMethod]
    public void DashboardWidgetOffsetsStayFiniteAndInsideDashboardCanvas()
    {
        var source = new DashboardWidgetLayout().Set(
            DashboardWidgetKind.SpeedGear,
            new DashboardWidgetPlacement(
                IsVisible: true,
                OffsetX: double.NaN,
                OffsetY: 10));

        var normalized = DashboardWidgetLayoutSettings.Normalize(source);
        var speed = normalized.Get(DashboardWidgetKind.SpeedGear);
        var bounds = DashboardWidgetLayoutSettings.DefaultBounds(
            DashboardWidgetKind.SpeedGear);

        Assert.AreEqual(0, speed.OffsetX, 1e-9);
        Assert.AreEqual(1 - bounds.Bottom, speed.OffsetY, 1e-9);
        Assert.IsTrue(bounds.Left + speed.OffsetX >= 0);
        Assert.IsTrue(bounds.Right + speed.OffsetX <= 1);
        Assert.IsTrue(bounds.Top + speed.OffsetY >= 0);
        Assert.IsTrue(bounds.Bottom + speed.OffsetY <= 1);
    }

    [TestMethod]
    public void OverlayHudScalingKeepsEachCenterFixed()
    {
        var layout = OverlayLayoutGeometry.DetachLap(new OverlayLayout(
            Left: 100,
            Top: 200,
            Width: 1000,
            Height: 500,
            Scale: 0.6)) with
        {
            LapHudLeft = 420,
            LapHudTop = 90,
            LapHudScale = 0.4
        };
        var dashboardBefore = OverlayLayoutGeometry.Bounds(
            layout,
            OverlayHudKind.Dashboard);
        var lapBefore = OverlayLayoutGeometry.Bounds(
            layout,
            OverlayHudKind.Lap);
        var driftBefore = OverlayLayoutGeometry.Bounds(
            layout,
            OverlayHudKind.Drift);

        var dashboardScaled = OverlayLayoutGeometry.ScaleAroundCenter(
            layout,
            OverlayHudKind.Dashboard,
            0.9);
        var dashboardAfter = OverlayLayoutGeometry.Bounds(
            dashboardScaled,
            OverlayHudKind.Dashboard);
        var lapAfterDashboardScale = OverlayLayoutGeometry.Bounds(
            dashboardScaled,
            OverlayHudKind.Lap);
        Assert.AreEqual(dashboardBefore.CenterX, dashboardAfter.CenterX, 1e-9);
        Assert.AreEqual(dashboardBefore.CenterY, dashboardAfter.CenterY, 1e-9);
        Assert.AreEqual(lapBefore, lapAfterDashboardScale);
        Assert.AreEqual(
            driftBefore,
            OverlayLayoutGeometry.Bounds(
                dashboardScaled,
                OverlayHudKind.Drift));

        var lapScaled = OverlayLayoutGeometry.ScaleAroundCenter(
            dashboardScaled,
            OverlayHudKind.Lap,
            0.55);
        var lapAfter = OverlayLayoutGeometry.Bounds(
            lapScaled,
            OverlayHudKind.Lap);
        Assert.AreEqual(lapBefore.CenterX, lapAfter.CenterX, 1e-9);
        Assert.AreEqual(lapBefore.CenterY, lapAfter.CenterY, 1e-9);
        Assert.IsFalse(lapScaled.LapHudAttachedToDashboard);

        var driftScaled = OverlayLayoutGeometry.ScaleAroundCenter(
            lapScaled,
            OverlayHudKind.Drift,
            0.7);
        var driftAfter = OverlayLayoutGeometry.Bounds(
            driftScaled,
            OverlayHudKind.Drift);
        Assert.AreEqual(
            driftBefore.CenterX,
            driftAfter.CenterX,
            1e-9);
        Assert.AreEqual(
            driftBefore.CenterY,
            driftAfter.CenterY,
            1e-9);
        Assert.AreEqual(0.7, driftScaled.DriftHudScale!.Value, 1e-9);
    }

    [TestMethod]
    public void AttachLapHudCopiesDashboardPlacementAndScale()
    {
        var detached = new OverlayLayout(
            Left: 90,
            Top: 140,
            Width: 1000,
            Height: 500,
            Scale: 0.72,
            LapHudLeft: 600,
            LapHudTop: 300,
            LapHudScale: 0.35,
            LapHudAttachedToDashboard: false);

        var attached = OverlayLayoutGeometry.AttachLapToDashboard(detached);

        Assert.IsTrue(attached.LapHudAttachedToDashboard);
        Assert.AreEqual(
            OverlayLayoutGeometry.Bounds(attached, OverlayHudKind.Dashboard),
            OverlayLayoutGeometry.Bounds(attached, OverlayHudKind.Lap));
    }

    [TestMethod]
    public void OverlayCornerResizeProjectsBothAxesWithoutAxisSwitching()
    {
        const double startScale = 0.6;
        const double width = 1_200;
        const double height = 675;

        var proportional = OverlayResizeMath.ScaleFromDrag(
            startScale,
            width,
            height,
            120,
            67.5,
            resizeHorizontally: true,
            resizeVertically: true);
        Assert.AreEqual(0.7, proportional, 1e-9);

        var horizontalOnly = OverlayResizeMath.ScaleFromDrag(
            startScale,
            width,
            height,
            120,
            0,
            resizeHorizontally: true,
            resizeVertically: true);
        var expectedProjection = startScale + 120 * width / (width * width + height * height);
        Assert.AreEqual(expectedProjection, horizontalOnly, 1e-9);

        var side = OverlayResizeMath.ScaleFromDrag(
            startScale,
            width,
            height,
            37,
            0,
            resizeHorizontally: true,
            resizeVertically: false);
        Assert.AreEqual(startScale + 37 / width, side, 1e-9);

        var centeredSide = OverlayResizeMath.ScaleFromCenteredDrag(
            startScale,
            width,
            height,
            60,
            0,
            resizeHorizontally: true,
            resizeVertically: false);
        Assert.AreEqual(startScale + 120 / width, centeredSide, 1e-9);
    }

    [TestMethod]
    public void OverlayLayoutPreviewAlwaysProvidesAllThreeHuds()
    {
        var now = DateTimeOffset.UtcNow;
        var dashboard = OverlayLayoutPreviewState.Dashboard(null, now);
        var lap = OverlayLayoutPreviewState.Lap(null, now);
        var drift = OverlayLayoutPreviewState.Drift(null, now);

        Assert.IsTrue(dashboard.IsDriving);
        Assert.IsFalse(dashboard.IsStale);
        Assert.AreEqual(now, dashboard.UpdatedAt);
        Assert.IsTrue(lap.IsCompetitionActive);
        Assert.IsTrue(lap.Sectors.Count >= 4);
        Assert.IsNotNull(lap.CumulativeHistoricalDeltaSeconds);
        Assert.IsTrue(lap.CurrentLapSeconds > 0);
        Assert.IsTrue(drift.IsDriving);
        Assert.IsTrue(drift.IsDrifting);
        Assert.AreEqual(DriftPracticePhase.Stable, drift.Phase);
        Assert.IsTrue(drift.DriftAngleDegrees > 0);
        Assert.IsTrue(drift.StabilityScore >= 75);
        Assert.AreEqual(DriftSpinRiskLevel.Safe, drift.SpinRiskLevel);
        Assert.AreEqual(DriftSteeringCue.Left, drift.SteeringCue);
        Assert.AreEqual(DriftGearCue.ShiftUp, drift.GearCue);
        Assert.IsTrue(drift.AngleScorePotential > 0.5);
        Assert.IsTrue(drift.CanBuildAngle);
    }

    [TestMethod]
    public void DriftHudMovesIndependentlyAndParticipatesInUnionBounds()
    {
        var original = OverlayLayoutGeometry.Normalize(new OverlayLayout(
            Left: 100,
            Top: 150,
            Width: 1_000,
            Height: 500,
            Scale: 0.5));
        var dashboardBefore = OverlayLayoutGeometry.Bounds(
            original,
            OverlayHudKind.Dashboard);
        var lapBefore = OverlayLayoutGeometry.Bounds(
            original,
            OverlayHudKind.Lap);

        var moved = OverlayLayoutGeometry.Move(
            original,
            OverlayHudKind.Drift,
            900,
            260);
        var drift = OverlayLayoutGeometry.Bounds(
            moved,
            OverlayHudKind.Drift);
        var union = OverlayLayoutGeometry.UnionBounds(moved);

        Assert.AreEqual(dashboardBefore, OverlayLayoutGeometry.Bounds(
            moved,
            OverlayHudKind.Dashboard));
        Assert.AreEqual(lapBefore, OverlayLayoutGeometry.Bounds(
            moved,
            OverlayHudKind.Lap));
        Assert.AreEqual(900, drift.Left, 1e-9);
        Assert.AreEqual(260, drift.Top, 1e-9);
        Assert.AreEqual(drift.Right, union.Right, 1e-9);
        Assert.IsTrue(union.Left <= dashboardBefore.Left);
    }

    [TestMethod]
    public void EstateRaceViewportRemainsIndependentFromDashboardChanges()
    {
        var original = OverlayLayoutGeometry.Normalize(new OverlayLayout(
            Left: 100,
            Top: 150,
            Width: 1_000,
            Height: 500,
            Scale: 0.5,
            EstateRaceHudLeft: 20,
            EstateRaceHudTop: 30,
            EstateRaceHudWidth: 1_920,
            EstateRaceHudHeight: 1_080));
        var estateBefore = OverlayLayoutGeometry.Bounds(
            original,
            OverlayHudKind.EstateRace);

        var moved = OverlayLayoutGeometry.Move(
            original,
            OverlayHudKind.Dashboard,
            740,
            480);
        var scaled = OverlayLayoutGeometry.ScaleAroundCenter(
            moved,
            OverlayHudKind.Dashboard,
            0.8);

        Assert.AreEqual(
            estateBefore,
            OverlayLayoutGeometry.Bounds(scaled, OverlayHudKind.EstateRace));
        Assert.AreEqual(20, estateBefore.Left, 1e-9);
        Assert.AreEqual(30, estateBefore.Top, 1e-9);
        Assert.AreEqual(1_920, estateBefore.Width, 1e-9);
        Assert.AreEqual(1_080, estateBefore.Height, 1e-9);
    }

    [TestMethod]
    public void EstateRaceStartLightsAreAnIndependentConfigurableWidget()
    {
        var defaults = EstateRaceHudLayoutSettings.Normalize(null);
        foreach (var kind in Enum.GetValues<EstateRaceHudWidgetKind>())
            Assert.IsTrue(defaults.Get(kind).IsVisible, $"{kind} should be visible by default.");

        var customized = EstateRaceHudLayoutSettings.Normalize(defaults.Set(
            EstateRaceHudWidgetKind.StartLights,
            defaults.Get(EstateRaceHudWidgetKind.StartLights) with
            {
                Left = .41,
                Top = .08,
                Scale = 1.25,
                Opacity = .55
            }));
        var startLights = customized.Get(EstateRaceHudWidgetKind.StartLights);
        Assert.AreEqual(.41, startLights.Left, 1e-9);
        Assert.AreEqual(.08, startLights.Top, 1e-9);
        Assert.AreEqual(1.25, startLights.Scale, 1e-9);
        Assert.AreEqual(.55, startLights.Opacity, 1e-9);
        Assert.AreEqual(defaults.Get(EstateRaceHudWidgetKind.Leaderboard),
            customized.Get(EstateRaceHudWidgetKind.Leaderboard));
    }

    [TestMethod]
    public void LegacyEstateRaceViewportMigratesOnceAndJoinsUnionBounds()
    {
        var migrated = OverlayLayoutGeometry.Normalize(new OverlayLayout(
            Left: 320,
            Top: 240,
            Width: 1_000,
            Height: 500,
            Scale: 0.6));
        var migratedEstate = OverlayLayoutGeometry.Bounds(
            migrated,
            OverlayHudKind.EstateRace);
        Assert.AreEqual(
            OverlayLayoutGeometry.Bounds(migrated, OverlayHudKind.Dashboard),
            migratedEstate);

        var independent = migrated with
        {
            EstateRaceHudLeft = 0,
            EstateRaceHudTop = 0,
            EstateRaceHudWidth = 1_920,
            EstateRaceHudHeight = 1_080
        };
        var union = OverlayLayoutGeometry.UnionBounds(independent);
        Assert.AreEqual(0, union.Left, 1e-9);
        Assert.AreEqual(0, union.Top, 1e-9);
        Assert.AreEqual(1_920, union.Width, 1e-9);
        Assert.AreEqual(1_080, union.Height, 1e-9);
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
    public void LinkedAnalysisCursorPublishesChangesOnceAndCanBeCleared()
    {
        var cursor = new LazyForza.App.LapAnalysisCursor();
        var source = new object();
        var lapId = Guid.NewGuid();
        var changes = new List<LazyForza.App.LapAnalysisCursorPosition?>();
        var commits = new List<LazyForza.App.LapAnalysisCursorPosition>();
        cursor.Changed += (_, position) => changes.Add(position);
        cursor.CommitRequested += (_, position) => commits.Add(position);

        cursor.Set(source, lapId, 123.45);
        cursor.Set(source, lapId, 123.45);
        Assert.HasCount(0, commits, "Hover updates must not commit a replay seek.");
        cursor.Commit(source);
        cursor.Clear(source);
        cursor.Commit(source);
        cursor.Clear(source);

        Assert.HasCount(2, changes);
        Assert.HasCount(1, commits);
        Assert.AreEqual(123.45, commits[0].ProgressMeters, 0.001);
        Assert.AreEqual(lapId, changes[0]!.Value.LapId);
        Assert.AreEqual(123.45, changes[0]!.Value.ProgressMeters, 0.001);
        Assert.IsNull(changes[1]);
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
        var locked = OverlayNativeStyles.Apply(original);
        Assert.AreNotEqual(0, locked & OverlayNativeStyles.WsExTransparent);
        Assert.AreNotEqual(0, locked & OverlayNativeStyles.WsExNoActivate);
        Assert.AreNotEqual(0, locked & OverlayNativeStyles.WsExToolWindow);
        var reapplied = OverlayNativeStyles.Apply(locked);
        Assert.AreNotEqual(0, reapplied & OverlayNativeStyles.WsExTransparent);
        var limiter = new FrameRateLimiter(60);
        Assert.IsTrue(limiter.ShouldRender(0));
        Assert.IsFalse(limiter.ShouldRender(0.010));
        Assert.IsTrue(limiter.ShouldRender(0.017));
        var animatedLimiter = new FrameRateLimiter(30);
        Assert.IsTrue(animatedLimiter.ShouldRender(0));
        Assert.IsFalse(animatedLimiter.ShouldRender(0.020));
        Assert.IsTrue(animatedLimiter.ShouldRender(0.034));
        var transitionLimiter = new FrameRateLimiter(60);
        Assert.IsTrue(transitionLimiter.ShouldRender(0));
        Assert.IsTrue(transitionLimiter.ShouldRender(0.017));
    }

    [TestMethod]
    public void EstateRaceWidgetAnimationKeepsPersistentAndConditionalHudTransitionsStable()
    {
        var controller = new EstateRaceHudAnimationController();
        var entering = controller.Update(
            EstateRaceHudWidgetKind.Leaderboard,
            visible: true,
            nowSeconds: 0,
            reduceMotion: false);
        Assert.IsTrue(entering.ShouldDraw);
        Assert.AreEqual(0, entering.Opacity, 0.000001);
        Assert.IsTrue(entering.OffsetXFactor < 0);
        Assert.IsTrue(controller.AnyAnimating);

        var entered = controller.Update(
            EstateRaceHudWidgetKind.Leaderboard,
            visible: true,
            nowSeconds: 0.18,
            reduceMotion: false);
        Assert.AreEqual(1, entered.Opacity, 0.000001);
        Assert.AreEqual(0, entered.OffsetXFactor, 0.000001);

        var exiting = controller.Update(
            EstateRaceHudWidgetKind.Leaderboard,
            visible: false,
            nowSeconds: 0.27,
            reduceMotion: false);
        Assert.IsTrue(exiting.ShouldDraw, "退场期间必须保留最后一帧内容，不能突然消失。 ");
        Assert.IsTrue(exiting.Opacity is > 0 and < 1);
        var hidden = controller.Update(
            EstateRaceHudWidgetKind.Leaderboard,
            visible: false,
            nowSeconds: 0.45,
            reduceMotion: false);
        Assert.IsFalse(hidden.ShouldDraw);

        var reduced = new EstateRaceHudAnimationController();
        var reducedEntry = reduced.Update(
            EstateRaceHudWidgetKind.PitLimiter,
            visible: true,
            nowSeconds: 0,
            reduceMotion: true);
        Assert.AreEqual(1, reducedEntry.Scale, 0.000001);
        Assert.AreEqual(0, reducedEntry.OffsetXFactor, 0.000001);
        Assert.AreEqual(0, reducedEntry.OffsetYFactor, 0.000001);
        Assert.AreEqual(1, reduced.Update(
            EstateRaceHudWidgetKind.PitLimiter,
            visible: true,
            nowSeconds: 0.10,
            reduceMotion: true).Opacity, 0.000001);
    }

    [TestMethod]
    public void OverlayEditorSnapsToWindowEdgesAndCenters()
    {
        var centered = OverlayLayoutSnapping.Snap(
            left: 395,
            top: 246,
            width: 200,
            height: 100,
            workspaceWidth: 1_000,
            workspaceHeight: 600);
        Assert.AreEqual(400, centered.Left, 0.001);
        Assert.AreEqual(250, centered.Top, 0.001);
        Assert.AreEqual(500, centered.VerticalGuide);
        Assert.AreEqual(300, centered.HorizontalGuide);
        StringAssert.Contains(centered.AlignmentText, "垂直居中");
        StringAssert.Contains(centered.AlignmentText, "水平居中");

        var edgeAligned = OverlayLayoutSnapping.Snap(
            left: 8,
            top: 11,
            width: 200,
            height: 100,
            workspaceWidth: 1_000,
            workspaceHeight: 600);
        Assert.AreEqual(12, edgeAligned.Left, 0.001);
        Assert.AreEqual(12, edgeAligned.Top, 0.001);
        StringAssert.Contains(edgeAligned.AlignmentText, "左对齐");
        StringAssert.Contains(edgeAligned.AlignmentText, "顶部对齐");
    }

    [TestMethod]
    public void LapHudSnapAssistanceAllowsFineSpacingBeforeExactAlignment()
    {
        var assistedOnly = OverlayHudSnapping.AssistLapNearDashboard(
            lapLeft: 320,
            lapTop: 220,
            lapWidth: 400,
            lapHeight: 200,
            dashboardLeft: 300,
            dashboardTop: 200,
            dashboardWidth: 400,
            dashboardHeight: 200);
        Assert.AreEqual(320, assistedOnly.Left, 1e-9);
        Assert.AreEqual(220, assistedOnly.Top, 1e-9);
        Assert.IsNotNull(assistedOnly.VerticalGuide);
        Assert.IsNotNull(assistedOnly.HorizontalGuide);
        StringAssert.Contains(assistedOnly.AlignmentText, "微调间隔");

        var snapped = OverlayHudSnapping.AssistLapNearDashboard(
            lapLeft: 305,
            lapTop: 196,
            lapWidth: 400,
            lapHeight: 200,
            dashboardLeft: 300,
            dashboardTop: 200,
            dashboardWidth: 400,
            dashboardHeight: 200);
        Assert.AreEqual(300, snapped.Left, 1e-9);
        Assert.AreEqual(200, snapped.Top, 1e-9);
        StringAssert.Contains(snapped.AlignmentText, "已对齐");
    }

    [TestMethod]
    public void ForzaWindowMatchingRejectsOtherGamesAndRecognizesFh6()
    {
        Assert.IsTrue(
            ForzaHorizonWindow.CandidateScore(
                "ForzaHorizon6",
                "Forza Horizon 6") >= 200);
        Assert.IsTrue(
            ForzaHorizonWindow.CandidateScore(
                "ForzaHorizon6_Steam",
                string.Empty) >= 100);
        Assert.AreEqual(
            0,
            ForzaHorizonWindow.CandidateScore(
                "notepad",
                "LazyForza"));
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
