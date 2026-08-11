using System.Diagnostics;
using System.Threading.Channels;
using LazyForza.Analysis;
using LazyForza.Domain;
using LazyForza.Modules.Abstractions;
using LazyForza.Modules.LapAnalysis;
using LazyForza.Storage;

namespace LazyForza.IntegrationTests;

[TestClass]
public sealed class EstateCircuitModuleTests
{
    [TestMethod]
    public async Task EnrollmentFitsGateRecordsReferenceAndRequiresValidationLap()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lazyforza-estate-enrollment-{Guid.NewGuid():N}.db");
        try
        {
            using var store = new LazyForzaStore(path);
            var feed = new TestFeed();
            var module = new EstateCircuitModule(store, TelemetrySourceKind.Live);
            await module.InitializeAsync(new TestContext(feed, store), CancellationToken.None);
            await module.StartAsync(CancellationToken.None);
            try
            {
                module.BeginEnrollment(new EstateEnrollmentRequest("实机地产测试", "作者", "share", "1", 7));
                PublishLineTrace(feed, 100, reverse: false, zOffset: 0.03);
                module.StartLineTrace();
                await Task.Delay(100);
                Assert.AreEqual(0, module.State.FirstTraceSamples,
                    "点击开始前积压的 UDP 帧不能被算作新的描摹样本。");
                PublishLineTrace(feed, 10_000, reverse: false, zOffset: 0.03);
                await WaitUntilAsync(() => module.State.FirstTraceSamples >= 25, TimeSpan.FromSeconds(2), () => module.State.ToString());
                module.StopLineTrace();

                module.StartLineTrace();
                PublishLineTrace(feed, 20_000, reverse: true, zOffset: -0.02);
                await WaitUntilAsync(() => module.State.SecondTraceSamples >= 25, TimeSpan.FromSeconds(2), () => module.State.ToString());
                var fit = module.StopLineTrace();
                Assert.IsTrue(fit.IsAccepted, fit.Explanation);

                module.StartDirectionCapture();
                PublishDirectionTrace(feed, 40_000);
                await Task.Delay(100);
                module.StopDirectionCapture();
                Assert.AreEqual(EstateCircuitPhase.AwaitingReferenceLap, module.State.Phase);

                module.StartReferenceLapCapture();
                PublishEnrollmentLaps(feed, 100_000);
                await WaitUntilAsync(() => module.State.Phase == EstateCircuitPhase.Ready, TimeSpan.FromSeconds(5), () => module.State.ToString());

                var saved = store.ListTracks().Single(track => track.TimingKind == TrackTimingKind.EstateGeometry);
                var definition = store.LoadEstateTrackDefinition(saved.Id);
                Assert.IsNotNull(definition);
                Assert.IsTrue(definition.ValidationProjectionRatio >= 0.95);
                Assert.IsTrue(definition.ReferenceLapSeconds > 60);
                Assert.IsTrue(definition.ValidationLapSeconds > 60);
                Assert.IsNull(definition.Pit, "第一阶段只预留维修区模型，不应伪造未录入的维修区。");
                Assert.HasCount(7, store.LoadTrack(saved.Id)!.Value.Sectors);
            }
            finally
            {
                await module.DisposeAsync();
                await feed.DisposeAsync();
            }
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [TestMethod]
    public async Task EstateTimingRequiresExplicitSelectionAndSavesGeometryTimedLap()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lazyforza-estate-module-{Guid.NewGuid():N}.db");
        try
        {
            using var store = new LazyForzaStore(path);
            var rawRoute = Enumerable.Range(0, 241)
                .Select(index =>
                {
                    var angle = index * Math.PI * 2 / 240;
                    return new TrackPoint(100 * Math.Cos(angle), 2, 100 * Math.Sin(angle), 0, 0, 0);
                })
                .ToArray();
            var track = TrackAlgorithms.BuildTemplate("地产测试环道", rawRoute) with
            {
                Source = TelemetryDataPartition.TrackSource(TelemetrySourceKind.Live),
                TimingKind = TrackTimingKind.EstateGeometry,
                Category = "地产环道",
                CaptureLapCount = 2
            };
            var gate = new EstateTimingGate(
                new EstateGatePoint(88, 2, 0),
                new EstateGatePoint(112, 2, 0),
                0,
                1,
                0.05,
                0.04,
                0.1);
            var checkpoints = EstateTrackAlgorithms.CreateCheckpoints(track, 6);
            var definition = new EstateTrackDefinition(
                track.Id, track.Name, "test", "estate-test", "1", gate, checkpoints, null,
                90, 90, 1, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
            store.SaveTrack(track, TrackAlgorithms.CreateSectors(track), definition);

            var feed = new TestFeed();
            var module = new EstateCircuitModule(store, TelemetrySourceKind.Live);
            await module.InitializeAsync(new TestContext(feed, store), CancellationToken.None);
            await module.StartAsync(CancellationToken.None);
            try
            {
                PublishLap(feed, timestampStart: 1_000);
                await Task.Delay(100);
                Assert.AreEqual(EstateCircuitPhase.Idle, module.State.Phase);
                Assert.AreEqual(0, store.CountLaps(track.Id));

                module.StartTiming(track.Id);
                PublishPosition(feed, 0, 100, 2, -0.005, 20);
                PublishPosition(feed, 1, 100, 2, 0.25, 20);
                await Task.Delay(50);
                Assert.AreEqual(
                    EstateCircuitPhase.WaitingForTimingStart,
                    module.State.Phase,
                    "A zero-timestamp menu frame must not arm estate timing or seed a phantom crossing.");
                PublishLap(feed, timestampStart: 200_000);
                await WaitUntilAsync(
                    () => module.State.CompletedLaps == 1,
                    TimeSpan.FromSeconds(5),
                    () => module.State.ToString());

                Assert.AreEqual(EstateCircuitPhase.TimingLap, module.State.Phase);
                Assert.AreEqual(1, store.CountLaps(track.Id));
                var lap = store.LoadLapSummaries(track.Id, 5).Single();
                Assert.IsTrue(lap.IsValid, lap.InvalidReason);
                Assert.IsTrue(lap.TotalSeconds > 60);

                await WaitUntilAsync(
                    () => ((IHudContribution)module).Snapshot is LapHudState currentHud &&
                          currentHud.Sectors.Any(sector => sector.HistoricalBestSeconds is > 0),
                    TimeSpan.FromSeconds(2),
                    () => module.State.ToString());
                var hud = (LapHudState?)((IHudContribution)module).Snapshot;
                Assert.IsNotNull(hud);
                Assert.AreEqual(track.Name, hud.TrackName);
                Assert.AreNotEqual(Guid.Empty, hud.CompetitionSessionId);
                Assert.HasCount(TrackAlgorithms.CreateSectors(track).Count, hud.Sectors);
                Assert.IsTrue(hud.Sectors.Any(sector => sector.CurrentSeconds is > 0));
                Assert.IsTrue(hud.Sectors.Any(sector => sector.HistoricalBestSeconds is > 0));

                var analysis = new LapAnalysisModule(store, TelemetrySourceKind.Live);
                Assert.IsNull(analysis.IncompatibleTrackName, "地产环道不能触发旧版官方计时赛道的重新学习警告。");
                analysis.SelectTrack(track.Id);
                Assert.AreEqual(track.Id, analysis.CurrentTrack?.Id);
                Assert.HasCount(1, analysis.VisibleLaps);
                Assert.AreEqual(lap.Id, analysis.VisibleLaps[0].Id);
                var analysisHud = (LapHudState?)analysis.Snapshot;
                Assert.IsNotNull(analysisHud);
                Assert.AreEqual("正在查看地产环道圈速。", analysisHud.Status);

                PublishPosition(
                    feed,
                    295_000,
                    99,
                    2,
                    10,
                    20,
                    DateTimeOffset.UnixEpoch.AddMilliseconds(300_000),
                    isRaceOn: false);
                await WaitUntilAsync(
                    () => module.State.Phase == EstateCircuitPhase.WaitingForTimingStart,
                    TimeSpan.FromSeconds(2),
                    () => module.State.ToString());
                Assert.AreEqual(1, store.CountLaps(track.Id), "暂停或遥测中断后的半圈不能保存。");
                StringAssert.Contains(module.State.Status, "本圈已取消");

                PublishLap(feed, timestampStart: 400_000);
                await WaitUntilAsync(
                    () => module.State.CompletedLaps == 2,
                    TimeSpan.FromSeconds(5),
                    () => module.State.ToString());
                Assert.AreEqual(2, store.CountLaps(track.Id), "暂停后应能从下一次正向过线重新开始完整计时。");

                PublishPosition(feed, 480_000, 96, 2, 18, 20);
                await Task.Delay(100);
                Assert.AreEqual(EstateCircuitPhase.TimingLap, module.State.Phase,
                    "仅有时间戳回退不能被当作暂停或回转遥测。");
                PublishPosition(feed, 481_000, 96, 2, 18, 20, isRaceOn: false);
                await WaitUntilAsync(
                    () => module.State.Phase == EstateCircuitPhase.WaitingForTimingStart,
                    TimeSpan.FromSeconds(2),
                    () => module.State.ToString());
                Assert.AreEqual(2, store.CountLaps(track.Id),
                    "手动计时和排位赛模式下，明确的暂停或回转遥测必须取消当前圈。");
                Assert.IsFalse(module.LastCompletedLap?.IsValid ?? true,
                    "取消的圈必须形成无效事件，确保排位最后飞驰圈不会一直挂起。");

                PublishLap(feed, timestampStart: 500_000, injectTrackDeviation: true);
                await WaitUntilAsync(
                    () => module.State.CompletedLaps == 3,
                    TimeSpan.FromSeconds(5),
                    () => module.State.ToString());
                var strictDeviation = store.LoadLapSummaries(track.Id, 1).Single();
                Assert.IsFalse(strictDeviation.IsValid,
                    "手动计时和排位赛模式下，持续偏离参考路线必须使该圈无效。");
                Assert.AreEqual("estate-track-deviation", strictDeviation.InvalidReason);

                PublishCircularRange(feed, 591_000, 0, 30);
                PublishShortcutChord(feed, 599_000, 30, 150);
                await WaitUntilAsync(
                    () => module.State.Phase == EstateCircuitPhase.WaitingForTimingStart,
                    TimeSpan.FromSeconds(2),
                    () => module.State.ToString());
                Assert.IsFalse(module.LastCompletedLap?.IsValid ?? true,
                    "跨过大半圆弧的捷径必须立即取消手动计时或排位赛圈。");
                StringAssert.Contains(module.LastCompletedLap?.InvalidReason ?? string.Empty, "跨越赛道大段路线");

                PublishPosition(feed, 606_000, 100, 2, -1, 20);
                PublishPosition(feed, 606_100, 100, 2, 1, 20);
                await WaitUntilAsync(
                    () => module.State.Phase == EstateCircuitPhase.TimingLap,
                    TimeSpan.FromSeconds(2),
                    () => module.State.ToString());

                module.SetEstateRaceInterventionInvalidation(false);
                PublishPosition(feed, 0, 100, 2, 0, 0, isRaceOn: false);
                await Task.Delay(50);
                Assert.AreEqual(
                    EstateCircuitPhase.TimingLap,
                    module.State.Phase,
                    "正赛暂停帧应保留当前圈，而不是切回等待下一次过线。");

                PublishLap(
                    feed,
                    timestampStart: 700_000,
                    injectSmallReorderAtFinish: true,
                    injectTrackDeviation: true);
                await WaitUntilAsync(
                    () => module.State.CompletedLaps >= 5,
                    TimeSpan.FromSeconds(5),
                    () => module.State.ToString());
                Assert.AreEqual(5, store.CountLaps(track.Id),
                    "暂停前已经开始的正赛圈应在首次冲线完成，随后完整一圈也必须正常计入。");
                Assert.IsTrue(store.LoadLapSummaries(track.Id, 1).Single().IsValid,
                    "正赛中的偏离路线只交由服务端判罚，不能使该圈失效或阻止计圈。");

                PublishPosition(feed, 790_900, 99, 2, 3, 20);
                await Task.Delay(50);
                Assert.AreEqual(
                    EstateCircuitPhase.TimingLap,
                    module.State.Phase,
                    "少量 UDP 乱序不应被误判成游戏倒带。");
                PublishPosition(feed, 780_000, 96, 2, 18, 20);
                await Task.Delay(100);
                Assert.AreEqual(EstateCircuitPhase.TimingLap, module.State.Phase,
                    "正赛中的时间回退或回转只能重新同步位置，不能取消当前圈。");
                PublishLap(feed, timestampStart: 900_000);
                await WaitUntilAsync(
                    () => module.State.CompletedLaps >= 7,
                    TimeSpan.FromSeconds(5),
                    () => module.State.ToString());
                Assert.AreEqual(7, store.CountLaps(track.Id),
                    "正赛回转后保留的当前圈与随后完整行驶的一圈都应正常计入。");
            }
            finally
            {
                await module.DisposeAsync();
                await feed.DisposeAsync();
            }
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [TestMethod]
    public async Task LegalPitLaneFinishCrossingCompletesOneValidLapWithoutMainGateHit()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lazyforza-estate-pit-lap-{Guid.NewGuid():N}.db");
        try
        {
            using var store = new LazyForzaStore(path);
            var route = Enumerable.Range(0, 361)
                .Select(index =>
                {
                    var angle = index * Math.PI * 2 / 360;
                    return new TrackPoint(100 * Math.Cos(angle), 2, 100 * Math.Sin(angle), 0, 0, 0);
                })
                .ToArray();
            var track = TrackAlgorithms.BuildTemplate("维修区冲线测试", route) with
            {
                Source = TelemetryDataPartition.TrackSource(TelemetrySourceKind.Live),
                TimingKind = TrackTimingKind.EstateGeometry,
                Category = "地产环道",
                CaptureLapCount = 2
            };
            var mainGate = new EstateTimingGate(
                new EstateGatePoint(88, 2, 0), new EstateGatePoint(112, 2, 0),
                0, 1, 0, 0, 0);
            var pitGate = new EstateTimingGate(
                new EstateGatePoint(114, 2, 0), new EstateGatePoint(122, 2, 0),
                0, 1, 0, 0, 0);
            var pit = new EstatePitDefinition(
                new EstateTimingGate(new EstateGatePoint(114, 2, -20), new EstateGatePoint(122, 2, -20), 0, 1, 0, 0, 0),
                new EstateTimingGate(new EstateGatePoint(114, 2, 20), new EstateGatePoint(122, 2, 20), 0, 1, 0, 0, 0),
                [
                    new EstateGatePoint(118, 2, -22),
                    new EstateGatePoint(118, 2, -10),
                    new EstateGatePoint(118, 2, 0),
                    new EstateGatePoint(118, 2, 10),
                    new EstateGatePoint(118, 2, 22)
                ],
                new EstateGatePoint(118, 2, 8),
                3,
                80,
                3,
                4,
                null,
                pitGate);
            var definition = new EstateTrackDefinition(
                track.Id, track.Name, "test", "pit-lap", "1", mainGate,
                EstateTrackAlgorithms.CreateCheckpoints(track, 6), pit,
                90, 90, 1, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
            store.SaveTrack(track, TrackAlgorithms.CreateSectors(track), definition);

            var feed = new TestFeed();
            var module = new EstateCircuitModule(store, TelemetrySourceKind.Live);
            await module.InitializeAsync(new TestContext(feed, store), CancellationToken.None);
            await module.StartAsync(CancellationToken.None);
            try
            {
                module.StartTiming(track.Id);
                PublishPosition(feed, 1_000, 100, 2, -2, 20);
                PublishPosition(feed, 1_100, 100, 2, 2, 20);
                for (var index = 2; index <= 350; index += 2)
                {
                    var angle = index * Math.PI * 2 / 360;
                    PublishPosition(feed, (uint)(1_100 + index * 250),
                        100 * Math.Cos(angle), 2, 100 * Math.Sin(angle), 20);
                }
                PublishPosition(feed, 89_000, 118, 2, -22, 15);
                PublishPosition(feed, 89_250, 118, 2, -18, 15);
                PublishPosition(feed, 90_000, 118, 2, -1, 15);
                PublishPosition(feed, 90_100, 118, 2, 1, 15);

                await WaitUntilAsync(
                    () => module.State.CompletedLaps == 1,
                    TimeSpan.FromSeconds(5),
                    () => module.State.ToString());

                Assert.AreEqual(1, store.CountLaps(track.Id));
                var lap = store.LoadLapSummaries(track.Id, 2).Single();
                Assert.IsTrue(lap.IsValid, lap.InvalidReason);
                Assert.AreEqual(1, module.LastCompletedLap?.LapNumber);

                // Crossing the alternate pit finish gate starts the next lap
                // while the car is still inside the lane. The pit-transit state
                // must survive that lap reset until the deterministic exit line.
                PublishPosition(feed, 90_200, 118, 2, 18, 15);
                PublishPosition(feed, 90_300, 118, 2, 22, 15);
                for (var index = 14; index <= 350; index += 2)
                {
                    var angle = index * Math.PI * 2 / 360;
                    PublishPosition(feed, (uint)(90_300 + index * 180),
                        100 * Math.Cos(angle), 2, 100 * Math.Sin(angle), 20);
                }
                PublishPosition(feed, 154_000, 100, 2, -2, 20);
                PublishPosition(feed, 154_100, 100, 2, 2, 20);

                await WaitUntilAsync(
                    () => module.State.CompletedLaps == 2,
                    TimeSpan.FromSeconds(5),
                    () => module.State.ToString());
                Assert.AreEqual(2, store.CountLaps(track.Id));
                Assert.IsTrue(store.LoadLapSummaries(track.Id, 1).Single().IsValid,
                    "维修区内冲线后，出站并完成的下一圈不应继承虚假的赛道边界事件。");
            }
            finally
            {
                await module.DisposeAsync();
                await feed.DisposeAsync();
            }
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [TestMethod]
    public async Task EstateHudShowsFastestLapDeltaOnlyAfterSectorBoundariesAndUsesSharedHoldSetting()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lazyforza-estate-hud-{Guid.NewGuid():N}.db");
        try
        {
            using var store = new LazyForzaStore(path);
            var rawRoute = Enumerable.Range(0, 241)
                .Select(index =>
                {
                    var angle = index * Math.PI * 2 / 240;
                    return new TrackPoint(100 * Math.Cos(angle), 2, 100 * Math.Sin(angle), 0, 0, 0);
                })
                .ToArray();
            var track = TrackAlgorithms.BuildTemplate("地产 HUD 测试环道", rawRoute) with
            {
                Source = TelemetryDataPartition.TrackSource(TelemetrySourceKind.Live),
                TimingKind = TrackTimingKind.EstateGeometry,
                Category = "地产环道",
                CaptureLapCount = 2
            };
            var definition = new EstateTrackDefinition(
                track.Id,
                track.Name,
                "test",
                "estate-hud-test",
                "1",
                new EstateTimingGate(
                    new EstateGatePoint(88, 2, 0),
                    new EstateGatePoint(112, 2, 0),
                    0,
                    1,
                    0.05,
                    0.04,
                    0.1),
                EstateTrackAlgorithms.CreateCheckpoints(track, 6),
                null,
                90,
                90,
                1,
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch);
            store.SaveTrack(track, TrackAlgorithms.CreateSectors(track), definition);

            var feed = new TestFeed();
            var module = new EstateCircuitModule(
                store,
                TelemetrySourceKind.Live,
                () => new OverlayLayout(LapCompletedHoldSeconds: 1));
            await module.InitializeAsync(new TestContext(feed, store), CancellationToken.None);
            await module.StartAsync(CancellationToken.None);
            try
            {
                const uint lapTimestampStart = 1_000;
                module.StartTiming(track.Id);
                PublishLap(feed, lapTimestampStart);
                await WaitUntilAsync(
                    () => module.State.CompletedLaps == 1 &&
                          ((LapHudState?)((IHudContribution)module).Snapshot) is
                          { CompletedLaps: 1, ShowingPreviousLap: true },
                    TimeSpan.FromSeconds(5),
                    () => module.State.ToString());

                var completed = (LapHudState?)((IHudContribution)module).Snapshot;
                Assert.IsNotNull(completed);
                Assert.IsTrue(completed.ShowingPreviousLap,
                    "地产 HUD 应与普通圈速 HUD 一样，按布局设置短暂保留完成圈分段。 ");
                Assert.IsNull(completed.CumulativeHistoricalDeltaSeconds,
                    "首个有效圈尚无此前最快圈，不应伪造 Delta。 ");

                PublishCircularRange(feed, lapTimestampStart, 363, 366);
                await WaitUntilAsync(
                    () => ((LapHudState?)((IHudContribution)module).Snapshot)?.ShowingPreviousLap == false,
                    TimeSpan.FromSeconds(2),
                    () => module.State.ToString());
                var beforeSector = (LapHudState?)((IHudContribution)module).Snapshot;
                Assert.IsNotNull(beforeSector);
                Assert.AreEqual(0, beforeSector.CurrentSector);
                Assert.IsNull(beforeSector.CumulativeHistoricalDeltaSeconds,
                    "未通过新分段时，地产 HUD 的最快圈 Delta 必须保持隐藏。 ");

                PublishCircularRange(feed, lapTimestampStart, 367, 455);
                await WaitUntilAsync(
                    () => ((LapHudState?)((IHudContribution)module).Snapshot) is
                        { CurrentSector: > 0, CumulativeHistoricalDeltaSeconds: not null },
                    TimeSpan.FromSeconds(3),
                    () => module.State.ToString());
                var firstSector = (LapHudState?)((IHudContribution)module).Snapshot;
                Assert.IsNotNull(firstSector);
                Assert.IsTrue(Math.Abs(firstSector.CumulativeHistoricalDeltaSeconds!.Value) < 0.5,
                    $"相同路线和节奏下，分段累计 Delta 应接近 0，实际为 {firstSector.CumulativeHistoricalDeltaSeconds:0.000}。 ");

                PublishCircularRange(feed, lapTimestampStart, 456, 457);
                await WaitUntilAsync(
                    () => ((LapHudState?)((IHudContribution)module).Snapshot)?.CurrentLapSeconds >
                          firstSector.CurrentLapSeconds + 0.4,
                    TimeSpan.FromSeconds(2),
                    () => module.State.ToString());
                Assert.IsNotNull(((LapHudState?)((IHudContribution)module).Snapshot)?.CumulativeHistoricalDeltaSeconds,
                    "分段 Delta 在触发后的 2 秒内必须保持可见。 ");

                PublishCircularRange(feed, lapTimestampStart, 458, 463);
                await WaitUntilAsync(
                    () => ((LapHudState?)((IHudContribution)module).Snapshot)?.CumulativeHistoricalDeltaSeconds is null,
                    TimeSpan.FromSeconds(2),
                    () => module.State.ToString());
                Assert.AreEqual(1, ((LapHudState?)((IHudContribution)module).Snapshot)?.CurrentSector,
                    "Delta 消失不应改变当前分段。 ");

                PublishCircularRange(feed, lapTimestampStart, 464, 545);
                await WaitUntilAsync(
                    () => ((LapHudState?)((IHudContribution)module).Snapshot) is
                        { CurrentSector: >= 2, CumulativeHistoricalDeltaSeconds: not null },
                    TimeSpan.FromSeconds(3),
                    () => module.State.ToString());
            }
            finally
            {
                await module.DisposeAsync();
                await feed.DisposeAsync();
            }
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [TestMethod]
    public async Task PitEnrollmentBuildsDirectedEntryLaneServicePolygonAndExit()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lazyforza-estate-pit-{Guid.NewGuid():N}.db");
        try
        {
            using var store = new LazyForzaStore(path);
            var route = Enumerable.Range(0, 121)
                .Select(index =>
                {
                    var angle = index * Math.PI * 2 / 120;
                    return new TrackPoint(80 * Math.Cos(angle), 2, 80 * Math.Sin(angle), 0, 0, 0);
                })
                .ToArray();
            var track = TrackAlgorithms.BuildTemplate("维修区录入测试", route) with
            {
                Source = TelemetryDataPartition.TrackSource(TelemetrySourceKind.Live),
                TimingKind = TrackTimingKind.EstateGeometry,
                Category = "地产环道",
                CaptureLapCount = 2
            };
            var definition = new EstateTrackDefinition(
                track.Id,
                track.Name,
                "test",
                "pit-test",
                "1",
                new EstateTimingGate(
                    new EstateGatePoint(-8, 2, 0),
                    new EstateGatePoint(8, 2, 0),
                    0,
                    1,
                    0,
                    0,
                    0),
                EstateTrackAlgorithms.CreateCheckpoints(track, 4),
                null,
                60,
                60,
                1,
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch);
            store.SaveTrack(track, TrackAlgorithms.CreateSectors(track), definition);

            var feed = new TestFeed();
            var module = new EstateCircuitModule(store, TelemetrySourceKind.Live);
            await module.InitializeAsync(new TestContext(feed, store), CancellationToken.None);
            await module.StartAsync(CancellationToken.None);
            try
            {
                module.BeginPitEnrollment(new EstatePitEnrollmentRequest(track.Id, 4, 80, 3));
                module.StartPitLaneCapture();
                static double PitLaneX(double z) => z < -10 ? 14 + 6 * ((z + 20) / 10) : 20;
                for (var index = 0; index <= 80; index++)
                {
                    var z = -20 + index * 0.5;
                    PublishPosition(feed, (uint)(10_000 + index * 100), PitLaneX(z), 2, z, 5);
                }
                await WaitUntilAsync(
                    () => module.PitState.LaneSamples >= 40,
                    TimeSpan.FromSeconds(2),
                    () => module.PitState.ToString());
                module.StopPitLaneCapture();

                var gateTimestamp = 25_000u;
                var gateArrival = DateTimeOffset.UtcNow;
                for (var sample = 0; sample < 8; sample++)
                    PublishPosition(feed, gateTimestamp++, PitLaneX(-18), 2, -18, 0, gateArrival.AddMilliseconds(sample * 15));
                await Task.Delay(40);
                _ = module.CapturePitEntryGate();
                gateArrival = DateTimeOffset.UtcNow;
                for (var sample = 0; sample < 8; sample++)
                    PublishPosition(feed, gateTimestamp++, 20, 2, 18, 0, gateArrival.AddMilliseconds(sample * 15));
                await Task.Delay(40);
                _ = module.CapturePitExitGate();

                var corners = new[]
                {
                    (X: 15d, Z: -2d),
                    (X: 21d, Z: -2d),
                    (X: 21d, Z: 2d),
                    (X: 15d, Z: 2d)
                };
                var timestamp = 30_000u;
                foreach (var cornerPoint in corners)
                {
                    var now = DateTimeOffset.UtcNow;
                    for (var sample = 0; sample < 8; sample++)
                    {
                        PublishPosition(
                            feed,
                            timestamp++,
                            cornerPoint.X,
                            2,
                            cornerPoint.Z,
                            0,
                            now.AddMilliseconds(sample * 15));
                    }
                    await Task.Delay(40);
                    _ = module.CaptureServiceZoneCorner();
                }

                var pit = module.SavePitEnrollment();
                Assert.IsTrue(pit.EntryGate.HasDirection);
                Assert.IsTrue(pit.ExitGate.HasDirection);
                Assert.IsTrue(pit.EntryGate.ForwardX > 0.1, "弯道入口门应使用入口附近的局部通道方向。");
                Assert.IsTrue(pit.EntryGate.ForwardZ > 0.7);
                Assert.IsTrue(module.PitState.EntryLineCaptured);
                Assert.IsTrue(module.PitState.ExitLineCaptured);
                Assert.IsNotNull(pit.StartFinishGate);
                Assert.IsTrue(pit.StartFinishGate.HasDirection);
                Assert.AreEqual(4, pit.LaneHalfWidthMeters, 0.001);
                Assert.IsTrue(pit.CenterLine.Count >= 20);
                Assert.HasCount(4, pit.ServiceZoneBoundary!);
                Assert.AreEqual(EstatePitCapturePhase.Saved, module.PitState.Phase);
                var stored = store.LoadEstateTrackDefinition(track.Id)?.Pit;
                Assert.IsNotNull(stored);
                Assert.HasCount(4, stored.ServiceZoneBoundary!);
                Assert.AreEqual(80, stored.SpeedLimitKph, 0.001);
                Assert.AreEqual(3, stored.MinimumServiceSeconds, 0.001);
            }
            finally
            {
                await module.DisposeAsync();
                await feed.DisposeAsync();
            }
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    private static void PublishLap(
        TestFeed feed,
        uint timestampStart,
        bool injectSmallReorderAtFinish = false,
        bool injectTrackDeviation = false)
    {
        const int samples = 360;
        for (var index = -2; index <= samples + 2; index++)
        {
            var angle = (index + 0.5) * Math.PI * 2 / samples;
            var timestamp = timestampStart + (uint)((index + 2) * 250);
            var radius = injectTrackDeviation && index is >= 80 and <= 88 ? 125 : 100;
            if (injectSmallReorderAtFinish && index == samples)
            {
                PublishPosition(
                    feed,
                    timestamp - 350,
                    radius * Math.Cos(angle),
                    2,
                    radius * Math.Sin(angle),
                    20);
            }
            var raw = new Fh6RawTelemetry
            {
                IsRaceOn = 1,
                TimestampMS = timestamp,
                EngineMaxRpm = 8_000,
                CurrentEngineRpm = 5_000,
                Position = new Vector3F((float)(radius * Math.Cos(angle)), 2, (float)(radius * Math.Sin(angle))),
                Speed = 20,
                CarOrdinal = 1_234,
                CarClass = 5,
                CarPerformanceIndex = 850,
                DrivetrainType = 2,
                NumCylinders = 6,
                Gear = 4,
                Accel = 180
            };
            feed.Publish(new TelemetryFrame(
                index + 3,
                DateTimeOffset.UnixEpoch.AddMilliseconds(timestamp),
                TelemetrySourceKind.Live,
                raw,
                new NormalizedTelemetry(72, 44.7, 200, 180 / 255d, 0, 0, 0, 0.625, default),
                ReadOnlyMemory<byte>.Empty));
        }
    }

    private static void PublishLineTrace(TestFeed feed, uint timestampStart, bool reverse, double zOffset)
    {
        for (var index = 0; index < 31; index++)
        {
            var amount = index / 30d;
            var x = reverse ? 6 - amount * 12 : -6 + amount * 12;
            PublishPosition(feed, timestampStart + (uint)(index * 100), x, 2, zOffset + Math.Sin(index) * 0.01, 1.5);
        }
    }

    private static void PublishCircularRange(TestFeed feed, uint timestampStart, int fromIndex, int toIndex)
    {
        const int samples = 360;
        for (var index = fromIndex; index <= toIndex; index++)
        {
            var angle = (index + 0.5) * Math.PI * 2 / samples;
            PublishPosition(
                feed,
                timestampStart + (uint)((index + 2) * 250),
                100 * Math.Cos(angle),
                2,
                100 * Math.Sin(angle),
                20);
        }
    }

    private static void PublishShortcutChord(
        TestFeed feed,
        uint timestampStart,
        int fromDegrees,
        int toDegrees)
    {
        var from = fromDegrees * Math.PI / 180;
        var to = toDegrees * Math.PI / 180;
        var startX = 100 * Math.Cos(from);
        var startZ = 100 * Math.Sin(from);
        var endX = 100 * Math.Cos(to);
        var endZ = 100 * Math.Sin(to);
        const int samples = 24;
        for (var index = 1; index <= samples; index++)
        {
            var amount = index / (double)samples;
            PublishPosition(
                feed,
                timestampStart + (uint)(index * 250),
                startX + (endX - startX) * amount,
                2,
                startZ + (endZ - startZ) * amount,
                20);
        }
    }

    private static void PublishDirectionTrace(TestFeed feed, uint timestampStart)
    {
        for (var index = 0; index < 41; index++)
            PublishPosition(feed, timestampStart + (uint)(index * 100), 0, 2, -10 + index * 0.5, 5);
    }

    private static void PublishEnrollmentLaps(TestFeed feed, uint timestampStart)
    {
        const int samplesPerLap = 360;
        for (var index = -2; index <= samplesPerLap * 2 + 2; index++)
        {
            var angle = (index + 0.5) * Math.PI * 2 / samplesPerLap;
            PublishPosition(
                feed,
                timestampStart + (uint)((index + 2) * 250),
                100 - 100 * Math.Cos(angle),
                2,
                100 * Math.Sin(angle),
                20);
        }
    }

    private static void PublishPosition(
        TestFeed feed,
        uint timestamp,
        double x,
        double y,
        double z,
        double speed,
        DateTimeOffset? arrivalTime = null,
        bool isRaceOn = true)
    {
        var raw = new Fh6RawTelemetry
        {
            IsRaceOn = isRaceOn ? 1 : 0,
            TimestampMS = timestamp,
            EngineMaxRpm = 8_000,
            CurrentEngineRpm = 4_000,
            Position = new Vector3F((float)x, (float)y, (float)z),
            Speed = (float)speed,
            CarOrdinal = 1_234,
            CarClass = 5,
            CarPerformanceIndex = 850,
            DrivetrainType = 2,
            NumCylinders = 6,
            Gear = 3,
            Accel = 100
        };
        feed.Publish(new TelemetryFrame(
            timestamp,
            arrivalTime ?? DateTimeOffset.UnixEpoch.AddMilliseconds(timestamp),
            TelemetrySourceKind.Live,
            raw,
            new NormalizedTelemetry(speed * 3.6, speed * 2.23694, 100, 100 / 255d, 0, 0, 0, 0.5, default),
            ReadOnlyMemory<byte>.Empty));
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, Func<string> diagnostic)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition() && stopwatch.Elapsed < timeout)
            await Task.Delay(20);
        Assert.IsTrue(condition(), $"Timed out waiting for estate circuit state transition. {diagnostic()}");
    }

    private static void DeleteDatabase(string path)
    {
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            try { File.Delete(path + suffix); } catch (IOException) { }
        }
    }

    private sealed record TestContext(TestFeed Feed, LazyForzaStore Store) : IModuleContext
    {
        public ITelemetryFeed Telemetry => Feed;
        public IHudHost Hud { get; } = new EmptyHud();
        public IModuleSettingsStore Settings => Store;
        public IAnalysisStore AnalysisStore => Store;
        public Action<string> Log => _ => { };
    }

    private sealed class EmptyHud : IHudHost
    {
        public ValueTask AttachAsync(IHudContribution contribution, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask DetachAsync(string contributionId, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask SetLayoutAsync(OverlayLayout layout, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private sealed class TestFeed : ITelemetryFeed
    {
        private readonly Channel<TelemetryFrame> channel = Channel.CreateUnbounded<TelemetryFrame>();
        public TelemetryFrame? Latest { get; private set; }
        public TelemetryDiagnostics Diagnostics => new("test", 0, TelemetryStreamState.Live, 0, 0, 0, 0, 0, 0, 0, Latest?.ArrivalTime, null);

        public ValueTask<ITelemetrySubscription> SubscribeAsync(string consumerId, CancellationToken cancellationToken) =>
            ValueTask.FromResult<ITelemetrySubscription>(new Subscription(channel.Reader));

        public void Publish(TelemetryFrame frame)
        {
            Latest = frame;
            channel.Writer.TryWrite(frame);
        }

        public ValueTask DisposeAsync()
        {
            channel.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }

        private sealed record Subscription(ChannelReader<TelemetryFrame> Frames) : ITelemetrySubscription
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
