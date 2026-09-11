using LazyForza.Speech;

namespace LazyForza.Modules.EstateRace;

public enum EngineerPriority { Information, Important, Emergency }
public sealed record EngineerMessage(string Key, string Category, string Text, EngineerPriority Priority,
    TimeSpan Cooldown, DateTimeOffset ExpiresAt);

/// <summary>Bounded queue with replaceable speech output. No race or telemetry producer waits for audio.</summary>
public sealed class RaceEngineer : IDisposable
{
    private readonly object sync = new();
    private readonly ISpeechOutput speech;
    private readonly Func<DateTimeOffset> clock;
    private readonly List<EngineerMessage> queue = [];
    private readonly HashSet<string> seen = [];
    private readonly Queue<string> seenOrder = [];
    private readonly Dictionary<string, DateTimeOffset> lastSpoken = [];
    private readonly SemaphoreSlim signal = new(0, 1);
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? playing;
    private EngineerMessage? current;
    private EngineerMessage? preview;
    private TaskCompletionSource<bool>? previewCompletion;
    private bool currentIsPreview;
    private string? stage;
    private bool enabled;
    private bool muted;
    private bool disposed;
    private int volume = 70;
    private string? error;
    public Task Completion { get; }
    public string? Error { get { lock (sync) return error; } }
    public bool IsPreviewing { get { lock (sync) return previewCompletion is not null || currentIsPreview; } }

    public RaceEngineer(ISpeechOutput speech, Func<DateTimeOffset>? clock = null)
    {
        this.speech = speech;
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        Completion = Task.Run(RunAsync);
    }

    public void Configure(bool enabled, bool muted, int volume)
    {
        lock (sync)
        {
            if (disposed) return;
            if (enabled && !this.enabled) error = null;
            this.enabled = enabled;
            this.muted = muted;
            this.volume = Math.Clamp(volume, 0, 100);
            if (!enabled || muted || this.volume == 0) ClearPlayback();
        }
    }

    public void SetStage(string? identity)
    {
        lock (sync)
        {
            if (stage == identity) return;
            stage = identity;
            ClearPlayback();
            seen.Clear();
            seenOrder.Clear();
            lastSpoken.Clear();
        }
    }

    public bool Enqueue(EngineerMessage message)
    {
        lock (sync)
        {
            if (disposed || !enabled || muted || volume == 0 || error is not null || stage is null ||
                message.ExpiresAt <= clock() || !seen.Add(message.Key)) return false;
            // Evidence IDs are scoped to a stage. Keep memory bounded even in very long sessions.
            seenOrder.Enqueue(message.Key);
            if (seenOrder.Count > 4096) seen.Remove(seenOrder.Dequeue());
            if (lastSpoken.TryGetValue(message.Category, out var last) && clock() - last < message.Cooldown) return false;
            queue.RemoveAll(item => item.ExpiresAt <= clock() || item.Category == message.Category);
            if (queue.Count >= 16)
            {
                var lowest = queue.OrderBy(item => item.Priority).First();
                if (lowest.Priority >= message.Priority) return false;
                queue.Remove(lowest);
            }
            CancelPreview(); // Real race messages always take precedence over a sample.
            queue.Add(message);
            if (message.Priority == EngineerPriority.Emergency && current is { Priority: < EngineerPriority.Emergency })
                playing?.Cancel();
            if (signal.CurrentCount == 0) signal.Release();
            return true;
        }
    }

    /// <summary>Explicit one-shot preview, also available offline or with automatic speech disabled.</summary>
    public Task<bool> PreviewAsync(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        lock (sync)
        {
            if (disposed || muted || volume == 0 || error is not null || current is not null ||
                queue.Count != 0 || previewCompletion is not null) return Task.FromResult(false);
            preview = new("preview", "preview", text, EngineerPriority.Information, TimeSpan.Zero, clock().AddSeconds(30));
            previewCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            if (signal.CurrentCount == 0) signal.Release();
            return previewCompletion.Task;
        }
    }

