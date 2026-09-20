using System.Collections.Concurrent;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;

namespace LazyForza.EstatePeer;

/// <summary>A QUIC stream carries one existing pinned HTTPS/WSS connection, including track download.</summary>
[SupportedOSPlatform("windows")]
public sealed class PeerQuicHost : IAsyncDisposable
{
    internal static readonly SslApplicationProtocol Protocol = new("lazyforza-peer-1");
    private readonly QuicListener listener;
    private readonly PeerUdpPath path;
    private readonly int tcpPort;
    private readonly CancellationTokenSource lifetime = new();
    private readonly ConcurrentDictionary<int, Task> workers = new();
    private readonly SemaphoreSlim capacity = new(32, 32);
    private readonly Task accept;
    private int nextId;
    public static bool IsSupported => QuicConnection.IsSupported && QuicListener.IsSupported;

    private PeerQuicHost(QuicListener listener, PeerUdpPath path, int tcpPort)
    { this.listener = listener; this.path = path; this.tcpPort = tcpPort; accept = AcceptAsync(); }

    public static async Task<PeerQuicHost> StartAsync(int publicPort, int tcpPort, X509Certificate2 certificate, CancellationToken token)
    {
        var path = new PeerUdpPath(publicPort);
        try
        {
            var listener = await QuicListener.ListenAsync(new QuicListenerOptions
            {
                ListenEndPoint = new(IPAddress.Loopback, 0), ApplicationProtocols = [Protocol], ListenBacklog = 32,
                ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(new QuicServerConnectionOptions
                {
                    DefaultCloseErrorCode = 1, DefaultStreamErrorCode = 1,
                    MaxInboundBidirectionalStreams = 8, MaxInboundUnidirectionalStreams = 0,
                    IdleTimeout = TimeSpan.FromSeconds(45), HandshakeTimeout = TimeSpan.FromSeconds(8),
                    ServerAuthenticationOptions = new SslServerAuthenticationOptions
                    { ApplicationProtocols = [Protocol], ServerCertificate = certificate }
                })
            }, token).ConfigureAwait(false);
            return new(listener, path, tcpPort);
        }
        catch { await path.DisposeAsync().ConfigureAwait(false); throw; }
    }

