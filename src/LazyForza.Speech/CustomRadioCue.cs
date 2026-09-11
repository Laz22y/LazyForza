using System.Text.Json;

namespace LazyForza.Speech;

/// <summary>A bounded imported PCM copy, persisted as one atomic AppSettings value.</summary>
public sealed class CustomRadioCue
{
    public const int SampleRate = 24000;
    public const int MaximumSeconds = 5;
    public const int MaximumPcmBytes = SampleRate * 2 * MaximumSeconds;
    public string Name { get; }
    public SpeechAudio Audio { get; }

    public CustomRadioCue(string name, SpeechAudio audio)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(audio);
        if (name.Length > 120 || name.Any(char.IsControl)) throw new ArgumentException("Invalid cue name.", nameof(name));
        if (audio.SampleRate != SampleRate || audio.Channels != 1 || audio.Samples.Length > MaximumPcmBytes)
            throw new ArgumentException("Custom cue must be 24 kHz mono PCM and at most 5 seconds.", nameof(audio));
        Name = name;
        Audio = audio;
    }

    public string Serialize() => JsonSerializer.Serialize(new StoredCue(1, Name, Convert.ToBase64String(Audio.Samples.Span)));

    public static bool TryDeserialize(string? value, out CustomRadioCue? cue)
    {
        cue = null;
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumPcmBytes * 4 / 3 + 2048) return false;
        try
        {
            var stored = JsonSerializer.Deserialize<StoredCue>(value);
            if (stored is null || stored.Version != 1 || stored.Pcm16 is null || stored.Pcm16.Length > MaximumPcmBytes * 4 / 3) return false;
            cue = new(stored.Name, new SpeechAudio(Convert.FromBase64String(stored.Pcm16), SampleRate));
            return true;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or FormatException) { return false; }
    }

    private sealed record StoredCue(int Version, string Name, string Pcm16);
}
