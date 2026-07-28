using System.Threading;
using System.Windows.Threading;
using System.Security.Cryptography;
using System.Text;
using System.IO;

namespace LazyForza.App;

internal sealed class SingleInstanceCoordinator : IDisposable
{
    private readonly Mutex mutex;
    private readonly EventWaitHandle showEvent;
    private readonly RegisteredWaitHandle registeredWait;
    private bool ownsMutex;

    private SingleInstanceCoordinator(
        Mutex mutex,
        EventWaitHandle showEvent,
        Dispatcher dispatcher,
        Action showMainWindow)
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
    }

    public static SingleInstanceCoordinator? TryAcquire(
        Dispatcher dispatcher,
        Action showMainWindow)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(showMainWindow);

        var instanceKey = ExecutableInstanceKey();
        var mutexName = $@"Local\LazyForza.Application.{instanceKey}.SingleInstance";
        var showEventName = $@"Local\LazyForza.Application.{instanceKey}.ShowMainWindow";
        var mutex = new Mutex(initiallyOwned: true, mutexName, out var createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
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
        return new SingleInstanceCoordinator(mutex, showEvent, dispatcher, showMainWindow);
    }

    public void Dispose()
    {
        registeredWait.Unregister(null);
        showEvent.Dispose();
        if (ownsMutex)
        {
            mutex.ReleaseMutex();
            ownsMutex = false;
        }
        mutex.Dispose();
        GC.SuppressFinalize(this);
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
