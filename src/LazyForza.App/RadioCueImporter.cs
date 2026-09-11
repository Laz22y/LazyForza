using System.IO;
using LazyForza.Speech;
using NAudio.Wave;

namespace LazyForza.App;

internal static class RadioCueImporter
{
    public const int MaximumFileBytes = 10 * 1024 * 1024;

    public static CustomRadioCue Import(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is not (".wav" or ".mp3" or ".m4a" or ".flac"))
            throw new InvalidDataException("Unsupported radio cue file type.");
        // Hold a read-only file handle so the checked file cannot grow or change during decoding.
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length is <= 0 or > MaximumFileBytes) throw new InvalidDataException("Radio cue file exceeds 10 MiB or is empty.");
        using var reader = new StreamMediaFoundationReader(file, originName: Path.GetFileName(path));
        if (reader.WaveFormat.Channels is < 1 or > 2 || reader.TotalTime.TotalSeconds > CustomRadioCue.MaximumSeconds + .05)
            throw new InvalidDataException("Radio cue exceeds 5 seconds or two channels.");
        using var resampler = new MediaFoundationResampler(reader, new WaveFormat(CustomRadioCue.SampleRate, 16, 1));
        var pcm = new byte[CustomRadioCue.MaximumPcmBytes + 2];
        var length = 0;
        while (length < pcm.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = resampler.Read(pcm.AsSpan(length));
            if (count == 0) break;
            length += count;
        }
        if (length > CustomRadioCue.MaximumPcmBytes) throw new InvalidDataException("Radio cue exceeds 5 seconds.");
        cancellationToken.ThrowIfCancellationRequested();
        var name = Path.GetFileName(path);
        if (name.Length > 120) name = name[..120];
        return new(name, new SpeechAudio(pcm.AsSpan(0, length), CustomRadioCue.SampleRate));
    }
}
