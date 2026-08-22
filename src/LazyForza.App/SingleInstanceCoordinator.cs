using System.Threading;
using System.Windows.Threading;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.IO;
using System.IO.Pipes;

namespace LazyForza.App;

internal sealed class SingleInstanceCoordinator : IDisposable
{
    private readonly Mutex mutex;
    private readonly EventWaitHandle showEvent;
    private readonly RegisteredWaitHandle registeredWait;
    private readonly CancellationTokenSource activationCancellation = new();
    private readonly Task activationServer;
    private bool ownsMutex;

    private SingleInstanceCoordinator(
        Mutex mutex,
        EventWaitHandle showEvent,
        Dispatcher dispatcher,
        Action showMainWindow,
        string activationPipeName,
        Action<IReadOnlyList<string>> activate)
    {
        this.mutex = mutex;
        this.showEvent = showEvent;
        ownsMutex = true;
        registeredWait = ThreadPool.RegisterWaitForSingleObject(
            showEvent,
            (_, _) => dispatcher.BeginInvoke(showMainWindow),
            null,
            Timeout.Infinite,
            executeOnlyOnce: false);
        activationServer = RunActivationServerAsync(
            activationPipeName,
            dispatcher,
            showMainWindow,
            activate,
            activationCancellation.Token);
    }

    public static SingleInstanceCoordinator? TryAcquire(
        Dispatcher dispatcher,
        Action showMainWindow,
        IReadOnlyList<string> activationArguments,
        Action<IReadOnlyList<string>> activate)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(showMainWindow);
        ArgumentNullException.ThrowIfNull(activationArguments);
        ArgumentNullException.ThrowIfNull(activate);

        var instanceKey = ExecutableInstanceKey();
        var mutexName = $@"Local\LazyForza.Application.{instanceKey}.SingleInstance";
        var showEventName = $@"Local\LazyForza.Application.{instanceKey}.ShowMainWindow";
        var activationPipeName = $"LazyForza.Application.{instanceKey}.Activation";
        var mutex = new Mutex(initiallyOwned: true, mutexName, out var createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            TrySendActivation(activationPipeName, activationArguments);
            try
            {
                using var existingEvent = EventWaitHandle.OpenExisting(showEventName);
                existingEvent.Set();
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                // The first process is still starting. Its main window will be shown normally.
            }
            return null;
        }

        var showEvent = new EventWaitHandle(
            initialState: false,
            EventResetMode.AutoReset,
            showEventName);
        return new SingleInstanceCoordinator(
            mutex,
            showEvent,
            dispatcher,
            showMainWindow,
            activationPipeName,
            activate);
    }

    public void Dispose()
    {
        activationCancellation.Cancel();
        registeredWait.Unregister(null);
        showEvent.Dispose();
        if (ownsMutex)
        {
            mutex.ReleaseMutex();
            ownsMutex = false;
        }
        mutex.Dispose();
        if (activationServer.IsFaulted)
            _ = activationServer.Exception;
        activationCancellation.Dispose();
        GC.SuppressFinalize(this);
    }

    private static async Task RunActivationServerAsync(
        string pipeName,
        Dispatcher dispatcher,
        Action showMainWindow,
        Action<IReadOnlyList<string>> activate,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(
                    pipeName,
                    PipeDirection.In,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
                using var cancellationRegistration = cancellationToken.Register(
                    static state => ((NamedPipeServerStream)state!).Dispose(),
                    pipe);
                await pipe.WaitForConnectionAsync(cancellationToken);
                using var reader = new StreamReader(
                    pipe,
                    Encoding.UTF8,
                    detectEncodingFromByteOrderMarks: false,
                    leaveOpen: true);
                var payload = await reader.ReadToEndAsync(cancellationToken);
                var arguments = JsonSerializer.Deserialize<string[]>(payload) ?? [];
                _ = dispatcher.BeginInvoke(new Action(() =>
                {
                    showMainWindow();
                    activate(arguments);
                }));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (IOException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (IOException) when (!cancellationToken.IsCancellationRequested)
            {
                // A client exited while sending. Keep accepting later activations.
            }
            catch (JsonException) when (!cancellationToken.IsCancellationRequested)
            {
                // Ignore malformed activation payloads from unrelated local clients.
            }
        }
    }

    private static void TrySendActivation(
        string pipeName,
        IReadOnlyList<string> arguments)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.Out,
                PipeOptions.Asynchronous);
            pipe.Connect(1500);
            using var writer = new StreamWriter(
                pipe,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                leaveOpen: true);
            writer.Write(JsonSerializer.Serialize(arguments));
            writer.Flush();
        }
        catch (IOException)
        {
        }
        catch (TimeoutException)
        {
        }
    }

    private static string ExecutableInstanceKey()
    {
        var executable = Environment.ProcessPath ?? AppContext.BaseDirectory;
        var normalized = Path.GetFullPath(executable)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..16];
    }
}
