using System.Collections.Concurrent;
using System.Threading.Channels;
using LazyForza.Modules.EstateRace;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LazyForza.IntegrationTests;

[TestClass]
public sealed class EstateRaceNetworkFlowTests
{
    [TestMethod]
    public void TelemetryCatchUpKeepsOnlyTheLatestShortContinuityWindow()
    {
        var frames = Channel.CreateUnbounded<int>();
        for (var value = 1; value <= 20; value++)
            Assert.IsTrue(frames.Writer.TryWrite(value));
        Assert.IsTrue(frames.Reader.TryRead(out var first));

        var latest = LatestTelemetryBatch.Drain(first, frames.Reader);

        CollectionAssert.AreEqual(new[] { 17, 18, 19, 20 }, latest.ToArray());
        Assert.AreEqual(4, LatestTelemetryBatch.MaximumFrames,
            "地产赛事积压时只保留短连续窗口，不能补算大批过期遥测拖慢整个客户端。");
    }

    [TestMethod]
    public async Task TelemetryQueueKeepsOnlyLatestValueWhileSendIsBlocked()
    {
        var queue = new LatestValueSendQueue<int>();
        var sent = new ConcurrentQueue<int>();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var run = queue.RunAsync(async (value, token) =>
        {
            sent.Enqueue(value);
            if (value == 1)
            {
                firstStarted.TrySetResult();
                await releaseFirst.Task.WaitAsync(token);
            }
            else
            {
                secondSent.TrySetResult();
            }
        }, cancellation.Token);

        Assert.IsTrue(queue.TryWrite(1));
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsTrue(queue.TryWrite(2));
        Assert.IsTrue(queue.TryWrite(3));
        releaseFirst.TrySetResult();
        await secondSent.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () => await run);

        CollectionAssert.AreEqual(new[] { 1, 3 }, sent.ToArray());
    }

    [TestMethod]
    public void ReconnectDelayContinuesBackingOffAcrossShortConnections()
    {
        var delays = Enumerable.Range(1, 7)
            .Select(attempt => EstateRaceModule.ReconnectDelay(attempt, 1))
            .ToArray();

        CollectionAssert.AreEqual(
            new[] { 0.5, 1d, 2d, 4d, 8d, 10d, 10d },
            delays.Select(value => value.TotalSeconds).ToArray());
        Assert.AreEqual(0.425, EstateRaceModule.ReconnectDelay(1, 0).TotalSeconds, 0.001);
        Assert.AreEqual(11.5, EstateRaceModule.ReconnectDelay(20, 2).TotalSeconds, 0.001);
    }
}
