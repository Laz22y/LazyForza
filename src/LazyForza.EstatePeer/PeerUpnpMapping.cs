using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace LazyForza.EstatePeer;

/// <summary>IGDv2 AddAnyPortMapping reserves a new port without overwriting an existing mapping.</summary>
internal sealed class PeerUpnpMapping : IPeerMapping
{
    private const string Service = "urn:schemas-upnp-org:service:WANIPConnection:2";
    private readonly HttpClient http;
    private readonly PeerGateway gateway;
    private readonly Uri control;
    private readonly int port;
    private readonly string protocol, owner = "LazyForza-" + Guid.NewGuid().ToString("N");
    private bool owned;
    public PeerEndpoint Endpoint { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    internal PeerUpnpMapping(PeerGateway gateway, Uri control, int port, bool tcp)
    {
        this.gateway = gateway; this.control = control; this.port = port; protocol = tcp ? "TCP" : "UDP"; Endpoint = new(gateway.Local, port);
        http = new(new SocketsHttpHandler
        {
            UseProxy = false, AllowAutoRedirect = false,
            ConnectCallback = async (context, token) =>
            {
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    socket.Bind(new IPEndPoint(gateway.Local, 0));
                    await socket.ConnectAsync(new IPEndPoint(gateway.Gateway, context.DnsEndPoint.Port), token).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch { socket.Dispose(); throw; }
            }
        });
    }
    internal static async Task<IPeerMapping?> CreateAsync(PeerGateway gateway, int port, bool tcp, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        using var socket = new UdpClient(new IPEndPoint(gateway.Local, 0));
        socket.Ttl = 1;
        var request = Encoding.ASCII.GetBytes("M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 1\r\nST: " + Service + "\r\n\r\n");
        await socket.SendAsync(request, new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900), deadline.Token).ConfigureAwait(false);
        while (true)
        {
            var reply = await socket.ReceiveAsync(deadline.Token).ConfigureAwait(false);
            if (!reply.RemoteEndPoint.Address.Equals(gateway.Gateway) || reply.Buffer.Length > 8192) continue;
            var location = Encoding.ASCII.GetString(reply.Buffer).Split("\r\n")
                .FirstOrDefault(l => l.StartsWith("location:", StringComparison.OrdinalIgnoreCase))?[9..].Trim();
            if (!Uri.TryCreate(location, UriKind.Absolute, out var url) || !Allowed(url, gateway.Gateway)) continue;
            var mapping = new PeerUpnpMapping(gateway, url, port, tcp);
            try
            {
                using var response = await mapping.http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
                var document = await ReadAsync(response, deadline.Token).ConfigureAwait(false);
                var service = document.Descendants().FirstOrDefault(e => e.Name.LocalName == "service" && Value(e, "serviceType") == Service);
                if (service is null || !Uri.TryCreate(url, Value(service, "controlURL"), out var control) || !Allowed(control, gateway.Gateway))
                { await mapping.DisposeAsync(); return null; }
                await mapping.DisposeAsync();
                mapping = new PeerUpnpMapping(gateway, control, port, tcp);
                await mapping.RenewAsync(deadline.Token).ConfigureAwait(false);
                return mapping;
            }
            catch { await mapping.DisposeAsync(); throw; }
        }
    }
    internal static bool Allowed(Uri uri, IPAddress gateway) => uri.Scheme == "http" && uri.UserInfo.Length == 0 &&
        uri.Fragment.Length == 0 && IPAddress.TryParse(uri.Host, out var address) && address.Equals(gateway);
    public async Task RenewAsync(CancellationToken token)
    {
        if (owned && !await IsOwnedAsync(token).ConfigureAwait(false)) { owned = false; throw new IOException("Mapping ownership changed."); }
        var address = await CallAsync("GetExternalIPAddress", [], token).ConfigureAwait(false);
        if (!IPAddress.TryParse(Value(address, "NewExternalIPAddress"), out var external) || !PeerInvitation.IsAllowedAddress(external))
            throw new IOException("Invalid gateway address.");
        var values = Arguments().Concat(new[] { Pair("NewInternalPort", port), Pair("NewInternalClient", gateway.Local),
            Pair("NewEnabled", 1), Pair("NewPortMappingDescription", owner), Pair("NewLeaseDuration", 120) }).ToArray();
        var result = await CallAsync(owned ? "AddPortMapping" : "AddAnyPortMapping", values, token).ConfigureAwait(false);
        var assigned = owned ? Endpoint.Port : int.TryParse(Value(result, "NewReservedPort"), out var parsed) ? parsed : 0;
        if (assigned is < 1 or > 65535) throw new IOException("Invalid mapping port.");
        Endpoint = new(external, assigned); owned = true;
        if (!await IsOwnedAsync(token).ConfigureAwait(false)) { owned = false; throw new IOException("Mapping not confirmed."); }
        var confirmed = await CallAsync("GetSpecificPortMappingEntry", Arguments(), token).ConfigureAwait(false);
        if (!uint.TryParse(Value(confirmed, "NewLeaseDuration"), out var leaseSeconds) || leaseSeconds is 0 or > 120)
            throw new IOException("Gateway did not grant a finite short lease.");
        ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(leaseSeconds);
    }
    private XElement[] Arguments() => [Pair("NewRemoteHost", ""), Pair("NewExternalPort", owned ? Endpoint.Port : port), Pair("NewProtocol", protocol)];
    private async Task<bool> IsOwnedAsync(CancellationToken token)
    {
        var result = await CallAsync("GetSpecificPortMappingEntry", Arguments(), token).ConfigureAwait(false);
        return Value(result, "NewPortMappingDescription") == owner && Value(result, "NewInternalClient") == gateway.Local.ToString() &&
            int.TryParse(Value(result, "NewInternalPort"), out var target) && target == port;
    }
    private static XElement Pair(string key, object value) => new(key, value);
    private static string? Value(XContainer parent, string key) => parent.Descendants().FirstOrDefault(e => e.Name.LocalName == key)?.Value;
    private async Task<XDocument> CallAsync(string action, XElement[] arguments, CancellationToken token)
    {
        XNamespace soap = "http://schemas.xmlsoap.org/soap/envelope/";
        var doc = new XDocument(new XElement(soap + "Envelope", new XAttribute(XNamespace.Xmlns + "s", soap),
            new XElement(soap + "Body", new XElement(XName.Get(action, Service), arguments))));
        using var request = new HttpRequestMessage(HttpMethod.Post, control);
        request.Headers.Add("SOAPACTION", $"\"{Service}#{action}\"");
        request.Content = new StringContent(doc.ToString(), Encoding.UTF8, "text/xml");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        var result = await ReadAsync(response, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode || Value(result, "errorCode") is not null)
        {
            if (Value(result, "errorCode") == "606") throw new MappingDeniedException();
            throw new IOException("UPnP mapping unavailable.");
        }
        return result;
    }
    private static async Task<XDocument> ReadAsync(HttpResponseMessage response, CancellationToken token)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var bytes = new byte[4096]; int count;
        while ((count = await stream.ReadAsync(bytes, token).ConfigureAwait(false)) > 0)
        { if (buffer.Length + count > 65536) throw new IOException("Gateway response too large."); buffer.Write(bytes, 0, count); }
        buffer.Position = 0;
        try
        {
            using var reader = XmlReader.Create(buffer, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 65536 });
            return XDocument.Load(reader);
        }
        catch (XmlException e) { throw new IOException("Invalid gateway XML.", e); }
    }
    public async ValueTask DisposeAsync()
    {
        if (owned)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(600));
            try { if (await IsOwnedAsync(deadline.Token).ConfigureAwait(false)) await CallAsync("DeletePortMapping", Arguments(), deadline.Token).ConfigureAwait(false); }
            catch (Exception e) when (e is IOException or HttpRequestException or OperationCanceledException) { }
        }
        owned = false; http.Dispose();
    }
}
