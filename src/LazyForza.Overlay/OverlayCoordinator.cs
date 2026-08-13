using System.Collections.Concurrent;
using System.Windows;
using LazyForza.Domain;
using LazyForza.Modules.Abstractions;

namespace LazyForza.Overlay;

public sealed class OverlayCoordinator : IHudHost, IDisposable
{
    private readonly ConcurrentDictionary<string, IHudContribution> contributions = new();
    private readonly object contributionSync = new();
    private IHudContribution[] orderedContributions = [];
    private TelemetryOverlayWindow? window;
    private OverlayLayout layout;
    private bool disposed;

    public OverlayCoordinator(OverlayLayout? initialLayout = null) =>
        layout = NormalizeLayout(initialLayout ?? new OverlayLayout());

    public OverlayLayout CurrentLayout => window?.CaptureLayout() ?? layout;
    public OverlayLayout TimingLayout => layout;

    public async ValueTask AttachAsync(IHudContribution contribution, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        lock (contributionSync)
        {
            contributions[contribution.Id] = contribution;
            RefreshContributionSnapshot();
        }
        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureWindow();
            window!.InvalidateHud();
            if (!window.IsVisible) window.Show();
        });
    }

    public async ValueTask DetachAsync(string contributionId, CancellationToken cancellationToken)
    {
        lock (contributionSync)
        {
            contributions.TryRemove(contributionId, out _);
            RefreshContributionSnapshot();
        }
        if (Application.Current is null) return;
        var dispatcher = Application.Current.Dispatcher;
        if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) return;

        void UpdateWindow()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (window is null) return;
            if (contributions.IsEmpty)
            {
                layout = window.CaptureLayout();
                window.Close();
                window = null;
            }
            else
            {
                window.InvalidateHud();
            }
        }

        if (dispatcher.CheckAccess()) UpdateWindow();
        else
        {
            try { await dispatcher.InvokeAsync(UpdateWindow); }
            catch (TaskCanceledException) when (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) { }
        }
    }

    public async ValueTask SetLayoutAsync(OverlayLayout newLayout, CancellationToken cancellationToken)
    {
        layout = NormalizeLayout(newLayout);
        if (Application.Current is null) return;
        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            window?.ApplyLayout(layout);
        });
    }

    public OverlayLayout? EditLayout(
        ForzaHorizonWindowInfo target,
        string? setupNotice = null)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (Application.Current is null ||
            !Application.Current.Dispatcher.CheckAccess())
            throw new InvalidOperationException("Overlay layout editing must start on the UI thread.");

        var wasVisible = window?.IsVisible == true;
        window?.Hide();
        try
        {
            _ = ForzaHorizonWindow.TryActivate(target);
            var editor = new OverlayLayoutEditorWindow(
                GetContributionSnapshot,
                layout,
                target,
                setupNotice);
            var accepted = editor.ShowDialog() == true;
            if (!accepted || editor.Result is not { } result) return null;
            layout = NormalizeLayout(result);
            return layout;
        }
        finally
        {
            if (window is not null)
            {
                window.ApplyLayout(layout);
                if (wasVisible) window.Show();
            }
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (window is not null)
        {
            layout = window.CaptureLayout();
            window.Close();
            window = null;
        }
    }

    public async ValueTask CapturePngAsync(
        string path,
        CancellationToken cancellationToken,
        bool previewDrift = false,
        bool previewEstateRace = false,
        bool previewEstateRaceFinished = false,
        bool previewEstateRaceChequered = false)
    {
        if (Application.Current is null) throw new InvalidOperationException("WPF application is not running.");
        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureWindow();
            var bounds = OverlayLayoutGeometry.UnionBounds(layout);
            window!.CapturePng(
                path,
                bounds.Width,
                bounds.Height,
                previewDrift,
                previewEstateRace,
                previewEstateRaceFinished,
                previewEstateRaceChequered);
        });
    }

    private void EnsureWindow()
    {
        if (window is not null) return;
        window = new TelemetryOverlayWindow(GetContributionSnapshot, layout);
    }

    private IReadOnlyList<IHudContribution> GetContributionSnapshot() =>
        Volatile.Read(ref orderedContributions);

    private void RefreshContributionSnapshot() =>
        Volatile.Write(
            ref orderedContributions,
            contributions.Values.OrderBy(item => item.ZIndex).ToArray());

    private static OverlayLayout NormalizeLayout(OverlayLayout value) =>
        OverlayLayoutGeometry.Normalize(value) with
        {
            ClickThrough = true,
            IsLocked = true
        };
}
