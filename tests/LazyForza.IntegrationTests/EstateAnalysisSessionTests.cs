using LazyForza.Domain;
using LazyForza.Modules.EstateRace;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LazyForza.IntegrationTests;

[TestClass]
public sealed class EstateAnalysisSessionTests
{
    [TestMethod]
    public void ServerStageIdentitySurvivesReconnectPauseAndSeparatesDriverAndStage()
    {
        var session = System.Text.Json.JsonSerializer.Deserialize<EstateRaceSession>("{}")! with
        { StageId = Guid.NewGuid(), EventId = Guid.NewGuid(), Phase = RaceSessionPhase.Race, SessionName = "Race" };
        var driver = Guid.NewGuid();
        var first = EstateAnalysisSession.Resolve(session, driver, null, true)!;
        Assert.AreEqual(LapSessionKind.EstateRace, first.Kind);
        Assert.AreEqual(first.Id, EstateAnalysisSession.Resolve(session, driver, null, true)!.Id);
        Assert.AreEqual(first.Id, EstateAnalysisSession.Resolve(session with
        { Phase = RaceSessionPhase.Suspended, SuspendedFromPhase = RaceSessionPhase.Race }, driver, first, true)!.Id);
        Assert.AreNotEqual(first.Id, EstateAnalysisSession.Resolve(session, Guid.NewGuid(), first, false)!.Id);
        Assert.AreNotEqual(first.Id, EstateAnalysisSession.Resolve(session with { StageId = Guid.NewGuid() }, driver, first, true)!.Id);
        Assert.AreNotEqual(first.Id, EstateAnalysisSession.Resolve(session with { Phase = RaceSessionPhase.Practice }, driver, first, true)!.Id);
        var legacy = session with { StageId = null };
        var local = EstateAnalysisSession.Resolve(legacy, driver, null, true)!;
        Assert.AreEqual(local.Id, EstateAnalysisSession.Resolve(legacy, driver, local, false)!.Id);
        Assert.AreNotEqual(local.Id, EstateAnalysisSession.Resolve(legacy, driver, local, true)!.Id);
    }
}
