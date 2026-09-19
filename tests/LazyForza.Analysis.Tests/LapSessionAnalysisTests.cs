using LazyForza.Domain;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LazyForza.Analysis.Tests;

[TestClass]
public sealed class LapSessionAnalysisTests
{
    [TestMethod]
    public void GroupsChronologicallyAndIncludesInvalidLapsInRecordedTimeOnly()
    {
        var first = Lap(Guid.NewGuid(), 0, 100);
        var second = first with { Id = Guid.NewGuid(), StartedAt = first.StartedAt.AddMinutes(2), TotalSeconds = 130, IsValid = false };
        var third = first with { Id = Guid.NewGuid(), StartedAt = first.StartedAt.AddMinutes(4), TotalSeconds = 99 };
        var groups = LapSessionAnalyzer.Group([third, second, first, first]);
        Assert.HasCount(1, groups);
        CollectionAssert.AreEqual(new[] { first.Id, second.Id, third.Id }, groups[0].Laps.Select(lap => lap.Id).ToArray());
        Assert.AreEqual(329, groups[0].RecordedSeconds);
        Assert.AreEqual(2, groups[0].Review.ValidLaps);
        Assert.AreEqual(99.5, groups[0].Review.MedianLapSeconds);
    }

    [TestMethod]
    public void NeverMergesDifferentEventsDriversRevisionsOrMissingIdentities()
    {
        var first = Lap(Guid.NewGuid(), 0, 100);
        LapSummary Change(int index) => first with { Id = Guid.NewGuid(), StartedAt = first.StartedAt.AddSeconds(index) };
        var laps = new[] { first, Change(1) with { SessionId = Guid.NewGuid() },
            Change(2) with { PlayerCode = "OTHER" }, Change(3) with { TrackRevision = "v2" },
            Change(4) with { SessionId = Guid.Empty }, Change(5) with { SessionId = Guid.Empty },
            Change(6) with { TrackId = Guid.NewGuid() }, Change(7) with { Direction = -1 } };
        Assert.HasCount(laps.Length, LapSessionAnalyzer.Group(laps));
    }

    private static LapSummary Lap(Guid session, int minute, double seconds) => new(
        Guid.NewGuid(), Guid.NewGuid(), 1, 2, session,
        new VehicleProfileFingerprint(1, 5, 850, 2, 8, 8000, "g", "c"),
        DateTimeOffset.UnixEpoch.AddMinutes(minute), seconds, true, null, []);
}
