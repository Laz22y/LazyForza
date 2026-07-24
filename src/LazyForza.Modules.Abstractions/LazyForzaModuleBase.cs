namespace LazyForza.Modules.Abstractions;

public abstract class LazyForzaModuleBase : ILazyForzaModule
{
    private readonly SemaphoreSlim lifecycleLock = new(1, 1);
    private IModuleContext? context;
    private bool disposed;

    protected LazyForzaModuleBase(ModuleDescriptor descriptor)
    {
        Descriptor = descriptor;
        Status = NewStatus(false, ModuleRuntimeState.Disabled, null);
    }

    public ModuleDescriptor Descriptor { get; }
    public ModuleStatus Status { get; private set; }
    protected IModuleContext Context => context ?? throw new InvalidOperationException("Module has not been initialized.");
    protected void LogIfInitialized(string message) => context?.Log(message);

    public async ValueTask InitializeAsync(IModuleContext moduleContext, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(moduleContext);
        await lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (context is not null)
            {
                return;
            }

            context = moduleContext;
            await OnInitializeAsync(cancellationToken).ConfigureAwait(false);
            Status = NewStatus(false, ModuleRuntimeState.Initialized, null);
        }
        catch (Exception ex)
        {
            Status = NewStatus(false, ModuleRuntimeState.Faulted, ex.Message);
            throw;
        }
        finally
        {
            lifecycleLock.Release();
        }
    }

    public async ValueTask StartAsync(CancellationToken cancellationToken)
    {
        await lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            _ = Context;
            if (Status.State == ModuleRuntimeState.Running)
            {
                return;
            }

            Status = NewStatus(true, ModuleRuntimeState.Starting, null);
            await OnStartAsync(cancellationToken).ConfigureAwait(false);
            Status = NewStatus(true, ModuleRuntimeState.Running, null);
        }
        catch (Exception ex)
        {
            Status = NewStatus(false, ModuleRuntimeState.Faulted, ex.Message);
            Context.Log($"Module {Descriptor.Id} failed to start: {ex}");
            throw;
        }
        finally
        {
            lifecycleLock.Release();
        }
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        await lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Status.State is ModuleRuntimeState.Disabled or ModuleRuntimeState.Initialized)
            {
                return;
            }

            Status = NewStatus(false, ModuleRuntimeState.Stopping, Status.LastError);
            await OnStopAsync(cancellationToken).ConfigureAwait(false);
            Status = NewStatus(false, ModuleRuntimeState.Initialized, null);
        }
        catch (Exception ex)
        {
            Status = NewStatus(false, ModuleRuntimeState.Faulted, ex.Message);
            Context.Log($"Module {Descriptor.Id} failed to stop: {ex}");
        }
        finally
        {
            lifecycleLock.Release();
        }
    }

    protected virtual ValueTask OnInitializeAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    protected abstract ValueTask OnStartAsync(CancellationToken cancellationToken);
    protected abstract ValueTask OnStopAsync(CancellationToken cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        disposed = true;
        lifecycleLock.Dispose();
        GC.SuppressFinalize(this);
    }

    private ModuleStatus NewStatus(bool enabled, ModuleRuntimeState state, string? error) =>
        new(Descriptor.Id, enabled, state, error, DateTimeOffset.UtcNow);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);
}
