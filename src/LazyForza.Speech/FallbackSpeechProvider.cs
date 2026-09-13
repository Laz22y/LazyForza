namespace LazyForza.Speech;

/// <summary>Online failures cool down before another paid request; cancellation never starts fallback work.</summary>
public sealed class FallbackSpeechProvider(ISpeechSynthesisProvider primary, ISpeechSynthesisProvider fallback,
    Func<DateTimeOffset>? clock = null) : ISpeechSynthesisProvider
{
    private readonly Func<DateTimeOffset> clock = clock ?? (() => DateTimeOffset.UtcNow);
    private readonly object sync = new();
    private DateTimeOffset retryAt;
    private SpeechServiceFailure? failure;
    public SpeechServiceFailure? LastFailure { get { lock (sync) return failure; } }
    public string Id => primary.Id;
    public SpeechProviderLocation Location => primary.Location;

    public async Task<SpeechAudio> SynthesizeAsync(SpeechSynthesisRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        bool tryPrimary;
        lock (sync) tryPrimary = clock() >= retryAt;
        if (tryPrimary)
        {
            try
            {
                var result = await primary.SynthesizeAsync(request, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                lock (sync) { failure = null; retryAt = default; }
                return result;
            }
            catch (SpeechServiceException error)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var minimum = error.Failure is SpeechServiceFailure.Authentication or SpeechServiceFailure.Configuration ? 300 : 60;
                lock (sync)
                {
                    failure = error.Failure;
                    retryAt = clock().AddSeconds(Math.Clamp(error.RetryAfter?.TotalSeconds ?? minimum, minimum, 3600));
                }
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        // The local voice is selected by language, never by the online service's voice identifier.
        var audio = await fallback.SynthesizeAsync(request with { VoiceId = null }, cancellationToken).ConfigureAwait(false);
        return new SpeechAudio(audio.Samples.Span, audio.SampleRate, audio.Channels, cacheable: false);
    }

    public async ValueTask DisposeAsync()
    {
        try { await primary.DisposeAsync().ConfigureAwait(false); }
        finally { await fallback.DisposeAsync().ConfigureAwait(false); }
    }
}
