using LazyForza.Speech;

namespace LazyForza.App;

/// <summary>The default composition remains local; future providers share the radio/player pipeline.</summary>
internal sealed class LocalRaceSpeech(bool english, Func<RadioTransmission>? transmissionSettings = null,
    WindowsSpeechVoice? voice = null) : ISpeechOutput
{
    private readonly RadioSpeechOutput output = new(new WindowsSapiSpeechProvider(),
        new WindowsPcmAudioPlayer(), voice?.Language ?? (english ? "en-US" : "zh-CN"), voice?.Id,
        transmissionSettings: transmissionSettings);

    public Task SpeakAsync(string text, int volume, CancellationToken cancellationToken) =>
        output.SpeakAsync(text, volume, cancellationToken);

    public ValueTask DisposeAsync() => output.DisposeAsync();
}
