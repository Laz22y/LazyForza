using System.Net.NetworkInformation;

namespace LazyForza.EstatePeer;

/// <summary>Recollect on a bounded timer, including after resume. Does not revoke admission or change identity.</summary>
public sealed class PeerCandidateMonitor : IAsyncDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly PeerPortMapping? mapping;
    private readonly Task worker;
    public int MappedEndpoints => mapping?.Endpoints.Count ?? 0;
    private IReadOnlyList<PeerEndpoint> current;
    public IReadOnlyList<PeerEndpoint> Current => Volatile.Read(ref current);
    public PeerCandidateMonitor(int port, bool tcp, bool udp, bool automaticMapping,
        Action<IReadOnlyList<PeerEndpoint>> changed, string? externalAddress = null, int? externalPort = null)
    {
        current = PeerAddresses.Collect(port, externalAddress, externalPort);
        mapping = automaticMapping ? new PeerPortMapping(port, tcp, udp) : null;
        worker = RunAsync();
        async Task RunAsync()
        {
            try
            {
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
                while (await timer.WaitForNextTickAsync(lifetime.Token).ConfigureAwait(false))
                {
                    IReadOnlyList<PeerEndpoint> next;
                    try { next = Merge(PeerAddresses.Collect(port, externalAddress, externalPort), mapping?.Endpoints ?? []); }
                    catch (NetworkInformationException) { PeerDiagnostics.Record(PeerStage.Network, PeerOutcome.Unavailable); continue; }
                    if (next.Count == 0 || Current.SequenceEqual(next)) continue;
                    Volatile.Write(ref current, next);
                    PeerDiagnostics.Record(PeerStage.Network, PeerOutcome.Changed);
                    changed(next);
                }
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        }
    }
    internal static IReadOnlyList<PeerEndpoint> Merge(IReadOnlyList<PeerEndpoint> local, IReadOnlyList<PeerEndpoint> mapped) =>
        local.Take(2).Concat(mapped.Take(4)).Concat(local.Skip(2)).Distinct().Take(PeerInvitation.MaximumCandidates).ToArray();
    public async ValueTask DisposeAsync()
    {
        await lifetime.CancelAsync().ConfigureAwait(false);
        await worker.ConfigureAwait(false);
        if (mapping is not null) await mapping.DisposeAsync().ConfigureAwait(false);
        lifetime.Dispose();
    }
}
