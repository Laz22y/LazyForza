using LazyForza.Analysis;
using LazyForza.Domain;
using LazyForza.Modules.Abstractions;
using LazyForza.Storage;

namespace LazyForza.Modules.LapAnalysis;

public enum EstateCircuitPhase
{
    Idle,
    CapturingFirstTrace,
    CapturingSecondTrace,
    AwaitingDirection,
    CapturingDirection,
    AwaitingReferenceLap,
    WaitingForReferenceStart,
    CapturingReferenceLap,
    ValidatingLap,
    ValidationFailed,
    Ready,
    WaitingForTimingStart,
    TimingLap,
    Faulted
}

public sealed record EstateCircuitState(
    DateTimeOffset UpdatedAt,
    EstateCircuitPhase Phase,
    string Status,
    string Instruction,
    string? MapName,
    Guid? TrackId,
    int FirstTraceSamples,
    int SecondTraceSamples,
    double? GateWidthMeters,
    double? FitRmsMeters,
    double? TraceOffsetMeters,
    double? TraceAngleDegrees,
    double CurrentLapSeconds,
    double? LastLapSeconds,
    int CompletedLaps,
    int PassedCheckpoints,
    int TotalCheckpoints,
    double ProjectionRatio,
    bool IsTimingActive,
    bool IsEnrollmentActive);

public sealed record EstateEnrollmentRequest(
    string MapName,
    string? Creator,
    string? ShareCode,
    string MapRevision,
    int SectorCount = 4);

/// <summary>
/// Explicit, user-selected estate circuit timing. It never consumes FH6 race
/// clocks or lap counters and therefore cannot be activated by free-roam data.
/// </summary>
public sealed class EstateCircuitModule : LazyForzaModuleBase, IHudContribution
{
    public const string ModuleId = "estate-circuit";
    internal const int HudRefreshIntervalMilliseconds = 100;
    internal const double MaximumTelemetryGapSeconds = 2;
    internal const int MaximumTimestampReorderMilliseconds = 250;
    internal const double MaximumReverseProgressMeters = 15;
    private readonly LazyForzaStore store;
    private readonly string trackSource;
    private readonly Func<OverlayLayout> getOverlayLayout;
    private readonly object stateGate = new();
    private readonly EstateTimestampUnwrapper timestampUnwrapper = new();
    private readonly List<EstateGatePoint> firstTrace = [];
    private readonly List<EstateGatePoint> secondTrace = [];
    private readonly List<EstateGatePoint> directionTrace = [];
    private readonly List<TrackPoint> routeCapture = [];
    private readonly List<LapSample> lapSamples = [];
    private readonly List<LapSummary> timingHistory = [];
    private ITelemetrySubscription? subscription;
    private CancellationTokenSource? runCancellation;
    private Task? runTask;
    private EstateCircuitState state = EmptyState();
    private LapHudState? hudSnapshot;
    private EstateEnrollmentRequest? enrollment;
    private EstateTimingGate? fittedGate;
    private TrackTemplate? activeTrack;
    private EstateTrackDefinition? activeDefinition;
    private IReadOnlyList<SectorDefinition> activeSectors = [];
    private EstateTimedPosition? previousPosition;
    private long lastCrossingTimestamp = long.MinValue;
    private long lapStartTimestamp;
    private DateTimeOffset lapStartedAt;
    private int projectionIndex;
    private int validProjectionSamples;
    private int invalidProjectionSamples;
    private int nextCheckpoint;
    private double maximumProgress;
    private TelemetryFrame? lastFrame;
    private long lastUnwrappedTimestamp;
    private long lastHudUpdateTimestamp = long.MinValue;
    private double referenceLapSeconds;
    private int completedLaps;
    private double? lastLapSeconds;
    private Guid timingSessionId;
    private DateTimeOffset? lastTelemetryArrival;
    private DateTimeOffset? nonAdvancingTimestampStartedAt;
    private long minimumAcceptedFrameSequence = long.MinValue;
    private DateTimeOffset minimumAcceptedFrameArrival = DateTimeOffset.MinValue;
    private double? previousProjectedProgress;
    private double accumulatedReverseProgress;
    private int? comparisonPerformanceClass;
    private LapRecord? historicalReferenceLap;
    private IReadOnlyList<SectorComparison>? heldComparisons;
    private DateTimeOffset heldComparisonsUntil;
    private double? heldCumulativeHistoricalDeltaSeconds;
    private DateTimeOffset heldCumulativeHistoricalDeltaUntil;
    private double? liveCumulativeHistoricalDeltaSeconds;
    private DateTimeOffset liveCumulativeHistoricalDeltaUntil;
    private int liveCumulativeHistoricalDeltaSector = -1;

    public EstateCircuitModule(
        LazyForzaStore store,
        TelemetrySourceKind sourceKind,
        Func<OverlayLayout>? getOverlayLayout = null)
        : base(new ModuleDescriptor(
            ModuleId,
            "地产环道",
            "手动录入地产环道，以几何终点门和本地时钟记录圈速。",
            [],
            "tracks",
            null,
            true))
    {
        this.store = store;
        trackSource = TelemetryDataPartition.TrackSource(sourceKind);
        this.getOverlayLayout = getOverlayLayout ?? (() => new OverlayLayout());
    }

    public string Id => "hud.estate-lap";
    public HudContributionKind Kind => HudContributionKind.LapSectors;
    public int ZIndex => 21;
    object? IHudContribution.Snapshot => Volatile.Read(ref hudSnapshot);
    public EstateCircuitState State => Volatile.Read(ref state);
    public TrackTemplate? ActiveTrack
    {
        get { lock (stateGate) return activeTrack; }
    }

    public EstateTrackDefinition? ActiveDefinition
    {
        get { lock (stateGate) return activeDefinition; }
    }

    protected override async ValueTask OnStartAsync(CancellationToken cancellationToken)
    {
        runCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        subscription = await Context.Telemetry.SubscribeAsync(ModuleId, runCancellation.Token).ConfigureAwait(false);
        await Context.Hud.AttachAsync(this, cancellationToken).ConfigureAwait(false);
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
        await Context.Hud.DetachAsync(Id, cancellationToken).ConfigureAwait(false);
        lock (stateGate) ResetAll("地产环道模块已停止。");
        subscription = null;
        runTask = null;
        runCancellation?.Dispose();
        runCancellation = null;
    }

