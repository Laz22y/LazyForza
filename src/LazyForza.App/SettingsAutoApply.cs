using System.Windows.Threading;

namespace LazyForza.App;

/// <summary>Coalesces input edits and serializes application; a change during a save runs next.</summary>
internal sealed class SettingsAutoApply : IDisposable
{
    private readonly DispatcherTimer timer;
    private readonly Func<Task> apply;
    private readonly Action<Exception> report;
    private bool pending, disposed;
    private Task running = Task.CompletedTask;

    internal SettingsAutoApply(Func<Task> apply, Action<Exception> report, TimeSpan? delay = null)
    {
        this.apply = apply; this.report = report;
        timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = delay ?? TimeSpan.FromMilliseconds(180) };
        timer.Tick += Tick;
    }

    internal void Request()
    {
        if (disposed) return;
        pending = true;
        timer.Stop(); timer.Start();
    }

    internal Task FlushAsync()
    {
        timer.Stop();
        if (running.IsCompleted && pending) running = DrainAsync();
        return running;
    }

    private void Tick(object? sender, EventArgs args) => _ = FlushAsync();

    private async Task DrainAsync()
    {
        do
        {
            pending = false;
            try { await apply(); }
            catch (OperationCanceledException) when (disposed) { }
            catch (Exception exception) { report(exception); }
        } while (pending && !disposed);
    }

    public void Dispose()
    {
        disposed = true; pending = false;
        timer.Stop(); timer.Tick -= Tick;
    }
}
