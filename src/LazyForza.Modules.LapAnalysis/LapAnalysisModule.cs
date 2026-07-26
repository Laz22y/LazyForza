using LazyForza.Analysis;
using LazyForza.Domain;
using LazyForza.Modules.Abstractions;
using LazyForza.Storage;
using System.Threading.Channels;

namespace LazyForza.Modules.LapAnalysis;

public sealed class LapAnalysisModule : LazyForzaModuleBase, IHudContribution
{
    public const string ModuleId = "lap-analysis";
    private const double AutomaticMatchMaximumTravelMeters = 1_200;
    private const double AutomaticMatchMinimumProgressMeters = 100;
    private const double AutomaticMatchMaximumProgressMeters = 220;
    private const double SharedStartDecisionMeters = 300;
    private const int MaximumFineMatchCandidates = 12;
    private const string AutomaticMatchRejectedStatus = "没有找到匹配赛道，本场不会记录圈速。";
    private const string AutomaticMatchRejectedInstruction = "圈速 HUD 即将隐藏。可在赛道页添加自定义赛道。";
    private const string AutomaticMatchRejectedTrackName = "未识别赛事";
    private static readonly TimeSpan AutomaticMatchMaximumDuration = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CumulativeHistoricalDeltaDisplayDuration = TimeSpan.FromSeconds(2);
    private readonly LazyForzaStore store;
    private readonly Func<OverlayLayout> getOverlayLayout;
    private readonly Action<DiagnosticSignal>? diagnosticSink;
    private readonly List<TrackPoint> capture = [];
    private readonly List<LapSample> currentSamples = [];
    private readonly List<LapSummary> visibleLaps = [];
    private readonly Dictionary<Guid, LapRecord> visibleLapDetails = [];
    private readonly List<CompatibleTrack> compatibleTracks = [];
    private readonly List<TelemetryFrame> automaticMatchFrames = [];
    private readonly Dictionary<Guid, AutomaticMatchCandidate> automaticMatchCandidates = [];
    private readonly List<TrackMatchCandidateDiagnostic> automaticMatchCoarseEliminated = [];
    private readonly object lapGate = new();
    private ITelemetrySubscription? subscription;
    private CancellationTokenSource? runCancellation;
    private Task? runTask;
    private Channel<PersistenceCommand>? persistenceQueue;
    private Task? persistenceTask;
    private LapHudState? snapshot;
    private LapHudState? currentCompetitionSnapshot;
    private RecentCompetitionState? recentCompetition;
    private TrackMatchDiagnostics trackMatchDiagnostics = TrackMatchDiagnostics.Empty;
    private TrackTemplate? track;
    private TrackSpatialIndex? trackSpatialIndex;
    private IReadOnlyList<SectorDefinition> sectors = [];
    private ushort? previousLapNumber;
    private float? previousCurrentLap;
    private float? previousCurrentRaceTime;
    private float? previousLastLap;
    private float lastLapValueAtLapStart;
    private float? lastRewindLapTime;
    private TelemetryFrame? lastCompetitionFrame;
    private DateTimeOffset lastCrossingAt = DateTimeOffset.MinValue;
    private bool captureArmed;
    private bool captureStartConfirmed;
    private bool captureEligibleForPointToPointFinish;
    private bool competitionActive;
    private bool competitionSignalSuspended;
    private bool lapArmed;
    private bool waitingForInitialStartLine;
    private int projectionIndex;
    private int confidentProjectionCount;
    private double lastS;
    private double[] sectorStartTimes = [];
    private double?[] completedSectorTimes = [];
    private Guid sessionId = Guid.NewGuid();
    private int validProjectionSamples;
    private int invalidProjectionSamples;
    private DateTimeOffset lapStartedAt;
    private IReadOnlyList<SectorComparison>? heldComparisons;
    private DateTimeOffset holdComparisonsUntil;
    private double? heldCumulativeHistoricalDeltaSeconds;
    private DateTimeOffset heldCumulativeHistoricalDeltaUntil;
    private double? liveCumulativeHistoricalDeltaSeconds;
    private DateTimeOffset liveCumulativeHistoricalDeltaUntil;
    private int liveCumulativeHistoricalDeltaSector = -1;
    private bool currentLapInvalidated;
    private readonly string? expectedTrackSource;
    private readonly string selectionSettingKey;
    private string? incompatibleTrackName;
    private DateTimeOffset? nonCompetitionDrivingSince;
    private int observedSectorIndex;
    private bool trackMatchEverPlausible;
    private bool currentTrackGeometryPlausible = true;
    private bool forceTrackLearning;
    private bool automaticMatchStarted;
    private bool automaticMatchLocked;
    private bool automaticMatchRejected;
    private bool automaticMatchStartedMidLap;
    private bool automaticMatchStartedAtConfirmedLine;
    private double automaticMatchTravelMeters;
    private int automaticMatchCoarseEligibleCount;
    private Vector3F? automaticMatchPreviousPosition;
    private DateTimeOffset automaticMatchStartedAt;
    private DateTimeOffset? severeDeviationStartedAt;
    private double severeDeviationTravelMeters;
    private Vector3F? severeDeviationPreviousPosition;
    private DateTimeOffset? lastInferredCompetitionEndAt;
    private float lastInferredCompetitionRaceTime;

    public LapAnalysisModule(
        LazyForzaStore store,
        TelemetrySourceKind? expectedSource = null,
        Func<OverlayLayout>? getOverlayLayout = null,
        Action<DiagnosticSignal>? diagnosticSink = null)
        : base(new ModuleDescriptor(
            ModuleId,
            "圈速分析",
            "识别赛道、记录圈速，并对比分段与走线。",
            [],
            "lap-analysis",
            "lap-settings",
            true))
    {
        this.store = store;
        this.getOverlayLayout = getOverlayLayout ?? (() => new OverlayLayout());
        this.diagnosticSink = diagnosticSink;
        expectedTrackSource = expectedSource is null ? null : TelemetryDataPartition.TrackSource(expectedSource.Value);
        selectionSettingKey = $"lap.selectedTrack.{expectedTrackSource ?? "all"}";
        foreach (var candidateId in store.ListTracks(expectedTrackSource).Select(candidate => candidate.Id))
        {
            if (store.LoadTrack(candidateId) is not { } saved || !IsCompatible(saved.Track, saved.Sectors)) continue;
            compatibleTracks.Add(new CompatibleTrack(saved.Track, saved.Sectors));
        }
        store.SetAppSetting(selectionSettingKey, string.Empty);
        if (compatibleTracks.Count == 0)
        {
            var latest = store.LoadLatestTrack(expectedTrackSource);
            if (latest is not null) incompatibleTrackName = latest.Value.Track.Name;
        }
        trackMatchDiagnostics = new TrackMatchDiagnostics(
            DateTimeOffset.MinValue,
            "等待比赛或起点",
            compatibleTracks.Count,
            0,
            0,
            [],
            []);
    }

    public string Id => "hud.lap-sectors";
    public HudContributionKind Kind => HudContributionKind.LapSectors;
    public int ZIndex => 20;
    public object? Snapshot => Volatile.Read(ref snapshot);
    public TrackMatchDiagnostics MatchDiagnostics => Volatile.Read(ref trackMatchDiagnostics);
    public LapHudState? CurrentCompetitionSnapshot => competitionActive
        ? Volatile.Read(ref currentCompetitionSnapshot)
        : null;
    public LapHudState? CompetitionPageSnapshot => competitionActive
        ? Volatile.Read(ref currentCompetitionSnapshot)
        : GetRecentCompetition()?.Snapshot;
    public TrackTemplate? CurrentTrack => track;
    public IReadOnlyList<LapSummary> VisibleLaps
    {
        get { lock (lapGate) return visibleLaps.ToArray(); }
    }
    public IReadOnlyList<LapSummary> CurrentSessionLaps
    {
        get
        {
            lock (lapGate)
            {
                return visibleLaps.Where(lap => lap.SessionId == sessionId).OrderBy(lap => lap.StartedAt).ToArray();
            }
        }
    }
    public IReadOnlyList<LapRecord> LoadLapDetails(IReadOnlyCollection<Guid> lapIds)
    {
        if (lapIds.Count == 0) return [];
        var requestedIds = lapIds.Distinct().ToArray();
        Dictionary<Guid, LapRecord> cached;
        lock (lapGate)
        {
            cached = requestedIds
                .Where(visibleLapDetails.ContainsKey)
                .ToDictionary(id => id, id => visibleLapDetails[id]);
        }

        var missingIds = requestedIds.Where(id => !cached.ContainsKey(id)).ToArray();
        foreach (var lap in store.LoadLapsByIds(missingIds)) cached[lap.Id] = lap;
        lock (lapGate)
        {
            foreach (var lap in cached.Values) visibleLapDetails[lap.Id] = lap;
            TrimVisibleLapDetails();
        }

        return requestedIds
            .Where(cached.ContainsKey)
            .Select(id => cached[id])
            .ToArray();
    }
    public Guid CurrentSessionId => sessionId;
    public int? CurrentCompetitionPerformanceClass
    {
        get
        {
            if (lastCompetitionFrame is { } frame)
                return PerformanceClassCatalog.Resolve(frame.Raw.CarClass, frame.Raw.CarPerformanceIndex);
            lock (lapGate)
                return visibleLaps.LastOrDefault(lap => lap.SessionId == sessionId)?.Vehicle.CarClass;
        }
    }
    public int? CurrentCompetitionPerformanceIndex
    {
        get
        {
            if (lastCompetitionFrame is { } frame && frame.Raw.CarPerformanceIndex > 0)
                return frame.Raw.CarPerformanceIndex;
            lock (lapGate)
                return visibleLaps.LastOrDefault(lap => lap.SessionId == sessionId)?.Vehicle.PerformanceIndex;
        }
    }
    public bool IsCompetitionActive => competitionActive && !competitionSignalSuspended;
    public bool HasCurrentCompetitionSession => competitionActive;
    public bool HasCompetitionPageContent => competitionActive || GetRecentCompetition() is not null;
    public bool IsShowingRecentCompetition => !competitionActive && GetRecentCompetition() is not null;
    public DateTimeOffset? RecentCompetitionExpiresAt => GetRecentCompetition()?.ExpiresAt;
    public static TimeSpan RecentCompetitionRetention { get; } = TimeSpan.FromMinutes(5);
    public string? IncompatibleTrackName => incompatibleTrackName;

    protected override async ValueTask OnStartAsync(CancellationToken cancellationToken)
    {
        runCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        subscription = await Context.Telemetry.SubscribeAsync(ModuleId, runCancellation.Token).ConfigureAwait(false);
        await Context.Hud.AttachAsync(this, cancellationToken).ConfigureAwait(false);
        persistenceQueue = Channel.CreateUnbounded<PersistenceCommand>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
        persistenceTask = Task.Run(() => PersistAsync(persistenceQueue.Reader), CancellationToken.None);
        PublishWaitingForCompetition();
        runTask = Task.Run(() => ConsumeAsync(subscription.Frames, runCancellation.Token), CancellationToken.None);
    }

