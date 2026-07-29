using System.Diagnostics;
using LazyForza.Analysis;
using LazyForza.Domain;
using LazyForza.Modules.LapAnalysis;
using LazyForza.Storage;

namespace LazyForza.IntegrationTests;

[TestClass]
public sealed class LapAnalysisBehaviorTests
{
    private const int MaximumExpectedCoarseCandidates = 12;

    [TestMethod]
    public void StartsWithNoSelectedTrackAndAllowsManualSelectionToBeCleared()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"lazyforza-empty-track-selection-{Guid.NewGuid():N}.db");
        try
        {
            using var store = new LazyForzaStore(databasePath);
            var savedTrack = SaveCircleTrack(store, "Saved circuit");
            store.SetAppSetting("lap.selectedTrack.simulator", savedTrack.Id.ToString());

            var module = new LapAnalysisModule(store, TelemetrySourceKind.Simulator);

            Assert.IsNull(module.CurrentTrack, "Every program start must begin with an empty analysis-track selection.");
            Assert.AreEqual(string.Empty, store.GetAppSetting("lap.selectedTrack.simulator"));

            module.SelectTrack(savedTrack.Id);
            Assert.AreEqual(savedTrack.Id, module.CurrentTrack?.Id);

            module.ClearTrackSelection();
            Assert.IsNull(module.CurrentTrack);
            Assert.IsEmpty(module.VisibleLaps);
            Assert.AreEqual(string.Empty, store.GetAppSetting("lap.selectedTrack.simulator"));
            Assert.AreEqual("等待比赛", Hud(module).TrackName);
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [TestMethod]
    public void VisibleLapSummariesHydrateAndCacheOnlyRequestedChartDetails()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"lazyforza-lap-details-{Guid.NewGuid():N}.db");
        try
        {
            using var store = new LazyForzaStore(databasePath);
            var track = SaveCircleTrack(store, "Lazy chart details");
            var saved = store.LoadTrack(track.Id)!.Value;
            var lapId = Guid.NewGuid();
            var vehicle = new VehicleProfileFingerprint(1, 4, 800, 2, 6, 8_000, "g", "c");
            var samples = saved.Track.Points.Take(20)
                .Select((point, index) => new LapSample(
                    point.S,
                    index * 0.1,
                    30,
                    5_000,
                    4,
                    1,
                    0,
                    0,
                    point.X,
                    point.Y,
                    point.Z))
                .ToArray();
            store.SaveLap(new LapRecord(
                lapId,
                track.Id,
                track.Direction,
                TrackAlgorithms.SectorSchemaVersion,
                Guid.NewGuid(),
                vehicle,
                DateTimeOffset.UtcNow,
                30,
                true,
                null,
                saved.Sectors.Select(sector => new LapSegment(
                    sector.Index,
                    30d / saved.Sectors.Count,
                    true)).ToArray(),
                samples));

            var module = new LapAnalysisModule(store, TelemetrySourceKind.Simulator);
            module.SelectTrack(track.Id);
            Assert.HasCount(1, module.VisibleLaps);
            Assert.AreEqual(lapId, module.VisibleLaps[0].Id);

            var details = module.LoadLapDetails([lapId]);
            Assert.HasCount(1, details);
            Assert.HasCount(samples.Length, details[0].Samples);

            store.DeleteLap(lapId);
            var cached = module.LoadLapDetails([lapId]);
            Assert.HasCount(1, cached);
            Assert.HasCount(samples.Length, cached[0].Samples);
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [TestMethod]
    public void TrackLearningStartsAtFirstTimerResetInsteadOfGridPosition()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"lazyforza-start-line-{Guid.NewGuid():N}.db");
        try
        {
            using var store = new LazyForzaStore(databasePath);
            var module = new LapAnalysisModule(store, TelemetrySourceKind.Simulator);
            var feed = new CircleFrameFeed(module);
            var gridPosition = new Vector3F(180, 0, 0);

            feed.Send(1, 20, gridPosition, 2.1f);
            Assert.AreEqual(TrackLearningPhase.WaitingForStartLine, Hud(module).Phase);
            feed.Send(1, 0, Position(0), 2.2f);
            feed.Send(1, 1, raceTimeOverride: 2.3f);
            Assert.AreEqual(TrackLearningPhase.CapturingReferenceLap, Hud(module).Phase);
            feed.Drive(1, 2, 239);
            feed.Send(2, 0);

            Assert.IsNotNull(module.CurrentTrack);
            var learnedStart = module.CurrentTrack.Points[0];
            Assert.IsTrue(Math.Abs(learnedStart.X - Position(0).X) < 1);
            Assert.IsTrue(Math.Abs(learnedStart.X - gridPosition.X) > 10);
            Assert.AreEqual(TrackAlgorithms.SectorSchemaVersion, module.CurrentTrack is null
                ? -1
                : store.LoadLatestTrack("simulator")!.Value.Sectors[0].SectorSchemaVersion);
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [TestMethod]
    public void CompletedLapColorsAreHeldForTwoSecondsAndIgnoreTransientProjectionLoss()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"lazyforza-lap-hold-{Guid.NewGuid():N}.db");
        try
        {
            using var store = new LazyForzaStore(databasePath);
            SaveCircleTrack(store);
            var module = new LapAnalysisModule(
                store,
                TelemetrySourceKind.Simulator,
                () => new OverlayLayout(LapCompletedHoldSeconds: 2));
            var feed = new CircleFrameFeed(module);

            feed.Send(0, 20, raceTimeOverride: 2.1f);
            Assert.AreEqual(TrackLearningPhase.MatchingTrack, Hud(module).Phase);
            feed.Send(0, 0, raceTimeOverride: 2.2f);
            feed.Drive(0, 1, 239);
            feed.Send(1, 0);

            var completed = Hud(module);
            Assert.IsTrue(completed.ShowingPreviousLap);
            Assert.IsTrue(completed.Sectors.All(sector => sector.State != SectorColorState.Gray));
            Assert.HasCount(1, module.VisibleLaps);
            Assert.IsTrue(module.VisibleLaps[0].IsValid, module.VisibleLaps[0].InvalidReason);

            feed.Drive(1, 1, 15);
            Assert.IsTrue(Hud(module).ShowingPreviousLap);
            feed.Drive(1, 16, 70);

            var live = Hud(module);
            Assert.IsFalse(live.ShowingPreviousLap);
            Assert.AreNotEqual(SectorColorState.Gray, live.Sectors[0].State);
            var completedColor = live.Sectors[0].State;

            feed.Send(1, 71, new Vector3F(10_000, 10_000, 10_000));
            var projectionLost = Hud(module);
            Assert.AreEqual(completedColor, projectionLost.Sectors[0].State);
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [TestMethod]
    public void DashboardDeltaUsesCumulativeTimeAgainstFastestLapAndExpiresAfterTwoSeconds()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"lazyforza-cumulative-delta-{Guid.NewGuid():N}.db");
        try
        {
            using var store = new LazyForzaStore(databasePath);
            SaveCircleTrack(store);
            var module = new LapAnalysisModule(store, TelemetrySourceKind.Simulator);
            var feed = new CircleFrameFeed(module);

            feed.Send(0, 20, raceTimeOverride: 2.1f);
            feed.Send(0, 0, raceTimeOverride: 2.2f);
            feed.Drive(0, 1, 239);
            feed.Send(1, 0);
            Assert.HasCount(1, module.VisibleLaps);

            var frame = 1;
            while (frame < 239 && Hud(module).ShowingPreviousLap) feed.Send(1, frame++);
            while (frame < 239 && Hud(module).CurrentSector == 0)
            {
                feed.Send(1, frame);
                frame++;
            }
            var current = Hud(module);
            Assert.IsTrue(current.CurrentSector > 0, "测试帧必须已经开过至少一个分段。 ");
            Assert.IsNotNull(current.CumulativeHistoricalDeltaSeconds,
                "有同等级历史完整圈后，通过分段应发布累计 Delta。 ");
            Assert.IsTrue(Math.Abs(current.CumulativeHistoricalDeltaSeconds.Value) < 0.5,
                $"两圈使用相同确定性轨迹与计时，累计 Delta 应接近 0，实际为 {current.CumulativeHistoricalDeltaSeconds:0.000}。 ");

            for (var offset = 0; offset < 19; offset++) feed.Send(1, frame++);
            Assert.IsNotNull(Hud(module).CumulativeHistoricalDeltaSeconds,
                "累计 Delta 在触发后的 2 秒内必须保持可见。");
            feed.Send(1, frame++);
            Assert.IsNull(Hud(module).CumulativeHistoricalDeltaSeconds,
                "累计 Delta 显示满 2 秒后必须消失。");

            feed.Drive(1, frame, 239);
            feed.Send(2, 0);
            Assert.IsNotNull(Hud(module).CumulativeHistoricalDeltaSeconds,
                "完成圈与历史最快圈的 Delta 必须立即显示。");
            feed.Drive(2, 1, 19);
            Assert.IsNotNull(Hud(module).CumulativeHistoricalDeltaSeconds,
                "完成圈 Delta 在触发后的 2 秒内必须保持可见。");
            feed.Send(2, 20);
            Assert.IsNull(Hud(module).CumulativeHistoricalDeltaSeconds,
                "完成圈 Delta 显示满 2 秒后必须消失。");
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [TestMethod]
    public void TwentyMinuteLapKeepsHudProcessingInsideRealtimeBudget()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"lazyforza-long-lap-{Guid.NewGuid():N}.db");
        try
        {
            using var store = new LazyForzaStore(databasePath);
            SaveCircleTrack(store, "Long circuit");
            var module = new LapAnalysisModule(store, TelemetrySourceKind.Simulator);
            var feed = new CircleFrameFeed(module);

            feed.Send(0, 20, raceTimeOverride: 2.1f);
            feed.Send(0, 0, raceTimeOverride: 2.2f);
            feed.Drive(0, 1, 239);
            feed.Send(1, 0);

            const int totalFrames = 40_000;
            var stopwatch = Stopwatch.StartNew();
            for (var frame = 1; frame <= totalFrames; frame++)
            {
                var routeFrame = (int)Math.Floor(frame * 239d / totalFrames);
                feed.Send(1, frame, Position(routeFrame), currentLapOverride: frame * 1_200f / totalFrames);
            }
            stopwatch.Stop();

            var hud = Hud(module);
            Assert.AreEqual("Long circuit", hud.TrackName);
            Assert.AreEqual(1_200, hud.CurrentLapSeconds, 0.001);
            Assert.IsFalse(hud.MatchRejectionEligible);
            Assert.IsTrue(
                stopwatch.Elapsed < TimeSpan.FromSeconds(2),
                $"20 分钟长圈的 40,000 帧处理耗时 {stopwatch.Elapsed.TotalSeconds:0.000}s，" +
                "圈速处理不能随累计采样数退化到无法实时更新 HUD。");
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [TestMethod]
    public void UnmatchedSavedTrackPublishesRejectionEvidenceWithoutEndingCompetition()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"lazyforza-no-match-{Guid.NewGuid():N}.db");
        try
        {
            using var store = new LazyForzaStore(databasePath);
            SaveCircleTrack(store);
            var module = new LapAnalysisModule(store, TelemetrySourceKind.Simulator);
            var feed = new CircleFrameFeed(module);

            feed.Send(0, 20, new Vector3F(10_000, 10_000, 10_000), raceTimeOverride: 2.1f);
            var unmatched = Hud(module);
            Assert.IsTrue(unmatched.IsCompetitionActive);
            Assert.IsTrue(unmatched.MatchRejectionEligible);
            Assert.AreEqual("未识别赛事", unmatched.TrackName,
                "No-match evidence must show the existing rejection prompt before the overlay fade begins.");
            Assert.AreEqual("没有找到匹配赛道，本场不会记录圈速。", unmatched.Status);
            var session = unmatched.CompetitionSessionId;

            feed.Send(0, 21, Position(20), raceTimeOverride: 2.2f);
            var plausible = Hud(module);
            Assert.IsTrue(plausible.IsCompetitionActive);
            Assert.AreEqual(session, plausible.CompetitionSessionId);
            Assert.IsFalse(plausible.MatchRejectionEligible);
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [TestMethod]
    public void AutomaticallySwitchesFromPersistedWrongTrackToActualRaceAndSavesTheLap()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"lazyforza-auto-match-{Guid.NewGuid():N}.db");
        try
        {
            using var store = new LazyForzaStore(databasePath);
            var actualTrack = SaveCircleTrack(store, "Electric Street Circuit");
            var wrongTrack = SaveCircleTrack(store, "Goliath", new Vector3F(5_000, 0, 5_000));
            store.SetAppSetting("lap.selectedTrack.simulator", wrongTrack.Id.ToString());

            var module = new LapAnalysisModule(store, TelemetrySourceKind.Simulator);
            Assert.IsNull(module.CurrentTrack,
                "A stale persisted selection must not preselect a track when the program opens.");
            Assert.AreEqual(string.Empty, store.GetAppSetting("lap.selectedTrack.simulator"));
            var feed = new CircleFrameFeed(module);

            feed.Send(0, 20, raceTimeOverride: 2.1f);
            Assert.AreEqual("正在识别赛事", Hud(module).TrackName);
            feed.Send(0, 0, raceTimeOverride: 2.2f);
            feed.Drive(0, 1, 80);

            Assert.AreEqual(actualTrack.Id, module.CurrentTrack?.Id);
            Assert.AreEqual("Electric Street Circuit", Hud(module).TrackName);
            Assert.AreEqual(actualTrack.Id.ToString(), store.GetAppSetting("lap.selectedTrack.simulator"));

            feed.Drive(0, 81, 239);
            feed.Send(1, 0);

            Assert.HasCount(1, module.VisibleLaps);
            Assert.AreEqual(actualTrack.Id, module.VisibleLaps[0].TrackId);
            Assert.IsTrue(module.VisibleLaps[0].IsValid, module.VisibleLaps[0].InvalidReason);
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [TestMethod]
    public void IdenticalCandidateRoutesRemainUnmatchedInsteadOfChoosingArbitrarily()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"lazyforza-auto-match-ambiguous-{Guid.NewGuid():N}.db");
        try
        {
            using var store = new LazyForzaStore(databasePath);
            var first = SaveCircleTrack(store, "Shared route A");
            var second = SaveCircleTrack(store, "Shared route B");
            store.SetAppSetting("lap.selectedTrack.simulator", second.Id.ToString());
            var module = new LapAnalysisModule(store, TelemetrySourceKind.Simulator);
            var feed = new CircleFrameFeed(module);

            feed.Send(0, 20, raceTimeOverride: 2.1f);
            feed.Send(0, 0, raceTimeOverride: 2.2f);
            feed.Drive(0, 1, 239);
            feed.Send(1, 0);

            Console.WriteLine(DescribeMatchDiagnostics(module));
            Assert.HasCount(0, module.VisibleLaps);
            Assert.IsTrue(Hud(module).MatchRejectionEligible);
            Assert.AreEqual("未识别赛事", Hud(module).TrackName);
            Assert.AreEqual(string.Empty, store.GetAppSetting("lap.selectedTrack.simulator"),
                "An ambiguous match must not replace the empty selection with an arbitrary candidate.");
            Assert.AreNotEqual(first.Id, module.VisibleLaps.FirstOrDefault()?.TrackId);
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [TestMethod]
    public void UserCorrectionAppliesOnlyToCurrentCompetitionAndDefersRecording()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"lazyforza-track-correction-{Guid.NewGuid():N}.db");
        try
        {
            using var store = new LazyForzaStore(databasePath);
            var selected = SaveCircleTrack(store, "Actual official event");
            SaveCircleTrack(store, "Overlapping official event");
            var module = new LapAnalysisModule(store, TelemetrySourceKind.Simulator);
            var feed = new CircleFrameFeed(module);
            feed.Send(0, 20, raceTimeOverride: 2.1f);
            feed.Send(0, 0, raceTimeOverride: 2.2f);
            feed.Drive(0, 1, 45);
            var sessionId = module.CurrentSessionId;

            var result = module.CorrectTrackMatch(selected.Id);

            Assert.AreEqual(selected.Id, module.CurrentTrack?.Id);
            Assert.AreEqual(sessionId, module.CurrentSessionId);
            Assert.AreEqual("已由用户纠正", module.MatchDiagnostics.State);
            Assert.AreEqual(selected.Id, module.MatchDiagnostics.TopCandidates.Single().TrackId);
            Assert.AreEqual(string.Empty, store.GetAppSetting("lap.selectedTrack.simulator"));
            StringAssert.Contains(result.Message, "下次经过起点");
            Assert.HasCount(0, module.VisibleLaps);
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [TestMethod]
    public void CoarseFilterCapsFineCandidatesAndPublishesEliminationReasons()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"lazyforza-auto-match-prefilter-{Guid.NewGuid():N}.db");
        try
        {
            using var store = new LazyForzaStore(databasePath);
            for (var index = 0; index < 20; index++)
                SaveCircleTrack(store, $"Shared start {index:00}");
            var module = new LapAnalysisModule(store, TelemetrySourceKind.Simulator);
            var feed = new CircleFrameFeed(module);

            feed.Send(0, 20, raceTimeOverride: 2.1f);

            var diagnostics = module.MatchDiagnostics;
            Assert.AreEqual(20, diagnostics.TotalRoutes);
            Assert.AreEqual(20, diagnostics.CoarseEligibleRoutes);
            Assert.AreEqual(MaximumExpectedCoarseCandidates, diagnostics.FineCandidateRoutes);
            Assert.HasCount(3, diagnostics.TopCandidates);
            Assert.IsTrue(diagnostics.TopCandidates.All(candidate => candidate.Stage == "精匹配"));
            Assert.IsTrue(
                diagnostics.EliminatedCandidates.Any(candidate =>
                    candidate.EliminationReason?.Contains("未进入精匹配集合", StringComparison.Ordinal) == true),
                "Diagnostics must explain why otherwise start-compatible routes were not sent to fine projection.");
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [TestMethod]
    public void DirectionPrefilterPromotesTheCorrectRouteIntoTheFineCandidateSet()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"lazyforza-auto-match-direction-{Guid.NewGuid():N}.db");
        try
        {
            using var store = new LazyForzaStore(databasePath);
            for (var index = 0; index < MaximumExpectedCoarseCandidates; index++)
                SaveReverseCircleTrack(store, $"A reverse route {index:00}");
            var expected = SaveCircleTrack(store, "Z forward route");
            var module = new LapAnalysisModule(store, TelemetrySourceKind.Simulator);
            var feed = new CircleFrameFeed(module);

            feed.Send(0, 20, raceTimeOverride: 2.1f);
            feed.Send(0, 0, raceTimeOverride: 2.2f);
            Assert.AreEqual(13, module.MatchDiagnostics.CoarseEligibleRoutes);
            Assert.AreEqual(MaximumExpectedCoarseCandidates, module.MatchDiagnostics.FineCandidateRoutes);
            Assert.IsTrue(module.MatchDiagnostics.EliminatedCandidates.Any(candidate =>
                candidate.TrackName == expected.Name && candidate.Stage == "粗筛候补"));

            feed.Drive(0, 1, 80);

            Assert.AreEqual(
                expected.Id,
                module.CurrentTrack?.Id,
                "Forward movement must eliminate reverse candidates and promote the initially thirteenth route before fine matching. " +
                DescribeMatchDiagnostics(module));
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [TestMethod]
    public void FullOfficialCatalogIdentifiesElectricStreetCircuitDespitePersistedGoliathSelection()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"lazyforza-electric-street-{Guid.NewGuid():N}.db");
        try
        {
            using var store = new LazyForzaStore(databasePath);
            PlaygroundOfficialTrackCatalog.EnsureImported(store);
            var electricSummary = store.ListTracks("fh6_udp_live").Single(track => track.Name == "电器街环道赛");
            var goliathSummary = store.ListTracks("fh6_udp_live").Single(track => track.Name == "歌利亚");
            var electric = store.LoadTrack(electricSummary.Id)!.Value.Track;
            store.SetAppSetting("lap.selectedTrack.fh6_udp_live", goliathSummary.Id.ToString());
            var module = new LapAnalysisModule(store, TelemetrySourceKind.Live);
            Assert.IsNull(module.CurrentTrack);
            Assert.AreEqual(string.Empty, store.GetAppSetting("lap.selectedTrack.fh6_udp_live"));

            long sequence = 0;
            var arrivalTime = DateTimeOffset.UnixEpoch;
            void Send(TrackPoint point, float lapTime, float raceTime)
            {
                var raw = new Fh6RawTelemetry
                {
                    IsRaceOn = 1,
                    TimestampMS = (uint)(sequence * 20),
                    EngineMaxRpm = 8_000,
                    CurrentEngineRpm = 5_000,
                    CarOrdinal = 1,
                    CarClass = 4,
                    CarPerformanceIndex = 800,
                    DrivetrainType = 1,
                    NumCylinders = 6,
                    Position = new Vector3F((float)point.X, (float)point.Y, (float)point.Z),
                    Speed = 45,
                    CurrentLap = lapTime,
                    CurrentRaceTime = raceTime,
                    LapNumber = 0,
                    RacePosition = 1,
                    Accel = 200,
                    Gear = 4
                };
                var normalized = new NormalizedTelemetry(162, 89.5, 200, 200 / 255d, 0, 0, 0, 0.625, default);
                module.Observe(new TelemetryFrame(
                    sequence++,
                    arrivalTime,
                    TelemetrySourceKind.Live,
                    raw,
                    normalized,
                    ReadOnlyMemory<byte>.Empty));
                arrivalTime += TimeSpan.FromMilliseconds(20);
            }

            Send(electric.Points[20], 2, 4.9f);
            Send(electric.Points[0], 0, 5.0f);
            for (var index = 1; index <= 100; index++)
                Send(electric.Points[index], index * 0.05f, 5 + (index * 0.05f));

            Console.WriteLine(DescribeMatchDiagnostics(module));
            Assert.AreEqual(electricSummary.Id, module.CurrentTrack?.Id);
            Assert.AreEqual("电器街环道赛", Hud(module).TrackName);
            Assert.AreEqual(TrackMatchState.Confirmed, Hud(module).MatchState);
            Assert.AreEqual(
                electricSummary.Id.ToString(),
                store.GetAppSetting("lap.selectedTrack.fh6_udp_live"));
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [TestMethod]
    public void FullOfficialCatalogDoesNotConfuseMingweiOffroadCircuitWithAirportSprint()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"lazyforza-mingwei-airport-{Guid.NewGuid():N}.db");
        try
        {
            using var store = new LazyForzaStore(databasePath);
            PlaygroundOfficialTrackCatalog.EnsureImported(store);
            var mingweiSummary = store.ListTracks("fh6_udp_live").Single(track => track.Name == "鸣尾越野环道赛");
            var airportSummary = store.ListTracks("fh6_udp_live").Single(track => track.Name == "机场径道赛");
            var mingwei = store.LoadTrack(mingweiSummary.Id)!.Value.Track;
            var module = new LapAnalysisModule(store, TelemetrySourceKind.Live);

            long sequence = 0;
            var arrivalTime = DateTimeOffset.UnixEpoch;
            void Send(TrackPoint point)
            {
                var elapsed = 2f + (sequence * 0.02f);
                var raw = new Fh6RawTelemetry
                {
                    IsRaceOn = 1,
                    TimestampMS = (uint)(sequence * 20),
                    EngineMaxRpm = 8_000,
                    CurrentEngineRpm = 5_000,
                    CarOrdinal = 1,
                    CarClass = 4,
                    CarPerformanceIndex = 800,
                    DrivetrainType = 1,
                    NumCylinders = 6,
                    Position = new Vector3F((float)point.X, (float)point.Y, (float)point.Z),
                    Speed = 45,
                    CurrentLap = elapsed,
                    CurrentRaceTime = elapsed,
                    LapNumber = 0,
                    RacePosition = 1,
                    Accel = 200,
                    Gear = 4
                };
                var normalized = new NormalizedTelemetry(162, 89.5, 200, 200 / 255d, 0, 0, 0, 0.625, default);
                module.Observe(new TelemetryFrame(
                    sequence++,
                    arrivalTime,
                    TelemetrySourceKind.Live,
                    raw,
                    normalized,
                    ReadOnlyMemory<byte>.Empty));
                arrivalTime += TimeSpan.FromMilliseconds(20);
            }

            // FH6 grids can be behind the recorded line. Start on the final route
            // corridor and drive through the wrap without relying on a timer reset.
            var suffixStart = Math.Max(0, mingwei.Points.Count - 35);
            for (var index = suffixStart; index < mingwei.Points.Count; index++)
                Send(mingwei.Points[index]);
            for (var index = 1; index <= Math.Min(100, mingwei.Points.Count - 1); index++)
                Send(mingwei.Points[index]);

            Assert.AreEqual(mingweiSummary.Id, module.CurrentTrack?.Id);
            Assert.AreNotEqual(airportSummary.Id, module.CurrentTrack?.Id);
            Assert.AreEqual("鸣尾越野环道赛", Hud(module).TrackName);
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [TestMethod]
    [TestCategory("CatalogAudit")]
    public void EveryOfficialTrackOpeningRouteIdentifiesItself()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"lazyforza-official-match-audit-{Guid.NewGuid():N}.db");
        try
        {
            using var store = new LazyForzaStore(databasePath);
            PlaygroundOfficialTrackCatalog.EnsureImported(store);
            var officialTracks = store.ListTracks("fh6_udp_live")
                .Select(summary => store.LoadTrack(summary.Id)!.Value.Track)
                .ToArray();
            var wrongMatches = new List<string>();
            var unresolved = new List<string>();

            foreach (var expected in officialTracks)
            {
                var module = new LapAnalysisModule(store, TelemetrySourceKind.Live);
                long sequence = 0;
                var arrivalTime = DateTimeOffset.UnixEpoch;
                foreach (var point in expected.Points)
                {
                    if (point.S > 1_200) break;
                    SendLiveTrackFrame(module, point, ref sequence, ref arrivalTime);
                    if (module.CurrentTrack is not null) break;
                }

                if (module.CurrentTrack is { } actual && actual.Id != expected.Id)
                {
                    wrongMatches.Add($"{expected.Name} -> {actual.Name}");
                    Console.WriteLine($"{expected.Name} -> {actual.Name}: {DescribeMatchDiagnostics(module)}");
                }
                else if (module.CurrentTrack is null)
                    unresolved.Add(expected.Name);
            }

            Assert.IsEmpty(
                wrongMatches,
                "No official route may be replaced by a different official event: " +
                string.Join("; ", wrongMatches));
            Assert.IsEmpty(
                unresolved,
                "Every official route must identify itself within the automatic matching travel limit: " +
                string.Join("; ", unresolved));
            Console.WriteLine(
                $"Official matching audit: exact={officialTracks.Length - unresolved.Count}, " +
                $"unresolved={unresolved.Count}, wrong={wrongMatches.Count}.");
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [TestMethod]
    [TestCategory("CatalogAudit")]
    public void EveryOfficialTrackIdentifiesAfterLeavingAnOffsetStartingGrid()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"lazyforza-official-offset-grid-audit-{Guid.NewGuid():N}.db");
        try
        {
            using var store = new LazyForzaStore(databasePath);
            PlaygroundOfficialTrackCatalog.EnsureImported(store);
            var officialTracks = store.ListTracks("fh6_udp_live")
                .Select(summary => store.LoadTrack(summary.Id)!.Value.Track)
                .ToArray();
            var wrongMatches = new List<string>();
            var unresolved = new List<string>();

            foreach (var expected in officialTracks)
            {
                var module = new LapAnalysisModule(store, TelemetrySourceKind.Live);
                long sequence = 0;
                var arrivalTime = DateTimeOffset.UnixEpoch;
                var start = expected.Points[0];
                var startingGrid = start with
                {
                    X = start.X - (start.TangentZ * 79),
                    Z = start.Z + (start.TangentX * 79)
                };

                for (var frame = 0; frame < 24; frame++)
                    SendLiveTrackFrame(module, startingGrid, ref sequence, ref arrivalTime);
                foreach (var point in expected.Points)
                {
                    if (point.S > 1_500) break;
                    SendLiveTrackFrame(module, point, ref sequence, ref arrivalTime);
                    if (module.CurrentTrack is not null) break;
                }

                if (module.CurrentTrack is { } actual && actual.Id != expected.Id)
                {
                    wrongMatches.Add($"{expected.Name} -> {actual.Name}");
                    Console.WriteLine($"{expected.Name} -> {actual.Name}: {DescribeMatchDiagnostics(module)}");
                }
                else if (module.CurrentTrack is null)
                {
                    unresolved.Add(expected.Name);
                    Console.WriteLine($"{expected.Name} unresolved: {DescribeMatchDiagnostics(module)}");
                }
            }

            Assert.IsEmpty(
                wrongMatches,
                "No offset-grid replay may identify a different official event: " +
                string.Join("; ", wrongMatches));
            Assert.IsEmpty(
                unresolved,
                "Every official route must identify after leaving a start-compatible offset grid: " +
                string.Join("; ", unresolved));
            Console.WriteLine(
                $"Official offset-grid audit: exact={officialTracks.Length - unresolved.Count}, " +
                $"unresolved={unresolved.Count}, wrong={wrongMatches.Count}.");
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [TestMethod]
    [TestCategory("CatalogAudit")]
    public void OfficialReplayCorpusCoversCircuitSprintSharedStartOverpassAndWideOffroad()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"lazyforza-official-replay-corpus-{Guid.NewGuid():N}.db");
        try
        {
            using var store = new LazyForzaStore(databasePath);
            PlaygroundOfficialTrackCatalog.EnsureImported(store);
            var tracks = store.ListTracks("fh6_udp_live")
                .Select(summary => store.LoadTrack(summary.Id)!.Value.Track)
                .ToDictionary(track => track.Name, StringComparer.Ordinal);

            LapAnalysisModule Replay(TrackTemplate expected, double maximumProgress, double lateralOffset = 0)
            {
                var module = new LapAnalysisModule(store, TelemetrySourceKind.Live);
                long sequence = 0;
                var arrivalTime = DateTimeOffset.UnixEpoch;
                foreach (var point in expected.Points)
                {
                    if (point.S > maximumProgress || module.CurrentTrack is not null) break;
                    var replayPoint = lateralOffset == 0
                        ? point
                        : point with
                        {
                            X = point.X - (point.TangentZ * lateralOffset),
                            Z = point.Z + (point.TangentX * lateralOffset)
                        };
                    SendLiveTrackFrame(module, replayPoint, ref sequence, ref arrivalTime);
                }
                return module;
            }

            foreach (var name in new[] { "电器街环道赛", "机场径道赛", "彩虹桥下坡赛" })
            {
                var expected = tracks[name];
                var module = Replay(expected, 1_150);
                Assert.AreEqual(
                    expected.Id,
                    module.CurrentTrack?.Id,
                    $"{name} replay must identify the exact {(expected.LayoutKind == TrackLayoutKind.Circuit ? "circuit" : "point-to-point")} route. " +
                    DescribeMatchDiagnostics(module));
                Assert.IsTrue(module.MatchDiagnostics.FineCandidateRoutes <= MaximumExpectedCoarseCandidates);
            }

            var mingwei = tracks["鸣尾越野环道赛"];
            var wideOffroad = Replay(mingwei, 1_150, lateralOffset: 30);
            Assert.AreEqual(
                mingwei.Id,
                wideOffroad.CurrentTrack?.Id,
                "A legal 30 m offroad line must remain attributable to Mingwei instead of Airport. " +
                DescribeMatchDiagnostics(wideOffroad));

            var goliath = tracks["歌利亚"];
            var sharedStart = Replay(goliath, 250);
            Assert.IsNull(
                sharedStart.CurrentTrack,
                "Goliath and Legend Island share their opening corridor; Track Match 2.0 must delay the decision beyond 250 m.");
            Assert.IsTrue(
                sharedStart.MatchDiagnostics.TopCandidates.Any(candidate => candidate.TrackName == "歌利亚"));
            Assert.IsTrue(
                sharedStart.MatchDiagnostics.TopCandidates.Any(candidate => candidate.TrackName == "传奇岛径道赛"));
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [TestMethod]
    public void OfficialShowcaseRoutesWaitForCarsToLeaveOffsetStartingGrids()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"lazyforza-official-showcase-grid-{Guid.NewGuid():N}.db");
        try
        {
            using var store = new LazyForzaStore(databasePath);
            PlaygroundOfficialTrackCatalog.EnsureImported(store);
            var tracks = store.ListTracks("fh6_udp_live")
                .Select(summary => store.LoadTrack(summary.Id)!.Value.Track)
                .ToDictionary(track => track.Name, StringComparer.Ordinal);

            foreach (var name in new[] { "巨人对决", "苦行" })
            {
                var expected = tracks[name];
                var start = expected.Points[0];
                var startingGrid = start with
                {
                    X = start.X - (start.TangentZ * 79),
                    Z = start.Z + (start.TangentX * 79)
                };
                var module = new LapAnalysisModule(store, TelemetrySourceKind.Live);
                long sequence = 0;
                var arrivalTime = DateTimeOffset.UnixEpoch;

                for (var frame = 0; frame < 24; frame++)
                    SendLiveTrackFrame(module, startingGrid, ref sequence, ref arrivalTime);

                Assert.IsNull(
                    module.CurrentTrack,
                    $"{name} must remain a candidate while the car is staged outside the recorded route corridor.");
                Assert.IsFalse(
                    Hud(module).MatchRejectionEligible,
                    $"{name} must not be rejected before the car can leave its offset starting grid. " +
                    DescribeMatchDiagnostics(module));

                foreach (var point in expected.Points)
                {
                    if (point.S > 400 || module.CurrentTrack is not null) break;
                    SendLiveTrackFrame(module, point, ref sequence, ref arrivalTime);
                }

                Assert.AreEqual(
                    expected.Id,
                    module.CurrentTrack?.Id,
                    $"{name} must identify after the car joins the recorded route. " +
                    DescribeMatchDiagnostics(module));
            }
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [TestMethod]
    public void SustainedExtremeRouteDeviationRematchesWithoutSavingThePartialWrongLap()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"lazyforza-severe-rematch-{Guid.NewGuid():N}.db");
        try
        {
            using var store = new LazyForzaStore(databasePath);
            var first = SaveCircleTrack(store, "First circuit");
            var offset = new Vector3F(5_000, 0, 5_000);
            var second = SaveCircleTrack(store, "Second circuit", offset);
            var diagnosticSignals = new List<DiagnosticSignal>();
            var module = new LapAnalysisModule(
                store,
                TelemetrySourceKind.Simulator,
                diagnosticSink: diagnosticSignals.Add);
            var feed = new CircleFrameFeed(module);

            feed.Send(0, 20, raceTimeOverride: 2.1f);
            feed.Send(0, 0, raceTimeOverride: 2.2f);
            feed.Drive(0, 1, 80);
            Assert.AreEqual(first.Id, module.CurrentTrack?.Id);

            for (var frame = 81; frame <= 190; frame++)
            {
                var position = Position(frame);
                feed.Send(
                    0,
                    frame,
                    new Vector3F(position.X + offset.X, position.Y + offset.Y, position.Z + offset.Z));
            }

            Assert.AreEqual(second.Id, module.CurrentTrack?.Id);
            Assert.AreEqual("Second circuit", Hud(module).TrackName);
            Assert.HasCount(0, module.VisibleLaps,
                "A route locked from the middle must wait for a real start line instead of storing a partial lap.");
            Assert.IsTrue(diagnosticSignals.Any(signal =>
                signal.Code == "track.rematch" &&
                signal.IsAnomaly));
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [TestMethod]
    public void WideOffroadExcursionDoesNotTriggerRouteRematching()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"lazyforza-offroad-corridor-{Guid.NewGuid():N}.db");
        try
        {
            using var store = new LazyForzaStore(databasePath);
            var saved = SaveCircleTrack(store, "Wide offroad circuit");
            var offroad = saved with { Category = "越野" };
            store.SaveTrack(offroad, TrackAlgorithms.CreateSectors(offroad));
            var module = new LapAnalysisModule(store, TelemetrySourceKind.Simulator);
            var feed = new CircleFrameFeed(module);

            feed.Send(0, 20, raceTimeOverride: 2.1f);
            feed.Send(0, 0, raceTimeOverride: 2.2f);
            feed.Drive(0, 1, 80);
            Assert.AreEqual(offroad.Id, module.CurrentTrack?.Id);

            for (var frame = 81; frame <= 130; frame++)
            {
                var position = Position(frame);
                feed.Send(0, frame, new Vector3F(position.X, position.Y + 200, position.Z));
            }

            Assert.AreEqual(offroad.Id, module.CurrentTrack?.Id,
                "A 200 m offroad line choice remains inside the conservative 250 m rematch corridor.");
            Assert.AreNotEqual("未识别赛事", Hud(module).TrackName);
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [TestMethod]
    public void RewindWithinLapTrimsFutureSamplesWithoutCreatingFalseLap()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"lazyforza-lap-rewind-{Guid.NewGuid():N}.db");
        try
        {
            using var store = new LazyForzaStore(databasePath);
            SaveCircleTrack(store);
            var module = new LapAnalysisModule(store, TelemetrySourceKind.Simulator);
            var feed = new CircleFrameFeed(module);

            feed.Send(0, 20, raceTimeOverride: 2.1f);
            feed.Send(0, 0, raceTimeOverride: 2.2f);
            feed.Drive(0, 1, 239);
            feed.Send(1, 0);
            feed.Drive(1, 1, 100);
            Assert.HasCount(1, module.VisibleLaps);

            feed.Send(1, 60, raceTimeOverride: 30);
            Assert.HasCount(1, module.VisibleLaps);

            feed.Drive(1, 61, 239);
            feed.Send(2, 0);

            Assert.HasCount(2, module.VisibleLaps);
            Assert.IsTrue(module.VisibleLaps[^1].IsValid, module.VisibleLaps[^1].InvalidReason);
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [TestMethod]
    public void TimerResetBeforeLapNumberAdvanceSavesConsecutiveLapsWithOfficialLastLapTime()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"lazyforza-delayed-lap-number-{Guid.NewGuid():N}.db");
        try
        {
            using var store = new LazyForzaStore(databasePath);
            SaveCircleTrack(store);
            var module = new LapAnalysisModule(store, TelemetrySourceKind.Simulator);
            var feed = new CircleFrameFeed(module);

            feed.Send(1, 20, raceTimeOverride: 2.1f);
            feed.Send(1, 0, raceTimeOverride: 2.2f);
            feed.Drive(1, 1, 239);

            feed.Send(1, 0, raceTimeOverride: 48.015f, lastLapOverride: 24.015f);
            Assert.HasCount(1, module.VisibleLaps, "CurrentLap 清零时应立即保存，不等待下一帧 LapNumber。 ");
            Assert.AreEqual(24.015, module.VisibleLaps[0].TotalSeconds, 0.0005);

            feed.Send(2, 1, raceTimeOverride: 48.115f, lastLapOverride: 24.015f);
            Assert.HasCount(1, module.VisibleLaps, "随后到达的 LapNumber 增加帧不能重复触发冲线。");
            feed.Drive(2, 2, 239);
            feed.Send(2, 0, raceTimeOverride: 72.025f, lastLapOverride: 24.025f);

            Assert.HasCount(2, module.VisibleLaps, "下一圈必须继续采样并连续入库。");
            Assert.AreEqual(24.025, module.VisibleLaps[1].TotalSeconds, 0.0005);
            Assert.IsTrue(module.VisibleLaps.All(lap => lap.IsValid),
                string.Join(" | ", module.VisibleLaps.Select(lap => lap.InvalidReason)));
            Assert.IsTrue(module.VisibleLaps.All(lap =>
                Math.Abs(lap.Segments.Sum(segment => segment.TimeSeconds) - lap.TotalSeconds) < 0.0005));
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [TestMethod]
    public void MenuAndRewindPreserveSessionAndStillSaveFollowingLaps()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"lazyforza-suspended-race-{Guid.NewGuid():N}.db");
        try
        {
            using var store = new LazyForzaStore(databasePath);
            SaveCircleTrack(store);
            var module = new LapAnalysisModule(store, TelemetrySourceKind.Simulator);
            var feed = new CircleFrameFeed(module);

            feed.Send(1, 20, raceTimeOverride: 2.1f);
            var sessionId = module.CurrentSessionId;
            feed.Send(1, 0, raceTimeOverride: 2.2f);
            feed.Drive(1, 1, 150);

            feed.SendInactive();
            feed.SendInactive();
            Assert.IsFalse(module.IsCompetitionActive, "The HUD should hide while the competition signal is suspended.");
            Assert.IsTrue(module.HasCurrentCompetitionSession, "The current competition page must remain available in a menu or rewind.");
            Assert.AreEqual(sessionId, module.CurrentSessionId, "A menu frame must not replace the current race session.");
            Assert.IsFalse(Hud(module).IsCompetitionActive);
            Assert.IsNotNull(module.CurrentCompetitionSnapshot, "The current competition page needs the last active snapshot while values are frozen.");

            feed.Send(1, 80, raceTimeOverride: 30f);
            Assert.IsTrue(module.IsCompetitionActive);
            Assert.AreEqual(sessionId, module.CurrentSessionId, "Returning from a rewind must continue the same race session.");
            feed.Drive(1, 81, 239);
            feed.Send(1, 0, raceTimeOverride: 48.015f, lastLapOverride: 24.015f);

            Assert.HasCount(1, module.CurrentSessionLaps, "The rewound lap should still be completed after future samples are trimmed.");
            Assert.AreEqual(24.015, module.CurrentSessionLaps[0].TotalSeconds, 0.0005);

            feed.Send(2, 1, raceTimeOverride: 48.115f, lastLapOverride: 24.015f);
            feed.Drive(2, 2, 239);
            feed.SendInactive();
            feed.Send(2, 0, raceTimeOverride: 72.025f, lastLapOverride: 24.025f);

            Assert.AreEqual(sessionId, module.CurrentSessionId);
            Assert.HasCount(2, module.CurrentSessionLaps,
                "A transient menu/rewind signal immediately before the line must not drop the next completed lap.");
            Assert.AreEqual(24.025, module.CurrentSessionLaps[1].TotalSeconds, 0.0005);
            Assert.IsTrue(module.CurrentSessionLaps.All(lap => lap.IsValid),
                string.Join(" | ", module.CurrentSessionLaps.Select(lap => lap.InvalidReason)));

            for (var index = 0; index <= 20; index++) feed.SendFreeRoam();
            Assert.IsFalse(module.HasCurrentCompetitionSession,
                "Sustained non-competition driving should close the current competition after the player exits the event.");
            Assert.IsNull(module.CurrentCompetitionSnapshot);
            Assert.IsTrue(module.HasCompetitionPageContent,
                "The finished competition must remain available on the current-competition page for five minutes.");
            Assert.IsTrue(module.IsShowingRecentCompetition);
            Assert.IsNotNull(module.CompetitionPageSnapshot);
            Assert.IsNotNull(module.RecentCompetitionExpiresAt);
            Assert.IsTrue(module.RecentCompetitionExpiresAt.Value > DateTimeOffset.UtcNow + TimeSpan.FromMinutes(4.9));
            Assert.HasCount(2, module.CurrentSessionLaps,
                "Retaining the page must keep the completed laps from the finished session.");

            feed.Send(0, 20, raceTimeOverride: 2.1f);
            Assert.IsTrue(module.HasCurrentCompetitionSession,
                "A newly detected competition must immediately replace the retained previous competition.");
            Assert.IsFalse(module.IsShowingRecentCompetition);
            Assert.AreNotEqual(sessionId, module.CurrentSessionId);
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [TestMethod]
    public void ContinuingRaceClockAfterInferredEndCreatesFalseEndDiagnostic()
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            $"lazyforza-false-end-diagnostic-{Guid.NewGuid():N}.db");
        try
        {
            using var store = new LazyForzaStore(databasePath);
            SaveCircleTrack(store);
            var diagnosticSignals = new List<DiagnosticSignal>();
            var module = new LapAnalysisModule(
                store,
                TelemetrySourceKind.Simulator,
                diagnosticSink: diagnosticSignals.Add);
            var feed = new CircleFrameFeed(module);

            feed.Send(1, 20, raceTimeOverride: 2.1f);
            feed.Send(1, 0, raceTimeOverride: 2.2f);
            feed.Drive(1, 1, 150);
            for (var index = 0; index <= 20; index++) feed.SendFreeRoam();
            Assert.IsFalse(module.HasCurrentCompetitionSession);

            feed.Send(1, 151, raceTimeOverride: 39.5f);

            Assert.IsTrue(diagnosticSignals.Any(signal =>
                signal.Code == "race.false-end-recovered" &&
                signal.IsAnomaly));
            Assert.IsTrue(diagnosticSignals.Any(signal =>
                signal.Code == "race.inferred-end" &&
                !signal.IsAnomaly));
            Assert.IsTrue(diagnosticSignals.Any(signal =>
                signal.Code == "lap.not-settled-on-exit" &&
                signal.IsAnomaly));
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [TestMethod]
    public void OfficialLastLapUpdateCompletesFinalLapWithoutTimerReset()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"lazyforza-final-lastlap-{Guid.NewGuid():N}.db");
        try
        {
            using var store = new LazyForzaStore(databasePath);
            SaveCircleTrack(store);
            var module = new LapAnalysisModule(store, TelemetrySourceKind.Simulator);
            var feed = new CircleFrameFeed(module);

            feed.Send(0, 20, raceTimeOverride: 2.1f);
            feed.Send(0, 0, raceTimeOverride: 2.2f);
            feed.Drive(0, 1, 239);
            feed.Send(1, 0, raceTimeOverride: 26.215f, lastLapOverride: 24.015f);
            Assert.HasCount(1, module.CurrentSessionLaps);

            feed.Drive(1, 1, 239);
            feed.Send(1, 240, raceTimeOverride: 50.340f, lastLapOverride: 24.125f);

            Assert.HasCount(2, module.CurrentSessionLaps,
                "FH6 may update LastLap for the final lap without resetting CurrentLap or incrementing LapNumber.");
            Assert.AreEqual(24.125, module.CurrentSessionLaps[1].TotalSeconds, 0.0005,
                "The official LastLap value should remain the preferred final-lap time.");
            Assert.IsTrue(module.CurrentSessionLaps[1].IsValid);
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [TestMethod]
    public void EventExitRecoversFinalLapWhenNoCompletionCounterChanges()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"lazyforza-final-exit-{Guid.NewGuid():N}.db");
        try
        {
            using var store = new LazyForzaStore(databasePath);
            SaveCircleTrack(store);
            var module = new LapAnalysisModule(store, TelemetrySourceKind.Simulator);
            var feed = new CircleFrameFeed(module);

            feed.Send(0, 20, raceTimeOverride: 2.1f);
            feed.Send(0, 0, raceTimeOverride: 2.2f);
            feed.Drive(0, 1, 239);
            feed.Send(1, 0, raceTimeOverride: 26.215f, lastLapOverride: 24.015f);
            feed.Drive(1, 1, 239);
            feed.Send(2, 0, raceTimeOverride: 50.225f, lastLapOverride: 24.010f);
            Assert.HasCount(2, module.CurrentSessionLaps);

            feed.Drive(2, 1, 240);
            feed.SendInactive();
            feed.SendInactive();
            Assert.HasCount(2, module.CurrentSessionLaps,
                "A transient menu or pause must not finalize a lap by itself.");

            for (var index = 0; index <= 20; index++) feed.SendFreeRoam();

            Assert.HasCount(3, module.CurrentSessionLaps,
                "Confirmed event exit should recover a geometrically complete final lap even when FH6 sends no reset counter.");
            Assert.AreEqual(24.0, module.CurrentSessionLaps[2].TotalSeconds, 0.0005);
            Assert.IsTrue(module.CurrentSessionLaps[2].IsValid, module.CurrentSessionLaps[2].InvalidReason);
            Assert.IsTrue(module.IsShowingRecentCompetition);
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [TestMethod]
    public void RewindNearStartLineDoesNotMasqueradeAsFinalLapCompletion()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"lazyforza-final-rewind-guard-{Guid.NewGuid():N}.db");
        try
        {
            using var store = new LazyForzaStore(databasePath);
            SaveCircleTrack(store);
            var module = new LapAnalysisModule(store, TelemetrySourceKind.Simulator);
            var feed = new CircleFrameFeed(module);

            feed.Send(0, 20, raceTimeOverride: 2.1f);
            feed.Send(0, 0, raceTimeOverride: 2.2f);
            feed.Drive(0, 1, 239);
            feed.Send(1, 0, raceTimeOverride: 26.215f, lastLapOverride: 24.015f);
            feed.Drive(1, 1, 239);
            feed.Send(2, 0, raceTimeOverride: 50.225f, lastLapOverride: 24.010f);
            Assert.HasCount(2, module.CurrentSessionLaps);

            feed.Drive(2, 1, 240);
            feed.Send(2, 235, Position(240), raceTimeOverride: 73.725f, lastLapOverride: 23.500f);
            Assert.HasCount(2, module.CurrentSessionLaps,
                "A LastLap change on the same frame as a rewind must not be treated as a finish signal.");

            feed.SendInactive();
            for (var index = 0; index <= 20; index++) feed.SendFreeRoam();

            Assert.HasCount(2, module.CurrentSessionLaps,
                "Exiting immediately after rewinding near the start line must not recover a false final lap.");
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [TestMethod]
    public void SectorHistoryComparisonUsesOnlyTheCurrentPerformanceClass()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"lazyforza-class-comparison-{Guid.NewGuid():N}.db");
        try
        {
            using var store = new LazyForzaStore(databasePath);
            SaveCircleTrack(store);
            var savedTrack = store.LoadLatestTrack("simulator")!.Value;
            var classFourVehicle = new VehicleProfileFingerprint(1, 4, 800, 1, 6, 8000, "g", "c");
            var classFiveVehicle = new VehicleProfileFingerprint(2, 5, 900, 1, 8, 8500, "g", "c");
            store.SaveLap(new LapRecord(
                Guid.NewGuid(), savedTrack.Track.Id, savedTrack.Track.Direction, TrackAlgorithms.SectorSchemaVersion,
                Guid.NewGuid(), classFourVehicle, DateTimeOffset.UnixEpoch, 40, true, null,
                savedTrack.Sectors.Select(sector => new LapSegment(sector.Index, 10, true)).ToArray(), []));
            store.SaveLap(new LapRecord(
                Guid.NewGuid(), savedTrack.Track.Id, savedTrack.Track.Direction, TrackAlgorithms.SectorSchemaVersion,
                Guid.NewGuid(), classFiveVehicle, DateTimeOffset.UnixEpoch.AddMinutes(1), 20, true, null,
                savedTrack.Sectors.Select(sector => new LapSegment(sector.Index, 5, true)).ToArray(), []));

            var module = new LapAnalysisModule(store, TelemetrySourceKind.Simulator);
            var feed = new CircleFrameFeed(module);
            feed.Send(0, 20, raceTimeOverride: 2.1f, carClass: 4, performanceIndex: 800);
            feed.Send(0, 0, raceTimeOverride: 2.2f, carClass: 4, performanceIndex: 800);
            feed.Drive(0, 1, 80, carClass: 4, performanceIndex: 800);

            var firstSector = Hud(module).Sectors[0];
            Assert.IsNotNull(firstSector.HistoricalBestSeconds);
            Assert.AreEqual(10, firstSector.HistoricalBestSeconds.Value, 0.0005,
                "S1 当前圈不能使用更快的 S2 历史分段作为比较基准。");
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [TestMethod]
    public void TrackBulkDeletePreservesEachClassBestAndCanTargetSelectedClasses()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"lazyforza-delete-track-laps-{Guid.NewGuid():N}.db");
        try
        {
            using var store = new LazyForzaStore(databasePath);
            SaveCircleTrack(store);
            var module = new LapAnalysisModule(store, TelemetrySourceKind.Simulator);
            var feed = new CircleFrameFeed(module);

            feed.Send(0, 20, raceTimeOverride: 2.1f, carClass: 4, performanceIndex: 800);
            feed.Send(0, 0, raceTimeOverride: 2.2f, carClass: 4, performanceIndex: 800);
            feed.Drive(0, 1, 239, carClass: 4, performanceIndex: 800);
            feed.Send(1, 0, raceTimeOverride: 26.215f, lastLapOverride: 24.015f, carClass: 4, performanceIndex: 800);
            feed.Drive(1, 1, 239, carClass: 5, performanceIndex: 900);
            feed.Send(2, 0, raceTimeOverride: 50.125f, lastLapOverride: 23.910f, carClass: 5, performanceIndex: 900);

            Assert.HasCount(2, module.VisibleLaps);
            var trackId = module.CurrentTrack!.Id;

            module.DeleteTrackLaps(trackId, deleteHistoricalBests: false);

            Assert.HasCount(2, module.VisibleLaps, "默认删除必须为每个性能等级各保留一条最快有效圈。");
            Assert.AreEqual(2, store.CountLaps(trackId));
            Assert.IsNotNull(store.LoadTrack(trackId));

            module.DeleteTrackLaps(trackId, deleteHistoricalBests: true, performanceClasses: new HashSet<int> { 4 });

            Assert.HasCount(1, module.VisibleLaps);
            Assert.AreEqual(5, module.VisibleLaps[0].Vehicle.CarClass);
            Assert.AreEqual(1, store.CountLaps(trackId));

            module.DeleteTrackLaps(trackId, deleteHistoricalBests: true);

            Assert.IsEmpty(module.VisibleLaps);
            Assert.AreEqual(0, store.CountLaps(trackId));
            Assert.IsNotNull(store.LoadTrack(trackId));
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [TestMethod]
    public void LearnsPointToPointRouteWhenOfficialFinishTimeAppears()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"lazyforza-point-to-point-learning-{Guid.NewGuid():N}.db");
        try
        {
            using var store = new LazyForzaStore(databasePath);
            var module = new LapAnalysisModule(store, TelemetrySourceKind.Simulator);
            var feed = new PointToPointFrameFeed(module);

            feed.Send(1);
            feed.Drive(2, 199);
            feed.Send(200, lastLapOverride: 20.0f);

            Assert.IsNotNull(module.CurrentTrack);
            Assert.AreEqual(TrackLayoutKind.PointToPoint, module.CurrentTrack.LayoutKind);
            Assert.IsTrue(Math.Sqrt(module.CurrentTrack.Points[0].DistanceSquaredTo(module.CurrentTrack.Points[^1])) > 900,
                "Point-to-point templates must retain a distinct finish instead of being closed back to the start.");
            var persisted = store.LoadLatestTrack("simulator")!.Value.Track;
            Assert.AreEqual(TrackLayoutKind.PointToPoint, persisted.LayoutKind);
            Assert.AreEqual(module.CurrentTrack.Id, persisted.Id);
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [TestMethod]
    public void LearnsPointToPointRouteAtConfirmedEventExitWhenFh6DoesNotPublishLastLap()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"lazyforza-point-to-point-exit-learning-{Guid.NewGuid():N}.db");
        try
        {
            using var store = new LazyForzaStore(databasePath);
            var module = new LapAnalysisModule(store, TelemetrySourceKind.Simulator);
            var feed = new PointToPointFrameFeed(module);

            feed.Send(1);
            feed.Drive(2, 200);
            for (var index = 0; index <= 20; index++) feed.SendFreeRoam();

            Assert.IsNotNull(module.CurrentTrack,
                "FH6 may leave LastLap at zero for point-to-point events, so a continuous open route must be finalized at confirmed event exit.");
            Assert.AreEqual(TrackLayoutKind.PointToPoint, module.CurrentTrack.LayoutKind);
            Assert.IsTrue(module.CurrentTrack.LengthMeters >= 900);
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [TestMethod]
    public void PointToPointExitFallbackRejectsCaptureThatStartedMidEvent()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"lazyforza-point-to-point-mid-event-{Guid.NewGuid():N}.db");
        try
        {
            using var store = new LazyForzaStore(databasePath);
            var module = new LapAnalysisModule(store, TelemetrySourceKind.Simulator);
            var feed = new PointToPointFrameFeed(module);

            feed.Send(80);
            feed.Drive(81, 200);
            for (var index = 0; index <= 20; index++) feed.SendFreeRoam();

            Assert.IsNull(module.CurrentTrack,
                "An open trace captured only after joining an event in progress must not be mistaken for a complete point-to-point route.");
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [TestMethod]
    public void SavedPointToPointRouteArmsAtStartAndSavesAtFinishWithoutLapReset()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"lazyforza-point-to-point-comparison-{Guid.NewGuid():N}.db");
        try
        {
            using var store = new LazyForzaStore(databasePath);
            SavePointToPointTrack(store);
            var module = new LapAnalysisModule(store, TelemetrySourceKind.Simulator);
            var feed = new PointToPointFrameFeed(module);

            feed.Send(1);
            feed.Drive(2, 199);
            feed.Send(200, lastLapOverride: 20.0f);

            Assert.HasCount(1, module.CurrentSessionLaps);
            var run = module.CurrentSessionLaps[0];
            Assert.IsTrue(run.IsValid, run.InvalidReason);
            Assert.AreEqual(20.0, run.TotalSeconds, 0.0005);
            Assert.AreEqual(module.CurrentTrack!.Id, run.TrackId);
            Assert.AreEqual(TrackLayoutKind.PointToPoint, module.CurrentTrack.LayoutKind);
            Assert.IsTrue(Hud(module).IsPointToPoint,
                "The HUD must expose point-to-point timing as approximate.");
            Assert.IsFalse(Hud(module).CurrentLapValid,
                "After a point-to-point finish the analyzer must not arm a fictitious next lap.");
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [TestMethod]
    public void PointToPointOfficialResultAfterMenuIsSavedBeforeRaceRestart()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"lazyforza-point-to-point-result-menu-{Guid.NewGuid():N}.db");
        try
        {
            using var store = new LazyForzaStore(databasePath);
            SavePointToPointTrack(store);
            var module = new LapAnalysisModule(store, TelemetrySourceKind.Simulator);
            var feed = new PointToPointFrameFeed(module);

            feed.Send(1);
            var firstSession = module.CurrentSessionId;
            feed.Drive(2, 199);
            feed.SendInactive();
            feed.Send(
                200,
                lastLapOverride: 20.0f,
                lapNumberOverride: 1,
                currentLapOverride: 0,
                raceTimeOverride: 22.0f);

            Assert.HasCount(1, module.CurrentSessionLaps,
                "A point-to-point result published after a menu frame must commit the completed run before restart.");
            Assert.AreEqual(20.0, module.CurrentSessionLaps[0].TotalSeconds, 0.0005);
            Assert.IsTrue(module.CurrentSessionLaps[0].IsValid, module.CurrentSessionLaps[0].InvalidReason);
            Assert.IsFalse(Hud(module).CurrentLapValid,
                "An official point-to-point finish must not arm a fictitious next lap.");

            feed.SendInactive();
            feed.Send(
                1,
                lapNumberOverride: 0,
                currentLapOverride: 0.1f,
                raceTimeOverride: 0.2f);

            Assert.AreNotEqual(firstSession, module.CurrentSessionId,
                "Restarting the event must open a new session after the completed run has been committed.");
            Assert.AreEqual(1, module.VisibleLaps.Count);
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [TestMethod]
    public void PointToPointRestartRecoversPendingRunWhenLastLapNeverArrives()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"lazyforza-point-to-point-restart-fallback-{Guid.NewGuid():N}.db");
        try
        {
            using var store = new LazyForzaStore(databasePath);
            SavePointToPointTrack(store);
            var module = new LapAnalysisModule(store, TelemetrySourceKind.Simulator);
            var feed = new PointToPointFrameFeed(module);

            feed.Send(1);
            var firstSession = module.CurrentSessionId;
            feed.Drive(2, 200);
            feed.SendInactive();
            feed.Send(
                1,
                lapNumberOverride: 0,
                currentLapOverride: 0.1f,
                raceTimeOverride: 0.2f);

            Assert.HasCount(1, module.VisibleLaps,
                "A direct race restart must recover the geometrically complete run even when FH6 never publishes LastLap.");
            Assert.AreEqual(20.0, module.VisibleLaps[0].TotalSeconds, 0.0005,
                "The terminal CurrentLap timer must be preferred over a route sample captured one frame early.");
            Assert.AreEqual(firstSession, module.VisibleLaps[0].SessionId);
            Assert.AreNotEqual(firstSession, module.CurrentSessionId);
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [TestMethod]
    public void PointToPointExitRecoversPendingRunWhenLastLapNeverArrives()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"lazyforza-point-to-point-exit-fallback-{Guid.NewGuid():N}.db");
        try
        {
            using var store = new LazyForzaStore(databasePath);
            SavePointToPointTrack(store);
            var module = new LapAnalysisModule(store, TelemetrySourceKind.Simulator);
            var feed = new PointToPointFrameFeed(module);

            feed.Send(1);
            feed.Drive(2, 200);
            for (var index = 0; index <= 20; index++) feed.SendFreeRoam();

            Assert.HasCount(1, module.VisibleLaps,
                "Confirmed event exit must recover a complete point-to-point run when FH6 never publishes LastLap.");
            Assert.AreEqual(20.0, module.VisibleLaps[0].TotalSeconds, 0.0005,
                "The terminal CurrentLap timer must be preferred over a route sample captured one frame early.");
            Assert.IsTrue(module.VisibleLaps[0].IsValid, module.VisibleLaps[0].InvalidReason);
            Assert.IsFalse(module.HasCurrentCompetitionSession);
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [TestMethod]
    public void FirstRunWithoutHistoryShowsCompletedPointToPointSectorsAsPurple()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"lazyforza-point-to-point-first-run-color-{Guid.NewGuid():N}.db");
        try
        {
            using var store = new LazyForzaStore(databasePath);
            SavePointToPointTrack(store);
            var module = new LapAnalysisModule(store, TelemetrySourceKind.Simulator);
            var feed = new PointToPointFrameFeed(module);

            feed.Send(1);
            feed.Drive(2, 80);

            var completedSectors = Hud(module).Sectors.Where(sector => sector.CurrentSeconds is not null).ToArray();
            Assert.IsNotEmpty(completedSectors);
            Assert.IsTrue(completedSectors.All(sector => sector.State == SectorColorState.Purple),
                "With no saved laps, every completed valid sector is the current historical best.");
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [TestMethod]
    public void PointToPointLiveSectorsIgnoreCurrentSessionBest()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"lazyforza-point-to-point-history-color-{Guid.NewGuid():N}.db");
        try
        {
            using var store = new LazyForzaStore(databasePath);
            SavePointToPointTrack(store);
            var saved = store.LoadLatestTrack("simulator")!.Value;
            var historicalVehicle = new VehicleProfileFingerprint(1, 4, 800, 1, 6, 8000, "g", "c");
            store.SaveLap(new LapRecord(
                Guid.NewGuid(),
                saved.Track.Id,
                saved.Track.Direction,
                TrackAlgorithms.SectorSchemaVersion,
                Guid.NewGuid(),
                historicalVehicle,
                DateTimeOffset.UnixEpoch,
                saved.Sectors.Count,
                true,
                null,
                saved.Sectors.Select(sector => new LapSegment(sector.Index, 1, true)).ToArray(),
                []));
            var module = new LapAnalysisModule(store, TelemetrySourceKind.Simulator);
            var feed = new PointToPointFrameFeed(module);

            feed.Send(1);
            feed.Drive(2, 80);

            var completedSectors = Hud(module).Sectors.Where(sector => sector.CurrentSeconds is not null).ToArray();
            Assert.IsNotEmpty(completedSectors);
            Assert.IsTrue(completedSectors.All(sector => sector.State == SectorColorState.Yellow),
                "A slower point-to-point sector must be yellow even when it is the fastest sector of the current session.");
            Assert.IsFalse(completedSectors.Any(sector => sector.State == SectorColorState.Green));
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    private static TrackTemplate SaveCircleTrack(
        LazyForzaStore store,
        string name = "Integration circle",
        Vector3F? offset = null)
    {
        const int framesPerLap = 240;
        var translation = offset ?? default;
        var rawPoints = Enumerable.Range(0, framesPerLap + 1)
            .Select(index => Position(index % framesPerLap))
            .Select(position => new Vector3F(
                position.X + translation.X,
                position.Y + translation.Y,
                position.Z + translation.Z))
            .Select(position => new TrackPoint(position.X, position.Y, position.Z, 0, 0, 0))
            .ToArray();
        var track = TrackAlgorithms.BuildTemplate(name, rawPoints) with { Source = "simulator" };
        store.SaveTrack(track, TrackAlgorithms.CreateSectors(track));
        return track;
    }

    private static TrackTemplate SaveReverseCircleTrack(
        LazyForzaStore store,
        string name)
    {
        const int framesPerLap = 240;
        var rawPoints = Enumerable.Range(0, framesPerLap + 1)
            .Select(index => Position((framesPerLap - index) % framesPerLap))
            .Select(position => new TrackPoint(position.X, position.Y, position.Z, 0, 0, 0))
            .ToArray();
        var track = TrackAlgorithms.BuildTemplate(name, rawPoints, direction: -1) with { Source = "simulator" };
        store.SaveTrack(track, TrackAlgorithms.CreateSectors(track));
        return track;
    }

    private static void SavePointToPointTrack(LazyForzaStore store)
    {
        var rawPoints = Enumerable.Range(0, 201)
            .Select(index => PointToPointPosition(index))
            .Select(position => new TrackPoint(position.X, position.Y, position.Z, 0, 0, 0))
            .ToArray();
        var track = TrackAlgorithms.BuildTemplate(
            "Integration point-to-point",
            rawPoints,
            layoutKind: TrackLayoutKind.PointToPoint) with { Source = "simulator" };
        store.SaveTrack(track, TrackAlgorithms.CreateSectors(track));
    }

    private static LapHudState Hud(LapAnalysisModule module) =>
        module.Snapshot as LapHudState ?? throw new AssertFailedException("Lap HUD snapshot was not published.");

    private static void SendLiveTrackFrame(
        LapAnalysisModule module,
        TrackPoint point,
        ref long sequence,
        ref DateTimeOffset arrivalTime)
    {
        var elapsed = 2f + (sequence * 0.02f);
        var raw = new Fh6RawTelemetry
        {
            IsRaceOn = 1,
            TimestampMS = (uint)(sequence * 20),
            EngineMaxRpm = 8_000,
            CurrentEngineRpm = 5_000,
            CarOrdinal = 1,
            CarClass = 4,
            CarPerformanceIndex = 800,
            DrivetrainType = 1,
            NumCylinders = 6,
            Position = new Vector3F((float)point.X, (float)point.Y, (float)point.Z),
            Speed = 45,
            CurrentLap = elapsed,
            CurrentRaceTime = elapsed,
            LapNumber = 0,
            RacePosition = 1,
            Accel = 200,
            Gear = 4
        };
        var normalized = new NormalizedTelemetry(162, 89.5, 200, 200 / 255d, 0, 0, 0, 0.625, default);
        module.Observe(new TelemetryFrame(
            sequence++,
            arrivalTime,
            TelemetrySourceKind.Live,
            raw,
            normalized,
            ReadOnlyMemory<byte>.Empty));
        arrivalTime += TimeSpan.FromMilliseconds(20);
    }

    private static Vector3F Position(int lapFrame)
    {
        const int framesPerLap = 240;
        var angle = lapFrame / (double)framesPerLap * Math.PI * 2;
        return new Vector3F((float)(150 * Math.Cos(angle)), (float)(3 * Math.Sin(angle * 2)), (float)(110 * Math.Sin(angle)));
    }

    private static Vector3F PointToPointPosition(int frame) =>
        new(frame * 5f, (float)Math.Sin(frame / 20d) * 2f, (float)Math.Sin(frame / 12d) * 20f);

    private static string DescribeMatchDiagnostics(LapAnalysisModule module)
    {
        var diagnostics = module.MatchDiagnostics;
        var candidates = diagnostics.TopCandidates.Count == 0
            ? "none"
            : string.Join("; ", diagnostics.TopCandidates.Select(candidate =>
                $"{candidate.TrackName}/{candidate.Stage}/start={candidate.StartDistanceMeters:0.0}/" +
                $"mean={candidate.MeanDistanceMeters:0.0}/progress={candidate.ProgressMeters:0}/" +
                $"valid={candidate.ValidRatio:P0}/reason={candidate.EliminationReason ?? "none"}"));
        var eliminated = diagnostics.EliminatedCandidates.Count == 0
            ? "none"
            : string.Join("; ", diagnostics.EliminatedCandidates.Select(candidate =>
                $"{candidate.TrackName}/{candidate.EliminationReason ?? "none"}"));
        return $"state={diagnostics.State}, total={diagnostics.TotalRoutes}, " +
               $"coarse={diagnostics.CoarseEligibleRoutes}, fine={diagnostics.FineCandidateRoutes}, " +
               $"top={candidates}, eliminated={eliminated}";
    }

    private static void DeleteDatabase(string databasePath)
    {
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            var file = databasePath + suffix;
            if (File.Exists(file)) File.Delete(file);
        }
    }

    private sealed class PointToPointFrameFeed(LapAnalysisModule module)
    {
        private long sequence;
        private DateTimeOffset arrivalTime = DateTimeOffset.UnixEpoch;

        public void Drive(int firstFrame, int lastFrame)
        {
            for (var frame = firstFrame; frame <= lastFrame; frame++) Send(frame);
        }

        public void Send(
            int frame,
            float? lastLapOverride = null,
            ushort? lapNumberOverride = null,
            float? currentLapOverride = null,
            float? raceTimeOverride = null)
        {
            var currentLap = currentLapOverride ?? frame / 10f;
            var raw = new Fh6RawTelemetry
            {
                IsRaceOn = 1,
                TimestampMS = (uint)(sequence * 100),
                EngineMaxRpm = 8000,
                CurrentEngineRpm = 5000,
                CarOrdinal = 1,
                CarClass = 4,
                CarPerformanceIndex = 800,
                DrivetrainType = 1,
                NumCylinders = 6,
                Position = PointToPointPosition(frame),
                Speed = 50,
                LastLap = lastLapOverride ?? 0,
                CurrentLap = currentLap,
                CurrentRaceTime = raceTimeOverride ?? currentLap + 0.1f,
                LapNumber = lapNumberOverride ?? 0,
                RacePosition = 1,
                Accel = 200,
                Gear = 4
            };
            var normalized = new NormalizedTelemetry(180, 89.5, 200, 200 / 255d, 0, 0, 0, 0.625, default);
            module.Observe(new TelemetryFrame(sequence++, arrivalTime, TelemetrySourceKind.Simulator, raw, normalized, ReadOnlyMemory<byte>.Empty));
            arrivalTime += TimeSpan.FromMilliseconds(100);
        }

        public void SendInactive()
        {
            var raw = new Fh6RawTelemetry
            {
                IsRaceOn = 0,
                TimestampMS = (uint)(sequence * 100)
            };
            var normalized = new NormalizedTelemetry(0, 0, 0, 0, 0, 0, 0, 0, default);
            module.Observe(new TelemetryFrame(sequence++, arrivalTime, TelemetrySourceKind.Simulator, raw, normalized, ReadOnlyMemory<byte>.Empty));
            arrivalTime += TimeSpan.FromMilliseconds(100);
        }

        public void SendFreeRoam()
        {
            var raw = new Fh6RawTelemetry
            {
                IsRaceOn = 1,
                TimestampMS = (uint)(sequence * 100),
                RacePosition = 0,
                CurrentRaceTime = 0
            };
            var normalized = new NormalizedTelemetry(0, 0, 0, 0, 0, 0, 0, 0, default);
            module.Observe(new TelemetryFrame(sequence++, arrivalTime, TelemetrySourceKind.Simulator, raw, normalized, ReadOnlyMemory<byte>.Empty));
            arrivalTime += TimeSpan.FromMilliseconds(100);
        }
    }

    private sealed class CircleFrameFeed(LapAnalysisModule module)
    {
        private long sequence;
        private DateTimeOffset arrivalTime = DateTimeOffset.UnixEpoch;

        public void Drive(ushort lapNumber, int firstFrame, int lastFrame, int carClass = 4, int performanceIndex = 800)
        {
            for (var frame = firstFrame; frame <= lastFrame; frame++)
                Send(lapNumber, frame, carClass: carClass, performanceIndex: performanceIndex);
        }

        public void Send(
            ushort lapNumber,
            int lapFrame,
            Vector3F? positionOverride = null,
            float? raceTimeOverride = null,
            float? lastLapOverride = null,
            int carClass = 4,
            int performanceIndex = 800,
            float? currentLapOverride = null)
        {
            const int framesPerLap = 240;
            var currentLap = currentLapOverride ?? lapFrame / 10f;
            var raceTime = raceTimeOverride ?? lapNumber * (framesPerLap / 10f) + currentLap + 0.1f;
            var raw = new Fh6RawTelemetry
            {
                IsRaceOn = 1,
                TimestampMS = (uint)(sequence * 100),
                EngineMaxRpm = 8000,
                CurrentEngineRpm = 5000,
                CarOrdinal = 1,
                CarClass = carClass,
                CarPerformanceIndex = performanceIndex,
                DrivetrainType = 1,
                NumCylinders = 6,
                Position = positionOverride ?? Position(lapFrame),
                Speed = 40,
                LastLap = lastLapOverride ?? 0,
                CurrentLap = currentLap,
                CurrentRaceTime = raceTime,
                LapNumber = lapNumber,
                RacePosition = 1,
                Accel = 200,
                Gear = 3
            };
            var normalized = new NormalizedTelemetry(144, 89.5, 200, 200 / 255d, 0, 0, 0, 0.625, default);
            module.Observe(new TelemetryFrame(sequence++, arrivalTime, TelemetrySourceKind.Simulator, raw, normalized, ReadOnlyMemory<byte>.Empty));
            arrivalTime += TimeSpan.FromMilliseconds(100);
        }

        public void SendInactive()
        {
            var raw = new Fh6RawTelemetry
            {
                IsRaceOn = 0,
                TimestampMS = (uint)(sequence * 100)
            };
            var normalized = new NormalizedTelemetry(0, 0, 0, 0, 0, 0, 0, 0, default);
            module.Observe(new TelemetryFrame(sequence++, arrivalTime, TelemetrySourceKind.Simulator, raw, normalized, ReadOnlyMemory<byte>.Empty));
            arrivalTime += TimeSpan.FromMilliseconds(100);
        }

        public void SendFreeRoam()
        {
            var raw = new Fh6RawTelemetry
            {
                IsRaceOn = 1,
                TimestampMS = (uint)(sequence * 100),
                RacePosition = 0,
                CurrentRaceTime = 0
            };
            var normalized = new NormalizedTelemetry(0, 0, 0, 0, 0, 0, 0, 0, default);
            module.Observe(new TelemetryFrame(sequence++, arrivalTime, TelemetrySourceKind.Simulator, raw, normalized, ReadOnlyMemory<byte>.Empty));
            arrivalTime += TimeSpan.FromMilliseconds(100);
        }
    }
}
