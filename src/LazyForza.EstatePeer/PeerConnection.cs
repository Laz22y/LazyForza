using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace LazyForza.EstatePeer;

public sealed record PeerIdentity(Guid RoomId, int Generation, int ProtocolVersion);

/// <summary>A verified origin and pinned key, shared by metadata, assets, and WebSocket traffic.</summary>
public sealed class PeerConnection(PeerInvitation invitation, PeerEndpoint endpoint, IAsyncDisposable? transport = null) : IAsyncDisposable
{
    public ValueTask DisposeAsync() => transport?.DisposeAsync() ?? ValueTask.CompletedTask;
    public PeerInvitation Invitation { get; } = invitation;
    public Uri Origin { get; } = endpoint.HttpsUri;
    public string RecoveryScope => $"{Invitation.RoomId:N}.{Invitation.PublicKeySha256}";
    public Uri WebSocketUri => new UriBuilder(Origin) { Scheme = "wss", Path = "/ws" }.Uri;

    public HttpClient CreateHttpClient(string? password = null)
    {
        var transport = new HttpClientHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback = (_, certificate, _, _) => MatchesCertificate(certificate)
        };
        var client = new HttpClient(new OriginGuard(Origin, transport))
        {
            BaseAddress = Origin,
            Timeout = TimeSpan.FromSeconds(8),
            MaxResponseContentBufferSize = 1_572_864
        };
        client.DefaultRequestHeaders.Add("X-LazyForza-Room", Invitation.Header);
        if (password is not null)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("peer:" + password)));
        return client;
    }

    public void Configure(ClientWebSocket socket)
    {
        socket.Options.Proxy = null;
        socket.Options.SetRequestHeader("X-LazyForza-Room", Invitation.Header);
        socket.Options.RemoteCertificateValidationCallback = (_, certificate, _, _) =>
            certificate is not null && MatchesCertificate(certificate);
    }

    public bool MatchesCertificate(X509Certificate? certificate)
    {
        if (certificate is null) return false;
        try
        {
            using var value = new X509Certificate2(certificate);
            var now = DateTime.UtcNow;
            return now >= value.NotBefore.ToUniversalTime() && now <= value.NotAfter.ToUniversalTime() &&
                CryptographicOperations.FixedTimeEquals(SHA256.HashData(value.PublicKey.ExportSubjectPublicKeyInfo()),
                    Convert.FromHexString(Invitation.PublicKeySha256));
        }
        catch (CryptographicException) { return false; }
    }

    public static async Task<PeerConnection> FindAsync(PeerInvitation invitation, CancellationToken cancellationToken)
    {
        using var attempts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attempts.CancelAfter(TimeSpan.FromSeconds(10));
        var tasks = invitation.Candidates.Select((candidate, index) => ProbeAsync(candidate, index)).ToList();
        try
        {
            while (tasks.Count > 0)
            {
                var completed = await Task.WhenAny(tasks).ConfigureAwait(false);
                tasks.Remove(completed);
                if (await completed.ConfigureAwait(false) is { } result) return result;
            }
            cancellationToken.ThrowIfCancellationRequested();
            throw new IOException("未找到可达的房主。请检查双方 IPv6、房主端口映射和防火墙；此模式没有中继后备。");
        }
        finally
        {
            await attempts.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        async Task<PeerConnection?> ProbeAsync(PeerEndpoint candidate, int index)
        {
            try
            {
                await Task.Delay(index * 180, attempts.Token).ConfigureAwait(false);
                var connection = new PeerConnection(invitation, candidate);
                using var client = connection.CreateHttpClient();
                client.Timeout = TimeSpan.FromSeconds(4);
                client.MaxResponseContentBufferSize = 4096;
                var identity = await client.GetFromJsonAsync<PeerIdentity>("peer/identity", attempts.Token).ConfigureAwait(false);
                return identity is not null && identity.RoomId == invitation.RoomId &&
                    identity.Generation == invitation.Generation && identity.ProtocolVersion == 2 ? connection : null;
            }
            catch (Exception error) when (error is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException or IOException)
            {
                return null;
            }
        }
    }

    private sealed class OriginGuard(Uri origin, HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri is not { Scheme: "https" } uri || uri.Host != origin.Host || uri.Port != origin.Port)
                throw new InvalidOperationException("直连请求不能离开已验证的房主地址。");
            return base.SendAsync(request, cancellationToken);
        }
    }
}
