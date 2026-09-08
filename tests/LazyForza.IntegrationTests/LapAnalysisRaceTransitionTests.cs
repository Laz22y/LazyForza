using LazyForza.Analysis;
using LazyForza.Domain;
using LazyForza.Modules.LapAnalysis;
using LazyForza.Storage;

namespace LazyForza.IntegrationTests;

public sealed partial class LapAnalysisBehaviorTests
{
    [TestMethod]
    [DataRow(112d, 20)]
    [DataRow(180d, 100)]
    public void LegendIslandRearGridKeepsBothSharedRoutesUntilTheyDiverge(double rearOffset, int intervalMilliseconds)
    {
        WithOfficialTracks((store, tracks) =>
        {
            var expected = tracks["传奇岛径道赛"];
            var start = expected.Points[0];
            // The September 3 release recording started 112.3 m behind this route.
            var grid = rearOffset == 112
                ? start with { X = 4203.3394, Y = 166.97974, Z = -5592.32 }
                : start with { X = start.X - start.TangentX * rearOffset, Z = start.Z - start.TangentZ * rearOffset };
            var module = new LapAnalysisModule(store, TelemetrySourceKind.Live);
            long sequence = 0;
            var arrival = DateTimeOffset.UnixEpoch;
            SendLiveTrackFrame(module, grid, ref sequence, ref arrival);
            Assert.IsFalse(module.MatchDiagnostics.EliminatedCandidates.Any(c => c.TrackId == expected.Id),
                DescribeMatchDiagnostics(module));
            Assert.IsTrue(module.MatchDiagnostics.CoarseEligibleRoutes >= 2);
            for (var offset = rearOffset - 5; offset > 0; offset -= 5)
                SendLiveTrackFrame(module, start with
                {
                    X = start.X - start.TangentX * offset,
                    Z = start.Z - start.TangentZ * offset
                }, ref sequence, ref arrival);
            foreach (var point in expected.Points.TakeWhile(p => p.S <= 1_600))
            {
                SendLiveTrackFrame(module, point, ref sequence, ref arrival);
                arrival += TimeSpan.FromMilliseconds(intervalMilliseconds - 20);
                Assert.IsTrue(module.CurrentTrack is null || module.CurrentTrack.Id == expected.Id,
                    $"Rear-grid evidence must never lock Goliath. {DescribeMatchDiagnostics(module)}");
                if (point.S <= 1_000) Assert.IsNull(module.CurrentTrack, "Shared geometry cannot identify the event.");
            }
            Assert.AreEqual(expected.Id, module.CurrentTrack?.Id, DescribeMatchDiagnostics(module));
        });
    }