    protected override async ValueTask OnStopAsync(CancellationToken cancellationToken)
    {
        runCancellation?.Cancel();
        if (subscription is not null) await subscription.DisposeAsync().ConfigureAwait(false);
        if (runTask is not null)
        {
            try { await runTask.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }

        persistenceQueue?.Writer.TryComplete();
        if (persistenceTask is not null) await persistenceTask.ConfigureAwait(false);

        await Context.Hud.DetachAsync(Id, cancellationToken).ConfigureAwait(false);
        subscription = null;
        runTask = null;
        persistenceQueue = null;
        persistenceTask = null;
        runCancellation?.Dispose();
        runCancellation = null;
        ResetCompetitionSession();
        competitionActive = false;
        ClearRecentCompetition();
        Volatile.Write(ref snapshot, null);
    }

    private async Task ConsumeAsync(System.Threading.Channels.ChannelReader<TelemetryFrame> frames, CancellationToken cancellationToken)
    {
        await foreach (var frame in frames.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            Observe(frame);
        }
    }

    public void Observe(TelemetryFrame frame)
    {
        var raw = frame.Raw;
        if (!TelemetryContextClassifier.IsCompetition(raw))
        {
            if (competitionActive && TelemetryContextClassifier.IsDriving(raw))
            {
                nonCompetitionDrivingSince ??= frame.ArrivalTime;
                competitionSignalSuspended = true;
                if (frame.ArrivalTime - nonCompetitionDrivingSince >= TimeSpan.FromSeconds(2))
                {
                    var learnedRoute = TryCompletePendingRouteLearningAtCompetitionEnd();
                    var recoveredFinalLap = learnedRoute ? null : TryCompletePendingLapAtCompetitionEnd();
                    if (!learnedRoute &&
                        recoveredFinalLap is null &&
                        lapArmed &&
                        currentSamples.Count >= 20)
                    {
                        RecordDiagnostic(
                            "lap.not-settled-on-exit",
                            "比赛会话结束时仍有一圈未能结算。",
                            true,
                            frame.ArrivalTime,
                            new Dictionary<string, string>
                            {
                                ["samples"] = currentSamples.Count.ToString(),
                                ["validProjectionSamples"] = validProjectionSamples.ToString(),
                                ["invalidProjectionSamples"] = invalidProjectionSamples.ToString(),
                                ["lastProgressMeters"] = lastS.ToString("0.0")
                            });
                    }
                    RecordDiagnostic(
                        "race.inferred-end",
                        recoveredFinalLap is null
                            ? "持续收到非比赛驾驶帧，程序据此结束当前比赛会话。"
                            : "持续收到非比赛驾驶帧，程序在结束会话前补记了最后一圈。",
                        false,
                        frame.ArrivalTime,
                        new Dictionary<string, string>
                        {
                            ["sessionId"] = sessionId.ToString(),
                            ["lapNumber"] = raw.LapNumber.ToString(),
                            ["currentLap"] = raw.CurrentLap.ToString("0.000"),
                            ["lastLap"] = raw.LastLap.ToString("0.000"),
                            ["recoveredFinalLap"] = (recoveredFinalLap is not null).ToString()
                        });
                    lastInferredCompetitionEndAt = frame.ArrivalTime;
                    lastInferredCompetitionRaceTime =
                        lastCompetitionFrame?.Raw.CurrentRaceTime ?? raw.CurrentRaceTime;
                    LogIfInitialized(learnedRoute
                        ? $"Sustained non-competition driving ended session {sessionId}; learned a route from the confirmed event exit."
                        : recoveredFinalLap is null
                            ? $"Sustained non-competition driving ended session {sessionId}."
                            : $"Sustained non-competition driving ended session {sessionId}; recovered final lap {recoveredFinalLap.Id} without a timer reset.");
                    EndCompetitionSession();
                }
                PublishWaitingForCompetition(frame);
                return;
            }

            nonCompetitionDrivingSince = null;
            if (competitionActive && !competitionSignalSuspended)
            {
                competitionSignalSuspended = true;
                LogIfInitialized(
                    $"Competition signal suspended; preserving session {sessionId} and the current lap. " +
                    $"lap={raw.LapNumber}, current={raw.CurrentLap:0.000}, last={raw.LastLap:0.000}, race={raw.CurrentRaceTime:0.000}, " +
                    $"capturePoints={capture.Count}, pointToPointEligible={captureEligibleForPointToPointFinish}.");
            }
            PublishWaitingForCompetition(frame);
            return;
        }

        nonCompetitionDrivingSince = null;

        if (competitionActive && competitionSignalSuspended)
        {
            if (ShouldBeginNewCompetitionSession(raw))
            {
                var recoveredFinalLap = TryCompletePendingLapAtCompetitionEnd();
                LogIfInitialized(recoveredFinalLap is null
                    ? $"Detected a reset race clock and cleared results; closing session {sessionId}."
                    : $"Detected a reset race clock and cleared results; recovered final lap {recoveredFinalLap.Id} before closing session {sessionId}.");
                ResetCompetitionSession();
                competitionActive = false;
            }
            else
            {
                competitionSignalSuspended = false;
                LogIfInitialized($"Competition signal resumed; continuing session {sessionId}.");
            }
        }

        if (!competitionActive)
        {
            BeginCompetitionSession(frame);
        }
        lastCompetitionFrame = frame;

        var lapNumberAdvanced = previousLapNumber is ushort previousNumber && raw.LapNumber > previousNumber;
        var lapNumberWentBack = previousLapNumber is ushort previousNumberForRewind && raw.LapNumber < previousNumberForRewind;
        var lapTimerWentBack = previousCurrentLap is float previousLapTime && raw.CurrentLap < previousLapTime - 0.2f;
        var raceTimerWentBack = previousCurrentRaceTime is float previousRaceTime &&
            raw.CurrentRaceTime < previousRaceTime - 0.2f;
        var sameLapTimerReset = !lapNumberAdvanced && lapTimerWentBack && !raceTimerWentBack &&
            previousLapNumber == raw.LapNumber;
        var timerResetAtStartLine = sameLapTimerReset &&
            (waitingForInitialStartLine ||
             previousCurrentLap is >= 5 && raw.CurrentLap <= 2 && IsStartLineGeometryPlausible(raw.Position));
        var crossingSignal = lapNumberAdvanced || timerResetAtStartLine;
        var lastLapUpdated = raw.LastLap > 0 && Math.Abs(raw.LastLap - lastLapValueAtLapStart) > 0.0005f;
        var pointToPointFinishReset = track?.LayoutKind == TrackLayoutKind.PointToPoint && lapNumberAdvanced;
        var finalLapSignal = !lapNumberWentBack && !raceTimerWentBack &&
                             (!lapTimerWentBack || pointToPointFinishReset) &&
                             lastLapUpdated &&
                             (track?.LayoutKind == TrackLayoutKind.PointToPoint || !crossingSignal) &&
                             IsPendingRouteCompleteAtFinish(raw.Position, raw.CurrentLap, true);
        var crossed = (crossingSignal || finalLapSignal) && frame.ArrivalTime - lastCrossingAt >= TimeSpan.FromSeconds(2);
        var initialStartCrossing = crossed && crossingSignal && !finalLapSignal && waitingForInitialStartLine;
        if (crossed)
        {
            lastCrossingAt = frame.ArrivalTime;
            LogIfInitialized(
                $"Lap crossing detected ({(finalLapSignal ? "last-lap update" : "timer/lap-number")}): lap={raw.LapNumber}, current={raw.CurrentLap:0.000}, last={raw.LastLap:0.000}, race={raw.CurrentRaceTime:0.000}, samples={currentSamples.Count}.");
        }
        var rewound = lapNumberWentBack || !crossed && lapTimerWentBack;
        if (rewound)
            LogIfInitialized(
                $"Rewind detected: lap={raw.LapNumber}, current={raw.CurrentLap:0.000}; preserving session {sessionId}.");
        if (crossed) waitingForInitialStartLine = false;

        if (!forceTrackLearning && compatibleTracks.Count > 0 && !automaticMatchLocked)
        {
            ObserveAutomaticTrackMatch(frame, initialStartCrossing);
            UpdatePrevious(raw);
            return;
        }

        if (track is null)
        {
            if (rewound)
            {
                capture.Clear();
                captureArmed = false;
                captureStartConfirmed = false;
                captureEligibleForPointToPointFinish = false;
                PublishLearning(frame, TrackLearningPhase.WaitingForStartLine,
                    "检测到倒转，已取消本次学习。",
                    "重新通过起点，再完整跑完一条环道或定点赛道。", 0);
                UpdatePrevious(raw);
                return;
            }

            if (!captureArmed)
            {
                capture.Clear();
                captureArmed = true;
                captureStartConfirmed = crossed;
                if (crossed) captureEligibleForPointToPointFinish = true;
                AddCapturePoint(raw.Position);
                PublishLearning(frame, TrackLearningPhase.WaitingForStartLine,
                    "已进入比赛，等待起点。",
                    "起点确认后开始学习。", 0);
            }
            else
            {
                AddCapturePoint(raw.Position);
                var inferredLayout = capture.Count >= 40
                    ? TrackAlgorithms.InferLayout(capture)
                    : TrackLayoutKind.Circuit;
                var hasCompletedTime = raw.LastLap >= 10 || raw.CurrentLap >= 10 || previousCurrentLap is >= 10;
                var pointToPointFinished = capture.Count >= 40 &&
                                           inferredLayout == TrackLayoutKind.PointToPoint &&
                                           captureEligibleForPointToPointFinish &&
                                           hasCompletedTime &&
                                           (lastLapUpdated || lapNumberAdvanced);

                if (pointToPointFinished)
                {
                    CompleteTemplate(frame.Source, TrackLayoutKind.PointToPoint);
                    lapArmed = false;
                }
                else if (crossed && !captureStartConfirmed)
                {
                    capture.Clear();
                    captureStartConfirmed = true;
                    captureEligibleForPointToPointFinish = true;
                    lastLapValueAtLapStart = raw.LastLap;
                    AddCapturePoint(raw.Position);
                    PublishLearning(frame, TrackLearningPhase.CapturingReferenceLap,
                        "正在学习参考路线。",
                        "完整跑完路线，期间不要倒带、传送或退出。", 0);
                }
                else if (crossed && captureStartConfirmed && capture.Count >= 40)
                {
                    var layout = TrackAlgorithms.InferLayout(capture);
                    CompleteTemplate(frame.Source, layout);
                    if (layout == TrackLayoutKind.Circuit)
                    {
                        BeginLap(raw.CurrentLap, raw.LastLap, frame.ArrivalTime);
                        lapArmed = true;
                    }
                    else
                    {
                        lapArmed = false;
                    }
                }
                else if (crossed)
                {
                    capture.Clear();
                    captureStartConfirmed = true;
                    captureEligibleForPointToPointFinish = true;
                    lastLapValueAtLapStart = raw.LastLap;
                    AddCapturePoint(raw.Position);
                    PublishLearning(frame, TrackLearningPhase.CapturingReferenceLap,
                        "轨迹不足，已从本次起点重新开始。",
                        "完整跑完路线即可保存。", 0);
                }
                else
                {
                    var referenceSeconds = raw.BestLap > 10 ? raw.BestLap : raw.LastLap > 10 ? raw.LastLap : 90;
                    PublishLearning(frame,
                        captureStartConfirmed ? TrackLearningPhase.CapturingReferenceLap : TrackLearningPhase.WaitingForStartLine,
                        captureStartConfirmed
                            ? $"正在学习 · {raw.CurrentLap:0.0} 秒 · {capture.Count} 个轨迹点"
                            : $"正在确认起点 · {capture.Count} 个轨迹点",
                        captureStartConfirmed
                            ? "完整跑完路线。"
                            : "起点确认后重新采集。",
                        captureStartConfirmed ? Math.Clamp(raw.CurrentLap / referenceSeconds, 0.02, 0.95) : 0);
                }
            }

            UpdatePrevious(raw);
            return;
        }

        UpdateTrackMatchPlausibility(raw.Position);

        if (waitingForInitialStartLine && !crossed &&
            !(track.LayoutKind == TrackLayoutKind.PointToPoint && lapArmed))
        {
            PublishAwaitingStartLine(frame);
            UpdatePrevious(raw);
            return;
        }

        if (rewound)
        {
            RecoverFromRewind(frame);
        }

        if (crossed)
        {
            if (initialStartCrossing)
            {
                BeginLap(raw.CurrentLap, raw.LastLap, frame.ArrivalTime);
                lapArmed = true;
            }
            else
            {
                LapRecord? completedLap = null;
                var performanceClass = PerformanceClassCatalog.Resolve(frame.Raw.CarClass, frame.Raw.CarPerformanceIndex);
                var historicalReference = HistoricalFastestLap(performanceClass);
                if (lapArmed && currentSamples.Count >= 20) completedLap = CompleteLap(frame);
                if (completedLap is null && lapArmed)
                {
                    RecordDiagnostic(
                        "lap.not-settled",
                        "检测到过线或完赛信号，但当前圈未能结算。",
                        true,
                        frame.ArrivalTime,
                        new Dictionary<string, string>
                        {
                            ["samples"] = currentSamples.Count.ToString(),
                            ["validProjectionSamples"] = validProjectionSamples.ToString(),
                            ["invalidProjectionSamples"] = invalidProjectionSamples.ToString(),
                            ["currentLap"] = raw.CurrentLap.ToString("0.000"),
                            ["lastLap"] = raw.LastLap.ToString("0.000")
                        });
                }
                if (completedLap is not null)
                {
                    heldComparisons = BuildCompletedComparisons(completedLap);
                    heldCumulativeHistoricalDeltaSeconds = CompletedLapDelta(completedLap, historicalReference);
                    heldCumulativeHistoricalDeltaUntil = heldCumulativeHistoricalDeltaSeconds is null
                        ? DateTimeOffset.MinValue
                        : frame.ArrivalTime + CumulativeHistoricalDeltaDisplayDuration;
                    holdComparisonsUntil = frame.ArrivalTime + CompletedLapHoldDuration();
                }
                if (finalLapSignal)
                {
                    lapArmed = false;
                }
                else
                {
                    BeginLap(raw.CurrentLap, raw.LastLap, frame.ArrivalTime);
                    lapArmed = true;
                }
            }
        }

        var projection = TrackAlgorithms.ProjectConstrained(
            track.Points,
            raw.Position.X,
            raw.Position.Y,
            raw.Position.Z,
            projectionIndex);
        var validProjection = IsProjectionValid(track, projection);
        if (!validProjection)
        {
            // A dropped packet, rewind, or a sparse route can move farther than the
            // constrained projection window. Reacquire globally before treating the
            // route itself as wrong.
            var reacquired = TrackAlgorithms.ProjectRange(
                track.Points,
                raw.Position.X,
                raw.Position.Y,
                raw.Position.Z,
                0,
                track.Points.Count - 2);
            if (IsProjectionValid(track, reacquired))
            {
                projection = reacquired;
                validProjection = true;
            }
        }

        if (!rewound &&
            automaticMatchLocked &&
            !validProjection &&
            ObserveSevereDeviation(frame))
        {
            RestartAutomaticTrackMatchAfterSevereDeviation(frame);
            UpdatePrevious(raw);
            return;
        }

        if (validProjection || rewound) ResetSevereDeviationEvidence();
        if (validProjection && lapArmed)
        {
            projectionIndex = projection.SegmentIndex;
            confidentProjectionCount++;
            lastS = projection.S;
            validProjectionSamples++;
            trackMatchEverPlausible = true;
            currentTrackGeometryPlausible = true;
        }
        else
        {
            confidentProjectionCount = Math.Max(0, confidentProjectionCount - 3);
            invalidProjectionSamples++;
        }

        var matchState = confidentProjectionCount >= 30 ? TrackMatchState.Confirmed : confidentProjectionCount > 5 ? TrackMatchState.Candidate : TrackMatchState.Unknown;
        if (validProjection && lapArmed)
        {
            currentSamples.Add(new LapSample(
                projection.S, raw.CurrentLap, raw.Speed, raw.CurrentEngineRpm, raw.Gear,
                frame.Normalized.AccelRatio, frame.Normalized.BrakeRatio, 0,
                raw.Position.X, raw.Position.Y, raw.Position.Z));
        }

        var currentSector = sectors.Count == 0 ? 0 : Math.Clamp(sectors.ToList().FindLastIndex(sector => lastS >= sector.StartS), 0, sectors.Count - 1);
        UpdateSectorProgress(currentSector, raw.CurrentLap, rewound);
        PublishComparison(frame, matchState, currentSector, validProjection && lapArmed);
        UpdatePrevious(raw);
    }

    private void ObserveAutomaticTrackMatch(TelemetryFrame frame, bool initialStartCrossing)
    {
        var raw = frame.Raw;
        UpdateAutomaticMatchGeometryPlausibility(raw.Position);

        if (initialStartCrossing)
        {
            StartAutomaticTrackMatch(
                frame,
                allowMidRouteStart: false,
                startedAtConfirmedLine: true,
                reason: "confirmed-start/all-layouts");
        }
        else if (!automaticMatchStarted &&
                 !automaticMatchRejected &&
                 CanStartAutomaticTrackIdentification(raw.Position))
        {
            StartAutomaticTrackMatch(
                frame,
                allowMidRouteStart: false,
                startedAtConfirmedLine: false,
                reason: "event-start/all-layouts");
        }

        if (automaticMatchRejected)
        {
            PublishAutomaticMatchRejected(frame);
            return;
        }

        if (!automaticMatchStarted)
        {
            PublishAwaitingStartLine(frame);
            return;
        }

        automaticMatchFrames.Add(frame);
        if (automaticMatchPreviousPosition is Vector3F previous)
        {
            var dx = raw.Position.X - previous.X;
            var dy = raw.Position.Y - previous.Y;
            var dz = raw.Position.Z - previous.Z;
            var movement = Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
            if (movement is > 0.05 and < 100) automaticMatchTravelMeters += movement;
        }
        automaticMatchPreviousPosition = raw.Position;

        foreach (var candidate in automaticMatchCandidates.Values)
            candidate.ObserveCoarse(raw.Position);
        SelectFineMatchCandidates();
        foreach (var candidate in automaticMatchCandidates.Values.Where(candidate => candidate.IsFineCandidate))
            candidate.ObserveProjection(raw);

        var ranked = automaticMatchCandidates.Values
            .Where(candidate => candidate.IsFineCandidate &&
                                !candidate.IsEliminated &&
                                candidate.ValidObservations >= 4)
            .OrderBy(candidate => candidate.Quality)
            .ThenByDescending(candidate => candidate.ProgressMeters)
            .ToArray();
        var best = ranked.FirstOrDefault();
        var second = best is null
            ? null
            : ranked.FirstOrDefault(candidate => candidate.Saved.Track.Id != best.Saved.Track.Id);

        if (best is not null && IsAutomaticMatchConfident(best, second))
        {
            LockAutomaticTrackMatch(best, second, frame.ArrivalTime);
            if (waitingForInitialStartLine)
            {
                PublishAwaitingStartLine(frame);
            }
            else
            {
                PublishComparison(
                    frame,
                    confidentProjectionCount >= 30 ? TrackMatchState.Confirmed : TrackMatchState.Candidate,
                    CurrentSectorIndex(),
                    validProjectionSamples > 0);
            }
            return;
        }

        var elapsed = frame.ArrivalTime - automaticMatchStartedAt;
        var travelLimit = AutomaticMatchTravelLimitMeters();
        if (automaticMatchTravelMeters >= travelLimit ||
            elapsed >= AutomaticMatchMaximumDuration)
        {
            automaticMatchRejected = true;
            automaticMatchStarted = false;
            currentTrackGeometryPlausible = false;
            var evidence = ranked.Take(3)
                .Select(candidate =>
                    $"{candidate.Saved.Track.Name}: valid={candidate.ValidRatio:P0}, mean={candidate.MeanDistanceMeters:0.0}m, progress={candidate.ProgressMeters:0}m")
                .ToArray();
            LogIfInitialized(
                $"Automatic track identification rejected after {automaticMatchTravelMeters:0}m/{elapsed.TotalSeconds:0.0}s " +
                $"(limit={travelLimit:0}m). " +
                (evidence.Length == 0 ? "No start-compatible candidates." : string.Join("; ", evidence)));
            PublishAutomaticMatchRejected(frame);
            PublishTrackMatchDiagnostics(frame.ArrivalTime, "未找到可靠匹配", ranked);
            return;
        }

        PublishAutomaticMatching(frame, best, second);
        PublishTrackMatchDiagnostics(frame.ArrivalTime, "精匹配中", ranked);
    }

    private double AutomaticMatchTravelLimitMeters()
    {
        var shortestEligibleRoute = automaticMatchCandidates.Values
            .Where(candidate => candidate.IsFineCandidate && !candidate.IsEliminated)
            .Select(candidate => candidate.Saved.Track.LengthMeters)
            .DefaultIfEmpty(AutomaticMatchMaximumTravelMeters)
            .Min();
        return Math.Min(
            AutomaticMatchMaximumTravelMeters,
            Math.Max(300, shortestEligibleRoute * 0.97));
    }

    private bool CanStartAutomaticTrackIdentification(Vector3F position) =>
        compatibleTracks.Any(candidate => candidate.IsStartEligible(position, allowMidRouteStart: false));

    private void StartAutomaticTrackMatch(
        TelemetryFrame frame,
        bool allowMidRouteStart,
        bool startedAtConfirmedLine,
        string reason)
    {
        automaticMatchStarted = true;
        automaticMatchRejected = false;
        automaticMatchStartedMidLap = allowMidRouteStart;
        automaticMatchStartedAtConfirmedLine = startedAtConfirmedLine;
        automaticMatchTravelMeters = 0;
        automaticMatchPreviousPosition = null;
        automaticMatchStartedAt = frame.ArrivalTime;
        automaticMatchFrames.Clear();
        automaticMatchCandidates.Clear();
        automaticMatchCoarseEliminated.Clear();
        var evaluated = compatibleTracks
            .Select(candidate => new AutomaticMatchCandidate(
                candidate,
                allowMidRouteStart,
                frame.Raw))
            .OrderBy(candidate => candidate.StartDistanceMeters)
            .ThenBy(candidate => candidate.Saved.Track.Name, StringComparer.Ordinal)
            .ToArray();
        var eligible = evaluated.Where(candidate => candidate.StartEligible).ToArray();
        automaticMatchCoarseEligibleCount = eligible.Length;
        foreach (var candidate in eligible)
            automaticMatchCandidates[candidate.Saved.Track.Id] = candidate;
        SelectFineMatchCandidates();
        foreach (var candidate in evaluated.Where(candidate => !candidate.StartEligible))
        {
            automaticMatchCoarseEliminated.Add(candidate.ToDiagnostic(
                "起点粗筛",
                candidate.EliminationReason));
        }
        LogIfInitialized(
            $"Automatic track identification started: routes={compatibleTracks.Count}, " +
            $"coarseEligible={automaticMatchCoarseEligibleCount}, " +
            $"fine={automaticMatchCandidates.Values.Count(candidate => candidate.IsFineCandidate)}, " +
            $"mode={reason}.");
        PublishTrackMatchDiagnostics(
            frame.ArrivalTime,
            automaticMatchCandidates.Count == 0 ? "起点粗筛无候选" : "粗筛完成",
            []);
    }

    private void SelectFineMatchCandidates()
    {
        // FH6 Data Out has no TrackOrdinal. Keep all start-compatible routes in
        // the cheap geometric stage, but spend continuous 3D projection work on
        // only the best direction/curvature/start-distance candidates.
        var selectedIds = automaticMatchCandidates.Values
            .Where(candidate => !candidate.IsEliminated)
            .OrderBy(candidate => candidate.CoarseQuality)
            .ThenBy(candidate => candidate.Saved.Track.Name, StringComparer.Ordinal)
            .Take(MaximumFineMatchCandidates)
            .Select(candidate => candidate.Saved.Track.Id)
            .ToHashSet();
        foreach (var candidate in automaticMatchCandidates.Values)
            candidate.IsFineCandidate = selectedIds.Contains(candidate.Saved.Track.Id);
    }

    private static bool IsAutomaticMatchConfident(
        AutomaticMatchCandidate best,
        AutomaticMatchCandidate? second)
    {
        var tolerance = Math.Max(8, best.EffectiveToleranceMeters * 0.75);
        if (best.ValidObservations < 10 ||
            best.ValidRatio < 0.80 ||
            best.ProgressMeters < best.RequiredProgressMeters ||
            best.MeanDistanceMeters > tolerance)
        {
            return false;
        }

        if (second is null || second.ValidObservations < 4) return true;
        var qualityMargin = second.Quality - best.Quality;
        var progressLead = best.ProgressMeters - second.ProgressMeters;
        if (best.SharesStartWith(second) &&
            best.ProgressMeters < SharedStartDecisionMeters &&
            qualityMargin < 10)
        {
            return false;
        }
        return qualityMargin >= 6 || qualityMargin >= 3 && progressLead >= 40;
    }

    private void LockAutomaticTrackMatch(
        AutomaticMatchCandidate winner,
        AutomaticMatchCandidate? runnerUp,
        DateTimeOffset matchedAt)
    {
        var deferRecordingUntilNextStart =
            automaticMatchStartedMidLap ||
            (!automaticMatchStartedAtConfirmedLine &&
             winner.Saved.Track.LayoutKind == TrackLayoutKind.Circuit);
        track = winner.Saved.Track;
        trackSpatialIndex = winner.Saved.RouteSpatialIndex;
        sectors = winner.Saved.Sectors;
        sectorStartTimes = new double[sectors.Count];
        completedSectorTimes = new double?[sectors.Count];
        incompatibleTrackName = null;
        forceTrackLearning = false;
        automaticMatchLocked = true;
        automaticMatchRejected = false;
        automaticMatchStarted = false;
        store.SetAppSetting(selectionSettingKey, track.Id.ToString());
        ReloadVisibleLaps();

        if (deferRecordingUntilNextStart)
        {
            currentSamples.Clear();
            projectionIndex = 0;
            confidentProjectionCount = 0;
            validProjectionSamples = 0;
            invalidProjectionSamples = 0;
            lastS = 0;
            lapArmed = false;
            waitingForInitialStartLine = true;
        }
        else
        {
            var firstFrame = automaticMatchFrames[0];
            BeginLap(firstFrame.Raw.CurrentLap, firstFrame.Raw.LastLap, firstFrame.ArrivalTime);
            lapArmed = true;
            waitingForInitialStartLine = false;
            ReplayAutomaticMatchFrames();
        }
        trackMatchEverPlausible = true;
        currentTrackGeometryPlausible = true;
        ResetSevereDeviationEvidence();

        LogIfInitialized(
            $"Automatic track identified {track.Name} ({track.Id}): valid={winner.ValidRatio:P0}, " +
            $"mean={winner.MeanDistanceMeters:0.0}m, progress={winner.ProgressMeters:0}m, " +
            $"runnerUp={(runnerUp is null ? "none" : $"{runnerUp.Saved.Track.Name}/{runnerUp.Quality:0.0}")}, " +
            $"recording={(deferRecordingUntilNextStart ? "next-start" : "current-run")}.");
        Volatile.Write(ref trackMatchDiagnostics, new TrackMatchDiagnostics(
            matchedAt,
            "已锁定",
            compatibleTracks.Count,
            automaticMatchCoarseEligibleCount,
            1,
            [winner.ToDiagnostic("已锁定")],
            automaticMatchCoarseEliminated.Take(3).ToArray()));
        automaticMatchFrames.Clear();
        automaticMatchCandidates.Clear();
    }

    private void ReplayAutomaticMatchFrames()
    {
        if (track is null) return;
        projectionIndex = 0;
        confidentProjectionCount = 0;
        validProjectionSamples = 0;
        invalidProjectionSamples = 0;
        lastS = 0;
        foreach (var frame in automaticMatchFrames)
        {
            var raw = frame.Raw;
            var projection = TrackAlgorithms.ProjectConstrained(
                track.Points,
                raw.Position.X,
                raw.Position.Y,
                raw.Position.Z,
                projectionIndex,
                searchAhead: 48);
            var valid = projection.IsValid &&
                        projection.DistanceMeters <= track.MatchingToleranceMeters &&
                        projection.ElevationErrorMeters <= 10;
            if (!valid)
            {
                invalidProjectionSamples++;
                continue;
            }

            projectionIndex = projection.SegmentIndex;
            lastS = projection.S;
            confidentProjectionCount++;
            validProjectionSamples++;
            currentSamples.Add(new LapSample(
                projection.S,
                raw.CurrentLap,
                raw.Speed,
                raw.CurrentEngineRpm,
                raw.Gear,
                frame.Normalized.AccelRatio,
                frame.Normalized.BrakeRatio,
                0,
                raw.Position.X,
                raw.Position.Y,
                raw.Position.Z));
            UpdateSectorProgress(CurrentSectorIndex(), raw.CurrentLap, false);
        }
    }

    private void PublishAutomaticMatching(
        TelemetryFrame frame,
        AutomaticMatchCandidate? best,
        AutomaticMatchCandidate? second)
    {
        var placeholders = Enumerable.Range(0, 4)
            .Select(index => new SectorComparison(index, null, null, null, null, SectorColorState.Gray, index == 0))
            .ToArray();
        var confidence = best?.Confidence(second) ?? 0;
        var candidateText = best is null
            ? "正在识别赛道…"
            : $"候选：{best.Saved.Track.Name} · 已验证 {best.ProgressMeters:0} m";
        PublishCompetitionState(new LapHudState(
            frame.ArrivalTime,
            frame.Source,
            true,
            TrackLearningPhase.MatchingTrack,
            candidateText,
            "继续沿比赛路线驾驶。",
            best is null ? TrackMatchState.Unknown : TrackMatchState.Candidate,
            confidence,
            "正在识别赛事",
            0,
            placeholders,
            Math.Clamp(automaticMatchTravelMeters / AutomaticMatchTravelLimitMeters(), 0, 0.95),
            0,
            false));
    }

    private void PublishAutomaticMatchRejected(TelemetryFrame frame)
    {
        var placeholders = Enumerable.Range(0, 4)
            .Select(index => new SectorComparison(index, null, null, null, null, SectorColorState.Gray, index == 0))
            .ToArray();
        PublishCompetitionState(new LapHudState(
            frame.ArrivalTime,
            frame.Source,
            true,
            TrackLearningPhase.MatchingTrack,
            AutomaticMatchRejectedStatus,
            AutomaticMatchRejectedInstruction,
            TrackMatchState.Unknown,
            0,
            AutomaticMatchRejectedTrackName,
            0,
            placeholders,
            0,
            0,
            false));
    }

    private void PublishTrackMatchDiagnostics(
        DateTimeOffset updatedAt,
        string state,
        IReadOnlyList<AutomaticMatchCandidate> ranked)
    {
        var orderedActive = (ranked.Count > 0
                ? ranked
                : automaticMatchCandidates.Values
                    .Where(candidate => candidate.IsFineCandidate && !candidate.IsEliminated)
                    .OrderBy(candidate => candidate.CoarseQuality)
                    .ToArray())
            .Take(3)
            .Select(candidate => candidate.ToDiagnostic("精匹配"))
            .ToArray();
        var eliminated = automaticMatchCandidates.Values
            .Where(candidate => candidate.IsEliminated)
            .OrderBy(candidate => candidate.StartDistanceMeters)
            .Select(candidate => candidate.ToDiagnostic("已淘汰"))
            .Concat(automaticMatchCandidates.Values
                .Where(candidate => !candidate.IsEliminated && !candidate.IsFineCandidate)
                .OrderBy(candidate => candidate.CoarseQuality)
                .Select(candidate => candidate.ToDiagnostic(
                    "粗筛候补",
                    "方向/曲率/起点距离排名未进入精匹配集合（当前最多 12 条）")))
            .Concat(automaticMatchCoarseEliminated)
            .Take(3)
            .ToArray();

        Volatile.Write(ref trackMatchDiagnostics, new TrackMatchDiagnostics(
            updatedAt,
            state,
            compatibleTracks.Count,
            automaticMatchCoarseEligibleCount,
            automaticMatchCandidates.Values.Count(candidate =>
                candidate.IsFineCandidate && !candidate.IsEliminated),
            orderedActive,
            eliminated));
    }

    private void UpdateAutomaticMatchGeometryPlausibility(Vector3F position)
    {
        if (automaticMatchStarted || automaticMatchRejected) return;
        currentTrackGeometryPlausible = compatibleTracks.Any(candidate =>
        {
            var margin = StartGateMeters(candidate.Track);
            return position.X >= candidate.Track.MinX - margin && position.X <= candidate.Track.MaxX + margin &&
                   position.Y >= candidate.Track.MinY - margin && position.Y <= candidate.Track.MaxY + margin &&
                   position.Z >= candidate.Track.MinZ - margin && position.Z <= candidate.Track.MaxZ + margin;
        });
        if (currentTrackGeometryPlausible) trackMatchEverPlausible = true;
    }

    private int CurrentSectorIndex() =>
        sectors.Count == 0
            ? 0
            : Math.Clamp(sectors.ToList().FindLastIndex(sector => lastS >= sector.StartS), 0, sectors.Count - 1);

    private static double StartGateMeters(TrackTemplate candidate) =>
        Math.Max(30, candidate.MatchingToleranceMeters * 2);

    private static bool IsProjectionValid(TrackTemplate candidate, ProjectionResult projection) =>
        projection.IsValid &&
        projection.DistanceMeters <= candidate.MatchingToleranceMeters &&
        projection.ElevationErrorMeters <= 10;

    private static double SevereDeviationDistanceMeters(TrackTemplate candidate) =>
        Math.Max(
            candidate.Category switch
            {
                "越野" => 250,
                "泥地" => 180,
                "山道" => 150,
                _ => 120
            },
            candidate.MatchingToleranceMeters * 6);

    private static TimeSpan SevereDeviationDuration(TrackTemplate candidate) =>
        candidate.Category switch
        {
            "越野" => TimeSpan.FromSeconds(3),
            "泥地" => TimeSpan.FromSeconds(2.5),
            _ => TimeSpan.FromSeconds(2)
        };

    private static double SevereDeviationTravelMeters(TrackTemplate candidate) =>
        candidate.Category switch
        {
            "越野" => 100,
            "泥地" => 75,
            _ => 50
        };

    private bool ObserveSevereDeviation(TelemetryFrame frame)
    {
        if (track is null) return false;
        var position = frame.Raw.Position;
        var deviationDistance = SevereDeviationDistanceMeters(track);
        var nearest = trackSpatialIndex?.ProjectNearest(
            position.X,
            position.Y,
            position.Z,
            deviationDistance) ?? ProjectionResult.Invalid;
        if (nearest.IsValid && nearest.DistanceMeters <= deviationDistance)
        {
            ResetSevereDeviationEvidence();
            return false;
        }

        if (severeDeviationStartedAt is null)
        {
            severeDeviationStartedAt = frame.ArrivalTime;
            severeDeviationPreviousPosition = position;
            severeDeviationTravelMeters = 0;
            return false;
        }

        if (severeDeviationPreviousPosition is Vector3F previous)
        {
            var dx = position.X - previous.X;
            var dy = position.Y - previous.Y;
            var dz = position.Z - previous.Z;
            var movement = Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
            if (movement is > 0.05 and < 100) severeDeviationTravelMeters += movement;
        }
        severeDeviationPreviousPosition = position;

        return frame.ArrivalTime - severeDeviationStartedAt.Value >= SevereDeviationDuration(track) &&
               severeDeviationTravelMeters >= SevereDeviationTravelMeters(track);
    }

    private void RestartAutomaticTrackMatchAfterSevereDeviation(TelemetryFrame frame)
    {
        if (track is null) return;
        var rejectedTrack = track;
        var duration = severeDeviationStartedAt is DateTimeOffset startedAt
            ? frame.ArrivalTime - startedAt
            : TimeSpan.Zero;
        LogIfInitialized(
            $"Severe route deviation invalidated {rejectedTrack.Name} ({rejectedTrack.Id}) after " +
            $"{duration.TotalSeconds:0.0}s/{severeDeviationTravelMeters:0}m; restarting identification from the current route.");
        RecordDiagnostic(
            "track.rematch",
            $"赛道“{rejectedTrack.Name}”出现持续大幅偏离，程序已重新开始识别。",
            true,
            frame.ArrivalTime,
            new Dictionary<string, string>
            {
                ["trackId"] = rejectedTrack.Id.ToString(),
                ["durationSeconds"] = duration.TotalSeconds.ToString("0.0"),
                ["travelMeters"] = severeDeviationTravelMeters.ToString("0.0")
            });

        track = null;
        trackSpatialIndex = null;
        sectors = [];
        sectorStartTimes = [];
        completedSectorTimes = [];
        currentSamples.Clear();
        lock (lapGate)
        {
            visibleLaps.Clear();
            visibleLapDetails.Clear();
        }
        projectionIndex = 0;
        confidentProjectionCount = 0;
        validProjectionSamples = 0;
        invalidProjectionSamples = 0;
        lastS = 0;
        lapArmed = false;
        waitingForInitialStartLine = true;
        heldComparisons = null;
        ClearCumulativeHistoricalDeltaDisplay();
        currentLapInvalidated = false;
        observedSectorIndex = 0;
        trackMatchEverPlausible = false;
        currentTrackGeometryPlausible = true;
        store.SetAppSetting(selectionSettingKey, string.Empty);
        ResetAutomaticMatchState();
        StartAutomaticTrackMatch(
            frame,
            allowMidRouteStart: true,
            startedAtConfirmedLine: false,
            reason: "severe-deviation/all-layouts");
        ObserveAutomaticTrackMatch(frame, initialStartCrossing: false);
    }

    private void ResetSevereDeviationEvidence()
    {
        severeDeviationStartedAt = null;
        severeDeviationTravelMeters = 0;
        severeDeviationPreviousPosition = null;
    }

    private void AddCapturePoint(Vector3F position)
    {
        var point = new TrackPoint(position.X, position.Y, position.Z, 0, 0, 0);
        if (capture.Count == 0 || capture[^1].DistanceSquaredTo(point) >= 4) capture.Add(point);
    }

    private void CompleteTemplate(TelemetrySourceKind source, TrackLayoutKind layoutKind)
    {
        if (layoutKind == TrackLayoutKind.Circuit &&
            capture.Count > 1 && capture[^1].DistanceSquaredTo(capture[0]) > 0.25)
        {
            capture.Add(capture[0]);
        }
        var name = source == TelemetrySourceKind.Simulator
            ? layoutKind == TrackLayoutKind.Circuit ? "模拟环道" : "模拟定点赛道"
            : $"学习赛道 {DateTime.Now:MM-dd HH:mm}";
        track = TrackAlgorithms.BuildTemplate(name, capture, layoutKind: layoutKind) with
        {
            Source = TelemetryDataPartition.TrackSource(source)
        };
        sectors = TrackAlgorithms.CreateSectors(track);
        sectorStartTimes = new double[sectors.Count];
        completedSectorTimes = new double?[sectors.Count];
        store.SaveTrack(track, sectors);
        compatibleTracks.RemoveAll(candidate => candidate.Track.Id == track.Id);
        var compatibleTrack = new CompatibleTrack(track, sectors);
        compatibleTracks.Add(compatibleTrack);
        trackSpatialIndex = compatibleTrack.RouteSpatialIndex;
        store.SetAppSetting(selectionSettingKey, track.Id.ToString());
        forceTrackLearning = false;
        automaticMatchLocked = true;
        incompatibleTrackName = null;
        ReloadVisibleLaps();
        capture.Clear();
        captureArmed = false;
        captureStartConfirmed = false;
        captureEligibleForPointToPointFinish = false;
        confidentProjectionCount = 0;
        projectionIndex = 0;
        LogIfInitialized($"Learned {track.LayoutKind} route {track.Id} with {track.Points.Count} points and {sectors.Count} sectors.");
    }

    private void BeginLap(double currentLap, float lastLap, DateTimeOffset arrivalTime)
    {
        currentSamples.Clear();
        projectionIndex = 0;
        lastS = 0;
        sectorStartTimes = new double[sectors.Count];
        completedSectorTimes = new double?[sectors.Count];
        if (sectorStartTimes.Length > 0) sectorStartTimes[0] = currentLap;
        observedSectorIndex = 0;
        ResetLiveCumulativeHistoricalDelta();
        validProjectionSamples = 0;
        invalidProjectionSamples = 0;
        currentLapInvalidated = false;
        lastLapValueAtLapStart = lastLap;
        lastRewindLapTime = null;
        lapStartedAt = arrivalTime - TimeSpan.FromSeconds(currentLap);
    }

    private LapRecord? CompleteLap(TelemetryFrame frame)
    {
        if (track is null) return null;
        var projectionRatio = validProjectionSamples / (double)Math.Max(1, validProjectionSamples + invalidProjectionSamples);
        var lapProjectionValid = projectionRatio >= 0.95 && !currentLapInvalidated;
        var sampledTotal = currentSamples.Count == 0 ? 0 : currentSamples.Max(sample => sample.ElapsedSeconds);
        var resolvedTime = ResolveCompletedLapTime(frame.Raw, sampledTotal);
        var totalSeconds = resolvedTime.Seconds;
        var lastLapChanged = frame.Raw.LastLap > 0 &&
                             Math.Abs(frame.Raw.LastLap - lastLapValueAtLapStart) > 0.0005f;
        var timingDifference = lastLapChanged
            ? Math.Abs(frame.Raw.LastLap - totalSeconds)
            : 0;
        var timingTolerance = Math.Max(2, totalSeconds * 0.05);
        if (lastLapChanged && timingDifference > timingTolerance)
        {
            RecordDiagnostic(
                "lap.lastlap-difference",
                "FH6 LastLap 与程序结算成绩差异超过允许范围。",
                true,
                frame.ArrivalTime,
                new Dictionary<string, string>
                {
                    ["lastLap"] = frame.Raw.LastLap.ToString("0.000"),
                    ["resolved"] = totalSeconds.ToString("0.000"),
                    ["sampled"] = sampledTotal.ToString("0.000"),
                    ["difference"] = timingDifference.ToString("0.000"),
                    ["source"] = resolvedTime.Source
                });
        }
        var times = new List<LapSegment>();
        for (var index = 0; index < sectors.Count; index++)
        {
            var duration = EstimateSectorTime(index) ?? 0;
            times.Add(new LapSegment(index, duration, lapProjectionValid && duration > 0));
        }
        var segmentTotal = times.Sum(segment => segment.TimeSeconds);
        var timingCorrection = totalSeconds - segmentTotal;
        if (times.Count > 0 && times.All(segment => segment.TimeSeconds > 0) &&
            Math.Abs(timingCorrection) <= Math.Max(1, totalSeconds * 0.03) &&
            times[^1].TimeSeconds + timingCorrection > 0)
        {
            times[^1] = times[^1] with { TimeSeconds = times[^1].TimeSeconds + timingCorrection };
        }

        var lapIsValid = lapProjectionValid && times.All(segment => segment.IsValid);
        var persistedSamples = DownsampleForStorage(currentSamples);
        var lap = new LapRecord(
            Guid.NewGuid(), track.Id, track.Direction, TrackAlgorithms.SectorSchemaVersion, sessionId,
            VehicleProfileFingerprint.FromFrame(frame), lapStartedAt,
            totalSeconds,
            lapIsValid,
            lapIsValid ? null : currentLapInvalidated ? "rewind-crossed-lap-boundary" :
                lapProjectionValid ? "sector-coverage-incomplete" : $"projection-low-confidence ({projectionRatio:P0})",
            times, persistedSamples);
        RegisterVisibleLap(lap);
        QueuePersistence(PersistenceCommand.Save(lap));
        LogIfInitialized(
            $"Queued lap {lap.Id}: session={sessionId}, total={lap.TotalSeconds:0.000}, timing={resolvedTime.Source}, " +
            $"sampled={sampledTotal:0.000}, current={frame.Raw.CurrentLap:0.000}, last={frame.Raw.LastLap:0.000}, race={frame.Raw.CurrentRaceTime:0.000}, " +
            $"valid={lap.IsValid}, storedSamples={lap.Samples.Count}, liveSamples={currentSamples.Count}, reason={lap.InvalidReason ?? "none"}.");
        return lap;
    }

    public void SelectTrack(Guid trackId)
    {
        if (store.LoadTrack(trackId) is not { } saved || !IsCompatible(saved.Track, saved.Sectors))
            throw new InvalidOperationException("所选赛道与当前遥测来源或分段算法版本不兼容。");

        track = saved.Track;
        var compatibleTrack = compatibleTracks.FirstOrDefault(candidate => candidate.Track.Id == saved.Track.Id) ??
                              new CompatibleTrack(saved.Track, saved.Sectors);
        trackSpatialIndex = compatibleTrack.RouteSpatialIndex;
        sectors = saved.Sectors;
        sectorStartTimes = new double[sectors.Count];
        completedSectorTimes = new double?[sectors.Count];
        forceTrackLearning = false;
        incompatibleTrackName = null;
        store.SetAppSetting(selectionSettingKey, track.Id.ToString());
        ReloadVisibleLaps();
        ResetCompetitionSession();
        competitionActive = false;
        ClearRecentCompetition();
        sessionId = Guid.NewGuid();
        PublishWaitingForCompetition();
    }

    public void ClearTrackSelection()
    {
        track = null;
        trackSpatialIndex = null;
        sectors = [];
        sectorStartTimes = [];
        completedSectorTimes = [];
        forceTrackLearning = false;
        incompatibleTrackName = null;
        lock (lapGate)
        {
            visibleLaps.Clear();
            visibleLapDetails.Clear();
        }
        store.SetAppSetting(selectionSettingKey, string.Empty);
        ResetCompetitionSession();
        competitionActive = false;
        ClearRecentCompetition();
        sessionId = Guid.NewGuid();
        PublishWaitingForCompetition();
    }

    public void DeleteLap(Guid lapId)
    {
        lock (lapGate)
        {
            if (visibleLaps.All(lap => lap.Id != lapId)) return;
        }
        lock (lapGate)
        {
            visibleLaps.RemoveAll(lap => lap.Id == lapId);
            visibleLapDetails.Remove(lapId);
        }
        QueuePersistence(PersistenceCommand.Delete(lapId));
    }

    public void DeleteTrackLaps(
        Guid trackId,
        bool deleteHistoricalBests,
        IReadOnlySet<int>? performanceClasses = null)
    {
        if (performanceClasses is { Count: 0 }) return;
        LapSummary[] trackLaps;
        lock (lapGate)
        {
            trackLaps = visibleLaps.Where(lap => lap.TrackId == trackId).ToArray();
        }
        if (trackLaps.Length == 0)
        {
            trackLaps = store.LoadLapSummaries(trackId, LazyForzaStore.MaxLapsPerTrack).ToArray();
        }

        var targetedLaps = trackLaps
            .Where(lap => performanceClasses is null || performanceClasses.Contains(lap.Vehicle.CarClass))
            .ToArray();
        var preserveLapIds = deleteHistoricalBests
            ? []
            : targetedLaps
                .Where(lap => lap.IsValid)
                .GroupBy(lap => lap.Vehicle.CarClass)
                .Select(group => group
                    .OrderBy(lap => lap.TotalSeconds)
                    .ThenBy(lap => lap.StartedAt)
                    .ThenBy(lap => lap.Id)
                    .First().Id)
                .ToArray();
        var preserved = preserveLapIds.ToHashSet();
        lock (lapGate)
        {
            visibleLaps.RemoveAll(lap =>
                lap.TrackId == trackId &&
                (performanceClasses is null || performanceClasses.Contains(lap.Vehicle.CarClass)) &&
                !preserved.Contains(lap.Id));
            foreach (var lapId in visibleLapDetails
                         .Where(pair =>
                             pair.Value.TrackId == trackId &&
                             (performanceClasses is null ||
                              performanceClasses.Contains(pair.Value.Vehicle.CarClass)) &&
                             !preserved.Contains(pair.Key))
                         .Select(pair => pair.Key)
                         .ToArray())
                visibleLapDetails.Remove(lapId);
        }
        QueuePersistence(PersistenceCommand.DeleteTrack(
            trackId,
            performanceClasses?.Order().ToArray(),
            preserveLapIds));
    }

    public void ResetTrackLearning()
    {
        forceTrackLearning = true;
        track = null;
        trackSpatialIndex = null;
        sectors = [];
        sectorStartTimes = [];
        completedSectorTimes = [];
        capture.Clear();
        currentSamples.Clear();
        lock (lapGate)
        {
            visibleLaps.Clear();
            visibleLapDetails.Clear();
        }
        previousLapNumber = null;
        previousCurrentLap = null;
        previousCurrentRaceTime = null;
        previousLastLap = null;
        lastLapValueAtLapStart = 0;
        lastRewindLapTime = null;
        lastCompetitionFrame = null;
        lastCrossingAt = DateTimeOffset.MinValue;
        captureArmed = false;
        captureStartConfirmed = false;
        captureEligibleForPointToPointFinish = false;
        lapArmed = false;
        waitingForInitialStartLine = true;
        competitionActive = false;
        competitionSignalSuspended = false;
        projectionIndex = 0;
        confidentProjectionCount = 0;
        validProjectionSamples = 0;
        invalidProjectionSamples = 0;
        heldComparisons = null;
        ClearCumulativeHistoricalDeltaDisplay();
        currentLapInvalidated = false;
        incompatibleTrackName = null;
        ClearRecentCompetition();
        store.SetAppSetting(selectionSettingKey, string.Empty);
        PublishWaitingForCompetition();
    }

    public void RenameCurrentTrack(Guid trackId, string name)
    {
        if (track?.Id == trackId) track = track with { Name = name, UpdatedAt = DateTimeOffset.UtcNow };
    }

    private void PublishWaitingForCompetition(TelemetryFrame? frame = null)
    {
        var placeholders = Enumerable.Range(0, 4)
            .Select(index => new SectorComparison(index, null, null, null, null, SectorColorState.Gray, index == 0))
            .ToArray();
        var sessionPreserved = competitionActive && competitionSignalSuspended;
        if (!sessionPreserved && !competitionActive) Volatile.Write(ref currentCompetitionSnapshot, null);
        Volatile.Write(ref snapshot, new LapHudState(
            frame?.ArrivalTime ?? DateTimeOffset.UtcNow, frame?.Source ?? TelemetrySourceKind.Live,
            false, TrackLearningPhase.WaitingForCompetition,
            sessionPreserved ? "比赛已暂停；当前圈已保留。" : "等待比赛。",
            sessionPreserved
                ? "返回驾驶后继续。"
                : "进入赛事后自动识别赛道。",
            TrackMatchState.Unknown, 0, track?.Name ??
                (incompatibleTrackName is null ? "等待比赛" : $"需重新学习：{incompatibleTrackName}"),
            0, placeholders, 0, VisibleLapCount, false)
        {
            CompetitionSessionId = sessionId,
            CurrentLapSeconds = Math.Max(0, frame?.Raw.CurrentLap ?? 0)
        });
    }

    private void PublishLearning(TelemetryFrame frame, TrackLearningPhase phase, string status, string instruction, double progress)
    {
        var placeholders = Enumerable.Range(0, 4)
            .Select(index => new SectorComparison(index, null, null, null, null, SectorColorState.Gray, index == 0))
            .ToArray();
        PublishCompetitionState(new LapHudState(
            frame.ArrivalTime, frame.Source, true, phase, status, instruction,
            TrackMatchState.Unknown, 0, "正在学习新赛道", 0, placeholders, progress, VisibleLapCount, true));
    }

    private void PublishAwaitingStartLine(TelemetryFrame frame)
    {
        var automaticIdentificationPending =
            !forceTrackLearning && compatibleTracks.Count > 0 && !automaticMatchLocked;
        var placeholders = Enumerable.Range(
                0,
                automaticIdentificationPending ? 4 : Math.Max(4, sectors.Count))
            .Select(index => new SectorComparison(index, null, null, null, null, SectorColorState.Gray, index == 0))
            .ToArray();
        PublishCompetitionState(new LapHudState(
            frame.ArrivalTime, frame.Source, true, TrackLearningPhase.WaitingForStartLine,
            automaticIdentificationPending
                ? "等待起点，随后自动识别赛道。"
                : "等待首次通过起终点线。",
            automaticIdentificationPending
                ? "比赛开始后自动识别。"
                : "计时开始后自动记录。",
            TrackMatchState.Unknown, 0,
            automaticIdentificationPending ? "等待识别赛事" : track?.Name ?? "等待起终点线", 0,
            placeholders, 0, automaticIdentificationPending ? 0 : VisibleLapCount, false));
    }

    private void PublishComparison(TelemetryFrame frame, TrackMatchState matchState, int currentSector, bool validProjection)
    {
        if (track is null) return;
        var showingPreviousLap = heldComparisons is not null && frame.ArrivalTime < holdComparisonsUntil;
        if (!showingPreviousLap)
        {
            heldComparisons = null;
        }

        var performanceClass = PerformanceClassCatalog.Resolve(frame.Raw.CarClass, frame.Raw.CarPerformanceIndex);
        if (currentSector > 0 && currentSector != liveCumulativeHistoricalDeltaSector)
        {
            liveCumulativeHistoricalDeltaSector = currentSector;
            liveCumulativeHistoricalDeltaSeconds =
                CurrentCumulativeHistoricalDelta(currentSector, performanceClass);
            liveCumulativeHistoricalDeltaUntil = liveCumulativeHistoricalDeltaSeconds is null
                ? DateTimeOffset.MinValue
                : frame.ArrivalTime + CumulativeHistoricalDeltaDisplayDuration;
        }
        if (heldCumulativeHistoricalDeltaSeconds is not null &&
            frame.ArrivalTime >= heldCumulativeHistoricalDeltaUntil)
        {
            heldCumulativeHistoricalDeltaSeconds = null;
        }
        if (liveCumulativeHistoricalDeltaSeconds is not null &&
            frame.ArrivalTime >= liveCumulativeHistoricalDeltaUntil)
        {
            liveCumulativeHistoricalDeltaSeconds = null;
        }

        IReadOnlyList<SectorComparison> comparisons;
        if (showingPreviousLap)
        {
            comparisons = heldComparisons!;
            currentSector = Math.Max(0, sectors.Count - 1);
        }
        else
        {
            var liveComparisons = new List<SectorComparison>();
            for (var index = 0; index < sectors.Count; index++)
            {
                var complete = index < currentSector;
                var current = complete ? EstimateSectorTime(index) : null;
                var (sessionBest, allTimeBest) = BestVisibleSectorTimes(index, performanceClass);
                var pointToPoint = track.LayoutKind == TrackLayoutKind.PointToPoint;
                var state = SectorColorClassifier.Classify(
                    current,
                    current is not null,
                    sessionBest,
                    allTimeBest,
                    considerCurrentSessionBest: !pointToPoint);
                var reference = pointToPoint ? allTimeBest : sessionBest ?? allTimeBest;
                liveComparisons.Add(new SectorComparison(index, current, sessionBest, allTimeBest,
                    current is not null && reference is not null ? current - reference : null,
                    state, index == currentSector));
            }

            comparisons = liveComparisons;
        }

        var cumulativeDelta = heldCumulativeHistoricalDeltaSeconds ?? liveCumulativeHistoricalDeltaSeconds;
        PublishCompetitionState(new LapHudState(
            frame.ArrivalTime, frame.Source, true,
            matchState == TrackMatchState.Confirmed ? TrackLearningPhase.ComparingLaps : TrackLearningPhase.MatchingTrack,
            matchState == TrackMatchState.Confirmed ? "赛道已确认。" : "正在确认赛道…",
            matchState == TrackMatchState.Confirmed ? "完成本圈即可保存。" : "继续沿比赛路线驾驶。",
            matchState, Math.Clamp(confidentProjectionCount / 30d, 0, 1), track.Name, currentSector,
            comparisons, 1, VisibleLapCount, validProjection && !currentLapInvalidated, showingPreviousLap)
        {
            CumulativeHistoricalDeltaSeconds = cumulativeDelta
        });
    }

    private void ResetCompetitionSession()
    {
        competitionSignalSuspended = false;
        nonCompetitionDrivingSince = null;
        Volatile.Write(ref currentCompetitionSnapshot, null);
        captureArmed = false;
        captureStartConfirmed = false;
        captureEligibleForPointToPointFinish = false;
        lapArmed = false;
        previousLapNumber = null;
        previousCurrentLap = null;
        previousCurrentRaceTime = null;
        previousLastLap = null;
        lastLapValueAtLapStart = 0;
        lastRewindLapTime = null;
        lastCompetitionFrame = null;
        lastCrossingAt = DateTimeOffset.MinValue;
        waitingForInitialStartLine = false;
        currentSamples.Clear();
        capture.Clear();
        projectionIndex = 0;
        confidentProjectionCount = 0;
        validProjectionSamples = 0;
        invalidProjectionSamples = 0;
        heldComparisons = null;
        ClearCumulativeHistoricalDeltaDisplay();
        currentLapInvalidated = false;
        observedSectorIndex = 0;
        trackMatchEverPlausible = false;
        currentTrackGeometryPlausible = true;
        ResetAutomaticMatchState();
    }

    private void EndCompetitionSession()
    {
        var completedSnapshot = Volatile.Read(ref currentCompetitionSnapshot);
        if (completedSnapshot is not null)
        {
            Volatile.Write(ref recentCompetition, new RecentCompetitionState(
                completedSnapshot,
                DateTimeOffset.UtcNow + RecentCompetitionRetention));
        }
        else
        {
            ClearRecentCompetition();
        }

        ResetCompetitionSession();
        competitionActive = false;
    }

    private LapRecord? TryCompletePendingLapAtCompetitionEnd()
    {
        if (lastCompetitionFrame is not { } frame ||
            !IsPendingRouteCompleteAtFinish(frame.Raw.Position, frame.Raw.CurrentLap, false)) return null;
        var performanceClass = PerformanceClassCatalog.Resolve(frame.Raw.CarClass, frame.Raw.CarPerformanceIndex);
        var historicalReference = HistoricalFastestLap(performanceClass);
        var completedLap = CompleteLap(frame);
        if (completedLap is null) return null;
        lapArmed = false;
        heldComparisons = BuildCompletedComparisons(completedLap);
        heldCumulativeHistoricalDeltaSeconds = CompletedLapDelta(completedLap, historicalReference);
        heldCumulativeHistoricalDeltaUntil = heldCumulativeHistoricalDeltaSeconds is null
            ? DateTimeOffset.MinValue
            : frame.ArrivalTime + CumulativeHistoricalDeltaDisplayDuration;
        holdComparisonsUntil = frame.ArrivalTime + CompletedLapHoldDuration();
        return completedLap;
    }

    private bool TryCompletePendingRouteLearningAtCompetitionEnd()
    {
        if (track is not null || !captureArmed || capture.Count < 40 || lastCompetitionFrame is not { } frame)
            return false;

        var lastLapChanged = frame.Raw.LastLap > 0 &&
                             Math.Abs(frame.Raw.LastLap - lastLapValueAtLapStart) > 0.0005f;
        var layout = TrackAlgorithms.InferLayout(capture);
        var capturedLength = CapturedRouteLength();
        var elapsed = Math.Max(frame.Raw.LastLap, Math.Max(frame.Raw.CurrentLap, frame.Raw.CurrentRaceTime));
        var constrainedPointToPointFallback = layout == TrackLayoutKind.PointToPoint &&
                                              captureEligibleForPointToPointFinish &&
                                              capturedLength >= 300 &&
                                              elapsed >= 10;
        LogIfInitialized(
            $"Route learning exit assessment: points={capture.Count}, length={capturedLength:0.0}m, layout={layout}, " +
            $"eligible={captureEligibleForPointToPointFinish}, current={frame.Raw.CurrentLap:0.000}, " +
            $"last={frame.Raw.LastLap:0.000}, race={frame.Raw.CurrentRaceTime:0.000}, lastLapChanged={lastLapChanged}, " +
            $"openRouteFallback={constrainedPointToPointFallback}.");

        if (!captureEligibleForPointToPointFinish || elapsed < 10) return false;
        if (!lastLapChanged && !constrainedPointToPointFallback) return false;

        CompleteTemplate(frame.Source, layout);
        lapArmed = false;
        return true;
    }

    private double CapturedRouteLength()
    {
        var length = 0d;
        for (var index = 1; index < capture.Count; index++)
            length += Math.Sqrt(capture[index].DistanceSquaredTo(capture[index - 1]));
        return length;
    }

    private bool IsPendingRouteCompleteAtFinish(Vector3F position, float currentLapTime, bool officialCompletionSignal)
    {
        if (track is null || !lapArmed || sectors.Count == 0 || currentSamples.Count < 20) return false;
        if (!officialCompletionSignal && lastRewindLapTime is float rewindTime && currentLapTime < rewindTime + 1)
            return false;
        var sampledTotal = currentSamples.Max(sample => sample.ElapsedSeconds);
        if (sampledTotal < 10) return false;

        var projectionRatio = validProjectionSamples / (double)Math.Max(1, validProjectionSamples + invalidProjectionSamples);
        if (projectionRatio < 0.90) return false;

        var endTolerance = Math.Max(8, Math.Min(30, track.MatchingToleranceMeters * 1.5));
        if (currentSamples.Max(sample => sample.S) < track.LengthMeters - endTolerance) return false;

        var finish = track.LayoutKind == TrackLayoutKind.PointToPoint ? track.Points[^1] : track.Points[0];
        var dx = finish.X - position.X;
        var dy = finish.Y - position.Y;
        var dz = finish.Z - position.Z;
        if (dx * dx + dy * dy + dz * dz > endTolerance * endTolerance) return false;

        return Enumerable.Range(0, sectors.Count).All(index => EstimateSectorTime(index) is > 0);
    }

    private RecentCompetitionState? GetRecentCompetition()
    {
        var recent = Volatile.Read(ref recentCompetition);
        return recent is not null && DateTimeOffset.UtcNow < recent.ExpiresAt ? recent : null;
    }

    private void ClearRecentCompetition() => Volatile.Write(ref recentCompetition, null);

    private void PublishCompetitionState(LapHudState state)
    {
        var matchRejectionEligible = !forceTrackLearning &&
                                     compatibleTracks.Count > 0 &&
                                     (automaticMatchRejected ||
                                      !trackMatchEverPlausible && !currentTrackGeometryPlausible);
        if (matchRejectionEligible &&
            !string.Equals(state.TrackName, AutomaticMatchRejectedTrackName, StringComparison.Ordinal))
        {
            state = state with
            {
                Phase = TrackLearningPhase.MatchingTrack,
                Status = AutomaticMatchRejectedStatus,
                Instruction = AutomaticMatchRejectedInstruction,
                MatchState = TrackMatchState.Unknown,
                MatchConfidence = 0,
                TrackName = AutomaticMatchRejectedTrackName
            };
        }

        state = state with
        {
            CompetitionSessionId = sessionId,
            CurrentLapSeconds = Math.Max(0, lastCompetitionFrame?.Raw.CurrentLap ?? state.CurrentLapSeconds),
            IsPointToPoint = automaticMatchLocked && track?.LayoutKind == TrackLayoutKind.PointToPoint,
            MatchRejectionEligible = matchRejectionEligible
        };
        Volatile.Write(ref currentCompetitionSnapshot, state);
        Volatile.Write(ref snapshot, state);
    }

    private void BeginCompetitionSession(TelemetryFrame frame)
    {
        var raw = frame.Raw;
        if (lastInferredCompetitionEndAt is DateTimeOffset inferredEndAt &&
            frame.ArrivalTime - inferredEndAt <= TimeSpan.FromSeconds(15) &&
            raw.CurrentRaceTime >= lastInferredCompetitionRaceTime - 0.5f)
        {
            RecordDiagnostic(
                "race.false-end-recovered",
                "比赛计时仍在延续，上一条结束判定可能不正确。",
                true,
                frame.ArrivalTime,
                new Dictionary<string, string>
                {
                    ["endedRaceTime"] = lastInferredCompetitionRaceTime.ToString("0.000"),
                    ["resumedRaceTime"] = raw.CurrentRaceTime.ToString("0.000"),
                    ["resumeDelaySeconds"] =
                        (frame.ArrivalTime - inferredEndAt).TotalSeconds.ToString("0.000")
                });
        }
        lastInferredCompetitionEndAt = null;
        ResetAutomaticMatchState();
        ClearRecentCompetition();
        competitionActive = true;
        competitionSignalSuspended = false;
        sessionId = Guid.NewGuid();
        previousLapNumber = raw.LapNumber;
        previousCurrentLap = raw.CurrentLap;
        previousCurrentRaceTime = raw.CurrentRaceTime;
        previousLastLap = raw.LastLap;
        lastLapValueAtLapStart = raw.LastLap;
        lastRewindLapTime = null;
        lastCompetitionFrame = null;
        lastCrossingAt = DateTimeOffset.MinValue;
        lapArmed = false;
        captureArmed = false;
        captureStartConfirmed = false;
        captureEligibleForPointToPointFinish = track is null &&
                                                raw.CurrentRaceTime <= 5 &&
                                                raw.CurrentLap <= 5;
        waitingForInitialStartLine = true;
        currentSamples.Clear();
        capture.Clear();
        projectionIndex = 0;
        confidentProjectionCount = 0;
        validProjectionSamples = 0;
        invalidProjectionSamples = 0;
        heldComparisons = null;
        ClearCumulativeHistoricalDeltaDisplay();
        currentLapInvalidated = false;
        observedSectorIndex = 0;
        trackMatchEverPlausible = false;
        currentTrackGeometryPlausible = track is null;
    }

    private void ResetAutomaticMatchState()
    {
        automaticMatchStarted = false;
        automaticMatchLocked = false;
        automaticMatchRejected = false;
        automaticMatchStartedMidLap = false;
        automaticMatchStartedAtConfirmedLine = false;
        automaticMatchTravelMeters = 0;
        automaticMatchCoarseEligibleCount = 0;
        automaticMatchPreviousPosition = null;
        automaticMatchStartedAt = default;
        automaticMatchFrames.Clear();
        automaticMatchCandidates.Clear();
        automaticMatchCoarseEliminated.Clear();
        Volatile.Write(ref trackMatchDiagnostics, new TrackMatchDiagnostics(
            DateTimeOffset.MinValue,
            "等待比赛或起点",
            compatibleTracks.Count,
            0,
            0,
            [],
            []));
        ResetSevereDeviationEvidence();
    }

    private bool ShouldBeginNewCompetitionSession(Fh6RawTelemetry raw)
    {
        var raceClockRestarted = previousCurrentRaceTime is > 15 &&
                                 raw.CurrentRaceTime + 5 < previousCurrentRaceTime.Value;
        var returnedToOpeningLap = raw.LapNumber <= 1 && raw.CurrentLap <= 5 && raw.CurrentRaceTime <= 15;
        var previousResultsCleared = raw.LastLap <= 0 && raw.BestLap <= 0;
        bool currentSessionHasSavedLap;
        lock (lapGate) currentSessionHasSavedLap = visibleLaps.Any(lap => lap.SessionId == sessionId);
        var hasCompletePendingLap = lastCompetitionFrame is { } pendingFrame &&
                                    IsPendingRouteCompleteAtFinish(
                                        pendingFrame.Raw.Position,
                                        pendingFrame.Raw.CurrentLap,
                                        false);

        // FH6 Data Out has no event/session identifier. Requiring all four signals avoids
        // treating a menu frame or an ordinary rewind as a new competition. A geometrically
        // complete pending lap is also session evidence: point-to-point events may reset
        // directly into a restart without ever publishing LastLap.
        return (currentSessionHasSavedLap || hasCompletePendingLap) &&
               raceClockRestarted &&
               returnedToOpeningLap &&
               previousResultsCleared;
    }

    private IReadOnlyList<SectorComparison> BuildCompletedComparisons(LapRecord lap)
    {
        var comparisons = new List<SectorComparison>(sectors.Count);
        for (var index = 0; index < sectors.Count; index++)
        {
            var segment = lap.Segments.FirstOrDefault(candidate => candidate.Index == index);
            var current = segment is { IsValid: true, TimeSeconds: > 0 } ? segment.TimeSeconds : (double?)null;
            var (sessionBest, allTimeBest) = BestVisibleSectorTimes(index, lap.Vehicle.CarClass);
            var pointToPoint = track?.LayoutKind == TrackLayoutKind.PointToPoint;
            var state = SectorColorClassifier.Classify(
                current,
                current is not null,
                sessionBest,
                allTimeBest,
                considerCurrentSessionBest: !pointToPoint);
            var reference = pointToPoint ? allTimeBest : sessionBest ?? allTimeBest;
            comparisons.Add(new SectorComparison(index, current, sessionBest, allTimeBest,
                current is not null && reference is not null ? current - reference : null,
                state, false));
        }

        return comparisons;
    }

    private (double? SessionBest, double? AllTimeBest) BestVisibleSectorTimes(int index, int performanceClass)
    {
        if (track is null) return (null, null);
        LapSummary[] snapshotLaps;
        lock (lapGate) snapshotLaps = visibleLaps.ToArray();
        var comparable = snapshotLaps
            .Where(lap => lap.IsValid && lap.TrackId == track.Id && lap.Direction == track.Direction &&
                          lap.SectorSchemaVersion == TrackAlgorithms.SectorSchemaVersion &&
                          lap.Vehicle.CarClass == performanceClass)
            .ToArray();
        double? Best(IEnumerable<LapSummary> laps) => laps
            .SelectMany(lap => lap.Segments)
            .Where(segment => segment.Index == index && segment.IsValid && segment.TimeSeconds > 0)
            .Select(segment => (double?)segment.TimeSeconds)
            .Min();
        return (Best(comparable.Where(lap => lap.SessionId == sessionId)), Best(comparable));
    }

    private LapSummary? HistoricalFastestLap(int performanceClass)
    {
        if (track is null) return null;
        lock (lapGate)
        {
            return visibleLaps
                .Where(lap => lap.IsValid &&
                              lap.TrackId == track.Id &&
                              lap.Direction == track.Direction &&
                              lap.SectorSchemaVersion == TrackAlgorithms.SectorSchemaVersion &&
                              lap.Vehicle.CarClass == performanceClass &&
                              lap.Segments.Count == sectors.Count &&
                              lap.Segments.All(segment => segment.IsValid && segment.TimeSeconds > 0))
                .OrderBy(lap => lap.TotalSeconds)
                .ThenBy(lap => lap.StartedAt)
                .ThenBy(lap => lap.Id)
                .FirstOrDefault();
        }
    }

    private double? CurrentCumulativeHistoricalDelta(int currentSector, int performanceClass)
    {
        if (currentSector <= 0 || currentSector >= sectorStartTimes.Length) return null;
        var currentCumulative = sectorStartTimes[currentSector] - sectorStartTimes[0];
        if (currentCumulative <= 0) return null;
        var historical = HistoricalFastestLap(performanceClass);
        if (historical is null) return null;

        var referenceCumulative = 0d;
        for (var index = 0; index < currentSector; index++)
        {
            var segment = historical.Segments.FirstOrDefault(candidate => candidate.Index == index);
            if (segment is not { IsValid: true, TimeSeconds: > 0 }) return null;
            referenceCumulative += segment.TimeSeconds;
        }

        return currentCumulative - referenceCumulative;
    }

    private static double? CompletedLapDelta(LapRecord completedLap, LapSummary? historicalReference) =>
        historicalReference is null ? null : completedLap.TotalSeconds - historicalReference.TotalSeconds;

    private void UpdateSectorProgress(int currentSector, double currentLapSeconds, bool rewound)
    {
        if (sectorStartTimes.Length == 0) return;
        currentSector = Math.Clamp(currentSector, 0, sectorStartTimes.Length - 1);
        if (rewound)
        {
            for (var index = currentSector + 1; index < sectorStartTimes.Length; index++) sectorStartTimes[index] = 0;
            for (var index = currentSector; index < completedSectorTimes.Length; index++) completedSectorTimes[index] = null;
            observedSectorIndex = currentSector;
            return;
        }

        if (currentSector <= observedSectorIndex) return;
        for (var index = observedSectorIndex + 1; index <= currentSector; index++)
        {
            completedSectorTimes[index - 1] = EstimateSectorTimeFromSamples(index - 1);
            sectorStartTimes[index] = currentLapSeconds;
        }
        observedSectorIndex = currentSector;
    }

    private void UpdateTrackMatchPlausibility(Vector3F position)
    {
        if (track is null || track.Points.Count == 0)
        {
            currentTrackGeometryPlausible = true;
            return;
        }

        // A confirmed route is invalidated only by sustained high-distance
        // evidence. A single wide corner, rewind frame, or packet gap must not
        // flash the no-match state.
        if (automaticMatchLocked)
        {
            currentTrackGeometryPlausible = true;
            trackMatchEverPlausible = true;
            return;
        }

        var maximumDistance = SevereDeviationDistanceMeters(track);
        var maximumDistanceSquared = maximumDistance * maximumDistance;
        var minimumDistanceSquared = double.MaxValue;
        foreach (var point in track.Points)
        {
            var dx = point.X - position.X;
            var dy = point.Y - position.Y;
            var dz = point.Z - position.Z;
            minimumDistanceSquared = Math.Min(minimumDistanceSquared, dx * dx + dy * dy + dz * dz);
            if (minimumDistanceSquared <= maximumDistanceSquared) break;
        }

        currentTrackGeometryPlausible = minimumDistanceSquared <= maximumDistanceSquared;
        if (currentTrackGeometryPlausible) trackMatchEverPlausible = true;
    }

    private TimeSpan CompletedLapHoldDuration() => TimeSpan.FromSeconds(
        Math.Clamp(getOverlayLayout().LapCompletedHoldSeconds, 0, 15));

    private void ReloadVisibleLaps()
    {
        var loaded = track is null ? [] : store.LoadLapSummaries(track.Id, LazyForzaStore.MaxLapsPerTrack);
        lock (lapGate)
        {
            visibleLaps.Clear();
            visibleLaps.AddRange(loaded);
            visibleLapDetails.Clear();
        }
    }

    private int VisibleLapCount
    {
        get { lock (lapGate) return visibleLaps.Count; }
    }

    private static IReadOnlyList<LapSample> DownsampleForStorage(IReadOnlyList<LapSample> samples)
    {
        if (samples.Count <= 2) return samples.ToArray();

        const double minimumIntervalSeconds = 0.1;
        var downsampled = new List<LapSample>(Math.Min(samples.Count, 800)) { samples[0] };
        var lastAddedIndex = 0;
        var nextElapsed = samples[0].ElapsedSeconds + minimumIntervalSeconds;
        for (var index = 1; index < samples.Count - 1; index++)
        {
            if (samples[index].ElapsedSeconds + 0.000001 < nextElapsed) continue;
            downsampled.Add(samples[index]);
            lastAddedIndex = index;
            nextElapsed = samples[index].ElapsedSeconds + minimumIntervalSeconds;
        }

        if (lastAddedIndex != samples.Count - 1) downsampled.Add(samples[^1]);
        return downsampled;
    }

    private void RegisterVisibleLap(LapRecord lap)
    {
        lock (lapGate)
        {
            visibleLaps.RemoveAll(candidate => candidate.Id == lap.Id);
            visibleLaps.Add(LapSummary.FromRecord(lap));
            visibleLapDetails[lap.Id] = lap;
            TrimVisibleLapDetails();
            visibleLaps.Sort((left, right) => left.StartedAt.CompareTo(right.StartedAt));
            if (visibleLaps.Count <= LazyForzaStore.MaxLapsPerTrack) return;

            var keep = new HashSet<Guid>();
            foreach (var historicalBest in visibleLaps
                         .Where(candidate => candidate.IsValid)
                         .GroupBy(candidate => candidate.Vehicle.CarClass)
                         .Select(group => group
                             .OrderBy(candidate => candidate.TotalSeconds)
                             .ThenBy(candidate => candidate.StartedAt)
                             .ThenBy(candidate => candidate.Id)
                             .First().Id))
            {
                keep.Add(historicalBest);
            }
            foreach (var candidate in visibleLaps.OrderByDescending(candidate => candidate.StartedAt))
            {
                if (keep.Count >= LazyForzaStore.MaxLapsPerTrack) break;
                keep.Add(candidate.Id);
            }
            visibleLaps.RemoveAll(candidate => !keep.Contains(candidate.Id));
            foreach (var lapId in visibleLapDetails.Keys.Where(id => !keep.Contains(id)).ToArray())
                visibleLapDetails.Remove(lapId);
        }
    }

    private void TrimVisibleLapDetails()
    {
        const int maximumCachedLaps = 8;
        if (visibleLapDetails.Count <= maximumCachedLaps) return;
        foreach (var lapId in visibleLapDetails.Values
                     .OrderByDescending(lap => lap.StartedAt)
                     .Skip(maximumCachedLaps)
                     .Select(lap => lap.Id)
                     .ToArray())
            visibleLapDetails.Remove(lapId);
    }

    private void QueuePersistence(PersistenceCommand command)
    {
        if (persistenceQueue?.Writer.TryWrite(command) == true) return;
        Persist(command);
    }

    private async Task PersistAsync(ChannelReader<PersistenceCommand> reader)
    {
        await foreach (var command in reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                Persist(command);
            }
            catch (Exception exception)
            {
                LogIfInitialized($"Lap persistence failed: {exception}");
                RecordDiagnostic(
                    "lap.persistence-failed",
                    $"圈速写入失败：{exception.Message}",
                    true,
                    DateTimeOffset.UtcNow);
            }
        }
    }

