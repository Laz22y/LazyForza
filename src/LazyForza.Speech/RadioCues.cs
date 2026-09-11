using System.Buffers.Binary;

namespace LazyForza.Speech;

/// <summary>Original procedural radio sound design. No broadcast samples or reconstructed broadcast melody.</summary>
public static class RadioCues
{
    public static SpeechAudio Connect { get; } = Create(false);
    public static SpeechAudio Disconnect { get; } = Create(true);

    private static SpeechAudio Create(bool ending)
    {
        const int rate = 24000;
        var duration = ending ? .155 : .205;
        var samples = new byte[(int)(rate * duration) * 2];
        uint noiseState = ending ? 0xA341316Cu : 0xC8013EA4u;
        double previousNoise = 0, filteredNoise = 0;
        for (var i = 0; i < samples.Length / 2; i++)
        {
            var t = (double)i / rate;
            // Independent, unequal pulses and a very short squelch tail distinguish the two directions.
            var tone = ending
                ? Pulse(t, .012, .041, 2030, -85) + Pulse(t, .067, .057, 1420, -65)
                : Pulse(t, .018, .076, 1540, 65) + Pulse(t, .112, .055, 2140, 35);
            noiseState ^= noiseState << 13; noiseState ^= noiseState >> 17; noiseState ^= noiseState << 5;
            var noise = noiseState / (double)uint.MaxValue * 2 - 1;
            // A small high-passed noise burst suggests a radio gate opening/closing, without a long hiss.
            filteredNoise = .58 * (filteredNoise + noise - previousNoise);
            previousNoise = noise;
            var squelch = Envelope(t, .004, .023, .003) * .026 +
                          Envelope(t, ending ? .127 : .175, .022, .004) * .038;
            var signal = tone * .17 + filteredNoise * squelch;
            BinaryPrimitives.WriteInt16LittleEndian(samples.AsSpan(i * 2),
                (short)Math.Round(Math.Clamp(signal, -.3, .3) * short.MaxValue));
        }
        return new SpeechAudio(samples, rate);
    }

    private static double Pulse(double time, double start, double length, double frequency, double glide)
    {
        var local = time - start;
        var envelope = Envelope(time, start, length, .004);
        if (envelope == 0) return 0;
        var phase = 2 * Math.PI * (frequency * local + .5 * glide / length * local * local);
        var carrier = Math.Sin(phase) + .12 * Math.Sin(2 * phase) + .035 * Math.Sin(3 * phase);
        return carrier * envelope * (.94 + .06 * Math.Sin(2 * Math.PI * 83 * local));
    }

    private static double Envelope(double time, double start, double length, double fade)
    {
        var local = time - start;
        if (local <= 0 || local >= length) return 0;
        var edge = Math.Min(1, Math.Min(local, length - local) / fade);
        return .5 - .5 * Math.Cos(Math.PI * edge);
    }
}