    [TestMethod]
    [DataRow(120d, 0d)]
    [DataRow(0d, 40d)]
    public void RearGridAllowanceDoesNotAdmitDistantLateralOrElevatedRoutes(double lateral, double height)
    {
        WithOfficialTracks((store, tracks) =>
        {
            var expected = tracks["传奇岛径道赛"];
            var start = expected.Points[0];
            var module = new LapAnalysisModule(store, TelemetrySourceKind.Live);
            long sequence = 0;
            var arrival = DateTimeOffset.UnixEpoch;
            SendLiveTrackFrame(module, start with
            {
                X = start.X - start.TangentX * 150 + start.TangentZ * lateral,
                Y = start.Y + height,
                Z = start.Z - start.TangentZ * 150 - start.TangentX * lateral
            }, ref sequence, ref arrival);
            Assert.IsFalse(module.MatchDiagnostics.TopCandidates.Any(c => c.TrackId == expected.Id),
                DescribeMatchDiagnostics(module));
            Assert.IsNull(module.CurrentTrack);
        });
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void NewGridAndResetClockReplaceWrongTrackWithoutACompletedLap(bool menuFrames)
    {
        WithOfficialTracks((store, tracks) =>
        {
            var module = new LapAnalysisModule(store, TelemetrySourceKind.Live);
            var first = tracks["歌利亚"];
            var next = tracks["电器街环道赛"];
            long sequence = 0;
            var arrival = DateTimeOffset.UnixEpoch;
            foreach (var point in first.Points.TakeWhile(p => p.S <= 1_600))
                SendLiveTrackFrame(module, point, ref sequence, ref arrival);
            Assert.AreEqual(first.Id, module.CurrentTrack?.Id, DescribeMatchDiagnostics(module));
            var session = module.CurrentSessionId;
            // Last active position/time in the September 3 capture: no LastLap or BestLap.
            SendTransitionFrame(module, new Vector3F(4615.5693f, 109.943665f, -3813.3103f), 115.863f, 85, ref sequence, ref arrival);
            Assert.HasCount(0, module.CurrentSessionLaps);
            if (menuFrames) SendTransitionFrame(module, default, 0, 0, ref sequence, ref arrival, active: false);
            var start = next.Points[0];
            SendTransitionFrame(module, new Vector3F((float)start.X, (float)start.Y, (float)start.Z), .06f, 0,
                ref sequence, ref arrival);
            Assert.AreNotEqual(session, module.CurrentSessionId,
                "A reset clock at a different starting grid must not depend on the old route having a completed lap.");
            foreach (var point in next.Points.TakeWhile(p => p.S <= 1_150))
                SendLiveTrackFrame(module, point, ref sequence, ref arrival);
            Assert.AreEqual(next.Id, module.CurrentTrack?.Id, DescribeMatchDiagnostics(module));
            Assert.HasCount(0, module.CurrentSessionLaps, "The partial old route must not become a lap in the new race.");
        });
    }

    [TestMethod]
    public void RewindToOriginalGridWithoutResultsPreservesSession()
    {
        WithOfficialTracks((store, tracks) =>
        {
            var module = new LapAnalysisModule(store, TelemetrySourceKind.Live);
            var route = tracks["传奇岛径道赛"];
            long sequence = 0;
            var arrival = DateTimeOffset.UnixEpoch;
            foreach (var point in route.Points.TakeWhile(p => p.S <= 1_150))
                SendLiveTrackFrame(module, point, ref sequence, ref arrival);
            var session = module.CurrentSessionId;
            var far = route.Points.First(p => p.S >= 1_000);
            SendTransitionFrame(module, new Vector3F((float)far.X, (float)far.Y, (float)far.Z), 25, 40, ref sequence, ref arrival);
            SendTransitionFrame(module, default, 0, 0, ref sequence, ref arrival, active: false);
            var start = route.Points[0];
            SendTransitionFrame(module, new Vector3F((float)start.X, (float)start.Y, (float)start.Z), .06f, 0,
                ref sequence, ref arrival);
            Assert.AreEqual(session, module.CurrentSessionId);
            Assert.HasCount(0, module.VisibleLaps);
        });
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void GridResetRequiresKnownStartButDoesNotRequireOldTrackIdentification(bool knownStart)
    {
        WithOfficialTracks((store, tracks) =>
        {
            var module = new LapAnalysisModule(store, TelemetrySourceKind.Live);
            long sequence = 0;
            var arrival = DateTimeOffset.UnixEpoch;
            SendTransitionFrame(module, new Vector3F(-30_000, 0, -30_000), .06f, 0, ref sequence, ref arrival);
            var session = module.CurrentSessionId;
            SendTransitionFrame(module, new Vector3F(-30_000, 0, -29_000), 30, 40, ref sequence, ref arrival);
            SendTransitionFrame(module, default, 0, 0, ref sequence, ref arrival, active: false);
            var start = tracks["电器街环道赛"].Points[0];
            var nextPosition = knownStart
                ? new Vector3F((float)start.X, (float)start.Y, (float)start.Z)
                : new Vector3F(-40_000, 0, -40_000);
            SendTransitionFrame(module, nextPosition, .06f, 0, ref sequence, ref arrival);
            if (knownStart) Assert.AreNotEqual(session, module.CurrentSessionId);
            else Assert.AreEqual(session, module.CurrentSessionId, "A timer reset and arbitrary teleport alone are not a new event.");
            Assert.HasCount(0, module.VisibleLaps);
        });
    }

    private static void WithOfficialTracks(Action<LazyForzaStore, Dictionary<string, TrackTemplate>> action)
    {
        var path = Path.Combine(Path.GetTempPath(), $"lazyforza-race-transition-{Guid.NewGuid():N}.db");
        try
        {
            using var store = new LazyForzaStore(path);
            PlaygroundOfficialTrackCatalog.EnsureImported(store);
            var tracks = store.ListTracks("fh6_udp_live").Select(t => store.LoadTrack(t.Id)!.Value.Track)
                .ToDictionary(t => t.Name, StringComparer.Ordinal);
            action(store, tracks);
        }
        finally { DeleteDatabase(path); }
    }

    private static void SendTransitionFrame(LapAnalysisModule module, Vector3F position, float raceTime, float speed,
        ref long sequence, ref DateTimeOffset arrival, bool active = true)
    {
        var raw = new Fh6RawTelemetry
        {
            IsRaceOn = active ? 1 : 0, Position = position, Speed = speed,
            CurrentLap = raceTime, CurrentRaceTime = raceTime, RacePosition = active ? (byte)12 : (byte)0,
            CarOrdinal = 1, CarClass = 4, CarPerformanceIndex = 800, EngineMaxRpm = 8_000, CurrentEngineRpm = 5_000
        };
        var normalized = new NormalizedTelemetry(speed * 3.6, 89.5, 0, 0, 0, 0, 0, .625, default);
        module.Observe(new TelemetryFrame(sequence++, arrival, TelemetrySourceKind.Live, raw, normalized, ReadOnlyMemory<byte>.Empty));
        arrival += TimeSpan.FromMilliseconds(20);
    }
}
