using System.Runtime.InteropServices;
using LazyForza.Speech;

namespace LazyForza.App;

/// <summary>Owns a reusable waveOut device; cancellation never stops unrelated application sounds.</summary>
internal sealed class WindowsPcmAudioPlayer : ISpeechAudioPlayer
{
    private readonly SemaphoreSlim playback = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly object sync = new();
    private Task? disposal;
    private IntPtr device;
    private EventWaitHandle? completed;
    private bool callbackRetained;
    private bool poisoned;
    private int sampleRate, channels;

    public async Task PlayAsync(SpeechAudio audio, int volume, CancellationToken cancellationToken)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        var token = cancellation.Token;
        token.ThrowIfCancellationRequested();
        if (volume <= 0) return;
        await playback.WaitAsync(token).ConfigureAwait(false);
        try { await Task.Run(() => Play(audio, volume, token), token).ConfigureAwait(false); }
        finally { playback.Release(); }
    }

    private void EnsureDevice(SpeechAudio audio)
    {
        if (poisoned) throw new InvalidOperationException("Windows audio device unavailable.");
        if (device != IntPtr.Zero && sampleRate == audio.SampleRate && channels == audio.Channels) return;
        CloseDevice();
        var format = new WaveFormat
        {
            FormatTag = 1, Channels = (ushort)audio.Channels, SampleRate = (uint)audio.SampleRate,
            AverageBytesPerSecond = (uint)(audio.SampleRate * audio.Channels * 2),
            BlockAlign = (ushort)(audio.Channels * 2), BitsPerSample = 16
        };
        completed = new EventWaitHandle(false, EventResetMode.ManualReset);
        try
        {
            completed.SafeWaitHandle.DangerousAddRef(ref callbackRetained);
            var result = waveOutOpen(out var opened, uint.MaxValue, ref format,
                completed.SafeWaitHandle.DangerousGetHandle(), IntPtr.Zero, 0x50000); // CALLBACK_EVENT
            Check(result);
            device = opened;
            sampleRate = audio.SampleRate;
            channels = audio.Channels;
        }
        catch
        {
            if (callbackRetained) completed.SafeWaitHandle.DangerousRelease();
            callbackRetained = false;
            completed.Dispose();
            completed = null;
            throw;
        }
    }

    private void Play(SpeechAudio audio, int volume, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        EnsureDevice(audio);
        cancellation.ThrowIfCancellationRequested();
        var pcm = audio.CopySamples(volume);
        var pin = default(GCHandle);
        var header = IntPtr.Zero;
        var prepared = false;
        var safeToFree = true;
        var size = (uint)Marshal.SizeOf<WaveHeader>();
        try
        {
            pin = GCHandle.Alloc(pcm, GCHandleType.Pinned);
            header = Marshal.AllocHGlobal((int)size);
            Marshal.StructureToPtr(new WaveHeader { Data = pin.AddrOfPinnedObject(), Length = (uint)pcm.Length }, header, false);
            Check(waveOutPrepareHeader(device, header, size));
            prepared = true;
            completed!.Reset();
            cancellation.ThrowIfCancellationRequested();
            Check(waveOutWrite(device, header, size));
            WaitHandle[] waits = [completed, cancellation.WaitHandle];
            var deadline = Environment.TickCount64 + (long)audio.Duration.TotalMilliseconds + 2000;
            while ((Marshal.PtrToStructure<WaveHeader>(header).Flags & 1) == 0) // WHDR_DONE
            {
                cancellation.ThrowIfCancellationRequested();
                var remaining = deadline - Environment.TickCount64;
                if (remaining <= 0 || WaitHandle.WaitAny(waits, (int)remaining) == WaitHandle.WaitTimeout)
                    throw new TimeoutException("Windows audio playback timed out.");
                completed.Reset();
            }
            cancellation.ThrowIfCancellationRequested();
        }
        finally
        {
            if (prepared)
            {
                // Reset returns queued buffers before unpreparing/freeing their backing memory.
                if ((Marshal.PtrToStructure<WaveHeader>(header).Flags & 1) == 0) _ = waveOutReset(device);
                var unprepare = waveOutUnprepareHeader(device, header, size);
                safeToFree = unprepare == 0;
            }
            // Never free a buffer still owned by a failing native driver, or reuse that device.
            if (safeToFree)
            {
                if (header != IntPtr.Zero) Marshal.FreeHGlobal(header);
                if (pin.IsAllocated) pin.Free();
            }
            if (!safeToFree)
            {
                poisoned = true;
                throw new InvalidOperationException("Audio device cleanup failed.");
            }
        }
    }

    private void CloseDevice()
    {
        if (device != IntPtr.Zero)
        {
            _ = waveOutReset(device);
            // Retain the callback handle if a failing driver still owns the device.
            Check(waveOutClose(device));
            device = IntPtr.Zero;
        }
        if (completed is null) return;
        if (callbackRetained) completed.SafeWaitHandle.DangerousRelease();
        callbackRetained = false;
        completed.Dispose();
        completed = null;
    }

    private static void Check(uint result)
    {
        if (result != 0) throw new InvalidOperationException($"Windows audio error {result}.");
    }

    public ValueTask DisposeAsync()
    {
        lock (sync) return new ValueTask(disposal ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        await lifetime.CancelAsync().ConfigureAwait(false);
        await playback.WaitAsync().ConfigureAwait(false);
        try { await Task.Run(CloseDevice).ConfigureAwait(false); }
        finally { playback.Release(); }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WaveFormat
    {
        public ushort FormatTag, Channels;
        public uint SampleRate, AverageBytesPerSecond;
        public ushort BlockAlign, BitsPerSample, ExtraSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHeader
    {
        public IntPtr Data;
        public uint Length, BytesRecorded;
        public UIntPtr User;
        public uint Flags, Loops;
        public IntPtr Next;
        public UIntPtr Reserved;
    }

    [DllImport("winmm.dll")] private static extern uint waveOutOpen(out IntPtr device, uint deviceId, ref WaveFormat format, IntPtr callback, IntPtr instance, uint flags);
    [DllImport("winmm.dll")] private static extern uint waveOutPrepareHeader(IntPtr device, IntPtr header, uint size);
    [DllImport("winmm.dll")] private static extern uint waveOutWrite(IntPtr device, IntPtr header, uint size);
    [DllImport("winmm.dll")] private static extern uint waveOutReset(IntPtr device);
    [DllImport("winmm.dll")] private static extern uint waveOutUnprepareHeader(IntPtr device, IntPtr header, uint size);
    [DllImport("winmm.dll")] private static extern uint waveOutClose(IntPtr device);
}