    private void Persist(PersistenceCommand command)
    {
        if (command.Lap is { } lap)
        {
            store.SaveLap(lap);
            LogIfInitialized(
                $"Persisted lap {lap.Id}: total={lap.TotalSeconds:0.000}, samples={lap.Samples.Count}.");
            return;
        }

        if (command.DeleteLapId is Guid lapId)
        {
            store.DeleteLap(lapId);
            LogIfInitialized($"Deleted lap {lapId}.");
            return;
        }

        if (command.DeleteTrackLapsId is Guid trackId)
        {
            store.DeleteTrackLaps(trackId, command.PerformanceClasses, command.PreserveLapIds);
            var scope = command.PerformanceClasses is { Length: > 0 }
                ? $"classes [{string.Join(',', command.PerformanceClasses)}]"
                : "all classes";
            LogIfInitialized(command.PreserveLapIds is { Length: > 0 }
                ? $"Deleted saved laps for track {trackId}, scope={scope}, preserving class bests [{string.Join(',', command.PreserveLapIds)}]."
                : $"Deleted saved laps for track {trackId}, scope={scope}, including class bests.");
        }
    }

    private bool IsCompatible(TrackTemplate candidate, IReadOnlyList<SectorDefinition> candidateSectors) =>
        (expectedTrackSource is null || candidate.Source == expectedTrackSource) &&
        candidateSectors.Count > 0 &&
        candidateSectors.All(sector =>
            sector.SectorSchemaVersion == TrackAlgorithms.SectorSchemaVersion &&
            sector.AlgorithmVersion == TrackAlgorithms.SectorAlgorithmVersion);

