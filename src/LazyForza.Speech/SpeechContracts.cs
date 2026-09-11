namespace LazyForza.Speech;

public enum SpeechProviderLocation { Local, Online }

/// <summary>Plain text only. Each provider maps voice/rate and any service-specific encoding.</summary>
public sealed record SpeechSynthesisRequest(string Text, string Language, string? VoiceId = null, double Rate = 1);

/// <summary>
/// Produces bounded PCM, never plays sound or owns the race queue. Cancellation must abort
/// work promptly. At most two calls can overlap while a cancelled call is winding down.
/// Online implementations own authentication and response decoding; credentials are not requests.
/// Dispose cancels and releases the provider's outstanding work, including requests whose caller stopped waiting.
/// </summary>
public interface ISpeechSynthesisProvider : IAsyncDisposable
{
    string Id { get; }
    SpeechProviderLocation Location { get; }
    Task<SpeechAudio> SynthesizeAsync(SpeechSynthesisRequest request, CancellationToken cancellationToken);
}

/// <summary>Owns its playback device. Cancellation stops only this playback before returning.</summary>
public interface ISpeechAudioPlayer : IAsyncDisposable
{
    Task PlayAsync(SpeechAudio audio, int volume, CancellationToken cancellationToken);
}

/// <summary>The race queue depends only on cancellable output, independent of TTS vendor or location.</summary>
public interface ISpeechOutput : IAsyncDisposable
{
    Task SpeakAsync(string text, int volume, CancellationToken cancellationToken);
}
