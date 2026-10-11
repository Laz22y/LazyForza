using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading.Channels;
using LazyForza.Domain;

namespace LazyForza.Telemetry;

public sealed record TelemetryForwardingOptions(bool Enabled = false, string Address = "127.0.0.1", int Port = 2300);
public sealed record TelemetryForwardingStatus(bool Enabled, long Sent, long Dropped, long Errors, SocketError? LastError);

public static class TelemetryNetworkPolicy
{
    public static bool IsDiscouragedPort(int port) => port is >= 5200 and <= 5300;

    public static bool TryParseListener(string address, string port, out IPAddress? parsedAddress, out int parsedPort)
    {
        parsedPort = 0;
        return IPAddress.TryParse(address.Trim(), out parsedAddress) && int.TryParse(port.Trim(), out parsedPort) &&
            parsedPort is >= 1 and <= 65535;
    }

    internal static IPEndPoint ForwardingEndpoint(TelemetryForwardingOptions options)
    {
        if (!IPAddress.TryParse(options.Address?.Trim(), out var address) || options.Port is < 1 or > 65535)
            throw new ArgumentException("请输入有效目标 IP 地址和 1–65535 端口。");
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var first = address.GetAddressBytes()[0];
        if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || address.IsIPv6Multicast ||
            address.AddressFamily == AddressFamily.InterNetwork && (first == 0 || first >= 224))
            throw new ArgumentException("转发目标必须是单台设备的 IP，不能使用未指定、广播或组播地址。");
        return new(address, options.Port);
    }

    internal static bool IsLocal(IPAddress address) => IPAddress.IsLoopback(address) ||
        NetworkInterface.GetAllNetworkInterfaces().SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Any(a => a.Address.Equals(address));

    internal static void ValidateForwarding(TelemetryForwardingOptions options, int listeningPort)
    {
        var target = ForwardingEndpoint(options);
        if (options.Enabled && target.Port == listeningPort && IsLocal(target.Address))
            throw new ArgumentException("转发目标不能指向本机的遥测监听端口，请更换目标端口。");
    }
}

/// <summary>Owned by TelemetryHub. The receive path only writes to a bounded queue; it never awaits the network.</summary>
internal sealed class TelemetryForwarder(
    Func<ReadOnlyMemory<byte>, IPEndPoint, CancellationToken, ValueTask<int>>? send = null) : IAsyncDisposable
{
    private Session? current;
    internal bool Enabled => Volatile.Read(ref current) is not null;
    internal TelemetryForwardingOptions Options => Volatile.Read(ref current)?.Options ?? new();
    internal TelemetryForwardingStatus Status => Volatile.Read(ref current)?.Status ?? new(false, 0, 0, 0, null);

    internal async Task ConfigureAsync(TelemetryForwardingOptions options, int listeningPort)
    {
        var next = options.Enabled ? new Session(options, listeningPort, send) : null;
        var previous = Interlocked.Exchange(ref current, next);
        if (previous is not null) await previous.DisposeAsync().ConfigureAwait(false);
    }

    internal void Publish(TelemetryFrame frame)
    {
        if (frame.Source == TelemetrySourceKind.Live && frame.RawPacket.Length == Fh6PacketParser.PacketLength)
            Volatile.Read(ref current)?.Offer(frame.RawPacket);
    }

    internal void ChangeListeningPort(int port) => Volatile.Read(ref current)?.ChangeListeningPort(port);
    public async ValueTask DisposeAsync()
    {
        var previous = Interlocked.Exchange(ref current, null);
        if (previous is not null) await previous.DisposeAsync().ConfigureAwait(false);
    }

    private sealed class Session : IAsyncDisposable
    {
        private readonly UdpClient? socket;
        private readonly Func<ReadOnlyMemory<byte>, IPEndPoint, CancellationToken, ValueTask<int>> send;
        private readonly IPEndPoint target;
        private readonly Channel<ReadOnlyMemory<byte>> queue;
        private readonly CancellationTokenSource lifetime = new();
        private readonly Task worker;
        private int listeningPort, lastError = int.MinValue;
        private long sent, dropped, errors, checkAfter;
        private bool isLocal;
        internal TelemetryForwardingOptions Options { get; }
        internal TelemetryForwardingStatus Status => new(true, Interlocked.Read(ref sent), Interlocked.Read(ref dropped),
            Interlocked.Read(ref errors), Volatile.Read(ref lastError) is var value && value != int.MinValue ? (SocketError)value : null);

        internal Session(TelemetryForwardingOptions options, int listeningPort,
            Func<ReadOnlyMemory<byte>, IPEndPoint, CancellationToken, ValueTask<int>>? send)
        {
            target = TelemetryNetworkPolicy.ForwardingEndpoint(options);
            Options = options with { Address = target.Address.ToString() };
            this.listeningPort = listeningPort;
            if (send is null)
            {
                socket = new UdpClient(target.AddressFamily);
                this.send = socket.SendAsync;
            }
            else this.send = send;
            queue = Channel.CreateBounded<ReadOnlyMemory<byte>>(new BoundedChannelOptions(64)
            {
                FullMode = BoundedChannelFullMode.DropOldest, SingleWriter = true, SingleReader = true,
                AllowSynchronousContinuations = false
            }, _ => Interlocked.Increment(ref dropped));
            worker = RunAsync();
        }

        internal void ChangeListeningPort(int port) => Volatile.Write(ref listeningPort, port);
        internal void Offer(ReadOnlyMemory<byte> bytes) => queue.Writer.TryWrite(bytes);
        private async Task RunAsync()
        {
            try
            {
                await foreach (var bytes in queue.Reader.ReadAllAsync(lifetime.Token).ConfigureAwait(false))
                {
                    try
                    {
                        // Recheck local addresses when a network change could turn a remote target into ourselves.
                        if (target.Port == Volatile.Read(ref listeningPort))
                        {
                            if (Environment.TickCount64 >= checkAfter)
                            { isLocal = TelemetryNetworkPolicy.IsLocal(target.Address); checkAfter = Environment.TickCount64 + 1000; }
                            if (isLocal)
                            {
                                Interlocked.Increment(ref dropped);
                                Volatile.Write(ref lastError, (int)SocketError.InvalidArgument);
                                continue;
                            }
                        }
                        await send(bytes, target, lifetime.Token).ConfigureAwait(false);
                        Interlocked.Increment(ref sent); Volatile.Write(ref lastError, int.MinValue);
                    }
                    catch (Exception error) when (error is SocketException or NetworkInformationException)
                    {
                        Interlocked.Increment(ref errors);
                        Volatile.Write(ref lastError, error is SocketException socketError ? (int)socketError.SocketErrorCode : (int)SocketError.NetworkDown);
                        // A failed destination cannot stop telemetry or produce an unbounded retry loop.
                        await Task.Delay(500, lifetime.Token).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        }
        public async ValueTask DisposeAsync()
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
            queue.Writer.TryComplete();
            try { await worker.ConfigureAwait(false); }
            finally { socket?.Dispose(); lifetime.Dispose(); }
        }
    }
}