    private void RecoverFromRewind(TelemetryFrame frame)
    {
        if (track is null) return;
        heldComparisons = null;
        ClearCumulativeHistoricalDeltaDisplay();

        var stayedInLap = previousLapNumber == frame.Raw.LapNumber;
        if (stayedInLap)
        {
            currentSamples.RemoveAll(sample => sample.ElapsedSeconds > frame.Raw.CurrentLap + 0.05);
            validProjectionSamples = currentSamples.Count;
            invalidProjectionSamples = 0;
            lastRewindLapTime = frame.Raw.CurrentLap;
        }
        else
        {
            BeginLap(frame.Raw.CurrentLap, frame.Raw.LastLap, frame.ArrivalTime);
            lapArmed = true;
            currentLapInvalidated = true;
        }

        var projection = TrackAlgorithms.ProjectConstrained(
            track.Points,
            frame.Raw.Position.X,
            frame.Raw.Position.Y,
            frame.Raw.Position.Z,
            track.Points.Count / 2,
            track.Points.Count,
            track.Points.Count);
        if (projection.IsValid)
        {
            projectionIndex = projection.SegmentIndex;
            lastS = projection.S;
        }

        confidentProjectionCount = Math.Min(10, currentSamples.Count);
    }

    private void UpdatePrevious(Fh6RawTelemetry raw)
    {
        previousLapNumber = raw.LapNumber;
        previousCurrentLap = raw.CurrentLap;
        previousCurrentRaceTime = raw.CurrentRaceTime;
        previousLastLap = raw.LastLap;
    }

