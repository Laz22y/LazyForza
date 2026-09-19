using LazyForza.Speech;

namespace LazyForza.Modules.EstateRace;

public enum EngineerPriority { Information, Important, Emergency }
public sealed record EngineerMessage(string Key, string Category, string Text, EngineerPriority Priority,
    TimeSpan Cooldown, DateTimeOffset ExpiresAt)
{
    internal Func<bool>? IsCurrent { get; init; }
}

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
    private readonly Dictionary<string, string> latest = [];
    private readonly List<Broadcast> history = [];
    private EngineerPreferences preferences = new();
    private EngineerMessage? repeat;
    private long context, nextBroadcastId;
    private sealed record Broadcast(long Id, EngineerMessage Message, long Context, DateTimeOffset At, bool IsRepeat)
    {
        public EngineerDelivery Delivery { get; set; } = EngineerDelivery.Speaking;
    }
    private readonly SemaphoreSlim signal = new(0, 1);
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? playing;
    private EngineerMessage? current;
    private EngineerMessage? preview;
    private TaskCompletionSource<bool>? previewCompletion;
    private bool currentIsPreview;
    private bool currentIsRepeat;
    private string? stage;
    private bool enabled;
    private bool muted;
    private bool disposed;
    private int volume = 70;
    private string? error;
    public Task Completion { get; }
    public string? Error { get { lock (sync) return error; } }
    public bool IsPreviewing { get { lock (sync) return previewCompletion is not null || currentIsPreview; } }
    internal DateTimeOffset Now => clock();
    public IReadOnlyList<EngineerBroadcast> History
    {
        get { lock (sync) return history.Select(item => new EngineerBroadcast(item.Id, item.At, item.Message.Category,
            item.Message.Text, item.Delivery, item.IsRepeat, Validity(item))).ToArray(); }
    }
    public EngineerRepeatState RepeatState { get { lock (sync) return RepeatAvailability(); } }

    public RaceEngineer(ISpeechOutput speech, Func<DateTimeOffset>? clock = null)
    {
        this.speech = speech;
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        Completion = Task.Run(RunAsync);
    }

    public void Configure(bool enabled, bool muted, int volume, EngineerPreferences? preferences = null)
    {
        lock (sync)
        {
            if (disposed) return;
            if (enabled && !this.enabled) error = null;
            this.enabled = enabled;
            this.muted = muted;
            this.volume = Math.Clamp(volume, 0, 100);
            this.preferences = (preferences ?? this.preferences).Normalize();
            queue.RemoveAll(item => !this.preferences.Allows(item));
            if (repeat is not null && !this.preferences.AllowsCategory(repeat.Category)) repeat = null;
            if (current is not null && !currentIsPreview &&
                !(currentIsRepeat ? this.preferences.AllowsCategory(current.Category) : this.preferences.Allows(current))) playing?.Cancel();
            if (!enabled || muted || this.volume == 0) ClearPlayback();
        }
    }

    public void SetStage(string? identity)
    {
        lock (sync)
        {
            if (stage == identity) return;
            stage = identity;
            context++;
            ClearPlayback();
            seen.Clear();
            seenOrder.Clear();
            lastSpoken.Clear();
            latest.Clear();
        }
    }

    public bool Enqueue(EngineerMessage message)
    {
        lock (sync)
        {
            if (disposed || stage is null || message.ExpiresAt <= clock() || message.IsCurrent?.Invoke() == false || !seen.Add(message.Key)) return false;
            // Evidence IDs are scoped to a stage. Keep memory bounded even in very long sessions.
            seenOrder.Enqueue(message.Key);
            if (seenOrder.Count > 4096) seen.Remove(seenOrder.Dequeue());
            // New evidence invalidates old speech even while muted, filtered or inside a cooldown.
            latest[message.Category] = message.Key;
            queue.RemoveAll(item => item.ExpiresAt <= clock() || item.Category == message.Category);
            if (repeat?.Category == message.Category) repeat = null;
            if (current?.Category == message.Category) playing?.Cancel();
            if (!enabled || muted || volume == 0 || error is not null || !preferences.Allows(message)) return false;
            if (lastSpoken.TryGetValue(message.Category, out var last) && clock() - last < preferences.Cooldown(message)) return false;
            if (queue.Count >= 16)
            {
                var lowest = queue.OrderBy(item => item.Priority).First();
                if (lowest.Priority >= message.Priority) return false;
                queue.Remove(lowest);
            }
            CancelPreview(); // Real race messages always take precedence over a sample.
            repeat = null;
            queue.Add(message);
            if (currentIsRepeat || message.Priority == EngineerPriority.Emergency && current is { Priority: < EngineerPriority.Emergency })
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
                queue.Count != 0 || repeat is not null || previewCompletion is not null) return Task.FromResult(false);
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
            latest.Remove(category);
            if (repeat?.Category == category) repeat = null;
            if (current?.Category == category) playing?.Cancel();
        }
    }

    /// <summary>Repeats only the last completed transmission, without extending its original expiry.</summary>
    public bool RepeatLast()
    {
        lock (sync)
        {
            if (RepeatAvailability() != EngineerRepeatState.Ready) return false;
            repeat = history[0].Message;
            if (signal.CurrentCount == 0) signal.Release();
            return true;
        }
    }

    private EngineerRepeatState Validity(Broadcast item)
    {
        if (stage is null) return EngineerRepeatState.NoSession;
        if (item.Context != context || !latest.TryGetValue(item.Message.Category, out var key) || key != item.Message.Key)
            return EngineerRepeatState.StateChanged;
        if (item.Message.IsCurrent?.Invoke() == false) return EngineerRepeatState.StateChanged;
        if (item.Message.ExpiresAt <= clock()) return EngineerRepeatState.Expired;
        return item.Delivery == EngineerDelivery.Completed ? EngineerRepeatState.Ready : EngineerRepeatState.Incomplete;
    }

    private EngineerRepeatState RepeatAvailability()
    {
        if (history.Count == 0) return EngineerRepeatState.NoHistory;
        var validity = Validity(history[0]);
        if (validity != EngineerRepeatState.Ready) return validity;
        if (disposed || error is not null) return EngineerRepeatState.Unavailable;
        if (!enabled) return EngineerRepeatState.Disabled;
        if (muted || volume == 0) return EngineerRepeatState.Muted;
        if (!preferences.AllowsCategory(history[0].Message.Category)) return EngineerRepeatState.CategoryDisabled;
        if (current is not null || queue.Count != 0 || preview is not null || repeat is not null) return EngineerRepeatState.Busy;
        return EngineerRepeatState.Ready;
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
                    Broadcast? broadcast = null;
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
                        else if (repeat is not null)
                        {
                            message = repeat;
                            repeat = null;
                            // State may have changed after the user clicked, before the audio worker resumed.
                            if (RepeatAvailability() != EngineerRepeatState.Ready) continue;
                            broadcast = new(++nextBroadcastId, message, context, clock(), true);
                        }
                        else
                        {
                            if (queue.Count == 0 || !enabled) break;
                            message = queue.OrderByDescending(item => item.Priority).First();
                            queue.Remove(message);
                            if (!preferences.Allows(message) || message.IsCurrent?.Invoke() == false) continue;
                            broadcast = new(++nextBroadcastId, message, context, clock(), false);
                        }
                        if (broadcast is not null)
                        {
                            history.Insert(0, broadcast);
                            if (history.Count > 20) history.RemoveAt(history.Count - 1);
                        }
                        currentIsPreview = previewResult is not null;
                        currentIsRepeat = broadcast?.IsRepeat == true;
                        current = message;
                        playing = cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                        var remaining = message.ExpiresAt - clock();
                        cancellation.CancelAfter(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
                        level = volume;
                        if (previewResult is null && broadcast?.IsRepeat != true && message.Cooldown > TimeSpan.Zero) lastSpoken[message.Category] = clock();
                    }
                    var succeeded = false;
                    var validityWatch = WatchValidityAsync(message, cancellation);
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
                            if (broadcast is not null) broadcast.Delivery = cancellation.IsCancellationRequested
                                ? EngineerDelivery.Interrupted : succeeded ? EngineerDelivery.Completed : EngineerDelivery.Failed;
                            current = null; playing = null; currentIsPreview = false; currentIsRepeat = false;
                            if (previewCompletion == previewResult) previewCompletion = null;
                            previewResult?.TrySetResult(succeeded);
                        }
                        cancellation.Cancel();
                        await validityWatch.ConfigureAwait(false);
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

    private static async Task WatchValidityAsync(EngineerMessage message, CancellationTokenSource cancellation)
    {
        if (message.IsCurrent is null) return;
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                if (!message.IsCurrent()) { cancellation.Cancel(); return; }
                await Task.Delay(100, cancellation.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception) { cancellation.Cancel(); } // Missing authority state cannot authorize stale playback.
    }

    private void ClearPlayback() { queue.Clear(); repeat = null; CancelPreview(); playing?.Cancel(); }

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
public sealed class RaceEngineerObserver(RaceEngineer engineer, bool english = false, Func<EstateRaceHudState>? latestState = null)
{
    private string? stage;
    private RaceControlFlag? flag;
    private double? bestLap;
    private readonly Dictionary<Guid, EstateRacePenalty> penalties = [];
    private RaceSessionPhase? phase;
    private EstatePitStrategyDecision? decision;
    private long transition;
    private EstateRaceHudState? observed;

