using System.IO;
using LazyForza.Storage;
using LazyForza.Update;

namespace LazyForza.App;

internal enum VehicleNameUpdateState { Current, Ignored, Available }
internal sealed record VehicleNameUpdateCheck(VehicleNameUpdateState State, VehicleNameRevision Revision);

internal sealed class VehicleNameUpdateManager : IDisposable
{
    internal const string IgnoredRevisionSetting = "vehicleNames.ignoredRevision";
    private readonly LazyForzaStore store;
    private readonly VehicleNameCatalogCache cache;
    private readonly VehicleNameCatalogClient client;
    internal VehicleNameSnapshot Snapshot { get; private set; }

    internal VehicleNameUpdateManager(LazyForzaStore store, DataDirectoryService directories,
        VehicleNameCatalogClient? client = null)
    {
        this.store = store;
        this.client = client ?? new();
        cache = VehicleNameCatalogCache.ForDirectory(directories.Root);
        Snapshot = cache.Load(VehicleNameCatalog.EmbeddedSnapshot);
    }

    internal async Task<VehicleNameUpdateCheck> CheckAsync(CancellationToken cancellationToken)
    {
        var revision = await client.CheckAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        Reload();
        var current = Snapshot.Info;
        var state = current is not null && (current.Revision == revision.Revision || current.UpdatedAt > revision.UpdatedAt)
            ? VehicleNameUpdateState.Current
            : store.GetAppSetting(IgnoredRevisionSetting) == revision.Revision
                ? VehicleNameUpdateState.Ignored : VehicleNameUpdateState.Available;
        return new(state, revision);
    }

    internal void Ignore(VehicleNameRevision revision) => store.SetAppSetting(IgnoredRevisionSetting, revision.Revision);

    internal async Task<VehicleNameSnapshot> InstallAsync(VehicleNameRevision revision, CancellationToken cancellationToken)
    {
        if (store.GetAppSetting(IgnoredRevisionSetting) == revision.Revision)
            throw new InvalidOperationException("This vehicle name revision was ignored.");
        var json = await client.DownloadAsync(revision, cancellationToken).ConfigureAwait(false);
        var snapshot = VehicleNameSnapshot.FromUpstream(json, revision.Revision, revision.UpdatedAt);
        var saved = await cache.SaveAsync(snapshot, cancellationToken).ConfigureAwait(false);
        // The file replacement is the commit point; cancellation before it keeps the old cache intact.
        Snapshot = saved;
        return saved;
    }

    internal void Reload() => Snapshot = cache.Load(VehicleNameCatalog.EmbeddedSnapshot);

    internal void MigrateTo(string root, Action saveDirectory) =>
        VehicleNameCatalogCache.ForDirectory(root).Merge(cache.Load(VehicleNameCatalog.EmbeddedSnapshot), saveDirectory);

    public void Dispose() => client.Dispose();
}