    private bool IsStartLineGeometryPlausible(Vector3F position)
    {
        if (track is null) return true;
        var start = track.Points[0];
        var dx = start.X - position.X;
        var dy = start.Y - position.Y;
        var dz = start.Z - position.Z;
        var maximumDistance = Math.Max(20, track.MatchingToleranceMeters * 2);
        return dx * dx + dy * dy + dz * dz <= maximumDistance * maximumDistance &&
               (waitingForInitialStartLine || lastS >= track.LengthMeters * 0.75);
    }

    private CompletedLapTimeResolution ResolveCompletedLapTime(Fh6RawTelemetry raw, double sampledTotal)
    {
        var tolerance = Math.Max(2, sampledTotal * 0.05);
        var lastLapChanged = Math.Abs(raw.LastLap - lastLapValueAtLapStart) > 0.0005f;
        if (raw.LastLap > 0 && lastLapChanged && Math.Abs(raw.LastLap - sampledTotal) <= tolerance)
            return new CompletedLapTimeResolution(raw.LastLap, "LastLap");

        // The final route point can fall just beyond the projection tolerance. In that
        // case the last stored sample is one telemetry frame early, while CurrentLap
        // still carries the official event timer at the finish.
        if (raw.CurrentLap >= 10 && Math.Abs(raw.CurrentLap - sampledTotal) <= tolerance)
            return new CompletedLapTimeResolution(raw.CurrentLap, "CurrentLap");

        if (previousCurrentLap is float previousLapTime && previousCurrentRaceTime is float previousRaceTime &&
            raw.CurrentLap < previousLapTime && raw.CurrentRaceTime >= previousRaceTime)
        {
            var interpolated = previousLapTime + raw.CurrentRaceTime - previousRaceTime;
            if (interpolated > 0 && Math.Abs(interpolated - sampledTotal) <= tolerance)
                return new CompletedLapTimeResolution(interpolated, "RaceTimeInterpolation");
        }

        return new CompletedLapTimeResolution(sampledTotal, "SampledFallback");
    }

