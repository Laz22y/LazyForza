using System.Globalization;
using System.Runtime.InteropServices;
using LazyForza.Speech;

namespace LazyForza.App;

/// <summary>Windows SAPI renders to owned PCM in memory; no device playback occurs here.</summary>
internal sealed class WindowsSapiSpeechProvider : ISpeechSynthesisProvider
{
    private readonly object sync = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly HashSet<Task> pending = [];
    private Task? disposal;
    public string Id => "windows-sapi";
    public SpeechProviderLocation Location => SpeechProviderLocation.Local;

    public Task<SpeechAudio> SynthesizeAsync(SpeechSynthesisRequest request, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<SpeechAudio>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenSource cancellation;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposal is not null, this);
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
            pending.Add(completion.Task);
        }
        _ = completion.Task.ContinueWith(task =>
        {
            _ = task.Exception;
            lock (sync) pending.Remove(task);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        var token = cancellation.Token;
        var thread = new Thread(() =>
        {
            object? instance = null, voices = null, selected = null, stream = null, format = null;
            SpeechAudio? audio = null;
            Exception? failure = null;
            var cancelled = false;
            try
            {
                token.ThrowIfCancellationRequested();
                var culture = CultureInfo.GetCultureInfo(request.Language);
                instance = Create("SAPI.SpVoice");
                stream = Create("SAPI.SpMemoryStream");
                dynamic voice = instance;
                dynamic memory = stream;
                format = memory.Format;
                ((dynamic)format).Type = 26; // SAFT24kHz16BitMono
                voices = voice.GetVoices($"Language={culture.LCID:X}", "");
                dynamic installed = voices;
                for (var index = 0; index < installed.Count; index++)
                {
                    object candidate = installed.Item(index);
                    if (request.VoiceId is null || string.Equals((string)((dynamic)candidate).Id, request.VoiceId, StringComparison.OrdinalIgnoreCase))
                    {
                        selected = candidate;
                        break;
                    }
                    Marshal.FinalReleaseComObject(candidate);
                }
                if (selected is null) throw new InvalidOperationException("No installed SAPI voice matches the requested language and voice.");
                voice.Voice = selected;
                voice.Volume = 100;
                voice.Rate = (int)Math.Round(10 * Math.Log2(request.Rate));
                voice.AllowAudioOutputFormatChangesOnNextSet = false;
                voice.AudioOutputStream = memory;
                voice.Speak(request.Text, 1 | 16); // Asynchronous plain text, never SSML.
                while (!voice.WaitUntilDone(10))
                {
                    if (!token.IsCancellationRequested) continue;
                    voice.Speak("", 1 | 2);
                    token.ThrowIfCancellationRequested();
                }
                token.ThrowIfCancellationRequested();
                audio = new SpeechAudio((byte[])memory.GetData(), 24000);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { cancelled = true; }
            catch (Exception exception) { failure = exception; }
            finally
            {
                foreach (var value in new[] { selected, voices, instance, format, stream })
                {
                    try { if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value); }
                    catch (Exception) { /* Cleanup must not terminate the application. */ }
                }
                cancellation.Dispose();
            }
            // Completion also means this request's COM objects have been released.
            if (cancelled) completion.TrySetCanceled(token);
            else if (failure is not null) completion.TrySetException(failure);
            else completion.TrySetResult(audio!);
        }) { IsBackground = true, Name = "LazyForza SAPI synthesis" };
        try
        {
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }
        catch (Exception exception)
        {
            cancellation.Dispose();
            completion.TrySetException(exception);
        }
        return completion.Task;
    }

    private static object Create(string id) => Activator.CreateInstance(Type.GetTypeFromProgID(id)
        ?? throw new InvalidOperationException("Windows speech component unavailable."))
        ?? throw new InvalidOperationException("Windows speech component unavailable.");

    public ValueTask DisposeAsync()
    {
        lock (sync) return new ValueTask(disposal ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        await lifetime.CancelAsync().ConfigureAwait(false);
        Task[] outstanding;
        lock (sync) outstanding = pending.ToArray();
        try { await Task.WhenAll(outstanding).ConfigureAwait(false); }
        catch (Exception) { /* Individual requests already report their failures. */ }
        finally { lifetime.Dispose(); }
    }
}
