using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using LazyForza.RaceServer.Core;
using LazyForza.RaceServer.Protocol;
using LazyForza.RaceServer.Web;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace LazyForza.EstatePeer.Host;

public sealed class PeerRoom : IAsyncDisposable
{
    private readonly ConcurrentDictionary<Guid, PeerSocket> clients = new();
    private readonly SemaphoreSlim downloads = new(2, 2);
    private readonly SemaphoreSlim credentials = new(2, 2);
    private readonly object ingressSync = new();
    private readonly Dictionary<string, (DateTimeOffset Start, int Count)> attempts = [];
    private readonly CancellationTokenSource lifetime = new();
    private readonly PeerHostStart settings;
    private readonly byte[] passwordSalt;
    private readonly byte[] passwordHash;
    private readonly byte[] track;
    private readonly string trackFileHash;
    private readonly X509Certificate2 certificate;
    private readonly object identitySync = new();
    private readonly string identityPath;
    private RoomIdentity identity;
    private WebApplication? app;
    private Task? tickTask;
    private long sequence;
    private int disposed;
    private PeerQuicHost? udp;
    private PeerWebControl? webControl;
    private readonly RaceWebSocketRegistry webSockets = new();
    public RaceCoordinator Coordinator { get; }
    public PeerInvitation Invitation { get; private set; }
    public int Port { get; private set; }
    public Task Completion => tickTask ?? Task.CompletedTask;