    private readonly record struct CompletedLapTimeResolution(double Seconds, string Source);

    private void RecordDiagnostic(
        string code,
        string summary,
        bool isAnomaly,
        DateTimeOffset occurredAt,
        IReadOnlyDictionary<string, string>? data = null) =>
        diagnosticSink?.Invoke(new DiagnosticSignal(code, summary, isAnomaly, occurredAt, data));

    private double? EstimateSectorTime(int index)
    {
        if (index >= 0 && index < completedSectorTimes.Length && index < observedSectorIndex)
            return completedSectorTimes[index];
        return EstimateSectorTimeFromSamples(index);
    }

    private double? EstimateSectorTimeFromSamples(int index)
    {
        var samples = currentSamples.Where(sample => sample.S >= sectors[index].StartS && sample.S <= sectors[index].EndS).ToArray();
        if (samples.Length < 2) return null;
        var sectorLength = sectors[index].EndS - sectors[index].StartS;
        var edgeTolerance = Math.Min(25, Math.Max(5, sectorLength * 0.15));
        if (samples[0].S > sectors[index].StartS + edgeTolerance ||
            samples[^1].S < sectors[index].EndS - edgeTolerance)
        {
            return null;
        }

        var duration = samples[^1].ElapsedSeconds - samples[0].ElapsedSeconds;
        return duration > 0 ? duration : null;
    }

