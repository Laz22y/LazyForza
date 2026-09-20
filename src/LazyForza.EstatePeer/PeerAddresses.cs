using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace LazyForza.EstatePeer;

public static class PeerAddresses
{
    public static IReadOnlyList<PeerEndpoint> Collect(int port, string? externalAddress = null, int? externalPort = null)
    {
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        var result = new List<PeerEndpoint>();
        if (!string.IsNullOrWhiteSpace(externalAddress))
        {
            if (!IPAddress.TryParse(externalAddress.Trim(), out var address) || !PeerInvitation.IsAllowedAddress(address) ||
                externalPort is < 1 or > 65535)
                throw new ArgumentException("外部地址必须是有效 IP，端口范围为 1–65535。");
            result.Add(new PeerEndpoint(address, externalPort ?? port));
        }
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces()
                     .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up &&
                         adapter.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)))
        {
            var properties = adapter.GetIPProperties();
            // Virtual/private adapters without a gateway require explicit selection through the manual address.
            if (properties.GatewayAddresses.Count == 0) continue;
            foreach (var address in properties.UnicastAddresses.Select(item => item.Address)
                         .Where(PeerInvitation.IsAllowedAddress).OrderByDescending(item => item.AddressFamily == AddressFamily.InterNetworkV6))
                result.Add(new PeerEndpoint(address, port));
        }
        return result.Distinct().Take(PeerInvitation.MaximumCandidates).ToArray();
    }
}
