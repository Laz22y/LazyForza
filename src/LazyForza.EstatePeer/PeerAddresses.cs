using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace LazyForza.EstatePeer;

public static class PeerAddresses
{
    public static IReadOnlyList<PeerEndpoint> Collect(int port, string? externalAddress = null, int? externalPort = null)
    {
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        PeerEndpoint? explicitEndpoint = null;
        if (!string.IsNullOrWhiteSpace(externalAddress))
        {
            if (!IPAddress.TryParse(externalAddress.Trim(), out var address) || !PeerInvitation.IsAllowedAddress(address) ||
                externalPort is < 1 or > 65535)
                throw new ArgumentException("外部地址必须是有效 IP，端口范围为 1–65535。");
            explicitEndpoint = new PeerEndpoint(address, externalPort ?? port);
        }
        var candidates = new List<Candidate>();
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces()
                     .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up &&
                         adapter.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)))
        {
            var properties = adapter.GetIPProperties();
            foreach (var unicast in properties.UnicastAddresses)
            {
                if (!PeerInvitation.IsAllowedAddress(unicast.Address)) continue;
                if (OperatingSystem.IsWindows() && unicast.DuplicateAddressDetectionState is
                    DuplicateAddressDetectionState.Invalid or DuplicateAddressDetectionState.Tentative or
                    DuplicateAddressDetectionState.Duplicate or DuplicateAddressDetectionState.Deprecated) continue;
                candidates.Add(new(adapter.Id, new(unicast.Address, port), properties.GatewayAddresses.Count > 0));
            }
        }
        return Select(candidates, explicitEndpoint);
    }

    internal sealed record Candidate(string Interface, PeerEndpoint Endpoint, bool HasGateway);

    internal static IReadOnlyList<PeerEndpoint> Select(IEnumerable<Candidate> candidates, PeerEndpoint? explicitEndpoint = null)
    {
        var result = new List<PeerEndpoint>();
        if (explicitEndpoint is not null) result.Add(explicitEndpoint);
        // Give each interface/address family a slot before taking additional temporary IPv6 addresses.
        // Gateway-free LAN adapters remain usable, but follow adapters with an Internet route.
        var groups = candidates.OrderByDescending(item => item.HasGateway)
            .ThenByDescending(item => item.Endpoint.Address.AddressFamily == AddressFamily.InterNetworkV6)
            .GroupBy(item => (item.Interface, item.Endpoint.Address.AddressFamily))
            .Select(group => new Queue<PeerEndpoint>(group.Select(item => item.Endpoint).Distinct())).ToArray();
        while (result.Count < PeerInvitation.MaximumCandidates && groups.Any(group => group.Count > 0))
            foreach (var group in groups)
                if (result.Count < PeerInvitation.MaximumCandidates && group.TryDequeue(out var endpoint) && !result.Contains(endpoint))
                    result.Add(endpoint);
        return result;
    }
}
