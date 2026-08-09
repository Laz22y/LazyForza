using System.Buffers;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using LazyForza.Domain;
using LazyForza.Modules.Abstractions;

namespace LazyForza.Modules.EstateRace;

public sealed partial class EstateRaceModule : LazyForzaModuleBase, IHudContribution
{
    public const string ModuleId = "estate-race";
    private const string ServerAddressSetting = "serverAddress";
    private const string DisplayNameSetting = "displayName";
    private const string ThemeColorSetting = "themeColor";
    private const string TeamNameSetting = "teamName";
    private const string TeamIdSetting = "teamId";
    private const string ResumeTokenSetting = "resumeToken";
    private static readonly TimeSpan TelemetryInterval = TimeSpan.FromMilliseconds(100);
    private readonly Func<EstateRaceTrackContext?> trackContext;
    private readonly Action<Guid, bool, bool>? timingControl;
    private readonly SemaphoreSlim connectionLock = new(1, 1);
    private readonly SemaphoreSlim sendLock = new(1, 1);
    private readonly EstateRaceGripEstimator gripEstimator = new();
    private readonly EstatePitServiceTracker pitServiceTracker = new();
    private readonly Stopwatch monotonicClock = Stopwatch.StartNew();
    private ITelemetrySubscription? subscription;
    private CancellationTokenSource? runCancellation;
    private CancellationTokenSource? connectionCancellation;
    private CancellationTokenSource? reconnectCancellation;
    private Task? telemetryTask;
    private Task? receiveTask;
    private Task? reconnectTask;
    private ClientWebSocket? socket;
    private EstateRaceConnectionProfile? activeProfile;
    private EstateRaceHudState snapshot = EmptySnapshot();
    private Guid? participantId;
    private string? resumeToken;
    private EstateRaceSession? session;
    private Guid? sentLapEventId;
    private long sequence;
    private DateTimeOffset lastTelemetrySentAt;
    private RaceSessionPhase? lastSessionPhase;
    private EstateRaceProjection? lastValidProjection;
    private bool intentionalDisconnect;
    private bool raceTimingEnabled;
    private bool raceTimingInvalidatesLapOnDriverIntervention = true;
    private int reconnectLoopActive;

    public EstateRaceModule(
        Func<EstateRaceTrackContext?> trackContext,
        Action<Guid, bool, bool>? timingControl = null)
        : base(new ModuleDescriptor(
            ModuleId,
            "地产赛事",
            "连接自托管赛事服务，显示多车排名、位置、旗语和处罚。",
            [],
            "estate-race",
            "estate-race",
            true,
            DefaultEnabled: true))
    {
        this.trackContext = trackContext;
        this.timingControl = timingControl;
    }

    public static int ProtocolVersion => EstateRaceWireProtocol.Version;

    public string Id => "hud.estate-race";
    public HudContributionKind Kind => HudContributionKind.EstateRace;
    public int ZIndex => 30;
    public object Snapshot => Volatile.Read(ref snapshot);
    public EstateRaceHudState State => Volatile.Read(ref snapshot);
    public EstateRaceConnectionProfile? ActiveProfile
    {
        get
        {
            var profile = Volatile.Read(ref activeProfile);
            return profile is null ? null : profile with { Password = string.Empty };
        }
    }

    public async Task<EstateRaceConnectionProfile> LoadSavedProfileAsync(
        CancellationToken cancellationToken)
    {
        var address = await Context.Settings.GetAsync(ModuleId, ServerAddressSetting, cancellationToken).ConfigureAwait(false);
        var name = await Context.Settings.GetAsync(ModuleId, DisplayNameSetting, cancellationToken).ConfigureAwait(false);
        var color = await Context.Settings.GetAsync(ModuleId, ThemeColorSetting, cancellationToken).ConfigureAwait(false);
        var team = await Context.Settings.GetAsync(ModuleId, TeamNameSetting, cancellationToken).ConfigureAwait(false);
        var teamId = await Context.Settings.GetAsync(ModuleId, TeamIdSetting, cancellationToken).ConfigureAwait(false);
        resumeToken = await Context.Settings.GetAsync(ModuleId, ResumeTokenSetting, cancellationToken).ConfigureAwait(false);
        return new EstateRaceConnectionProfile(
            address ?? "http://127.0.0.1:24876",
            string.Empty,
            name ?? Environment.UserName,
            NormalizeColor(color),
            NullIfWhiteSpace(team),
            NullIfWhiteSpace(teamId));
    }