    private void ResetLiveCumulativeHistoricalDelta()
    {
        liveCumulativeHistoricalDeltaSeconds = null;
        liveCumulativeHistoricalDeltaUntil = DateTimeOffset.MinValue;
        liveCumulativeHistoricalDeltaSector = -1;
    }

    private void ClearCumulativeHistoricalDeltaDisplay()
    {
        heldCumulativeHistoricalDeltaSeconds = null;
        heldCumulativeHistoricalDeltaUntil = DateTimeOffset.MinValue;
        ResetLiveCumulativeHistoricalDelta();
    }

    private sealed class CompatibleTrack
    {
        private readonly TrackSpatialIndex startSpatialIndex;

        public CompatibleTrack(
            TrackTemplate track,
            IReadOnlyList<SectorDefinition> sectors)
        {
            Track = track;
            Sectors = sectors;
            MatchingRoute = track.LayoutKind == TrackLayoutKind.Circuit
                ? BuildWrappedCircuit(track)
                : track.Points;
            RouteSpatialIndex = new TrackSpatialIndex(track.Points);
            startSpatialIndex = new TrackSpatialIndex(
                track.Points,
                includedSegmentIndices: StartSegmentIndices(track));
        }

        public TrackTemplate Track { get; }
        public IReadOnlyList<SectorDefinition> Sectors { get; }
        public IReadOnlyList<TrackPoint> MatchingRoute { get; }
        public TrackSpatialIndex RouteSpatialIndex { get; }

        public bool IsStartEligible(Vector3F position, bool allowMidRouteStart)
        {
            var projection = InitialProjection(position, allowMidRouteStart);
            return projection.IsValid &&
                   projection.DistanceMeters <= AutomaticMatchCandidate.InitialGateMeters(Track);
        }

        public ProjectionResult InitialProjection(Vector3F position, bool allowMidRouteStart)
        {
            var radius = AutomaticMatchCandidate.InitialGateMeters(Track);
            var index = allowMidRouteStart ? RouteSpatialIndex : startSpatialIndex;
            return index.ProjectNearest(position.X, position.Y, position.Z, radius);
        }

