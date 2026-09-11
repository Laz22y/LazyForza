using System.Buffers.Binary;

namespace LazyForza.Speech;

/// <summary>Owned, interleaved little-endian PCM16; providers decode compressed responses before returning.</summary>
public sealed class SpeechAudio
{
    public const int MaximumDurationSeconds = 30;
    private readonly byte[] samples;
    public int SampleRate { get; }
    public int Channels { get; }
    public ReadOnlyMemory<byte> Samples => samples;
    public TimeSpan Duration => TimeSpan.FromSeconds((double)samples.Length / (SampleRate * Channels * 2));

    public SpeechAudio(ReadOnlySpan<byte> pcm16, int sampleRate, int channels = 1)
    {
        if (sampleRate is < 8000 or > 48000) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (channels is < 1 or > 2) throw new ArgumentOutOfRangeException(nameof(channels));
        if (pcm16.Length == 0 || pcm16.Length % (channels * 2) != 0 ||
            pcm16.Length > (long)sampleRate * channels * 2 * MaximumDurationSeconds)
            throw new ArgumentException("Speech PCM is empty, misaligned or longer than 30 seconds.", nameof(pcm16));
        SampleRate = sampleRate;
        Channels = channels;
        samples = pcm16.ToArray();
    }

    public byte[] CopySamples(int volume = 100)
    {
        var result = new byte[samples.Length];
        var gain = Math.Clamp(volume, 0, 100) / 100d;
        for (var offset = 0; offset < samples.Length; offset += 2)
            BinaryPrimitives.WriteInt16LittleEndian(result.AsSpan(offset),
                (short)Math.Round(BinaryPrimitives.ReadInt16LittleEndian(samples.AsSpan(offset)) * gain));
        return result;
    }

    public byte[] ToWave(int volume = 100)
    {
        using var stream = new MemoryStream(44 + samples.Length);
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8); writer.Write(36 + samples.Length); writer.Write("WAVEfmt "u8);
        writer.Write(16); writer.Write((short)1); writer.Write((short)Channels); writer.Write(SampleRate);
        writer.Write(SampleRate * Channels * 2); writer.Write((short)(Channels * 2)); writer.Write((short)16);
        writer.Write("data"u8); writer.Write(samples.Length); writer.Write(CopySamples(volume));
        return stream.ToArray();
    }
}