    public async Task ConnectAsync(
        EstateRaceConnectionProfile profile,
        CancellationToken cancellationToken)
    {
        await CancelReconnectAsync().ConfigureAwait(false);
        if (!await ConnectOnceAsync(profile, cancellationToken, isReconnectAttempt: false).ConfigureAwait(false))
            activeProfile = null;
    }

    public static async Task<EstateRaceServerDescriptor> ReadServerDescriptorAsync(
        string serverAddress,
        CancellationToken cancellationToken)
    {
        var websocket = ServerWebSocketUri(serverAddress);
        var builder = new UriBuilder(websocket)
        {
            Scheme = websocket.Scheme == "wss" ? "https" : "http",
            Path = "/.well-known/lazyforza-race.json",
            Query = string.Empty
        };
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        await using var stream = await client.GetStreamAsync(builder.Uri, cancellationToken).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<EstateRaceServerDescriptor>(
                   stream,
                   EstateRaceWireProtocol.JsonOptions,
                   cancellationToken).ConfigureAwait(false) ??
               throw new InvalidOperationException("服务端没有返回有效的房间信息。");
    }

    private async Task<bool> ConnectOnceAsync(
        EstateRaceConnectionProfile profile,
        CancellationToken cancellationToken,
        bool isReconnectAttempt)
    {
        await connectionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Status.State != ModuleRuntimeState.Running)
                throw new InvalidOperationException("请先启用地产赛事模块。");
            ValidateProfile(profile);
            await DisconnectCoreAsync().ConfigureAwait(false);
            intentionalDisconnect = false;
            activeProfile = profile with
            {
                ServerAddress = profile.ServerAddress.Trim(),
                DisplayName = profile.DisplayName.Trim(),
                ThemeColor = NormalizeColor(profile.ThemeColor),
                TeamName = NullIfWhiteSpace(profile.TeamName)
            };
            SetConnectionState(EstateRaceConnectionState.Connecting, "正在连接赛事服务…");
            connectionCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                runCancellation?.Token ?? CancellationToken.None,
                cancellationToken);
            socket = new ClientWebSocket();
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(connectionCancellation.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(12));
            await socket.ConnectAsync(ServerWebSocketUri(activeProfile.ServerAddress), timeout.Token).ConfigureAwait(false);

            var context = trackContext();
            await SendAsync("login", new RaceLoginRequest(
                activeProfile.Password,
                activeProfile.DisplayName,
                activeProfile.ThemeColor,
                activeProfile.TeamName,
                typeof(EstateRaceModule).Assembly.GetName().Version?.ToString(3) ?? "development",
                resumeToken,
                context?.Definition.TrackId.ToString("D"),
                context?.Definition.MapRevision,
                context?.TrackPackageHash,
                context?.SectorCount,
                activeProfile.TeamId), timeout.Token).ConfigureAwait(false);

            var loginEnvelope = await ReceiveEnvelopeAsync(socket, timeout.Token).ConfigureAwait(false);
            if (loginEnvelope.ProtocolVersion != EstateRaceWireProtocol.Version)
                throw new InvalidOperationException("服务端协议版本与当前 LazyForza 不兼容。");
            if (loginEnvelope.Type == "loginRejected")
            {
                var rejected = loginEnvelope.Payload.Deserialize<RaceLoginRejected>(EstateRaceWireProtocol.JsonOptions);
                SetConnectionState(EstateRaceConnectionState.Rejected, rejected?.Message ?? "赛事服务拒绝登录。");
                await DisconnectCoreAsync().ConfigureAwait(false);
                return false;
            }
            if (loginEnvelope.Type != "loginAccepted")
                throw new InvalidOperationException("赛事服务未返回有效的登录确认。");
            var accepted = loginEnvelope.Payload.Deserialize<RaceLoginAccepted>(EstateRaceWireProtocol.JsonOptions) ??
                           throw new InvalidOperationException("赛事服务返回的登录数据无效。");
            participantId = accepted.ParticipantId;
            resumeToken = accepted.ResumeToken;
            ApplySessionSnapshot(NormalizeSession(accepted.Snapshot), resetForConnection: !isReconnectAttempt);
            await SaveProfileAsync(activeProfile, accepted.ResumeToken, cancellationToken).ConfigureAwait(false);
            PublishSnapshot(EstateRaceConnectionState.Connected, "已连接赛事服务");
            var connectedSocket = socket;
            var connectedCancellation = connectionCancellation;
            receiveTask = Task.Run(
                () => ReceiveLoopAsync(connectedSocket, connectedCancellation.Token),
                CancellationToken.None);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await DisconnectCoreAsync().ConfigureAwait(false);
            throw;
        }
        catch (Exception exception)
        {
            SetConnectionState(EstateRaceConnectionState.Faulted, $"连接失败：{exception.Message}");
            await DisconnectCoreAsync().ConfigureAwait(false);
            LogIfInitialized($"Estate race connection failed: {exception}");
            return false;
        }
        finally
        {
            connectionLock.Release();
        }
    }

    public async Task DisconnectAsync()
    {
        await CancelReconnectAsync().ConfigureAwait(false);
        await connectionLock.WaitAsync().ConfigureAwait(false);
        try
        {
            intentionalDisconnect = true;
            await DisconnectCoreAsync().ConfigureAwait(false);
            activeProfile = null;
            lastSessionPhase = null;
            sentLapEventId = null;
            SetRaceTimingEnabled(false);
            SetConnectionState(EstateRaceConnectionState.Disconnected, "未连接赛事服务");
        }
        finally
        {
            connectionLock.Release();
        }
    }

    public Task SetReadyAsync(bool isReady, CancellationToken cancellationToken) =>
        SendAsync("ready", new RaceReadyUpdate(isReady), cancellationToken);

    protected override async ValueTask OnStartAsync(CancellationToken cancellationToken)
    {
        runCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        subscription = await Context.Telemetry.SubscribeAsync(ModuleId, runCancellation.Token).ConfigureAwait(false);
        await Context.Hud.AttachAsync(this, cancellationToken).ConfigureAwait(false);
        telemetryTask = Task.Run(
            () => ConsumeTelemetryAsync(subscription.Frames, runCancellation.Token),
            CancellationToken.None);
        PublishSnapshot(EstateRaceConnectionState.Disconnected, "未连接赛事服务");
    }

    protected override async ValueTask OnStopAsync(CancellationToken cancellationToken)
    {
        intentionalDisconnect = true;
        runCancellation?.Cancel();
        await CancelReconnectAsync().ConfigureAwait(false);
        await DisconnectCoreAsync().ConfigureAwait(false);
        if (subscription is not null) await subscription.DisposeAsync().ConfigureAwait(false);
        if (telemetryTask is not null)
        {
            try { await telemetryTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        await Context.Hud.DetachAsync(Id, cancellationToken).ConfigureAwait(false);
        subscription = null;
        telemetryTask = null;
        runCancellation?.Dispose();
        runCancellation = null;
        gripEstimator.Reset();
        pitServiceTracker.Reset();
        participantId = null;
        session = null;
        activeProfile = null;
        lastSessionPhase = null;
        lastValidProjection = null;
        sentLapEventId = null;
        SetRaceTimingEnabled(false);
        Volatile.Write(ref snapshot, EmptySnapshot());
    }

    private async Task ConsumeTelemetryAsync(
        System.Threading.Channels.ChannelReader<TelemetryFrame> frames,
        CancellationToken cancellationToken)
    {
        await foreach (var frame in frames.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            if (socket?.State != WebSocketState.Open) continue;
            var context = trackContext();
            if (context is null) continue;
            var valid = IsTelemetryValid(frame, out var pausedOrRewinding);
            var positionReliable = valid;
            var completedLaps = context.CompletedLaps;
            gripEstimator.Observe(frame, completedLaps, positionReliable);
            var localParticipant = session?.Participants.FirstOrDefault(candidate => candidate.Id == participantId);
            var serviceBlocked = localParticipant is
            {
                PendingTimePenaltySeconds: > 0
            } or { IsServingTimePenalty: true };
            var pitService = pitServiceTracker.Observe(
                frame,
                context.Definition.Pit,
                positionReliable,
                serviceBlocked);
            if (frame.ArrivalTime - lastTelemetrySentAt < TelemetryInterval) continue;
            lastTelemetrySentAt = frame.ArrivalTime;
            var projection = positionReliable
                ? EstateRaceGeometry.Project(context.Track, frame.Raw.Position)
                : lastValidProjection ?? new EstateRaceProjection(0, 0, 0.5, 0.5);
            if (positionReliable) lastValidProjection = projection;
            var update = new RaceTelemetryUpdate(
                monotonicClock.ElapsedMilliseconds,
                projection.Progress,
                projection.LateralOffsetMeters,
                projection.MapX,
                projection.MapY,
                frame.Normalized.SpeedKph,
                context.CompletedLaps,
                context.CurrentSector,
                context.CurrentLapSeconds,
                pitService.IsInPitLane,
                pitService.IsInServiceZone,
                positionReliable,
                pausedOrRewinding,
                gripEstimator.Current,
                pitService.ElapsedSeconds,
                pitService.RequirementMet,
                pitService.CompletedServices,
                context.Track.MatchingToleranceMeters,
                context.Track.LengthMeters,
                context.Definition.Pit?.SpeedLimitKph ?? 0,
                pitService.PitLaneElapsedSeconds,
                pitService.IsApproachingPit);
            try
            {
                await SendAsync("telemetry", update, cancellationToken).ConfigureAwait(false);
                if (context.LastCompletedLap is { } lap && lap.EventId != sentLapEventId)
                {
                    await SendAsync("lapCompleted", new RaceLapCompleted(
                        lap.EventId,
                        lap.LapNumber,
                        lap.LapSeconds,
                        lap.SectorSeconds,
                        lap.IsValid,
                        lap.InvalidReason,
                        monotonicClock.ElapsedMilliseconds,
                        lap.IsBestLapEligible), cancellationToken).ConfigureAwait(false);
                    sentLapEventId = lap.EventId;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                LogIfInitialized($"Estate race telemetry send failed: {exception.Message}");
            }
        }
    }

    private async Task ReceiveLoopAsync(
        ClientWebSocket activeSocket,
        CancellationToken cancellationToken)
    {
        try
        {
            while (activeSocket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                var envelope = await ReceiveEnvelopeAsync(activeSocket, cancellationToken).ConfigureAwait(false);
                if (envelope.ProtocolVersion != EstateRaceWireProtocol.Version) continue;
                if (envelope.Type == "snapshot")
                {
                    var received = envelope.Payload.Deserialize<EstateRaceSession>(EstateRaceWireProtocol.JsonOptions);
                    if (received is not null) ApplySessionSnapshot(NormalizeSession(received));
                    PublishSnapshot(EstateRaceConnectionState.Connected, "已连接赛事服务");
                }
                else if (envelope.Type == "error")
                {
                    var error = envelope.Payload.Deserialize<RaceProtocolError>(EstateRaceWireProtocol.JsonOptions);
                    if (error is not null) PublishSnapshot(EstateRaceConnectionState.Connected, error.Message);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (WebSocketException exception)
        {
            if (!intentionalDisconnect)
            {
                SetConnectionState(EstateRaceConnectionState.Reconnecting, "连接中断，正在尝试恢复赛事连接…");
                LogIfInitialized($"Estate race WebSocket disconnected: {exception.Message}");
                ScheduleReconnect();
            }
        }
        catch (JsonException exception)
        {
            SetConnectionState(EstateRaceConnectionState.Faulted, "服务端返回了无法解析的数据");
            LogIfInitialized($"Estate race WebSocket message invalid: {exception.Message}");
        }
    }

    private void ScheduleReconnect()
    {
        if (intentionalDisconnect || activeProfile is null || runCancellation?.IsCancellationRequested != false)
            return;
        if (Interlocked.CompareExchange(ref reconnectLoopActive, 1, 0) != 0)
            return;

        var profile = activeProfile;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(runCancellation.Token);
        reconnectCancellation = cancellation;
        reconnectTask = Task.Run(async () =>
        {
            var attempt = 0;
            try
            {
                while (!cancellation.IsCancellationRequested && !intentionalDisconnect)
                {
                    attempt++;
                    var delay = TimeSpan.FromSeconds(Math.Min(10, Math.Pow(2, Math.Min(attempt - 1, 3))));
                    SetConnectionState(
                        EstateRaceConnectionState.Reconnecting,
                        $"连接中断，{delay.TotalSeconds:0} 秒后进行第 {attempt} 次重连…");
                    await Task.Delay(delay, cancellation.Token).ConfigureAwait(false);
                    if (await ConnectOnceAsync(profile, cancellation.Token, isReconnectAttempt: true).ConfigureAwait(false))
                    {
                        LogIfInitialized($"Estate race connection resumed after {attempt} attempt(s).");
                        return;
                    }
                    if (State.ConnectionState == EstateRaceConnectionState.Rejected)
                    {
                        activeProfile = null;
                        return;
                    }
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            finally
            {
                if (ReferenceEquals(reconnectCancellation, cancellation))
                {
                    reconnectCancellation = null;
                    reconnectTask = null;
                }
                cancellation.Dispose();
                Interlocked.Exchange(ref reconnectLoopActive, 0);
            }
        }, CancellationToken.None);
    }

    private async Task CancelReconnectAsync()
    {
        var cancellation = reconnectCancellation;
        var task = reconnectTask;
        cancellation?.Cancel();
        if (task is not null && task.Id != Task.CurrentId)
        {
            try { await task.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        if (ReferenceEquals(reconnectCancellation, cancellation))
        {
            reconnectCancellation = null;
            reconnectTask = null;
            cancellation?.Dispose();
            Interlocked.Exchange(ref reconnectLoopActive, 0);
        }
    }

    private async Task SendAsync<T>(
        string type,
        T payload,
        CancellationToken cancellationToken)
    {
        var activeSocket = socket;
        if (activeSocket?.State != WebSocketState.Open)
            throw new InvalidOperationException("赛事服务尚未连接。");
        var bytes = EstateRaceWireProtocol.Serialize(type, Interlocked.Increment(ref sequence), payload);
        await sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await activeSocket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            sendLock.Release();
        }
    }

    private static async Task<RaceIncomingEnvelope> ReceiveEnvelopeAsync(
        ClientWebSocket activeSocket,
        CancellationToken cancellationToken)
    {
        var writer = new ArrayBufferWriter<byte>();
        var buffer = ArrayPool<byte>.Shared.Rent(4096);
        try
        {
            while (true)
            {
                var received = await activeSocket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (received.MessageType == WebSocketMessageType.Close)
                    throw new WebSocketException("赛事服务已关闭连接。");
                if (received.MessageType != WebSocketMessageType.Text)
                    throw new JsonException("赛事服务返回了非文本消息。");
                if (writer.WrittenCount + received.Count > EstateRaceWireProtocol.MaximumMessageBytes)
                    throw new JsonException("赛事服务消息超过大小限制。");
                writer.Write(buffer.AsSpan(0, received.Count));
                if (received.EndOfMessage) break;
            }
            return JsonSerializer.Deserialize<RaceIncomingEnvelope>(
                       writer.WrittenSpan,
                       EstateRaceWireProtocol.JsonOptions) ??
                   throw new JsonException("赛事服务消息为空。");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task DisconnectCoreAsync()
    {
        connectionCancellation?.Cancel();
        var activeSocket = socket;
        socket = null;
        if (activeSocket is not null)
        {
            try
            {
                if (activeSocket.State == WebSocketState.Open)
                    await activeSocket.CloseOutputAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "client disconnect",
                        CancellationToken.None).ConfigureAwait(false);
            }
            catch (WebSocketException) { }
            activeSocket.Dispose();
        }
        var currentReceive = receiveTask;
        receiveTask = null;
        if (currentReceive is not null)
        {
            try { await currentReceive.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (WebSocketException) { }
        }
        connectionCancellation?.Dispose();
        connectionCancellation = null;
        participantId = null;
        session = null;
        lastValidProjection = null;
        SetRaceTimingEnabled(false);
    }

    private static bool IsTelemetryValid(TelemetryFrame frame, out bool pausedOrRewinding)
    {
        pausedOrRewinding = TelemetryContextClassifier.IsDriverIntervention(frame.Raw);
        return !pausedOrRewinding &&
               float.IsFinite(frame.Raw.Position.X) &&
               float.IsFinite(frame.Raw.Position.Y) &&
               float.IsFinite(frame.Raw.Position.Z);
    }

    private void PublishSnapshot(EstateRaceConnectionState state, string text)
    {
        var context = trackContext();
        var outline = context is null
            ? Array.Empty<EstateRaceMapPoint>()
            : EstateRaceGeometry.NormalizeOutline(context.Track);
        Volatile.Write(ref snapshot, new EstateRaceHudState(
            DateTimeOffset.UtcNow,
            state,
            text,
            participantId,
            session,
            outline,
            gripEstimator.Current,
            GripExplanation(gripEstimator.Current),
            pitServiceTracker.Current));
    }

    private void SetConnectionState(EstateRaceConnectionState state, string text) =>
        PublishSnapshot(state, text);

    private async Task SaveProfileAsync(
        EstateRaceConnectionProfile profile,
        string token,
        CancellationToken cancellationToken)
    {
        await Context.Settings.SetAsync(ModuleId, ServerAddressSetting, profile.ServerAddress, cancellationToken).ConfigureAwait(false);
        await Context.Settings.SetAsync(ModuleId, DisplayNameSetting, profile.DisplayName, cancellationToken).ConfigureAwait(false);
        await Context.Settings.SetAsync(ModuleId, ThemeColorSetting, profile.ThemeColor, cancellationToken).ConfigureAwait(false);
        await Context.Settings.SetAsync(ModuleId, TeamNameSetting, profile.TeamName ?? string.Empty, cancellationToken).ConfigureAwait(false);
        await Context.Settings.SetAsync(ModuleId, TeamIdSetting, profile.TeamId ?? string.Empty, cancellationToken).ConfigureAwait(false);
        await Context.Settings.SetAsync(ModuleId, ResumeTokenSetting, token, cancellationToken).ConfigureAwait(false);
    }

    internal static Uri ServerWebSocketUri(string value)
    {
        var normalized = value.Trim();
        if (!normalized.Contains("://", StringComparison.Ordinal))
            normalized = "http://" + normalized;
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https" or "ws" or "wss"))
            throw new ArgumentException("服务端地址无效。请输入域名或 IP，可包含端口。", nameof(value));
        var builder = new UriBuilder(uri)
        {
            Scheme = uri.Scheme switch { "https" => "wss", "http" => "ws", _ => uri.Scheme },
            Path = string.IsNullOrWhiteSpace(uri.AbsolutePath) || uri.AbsolutePath == "/"
                ? "/ws"
                : uri.AbsolutePath
        };
        return builder.Uri;
    }

    private static void ValidateProfile(EstateRaceConnectionProfile profile)
    {
        _ = ServerWebSocketUri(profile.ServerAddress);
        if (profile.Password.Length > 128)
            throw new ArgumentException("赛事密码不能超过 128 个字符。", nameof(profile));
        if (profile.DisplayName.Trim().Length is < 2 or > 20)
            throw new ArgumentException("比赛显示名需要 2–20 个字符。", nameof(profile));
        if (!ThemeColorPattern().IsMatch(profile.ThemeColor.Trim()))
            throw new ArgumentException("主题色必须使用 #RRGGBB 格式。", nameof(profile));
        if (profile.TeamName?.Trim().Length > 24)
            throw new ArgumentException("车队名称不能超过 24 个字符。", nameof(profile));
    }

    private static string NormalizeColor(string? value) =>
        ThemeColorPattern().IsMatch(value?.Trim() ?? string.Empty)
            ? value!.Trim().ToUpperInvariant()
            : "#42D7E8";

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static EstateRaceSession NormalizeSession(EstateRaceSession value)
    {
        var participants = (value.Participants ?? [])
            .Take(12)
            .Select(participant => participant with
            {
                BestSectorSeconds = participant.BestSectorSeconds ?? [],
                Penalties = participant.Penalties ?? []
            })
            .ToArray();
        return value with
        {
            FastestSectorSeconds = value.FastestSectorSeconds ?? [],
            FastestLapSectorSeconds = value.FastestLapSectorSeconds ?? [],
            Participants = participants,
            YellowZones = value.YellowZones ?? []
            ,BlueFlags = value.BlueFlags ?? []
        };
    }

    private void ApplySessionSnapshot(
        EstateRaceSession value,
        bool resetForConnection = false)
    {
        var phaseBoundary = lastSessionPhase != value.Phase &&
                            value.Phase is RaceSessionPhase.Lobby or
                                RaceSessionPhase.Qualifying or RaceSessionPhase.Race;
        if (resetForConnection || phaseBoundary)
        {
            pitServiceTracker.Reset();
            gripEstimator.Reset();
            if (resetForConnection) lastValidProjection = null;
            sentLapEventId = trackContext()?.LastCompletedLap?.EventId;
        }
        lastSessionPhase = value.Phase;
        session = value;
        SetRaceTimingEnabled(ShouldEnableRaceTiming(value, participantId));
    }

    internal static bool ShouldEnableRaceTiming(EstateRaceSession value, Guid? localParticipantId)
    {
        if (value.Phase == RaceSessionPhase.Race) return true;
        if (value.Phase != RaceSessionPhase.Qualifying) return false;
        if (!value.QualifyingTimeExpired) return true;
        return localParticipantId is Guid localId &&
               value.Participants.FirstOrDefault(participant => participant.Id == localId)?.QualifyingFinalLapPending == true;
    }

    private void SetRaceTimingEnabled(bool enabled)
    {
        var context = trackContext();
        if (context is null) return;
        var invalidateLapOnDriverIntervention = ShouldInvalidateLapOnDriverIntervention(session);
        if (raceTimingEnabled == enabled && context.IsTimingActive == enabled &&
            raceTimingInvalidatesLapOnDriverIntervention == invalidateLapOnDriverIntervention) return;
        timingControl?.Invoke(context.Definition.TrackId, enabled, invalidateLapOnDriverIntervention);
        raceTimingEnabled = enabled;
        raceTimingInvalidatesLapOnDriverIntervention = invalidateLapOnDriverIntervention;
        if (!enabled) sentLapEventId = context.LastCompletedLap?.EventId;
    }

    internal static bool ShouldInvalidateLapOnDriverIntervention(EstateRaceSession? value) =>
        value?.Phase != RaceSessionPhase.Race &&
        (value?.Phase != RaceSessionPhase.Suspended || value.SuspendedFromPhase != RaceSessionPhase.Race);

    private static string GripExplanation(RaceGripCondition condition) => condition switch
    {
        RaceGripCondition.SlightlyReduced => "相比前三圈基准略有下降",
        RaceGripCondition.ModeratelyReduced => "相比前三圈基准中度下降",
        RaceGripCondition.SeverelyReduced => "相比前三圈基准明显下降",
        RaceGripCondition.AtLimit => "相比前三圈基准大幅下降",
        _ => "前三个有效完整圈用于建立基准"
    };

    private static EstateRaceHudState EmptySnapshot() => new(
        DateTimeOffset.UtcNow,
        EstateRaceConnectionState.Disconnected,
        "未连接赛事服务",
        null,
        null,
        [],
        RaceGripCondition.Unknown,
        "样本不足，暂不判断",
        EstatePitServiceState.Empty);

    [GeneratedRegex("^#[0-9A-Fa-f]{6}$", RegexOptions.CultureInvariant)]
    private static partial Regex ThemeColorPattern();
}
