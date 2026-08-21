using System.Buffers;
using System.Diagnostics;
using System.Net.Http;
using System.Net.WebSockets;
using System.Security.Cryptography;
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
    private const string ObserverResumeTokenSetting = "observerResumeToken";
    private const string ConnectionRoleSetting = "connectionRole";
    private const int MaximumOrganizerLogoBytes = 262_144;
    private static readonly TimeSpan TelemetryInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan TelemetrySendTimeout = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan CommandSendTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RecoveredLapRetryInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan FingerprintRefreshInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan BackgroundFailureLogInterval = TimeSpan.FromSeconds(10);
    private readonly Func<EstateRaceTrackContext?> trackContext;
    private readonly Action<Guid, bool, bool>? timingControl;
    private readonly Func<VehicleProfileFingerprint?>? vehicleFingerprint;
    private readonly Func<EstateStrategyTrackIdentity, IReadOnlyList<EstateStrategySample>>? strategySampleLoader;
    private readonly Action<EstateStrategySample>? strategySampleSaver;
    private readonly object strategySync = new();
    private readonly object telemetryStateSync = new();
    private readonly object lapRecoverySync = new();
    private readonly EstateCollisionEvidenceDetector collisionEvidenceDetector = new();
    private readonly EstateShortcutDetector shortcutDetector = new();
    private readonly object trackMapCacheSync = new();
    private readonly SemaphoreSlim connectionLock = new(1, 1);
    private readonly SemaphoreSlim sendLock = new(1, 1);
    private readonly EstateRaceGripEstimator gripEstimator = new();
    private readonly EstatePitServiceTracker pitServiceTracker = new();
    private readonly EstatePitStrategyPredictor pitStrategyPredictor = new();
    private readonly EstatePracticeTestManager practiceTestManager = new();
    private readonly Stopwatch monotonicClock = Stopwatch.StartNew();
    private ITelemetrySubscription? subscription;
    private CancellationTokenSource? runCancellation;
    private CancellationTokenSource? connectionCancellation;
    private CancellationTokenSource? reconnectCancellation;
    private Task? telemetryTask;
    private Task? receiveTask;
    private Task? heartbeatTask;
    private Task? reconnectTask;
    private ClientWebSocket? socket;
    private EstateRaceConnectionProfile? activeProfile;
    private EstateRaceHudState snapshot = EmptySnapshot();
    private Guid? participantId;
    private string? resumeToken;
    private string? driverResumeToken;
    private string? observerResumeToken;
    private bool connectionIsObserver;
    private bool connectionAuthenticated;
    private EstateRaceSession? session;
    private Guid? sentLapEventId;
    private readonly List<PendingLapUpload> pendingLapUploads = [];
    private long sequence;
    private DateTimeOffset lastTelemetrySentAt;
    private long lastTelemetrySentMonotonicMilliseconds;
    private RaceSessionPhase? lastSessionPhase;
    private int lastQualifyingSessionNumber;
    private int lastPracticeSessionNumber;
    private EstateRaceProjection? lastValidProjection;
    private bool intentionalDisconnect;
    private bool raceTimingEnabled;
    private bool raceTimingInvalidatesLapOnDriverIntervention = true;
    private int reconnectLoopActive;
    private EstateRaceOrganizerLogo? organizerLogo;
    private string? failedOrganizerLogoHash;
    private DateTimeOffset organizerLogoRetryAfter;
    private string? requestedTrackPackageHash;
    private string? loadedStrategyTrackKey;
    private VehicleProfileFingerprint? observedVehicleFingerprint;
    private VehicleProfileFingerprint? learnedVehicleFingerprint;
    private DateTimeOffset nextFingerprintRefreshAt;
    private DateTimeOffset lastStrategyFailureAt;
    private string? lastStrategyFailure;
    private DateTimeOffset lastTelemetryFailureAt;
    private string? lastTelemetryFailure;
    private DateTimeOffset lastSnapshotFailureAt;
    private string? lastSnapshotFailure;
    private long estimatedOneWayLatencyTicks;
    private long estimatedRoundTripLatencyTicks;
    private long networkJitterTicks;
    private long serverClockOffsetTicks;
    private long lastServerResponseUtcTicks;
    private int hasServerClockEstimate;
    private EstateRaceTrackMapCacheKey? trackMapCacheKey;
    private IReadOnlyList<EstateRaceMapPoint> cachedTrackOutline = [];
    private IReadOnlyList<EstateRaceMapPoint> cachedPitOutline = [];
    private EstateRaceMapGate? cachedStartFinishGate;
    private IReadOnlyList<EstateRaceMapSector> cachedTrackSectors = [];

    public EstateRaceModule(
        Func<EstateRaceTrackContext?> trackContext,
        Action<Guid, bool, bool>? timingControl = null,
        Func<VehicleProfileFingerprint?>? vehicleFingerprint = null,
        Func<EstateStrategyTrackIdentity, IReadOnlyList<EstateStrategySample>>? strategySampleLoader = null,
        Action<EstateStrategySample>? strategySampleSaver = null)
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
        this.vehicleFingerprint = vehicleFingerprint;
        this.strategySampleLoader = strategySampleLoader;
        this.strategySampleSaver = strategySampleSaver;
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

    public void StartPracticeTest(EstatePracticeTestKind kind)
    {
        lock (strategySync)
        {
            var currentSession = session ?? throw new InvalidOperationException("尚未进入赛事房间。 ");
            var context = trackContext() ?? throw new InvalidOperationException("尚未载入服务端指定的地产环道。 ");
            if (connectionIsObserver)
                throw new InvalidOperationException("OB 不参与练习测试。 ");
            var local = participantId is Guid localId
                ? currentSession.Participants.FirstOrDefault(participant => participant.Id == localId)
                : null;
            if (local is null) throw new InvalidOperationException("没有找到本机参赛车手。 ");
            practiceTestManager.Start(
                kind,
                currentSession,
                local,
                context,
                CurrentVehicleFingerprint(),
                pitServiceTracker.Current);
        }
        PublishSnapshot(EstateRaceConnectionState.Connected, "已连接赛事服务");
    }

    public void StopPracticeTest()
    {
        lock (strategySync) practiceTestManager.Stop();
        PublishSnapshot(EstateRaceConnectionState.Connected, "已连接赛事服务");
    }

    public async Task<EstateRaceConnectionProfile> LoadSavedProfileAsync(
        CancellationToken cancellationToken)
    {
        var address = await Context.Settings.GetAsync(ModuleId, ServerAddressSetting, cancellationToken).ConfigureAwait(false);
        var name = await Context.Settings.GetAsync(ModuleId, DisplayNameSetting, cancellationToken).ConfigureAwait(false);
        var color = await Context.Settings.GetAsync(ModuleId, ThemeColorSetting, cancellationToken).ConfigureAwait(false);
        var team = await Context.Settings.GetAsync(ModuleId, TeamNameSetting, cancellationToken).ConfigureAwait(false);
        var teamId = await Context.Settings.GetAsync(ModuleId, TeamIdSetting, cancellationToken).ConfigureAwait(false);
        driverResumeToken = await Context.Settings.GetAsync(ModuleId, ResumeTokenSetting, cancellationToken).ConfigureAwait(false);
        observerResumeToken = await Context.Settings.GetAsync(ModuleId, ObserverResumeTokenSetting, cancellationToken).ConfigureAwait(false);
        var role = await Context.Settings.GetAsync(ModuleId, ConnectionRoleSetting, cancellationToken).ConfigureAwait(false);
        var savedRole = string.Equals(role, "observer", StringComparison.OrdinalIgnoreCase)
            ? EstateRaceConnectionRole.Observer
            : EstateRaceConnectionRole.Driver;
        resumeToken = savedRole == EstateRaceConnectionRole.Observer ? observerResumeToken : driverResumeToken;
        return new EstateRaceConnectionProfile(
            address ?? "http://127.0.0.1:24876",
            string.Empty,
            name ?? string.Empty,
            NormalizeColor(color),
            NullIfWhiteSpace(team),
            NullIfWhiteSpace(teamId),
            savedRole);
    }

    public async Task ConnectAsync(
        EstateRaceConnectionProfile profile,
        CancellationToken cancellationToken,
        string? expectedTrackPackageHash = null)
    {
        await CancelReconnectAsync().ConfigureAwait(false);
        requestedTrackPackageHash = string.IsNullOrWhiteSpace(expectedTrackPackageHash)
            ? null
            : expectedTrackPackageHash.Trim().ToUpperInvariant();
        if (!await ConnectOnceAsync(profile, cancellationToken, isReconnectAttempt: false).ConfigureAwait(false))
        {
            var failure = State.ConnectionText;
            activeProfile = null;
            requestedTrackPackageHash = null;
            throw new InvalidOperationException(failure);
        }
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
            await DisconnectCoreAsync(preserveSessionState: isReconnectAttempt).ConfigureAwait(false);
            if (!isReconnectAttempt)
            {
                lock (telemetryStateSync) shortcutDetector.Reset();
            }
            intentionalDisconnect = false;
            connectionAuthenticated = false;
            activeProfile = profile with
            {
                ServerAddress = profile.ServerAddress.Trim(),
                DisplayName = profile.DisplayName.Trim(),
                ThemeColor = NormalizeColor(profile.ThemeColor),
                TeamName = NullIfWhiteSpace(profile.TeamName)
            };
            resumeToken = activeProfile.IsObserver ? observerResumeToken : driverResumeToken;
            SetConnectionState(
                isReconnectAttempt ? EstateRaceConnectionState.Reconnecting : EstateRaceConnectionState.Connecting,
                isReconnectAttempt ? "正在恢复赛事连接…" : "正在连接赛事服务…");
            connectionCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                runCancellation?.Token ?? CancellationToken.None,
                cancellationToken);
            socket = new ClientWebSocket();
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
            socket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(10);
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
                requestedTrackPackageHash ?? context?.TrackPackageHash,
                context?.SectorCount,
                activeProfile.TeamId,
                activeProfile.IsObserver), timeout.Token).ConfigureAwait(false);

            var loginEnvelope = await ReceiveEnvelopeAsync(socket, timeout.Token).ConfigureAwait(false);
            if (loginEnvelope.ProtocolVersion != EstateRaceWireProtocol.Version)
                throw new InvalidOperationException("服务端协议版本与当前 LazyForza 不兼容。");
            if (loginEnvelope.Type == "loginRejected")
            {
                var rejected = loginEnvelope.Payload.Deserialize<RaceLoginRejected>(EstateRaceWireProtocol.JsonOptions);
                if (string.Equals(rejected?.Code, "disconnectedByControl", StringComparison.Ordinal))
                {
                    if (activeProfile.IsObserver) observerResumeToken = null;
                    else driverResumeToken = null;
                    resumeToken = null;
                    await Context.Settings.SetAsync(
                        ModuleId,
                        activeProfile.IsObserver ? ObserverResumeTokenSetting : ResumeTokenSetting,
                        string.Empty,
                        CancellationToken.None).ConfigureAwait(false);
                }
                SetConnectionState(EstateRaceConnectionState.Rejected, rejected?.Message ?? "赛事服务拒绝登录。");
                await DisconnectCoreAsync().ConfigureAwait(false);
                return false;
            }
            if (loginEnvelope.Type != "loginAccepted")
                throw new InvalidOperationException("赛事服务未返回有效的登录确认。");
            var accepted = loginEnvelope.Payload.Deserialize<RaceLoginAccepted>(EstateRaceWireProtocol.JsonOptions) ??
                           throw new InvalidOperationException("赛事服务返回的登录数据无效。");
            if (activeProfile.IsObserver != accepted.IsObserver)
                throw new InvalidOperationException(activeProfile.IsObserver
                    ? "该服务端不支持 OB 身份，请更新服务端后重试。"
                    : "服务端返回了与请求不一致的连接身份。");
            participantId = accepted.ParticipantId;
            resumeToken = accepted.ResumeToken;
            connectionIsObserver = accepted.IsObserver;
            if (connectionIsObserver) observerResumeToken = accepted.ResumeToken;
            else driverResumeToken = accepted.ResumeToken;
            connectionAuthenticated = true;
            ApplySessionSnapshot(NormalizeSession(accepted.Snapshot), resetForConnection: !isReconnectAttempt);
            MarkServerResponse();
            await RefreshOrganizerLogoAsync(accepted.Snapshot, timeout.Token).ConfigureAwait(false);
            await SaveProfileAsync(activeProfile, accepted.ResumeToken, cancellationToken).ConfigureAwait(false);
            PublishSnapshot(EstateRaceConnectionState.Connected, "已连接赛事服务");
            var connectedSocket = socket;
            var connectedCancellation = connectionCancellation;
            receiveTask = Task.Run(
                () => ReceiveLoopAsync(connectedSocket, connectedCancellation.Token),
                CancellationToken.None);
            await FlushPendingLapUploadsAsync(timeout.Token).ConfigureAwait(false);
            heartbeatTask = Task.Run(
                () => HeartbeatLoopAsync(connectedCancellation.Token),
                CancellationToken.None);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await DisconnectCoreAsync(preserveSessionState: isReconnectAttempt).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception)
        {
            await DisconnectCoreAsync(preserveSessionState: isReconnectAttempt).ConfigureAwait(false);
            SetConnectionState(
                isReconnectAttempt ? EstateRaceConnectionState.Reconnecting : EstateRaceConnectionState.Faulted,
                isReconnectAttempt
                    ? $"赛事连接尚未恢复：{exception.Message}"
                    : $"连接失败：{exception.Message}");
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
            requestedTrackPackageHash = null;
            lastSessionPhase = null;
            lastQualifyingSessionNumber = 0;
            lastPracticeSessionNumber = 0;
            sentLapEventId = null;
            ClearPendingLapUploads();
            SetRaceTimingEnabled(false);
            SetConnectionState(EstateRaceConnectionState.Disconnected, "未连接赛事服务");
        }
        finally
        {
            connectionLock.Release();
        }
    }

    public Task SetReadyAsync(bool isReady, CancellationToken cancellationToken) =>
        connectionIsObserver
            ? Task.FromException(new InvalidOperationException("OB 不参与准备与比赛流程。"))
            : SendAsync("ready", new RaceReadyUpdate(isReady), cancellationToken);

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
        lock (telemetryStateSync)
        {
            gripEstimator.Reset();
            pitServiceTracker.Reset();
            collisionEvidenceDetector.Reset();
        }
        pitStrategyPredictor.Reset();
        practiceTestManager.Reset();
        participantId = null;
        connectionIsObserver = false;
        connectionAuthenticated = false;
        session = null;
        activeProfile = null;
        requestedTrackPackageHash = null;
        loadedStrategyTrackKey = null;
        observedVehicleFingerprint = null;
        learnedVehicleFingerprint = null;
        nextFingerprintRefreshAt = default;
        lastSessionPhase = null;
        lastQualifyingSessionNumber = 0;
        lastPracticeSessionNumber = 0;
        lastValidProjection = null;
        sentLapEventId = null;
        ClearPendingLapUploads();
        SetRaceTimingEnabled(false);
        Volatile.Write(ref snapshot, EmptySnapshot());
    }

    private async Task ConsumeTelemetryAsync(
        System.Threading.Channels.ChannelReader<TelemetryFrame> frames,
        CancellationToken cancellationToken)
    {
        await foreach (var frame in frames.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                const int maximumCatchUpFrames = 16;
                var catchUp = new Queue<TelemetryFrame>(maximumCatchUpFrames);
                catchUp.Enqueue(frame);
                while (frames.TryRead(out var newer))
                {
                    if (catchUp.Count == maximumCatchUpFrames) catchUp.Dequeue();
                    catchUp.Enqueue(newer);
                }
                while (catchUp.TryDequeue(out var buffered))
                    await ProcessTelemetryFrameAsync(buffered, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                LogBackgroundFailure(
                    "telemetry",
                    exception,
                    ref lastTelemetryFailureAt,
                    ref lastTelemetryFailure);
            }
        }
    }

    private async Task ProcessTelemetryFrameAsync(
        TelemetryFrame frame,
        CancellationToken cancellationToken)
    {
        var context = trackContext();
        if (context is null) return;
        if (connectionIsObserver) return;
        var monotonicNow = monotonicClock.ElapsedMilliseconds;
        var valid = IsTelemetryValid(frame, out var pausedOrRewinding);
        EstateShortcutObservation shortcutObservation;
        lock (telemetryStateSync)
            shortcutObservation = shortcutDetector.Observe(
                frame,
                context.Track,
                context.Definition.Pit,
                valid,
                monotonicNow);
        if (socket?.State != WebSocketState.Open || !connectionAuthenticated)
        {
            CaptureDisconnectedLap(context);
            return;
        }
        await FlushPendingLapUploadsAsync(cancellationToken).ConfigureAwait(false);
        if (context.LastCompletedLap is { } completedLap &&
            completedLap.EventId != sentLapEventId &&
            !HasPendingLapUpload(completedLap.EventId))
        {
            var upload = CreateLapUpload(completedLap, recoveredAfterDisconnect: false);
            if (session?.DisconnectedLapRecoveryEnabled == true)
                QueuePendingLapUpload(upload, Math.Max(1, monotonicClock.ElapsedMilliseconds));
            await SendAsync(
                "lapCompleted",
                upload,
                cancellationToken).ConfigureAwait(false);
            sentLapEventId = completedLap.EventId;
        }
        var collision = collisionEvidenceDetector.Observe(frame, valid);
        if (lastTelemetrySentAt != default &&
            frame.ArrivalTime - lastTelemetrySentAt < TelemetryInterval &&
            monotonicNow - lastTelemetrySentMonotonicMilliseconds < TelemetryInterval.TotalMilliseconds)
            return;
        lastTelemetrySentAt = frame.ArrivalTime;
        lastTelemetrySentMonotonicMilliseconds = monotonicNow;
        observedVehicleFingerprint = VehicleProfileFingerprint.FromFrame(frame);
        var positionReliable = valid;
        var localParticipant = session?.Participants.FirstOrDefault(candidate => candidate.Id == participantId);
        var serviceBlocked = localParticipant is
        {
            PendingTimePenaltySeconds: > 0
        } or { IsServingTimePenalty: true };
        EstatePitServiceState pitService;
        EstateRaceProjection projection;
        RaceGripCondition gripCondition;
        lock (telemetryStateSync)
        {
            gripEstimator.Observe(frame, context.CompletedLaps, positionReliable);
            pitService = pitServiceTracker.Observe(
                frame,
                context.Definition.Pit,
                positionReliable,
                serviceBlocked);
            projection = positionReliable && shortcutObservation.Projection.IsValid
                ? shortcutObservation.Projection
                : lastValidProjection ?? new EstateRaceProjection(0, 0, 0.5, 0.5);
            if (positionReliable) lastValidProjection = projection;
            gripCondition = gripEstimator.Current;
        }
        var pit = context.Definition.Pit;
        var isOnPitRoute = positionReliable && pitService.IsOnPitRoute;
        var update = new RaceTelemetryUpdate(
            monotonicNow,
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
            gripCondition,
            pitService.ElapsedSeconds,
            pitService.RequirementMet,
            pitService.CompletedServices,
            context.Track.MatchingToleranceMeters,
            context.Track.LengthMeters,
            pit?.SpeedLimitKph ?? 0,
            pitService.PitLaneElapsedSeconds,
            pitService.IsApproachingPit,
            isOnPitRoute,
            positionReliable,
            collision.WorldPosition.X,
            collision.WorldPosition.Y,
            collision.WorldPosition.Z,
            collision.Velocity.X,
            collision.Velocity.Y,
            collision.Velocity.Z,
            collision.ImpactSequence,
            collision.ImpactMagnitudeMps,
            collision.ImpactSpeedLossMps,
            collision.ImpactPosition.X,
            collision.ImpactPosition.Y,
            collision.ImpactPosition.Z,
            collision.ImpactAgeMilliseconds,
            positionReliable,
            collision.WorldVelocity.X,
            collision.WorldVelocity.Y,
            collision.WorldVelocity.Z,
            collision.ImpactWorldVelocity.X,
            collision.ImpactWorldVelocity.Y,
            collision.ImpactWorldVelocity.Z,
            collision.ImpactSmashableVelDiff,
            collision.ImpactSmashableMass,
            shortcutObservation.Evidence);
        await SendAsync("telemetry", update, cancellationToken).ConfigureAwait(false);
        // Practice strategy processing is optional. Run it after the time-sensitive
        // telemetry and lap messages so it can never delay the current upload.
        ObserveTelemetryStrategySafely(context, pausedOrRewinding, pitService);
    }

    private void CaptureDisconnectedLap(EstateRaceTrackContext context)
    {
        if (connectionIsObserver || context.LastCompletedLap is not { } lap || lap.EventId == sentLapEventId)
            return;
        if (session?.DisconnectedLapRecoveryEnabled != true)
        {
            sentLapEventId = lap.EventId;
            ClearPendingLapUploads();
            return;
        }

        lock (lapRecoverySync)
        {
            if (pendingLapUploads.Any(item => item.Lap.EventId == lap.EventId)) return;
            QueuePendingLapUploadLocked(CreateLapUpload(lap, recoveredAfterDisconnect: true), 0);
        }
    }

    private void QueuePendingLapUpload(RaceLapCompleted lap, long lastAttemptMonotonicMilliseconds)
    {
        lock (lapRecoverySync)
        {
            if (pendingLapUploads.Any(item => item.Lap.EventId == lap.EventId)) return;
            QueuePendingLapUploadLocked(lap, lastAttemptMonotonicMilliseconds);
        }
    }

    private void QueuePendingLapUploadLocked(RaceLapCompleted lap, long lastAttemptMonotonicMilliseconds)
    {
        if (pendingLapUploads.Count >= 12) pendingLapUploads.RemoveAt(0);
        pendingLapUploads.Add(new PendingLapUpload(lap, lastAttemptMonotonicMilliseconds));
    }

    private async Task FlushPendingLapUploadsAsync(CancellationToken cancellationToken)
    {
        if (connectionIsObserver || session?.DisconnectedLapRecoveryEnabled != true) return;
        while (true)
        {
            RaceLapCompleted? lap = null;
            var now = monotonicClock.ElapsedMilliseconds;
            lock (lapRecoverySync)
            {
                var index = pendingLapUploads.FindIndex(item =>
                    item.LastAttemptMonotonicMilliseconds == 0 ||
                    now - item.LastAttemptMonotonicMilliseconds >= RecoveredLapRetryInterval.TotalMilliseconds);
                if (index >= 0)
                {
                    var pending = pendingLapUploads[index];
                    lap = pending.Lap;
                    pendingLapUploads[index] = pending with { LastAttemptMonotonicMilliseconds = now };
                }
            }
            if (lap is null) return;
            await SendAsync("lapCompleted", lap, cancellationToken).ConfigureAwait(false);
        }
    }

    private void AcknowledgeLap(RaceLapAcknowledgement acknowledgement)
    {
        lock (lapRecoverySync)
            pendingLapUploads.RemoveAll(item => item.Lap.EventId == acknowledgement.EventId);
        sentLapEventId = acknowledgement.EventId;
        if (!acknowledgement.IsAccepted && !string.IsNullOrWhiteSpace(acknowledgement.Message))
            PublishSnapshot(EstateRaceConnectionState.Connected, acknowledgement.Message);
    }

    private bool HasPendingLapUpload(Guid eventId)
    {
        lock (lapRecoverySync)
            return pendingLapUploads.Any(item => item.Lap.EventId == eventId);
    }

    private void ClearPendingLapUploads()
    {
        lock (lapRecoverySync) pendingLapUploads.Clear();
    }

    private void MarkPendingLapUploadsForRecovery()
    {
        lock (lapRecoverySync)
            for (var index = 0; index < pendingLapUploads.Count; index++)
            {
                var pending = pendingLapUploads[index];
                pendingLapUploads[index] = pending with
                {
                    Lap = pending.Lap with { IsRecoveredAfterDisconnect = true },
                    LastAttemptMonotonicMilliseconds = 0
                };
            }
    }

    private RaceLapCompleted CreateLapUpload(
        EstateCompletedLapEvent lap,
        bool recoveredAfterDisconnect) => new(
        lap.EventId,
        lap.LapNumber,
        lap.LapSeconds,
        lap.SectorSeconds,
        lap.IsValid,
        lap.InvalidReason,
        monotonicClock.ElapsedMilliseconds,
        lap.IsBestLapEligible,
        recoveredAfterDisconnect);

    private async Task ReceiveLoopAsync(
        ClientWebSocket activeSocket,
        CancellationToken cancellationToken)
    {
        try
        {
            while (activeSocket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var envelope = await ReceiveEnvelopeAsync(activeSocket, cancellationToken).ConfigureAwait(false);
                    MarkServerResponse();
                    if (envelope.ProtocolVersion != EstateRaceWireProtocol.Version) continue;
                    if (envelope.Type == "snapshot")
                    {
                        var received = envelope.Payload.Deserialize<EstateRaceSession>(EstateRaceWireProtocol.JsonOptions);
                        if (received is not null)
                        {
                            received = NormalizeSession(received);
                            ApplySessionSnapshot(received);
                            await RefreshOrganizerLogoAsync(received, cancellationToken).ConfigureAwait(false);
                        }
                        PublishSnapshot(EstateRaceConnectionState.Connected, "已连接赛事服务");
                    }
                    else if (envelope.Type == "pong")
                    {
                        var pong = envelope.Payload.Deserialize<RaceClockPong>(EstateRaceWireProtocol.JsonOptions);
                        if (pong is not null) UpdateNetworkTiming(pong);
                    }
                    else if (envelope.Type == "lapAcknowledged")
                    {
                        var acknowledgement = envelope.Payload.Deserialize<RaceLapAcknowledgement>(
                            EstateRaceWireProtocol.JsonOptions);
                        if (acknowledgement is not null) AcknowledgeLap(acknowledgement);
                    }
                    else if (envelope.Type == "error")
                    {
                        var error = envelope.Payload.Deserialize<RaceProtocolError>(EstateRaceWireProtocol.JsonOptions);
                        if (error is not null) PublishSnapshot(EstateRaceConnectionState.Connected, error.Message);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (WebSocketException) { throw; }
                catch (Exception exception)
                {
                    LogBackgroundFailure(
                        "snapshot",
                        exception,
                        ref lastSnapshotFailureAt,
                        ref lastSnapshotFailure);
                    PublishSnapshot(
                        EstateRaceConnectionState.Connected,
                        "已跳过一次异常赛事快照，连接仍在继续");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (WebSocketException exception)
        {
            HandleConnectionInterrupted(exception);
        }
        catch (Exception exception)
        {
            HandleConnectionInterrupted(exception);
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await SendAsync(
                    "ping",
                    new RaceClockPing(monotonicClock.ElapsedMilliseconds),
                    cancellationToken).ConfigureAwait(false);
                await Task.Delay(HeartbeatInterval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            LogBackgroundFailure(
                "heartbeat",
                exception,
                ref lastSnapshotFailureAt,
                ref lastSnapshotFailure);
        }
    }

    private void UpdateNetworkTiming(RaceClockPong pong)
    {
        var receivedMonotonic = monotonicClock.ElapsedMilliseconds;
        var roundTripMilliseconds = receivedMonotonic - pong.ClientMonotonicMilliseconds;
        if (roundTripMilliseconds is < 0 or > 30_000) return;
        var oneWay = TimeSpan.FromMilliseconds(roundTripMilliseconds / 2d);
        var roundTrip = TimeSpan.FromMilliseconds(roundTripMilliseconds);
        var estimatedServerAtReceive = DateTimeOffset
            .FromUnixTimeMilliseconds(pong.ServerUnixMilliseconds)
            .Add(oneWay);
        var offset = estimatedServerAtReceive - DateTimeOffset.UtcNow;
        var priorLatency = Interlocked.Read(ref estimatedOneWayLatencyTicks);
        var priorRoundTrip = Interlocked.Read(ref estimatedRoundTripLatencyTicks);
        var priorJitter = Interlocked.Read(ref networkJitterTicks);
        var priorOffset = Interlocked.Read(ref serverClockOffsetTicks);
        var latencyTicks = priorLatency == 0
            ? oneWay.Ticks
            : (long)Math.Round(priorLatency * 0.8 + oneWay.Ticks * 0.2);
        var offsetTicks = Volatile.Read(ref hasServerClockEstimate) == 0
            ? offset.Ticks
            : (long)Math.Round(priorOffset * 0.8 + offset.Ticks * 0.2);
        var roundTripTicks = priorRoundTrip == 0
            ? roundTrip.Ticks
            : (long)Math.Round(priorRoundTrip * 0.8 + roundTrip.Ticks * 0.2);
        var jitterSampleTicks = priorRoundTrip == 0 ? 0 : Math.Abs(roundTrip.Ticks - priorRoundTrip);
        var jitterTicks = priorJitter == 0
            ? jitterSampleTicks
            : (long)Math.Round(priorJitter * 0.8 + jitterSampleTicks * 0.2);
        Interlocked.Exchange(ref estimatedOneWayLatencyTicks, latencyTicks);
        Interlocked.Exchange(ref estimatedRoundTripLatencyTicks, roundTripTicks);
        Interlocked.Exchange(ref networkJitterTicks, jitterTicks);
        Interlocked.Exchange(ref serverClockOffsetTicks, offsetTicks);
        Volatile.Write(ref hasServerClockEstimate, 1);
        PublishSnapshot(EstateRaceConnectionState.Connected, "已连接赛事服务");
    }

    private void MarkServerResponse() =>
        Interlocked.Exchange(ref lastServerResponseUtcTicks, DateTimeOffset.UtcNow.UtcDateTime.Ticks);

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
                    var baseDelaySeconds = attempt == 1
                        ? 0.5
                        : Math.Min(10, Math.Pow(2, Math.Min(attempt - 2, 3)));
                    var delay = TimeSpan.FromSeconds(
                        baseDelaySeconds * (0.85 + Random.Shared.NextDouble() * 0.30));
                    SetConnectionState(
                        EstateRaceConnectionState.Reconnecting,
                        $"连接中断，{delay.TotalSeconds:0.#} 秒后进行第 {attempt} 次重连…");
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
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(string.Equals(type, "telemetry", StringComparison.Ordinal)
            ? TelemetrySendTimeout
            : CommandSendTimeout);
        try
        {
            await sendLock.WaitAsync(timeout.Token).ConfigureAwait(false);
            try
            {
                await activeSocket.SendAsync(bytes, WebSocketMessageType.Text, true, timeout.Token).ConfigureAwait(false);
            }
            finally
            {
                sendLock.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            activeSocket.Abort();
            var timeoutException = new TimeoutException(
                string.Equals(type, "telemetry", StringComparison.Ordinal)
                    ? "赛事遥测发送超时。"
                    : "赛事服务命令发送超时。",
                exception);
            HandleConnectionInterrupted(timeoutException);
            throw timeoutException;
        }
        catch (Exception exception) when (
            exception is WebSocketException or ObjectDisposedException or InvalidOperationException)
        {
            HandleConnectionInterrupted(exception);
            throw;
        }
    }

    private void HandleConnectionInterrupted(Exception exception)
    {
        if (intentionalDisconnect) return;
        if (session?.DisconnectedLapRecoveryEnabled != true)
        {
            sentLapEventId = trackContext()?.LastCompletedLap?.EventId;
            ClearPendingLapUploads();
        }
        else
            MarkPendingLapUploadsForRecovery();
        SetConnectionState(EstateRaceConnectionState.Reconnecting, "连接中断，正在尝试恢复赛事连接…");
        LogIfInitialized($"Estate race WebSocket disconnected: {exception.Message}");
        ScheduleReconnect();
    }

    private static async Task<RaceIncomingEnvelope> ReceiveEnvelopeAsync(
        ClientWebSocket activeSocket,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(EstateRaceWireProtocol.MaximumMessageBytes);
        var written = 0;
        try
        {
            while (true)
            {
                if (written >= EstateRaceWireProtocol.MaximumMessageBytes)
                    throw new JsonException("赛事服务消息超过大小限制。");
                var received = await activeSocket.ReceiveAsync(
                    buffer.AsMemory(written, EstateRaceWireProtocol.MaximumMessageBytes - written),
                    cancellationToken).ConfigureAwait(false);
                if (received.MessageType == WebSocketMessageType.Close)
                    throw new WebSocketException("赛事服务已关闭连接。");
                if (received.MessageType != WebSocketMessageType.Text)
                    throw new JsonException("赛事服务返回了非文本消息。");
                written += received.Count;
                if (received.EndOfMessage) break;
            }
            return JsonSerializer.Deserialize<RaceIncomingEnvelope>(
                       buffer.AsSpan(0, written),
                       EstateRaceWireProtocol.JsonOptions) ??
                   throw new JsonException("赛事服务消息为空。");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task DisconnectCoreAsync(bool preserveSessionState = false)
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
            catch (Exception exception)
            {
                LogIfInitialized($"Estate race socket close failed: {exception.Message}");
            }
            activeSocket.Dispose();
        }
        var currentReceive = receiveTask;
        receiveTask = null;
        if (currentReceive is not null)
        {
            try { await currentReceive.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (WebSocketException) { }
            catch (Exception exception)
            {
                LogIfInitialized($"Estate race receive task ended with an error: {exception.Message}");
            }
        }
        var currentHeartbeat = heartbeatTask;
        heartbeatTask = null;
        if (currentHeartbeat is not null)
        {
            try { await currentHeartbeat.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception exception)
            {
                LogIfInitialized($"Estate race heartbeat task ended with an error: {exception.Message}");
            }
        }
        connectionCancellation?.Dispose();
        connectionCancellation = null;
        if (preserveSessionState)
        {
            connectionAuthenticated = false;
            return;
        }
        try
        {
            lock (strategySync)
            {
                practiceTestManager.StopAutomatically("赛事连接已经中断，测试已自动关闭。 ");
                PersistPendingStrategySamples();
                pitStrategyPredictor.Reset();
                practiceTestManager.Reset();
            }
        }
        catch (Exception exception)
        {
            LogStrategyFailure(exception);
        }
        participantId = null;
        connectionIsObserver = false;
        connectionAuthenticated = false;
        session = null;
        organizerLogo = null;
        failedOrganizerLogoHash = null;
        organizerLogoRetryAfter = default;
        lock (telemetryStateSync)
        {
            pitServiceTracker.Reset();
            gripEstimator.Reset();
            collisionEvidenceDetector.Reset();
            lastValidProjection = null;
        }
        loadedStrategyTrackKey = null;
        observedVehicleFingerprint = null;
        learnedVehicleFingerprint = null;
        nextFingerprintRefreshAt = default;
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
        var map = TrackMapSnapshot(context);
        Volatile.Write(ref snapshot, new EstateRaceHudState(
            DateTimeOffset.UtcNow,
            state,
            text,
            participantId,
            session,
            map.TrackOutline,
            gripEstimator.Current,
            GripExplanation(gripEstimator.Current),
            pitServiceTracker.Current,
            map.PitOutline,
            map.StartFinishGate,
            map.TrackSectors,
            organizerLogo,
            connectionIsObserver,
            pitStrategyPredictor.Current,
            practiceTestManager.Current,
            TimeSpan.FromTicks(Math.Max(0, Interlocked.Read(ref estimatedOneWayLatencyTicks))),
            Volatile.Read(ref hasServerClockEstimate) == 0
                ? null
                : TimeSpan.FromTicks(Interlocked.Read(ref serverClockOffsetTicks)),
            TimeSpan.FromTicks(Math.Max(0, Interlocked.Read(ref estimatedRoundTripLatencyTicks))),
            TimeSpan.FromTicks(Math.Max(0, Interlocked.Read(ref networkJitterTicks))),
            Interlocked.Read(ref lastServerResponseUtcTicks) is var responseTicks && responseTicks > 0
                ? new DateTimeOffset(responseTicks, TimeSpan.Zero)
                : null));
    }

    private EstateRaceTrackMapSnapshot TrackMapSnapshot(EstateRaceTrackContext? context)
    {
        if (context is null) return EstateRaceTrackMapSnapshot.Empty;
        var key = new EstateRaceTrackMapCacheKey(
            context.Track.Id,
            context.Track.UpdatedAt,
            context.Definition.UpdatedAt,
            context.Definition.MapRevision,
            context.Sectors?.Count ?? 0);
        lock (trackMapCacheSync)
        {
            if (trackMapCacheKey != key)
            {
                cachedTrackOutline = EstateRaceGeometry.NormalizeOutline(context.Track);
                cachedPitOutline = EstateRaceGeometry.NormalizePitLane(context.Track, context.Definition.Pit);
                cachedStartFinishGate = EstateRaceGeometry.NormalizeGate(
                    context.Track,
                    context.Definition.StartFinishGate);
                cachedTrackSectors = EstateRaceGeometry.NormalizeSectors(context.Track, context.Sectors);
                trackMapCacheKey = key;
            }
            return new EstateRaceTrackMapSnapshot(
                cachedTrackOutline,
                cachedPitOutline,
                cachedStartFinishGate,
                cachedTrackSectors);
        }
    }

    private async Task RefreshOrganizerLogoAsync(
        EstateRaceSession received,
        CancellationToken cancellationToken)
    {
        var expectedHash = received.OrganizerLogoHash?.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(expectedHash) ||
            string.IsNullOrWhiteSpace(received.OrganizerLogoDownloadPath))
        {
            organizerLogo = null;
            failedOrganizerLogoHash = null;
            organizerLogoRetryAfter = default;
            return;
        }
        if (organizerLogo?.Sha256.Equals(expectedHash, StringComparison.OrdinalIgnoreCase) == true) return;
        if (string.Equals(failedOrganizerLogoHash, expectedHash, StringComparison.OrdinalIgnoreCase) &&
            DateTimeOffset.UtcNow < organizerLogoRetryAfter) return;

        try
        {
            var uri = ServerHttpUri(activeProfile?.ServerAddress, received.OrganizerLogoDownloadPath);
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > MaximumOrganizerLogoBytes)
                throw new InvalidDataException("赛事 Logo 超过客户端大小限制。");
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            while (true)
            {
                var read = await source.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                if (buffer.Length + read > MaximumOrganizerLogoBytes)
                    throw new InvalidDataException("赛事 Logo 超过客户端大小限制。");
                await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
            var bytes = buffer.ToArray();
            var actualHash = Convert.ToHexString(SHA256.HashData(bytes));
            if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("赛事 Logo 的 SHA-256 与服务端声明不一致。");
            var mimeType = response.Content.Headers.ContentType?.MediaType ?? received.OrganizerLogoMimeType ?? "image/png";
            if (mimeType is not ("image/png" or "image/jpeg"))
                throw new InvalidDataException("赛事 Logo 类型不受支持。");
            organizerLogo = new EstateRaceOrganizerLogo(actualHash, mimeType, bytes);
            failedOrganizerLogoHash = null;
            organizerLogoRetryAfter = default;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            organizerLogo = null;
            failedOrganizerLogoHash = expectedHash;
            organizerLogoRetryAfter = DateTimeOffset.UtcNow.AddSeconds(15);
            LogIfInitialized($"Estate race organizer logo download failed: {exception.Message}");
        }
    }

    private static Uri ServerHttpUri(string? serverAddress, string path)
    {
        var websocket = ServerWebSocketUri(serverAddress ?? throw new InvalidOperationException("赛事服务地址为空。"));
        var builder = new UriBuilder(websocket)
        {
            Scheme = websocket.Scheme == "wss" ? "https" : "http",
            Path = path.StartsWith('/') ? path : "/" + path,
            Query = string.Empty
        };
        return builder.Uri;
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
        await Context.Settings.SetAsync(
            ModuleId,
            profile.IsObserver ? ObserverResumeTokenSetting : ResumeTokenSetting,
            token,
            cancellationToken).ConfigureAwait(false);
        await Context.Settings.SetAsync(
            ModuleId,
            ConnectionRoleSetting,
            profile.IsObserver ? "observer" : "driver",
            cancellationToken).ConfigureAwait(false);
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
        if (value.Participants is { Count: <= 12 } participantsFromServer &&
            value.FastestSectorSeconds is not null &&
            value.FastestLapSectorSeconds is not null &&
            value.YellowZones is not null &&
            value.BlueFlags is not null &&
            value.Penalties is not null &&
            value.Investigations is not null &&
            value.QualifyingSessionMinutes is not null &&
            value.QualifyingEliminationCounts is not null &&
            value.PracticeSessionMinutes is not null &&
            value.Observers is not null &&
            value.MinimumRequiredPitStops is >= 0 and <= 20 &&
            participantsFromServer.All(participant =>
                participant.BestSectorSeconds is not null &&
                participant.Penalties is not null &&
                participant.QualifyingSessionBestLapSeconds is not null &&
                participant.PracticeSessionBestLapSeconds is not null))
            return value;

        var participants = (value.Participants ?? [])
            .Take(12)
            .Select(participant => participant with
            {
                BestSectorSeconds = participant.BestSectorSeconds ?? [],
                Penalties = participant.Penalties ?? [],
                QualifyingSessionBestLapSeconds = participant.QualifyingSessionBestLapSeconds ?? [],
                PracticeSessionBestLapSeconds = participant.PracticeSessionBestLapSeconds ?? []
            })
            .ToArray();
        return value with
        {
            FastestSectorSeconds = value.FastestSectorSeconds ?? [],
            FastestLapSectorSeconds = value.FastestLapSectorSeconds ?? [],
            Participants = participants,
            YellowZones = value.YellowZones ?? [],
            BlueFlags = value.BlueFlags ?? [],
            Penalties = value.Penalties ?? [],
            Investigations = value.Investigations ?? [],
            QualifyingSessionMinutes = value.QualifyingSessionMinutes ?? [10],
            QualifyingEliminationCounts = value.QualifyingEliminationCounts ?? [],
            PracticeSessionMinutes = value.PracticeSessionMinutes ?? [60],
            Observers = value.Observers ?? [],
            MinimumRequiredPitStops = Math.Clamp(value.MinimumRequiredPitStops, 0, 20)
        };
    }

    private void ApplySessionSnapshot(
        EstateRaceSession value,
        bool resetForConnection = false)
    {
        var qualifyingSessionBoundary = value.Phase == RaceSessionPhase.Qualifying &&
                                        lastSessionPhase == RaceSessionPhase.Qualifying &&
                                        value.QualifyingSessionNumber > 0 &&
                                        value.QualifyingSessionNumber != lastQualifyingSessionNumber;
        var practiceSessionBoundary = value.Phase == RaceSessionPhase.Practice &&
                                      lastSessionPhase == RaceSessionPhase.Practice &&
                                      value.PracticeSessionNumber > 0 &&
                                      value.PracticeSessionNumber != lastPracticeSessionNumber;
        var phaseBoundary = lastSessionPhase != value.Phase &&
                            value.Phase is RaceSessionPhase.Lobby or
                                RaceSessionPhase.Practice or RaceSessionPhase.Qualifying or RaceSessionPhase.Race;
        if (resetForConnection || phaseBoundary || qualifyingSessionBoundary || practiceSessionBoundary)
        {
            lock (strategySync)
            {
                if (resetForConnection)
                {
                    pitStrategyPredictor.Reset();
                    practiceTestManager.Reset();
                    loadedStrategyTrackKey = null;
                }
                if (practiceSessionBoundary)
                    practiceTestManager.StopAutomatically("练习赛已经进入下一节，当前测试已自动结束。 ");
            }
            if (qualifyingSessionBoundary || practiceSessionBoundary) SetRaceTimingEnabled(false);
            lock (telemetryStateSync)
            {
                pitServiceTracker.Reset();
                gripEstimator.Reset();
                collisionEvidenceDetector.Reset();
                if (resetForConnection) lastValidProjection = null;
            }
            sentLapEventId = trackContext()?.LastCompletedLap?.EventId;
            ClearPendingLapUploads();
        }
        if (!value.DisconnectedLapRecoveryEnabled)
        {
            sentLapEventId = trackContext()?.LastCompletedLap?.EventId;
            ClearPendingLapUploads();
        }
        lastSessionPhase = value.Phase;
        lastQualifyingSessionNumber = value.Phase == RaceSessionPhase.Qualifying
            ? value.QualifyingSessionNumber
            : 0;
        lastPracticeSessionNumber = value.Phase == RaceSessionPhase.Practice
            ? value.PracticeSessionNumber
            : 0;
        session = value;
        ObserveSnapshotStrategySafely(value);
        SetRaceTimingEnabled(ShouldEnableRaceTiming(value, participantId));
    }

    private void ObserveTelemetryStrategySafely(
        EstateRaceTrackContext context,
        bool pausedOrRewinding,
        EstatePitServiceState pitService)
    {
        try
        {
            lock (strategySync)
            {
                if (pausedOrRewinding)
                    practiceTestManager.NotifyDriverIntervention(pitService);
                ObservePracticeTests(context);
                PersistPendingStrategySamples();
            }
        }
        catch (Exception exception)
        {
            LogStrategyFailure(exception);
        }
    }

    private void ObserveSnapshotStrategySafely(EstateRaceSession value)
    {
        try
        {
            lock (strategySync)
            {
                var context = trackContext();
                EnsureHistoricalStrategySamples(value, context);
                _ = pitStrategyPredictor.Observe(
                    value,
                    participantId,
                    context,
                    gripEstimator.Current,
                    connectionIsObserver,
                    CurrentVehicleFingerprint());
                ObservePracticeTests(context);
                PersistPendingStrategySamples();
            }
        }
        catch (Exception exception)
        {
            LogStrategyFailure(exception);
        }
    }

    private void ObservePracticeTests(EstateRaceTrackContext? context)
    {
        practiceTestManager.Observe(
            session,
            participantId,
            context,
            pitServiceTracker.Current,
            gripEstimator.Current,
            CurrentVehicleFingerprint(),
            connectionIsObserver);
    }

    private void EnsureHistoricalStrategySamples(
        EstateRaceSession value,
        EstateRaceTrackContext? context,
        bool force = false)
    {
        if (context is null || strategySampleLoader is null) return;
        var track = new EstateStrategyTrackIdentity(
            value.TrackId ?? context.Definition.TrackId.ToString("D"),
            value.TrackRevision ?? context.Definition.MapRevision,
            value.TrackPackageHash ?? context.TrackPackageHash ?? string.Empty);
        if (!force && string.Equals(loadedStrategyTrackKey, track.Key, StringComparison.Ordinal)) return;
        try
        {
            var samples = strategySampleLoader(track);
            pitStrategyPredictor.SetHistoricalSamples(samples);
            practiceTestManager.SetStoredSampleCount(samples.Count);
            loadedStrategyTrackKey = track.Key;
        }
        catch (Exception exception)
        {
            LogIfInitialized($"Estate strategy history load failed: {exception.Message}");
        }
    }

    private void PersistPendingStrategySamples()
    {
        var samples = practiceTestManager.DrainSamples()
            .Concat(pitStrategyPredictor.DrainSamples())
            .ToArray();
        if (samples.Length == 0 || strategySampleSaver is null) return;
        foreach (var sample in samples)
        {
            try { strategySampleSaver(sample); }
            catch (Exception exception)
            {
                LogIfInitialized($"Estate strategy sample save failed: {exception.Message}");
            }
        }
        if (session is { } current)
        {
            loadedStrategyTrackKey = null;
            EnsureHistoricalStrategySamples(current, trackContext(), force: true);
            _ = pitStrategyPredictor.Observe(
                current,
                participantId,
                trackContext(),
                gripEstimator.Current,
                connectionIsObserver,
                CurrentVehicleFingerprint());
        }
    }

    private VehicleProfileFingerprint CurrentVehicleFingerprint()
    {
        var now = DateTimeOffset.UtcNow;
        if (vehicleFingerprint is not null && now >= nextFingerprintRefreshAt)
        {
            nextFingerprintRefreshAt = now + FingerprintRefreshInterval;
            try
            {
                learnedVehicleFingerprint = vehicleFingerprint();
            }
            catch (Exception exception)
            {
                LogStrategyFailure(exception);
            }
        }
        var learned = learnedVehicleFingerprint;
        if (observedVehicleFingerprint is { CarOrdinal: > 0 } observed)
        {
            if (learned is { CarOrdinal: > 0 } &&
                learned.CarOrdinal == observed.CarOrdinal &&
                learned.PerformanceIndex == observed.PerformanceIndex)
                return learned;
            return observed;
        }
        if (learned is { CarOrdinal: > 0 }) return learned;
        return new VehicleProfileFingerprint(
            -1, -1, 0, -1, -1, 0,
            VehicleProfileIdentity.PendingSignature,
            VehicleProfileIdentity.PendingSignature);
    }

    private void LogStrategyFailure(Exception exception) =>
        LogBackgroundFailure(
            "strategy",
            exception,
            ref lastStrategyFailureAt,
            ref lastStrategyFailure);

    private void LogBackgroundFailure(
        string component,
        Exception exception,
        ref DateTimeOffset lastLoggedAt,
        ref string? lastMessage)
    {
        var now = DateTimeOffset.UtcNow;
        var message = $"{exception.GetType().Name}: {exception.Message}";
        if (string.Equals(lastMessage, message, StringComparison.Ordinal) &&
            now - lastLoggedAt < BackgroundFailureLogInterval)
            return;
        lastLoggedAt = now;
        lastMessage = message;
        LogIfInitialized($"Estate race {component} processing failed but core synchronization continues: {message}");
    }

    internal static bool ShouldEnableRaceTiming(EstateRaceSession value, Guid? localParticipantId)
    {
        var localParticipant = localParticipantId is Guid localId
            ? value.Participants.FirstOrDefault(participant => participant.Id == localId)
            : null;
        if (localParticipant is null) return false;
        if (value.Phase == RaceSessionPhase.Race) return true;
        if (value.Phase == RaceSessionPhase.Practice)
        {
            if (!value.PracticeTimeExpired) return true;
            return localParticipant.PracticeFinalLapPending;
        }
        if (value.Phase != RaceSessionPhase.Qualifying) return false;
        if (!localParticipant.QualifyingEligible)
            return false;
        if (!value.QualifyingTimeExpired) return true;
        return localParticipant.QualifyingFinalLapPending;
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

    private readonly record struct EstateRaceTrackMapCacheKey(
        Guid TrackId,
        DateTimeOffset TrackUpdatedAt,
        DateTimeOffset DefinitionUpdatedAt,
        string MapRevision,
        int SectorCount);

    private sealed record EstateRaceTrackMapSnapshot(
        IReadOnlyList<EstateRaceMapPoint> TrackOutline,
        IReadOnlyList<EstateRaceMapPoint> PitOutline,
        EstateRaceMapGate? StartFinishGate,
        IReadOnlyList<EstateRaceMapSector> TrackSectors)
    {
        public static EstateRaceTrackMapSnapshot Empty { get; } = new([], [], null, []);
    }

    private sealed record PendingLapUpload(
        RaceLapCompleted Lap,
        long LastAttemptMonotonicMilliseconds);

    [GeneratedRegex("^#[0-9A-Fa-f]{6}$", RegexOptions.CultureInvariant)]
    private static partial Regex ThemeColorPattern();
}