    public PeerRoom(PeerHostStart settings)
    {
        if (settings.Password.Length is < 6 or > 128) throw new ArgumentException("房间密码需要 6–128 个字符。");
        if (string.IsNullOrWhiteSpace(settings.RoomName) || settings.RoomName.Length > 64)
            throw new ArgumentException("房间名需要 1–64 个字符。");
        if (settings.Port is < 0 or > 65535 || settings.RaceLaps is < 1 or > 999 ||
            settings.SectorCount is < 1 or > 20 || !Guid.TryParse(settings.TrackId, out _) ||
            settings.TrackPackageHash.Length != 64) throw new ArgumentException("房间设置无效。");
        this.settings = settings;
        if (new FileInfo(settings.TrackPackagePath).Length is <= 0 or > 1_572_864)
            throw new InvalidDataException("赛道包超过 1.5 MiB 上限。");
        track = File.ReadAllBytes(settings.TrackPackagePath);
        trackFileHash = Convert.ToHexString(SHA256.HashData(track));
        Directory.CreateDirectory(settings.DataDirectory);
        identityPath = Path.Combine(settings.DataDirectory, "room-identity.dat");
        if (settings.Resume)
        {
            identity = PeerVault.Read<RoomIdentity>(identityPath);
            passwordSalt = identity.PasswordSalt;
            passwordHash = identity.PasswordHash;
            if (!MatchesPassword(settings.Password)) throw new InvalidOperationException("恢复房间的密码不正确。");
            if (identity.TrackHash != settings.TrackPackageHash) throw new InvalidOperationException("恢复房间必须使用原赛道版本。");
            certificate = X509CertificateLoader.LoadPkcs12(identity.Certificate, null, X509KeyStorageFlags.UserKeySet);
            identity = identity with { Generation = checked(identity.Generation + 1) };
            PeerVault.Write(identityPath, identity);
        }
        else
        {
            if (File.Exists(identityPath)) throw new IOException("此项目已有房间，请选择恢复或创建新项目。");
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest("CN=LazyForza Peer", key, HashAlgorithmName.SHA256);
            using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(2));
            var pfx = generated.Export(X509ContentType.Pfx);
            // Windows Schannel cannot use ephemeral private keys. UserKeySet creates a user-scoped
            // temporary key container removed on disposal; it does not install a trusted certificate.
            certificate = X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.UserKeySet);
            passwordSalt = RandomNumberGenerator.GetBytes(32);
            passwordHash = DerivePassword(settings.Password);
            identity = new RoomIdentity(Guid.NewGuid(), 1, settings.TrackPackageHash, passwordSalt, passwordHash, pfx);
            PeerVault.Write(identityPath, identity);
        }
        Coordinator = new RaceCoordinator(new RaceServerOptions
        {
            ServerName = settings.RoomName, SessionName = settings.RoomName, PlayerPassword = string.Empty,
            TrackId = settings.TrackId, TrackName = settings.TrackName, TrackRevision = settings.TrackRevision,
            TrackPackageHash = settings.TrackPackageHash, SectorCount = settings.SectorCount,
            TotalRaceLaps = settings.RaceLaps, MinimumRequiredPitStops = 0, AllowTeams = false, DisconnectedLapRecoveryEnabled = true
        }, new PeerPersistence(settings.DataDirectory), MatchesPassword);
        if (settings.Resume) Coordinator.RestorePersistedState();
        Invitation = new PeerInvitation
        {
            RoomId = identity.RoomId, Generation = identity.Generation, ExpiresAt = DateTimeOffset.UtcNow.AddDays(1),
            PublicKeySha256 = Convert.ToHexString(SHA256.HashData(certificate.PublicKey.ExportSubjectPublicKeyInfo())), Candidates = []
        };
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        webControl = await PeerWebControl.StartAsync(settings, Coordinator, webSockets, cancellationToken);
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ContentRootPath = settings.DataDirectory });
        builder.Logging.ClearProviders(); // Never log invites, headers, passwords, or resume tokens.
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            options.Limits.MaxConcurrentConnections = 48;
            options.Limits.MaxConcurrentUpgradedConnections = 32;
            options.Limits.MaxRequestBodySize = 4096;
            options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(5);
            options.ListenAnyIP(settings.Port, listen =>
            {
                listen.Protocols = HttpProtocols.Http1;
                listen.UseHttps(certificate);
            });
        });
        app = builder.Build();
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(15) });
        app.Use(async (context, next) =>
        {
            var suppliedRoom = context.Request.Headers["X-LazyForza-Room"].ToString();
            if (suppliedRoom != Invitation.Header && !(context.Request.Path == "/ws" && IsPreviousInvitation(suppliedRoom)))
            {
                context.Response.StatusCode = 404;
                return;
            }
            context.Response.Headers.CacheControl = "no-store";
            if (context.Request.Path != "/ws" && context.Request.Path != "/peer/identity")
            {
                if (!AllowAttempt(context.Connection.RemoteIpAddress) || !await credentials.WaitAsync(0, context.RequestAborted))
                {
                    context.Response.StatusCode = 429;
                    return;
                }
                try
                {
                    if (!AuthenticateHttp(context))
                    {
                        AllowAttempt(context.Connection.RemoteIpAddress, recordFailure: true);
                        context.Response.StatusCode = 401;
                        return;
                    }
                }
                finally { credentials.Release(); }
            }
            await next(context);
        });
        app.MapGet("/peer/identity", () => Results.Json(new PeerIdentity(Invitation.RoomId, Invitation.Generation, RaceProtocol.CurrentVersion)));
        app.MapGet("/.well-known/lazyforza-race.json", () => Results.Json(Descriptor(), RaceProtocolJson.Options));
        app.MapGet("/peer/track", async (HttpContext context) =>
        {
            if (!await downloads.WaitAsync(0, context.RequestAborted)) { context.Response.StatusCode = 429; return; }
            try
            {
                var currentTrack = await webControl.ReadTrackAsync(context.RequestAborted);
                if (currentTrack is null) { context.Response.StatusCode = 404; return; }
                context.Response.ContentType = "application/octet-stream";
                context.Response.ContentLength = currentTrack.Length;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                for (var offset = 0; offset < currentTrack.Length; offset += 32768)
                {
                    await context.Response.Body.WriteAsync(currentTrack.AsMemory(offset, Math.Min(32768, currentTrack.Length - offset)), timeout.Token);
                    await context.Response.Body.FlushAsync(timeout.Token);
                    await Task.Delay(20, timeout.Token);
                }
            }
            finally { downloads.Release(); }
        });
        app.MapGet("/api/organizer-logo", async (HttpContext context) =>
        {
            var bytes = await webControl.ReadLogoAsync(context.RequestAborted);
            return bytes is null ? Results.NotFound() : Results.Bytes(bytes, webControl.LogoMetadata?.MimeType ?? "application/octet-stream");
        });
        app.Map("/ws", HandleSocketAsync);
        await app.StartAsync(cancellationToken);
        Port = new Uri(app.Urls.Single()).Port;
        if (OperatingSystem.IsWindows() && PeerQuicHost.IsSupported)
            udp = await PeerQuicHost.StartAsync(Port, Port, certificate, cancellationToken);
        var candidates = PeerAddresses.Collect(Port, settings.ExternalAddress, settings.ExternalPort);
        if (candidates.Count == 0) throw new InvalidOperationException("未找到可分享的网络地址，请连接局域网或填写可达 IP。");
        Invitation = new PeerInvitation
        {
            RoomId = Invitation.RoomId, Generation = Invitation.Generation, ExpiresAt = Invitation.ExpiresAt,
            PublicKeySha256 = Invitation.PublicKeySha256, Candidates = candidates, SupportsUdp = udp is not null
        };
        tickTask = Task.Run(TickAsync);
    }

    public PeerHostReply Control(PeerHostCommand command)
    {
        RaceCommandResult result;
        switch (command.Action)
        {
            case "openControl": return webControl is null ? new(false, "总控尚未就绪。") : new(true, ControlUrl: webControl.OpenUrl());
            case "acceptReceipt":
                if (!OperatingSystem.IsWindows() || udp is null) return new(false, "当前系统不支持 UDP 直连。");
                try { udp.Admit(PeerReceipt.Parse(command.Value ?? string.Empty, Invitation)); return new(true); }
                catch (Exception error) when (error is IOException or ArgumentException) { return new(false, error.Message); }
            case "refreshInvitation":
                lock (identitySync)
                {
                    var updated = identity with { Generation = checked(identity.Generation + 1) };
                    PeerVault.Write(identityPath, updated);
                    identity = updated;
                    Invitation = new PeerInvitation
                    {
                        RoomId = identity.RoomId, Generation = identity.Generation, ExpiresAt = DateTimeOffset.UtcNow.AddDays(1),
                        PublicKeySha256 = Invitation.PublicKeySha256, Candidates = PeerAddresses.Collect(Port, settings.ExternalAddress, settings.ExternalPort), SupportsUdp = udp is not null
                    };
                    return new(true, Invitation: Invitation.Encode(), Port: Port);
                }
            case "phase" when Enum.TryParse<RaceSessionPhase>(command.Value, out var phase):
                result = Coordinator.ApplySessionCommand(new RaceAdminSessionCommand(phase, null, null, 5, 10, ForceStart: command.Force));
                break;
            case "flag" when Enum.TryParse<RaceControlFlag>(command.Value, out var flag):
                result = Coordinator.ApplyFlagCommand(new RaceAdminFlagCommand(flag, null));
                break;
            default: return new(false, "不支持的房主管理操作。");
        }
        return new(result.IsAccepted, result.Error);
    }

    private RaceServerDescriptor Descriptor()
    {
        var room = Coordinator.RoomSettings();
        var package = webControl?.TrackMetadata;
        return new(room.SessionName, RaceProtocol.CurrentVersion, RaceProtocol.MaximumParticipants, true, "/ws", string.Empty,
            room.TrackId, room.TrackRevision, Coordinator.Snapshot().Phase, DateTimeOffset.UtcNow,
            room.TrackName, room.TrackPackageHash, AllowTeams: room.AllowTeams, SectorCount: room.SectorCount,
            TrackPackageAvailable: package is not null, TrackPackageSizeBytes: package?.SizeBytes,
            TrackPackageDownloadPath: package is null ? null : "/peer/track", TrackPackageFileSha256: package?.FileSha256);
    }

    private async Task HandleSocketAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 426; return; }
        if (!AllowAttempt(context.Connection.RemoteIpAddress)) { context.Response.StatusCode = 429; return; }
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, lifetime.Token);
        PeerSocket? peer = null;
        Guid? id = null;
        try
        {
            using var loginTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
            loginTimeout.CancelAfter(TimeSpan.FromSeconds(8));
            var envelope = await ReceiveAsync(socket, loginTimeout.Token);
            if (envelope.Type != RaceMessageTypes.Login || envelope.ProtocolVersion != RaceProtocol.CurrentVersion) return;
            var request = RaceProtocolJson.DeserializePayload<RaceLoginRequest>(envelope);
            if (request.Password is not { Length: >= 6 and <= 128 }) return;
            if (Invitation.ExpiresAt <= DateTimeOffset.UtcNow || context.Request.Headers["X-LazyForza-Room"] != Invitation.Header)
            {
                lock (identitySync)
                    if (!(identity.Seats ?? []).Any(seat => seat.Token == request.ResumeToken && seat.Observer == request.IsObserver)) return;
            }
            if (!await credentials.WaitAsync(0, loginTimeout.Token)) return;
            RaceJoinResult joined;
            try { joined = Coordinator.TryJoin(request); }
            finally { credentials.Release(); }
            if (!joined.IsAccepted)
            {
                if (joined.Rejected?.Code == "invalidPassword") AllowAttempt(context.Connection.RemoteIpAddress, recordFailure: true);
                await socket.SendAsync(Message(RaceMessageTypes.LoginRejected, joined.Rejected!), WebSocketMessageType.Text, true, loginTimeout.Token);
                return;
            }
            id = joined.Accepted!.ParticipantId;
            lock (identitySync)
            {
                var seats = (identity.Seats ?? []).Where(seat => seat.ParticipantId != id && seat.Token != request.ResumeToken)
                    .TakeLast(63).Append(new PeerSeat(id.Value, joined.Accepted.ResumeToken, joined.Accepted.IsObserver)).ToArray();
                var updated = identity with { Seats = seats };
                PeerVault.Write(identityPath, updated);
                identity = updated;
            }
            await socket.SendAsync(Message(RaceMessageTypes.LoginAccepted, joined.Accepted), WebSocketMessageType.Text, true, loginTimeout.Token);
            peer = new PeerSocket(socket, cancellation.Token);
            if (clients.TryGetValue(id.Value, out var previous)) previous.Abort();
            clients[id.Value] = peer;
            await webSockets.RegisterAsync(id.Value, socket, cancellation.Token);
            var window = DateTimeOffset.UtcNow;
            var messageCount = 0;
            while (socket.State == WebSocketState.Open && !cancellation.IsCancellationRequested)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
                idle.CancelAfter(TimeSpan.FromSeconds(25));
                envelope = await ReceiveAsync(socket, idle.Token);
                if (!clients.TryGetValue(id.Value, out var current) || current != peer) break;
                if (DateTimeOffset.UtcNow - window > TimeSpan.FromSeconds(1)) { window = DateTimeOffset.UtcNow; messageCount = 0; }
                if (++messageCount > 60 || envelope.ProtocolVersion != RaceProtocol.CurrentVersion) break;
                if (envelope.Type == RaceMessageTypes.Leave)
                {
                    var result = Coordinator.DisconnectAndReleaseClient(id.Value, voluntary: true);
                    if (result.IsAccepted)
                    {
                        lock (identitySync)
                        {
                            var updated = identity with { Seats = (identity.Seats ?? []).Where(seat => seat.ParticipantId != id).ToArray() };
                            PeerVault.Write(identityPath, updated);
                            identity = updated;
                        }
                        await peer.SendAsync(Message(RaceMessageTypes.Left, new { }));
                    }
                    break;
                }
                await HandleMessageAsync(id.Value, joined.Accepted.IsObserver, peer, envelope);
            }
        }
        catch (Exception error) when (error is OperationCanceledException or WebSocketException or JsonException or IOException or InvalidOperationException or ArgumentException)
        {
            socket.Abort();
        }
        finally
        {
            if (id is { } registered) webSockets.Unregister(registered, socket);
            if (id is { } participant && peer is not null && clients.TryRemove(new KeyValuePair<Guid, PeerSocket>(participant, peer)))
                Coordinator.Disconnect(participant);
            else if (id is { } unregistered && peer is null && !clients.ContainsKey(unregistered))
                Coordinator.Disconnect(unregistered);
            if (peer is not null) await peer.DisposeAsync();
        }
    }

    private async Task HandleMessageAsync(Guid id, bool observer, PeerSocket peer, RaceEnvelope envelope)
    {
        if (observer && envelope.Type is not RaceMessageTypes.Ping)
        {
            await peer.SendAsync(Message(RaceMessageTypes.Error, new RaceErrorPayload("observerReadOnly", "OB 不能提交车手操作。")));
            return;
        }
        RaceCommandResult? result = null;
        switch (envelope.Type)
        {
            case RaceMessageTypes.Ping:
                var ping = RaceProtocolJson.DeserializePayload<RaceClockPing>(envelope);
                await peer.SendAsync(Message(RaceMessageTypes.Pong, new RaceClockPong(ping.ClientMonotonicMilliseconds, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())));
                return;
            case RaceMessageTypes.Ready:
                result = Coordinator.SetReady(id, RaceProtocolJson.DeserializePayload<RaceReadyUpdate>(envelope).IsReady);
                break;
            case RaceMessageTypes.Telemetry:
                result = Coordinator.UpdateTelemetry(id, RaceProtocolJson.DeserializePayload<RaceTelemetryUpdate>(envelope));
                break;
            case RaceMessageTypes.LapCompleted:
                var lap = RaceProtocolJson.DeserializePayload<RaceLapCompleted>(envelope);
                result = Coordinator.CompleteLap(id, lap);
                if (result.IsDeferred) break;
                await peer.SendAsync(Message(RaceMessageTypes.LapAcknowledged, new RaceLapAcknowledgement(lap.EventId, result.IsAccepted, result.Error,
                    result.LapValidationStatus ?? (result.IsAccepted ? RaceLapValidationStatus.InsufficientEvidence : RaceLapValidationStatus.Rejected))));
                return;
            case RaceMessageTypes.PitServiceCompleted:
                var pit = RaceProtocolJson.DeserializePayload<RacePitServiceCompleted>(envelope);
                result = Coordinator.CompletePitService(id, pit);
                if (result.IsDeferred) break;
                await peer.SendAsync(Message(RaceMessageTypes.PitServiceAcknowledged, new RacePitServiceAcknowledgement(pit.EventId, result.IsAccepted, result.Error)));
                return;
            default:
                await peer.SendAsync(Message(RaceMessageTypes.Error, new RaceErrorPayload("unsupportedMessage", "不支持此消息。")));
                return;
        }
        if (!result.IsAccepted)
            await peer.SendAsync(Message(RaceMessageTypes.Error, new RaceErrorPayload("commandRejected", result.Error ?? "操作被拒绝。")));
    }

    private async Task TickAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
            while (await timer.WaitForNextTickAsync(lifetime.Token))
            {
                Coordinator.Tick(DateTimeOffset.UtcNow);
                Coordinator.Checkpoint();
                var state = Coordinator.Snapshot();
                var snapshot = Message(RaceMessageTypes.Snapshot, webControl?.WithLogo(state) ?? state);
                foreach (var peer in clients.Values) peer.Snapshot(snapshot);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch
        {
            // A persistence/tick failure cannot leave clients racing against stale authority.
            foreach (var peer in clients.Values) peer.Abort();
            lifetime.Cancel();
            throw;
        }
    }

    private byte[] Message<T>(string type, T value)
    {
        var bytes = RaceProtocolJson.SerializeToUtf8Bytes(type, Interlocked.Increment(ref sequence), value);
        if (bytes.Length > RaceProtocol.MaximumMessageBytes) throw new InvalidDataException("赛事消息超过协议上限。");
        return bytes;
    }

    private static async Task<RaceEnvelope> ReceiveAsync(WebSocket socket, CancellationToken token)
    {
        var bytes = new byte[RaceProtocol.MaximumMessageBytes];
        var count = 0;
        while (true)
        {
            var result = await socket.ReceiveAsync(bytes.AsMemory(count), token);
            if (result.MessageType != WebSocketMessageType.Text) throw new WebSocketException("Expected text message.");
            count += result.Count;
            if (result.EndOfMessage) return RaceProtocolJson.DeserializeEnvelope(bytes.AsSpan(0, count));
            if (count == bytes.Length) throw new InvalidDataException("Message too large.");
        }
    }

    private bool AllowAttempt(IPAddress? source, bool recordFailure = false)
    {
        lock (ingressSync)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var key in attempts.Where(item => now - item.Value.Start > TimeSpan.FromMinutes(1)).Select(item => item.Key).ToArray()) attempts.Remove(key);
            var keyName = source?.ToString() ?? "unknown";
            if (!attempts.TryGetValue(keyName, out var entry))
            {
                if (attempts.Count >= 256) return false;
                entry = (now, 0);
            }
            if (recordFailure) attempts[keyName] = (entry.Start, entry.Count + 1);
            return entry.Count < 20;
        }
    }

    private bool AuthenticateHttp(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Basic ", StringComparison.Ordinal) || header.Length > 768) return false;
        try
        {
            var value = Encoding.UTF8.GetString(Convert.FromBase64String(header[6..]));
            return value.StartsWith("peer:", StringComparison.Ordinal) && MatchesPassword(value[5..]);
        }
        catch (FormatException) { return false; }
    }

    private byte[] DerivePassword(string password) => Rfc2898DeriveBytes.Pbkdf2(password, passwordSalt, 100_000, HashAlgorithmName.SHA256, 32);
    private bool MatchesPassword(string password) => password.Length <= 128 && CryptographicOperations.FixedTimeEquals(passwordHash, DerivePassword(password));

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        await lifetime.CancelAsync();
        foreach (var client in clients.Values) client.Abort();
        try { if (tickTask is not null) await tickTask; }
        finally
        {
            if (OperatingSystem.IsWindows() && udp is not null) await udp.DisposeAsync();
            if (webControl is not null) await webControl.DisposeAsync();
            if (app is not null)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try { await app.StopAsync(timeout.Token); }
                finally { await app.DisposeAsync(); }
            }
            certificate.Dispose();
            lifetime.Dispose();
            downloads.Dispose();
            credentials.Dispose();
        }
    }

    private bool IsPreviousInvitation(string header)
    {
        var prefix = $"{Invitation.RoomId:N}:";
        return header.StartsWith(prefix, StringComparison.Ordinal) && int.TryParse(header[prefix.Length..], out var generation) && generation > 0 && generation < Invitation.Generation;
    }

    private sealed record PeerSeat(Guid ParticipantId, string Token, bool Observer);
    private sealed record RoomIdentity(Guid RoomId, int Generation, string TrackHash, byte[] PasswordSalt, byte[] PasswordHash, byte[] Certificate, PeerSeat[]? Seats = null);
}