    public void BeginEnrollment(EstateEnrollmentRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.MapName))
            throw new ArgumentException("请填写地产地图名称。", nameof(request));
        if (request.SectorCount is < TrackAlgorithms.MinimumSectorCount or > TrackAlgorithms.MaximumSectorCount)
            throw new ArgumentOutOfRangeException(
                nameof(request),
                $"分段数必须在 {TrackAlgorithms.MinimumSectorCount} 到 {TrackAlgorithms.MaximumSectorCount} 之间。");
        lock (stateGate)
        {
            ResetWorkingState();
            enrollment = request with
            {
                MapName = request.MapName.Trim(),
                Creator = NullIfWhiteSpace(request.Creator),
                ShareCode = NullIfWhiteSpace(request.ShareCode),
                MapRevision = string.IsNullOrWhiteSpace(request.MapRevision) ? "1" : request.MapRevision.Trim()
            };
            SetState(EstateCircuitPhase.Idle, "地产环道录入已准备。", "开始第一次终点线描摹。", enrollmentActive: true);
        }
    }

    public void StartLineTrace()
    {
        lock (stateGate)
        {
            EnsureEnrollment();
            if (state.Phase is not (EstateCircuitPhase.Idle or EstateCircuitPhase.AwaitingDirection))
                throw new InvalidOperationException("当前步骤不能开始终点线描摹。");
            if (firstTrace.Count > 0 && secondTrace.Count > 0)
            {
                firstTrace.Clear();
                secondTrace.Clear();
                fittedGate = null;
            }
            var phase = firstTrace.Count == 0
                ? EstateCircuitPhase.CapturingFirstTrace
                : EstateCircuitPhase.CapturingSecondTrace;
            ArmAfterLatestTelemetryFrame();
            SetState(phase,
                phase == EstateCircuitPhase.CapturingFirstTrace ? "正在记录第一次描摹。" : "正在记录反向描摹。",
                "以约 2–12 km/h 沿棋盘格横线行驶，覆盖完整赛道宽度。",
                enrollmentActive: true);
        }
    }

    public EstateLineFitResult StopLineTrace()
    {
        lock (stateGate)
        {
            EnsureEnrollment();
            if (state.Phase == EstateCircuitPhase.CapturingFirstTrace)
            {
                if (firstTrace.Count < 10)
                    throw new InvalidOperationException("第一次描摹有效样本不足，请重新采集。");
                SetState(EstateCircuitPhase.Idle, "第一次描摹已完成。", "掉头后开始第二次反向描摹。", enrollmentActive: true);
                return new EstateLineFitResult(false, null, firstTrace.Count, double.NaN, double.NaN, double.NaN,
                    "第一次描摹已保存，等待第二次描摹。");
            }
            if (state.Phase != EstateCircuitPhase.CapturingSecondTrace)
                throw new InvalidOperationException("当前没有正在进行的终点线描摹。");

            var result = EstateTrackAlgorithms.FitStartFinishGate(firstTrace, secondTrace);
            fittedGate = result.IsAccepted ? result.Gate : null;
            SetState(
                result.IsAccepted ? EstateCircuitPhase.AwaitingDirection : EstateCircuitPhase.Idle,
                result.Explanation,
                result.IsAccepted ? "开始比赛方向采样，然后按正常比赛方向直穿终点线。" : "请重新进行两次终点线描摹。",
                enrollmentActive: true);
            if (!result.IsAccepted)
            {
                firstTrace.Clear();
                secondTrace.Clear();
            }
            return result;
        }
    }

    public void StartDirectionCapture()
    {
        lock (stateGate)
        {
            EnsureEnrollment();
            if (state.Phase != EstateCircuitPhase.AwaitingDirection || fittedGate is null)
                throw new InvalidOperationException("请先完成两次合格的终点线描摹。");
            directionTrace.Clear();
            ArmAfterLatestTelemetryFrame();
            SetState(EstateCircuitPhase.CapturingDirection, "正在记录比赛方向。",
                "从终点线前方驶向后方，直穿终点线至少 10 米。", enrollmentActive: true);
        }
    }

    public void StopDirectionCapture()
    {
        lock (stateGate)
        {
            EnsureEnrollment();
            if (state.Phase != EstateCircuitPhase.CapturingDirection || fittedGate is null)
                throw new InvalidOperationException("当前没有正在进行的比赛方向采样。");
            if (directionTrace.Count < 6)
                throw new InvalidOperationException("比赛方向样本不足，请直穿终点线后再停止采样。");
            if (!EstateTrackAlgorithms.TryApplyForwardDirection(
                    fittedGate,
                    directionTrace,
                    out var directed,
                    out var explanation))
                throw new InvalidOperationException(explanation);
            fittedGate = directed;
            SetState(EstateCircuitPhase.AwaitingReferenceLap, "终点门和比赛方向已确认。",
                "开始参考圈录入；首次正向过线开始，下一次正向过线结束。", enrollmentActive: true);
        }
    }

    public void StartReferenceLapCapture()
    {
        lock (stateGate)
        {
            EnsureEnrollment();
            if (state.Phase is not (EstateCircuitPhase.AwaitingReferenceLap or EstateCircuitPhase.ValidationFailed) ||
                fittedGate is null)
                throw new InvalidOperationException("当前步骤不能开始参考圈录入。");
            ResetLapTracking();
            routeCapture.Clear();
            activeTrack = null;
            activeDefinition = null;
            activeSectors = [];
            ArmAfterLatestTelemetryFrame();
            lastCrossingTimestamp = long.MinValue;
            SetState(EstateCircuitPhase.WaitingForReferenceStart, "等待首次正向通过终点线。",
                "过线后完整行驶一圈；不要倒带、传送或离开地产。", enrollmentActive: true);
        }
    }

    public void RetryValidationLap()
    {
        lock (stateGate)
        {
            if (state.Phase != EstateCircuitPhase.ValidationFailed || activeTrack is null || activeDefinition is null)
                throw new InvalidOperationException("当前没有可重试的验证圈。");
            ResetLapTracking();
            ArmAfterLatestTelemetryFrame();
            lastCrossingTimestamp = long.MinValue;
            SetState(EstateCircuitPhase.WaitingForReferenceStart, "等待验证圈起点。",
                "正向过线后完成一圈，必须依次通过所有检查点。", enrollmentActive: true);
        }
    }

    public void CancelEnrollment()
    {
        lock (stateGate) ResetAll("已取消地产环道录入。");
    }

    public void StartTiming(Guid trackId)
    {
        lock (stateGate)
        {
            var loaded = store.LoadTrack(trackId) ?? throw new InvalidOperationException("没有找到所选赛道。");
            var storedDefinition = store.LoadEstateTrackDefinition(trackId) ??
                             throw new InvalidOperationException("所选赛道缺少地产计时定义。");
            if (loaded.Track.TimingKind != TrackTimingKind.EstateGeometry || loaded.Track.LayoutKind != TrackLayoutKind.Circuit)
                throw new InvalidOperationException("只能手动启用地产环道计时。");
            if (!string.Equals(loaded.Track.Source, trackSource, StringComparison.Ordinal))
                throw new InvalidOperationException("赛道与当前遥测来源不兼容。");
            ResetWorkingState();
            activeTrack = loaded.Track;
            activeDefinition = storedDefinition with
            {
                StartFinishGate = storedDefinition.StartFinishGate with
                {
                    EndpointMarginMeters = Math.Max(
                        storedDefinition.StartFinishGate.EndpointMarginMeters,
                        EstateTrackAlgorithms.MinimumFinishEndpointMarginMeters)
                }
            };
            activeSectors = loaded.Sectors;
            timingSessionId = Guid.NewGuid();
            ReloadTimingHistory();
            completedLaps = 0;
            lastLapSeconds = null;
            previousPosition = null;
            timestampUnwrapper.Reset();
            ArmAfterLatestTelemetryFrame();
            SetState(EstateCircuitPhase.WaitingForTimingStart, "地产环道计时已启用。",
                "首次正向过线开始计时；暂停、倒带、传送或明显回转会取消当前圈，但计时模式仍保持启用。", timingActive: true);
        }
    }

    public void StopTiming()
    {
        lock (stateGate) ResetAll("地产环道计时已停止。");
    }

    private async Task ConsumeAsync(System.Threading.Channels.ChannelReader<TelemetryFrame> frames, CancellationToken cancellationToken)
    {
        await foreach (var frame in frames.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            lock (stateGate)
            {
                try { Observe(frame); }
                catch (Exception exception)
                {
                    SetState(EstateCircuitPhase.Faulted, $"地产环道处理失败：{exception.Message}",
                        "停止当前流程后重新开始。", state.IsTimingActive, state.IsEnrollmentActive);
                    LogIfInitialized($"Estate circuit processing failed: {exception}");
                }
            }
        }
    }

    private void Observe(TelemetryFrame frame)
    {
        // A manual capture action records the latest published sequence and
        // arrival time. Older frames may still be buffered for this subscriber
        // and must not be reclassified as samples collected after the click.
        if (frame.Sequence <= minimumAcceptedFrameSequence &&
            frame.ArrivalTime <= minimumAcceptedFrameArrival)
            return;

        if (lastTelemetryArrival is DateTimeOffset previousArrival &&
            (frame.ArrivalTime - previousArrival).TotalSeconds > MaximumTelemetryGapSeconds &&
            IsLapInProgress())
        {
            AbandonCurrentLap("检测到游戏暂停或遥测中断");
        }
        lastTelemetryArrival = frame.ArrivalTime;

        var rawTimestampCanBeWrap = frame.Raw.TimestampMS == 0 &&
                                    lastUnwrappedTimestamp > 0 &&
                                    (uint)(lastUnwrappedTimestamp & uint.MaxValue) > uint.MaxValue - 10_000;
        if ((frame.Raw.TimestampMS == 0 && !rawTimestampCanBeWrap) ||
            !float.IsFinite(frame.Raw.Position.X) ||
            !float.IsFinite(frame.Raw.Position.Y) ||
            !float.IsFinite(frame.Raw.Position.Z))
        {
            if (IsLapInProgress()) AbandonCurrentLap("检测到游戏暂停或菜单画面");
            previousPosition = null;
            return;
        }
        lastFrame = frame;
        var timestamp = timestampUnwrapper.Unwrap(frame.Raw.TimestampMS);
        if (lastUnwrappedTimestamp > 0 && timestamp <= lastUnwrappedTimestamp)
        {
            var regressionMilliseconds = lastUnwrappedTimestamp - timestamp;
            nonAdvancingTimestampStartedAt ??= frame.ArrivalTime;
            var timestampStalled = frame.ArrivalTime - nonAdvancingTimestampStartedAt.Value >=
                                   TimeSpan.FromSeconds(MaximumTelemetryGapSeconds);
            if (IsLapInProgress() &&
                (regressionMilliseconds > MaximumTimestampReorderMilliseconds || timestampStalled))
            {
                AbandonCurrentLap(
                    regressionMilliseconds > MaximumTimestampReorderMilliseconds
                        ? "检测到时间回退"
                        : "检测到游戏暂停或遥测时间停滞");
            }
            if (regressionMilliseconds > MaximumTimestampReorderMilliseconds)
            {
                previousPosition = null;
                timestampUnwrapper.Reset();
                lastUnwrappedTimestamp = 0;
                lastHudUpdateTimestamp = long.MinValue;
                nonAdvancingTimestampStartedAt = null;
            }
            else if (timestampStalled)
            {
                previousPosition = null;
            }
            return;
        }
        nonAdvancingTimestampStartedAt = null;
        lastUnwrappedTimestamp = timestamp;
        var position = new EstateTimedPosition(
            frame.Raw.Position.X,
            frame.Raw.Position.Y,
            frame.Raw.Position.Z,
            frame.Raw.Speed,
            timestamp);
        if (previousPosition is EstateTimedPosition previous &&
            IsLapInProgress() &&
            IsImplausiblePositionJump(previous, position))
        {
            AbandonCurrentLap("检测到位置跳变或倒带");
        }

        switch (state.Phase)
        {
            case EstateCircuitPhase.CapturingFirstTrace:
                CaptureTraceSample(firstTrace, position);
                RefreshTraceState();
                break;
            case EstateCircuitPhase.CapturingSecondTrace:
                CaptureTraceSample(secondTrace, position);
                RefreshTraceState();
                break;
            case EstateCircuitPhase.CapturingDirection:
                CaptureDirectionSample(position);
                break;
            case EstateCircuitPhase.WaitingForReferenceStart:
            case EstateCircuitPhase.CapturingReferenceLap:
            case EstateCircuitPhase.ValidatingLap:
            case EstateCircuitPhase.WaitingForTimingStart:
            case EstateCircuitPhase.TimingLap:
                ObserveLapFlow(frame, position);
                break;
        }
        previousPosition = position;
    }

    private void ObserveLapFlow(TelemetryFrame frame, EstateTimedPosition position)
    {
        var gate = activeDefinition?.StartFinishGate ?? fittedGate;
        if (gate is null) return;
        EstateGateCrossing crossing = default;
        var crossed = previousPosition is EstateTimedPosition previous &&
                      EstateTrackAlgorithms.TryDetectForwardCrossing(gate, previous, position, out crossing) &&
                      (lastCrossingTimestamp == long.MinValue ||
                       crossing.TimestampMilliseconds - lastCrossingTimestamp >= 2_000);
        if (crossed)
            LogIfInitialized(
                $"Estate finish crossing: phase={state.Phase}, timestamp={crossing.TimestampMilliseconds}, " +
                $"along={crossing.AlongGateMeters:0.00}/{EstateTrackAlgorithms.GateWidth(gate):0.00}m.");

        if (state.Phase == EstateCircuitPhase.WaitingForReferenceStart && crossed)
        {
            lastCrossingTimestamp = crossing.TimestampMilliseconds;
            BeginLap(crossing.TimestampMilliseconds, frame.ArrivalTime);
            if (activeTrack is null)
            {
                routeCapture.Clear();
                routeCapture.Add(new TrackPoint(crossing.X, crossing.Y, crossing.Z, 0, 0, 0));
                SetState(EstateCircuitPhase.CapturingReferenceLap, "正在录入参考圈。",
                    "完整行驶一圈并再次正向通过终点线。", enrollmentActive: true);
            }
            else
            {
                SetState(EstateCircuitPhase.ValidatingLap, "正在验证参考路线。",
                    "必须依次通过全部检查点并保持在路线范围内。", enrollmentActive: true);
            }
            return;
        }

        if (state.Phase == EstateCircuitPhase.WaitingForTimingStart && crossed)
        {
            lastCrossingTimestamp = crossing.TimestampMilliseconds;
            BeginLap(crossing.TimestampMilliseconds, frame.ArrivalTime);
            SetState(EstateCircuitPhase.TimingLap, "地产圈速计时中。", "依次通过检查点后返回终点线。", timingActive: true);
            return;
        }

        if (state.Phase == EstateCircuitPhase.CapturingReferenceLap)
        {
            AddRoutePoint(position);
            var elapsed = (position.TimestampMilliseconds - lapStartTimestamp) / 1000d;
            UpdateCurrentLapState(elapsed);
            if (crossed && elapsed >= 15 && routeCapture.Count >= 40)
            {
                lastCrossingTimestamp = crossing.TimestampMilliseconds;
                FinishReferenceLap(frame, crossing);
            }
            return;
        }

        if (state.Phase is EstateCircuitPhase.ValidatingLap or EstateCircuitPhase.TimingLap)
        {
            if (!ObserveProjectedLap(frame, position, crossed)) return;
            var elapsed = (position.TimestampMilliseconds - lapStartTimestamp) / 1000d;
            UpdateCurrentLapState(elapsed);
            if (crossed && elapsed >= 15)
            {
                lastCrossingTimestamp = crossing.TimestampMilliseconds;
                if (state.Phase == EstateCircuitPhase.ValidatingLap)
                    FinishValidationLap(frame, crossing);
                else
                    FinishTimedLap(frame, crossing);
            }
        }
    }

    private void FinishReferenceLap(TelemetryFrame frame, EstateGateCrossing crossing)
    {
        if (enrollment is null || fittedGate is null) return;
        routeCapture.Add(new TrackPoint(crossing.X, crossing.Y, crossing.Z, 0, 0, 0));
        referenceLapSeconds = (crossing.TimestampMilliseconds - lapStartTimestamp) / 1000d;
        var track = TrackAlgorithms.BuildTemplate(enrollment.MapName, routeCapture, layoutKind: TrackLayoutKind.Circuit) with
        {
            Source = trackSource,
            TimingKind = TrackTimingKind.EstateGeometry,
            Category = "地产环道",
            CaptureLapCount = 1
        };
        activeTrack = track;
        activeSectors = TrackAlgorithms.CreateSectors(track, requestedCount: enrollment.SectorCount);
        var checkpoints = EstateTrackAlgorithms.CreateCheckpoints(track);
        var now = DateTimeOffset.UtcNow;
        activeDefinition = new EstateTrackDefinition(
            track.Id,
            enrollment.MapName,
            enrollment.Creator,
            enrollment.ShareCode,
            enrollment.MapRevision,
            fittedGate,
            checkpoints,
            null,
            referenceLapSeconds,
            0,
            0,
            now,
            now);
        BeginLap(crossing.TimestampMilliseconds, frame.ArrivalTime);
        SetState(EstateCircuitPhase.ValidatingLap, $"参考圈已录入：{referenceLapSeconds:0.000} s。",
            $"继续完成验证圈；需依次通过 {checkpoints.Count} 个检查点。", enrollmentActive: true);
    }

    private void FinishValidationLap(TelemetryFrame frame, EstateGateCrossing crossing)
    {
        if (activeTrack is null || activeDefinition is null) return;
        var total = (crossing.TimestampMilliseconds - lapStartTimestamp) / 1000d;
        var ratio = ProjectionRatio();
        var valid = ratio >= 0.95 && nextCheckpoint == activeDefinition.Checkpoints.Count &&
                    maximumProgress >= activeTrack.LengthMeters * 0.85;
        if (!valid)
        {
            SetState(EstateCircuitPhase.ValidationFailed,
                $"验证圈未通过：路线有效率 {ratio:P0}，检查点 {nextCheckpoint}/{activeDefinition.Checkpoints.Count}。",
                "修正行驶路线后重试验证圈；必要时重新录入参考圈。", enrollmentActive: true);
            return;
        }

        var now = DateTimeOffset.UtcNow;
        activeTrack = activeTrack with { CaptureLapCount = 2, Confidence = ratio, UpdatedAt = now };
        activeDefinition = activeDefinition with
        {
            ValidationLapSeconds = total,
            ValidationProjectionRatio = ratio,
            UpdatedAt = now
        };
        store.SaveTrack(activeTrack, activeSectors, activeDefinition);
        SetState(EstateCircuitPhase.Ready,
            $"地产环道已保存：参考圈 {referenceLapSeconds:0.000} s，验证圈 {total:0.000} s。",
            "可关闭向导，在赛道页手动启用地产计时。", enrollmentActive: false);
        LogIfInitialized($"Estate circuit {activeTrack.Id} saved with {activeTrack.Points.Count} points and {activeDefinition.Checkpoints.Count} checkpoints.");
    }

    private void FinishTimedLap(TelemetryFrame frame, EstateGateCrossing crossing)
    {
        if (activeTrack is null || activeDefinition is null) return;
        var total = (crossing.TimestampMilliseconds - lapStartTimestamp) / 1000d;
        var ratio = ProjectionRatio();
        var valid = ratio >= 0.95 && nextCheckpoint == activeDefinition.Checkpoints.Count &&
                    maximumProgress >= activeTrack.LengthMeters * 0.85;
        var invalidReason = valid ? null :
            nextCheckpoint != activeDefinition.Checkpoints.Count ? "estate-checkpoints-incomplete" :
            maximumProgress < activeTrack.LengthMeters * 0.85 ? "estate-route-progress-incomplete" :
            $"estate-projection-low-confidence ({ratio:P0})";
        EnsureComparisonReference(frame);
        var previousHistoricalReference = historicalReferenceLap;
        var samples = Downsample(lapSamples);
        var lap = new LapRecord(
            Guid.NewGuid(),
            activeTrack.Id,
            activeTrack.Direction,
            TrackAlgorithms.SectorSchemaVersion,
            timingSessionId,
            VehicleProfileFingerprint.FromFrame(frame),
            lapStartedAt,
            total,
            valid,
            invalidReason,
            BuildSegments(total, samples),
            samples);
        store.SaveLap(lap);
        ReloadTimingHistory();
        heldComparisons = BuildCompletedComparisons(lap);
        heldComparisonsUntil = frame.ArrivalTime +
                               LapHudDisplayTiming.CompletedLapHoldDuration(getOverlayLayout());
        heldCumulativeHistoricalDeltaSeconds = valid && previousHistoricalReference is not null
            ? total - previousHistoricalReference.TotalSeconds
            : null;
        heldCumulativeHistoricalDeltaUntil = heldCumulativeHistoricalDeltaSeconds is null
            ? DateTimeOffset.MinValue
            : frame.ArrivalTime + LapHudDisplayTiming.CumulativeHistoricalDeltaDuration;
        comparisonPerformanceClass = null;
        historicalReferenceLap = null;
        completedLaps++;
        lastLapSeconds = total;
        BeginLap(crossing.TimestampMilliseconds, frame.ArrivalTime);
        SetState(EstateCircuitPhase.TimingLap,
            valid ? $"完成有效圈：{total:0.000} s。" : $"本圈无效：{invalidReason}。",
            "已从本次过线开始下一圈。", timingActive: true);
    }

    private bool ObserveProjectedLap(TelemetryFrame frame, EstateTimedPosition position, bool finishCrossed)
    {
        if (activeTrack is null || activeDefinition is null) return false;
        var projection = TrackAlgorithms.ProjectConstrained(
            activeTrack.Points, position.X, position.Y, position.Z, projectionIndex);
        if (!IsProjectionValid(activeTrack, projection))
            projection = TrackAlgorithms.ProjectRange(activeTrack.Points, position.X, position.Y, position.Z, 0, activeTrack.Points.Count - 2);
        if (IsProjectionValid(activeTrack, projection))
        {
            if (previousProjectedProgress is double previousProgress)
            {
                var wrappedAtFinish = finishCrossed &&
                                      previousProgress >= activeTrack.LengthMeters * 0.75 &&
                                      projection.S <= activeTrack.LengthMeters * 0.25;
                var delta = projection.S - previousProgress;
                if (!wrappedAtFinish && delta < -0.25)
                    accumulatedReverseProgress += -delta;
                else if (delta > 0.5)
                    accumulatedReverseProgress = Math.Max(0, accumulatedReverseProgress - delta);
                if (accumulatedReverseProgress >= MaximumReverseProgressMeters)
                {
                    AbandonCurrentLap("检测到车辆沿赛道明显回退或使用倒带");
                    return false;
                }
            }
            previousProjectedProgress = projection.S;
            projectionIndex = projection.SegmentIndex;
            validProjectionSamples++;
            maximumProgress = Math.Max(maximumProgress, projection.S);
            var elapsed = (position.TimestampMilliseconds - lapStartTimestamp) / 1000d;
            lapSamples.Add(new LapSample(
                projection.S, elapsed, frame.Raw.Speed, frame.Raw.CurrentEngineRpm, frame.Raw.Gear,
                frame.Normalized.AccelRatio, frame.Normalized.BrakeRatio, 0,
                position.X, position.Y, position.Z,
                new LapDynamics(frame.Raw.Steer / 127d, frame.Raw.TireSlipRatio, frame.Raw.TireSlipAngle, frame.Raw.TireCombinedSlip)));
        }
        else
        {
            invalidProjectionSamples++;
        }

        if (nextCheckpoint < activeDefinition.Checkpoints.Count && previousPosition is EstateTimedPosition previous &&
            EstateTrackAlgorithms.TryDetectForwardCrossing(
                activeDefinition.Checkpoints[nextCheckpoint].Gate,
                previous,
                position,
                out _))
            nextCheckpoint++;
        return true;
    }

    private void BeginLap(long timestamp, DateTimeOffset arrivalTime)
    {
        lapStartTimestamp = timestamp;
        lapStartedAt = arrivalTime;
        ResetLapTracking();
        ResetLiveCumulativeHistoricalDelta();
    }

    private void ResetLapTracking()
    {
        lapSamples.Clear();
        projectionIndex = 0;
        validProjectionSamples = 0;
        invalidProjectionSamples = 0;
        nextCheckpoint = 0;
        maximumProgress = 0;
        previousProjectedProgress = null;
        accumulatedReverseProgress = 0;
    }

    private bool IsLapInProgress() => state.Phase is
        EstateCircuitPhase.CapturingReferenceLap or
        EstateCircuitPhase.ValidatingLap or
        EstateCircuitPhase.TimingLap;

    private void AbandonCurrentLap(string reason)
    {
        var interruptedPhase = state.Phase;
        if (!IsLapInProgress()) return;

        if (interruptedPhase == EstateCircuitPhase.CapturingReferenceLap)
            routeCapture.Clear();

        ResetLapTracking();
        previousPosition = null;
        lastCrossingTimestamp = long.MinValue;
        lapStartTimestamp = 0;
        heldComparisons = null;
        heldComparisonsUntil = DateTimeOffset.MinValue;
        ClearCumulativeHistoricalDeltaDisplay();

        if (interruptedPhase == EstateCircuitPhase.TimingLap)
        {
            SetState(
                EstateCircuitPhase.WaitingForTimingStart,
                $"{reason}，本圈已取消。",
                "本圈不会保存；恢复正常行驶后，下一次正向通过终点线会重新开始计时。",
                timingActive: true);
        }
        else
        {
            SetState(
                EstateCircuitPhase.WaitingForReferenceStart,
                $"{reason}，本圈已取消。",
                interruptedPhase == EstateCircuitPhase.CapturingReferenceLap
                    ? "参考圈不会保存；恢复正常行驶后，下一次正向通过终点线会重新开始录入。"
                    : "验证圈不会保存；恢复正常行驶后，下一次正向通过终点线会重新开始验证。",
                enrollmentActive: true);
        }

        LogIfInitialized($"Estate lap abandoned: phase={interruptedPhase}, reason={reason}");
    }

    private static bool IsImplausiblePositionJump(EstateTimedPosition previous, EstateTimedPosition current)
    {
        var elapsedSeconds = (current.TimestampMilliseconds - previous.TimestampMilliseconds) / 1000d;
        if (elapsedSeconds is <= 0 or > 1.5) return false;

        var dx = current.X - previous.X;
        var dy = current.Y - previous.Y;
        var dz = current.Z - previous.Z;
        var distanceMeters = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        var reportedSpeed = Math.Max(previous.SpeedMetersPerSecond, current.SpeedMetersPerSecond);
        var plausibleDistanceMeters = Math.Max(40, reportedSpeed * elapsedSeconds * 3 + 15);
        return distanceMeters > plausibleDistanceMeters;
    }

    private void AddRoutePoint(EstateTimedPosition position)
    {
        var point = new TrackPoint(position.X, position.Y, position.Z, 0, 0, 0);
        if (routeCapture.Count == 0 || routeCapture[^1].DistanceSquaredTo(point) >= 4)
            routeCapture.Add(point);
    }

    private void CaptureTraceSample(List<EstateGatePoint> target, EstateTimedPosition position)
    {
        if (position.SpeedMetersPerSecond is < 0.4 or > 4.5) return;
        var point = new EstateGatePoint(position.X, position.Y, position.Z);
        if (target.Count == 0 || DistanceSquared(target[^1], point) >= 0.01) target.Add(point);
    }

    private void CaptureDirectionSample(EstateTimedPosition position)
    {
        if (position.SpeedMetersPerSecond < 1) return;
        var point = new EstateGatePoint(position.X, position.Y, position.Z);
        if (directionTrace.Count == 0 || DistanceSquared(directionTrace[^1], point) >= 0.25)
            directionTrace.Add(point);
    }

    private void RefreshTraceState()
    {
        SetState(state.Phase, state.Status,
            $"有效样本：第一次 {firstTrace.Count}，第二次 {secondTrace.Count}。保持低速并沿横线行驶。",
            enrollmentActive: true);
    }

    private void UpdateCurrentLapState(double elapsed)
    {
        if (lastHudUpdateTimestamp != long.MinValue &&
            lastUnwrappedTimestamp - lastHudUpdateTimestamp < HudRefreshIntervalMilliseconds)
            return;
        lastHudUpdateTimestamp = lastUnwrappedTimestamp;
        var ratio = ProjectionRatio();
        var updated = state with
        {
            UpdatedAt = DateTimeOffset.UtcNow,
            CurrentLapSeconds = Math.Max(0, elapsed),
            PassedCheckpoints = nextCheckpoint,
            TotalCheckpoints = activeDefinition?.Checkpoints.Count ?? 0,
            ProjectionRatio = ratio
        };
        Volatile.Write(ref state, updated);
        PublishHud(updated);
    }

    private void SetState(
        EstateCircuitPhase phase,
        string statusText,
        string instruction,
        bool timingActive = false,
        bool enrollmentActive = false)
    {
        var gate = activeDefinition?.StartFinishGate ?? fittedGate;
        var updated = new EstateCircuitState(
            DateTimeOffset.UtcNow,
            phase,
            statusText,
            instruction,
            enrollment?.MapName ?? activeDefinition?.MapName,
            activeTrack?.Id,
            firstTrace.Count,
            secondTrace.Count,
            gate is null ? null : EstateTrackAlgorithms.GateWidth(gate),
            gate?.FitRmsMeters,
            gate?.TraceOffsetMeters,
            gate?.TraceAngleDifferenceDegrees,
            phase is EstateCircuitPhase.CapturingReferenceLap or EstateCircuitPhase.ValidatingLap or EstateCircuitPhase.TimingLap
                ? Math.Max(0, (lastUnwrappedTimestamp - lapStartTimestamp) / 1000d)
                : 0,
            lastLapSeconds,
            completedLaps,
            nextCheckpoint,
            activeDefinition?.Checkpoints.Count ?? 0,
            ProjectionRatio(),
            timingActive,
            enrollmentActive);
        Volatile.Write(ref state, updated);
        lastHudUpdateTimestamp = lastUnwrappedTimestamp;
        PublishHud(updated);
    }

    private void PublishHud(EstateCircuitState updated)
    {
        var show = updated.Phase is EstateCircuitPhase.WaitingForReferenceStart or
            EstateCircuitPhase.CapturingReferenceLap or EstateCircuitPhase.ValidatingLap or
            EstateCircuitPhase.WaitingForTimingStart or EstateCircuitPhase.TimingLap;
        if (!show)
        {
            Volatile.Write(ref hudSnapshot, null);
            return;
        }
        if (lastFrame is { } comparisonFrame) EnsureComparisonReference(comparisonFrame);
        var now = lastFrame?.ArrivalTime ?? updated.UpdatedAt;
        var showingPreviousLap = heldComparisons is not null &&
                                 now < heldComparisonsUntil;
        if (!showingPreviousLap)
        {
            heldComparisons = null;
        }
        var currentSector = CurrentSector();
        if (currentSector > 0 && currentSector != liveCumulativeHistoricalDeltaSector)
        {
            liveCumulativeHistoricalDeltaSector = currentSector;
            liveCumulativeHistoricalDeltaSeconds = CurrentCumulativeHistoricalDelta(currentSector);
            liveCumulativeHistoricalDeltaUntil = liveCumulativeHistoricalDeltaSeconds is null
                ? DateTimeOffset.MinValue
                : now + LapHudDisplayTiming.CumulativeHistoricalDeltaDuration;
        }
        if (heldCumulativeHistoricalDeltaSeconds is not null &&
            now >= heldCumulativeHistoricalDeltaUntil)
        {
            heldCumulativeHistoricalDeltaSeconds = null;
        }
        if (liveCumulativeHistoricalDeltaSeconds is not null &&
            now >= liveCumulativeHistoricalDeltaUntil)
        {
            liveCumulativeHistoricalDeltaSeconds = null;
        }
        var performanceClass = lastFrame is null
            ? -1
            : PerformanceClassCatalog.Resolve(lastFrame.Raw.CarClass, lastFrame.Raw.CarPerformanceIndex);
        var comparisons = showingPreviousLap
            ? heldComparisons!
            : BuildLiveComparisons(performanceClass);
        var cumulativeHistoricalDelta = heldCumulativeHistoricalDeltaSeconds ??
                                        liveCumulativeHistoricalDeltaSeconds;
        Volatile.Write(ref hudSnapshot, new LapHudState(
            updated.UpdatedAt,
            lastFrame?.Source ?? TelemetrySourceKind.Live,
            true,
            updated.Phase is EstateCircuitPhase.CapturingReferenceLap or EstateCircuitPhase.ValidatingLap
                ? TrackLearningPhase.CapturingReferenceLap
                : TrackLearningPhase.ComparingLaps,
            updated.Status,
            updated.Instruction,
            TrackMatchState.Confirmed,
            updated.ProjectionRatio,
            updated.MapName ?? "地产环道",
            currentSector,
            comparisons,
            activeTrack is null ? 0 : Math.Clamp(maximumProgress / Math.Max(1, activeTrack.LengthMeters), 0, 1),
            updated.CompletedLaps,
            true,
            showingPreviousLap)
        {
            CurrentLapSeconds = updated.CurrentLapSeconds,
            CompetitionSessionId = timingSessionId,
            CumulativeHistoricalDeltaSeconds = cumulativeHistoricalDelta
        });
    }

    private IReadOnlyList<SectorComparison> BuildLiveComparisons(int performanceClass)
    {
        if (activeSectors.Count == 0)
            return Enumerable.Range(0, 4)
                .Select(index => new SectorComparison(index, null, null, null, null, SectorColorState.Gray, index == 0))
                .ToArray();

        var currentSector = CurrentSector();
        return activeSectors.Select(sector =>
        {
            var current = maximumProgress + 0.5 >= sector.EndS
                ? SectorDuration(lapSamples, sector)
                : null;
            var (sessionBest, historicalBest) = BestTimingSectorTimes(sector.Index, performanceClass);
            var state = SectorColorClassifier.Classify(current, current is not null, sessionBest, historicalBest);
            var reference = sessionBest ?? historicalBest;
            return new SectorComparison(
                sector.Index,
                current,
                sessionBest,
                historicalBest,
                current is not null && reference is not null ? current - reference : null,
                state,
                sector.Index == currentSector);
        }).ToArray();
    }

    private IReadOnlyList<SectorComparison> BuildCompletedComparisons(LapRecord lap)
    {
        return activeSectors.Select(sector =>
        {
            var segment = lap.Segments.FirstOrDefault(candidate => candidate.Index == sector.Index);
            var current = segment is { IsValid: true, TimeSeconds: > 0 }
                ? segment.TimeSeconds
                : (double?)null;
            var (sessionBest, historicalBest) = BestTimingSectorTimes(sector.Index, lap.Vehicle.CarClass);
            if (lap.IsValid && current is not null)
            {
                sessionBest = sessionBest is null ? current : Math.Min(sessionBest.Value, current.Value);
                historicalBest = historicalBest is null ? current : Math.Min(historicalBest.Value, current.Value);
            }
            var state = SectorColorClassifier.Classify(current, lap.IsValid && current is not null, sessionBest, historicalBest);
            var reference = sessionBest ?? historicalBest;
            return new SectorComparison(
                sector.Index,
                current,
                sessionBest,
                historicalBest,
                current is not null && reference is not null ? current - reference : null,
                state,
                false);
        }).ToArray();
    }

    private (double? SessionBest, double? HistoricalBest) BestTimingSectorTimes(int sectorIndex, int performanceClass)
    {
        var comparable = timingHistory.Where(lap =>
            lap.IsValid &&
            lap.Vehicle.CarClass == performanceClass &&
            lap.SectorSchemaVersion == TrackAlgorithms.SectorSchemaVersion);
        static double? Best(IEnumerable<LapSummary> laps, int index) => laps
            .SelectMany(lap => lap.Segments)
            .Where(segment => segment.Index == index && segment.IsValid && segment.TimeSeconds > 0)
            .Select(segment => (double?)segment.TimeSeconds)
            .Min();
        return (
            Best(comparable.Where(lap => lap.SessionId == timingSessionId), sectorIndex),
            Best(comparable, sectorIndex));
    }

    private static double? SectorDuration(IReadOnlyList<LapSample> samples, SectorDefinition sector)
    {
        var start = sector.Index == 0 ? 0 : ElapsedAtProgress(samples, sector.StartS);
        var end = ElapsedAtProgress(samples, sector.EndS);
        return start is not null && end is not null && end > start ? end - start : null;
    }

    private double? CurrentCumulativeHistoricalDelta(int currentSector)
    {
        if (historicalReferenceLap is null || currentSector <= 0 || currentSector >= activeSectors.Count)
            return null;
        var currentElapsed = ElapsedAtProgress(lapSamples, activeSectors[currentSector].StartS);
        if (currentElapsed is null || currentElapsed <= 0) return null;

        var referenceElapsed = 0d;
        for (var index = 0; index < currentSector; index++)
        {
            var segment = historicalReferenceLap.Segments.FirstOrDefault(candidate => candidate.Index == index);
            if (segment is not { IsValid: true, TimeSeconds: > 0 }) return null;
            referenceElapsed += segment.TimeSeconds;
        }
        return currentElapsed.Value - referenceElapsed;
    }

    private static double? ElapsedAtProgress(IReadOnlyList<LapSample> samples, double progress)
    {
        if (samples.Count == 0) return null;
        if (progress <= samples[0].S) return samples[0].ElapsedSeconds;
        for (var index = 1; index < samples.Count; index++)
        {
            if (samples[index].S < progress) continue;
            var previous = samples[index - 1];
            var current = samples[index];
            var distance = current.S - previous.S;
            if (distance <= 1e-6) return current.ElapsedSeconds;
            var amount = Math.Clamp((progress - previous.S) / distance, 0, 1);
            return previous.ElapsedSeconds + (current.ElapsedSeconds - previous.ElapsedSeconds) * amount;
        }
        return samples[^1].ElapsedSeconds;
    }

    private void EnsureComparisonReference(TelemetryFrame frame)
    {
        if (activeTrack is null) return;
        var performanceClass = PerformanceClassCatalog.Resolve(frame.Raw.CarClass, frame.Raw.CarPerformanceIndex);
        if (comparisonPerformanceClass == performanceClass) return;
        comparisonPerformanceClass = performanceClass;
        var fastest = timingHistory
            .Where(lap => lap.IsValid && lap.Vehicle.CarClass == performanceClass)
            .OrderBy(lap => lap.TotalSeconds)
            .ThenBy(lap => lap.StartedAt)
            .ThenBy(lap => lap.Id)
            .FirstOrDefault();
        historicalReferenceLap = fastest is null ? null : store.LoadLap(fastest.Id);
    }

    private void ReloadTimingHistory()
    {
        timingHistory.Clear();
        if (activeTrack is not null)
            timingHistory.AddRange(store.LoadLapSummaries(activeTrack.Id, LazyForzaStore.MaxLapsPerTrack));
    }

    private int CurrentSector()
    {
        if (activeSectors.Count == 0) return 0;
        return Math.Clamp(activeSectors.ToList().FindLastIndex(sector => maximumProgress >= sector.StartS), 0, activeSectors.Count - 1);
    }

    private double ProjectionRatio() => validProjectionSamples /
        (double)Math.Max(1, validProjectionSamples + invalidProjectionSamples);

    private static bool IsProjectionValid(TrackTemplate track, ProjectionResult projection) =>
        projection.IsValid && projection.DistanceMeters <= track.MatchingToleranceMeters &&
        projection.ElevationErrorMeters <= Math.Max(8, track.MatchingToleranceMeters * 0.8);

    private IReadOnlyList<LapSegment> BuildSegments(double totalSeconds, IReadOnlyList<LapSample> samples)
    {
        if (activeSectors.Count == 0) return [];
        var result = new List<LapSegment>(activeSectors.Count);
        var previousTime = 0d;
        foreach (var sector in activeSectors)
        {
            var boundaryTime = sector.Index == activeSectors.Count - 1
                ? totalSeconds
                : samples.FirstOrDefault(sample => sample.S >= sector.EndS)?.ElapsedSeconds ?? previousTime;
            var duration = Math.Max(0, boundaryTime - previousTime);
            result.Add(new LapSegment(sector.Index, duration, duration > 0));
            previousTime = boundaryTime;
        }
        return result;
    }

    private static IReadOnlyList<LapSample> Downsample(IReadOnlyList<LapSample> samples)
    {
        if (samples.Count <= 2) return samples.ToArray();
        var result = new List<LapSample> { samples[0] };
        var next = samples[0].ElapsedSeconds + 0.1;
        foreach (var sample in samples.Skip(1).SkipLast(1))
        {
            if (sample.ElapsedSeconds < next) continue;
            result.Add(sample);
            next = sample.ElapsedSeconds + 0.1;
        }
        result.Add(samples[^1]);
        return result;
    }

    private void ResetWorkingState()
    {
        enrollment = null;
        fittedGate = null;
        firstTrace.Clear();
        secondTrace.Clear();
        directionTrace.Clear();
        routeCapture.Clear();
        activeTrack = null;
        activeDefinition = null;
        activeSectors = [];
        previousPosition = null;
        lastCrossingTimestamp = long.MinValue;
        lastUnwrappedTimestamp = 0;
        lastHudUpdateTimestamp = long.MinValue;
        referenceLapSeconds = 0;
        completedLaps = 0;
        lastLapSeconds = null;
        timingHistory.Clear();
        timingSessionId = Guid.Empty;
        lastTelemetryArrival = null;
        nonAdvancingTimestampStartedAt = null;
        minimumAcceptedFrameSequence = long.MinValue;
        minimumAcceptedFrameArrival = DateTimeOffset.MinValue;
        comparisonPerformanceClass = null;
        historicalReferenceLap = null;
        heldComparisons = null;
        heldComparisonsUntil = DateTimeOffset.MinValue;
        ClearCumulativeHistoricalDeltaDisplay();
        lastFrame = null;
        timestampUnwrapper.Reset();
        ResetLapTracking();
    }

    private void ArmAfterLatestTelemetryFrame()
    {
        var latest = Context.Telemetry.Latest;
        minimumAcceptedFrameSequence = latest?.Sequence ?? long.MinValue;
        minimumAcceptedFrameArrival = latest?.ArrivalTime ?? DateTimeOffset.MinValue;
        previousPosition = null;
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

    private void ResetAll(string statusText)
    {
        ResetWorkingState();
        Volatile.Write(ref hudSnapshot, null);
        Volatile.Write(ref state, EmptyState() with { UpdatedAt = DateTimeOffset.UtcNow, Status = statusText });
    }

    private void EnsureEnrollment()
    {
        if (enrollment is null) throw new InvalidOperationException("请先填写地图信息并开始录入。");
    }

    private static EstateCircuitState EmptyState() => new(
        DateTimeOffset.UtcNow,
        EstateCircuitPhase.Idle,
        "未启用地产环道计时。",
        "在赛道页手动添加或选择地产环道。",
        null,
        null,
        0,
        0,
        null,
        null,
        null,
        null,
        0,
        null,
        0,
        0,
        0,
        0,
        false,
        false);

    private static string? NullIfWhiteSpace(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static double DistanceSquared(EstateGatePoint left, EstateGatePoint right)
    {
        var dx = left.X - right.X;
        var dy = left.Y - right.Y;
        var dz = left.Z - right.Z;
        return dx * dx + dy * dy + dz * dz;
    }
}