    public void Admit(PeerReceipt receipt)
    {
        path.Add(receipt, receipt.Candidates, listener.LocalEndPoint);
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                QuicConnection connection;
                try { connection = await listener.AcceptConnectionAsync(lifetime.Token).ConfigureAwait(false); }
                catch (Exception error) when (!lifetime.IsCancellationRequested &&
                    error is System.Security.Authentication.AuthenticationException or QuicException)
                { continue; } // A rejected handshake must not stop accepting other players.
                if (!path.Authenticate(connection.RemoteEndPoint) || !capacity.Wait(0))
                { await connection.DisposeAsync().ConfigureAwait(false); continue; }
                Track(ServeAsync(connection));
            }
        }
        catch (Exception error) when (PeerTunnelCopy.IsConnectionError(error)) { }
    }

    private async Task ServeAsync(QuicConnection connection)
    {
        await using (connection.ConfigureAwait(false))
        {
            try
            {
                while (!lifetime.IsCancellationRequested)
                {
                    var stream = await connection.AcceptInboundStreamAsync(lifetime.Token).ConfigureAwait(false);
                    Track(ForwardAsync(stream));
                }
            }
            catch (Exception error) when (PeerTunnelCopy.IsConnectionError(error)) { }
            finally { capacity.Release(); }
        }
    }

    private async Task ForwardAsync(QuicStream stream)
    {
        await using (stream.ConfigureAwait(false))
        using (var tcp = new TcpClient())
        {
            try
            {
                await tcp.ConnectAsync(IPAddress.Loopback, tcpPort, lifetime.Token).ConfigureAwait(false);
                await PeerTunnelCopy.RunAsync(tcp.GetStream(), stream, lifetime.Token).ConfigureAwait(false);
            }
            catch (Exception error) when (PeerTunnelCopy.IsConnectionError(error)) { }
        }
    }

    private void Track(Task task)
    {
        var id = Interlocked.Increment(ref nextId); workers[id] = task;
        _ = task.ContinueWith(_ => workers.TryRemove(id, out var ignored), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    public async ValueTask DisposeAsync()
    {
        await lifetime.CancelAsync().ConfigureAwait(false);
        await listener.DisposeAsync().ConfigureAwait(false);
        await accept.ConfigureAwait(false);
        await Task.WhenAll(workers.Values).ConfigureAwait(false);
        await path.DisposeAsync().ConfigureAwait(false);
        lifetime.Dispose(); capacity.Dispose();
    }
}

[SupportedOSPlatform("windows")]
public sealed class PeerQuicJoin : IAsyncDisposable
{
    private readonly PeerInvitation invitation;
    private readonly PeerUdpPath path;
    private readonly IPEndPoint target;
    private readonly CancellationTokenSource lifetime = new();
    private readonly TcpListener tcp = new(IPAddress.Loopback, 0);
    private readonly SemaphoreSlim connecting = new(1, 1);
    private readonly ConcurrentDictionary<int, Task> workers = new();
    private QuicConnection? connection;
    private Task? accept;
    private int nextId;
    private int disposed;
    public PeerReceipt Receipt { get; }
    public PeerConnection? Connection { get; private set; }

    public PeerQuicJoin(PeerInvitation invitation)
    {
        if (!invitation.SupportsUdp || !PeerQuicHost.IsSupported)
            throw new PlatformNotSupportedException("UDP 直连需要双方使用支持 QUIC 的 Windows 11 和新版房主组件。");
        this.invitation = invitation;
        path = new PeerUdpPath(0);
        try
        {
            var candidates = PeerAddresses.Collect(path.Port);
            if (candidates.Count == 0) throw new IOException("未找到可用于回执的网络地址。");
            Receipt = PeerReceipt.Create(invitation, candidates);
            target = path.Add(Receipt, invitation.Candidates, null);
        }
        catch { path.DisposeAsync().AsTask().GetAwaiter().GetResult(); throw; }
    }

    public async Task<PeerConnection> ConnectAsync(CancellationToken token)
    {
        await EnsureConnectionAsync(token).ConfigureAwait(false);
        if (Connection is null)
        {
            tcp.Start(16);
            Connection = new PeerConnection(invitation, new PeerEndpoint(IPAddress.Loopback, ((IPEndPoint)tcp.LocalEndpoint).Port), this);
            accept = AcceptAsync();
        }
        using var http = Connection.CreateHttpClient();
        var identity = await System.Net.Http.Json.HttpClientJsonExtensions.GetFromJsonAsync<PeerIdentity>(http, "peer/identity", token).ConfigureAwait(false);
        if (identity is null || identity.RoomId != invitation.RoomId || identity.Generation != invitation.Generation || identity.ProtocolVersion != 2)
            throw new IOException("房主身份已变更，请索取新的邀请代码。");
        return Connection;
    }

    private async Task<QuicConnection> EnsureConnectionAsync(CancellationToken token)
    {
        await connecting.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (connection is not null) return connection;
            var pin = new PeerConnection(invitation, new PeerEndpoint(IPAddress.Loopback, 1));
            connection = await QuicConnection.ConnectAsync(new QuicClientConnectionOptions
            {
                RemoteEndPoint = target, DefaultCloseErrorCode = 1, DefaultStreamErrorCode = 1,
                MaxInboundBidirectionalStreams = 0, MaxInboundUnidirectionalStreams = 0,
                IdleTimeout = TimeSpan.FromSeconds(45), HandshakeTimeout = TimeSpan.FromSeconds(8),
                ClientAuthenticationOptions = new SslClientAuthenticationOptions
                {
                    TargetHost = "LazyForza Peer", ApplicationProtocols = [PeerQuicHost.Protocol],
                    RemoteCertificateValidationCallback = (_, certificate, _, _) => pin.MatchesCertificate(certificate)
                }
            }, token).ConfigureAwait(false);
            path.Authenticate(Receipt.Nonce);
            return connection;
        }
        finally { connecting.Release(); }
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                var client = await tcp.AcceptTcpClientAsync(lifetime.Token).ConfigureAwait(false);
                if (workers.Count >= 16) { client.Dispose(); continue; }
                var id = Interlocked.Increment(ref nextId);
                workers[id] = ForwardAsync(client);
                _ = workers[id].ContinueWith(_ => workers.TryRemove(id, out var ignored), CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        catch (Exception error) when (PeerTunnelCopy.IsConnectionError(error)) { }
    }

    private async Task ForwardAsync(TcpClient client)
    {
        using (client)
        {
            QuicConnection? current = null;
            try
            {
                current = await EnsureConnectionAsync(lifetime.Token).ConfigureAwait(false);
                await using var stream = await current.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, lifetime.Token).ConfigureAwait(false);
                await PeerTunnelCopy.RunAsync(client.GetStream(), stream, lifetime.Token).ConfigureAwait(false);
            }
            catch (QuicException error) when (error.QuicError != QuicError.StreamAborted)
            {
                await connecting.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (current is not null && ReferenceEquals(connection, current))
                    { connection = null; await current.DisposeAsync().ConfigureAwait(false); }
                }
                finally { connecting.Release(); }
            }
            catch (Exception error) when (PeerTunnelCopy.IsConnectionError(error)) { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        await lifetime.CancelAsync().ConfigureAwait(false);
        tcp.Stop();
        if (accept is not null) await accept.ConfigureAwait(false);
        if (connection is not null) await connection.DisposeAsync().ConfigureAwait(false);
        await Task.WhenAll(workers.Values).ConfigureAwait(false);
        await path.DisposeAsync().ConfigureAwait(false);
        lifetime.Dispose(); connecting.Dispose();
    }
}

internal static class PeerTunnelCopy
{
    internal static async Task RunAsync(Stream tcp, Stream quic, CancellationToken token)
    {
        using var both = CancellationTokenSource.CreateLinkedTokenSource(token);
        var upstream = tcp.CopyToAsync(quic, 16384, both.Token);
        var downstream = quic.CopyToAsync(tcp, 16384, both.Token);
        await Task.WhenAny(upstream, downstream).ConfigureAwait(false);
        await both.CancelAsync().ConfigureAwait(false);
        try { await Task.WhenAll(upstream, downstream).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested &&
            (upstream.IsCompletedSuccessfully || downstream.IsCompletedSuccessfully)) { }
    }

    internal static bool IsConnectionError(Exception error) => error is IOException or SocketException or OperationCanceledException or ObjectDisposedException;
}