    public void Disconnect()
    {
        stage = null;
        engineer.SetStage(null);
    }

    public void Observe(EstateRaceHudState state)
    {
        observed = state;
        var session = state.Session;
        var driver = session?.Participants.FirstOrDefault(item => item.Id == state.LocalParticipantId);
        var identity = Identity(state);
        if (stage != identity)
        {
            stage = identity;
            engineer.SetStage(identity);
            flag = null;
            phase = null;
            bestLap = driver?.BestLapSeconds;
            penalties.Clear();
            if (driver is not null) foreach (var penalty in driver.Penalties) penalties[penalty.Id] = penalty;
            decision = null;
        }
        if (identity is null || session is null || driver is null) return;
        if (phase != session.Phase)
        {
            phase = session.Phase;
            engineer.Withdraw("flag");
        }
        if (session.Flag != flag)
        {
            engineer.Withdraw("flag");
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
        foreach (var missing in penalties.Keys.Except(driver.Penalties.Select(penalty => penalty.Id)).ToArray())
        {
            engineer.Withdraw($"penalty:{missing}");
            penalties.Remove(missing);
        }
        foreach (var penalty in driver.Penalties)
        {
            if (penalties.TryGetValue(penalty.Id, out var previousPenalty) && previousPenalty == penalty) continue;
            engineer.Withdraw($"penalty:{penalty.Id}");
            penalties[penalty.Id] = penalty;
            if (penalty.IsRevoked || penalty.IsServed) continue;
            var kind = penalty.Kind switch
            {
                RacePenaltyKind.Time => english ? $"Time penalty, {penalty.ValueSeconds:0} seconds." : $"罚时 {penalty.ValueSeconds:0} 秒。",
                RacePenaltyKind.DriveThrough => english ? "Drive through penalty." : "通过维修区处罚。",
                RacePenaltyKind.StopAndGo => english ? "Stop and go penalty." : "停车再走处罚。",
                RacePenaltyKind.Disqualification => english ? "You have been disqualified." : "你已被取消比赛资格。",
                _ => english ? "New penalty. Check race control." : "收到新处罚，请查看赛事总控。"
            };
            Say($"penalty:{penalty.Id}:{++transition}", $"penalty:{penalty.Id}", kind, EngineerPriority.Important, 0);
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

    private static string? Identity(EstateRaceHudState state) => state.IsConnected && !state.IsObserver &&
        state.Session is { } session && session.Participants.Any(item => item.Id == state.LocalParticipantId)
        ? $"{state.LocalParticipantId}:{session.StageId?.ToString() ?? $"{session.TrackId}:{session.Phase}:{session.StartsAt}:{session.PracticeSessionNumber}:{session.QualifyingSessionNumber}"}" : null;

    private void Say(string key, string category, string text, EngineerPriority priority, int cooldown)
    {
        var captured = observed!;
        engineer.Enqueue(new(key, category, text, priority, TimeSpan.FromSeconds(cooldown), engineer.Now.AddSeconds(priority == EngineerPriority.Information ? 15 : 30))
        {
            // Read the module's latest immutable snapshot again at repeat acceptance and dequeue,
            // so a UI refresh interval cannot make an old flag or penalty look current.
            IsCurrent = latestState is null ? null : () => StillCurrent(captured, latestState(), category)
        });
    }

    private static bool StillCurrent(EstateRaceHudState captured, EstateRaceHudState current, string category)
    {
        if (Identity(current) is not { } identity || identity != Identity(captured) || current.Session!.Phase != captured.Session!.Phase) return false;
        if (category == "flag") return current.Session.Flag == captured.Session.Flag;
        var before = captured.Session.Participants.First(item => item.Id == captured.LocalParticipantId);
        var after = current.Session.Participants.First(item => item.Id == current.LocalParticipantId);
        if (category.StartsWith("penalty:", StringComparison.Ordinal) && Guid.TryParse(category.AsSpan(8), out var id))
        {
            var penalty = after.Penalties.FirstOrDefault(item => item.Id == id);
            return penalty is { IsRevoked: false, IsServed: false } && penalty == before.Penalties.FirstOrDefault(item => item.Id == id);
        }
        if (category == "best") return before.BestLapSeconds == after.BestLapSeconds;
        return category != "pit" || current.Session.Flag != RaceControlFlag.Red &&
            current.PitStrategy is not null && current.PitStrategy.Decision == captured.PitStrategy?.Decision;
    }
}
