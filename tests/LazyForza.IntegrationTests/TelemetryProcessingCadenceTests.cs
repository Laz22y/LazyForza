using LazyForza.Modules.Abstractions;

namespace LazyForza.IntegrationTests;

[TestClass]
public sealed class TelemetryProcessingCadenceTests
{
    [TestMethod]
    public void EightyHertzInputPassesThroughUnchanged()
    {
        var cadence = new TelemetryProcessingCadence(
            TelemetryProcessingCadence.HighRateMinimumInterval);
        var startedAt = DateTimeOffset.Parse("2026-08-25T00:00:00Z");

        var accepted = Enumerable.Range(0, 80)
            .Count(index => cadence.ShouldProcess(startedAt + TimeSpan.FromSeconds(index / 80d)));

        Assert.AreEqual(80, accepted);
    }

    [TestMethod]
    public void NinetyNineHertzInputIsCoalescedWithoutAccumulatingDelay()
    {
        var cadence = new TelemetryProcessingCadence(
            TelemetryProcessingCadence.HighRateMinimumInterval);
        var startedAt = DateTimeOffset.Parse("2026-08-25T00:00:00Z");

        var accepted = Enumerable.Range(0, 99)
            .Select(index => startedAt + TimeSpan.FromSeconds(index / 99d))
            .Where(arrival => cadence.ShouldProcess(arrival))
            .ToArray();

        Assert.IsTrue(accepted.Length is >= 49 and <= 50);
        Assert.IsTrue(accepted[^1] >= startedAt + TimeSpan.FromMilliseconds(970));
    }

    [TestMethod]
    public void CriticalFrameBypassesCadenceAndResetRearmsNextSample()
    {
        var cadence = new TelemetryProcessingCadence(TimeSpan.FromMilliseconds(20));
        var startedAt = DateTimeOffset.Parse("2026-08-25T00:00:00Z");

        Assert.IsTrue(cadence.ShouldProcess(startedAt));
        Assert.IsFalse(cadence.ShouldProcess(startedAt + TimeSpan.FromMilliseconds(5)));
        Assert.IsTrue(cadence.ShouldProcess(startedAt + TimeSpan.FromMilliseconds(6), force: true));
        Assert.IsFalse(cadence.ShouldProcess(startedAt + TimeSpan.FromMilliseconds(10)));

        cadence.Reset();
        Assert.IsTrue(cadence.ShouldProcess(startedAt + TimeSpan.FromMilliseconds(11)));
    }
}