    public void StopPreview() { lock (sync) CancelPreview(); }

    private void CancelPreview()
    {
        preview = null;
        previewCompletion?.TrySetResult(false);
        previewCompletion = null;
        if (currentIsPreview) playing?.Cancel();
    }

    public void Withdraw(string category)
    {
        lock (sync)
        {
            queue.RemoveAll(item => item.Category == category);
            if (current?.Category == category) playing?.Cancel();
        }
    }

    private async Task RunAsync()
    {
        try
        {
            while (true)
            {
                await signal.WaitAsync(lifetime.Token).ConfigureAwait(false);
                while (true)
                {
                    EngineerMessage message;
                    CancellationTokenSource cancellation;
                    int level;
                    TaskCompletionSource<bool>? previewResult;
                    lock (sync)
                    {
                        queue.RemoveAll(item => item.ExpiresAt <= clock());
                        if (muted || volume == 0 || error is not null) break;
                        previewResult = preview is null ? null : previewCompletion;
                        if (preview is not null)
                        {
                            message = preview;
                            preview = null;
                        }
                        else
                        {
                            if (queue.Count == 0 || !enabled) break;
                            message = queue.OrderByDescending(item => item.Priority).First();
                            queue.Remove(message);
                        }
                        currentIsPreview = previewResult is not null;
                        current = message;
                        playing = cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                        var remaining = message.ExpiresAt - clock();
                        cancellation.CancelAfter(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
                        level = volume;
                        if (previewResult is null && message.Cooldown > TimeSpan.Zero) lastSpoken[message.Category] = clock();
                    }
                    var succeeded = false;
                    try
                    {
                        await speech.SpeakAsync(message.Text, level, cancellation.Token).ConfigureAwait(false);
                        succeeded = !cancellation.IsCancellationRequested;
                    }
                    catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
                    catch (Exception)
                    {
                        lock (sync)
                        {
                            if (!cancellation.IsCancellationRequested) { error = "Speech output unavailable"; queue.Clear(); }
                        }
                    }
                    finally
                    {
                        lock (sync)
                        {
                            current = null; playing = null; currentIsPreview = false;
                            if (previewCompletion == previewResult) previewCompletion = null;
                            previewResult?.TrySetResult(succeeded);
                        }
                        cancellation.Dispose();
                    }
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        finally
        {
            try { await speech.DisposeAsync().ConfigureAwait(false); }
            catch (Exception) { /* Provider/device cleanup must not affect race shutdown. */ }
        }
    }

    private void ClearPlayback() { queue.Clear(); CancelPreview(); playing?.Cancel(); }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            ClearPlayback();
            lifetime.Cancel();
        }
        _ = Completion.ContinueWith(_ => { signal.Dispose(); lifetime.Dispose(); }, TaskScheduler.Default);
    }
}

/// <summary>Observes existing authority snapshots and pit predictions, never recomputes race results.</summary>
public sealed class RaceEngineerObserver(RaceEngineer engineer, bool english = false)
{
    private string? stage;
    private RaceControlFlag? flag;
    private double? bestLap;
    private readonly HashSet<Guid> penalties = [];
    private EstatePitStrategyDecision? decision;
    private long transition;

    public void Observe(EstateRaceHudState state)
    {
        var session = state.Session;
        var driver = session?.Participants.FirstOrDefault(item => item.Id == state.LocalParticipantId);
        var identity = state.IsConnected && !state.IsObserver && session is not null && driver is not null
            ? $"{state.LocalParticipantId}:{session.StageId?.ToString() ?? $"{session.TrackId}:{session.Phase}:{session.StartsAt}:{session.PracticeSessionNumber}:{session.QualifyingSessionNumber}"}" : null;
        if (stage != identity)
        {
            stage = identity;
            engineer.SetStage(identity);
            flag = null;
            bestLap = driver?.BestLapSeconds;
            penalties.Clear();
            if (driver is not null) foreach (var penalty in driver.Penalties) penalties.Add(penalty.Id);
            decision = null;
        }
        if (identity is null || session is null || driver is null) return;
        if (session.Flag != flag)
        {
            var previous = flag;
            flag = session.Flag;
            var text = session.Flag switch
            {
                RaceControlFlag.Red => english ? "Red flag. Session suspended." : "红旗，比赛暂停。",
                RaceControlFlag.Yellow => english ? "Yellow flag. Caution." : "黄旗，注意减速。",
                RaceControlFlag.Chequered => english ? "Chequered flag." : "方格旗。",
                RaceControlFlag.Green when previous is not null => english ? "Green flag." : "绿旗，恢复比赛。",
                _ => null
            };
            if (text is not null) Say($"flag:{++transition}", "flag", text,
                session.Flag == RaceControlFlag.Red ? EngineerPriority.Emergency : EngineerPriority.Important, 0);
        }
        foreach (var penalty in driver.Penalties)
        {
            if (penalty.IsRevoked || penalty.IsServed) engineer.Withdraw($"penalty:{penalty.Id}");
            if (!penalties.Add(penalty.Id) || penalty.IsRevoked || penalty.IsServed) continue;
            var kind = penalty.Kind switch
            {
                RacePenaltyKind.Time => english ? $"Time penalty, {penalty.ValueSeconds:0} seconds." : $"罚时 {penalty.ValueSeconds:0} 秒。",
                RacePenaltyKind.DriveThrough => english ? "Drive through penalty." : "通过维修区处罚。",
                RacePenaltyKind.StopAndGo => english ? "Stop and go penalty." : "停车再走处罚。",
                RacePenaltyKind.Disqualification => english ? "You have been disqualified." : "你已被取消比赛资格。",
                _ => english ? "New penalty. Check race control." : "收到新处罚，请查看赛事总控。"
            };
            Say($"penalty:{penalty.Id}", $"penalty:{penalty.Id}", kind, EngineerPriority.Important, 0);
        }
        if (session.Flag == RaceControlFlag.Red || session.Phase == RaceSessionPhase.Suspended)
        {
            engineer.Withdraw("best");
            engineer.Withdraw("pit");
            bestLap = driver.BestLapSeconds;
            decision = null;
            return;
        }
        if (driver.BestLapSeconds is double best && best > 0 && best != bestLap)
        {
            if (bestLap is double previous && best < previous)
                Say($"best:{driver.CompletedLaps}:{best:R}", "best", english ? $"Personal best, {best:0.00} seconds." : $"个人最快圈，{best:0.00} 秒。", EngineerPriority.Information, 20);
            bestLap = best;
        }
        if (state.PitStrategy is { } prediction && prediction.Decision != decision)
        {
            decision = prediction.Decision;
            engineer.Withdraw("pit");
            if (decision is EstatePitStrategyDecision.PitThisLap or EstatePitStrategyDecision.PitWindow)
                Say($"pit:{++transition}", "pit", english
                    ? prediction.Confidence == EstatePitStrategyConfidence.Low ? "Limited evidence. A pit stop may be useful. Check the prediction." : "Estimated pit opportunity. Consider pitting; this is a prediction."
                    : prediction.Confidence == EstatePitStrategyConfidence.Low ? "样本不足，可能适合进站，请核对预测。" : "预计进入进站机会，可考虑进站，结果仍有不确定性。", EngineerPriority.Information, 60);
        }
        else if (state.PitStrategy is null)
        {
            decision = null;
            engineer.Withdraw("pit");
        }
    }

    private void Say(string key, string category, string text, EngineerPriority priority, int cooldown) =>
        engineer.Enqueue(new(key, category, text, priority, TimeSpan.FromSeconds(cooldown), DateTimeOffset.UtcNow.AddSeconds(priority == EngineerPriority.Information ? 15 : 30)));
}
