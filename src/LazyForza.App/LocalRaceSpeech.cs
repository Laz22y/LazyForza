using System.IO;
using System.Runtime.InteropServices;
using LazyForza.Modules.EstateRace;

namespace LazyForza.App;

/// <summary>Windows SAPI offline voice; all COM calls stay on one STA thread per transmission.</summary>
internal sealed class LocalRaceSpeech(bool english) : ILocalRaceSpeech
{
    public Task SpeakAsync(string text, int volume, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            object? instance = null;
            object? voices = null;
            object? selected = null;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var type = Type.GetTypeFromProgID("SAPI.SpVoice") ?? throw new InvalidOperationException("Local SAPI voice unavailable.");
                instance = Activator.CreateInstance(type) ?? throw new InvalidOperationException("Local SAPI voice unavailable.");
                dynamic voice = instance;
                voices = voice.GetVoices(english ? "Language=409" : "Language=804", "");
                dynamic installed = voices;
                if (installed.Count == 0) throw new InvalidOperationException("No local voice for the selected language.");
                selected = installed.Item(0);
                voice.Voice = selected;
                voice.Volume = Math.Clamp(volume, 0, 100);
                PlayCue(volume, false, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                voice.Speak(text, 1 | 16); // asynchronous, plain text (never interpret uploaded names as XML)
                while (!voice.WaitUntilDone(20))
                {
                    if (!cancellationToken.IsCancellationRequested) continue;
                    voice.Speak("", 1 | 2); // purge immediately; never play an end cue after mute
                    cancellationToken.ThrowIfCancellationRequested();
                }
                cancellationToken.ThrowIfCancellationRequested();
                PlayCue(volume, true, cancellationToken);
                completion.TrySetResult();
            }
            catch (OperationCanceledException) { completion.TrySetCanceled(cancellationToken); }
            catch (Exception exception) { completion.TrySetException(exception); }
            finally
            {
                foreach (var value in new[] { selected, voices, instance })
                {
                    try { if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value); }
                    catch (Exception) { /* Audio cleanup must never terminate the application. */ }
                }
            }
        }) { IsBackground = true, Name = "LazyForza local race radio" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static void PlayCue(int volume, bool ending, CancellationToken cancellation)
    {
        var handle = GCHandle.Alloc(CreateRadioCue(volume, ending), GCHandleType.Pinned);
        try
        {
            cancellation.ThrowIfCancellationRequested();
            if (!PlaySound(handle.AddrOfPinnedObject(), IntPtr.Zero, 0x0001 | 0x0004 | 0x0002))
                throw new InvalidOperationException("Radio audio unavailable.");
            if (cancellation.WaitHandle.WaitOne(150)) cancellation.ThrowIfCancellationRequested();
        }
        finally { PlaySound(IntPtr.Zero, IntPtr.Zero, 0); handle.Free(); }
    }

    internal static byte[] CreateRadioCue(int volume, bool ending)
    {
        // Original short two-tone radio cue, synthesized locally; no broadcast recording/assets.
        const int rate = 22050;
        const int milliseconds = 120;
        var count = rate * milliseconds / 1000;
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8); writer.Write(36 + count * 2); writer.Write("WAVEfmt "u8);
        writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(rate);
        writer.Write(rate * 2); writer.Write((short)2); writer.Write((short)16);
        writer.Write("data"u8); writer.Write(count * 2);
        for (var i = 0; i < count; i++)
        {
            var frequency = (i < count / 2) ^ ending ? 1300 : 1800;
            var edge = Math.Min(1, Math.Min(i, count - 1 - i) / 100d);
            writer.Write((short)(Math.Sin(2 * Math.PI * frequency * i / rate) * 4500 * Math.Clamp(volume, 0, 100) / 100 * edge));
        }
        return stream.ToArray();
    }

    [DllImport("winmm.dll", EntryPoint = "PlaySoundW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PlaySound(IntPtr sound, IntPtr module, uint flags);
}