        private static IEnumerable<int> StartSegmentIndices(TrackTemplate track)
        {
            if (track.Points.Count < 2) return [];
            var windowMeters = Math.Clamp(track.LengthMeters * 0.12, 200, 450);
            var indices = new HashSet<int>();
            for (var index = 0; index < track.Points.Count - 1; index++)
            {
                if (track.Points[index].S <= windowMeters) indices.Add(index);
            }
            if (track.LayoutKind == TrackLayoutKind.Circuit)
            {
                var suffixBoundary = Math.Max(0, track.LengthMeters - windowMeters);
                for (var index = 0; index < track.Points.Count - 1; index++)
                {
                    if (track.Points[index + 1].S >= suffixBoundary) indices.Add(index);
                }
            }
            return indices;
        }

        private static IReadOnlyList<TrackPoint> BuildWrappedCircuit(TrackTemplate track)
        {
            var points = new List<TrackPoint>(track.Points.Count * 2 - 1);
            points.AddRange(track.Points);
            points.AddRange(track.Points
                .Skip(1)
                .Select(point => point with { S = point.S + track.LengthMeters }));
            return points;
        }
    }

    private sealed class AutomaticMatchCandidate
    {
        private readonly IReadOnlyList<TrackPoint> matchingRoute;
        private readonly Vector3F initialPosition;
        private readonly RouteFeature expectedEarlyFeature;
        private readonly double initialS;
        private readonly double startTangentX;
        private readonly double startTangentZ;
        private double distanceTotal;
        private Vector3F lastObservedPosition;
        private Vector3F lastFeaturePosition;
        private double observedTravelMeters;
        private double observedSignedTurn;
        private double observedAbsoluteTurn;
        private double? previousObservedHeading;
        private double directionPenalty;
        private double curvaturePenalty;
        private bool directionEvaluated;
        private bool curvatureEvaluated;

        public AutomaticMatchCandidate(
            CompatibleTrack saved,
            bool allowMidRouteStart,
            Fh6RawTelemetry initialFrame)
        {
            Saved = saved;
            matchingRoute = saved.MatchingRoute;
            initialPosition = initialFrame.Position;
            lastObservedPosition = initialFrame.Position;
            lastFeaturePosition = initialFrame.Position;
            var initialProjection = saved.InitialProjection(initialFrame.Position, allowMidRouteStart);
            StartDistanceMeters = initialProjection.IsValid
                ? initialProjection.DistanceMeters
                : double.PositiveInfinity;
            StartEligible = initialProjection.IsValid &&
                            initialProjection.DistanceMeters <= InitialGateMeters(saved.Track);
            if (!StartEligible)
            {
                EliminationReason = initialProjection.IsValid
                    ? $"起点距离 {initialProjection.DistanceMeters:0.0} m 超出 {InitialGateMeters(saved.Track):0} m"
                    : "起点区域空间索引无相邻路线";
                return;
            }

            ProjectionIndex = initialProjection.SegmentIndex;
            initialS = initialProjection.S;
            var tangent = SegmentDirection(matchingRoute, ProjectionIndex);
            startTangentX = tangent.X;
            startTangentZ = tangent.Z;
            expectedEarlyFeature = CalculateRouteFeature(matchingRoute, ProjectionIndex, 120);
        }

        public CompatibleTrack Saved { get; }
        public bool StartEligible { get; }
        public bool IsEliminated => !StartEligible || EliminationReason is not null;
        public bool IsFineCandidate { get; set; }
        public string? EliminationReason { get; private set; }
        public double StartDistanceMeters { get; }
        public int Observations { get; private set; }
        public int ValidObservations { get; private set; }
        public int ProjectionIndex { get; private set; }
        public double ProgressMeters { get; private set; }
        public double RequiredProgressMeters => Math.Clamp(
            Saved.Track.LengthMeters * 0.12,
            AutomaticMatchMinimumProgressMeters,
            AutomaticMatchMaximumProgressMeters);
        public double MeanDistanceMeters => ValidObservations == 0
            ? double.PositiveInfinity
            : distanceTotal / ValidObservations;
        public double ValidRatio => ValidObservations / (double)Math.Max(1, Observations);
        public double Quality => MeanDistanceMeters + ((1 - ValidRatio) * 80);
        public double CoarseQuality => StartDistanceMeters + directionPenalty + curvaturePenalty;
        public double EffectiveToleranceMeters => FineToleranceMeters(Saved.Track);

        public void ObserveCoarse(Vector3F position)
        {
            if (IsEliminated) return;
            UpdateCoarseGeometry(position);
        }

        public void ObserveProjection(Fh6RawTelemetry raw)
        {
            if (IsEliminated || !IsFineCandidate) return;
            Observations++;
            var projection = TrackAlgorithms.ProjectConstrained(
                matchingRoute,
                raw.Position.X,
                raw.Position.Y,
                raw.Position.Z,
                ProjectionIndex,
                searchBehind: 8,
                searchAhead: 96);
            var valid = projection.IsValid &&
                        projection.DistanceMeters <= FineToleranceMeters(Saved.Track) &&
                        projection.ElevationErrorMeters <= ElevationToleranceMeters(Saved.Track);
            if (!valid)
            {
                if (Observations >= 16 && ValidRatio < 0.25)
                    EliminationReason = $"连续三维投影不匹配（有效率 {ValidRatio:P0}）";
                return;
            }

            ProjectionIndex = projection.SegmentIndex;
            ProgressMeters = Math.Max(ProgressMeters, projection.S - initialS);
            ValidObservations++;
            distanceTotal += projection.DistanceMeters;

            var lengthLimit = Math.Min(
                AutomaticMatchMaximumTravelMeters,
                Math.Max(300, Saved.Track.LengthMeters * 1.15));
            if (observedTravelMeters >= lengthLimit &&
                ProgressMeters < Math.Min(RequiredProgressMeters, observedTravelMeters * 0.35))
            {
                EliminationReason =
                    $"长度/进度范围不兼容（行驶 {observedTravelMeters:0} m，路线进度 {ProgressMeters:0} m）";
            }
        }

        public double Confidence(AutomaticMatchCandidate? runnerUp)
        {
            var tolerance = Math.Max(1, FineToleranceMeters(Saved.Track));
            var distanceConfidence = Math.Clamp(1 - (MeanDistanceMeters / tolerance), 0, 1);
            var progressConfidence = Math.Clamp(ProgressMeters / RequiredProgressMeters, 0, 1);
            var separationConfidence = runnerUp is null
                ? 1
                : Math.Clamp((runnerUp.Quality - Quality) / 12, 0, 1);
            return Math.Clamp(
                (ValidRatio * 0.45) +
                (distanceConfidence * 0.25) +
                (progressConfidence * 0.15) +
                (separationConfidence * 0.15),
                0,
                1);
        }

        public bool SharesStartWith(AutomaticMatchCandidate other)
        {
            var first = matchingRoute[Math.Clamp(ProjectionIndex, 0, matchingRoute.Count - 1)];
            var second = other.matchingRoute[Math.Clamp(other.ProjectionIndex, 0, other.matchingRoute.Count - 1)];
            var dx = first.X - second.X;
            var dy = first.Y - second.Y;
            var dz = first.Z - second.Z;
            var startDistance = Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
            var directionDot = (startTangentX * other.startTangentX) +
                               (startTangentZ * other.startTangentZ);
            return startDistance <= 60 && directionDot >= 0.75;
        }

        public TrackMatchCandidateDiagnostic ToDiagnostic(
            string stage,
            string? eliminationReason = null) =>
            new(
                Saved.Track.Name,
                Saved.Track.LayoutKind,
                Saved.Track.Category,
                Saved.Track.LengthMeters,
                stage,
                double.IsFinite(StartDistanceMeters) ? StartDistanceMeters : null,
                double.IsFinite(MeanDistanceMeters) ? MeanDistanceMeters : null,
                ProgressMeters,
                ValidRatio,
                eliminationReason ?? EliminationReason);

        public static double InitialGateMeters(TrackTemplate track) =>
            Math.Max(80, track.MatchingToleranceMeters * 4);

        private void UpdateCoarseGeometry(Vector3F position)
        {
            var dx = position.X - lastObservedPosition.X;
            var dy = position.Y - lastObservedPosition.Y;
            var dz = position.Z - lastObservedPosition.Z;
            var movement = Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
            if (movement is > 0.05 and < 100)
                observedTravelMeters += movement;
            lastObservedPosition = position;

            var fromStartX = position.X - initialPosition.X;
            var fromStartZ = position.Z - initialPosition.Z;
            var fromStartMagnitude = Math.Sqrt((fromStartX * fromStartX) + (fromStartZ * fromStartZ));
            if (!directionEvaluated && observedTravelMeters >= 20 && fromStartMagnitude >= 10)
            {
                directionEvaluated = true;
                var directionDot = ((fromStartX / fromStartMagnitude) * startTangentX) +
                                   ((fromStartZ / fromStartMagnitude) * startTangentZ);
                directionPenalty = (1 - Math.Clamp(directionDot, -1, 1)) * 20;
                if (directionDot < -0.2)
                {
                    EliminationReason = $"行驶方向相反（方向相似度 {directionDot:0.00}）";
                    return;
                }
            }

            var featureDx = position.X - lastFeaturePosition.X;
            var featureDz = position.Z - lastFeaturePosition.Z;
            var featureDistance = Math.Sqrt((featureDx * featureDx) + (featureDz * featureDz));
            if (featureDistance >= 4)
            {
                var heading = Math.Atan2(featureDz, featureDx);
                if (previousObservedHeading is double previousHeading)
                {
                    var turn = NormalizeAngle(heading - previousHeading);
                    observedSignedTurn += turn;
                    observedAbsoluteTurn += Math.Abs(turn);
                }
                previousObservedHeading = heading;
                lastFeaturePosition = position;
            }

            if (!curvatureEvaluated && observedTravelMeters >= 120)
            {
                curvatureEvaluated = true;
                var curvatureTolerance = Saved.Track.Category switch
                {
                    "越野" => 3.0,
                    "泥地" => 2.6,
                    _ => 2.2
                };
                var signedError = Math.Abs(observedSignedTurn - expectedEarlyFeature.SignedTurn);
                var absoluteError = Math.Abs(observedAbsoluteTurn - expectedEarlyFeature.AbsoluteTurn);
                curvaturePenalty = (signedError + absoluteError) * 5;
                if (signedError > curvatureTolerance ||
                    absoluteError > curvatureTolerance * 1.35)
                {
                    EliminationReason =
                        $"早期曲率不匹配（方向变化差 {signedError:0.00} rad，曲率差 {absoluteError:0.00} rad）";
                    return;
                }
            }

            if (Saved.Track.LayoutKind == TrackLayoutKind.PointToPoint &&
                observedTravelMeters >= 300)
            {
                var startDx = position.X - initialPosition.X;
                var startDy = position.Y - initialPosition.Y;
                var startDz = position.Z - initialPosition.Z;
                var distanceToStart = Math.Sqrt(
                    (startDx * startDx) + (startDy * startDy) + (startDz * startDz));
                if (distanceToStart <= 35)
                    EliminationReason = "观测到闭环返回起点，路线类型与定点赛不符";
            }
        }

        private static RouteFeature CalculateRouteFeature(
            IReadOnlyList<TrackPoint> route,
            int startSegment,
            double distanceLimit)
        {
            var accumulated = 0d;
            var signedTurn = 0d;
            var absoluteTurn = 0d;
            double? previousHeading = null;
            for (var index = Math.Clamp(startSegment, 0, route.Count - 2);
                 index < route.Count - 1 && accumulated < distanceLimit;
                 index++)
            {
                var start = route[index];
                var end = route[index + 1];
                var dx = end.X - start.X;
                var dz = end.Z - start.Z;
                var length = Math.Sqrt((dx * dx) + (dz * dz));
                if (length <= 0.01) continue;
                var heading = Math.Atan2(dz, dx);
                if (previousHeading is double previous)
                {
                    var turn = NormalizeAngle(heading - previous);
                    signedTurn += turn;
                    absoluteTurn += Math.Abs(turn);
                }
                previousHeading = heading;
                accumulated += length;
            }
            return new RouteFeature(signedTurn, absoluteTurn);
        }

        private static (double X, double Z) SegmentDirection(
            IReadOnlyList<TrackPoint> route,
            int segmentIndex)
        {
            var index = Math.Clamp(segmentIndex, 0, route.Count - 2);
            for (var offset = 0; offset < 8 && index + offset < route.Count - 1; offset++)
            {
                var start = route[index + offset];
                var end = route[index + offset + 1];
                var dx = end.X - start.X;
                var dz = end.Z - start.Z;
                var magnitude = Math.Sqrt((dx * dx) + (dz * dz));
                if (magnitude > 0.01) return (dx / magnitude, dz / magnitude);
            }
            return (0, 0);
        }

        private static double FineToleranceMeters(TrackTemplate track) =>
            Math.Max(
                track.MatchingToleranceMeters,
                track.Category switch
                {
                    "越野" => 55,
                    "泥地" => 35,
                    "山道" => 28,
                    _ => 22
                });

        private static double ElevationToleranceMeters(TrackTemplate track) =>
            track.Category switch
            {
                "越野" => 18,
                "泥地" => 14,
                _ => 10
            };

        private static double NormalizeAngle(double angle)
        {
            while (angle > Math.PI) angle -= Math.PI * 2;
            while (angle < -Math.PI) angle += Math.PI * 2;
            return angle;
        }

        private readonly record struct RouteFeature(double SignedTurn, double AbsoluteTurn);
    }

    private readonly record struct PersistenceCommand(
        LapRecord? Lap,
        Guid? DeleteLapId,
        Guid? DeleteTrackLapsId,
        int[]? PerformanceClasses,
        Guid[]? PreserveLapIds)
    {
        public static PersistenceCommand Save(LapRecord lap) => new(lap, null, null, null, null);
        public static PersistenceCommand Delete(Guid lapId) => new(null, lapId, null, null, null);
        public static PersistenceCommand DeleteTrack(
            Guid trackId,
            int[]? performanceClasses,
            Guid[] preserveLapIds) =>
            new(null, null, trackId, performanceClasses, preserveLapIds);
    }

    private sealed record RecentCompetitionState(LapHudState Snapshot, DateTimeOffset ExpiresAt);

}
