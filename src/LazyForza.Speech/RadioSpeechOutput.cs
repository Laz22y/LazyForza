namespace LazyForza.Speech;

/// <summary>Vendor-independent synthesis, bounded memory cache and a cancellable radio transmission.</summary>
public sealed class RadioSpeechOutput : ISpeechOutput
{
    private readonly ISpeechSynthesisProvider provider;
    private readonly ISpeechAudioPlayer player;
    private readonly SpeechSynthesisRequest settings;
    private readonly TimeSpan synthesisTimeout;
    private readonly Func<RadioTransmission> transmissionSettings;
    private readonly Func<TimeSpan, CancellationToken, Task> pause;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim transmissions = new(1, 1);
    private readonly object sync = new();
    private readonly Dictionary<string, SpeechAudio> cache = [];
    private readonly Queue<string> cacheOrder = [];
    private int cacheBytes;
    private int synthesizing;
    private Task? disposal;
    private const int MaximumCacheBytes = 4 * 1024 * 1024;
    private const int MaximumCachedPhrases = 32;

    public RadioSpeechOutput(ISpeechSynthesisProvider provider, ISpeechAudioPlayer player,
        string language, string? voiceId = null, double rate = 1, TimeSpan? synthesisTimeout = null,
        Func<RadioTransmission>? transmissionSettings = null,
        Func<TimeSpan, CancellationToken, Task>? pause = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(player);
        ArgumentException.ThrowIfNullOrWhiteSpace(language);
        if (!double.IsFinite(rate) || rate is < .5 or > 2) throw new ArgumentOutOfRangeException(nameof(rate));
        this.provider = provider;
        this.player = player;
        this.transmissionSettings = transmissionSettings ?? (() => RadioTransmission.Default);
        this.pause = pause ?? Task.Delay;
        settings = new("", language, voiceId, rate);
        this.synthesisTimeout = synthesisTimeout ?? TimeSpan.FromSeconds(10);
        if (this.synthesisTimeout <= TimeSpan.Zero || this.synthesisTimeout > TimeSpan.FromSeconds(30))
            throw new ArgumentOutOfRangeException(nameof(synthesisTimeout));
    }

    public async Task SpeakAsync(string text, int volume, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        if (text.Length > 512) throw new ArgumentException("Speech text exceeds 512 characters.", nameof(text));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        var token = cancellation.Token;
        token.ThrowIfCancellationRequested();
        if (volume <= 0) return;
        await transmissions.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var transmission = transmissionSettings();
            transmission.Validate();
            SpeechAudio? audio;
            lock (sync) cache.TryGetValue(text, out audio);
            if (audio is null)
            {
                var synthesis = CancellationTokenSource.CreateLinkedTokenSource(token);
                synthesis.CancelAfter(synthesisTimeout);
                var synthesisToken = synthesis.Token;
                // Bound overlap to one cancelled request and its replacement, even if a provider ignores cancellation.
                if (Interlocked.Increment(ref synthesizing) > 2)
                {
                    Interlocked.Decrement(ref synthesizing);
                    synthesis.Dispose();
                    throw new InvalidOperationException("Speech provider is still cancelling previous requests.");
                }
                var work = Task.Run(() => SynthesizeAsync(settings with { Text = text }, synthesis));
                _ = work.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                try { audio = await work.WaitAsync(synthesisToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (synthesisToken.IsCancellationRequested && !token.IsCancellationRequested)
                {
                    throw new TimeoutException("Speech synthesis timed out.");
                }
                token.ThrowIfCancellationRequested();
                lock (sync)
                {
                    while (cacheOrder.Count > 0 && (cacheOrder.Count >= MaximumCachedPhrases ||
                           cacheBytes + audio.Samples.Length > MaximumCacheBytes))
                    {
                        var oldest = cacheOrder.Dequeue();
                        cacheBytes -= cache[oldest].Samples.Length;
                        cache.Remove(oldest);
                    }
                    if (audio.Samples.Length <= MaximumCacheBytes)
                    {
                        cache.Add(text, audio);
                        cacheOrder.Enqueue(text);
                        cacheBytes += audio.Samples.Length;
                    }
                }
            }
            // Open the radio only after synthesis succeeds, never during a service/model startup wait.
            token.ThrowIfCancellationRequested();
            await player.PlayAsync(transmission.Connect, volume, token).ConfigureAwait(false);
            await pause(transmission.AfterConnect, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            await player.PlayAsync(audio, volume, token).ConfigureAwait(false);
            await pause(transmission.BeforeDisconnect, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            await player.PlayAsync(transmission.Disconnect, volume, token).ConfigureAwait(false);
        }
        finally { transmissions.Release(); }
    }

    private async Task<SpeechAudio> SynthesizeAsync(SpeechSynthesisRequest request, CancellationTokenSource cancellation)
    {
        try
        {
            cancellation.Token.ThrowIfCancellationRequested();
            return await provider.SynthesizeAsync(request, cancellation.Token).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Speech provider returned no audio.");
        }
        finally { cancellation.Dispose(); Interlocked.Decrement(ref synthesizing); }
    }

    public ValueTask DisposeAsync()
    {
        lock (sync) return new ValueTask(disposal ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        await lifetime.CancelAsync().ConfigureAwait(false);
        await transmissions.WaitAsync().ConfigureAwait(false);
        try
        {
            try { await player.DisposeAsync().ConfigureAwait(false); }
            finally { await provider.DisposeAsync().ConfigureAwait(false); }
        }
        finally
        {
            lock (sync) { cache.Clear(); cacheOrder.Clear(); cacheBytes = 0; }
            transmissions.Release();
        }
    }
}
