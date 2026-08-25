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
    StartFinishReadyToSave,
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

public sealed record EstateEnrollmentDraft(
    int FormatVersion,
    DateTimeOffset SavedAt,
    EstateEnrollmentRequest Enrollment,
    EstateCircuitPhase ResumePhase,
    IReadOnlyList<EstateGatePoint> FirstTrace,
    IReadOnlyList<EstateGatePoint> SecondTrace,
    IReadOnlyList<EstateGatePoint> DirectionTrace,
    EstateTimingGate? FittedGate,
    TrackTemplate? ActiveTrack,
    IReadOnlyList<SectorDefinition> ActiveSectors,
    EstateTrackDefinition? ActiveDefinition,
    double ReferenceLapSeconds);

public sealed record EstateEnrollmentPreview(
    IReadOnlyList<EstateGatePoint> FirstTrace,
    IReadOnlyList<EstateGatePoint> SecondTrace,
    IReadOnlyList<EstateGatePoint> DirectionTrace,
    IReadOnlyList<TrackPoint> ReferenceRoute,
    IReadOnlyList<TrackPoint> CaptureRoute,
    EstateTimingGate? Gate,
    IReadOnlyList<EstateCheckpoint> Checkpoints,
    EstateGatePoint? CurrentPosition);

public sealed record EstateCircuitCompletedLap(
    Guid EventId,
    int LapNumber,
    double LapSeconds,
    IReadOnlyList<double> SectorSeconds,
    bool IsValid,
    string? InvalidReason,
    bool IsBestLapEligible = true);

public enum EstatePitCapturePhase
{
    Idle,
    CapturingLane,
    AwaitingServiceCorners,
    ReadyToSave,
    Saved
}

[Flags]
public enum EstatePitEditScope
{
    None = 0,
    Lane = 1,
    EntryGate = 2,
    ExitGate = 4,
    ServiceZone = 8,
    Settings = 16,
    All = Lane | EntryGate | ExitGate | ServiceZone | Settings
}

public sealed record EstatePitEnrollmentState(
    DateTimeOffset UpdatedAt,
    EstatePitCapturePhase Phase,
    Guid? TrackId,
    string? TrackName,
    int LaneSamples,
    int ServiceCorners,
    bool EntryLineCaptured,
    bool ExitLineCaptured,
    string Status,
    string Instruction,
    bool IsActive);

public sealed record EstatePitEnrollmentRequest(
    Guid TrackId,
    double LaneHalfWidthMeters = 3.5,
    double SpeedLimitKph = 80,
    double MinimumServiceSeconds = 3,
    EstatePitEditScope EditScope = EstatePitEditScope.All);

public sealed record EstatePitEnrollmentPreview(
    IReadOnlyList<EstateGatePoint> CenterLine,
    EstateTimingGate? EntryGate,
    EstateTimingGate? ExitGate,
    IReadOnlyList<EstateGatePoint> ServiceZoneBoundary,
    EstateGatePoint? CurrentPosition);

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
    private readonly Func<string?> playerCodeProvider;
    private readonly object stateGate = new();
    private readonly EstateTimestampUnwrapper timestampUnwrapper = new();
    private readonly List<EstateGatePoint> firstTrace = [];
    private readonly List<EstateGatePoint> secondTrace = [];
    private readonly List<EstateGatePoint> directionTrace = [];
    private readonly List<TrackPoint> routeCapture = [];
    private readonly List<LapSample> lapSamples = [];
    private readonly List<LapSummary> timingHistory = [];
    private readonly List<EstateGatePoint> pitLaneCapture = [];
    private readonly List<EstateGatePoint> pitServiceCorners = [];
    private readonly Queue<(DateTimeOffset Time, EstateGatePoint Point, double SpeedKph)> recentPitSamples = [];
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
    private long minimumAcceptedFrameSequence = long.MinValue;
    private DateTimeOffset minimumAcceptedFrameArrival = DateTimeOffset.MinValue;
    private EstateCircuitCompletedLap? lastCompletedLap;
    private EstatePitEnrollmentRequest? pitEnrollment;
    private EstatePitEnrollmentState pitState = EmptyPitState();
    private EstateTimingGate? pitEntryGate;
    private EstateTimingGate? pitExitGate;
    private double? pitEntryProgressMeters;
    private double? pitExitProgressMeters;
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
    private bool pitTransitActive;
    private PitRouteProjection? previousPitRouteProjection;
    private double? activePitEntryProgressMeters;
    private double? activePitExitProgressMeters;
    private bool invalidateCurrentLapOnDriverIntervention = true;
    private DateTimeOffset? invalidProjectionStartedAt;
    private bool trackDeviationDetected;
    private Guid? startFinishEditTrackId;
    private TrackTemplate? startFinishEditTrack;
    private EstateTrackDefinition? startFinishEditDefinition;
    private IReadOnlyList<SectorDefinition> startFinishEditSectors = [];
    private EstatePitDefinition? originalPitDefinition;
    private EstatePitEditScope pitEditScope = EstatePitEditScope.All;

    public EstateCircuitModule(
        LazyForzaStore store,
        TelemetrySourceKind sourceKind,
        Func<OverlayLayout>? getOverlayLayout = null,
        Func<string?>? playerCodeProvider = null)
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
        this.playerCodeProvider = playerCodeProvider ?? (() => null);
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

    public EstateEnrollmentPreview EnrollmentPreview
    {
        get
        {
            lock (stateGate)
            {
                var current = lastFrame is null
                    ? (EstateGatePoint?)null
                    : new EstateGatePoint(
                        lastFrame.Raw.Position.X,
                        lastFrame.Raw.Position.Y,
                        lastFrame.Raw.Position.Z);
                return new EstateEnrollmentPreview(
                    firstTrace.ToArray(),
                    secondTrace.ToArray(),
                    directionTrace.ToArray(),
                    activeTrack?.Points.ToArray() ?? [],
                    routeCapture.ToArray(),
                    fittedGate ?? activeDefinition?.StartFinishGate,
                    activeDefinition?.Checkpoints.ToArray() ?? [],
                    current);
            }
        }
    }

    public int ActiveSectorCount
    {
        get { lock (stateGate) return activeSectors.Count; }
    }

    public IReadOnlyList<SectorDefinition> ActiveSectors
    {
        get { lock (stateGate) return activeSectors.ToArray(); }
    }

    public int ActiveCurrentSector
    {
        get { lock (stateGate) return CurrentSector(); }
    }

    public EstateCircuitCompletedLap? LastCompletedLap
    {
        get { lock (stateGate) return lastCompletedLap; }
    }

    public EstatePitEnrollmentState PitState => Volatile.Read(ref pitState);
    public EstatePitEditScope PitEditScope
    {
        get { lock (stateGate) return pitEditScope; }
    }

    public EstatePitEnrollmentPreview PitEnrollmentPreview
    {
        get
        {
            lock (stateGate)
            {
                var current = lastFrame is null
                    ? (EstateGatePoint?)null
                    : new EstateGatePoint(
                        lastFrame.Raw.Position.X,
                        lastFrame.Raw.Position.Y,
                        lastFrame.Raw.Position.Z);
                return new EstatePitEnrollmentPreview(
                    pitLaneCapture.ToArray(),
                    pitEntryGate,
                    pitExitGate,
                    pitServiceCorners.ToArray(),
                    current);
            }
        }
    }

    public void BeginPitEnrollment(EstatePitEnrollmentRequest request)
    {
        lock (stateGate)
        {
            if (state.IsTimingActive || state.IsEnrollmentActive)
                throw new InvalidOperationException("请先停止地产计时或赛道录入，再配置维修区。");
            var loaded = store.LoadTrack(request.TrackId) ??
                         throw new InvalidOperationException("没有找到所选地产环道。");
            var definition = store.LoadEstateTrackDefinition(request.TrackId) ??
                             throw new InvalidOperationException("所选赛道缺少地产计时定义。");
            if (loaded.Track.TimingKind != TrackTimingKind.EstateGeometry)
                throw new InvalidOperationException("只能为地产环道配置维修区。");
            var scope = request.EditScope & EstatePitEditScope.All;
            if (scope == EstatePitEditScope.None) scope = EstatePitEditScope.Settings;
            if (definition.Pit is null && scope != EstatePitEditScope.All)
                throw new InvalidOperationException("这条赛道还没有维修区，请先完成一次完整录入。");
            var laneHalfWidth = Math.Clamp(request.LaneHalfWidthMeters, 1.5, 10);
            var speedLimit = Math.Clamp(request.SpeedLimitKph, 10, 300);
            var minimumServiceSeconds = Math.Clamp(request.MinimumServiceSeconds, 1, 60);
            if (definition.Pit is { } retainedSettings && !scope.HasFlag(EstatePitEditScope.Settings))
            {
                laneHalfWidth = retainedSettings.LaneHalfWidthMeters;
                speedLimit = retainedSettings.SpeedLimitKph;
                minimumServiceSeconds = retainedSettings.MinimumServiceSeconds;
            }
            pitEnrollment = request with
            {
                LaneHalfWidthMeters = laneHalfWidth,
                SpeedLimitKph = speedLimit,
                MinimumServiceSeconds = minimumServiceSeconds,
                EditScope = scope
            };
            pitEditScope = scope;
            originalPitDefinition = definition.Pit;
            pitLaneCapture.Clear();
            pitServiceCorners.Clear();
            if (definition.Pit is { } existing)
            {
                if (!scope.HasFlag(EstatePitEditScope.Lane))
                    pitLaneCapture.AddRange(existing.CenterLine);
                if (!scope.HasFlag(EstatePitEditScope.ServiceZone) && existing.ServiceZoneBoundary is { Count: >= 3 } boundary)
                    pitServiceCorners.AddRange(boundary);
            }
            pitEntryGate = scope.HasFlag(EstatePitEditScope.EntryGate) ? null : definition.Pit?.EntryGate;
            pitExitGate = scope.HasFlag(EstatePitEditScope.ExitGate) ? null : definition.Pit?.ExitGate;
            pitEntryProgressMeters = null;
            pitExitProgressMeters = null;
            recentPitSamples.Clear();
            if (pitLaneCapture.Count >= 2)
                RefreshPitGateProgress();
            var readyWithoutCapture = !scope.HasFlag(EstatePitEditScope.Lane) &&
                                      !scope.HasFlag(EstatePitEditScope.EntryGate) &&
                                      !scope.HasFlag(EstatePitEditScope.ExitGate) &&
                                      !scope.HasFlag(EstatePitEditScope.ServiceZone);
            SetPitState(
                readyWithoutCapture ? EstatePitCapturePhase.ReadyToSave :
                pitLaneCapture.Count >= 2 ? EstatePitCapturePhase.AwaitingServiceCorners : EstatePitCapturePhase.Idle,
                request.TrackId,
                loaded.Track.Name,
                definition.Pit is null ? "维修区录入已准备。" : $"已载入维修区，仅重设：{PitScopeText(scope)}。",
                NextPitInstruction(scope),
                true);
        }
    }

    public void StartPitLaneCapture()
    {
        lock (stateGate)
        {
            EnsurePitEnrollment();
            if (!pitEditScope.HasFlag(EstatePitEditScope.Lane))
                throw new InvalidOperationException("本次没有选择重录维修区通道。");
            if (pitState.Phase is not (EstatePitCapturePhase.Idle or EstatePitCapturePhase.AwaitingServiceCorners))
                throw new InvalidOperationException("当前步骤不能开始维修区通道录入。");
            pitLaneCapture.Clear();
            pitEntryProgressMeters = null;
            pitExitProgressMeters = null;
            ArmAfterLatestTelemetryFrame();
            SetPitState(EstatePitCapturePhase.CapturingLane, pitEnrollment!.TrackId, pitState.TrackName,
                "正在录入维修区通道。",
                "以 2–30 km/h 从赛道分流点前开始，沿通道中心驶过维修区，到赛道并道点后停车并结束录入。", true);
        }
    }

    public void StopPitLaneCapture()
    {
        lock (stateGate)
        {
            EnsurePitEnrollment();
            if (pitState.Phase != EstatePitCapturePhase.CapturingLane)
                throw new InvalidOperationException("当前没有正在录入的维修区通道。");
            if (pitLaneCapture.Count < 12 || PolylineLength(pitLaneCapture) < 15)
                throw new InvalidOperationException("维修区通道样本或长度不足。请从入口前重新开始，完整驶到出口后再停止。");
            var maximumGap = EstatePitGeometryValidation.MaximumSegmentMeters(pitLaneCapture);
            if (maximumGap > EstatePitGeometryValidation.MaximumCaptureSegmentMeters)
            {
                pitLaneCapture.Clear();
                SetPitState(
                    EstatePitCapturePhase.Idle,
                    pitEnrollment!.TrackId,
                    pitState.TrackName,
                    $"维修区通道录入失败：遥测轨迹中断 {maximumGap:0.0} 米。",
                    "请保持 2–30 km/h，从分流点前连续驶到并道点后重新录入。",
                    true);
                throw new InvalidOperationException(
                    $"维修区通道存在 {maximumGap:0.0} 米的采样断点，无法可靠生成维修区起终点门。请降低车速并完整重录维修区通道。");
            }
            var simplified = ResamplePitLane(pitLaneCapture, 0.75);
            pitLaneCapture.Clear();
            pitLaneCapture.AddRange(simplified);
            RefreshPitGateProgress();
            SetPitState(PitCapturePhaseForCurrent(), pitEnrollment!.TrackId, pitState.TrackName,
                $"维修区通道已录入：{PolylineLength(pitLaneCapture):0} 米。",
                NextPitInstruction(pitEditScope), true);
        }
    }

    public EstateTimingGate CapturePitEntryGate() => CapturePitBoundaryGate(entry: true);

    public EstateTimingGate CapturePitExitGate() => CapturePitBoundaryGate(entry: false);

    private EstateTimingGate CapturePitBoundaryGate(bool entry)
    {
        lock (stateGate)
        {
            EnsurePitEnrollment();
            var requiredScope = entry ? EstatePitEditScope.EntryGate : EstatePitEditScope.ExitGate;
            if (!pitEditScope.HasFlag(requiredScope))
                throw new InvalidOperationException(entry ? "本次没有选择重设入口线。" : "本次没有选择重设出口线。");
            if (pitState.Phase is not (EstatePitCapturePhase.AwaitingServiceCorners or EstatePitCapturePhase.ReadyToSave))
                throw new InvalidOperationException("请先完成维修区通道录入。");
            var position = StablePitPosition("请把车停在需要设置的门线中心约 1 秒后再确认。");
            var captured = CreatePitGateAtPoint(
                pitLaneCapture,
                position,
                pitEnrollment!.LaneHalfWidthMeters);
            if (entry)
            {
                if (pitExitProgressMeters is double exitProgress && captured.ProgressMeters >= exitProgress - 2)
                    throw new InvalidOperationException("入口线必须位于出口线之前，且两条线至少间隔 2 米。");
                pitEntryGate = captured.Gate;
                pitEntryProgressMeters = captured.ProgressMeters;
            }
            else
            {
                if (pitEntryProgressMeters is double entryProgress && captured.ProgressMeters <= entryProgress + 2)
                    throw new InvalidOperationException("出口线必须位于入口线之后，且两条线至少间隔 2 米。");
                pitExitGate = captured.Gate;
                pitExitProgressMeters = captured.ProgressMeters;
            }
            SetPitState(
                PitCapturePhaseForCurrent(),
                pitEnrollment.TrackId,
                pitState.TrackName,
                entry ? "维修区入口线已确认。" : "维修区出口线已确认。",
                NextPitInstruction(pitEditScope),
                true);
            return captured.Gate;
        }
    }

    public EstateGatePoint CaptureServiceZoneCorner()
    {
        lock (stateGate)
        {
            EnsurePitEnrollment();
            if (!pitEditScope.HasFlag(EstatePitEditScope.ServiceZone))
                throw new InvalidOperationException("本次没有选择重录换胎区。");
            if (pitState.Phase is not (EstatePitCapturePhase.AwaitingServiceCorners or EstatePitCapturePhase.ReadyToSave))
                throw new InvalidOperationException("请先完成维修区通道录入。");
            if (pitServiceCorners.Count >= 8)
                throw new InvalidOperationException("换胎区最多记录 8 个边界点。需要重录时请先清空角点。");
            var point = StablePitPosition("请把车停稳约 1 秒后再记录角点。");
            if (pitServiceCorners.Any(existing => DistanceSquared(existing, point) < 1))
                throw new InvalidOperationException("这个角点与已记录角点过近，请移动到换胎区的下一个角。");
            pitServiceCorners.Add(point);
            var ready = PitCapturePhaseForCurrent() == EstatePitCapturePhase.ReadyToSave;
            SetPitState(ready ? EstatePitCapturePhase.ReadyToSave : EstatePitCapturePhase.AwaitingServiceCorners,
                pitEnrollment!.TrackId, pitState.TrackName,
                $"已记录 {pitServiceCorners.Count} 个换胎区边界点。",
                ready
                    ? "边界已闭合，可以保存；如换胎区不是四边形，可继续记录到最多 8 个点。"
                    : "继续按同一绕行方向记录角点，至少需要 4 个且围成面积大于 4 平方米的区域。",
                true);
            return point;
        }
    }

    public void ClearServiceZoneCorners()
    {
        lock (stateGate)
        {
            EnsurePitEnrollment();
            if (!pitEditScope.HasFlag(EstatePitEditScope.ServiceZone))
                throw new InvalidOperationException("本次没有选择重录换胎区。");
            pitServiceCorners.Clear();
            SetPitState(EstatePitCapturePhase.AwaitingServiceCorners, pitEnrollment!.TrackId, pitState.TrackName,
                "换胎区角点已清空。", "停车后按顺时针或逆时针重新记录至少 4 个角点。", true);
        }
    }

    public EstatePitDefinition SavePitEnrollment()
    {
        lock (stateGate)
        {
            EnsurePitEnrollment();
            var serviceZoneEdited = pitEditScope.HasFlag(EstatePitEditScope.ServiceZone);
            if (pitLaneCapture.Count < 2 || pitEntryGate is null || pitExitGate is null ||
                (serviceZoneEdited && (pitServiceCorners.Count < 4 || PolygonArea(pitServiceCorners) < 4)) ||
                (!serviceZoneEdited && originalPitDefinition is null))
                throw new InvalidOperationException("维修区通道、入口线、出口线或换胎区边界尚未完成，不能保存。");
            var loaded = store.LoadTrack(pitEnrollment!.TrackId) ??
                         throw new InvalidOperationException("赛道已不存在。");
            var definition = store.LoadEstateTrackDefinition(pitEnrollment.TrackId) ??
                             throw new InvalidOperationException("地产赛道定义已不存在。");
            var maximumGap = EstatePitGeometryValidation.MaximumSegmentMeters(pitLaneCapture);
            if (maximumGap > EstatePitGeometryValidation.MaximumPortableSegmentMeters)
                throw new InvalidOperationException(
                    $"现有维修区通道存在 {maximumGap:0.0} 米的轨迹断点。请勾选“维修区通道”并完整重录后再保存。");
            var entryCapture = CreatePitGateAtPoint(
                pitLaneCapture,
                GateCenter(pitEntryGate!),
                pitEnrollment.LaneHalfWidthMeters);
            var exitCapture = CreatePitGateAtPoint(
                pitLaneCapture,
                GateCenter(pitExitGate!),
                pitEnrollment.LaneHalfWidthMeters);
            if (entryCapture.ProgressMeters >= exitCapture.ProgressMeters - 2)
                throw new InvalidOperationException("维修区入口线必须位于出口线之前，且两条线至少间隔 2 米。");
            var laneEdited = pitEditScope.HasFlag(EstatePitEditScope.Lane) || originalPitDefinition is null;
            var widthChanged = originalPitDefinition is null ||
                               Math.Abs(pitEnrollment.LaneHalfWidthMeters - originalPitDefinition.LaneHalfWidthMeters) > 0.001;
            var entry = pitEditScope.HasFlag(EstatePitEditScope.EntryGate) || laneEdited || widthChanged
                ? entryCapture.Gate
                : originalPitDefinition!.EntryGate;
            var exit = pitEditScope.HasFlag(EstatePitEditScope.ExitGate) || laneEdited || widthChanged
                ? exitCapture.Gate
                : originalPitDefinition!.ExitGate;
            var pitStartFinishGate = originalPitDefinition?.StartFinishGate;
            if (laneEdited || widthChanged || pitStartFinishGate is null)
            {
                if (!EstateTrackAlgorithms.TryCreatePitStartFinishGate(
                        definition.StartFinishGate,
                        pitLaneCapture,
                        pitEnrollment.LaneHalfWidthMeters,
                        out pitStartFinishGate))
                    throw new InvalidOperationException(
                        "录入的维修区通道没有沿比赛方向穿过起终点所在平面。请从入口前开始，完整驶过维修区并在出口后停止录入。");
            }
            var center = serviceZoneEdited
                ? new EstateGatePoint(
                    pitServiceCorners.Average(point => point.X),
                    pitServiceCorners.Average(point => point.Y),
                    pitServiceCorners.Average(point => point.Z))
                : originalPitDefinition!.ServiceCenter;
            var radius = serviceZoneEdited
                ? pitServiceCorners.Max(point => Math.Sqrt(DistanceSquared(point, center)))
                : originalPitDefinition!.ServiceRadiusMeters;
            var boundary = serviceZoneEdited
                ? pitServiceCorners.ToArray()
                : originalPitDefinition!.ServiceZoneBoundary;
            var pit = new EstatePitDefinition(
                entry,
                exit,
                pitLaneCapture.ToArray(),
                center,
                radius,
                pitEnrollment.SpeedLimitKph,
                pitEnrollment.MinimumServiceSeconds,
                pitEnrollment.LaneHalfWidthMeters,
                boundary,
                pitStartFinishGate);
            store.SaveTrack(loaded.Track, loaded.Sectors, definition with
            {
                Pit = pit,
                UpdatedAt = DateTimeOffset.UtcNow
            });
            SetPitState(EstatePitCapturePhase.Saved, pitEnrollment.TrackId, loaded.Track.Name,
                "维修区定义已保存。",
                "入口、通道、维修停留区、维修区起终点门和出口会随 .lfzestate 文件一起导出。", false);
            LogIfInitialized($"Estate pit {pitEnrollment.TrackId} saved with {pit.CenterLine.Count} lane points and {pit.ServiceZoneBoundary?.Count ?? 0} service points.");
            return pit;
        }
    }

    public void CancelPitEnrollment()
    {
        lock (stateGate)
        {
            pitEnrollment = null;
            pitLaneCapture.Clear();
            pitServiceCorners.Clear();
            pitEntryGate = null;
            pitExitGate = null;
            pitEntryProgressMeters = null;
            pitExitProgressMeters = null;
            originalPitDefinition = null;
            pitEditScope = EstatePitEditScope.All;
            recentPitSamples.Clear();
            Volatile.Write(ref pitState, EmptyPitState() with { UpdatedAt = DateTimeOffset.UtcNow, Status = "已取消维修区录入。" });
        }
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
        var normalized = NormalizeEnrollment(request);
        lock (stateGate)
        {
            if (pitState.IsActive)
                throw new InvalidOperationException("请先完成或取消维修区录入。");
            ResetWorkingState();
            enrollment = normalized;
            SetState(EstateCircuitPhase.Idle, "地产环道录入已准备。", "开始第一次终点线描摹。", enrollmentActive: true);
        }
    }

    public void BeginStartFinishRevision(Guid trackId)
    {
        lock (stateGate)
        {
            if (state.IsTimingActive || state.IsEnrollmentActive || pitState.IsActive)
                throw new InvalidOperationException("请先结束当前地产流程，再重设起终点线。");
            var loaded = store.LoadTrack(trackId) ??
                         throw new InvalidOperationException("没有找到要修改的地产环道。");
            var definition = store.LoadEstateTrackDefinition(trackId) ??
                             throw new InvalidOperationException("所选赛道缺少地产环道定义。");
            ResetWorkingState();
            startFinishEditTrackId = trackId;
            startFinishEditTrack = loaded.Track;
            startFinishEditSectors = loaded.Sectors;
            startFinishEditDefinition = definition;
            enrollment = new EstateEnrollmentRequest(
                definition.MapName,
                definition.Creator,
                definition.ShareCode,
                definition.MapRevision,
                Math.Clamp(loaded.Sectors.Count, TrackAlgorithms.MinimumSectorCount, TrackAlgorithms.MaximumSectorCount));
            activeTrack = loaded.Track;
            activeSectors = loaded.Sectors;
            activeDefinition = definition;
            fittedGate = null;
            SetState(
                EstateCircuitPhase.Idle,
                "起终点线重设已准备。",
                "先完成两次横向描摹，再采集正常比赛方向。原定义会在确认保存前保持不变。",
                enrollmentActive: true);
        }
    }

    public void SaveStartFinishRevision()
    {
        lock (stateGate)
        {
            if (state.Phase != EstateCircuitPhase.StartFinishReadyToSave ||
                startFinishEditTrackId is null || startFinishEditTrack is null ||
                startFinishEditDefinition is null || fittedGate is null)
                throw new InvalidOperationException("请先完成两次终点线描摹和比赛方向采样。");
            var center = GateCenter(fittedGate);
            var projection = TrackAlgorithms.ProjectRange(
                startFinishEditTrack.Points,
                center.X,
                center.Y,
                center.Z,
                0,
                startFinishEditTrack.Points.Count - 2);
            var originalCenter = GateCenter(startFinishEditDefinition.StartFinishGate);
            var originalProjection = TrackAlgorithms.ProjectRange(
                startFinishEditTrack.Points,
                originalCenter.X,
                originalCenter.Y,
                originalCenter.Z,
                0,
                startFinishEditTrack.Points.Count - 2);
            var progressDifference = Math.Abs(projection.S - originalProjection.S);
            progressDifference = Math.Min(progressDifference, startFinishEditTrack.LengthMeters - progressDifference);
            if (!projection.IsValid || !originalProjection.IsValid || projection.DistanceMeters > 10 || progressDifference > 10)
                throw new InvalidOperationException("新起终点线必须位于原起终点位置 10 米范围内。若地图起点已明显移动，请重新录入整条赛道。");

            var pit = startFinishEditDefinition.Pit;
            if (pit is not null)
            {
                if (!EstateTrackAlgorithms.TryCreatePitStartFinishGate(
                        fittedGate,
                        pit.CenterLine,
                        pit.LaneHalfWidthMeters,
                        out var pitStartFinishGate))
                    throw new InvalidOperationException("新起终点线与现有维修区通道不再相交。请先重设维修区通道，或重新录入整条赛道。");
                pit = pit with { StartFinishGate = pitStartFinishGate };
            }

            var updated = startFinishEditDefinition with
            {
                StartFinishGate = fittedGate,
                Pit = pit,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            store.SaveTrack(startFinishEditTrack, startFinishEditSectors, updated, clearExistingLaps: true);
            activeDefinition = updated;
            SetState(
                EstateCircuitPhase.Ready,
                "起终点线已更新。",
                "赛道标识保持不变；导出文件的赛道特征值会反映新的起终点线。",
                enrollmentActive: false);
            startFinishEditTrackId = null;
            startFinishEditTrack = null;
            startFinishEditDefinition = null;
            startFinishEditSectors = [];
            enrollment = null;
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
            if (startFinishEditTrackId is not null)
            {
                SetState(EstateCircuitPhase.StartFinishReadyToSave, "新起终点线和比赛方向已确认。",
                    "检查预览后保存；保存前原赛道不会改变。", enrollmentActive: true);
            }
            else
            {
                SetState(EstateCircuitPhase.AwaitingReferenceLap, "终点门和比赛方向已确认。",
                    "开始参考圈录入；首次正向过线开始，下一次正向过线结束。", enrollmentActive: true);
            }
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

    public EstateEnrollmentDraft PauseEnrollmentForDraft()
    {
        lock (stateGate)
        {
            EnsureEnrollment();
            var resumePhase = state.Phase;
            switch (state.Phase)
            {
                case EstateCircuitPhase.CapturingFirstTrace:
                    firstTrace.Clear();
                    resumePhase = EstateCircuitPhase.Idle;
                    break;
                case EstateCircuitPhase.CapturingSecondTrace:
                    secondTrace.Clear();
                    resumePhase = EstateCircuitPhase.Idle;
                    break;
                case EstateCircuitPhase.CapturingDirection:
                    directionTrace.Clear();
                    resumePhase = EstateCircuitPhase.AwaitingDirection;
                    break;
                case EstateCircuitPhase.WaitingForReferenceStart:
                case EstateCircuitPhase.CapturingReferenceLap:
                    ResetLapTracking();
                    routeCapture.Clear();
                    resumePhase = EstateCircuitPhase.AwaitingReferenceLap;
                    break;
                case EstateCircuitPhase.ValidatingLap:
                    ResetLapTracking();
                    resumePhase = EstateCircuitPhase.ValidationFailed;
                    break;
                case EstateCircuitPhase.Ready:
                    throw new InvalidOperationException("录入已经完成，不需要暂存。");
            }

            var draft = new EstateEnrollmentDraft(
                1,
                DateTimeOffset.UtcNow,
                enrollment!,
                resumePhase,
                firstTrace.ToArray(),
                secondTrace.ToArray(),
                directionTrace.ToArray(),
                fittedGate,
                activeTrack,
                activeSectors.ToArray(),
                activeDefinition,
                referenceLapSeconds);
            ResetAll("地产环道录入已暂存。");
            return draft;
        }
    }

    public void ResumeEnrollment(EstateEnrollmentDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (draft.FormatVersion != 1)
            throw new InvalidDataException("暂存文件版本不受支持。");
        var normalized = NormalizeEnrollment(draft.Enrollment);
        lock (stateGate)
        {
            if (state.IsTimingActive || state.IsEnrollmentActive || pitState.IsActive)
                throw new InvalidOperationException("请先结束当前地产流程，再恢复暂存。");
            if (!IsSafeDraftPhase(draft.ResumePhase))
                throw new InvalidDataException("暂存文件中的恢复阶段无效。");
            if (draft.ActiveTrack is not null && draft.ActiveDefinition?.TrackId != draft.ActiveTrack.Id)
                throw new InvalidDataException("暂存文件中的候选路线标识不一致。");

            ResetWorkingState();
            enrollment = normalized;
            firstTrace.AddRange(draft.FirstTrace ?? []);
            secondTrace.AddRange(draft.SecondTrace ?? []);
            directionTrace.AddRange(draft.DirectionTrace ?? []);
            fittedGate = draft.FittedGate;
            activeTrack = draft.ActiveTrack;
            activeSectors = draft.ActiveSectors?.ToArray() ?? [];
            activeDefinition = draft.ActiveDefinition;
            referenceLapSeconds = draft.ReferenceLapSeconds;
            ArmAfterLatestTelemetryFrame();
            SetState(
                draft.ResumePhase,
                $"已恢复 {draft.SavedAt.ToLocalTime():MM-dd HH:mm} 的录入暂存。",
                ResumeInstruction(draft.ResumePhase),
                enrollmentActive: true);
        }
    }

    public void CancelEnrollment()
    {
        lock (stateGate) ResetAll("已取消地产环道录入。");
    }

    public void StartTiming(Guid trackId, bool invalidateLapOnDriverIntervention = true)
    {
        lock (stateGate)
        {
            if (pitState.IsActive)
                throw new InvalidOperationException("请先完成或取消维修区录入。");
            var loaded = store.LoadTrack(trackId) ?? throw new InvalidOperationException("没有找到所选赛道。");
            var storedDefinition = store.LoadEstateTrackDefinition(trackId) ??
                             throw new InvalidOperationException("所选赛道缺少地产计时定义。");
            if (storedDefinition.Pit is { } storedPit &&
                EstatePitGeometryValidation.MaximumSegmentMeters(storedPit.CenterLine) >
                EstatePitGeometryValidation.MaximumPortableSegmentMeters)
                throw new InvalidOperationException(
                    "这条赛道的维修区通道存在大段遥测缺口，维修区起终点门不可信。请只重录“维修区通道”后再开始计时。");
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
            PreparePitTimingGeometry(activeDefinition.Pit);
            activeSectors = loaded.Sectors;
            timingSessionId = Guid.NewGuid();
            invalidateCurrentLapOnDriverIntervention = invalidateLapOnDriverIntervention;
            ReloadTimingHistory();
            completedLaps = 0;
            lastLapSeconds = null;
            previousPosition = null;
            previousPitRouteProjection = null;
            timestampUnwrapper.Reset();
            ArmAfterLatestTelemetryFrame();
            SetState(EstateCircuitPhase.WaitingForTimingStart, "地产环道计时已启用。",
                invalidateLapOnDriverIntervention
                    ? "首次正向过线开始计时；暂停、回转、传送或偏离赛道会使当前圈无效，但计时模式仍保持启用。"
                    : "首次正向过线开始计时；正赛中的暂停、回转和偏离赛道不会取消计圈，比赛总时间仍由服务端连续计算。",
                timingActive: true);
        }
    }

    public void StopTiming()
    {
        lock (stateGate) ResetAll("地产环道计时已停止。");
    }

    public void SetEstateRaceInterventionInvalidation(bool invalidateLapOnDriverIntervention)
    {
        lock (stateGate)
            invalidateCurrentLapOnDriverIntervention = invalidateLapOnDriverIntervention;
    }

    public void PauseTimingForEstateRace()
    {
        lock (stateGate)
        {
            if (activeTrack is null || activeDefinition is null || !state.IsTimingActive) return;
            ResetLapTracking();
            pitTransitActive = false;
            completedLaps = 0;
            lastLapSeconds = null;
            previousPosition = null;
            timestampUnwrapper.Reset();
            ArmAfterLatestTelemetryFrame();
            SetState(
                EstateCircuitPhase.Ready,
                "地产赛事当前未计时。",
                "服务端开始排位赛或正赛后，LazyForza 会自动启用圈速计时。",
                timingActive: false);
        }
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
        if (!state.IsTimingActive && !state.IsEnrollmentActive && !pitState.IsActive)
            return;

        // A manual capture action records the latest published sequence and
        // arrival time. Older frames may still be buffered for this subscriber
        // and must not be reclassified as samples collected after the click.
        if (frame.Sequence <= minimumAcceptedFrameSequence &&
            frame.ArrivalTime <= minimumAcceptedFrameArrival)
            return;

        lastTelemetryArrival = frame.ArrivalTime;

        if (TelemetryContextClassifier.IsDriverIntervention(frame.Raw))
        {
            if (IsLapInProgress() && invalidateCurrentLapOnDriverIntervention)
                AbandonCurrentLap("收到游戏暂停或回转遥测");
            else if (IsLapInProgress())
                ResynchronizeLapAfterDriverIntervention("收到游戏暂停或回转遥测", resetTimestamp: true);
            else
            {
                timestampUnwrapper.Reset();
                lastUnwrappedTimestamp = 0;
                lastHudUpdateTimestamp = long.MinValue;
            }
            previousPosition = null;
            previousPitRouteProjection = null;
            return;
        }

        if (!float.IsFinite(frame.Raw.Position.X) ||
            !float.IsFinite(frame.Raw.Position.Y) ||
            !float.IsFinite(frame.Raw.Position.Z))
        {
            if (IsLapInProgress() && invalidateCurrentLapOnDriverIntervention)
                AbandonCurrentLap("收到无效位置遥测");
            else if (IsLapInProgress())
                ResynchronizeLapAfterDriverIntervention("收到无效位置遥测", resetTimestamp: false);
            previousPosition = null;
            previousPitRouteProjection = null;
            return;
        }
        lastFrame = frame;
        var timestamp = timestampUnwrapper.Unwrap(frame.Raw.TimestampMS);
        if (lastUnwrappedTimestamp > 0 && timestamp <= lastUnwrappedTimestamp)
        {
            var regressionMilliseconds = lastUnwrappedTimestamp - timestamp;
            // Timestamp reorder/reset is transport evidence, not an authoritative
            // pause or rewind signal. Only IsRaceOn may classify an intervention.
            // Ignore isolated regressions so UDP disorder cannot cancel a lap.
            if (regressionMilliseconds > MaximumTimestampReorderMilliseconds)
            {
                LogIfInitialized($"Estate telemetry timestamp regressed by {regressionMilliseconds} ms; frame ignored without invalidating the lap.");
            }
            return;
        }
        lastUnwrappedTimestamp = timestamp;
        var position = new EstateTimedPosition(
            frame.Raw.Position.X,
            frame.Raw.Position.Y,
            frame.Raw.Position.Z,
            frame.Raw.Speed,
            timestamp);
        if (previousPosition is null) previousPitRouteProjection = null;
        ObservePitEnrollment(frame, position);
        if (previousPosition is EstateTimedPosition previous &&
            IsLapInProgress() &&
            IsImplausiblePositionJump(previous, position))
        {
            if (invalidateCurrentLapOnDriverIntervention)
                AbandonCurrentLap("检测到位置跳变或使用回转");
            else
                ResynchronizeLapAfterDriverIntervention("位置跳变或使用回转", resetTimestamp: false);
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
                      TryDetectFinishCrossing(gate, previous, position, out crossing) &&
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
            var elapsed = CurrentLapElapsedSeconds(position.TimestampMilliseconds, frame.ArrivalTime);
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
            var elapsed = CurrentLapElapsedSeconds(position.TimestampMilliseconds, frame.ArrivalTime);
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
        referenceLapSeconds = CurrentLapElapsedSeconds(crossing.TimestampMilliseconds, frame.ArrivalTime);
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
        var total = CurrentLapElapsedSeconds(crossing.TimestampMilliseconds, frame.ArrivalTime);
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
        var total = CurrentLapElapsedSeconds(crossing.TimestampMilliseconds, frame.ArrivalTime);
        var ratio = ProjectionRatio();
        var geometryValid = !trackDeviationDetected && ratio >= 0.95 &&
                            nextCheckpoint == activeDefinition.Checkpoints.Count &&
                            maximumProgress >= activeTrack.LengthMeters * 0.85;
        var classificationValid = !invalidateCurrentLapOnDriverIntervention || geometryValid;
        var invalidReason = geometryValid ? null :
            trackDeviationDetected ? "estate-track-deviation" :
            nextCheckpoint != activeDefinition.Checkpoints.Count ? "estate-checkpoints-incomplete" :
            maximumProgress < activeTrack.LengthMeters * 0.85 ? "estate-route-progress-incomplete" :
            $"estate-projection-low-confidence ({ratio:P0})";
        EnsureComparisonReference(frame);
        // Capture the comparison target before this lap is inserted. Once the
        // new fastest lap enters timingHistory it must drive the next lap's live
        // comparison, but the finish-line result still belongs against the
        // fastest lap that existed when this lap started/finished.
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
            geometryValid,
            invalidReason,
            BuildSegments(total, samples),
            samples,
            PlayerIdentitySettings.Normalize(playerCodeProvider()));
        store.SaveLap(lap);
        lastCompletedLap = new EstateCircuitCompletedLap(
            lap.Id,
            completedLaps + 1,
            lap.TotalSeconds,
            lap.Segments.OrderBy(segment => segment.Index).Select(segment => segment.TimeSeconds).ToArray(),
            classificationValid,
            lap.InvalidReason,
            geometryValid);
        // Build the held finish-line sectors while timingHistory still excludes
        // the just-completed lap. Including it here makes every new fastest lap
        // compare against itself and incorrectly produces an all-zero Delta.
        heldComparisons = BuildCompletedComparisons(lap, previousHistoricalReference);
        ReloadTimingHistory();
        heldComparisonsUntil = frame.ArrivalTime +
                               LapHudDisplayTiming.CompletedLapHoldDuration(getOverlayLayout());
        heldCumulativeHistoricalDeltaSeconds = geometryValid && previousHistoricalReference is not null
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
            geometryValid
                ? $"完成有效圈：{total:0.000} s。"
                : classificationValid
                    ? $"已完成计圈，但本圈不计入最快圈：{invalidReason}。"
                    : $"本圈无效：{invalidReason}。",
            "已从本次过线开始下一圈。", timingActive: true);
    }

    private bool ObserveProjectedLap(TelemetryFrame frame, EstateTimedPosition position, bool finishCrossed)
    {
        if (activeTrack is null || activeDefinition is null) return false;
        // The recorded pit lane is a legal alternate route. Its samples are not
        // projected onto the racing line: doing that would falsely look like a
        // route deviation and invalidate every pit lap. Timing still advances,
        // and the pit finish gate may end/start a lap while the car is in lane.
        if (pitTransitActive) return true;
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
                if (!wrappedAtFinish && delta > 0 &&
                    previousPosition is EstateTimedPosition previousPositionValue &&
                    IsImplausibleForwardProgressJump(delta, previousPositionValue, position))
                {
                    trackDeviationDetected = true;
                    if (invalidateCurrentLapOnDriverIntervention)
                    {
                        AbandonCurrentLap("检测到跨越赛道大段路线的切弯");
                        return false;
                    }
                    LogIfInitialized($"Estate race shortcut retained for race timing: forward jump={delta:0.0}m.");
                }
                if (!wrappedAtFinish && delta < -0.25)
                    accumulatedReverseProgress += -delta;
                else if (delta > 0.5)
                    accumulatedReverseProgress = Math.Max(0, accumulatedReverseProgress - delta);
                if (accumulatedReverseProgress >= MaximumReverseProgressMeters)
                {
                    if (invalidateCurrentLapOnDriverIntervention)
                    {
                        AbandonCurrentLap("检测到车辆沿赛道明显回退或使用回转");
                        return false;
                    }
                    ResynchronizeLapAfterDriverIntervention("车辆沿赛道明显回退或使用回转", resetTimestamp: false);
                }
            }
            previousProjectedProgress = projection.S;
            projectionIndex = projection.SegmentIndex;
            validProjectionSamples++;
            invalidProjectionStartedAt = null;
            maximumProgress = Math.Max(maximumProgress, projection.S);
            var elapsed = CurrentLapElapsedSeconds(position.TimestampMilliseconds, frame.ArrivalTime);
            lapSamples.Add(new LapSample(
                projection.S, elapsed, frame.Raw.Speed, frame.Raw.CurrentEngineRpm, frame.Raw.Gear,
                frame.Normalized.AccelRatio, frame.Normalized.BrakeRatio, 0,
                position.X, position.Y, position.Z,
                new LapDynamics(frame.Raw.Steer / 127d, frame.Raw.TireSlipRatio, frame.Raw.TireSlipAngle, frame.Raw.TireCombinedSlip)));
        }
        else
        {
            invalidProjectionSamples++;
            if (invalidateCurrentLapOnDriverIntervention)
            {
                invalidProjectionStartedAt ??= frame.ArrivalTime;
                if (frame.ArrivalTime - invalidProjectionStartedAt.Value >= TimeSpan.FromMilliseconds(250))
                    trackDeviationDetected = true;
            }
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

    private bool TryDetectFinishCrossing(
        EstateTimingGate mainGate,
        EstateTimedPosition previous,
        EstateTimedPosition current,
        out EstateGateCrossing crossing)
    {
        var pit = activeDefinition?.Pit;
        var pitWasActiveAtFinish = ObservePitTransit(pit, previous, current);
        var crossedMain = EstateTrackAlgorithms.TryDetectForwardCrossing(
            mainGate,
            previous,
            current,
            out var mainCrossing);
        EstateGateCrossing pitCrossing = default;
        var crossedPit = pitWasActiveAtFinish && pit?.StartFinishGate is EstateTimingGate pitGate &&
                         EstateTrackAlgorithms.TryDetectForwardCrossing(
                             pitGate,
                             previous,
                             current,
                             out pitCrossing);

        if (!crossedMain && !crossedPit)
        {
            crossing = default;
            return false;
        }

        crossing = crossedMain && crossedPit
            ? (mainCrossing.TimestampMilliseconds <= pitCrossing.TimestampMilliseconds ? mainCrossing : pitCrossing)
            : crossedMain ? mainCrossing : pitCrossing;
        return true;
    }

    private bool ObservePitTransit(
        EstatePitDefinition? pit,
        EstateTimedPosition previous,
        EstateTimedPosition current)
    {
        if (pit is null)
        {
            pitTransitActive = false;
            previousPitRouteProjection = null;
            return false;
        }

        var previousRoute = previousPitRouteProjection ??
                            ProjectPitRoute(pit.CenterLine, previous.X, previous.Y, previous.Z);
        var currentRoute = ProjectPitRoute(pit.CenterLine, current.X, current.Y, current.Z);
        previousPitRouteProjection = currentRoute;
        var entryProgress = activePitEntryProgressMeters ??= PitGateProgress(pit.CenterLine, pit.EntryGate);
        var exitProgress = activePitExitProgressMeters ??= PitGateProgress(pit.CenterLine, pit.ExitGate);
        var corridorWidth = Math.Clamp(pit.LaneHalfWidthMeters, 1, 20) * 1.35 + 0.75;
        var enteredByProgress = previousRoute.DistanceMeters <= corridorWidth &&
                                currentRoute.DistanceMeters <= corridorWidth &&
                                previousRoute.ProgressMeters < entryProgress - 0.25 &&
                                currentRoute.ProgressMeters >= entryProgress - 0.25;
        var recoveredInsidePit = currentRoute.DistanceMeters <= corridorWidth &&
                                 currentRoute.ProgressMeters > entryProgress + 0.75 &&
                                 currentRoute.ProgressMeters < currentRoute.TotalLengthMeters - 0.75;
        if (!pitTransitActive &&
            (EstateTrackAlgorithms.TryDetectForwardCrossing(
                 pit.EntryGate,
                 previous,
                 current,
                 out _) ||
             enteredByProgress || recoveredInsidePit))
        {
            pitTransitActive = true;
            LogIfInitialized("Estate pit transit entered.");
        }

        var activeAtFinish = pitTransitActive;
        var transitEndProgress = Math.Max(exitProgress, currentRoute.TotalLengthMeters - 0.75);
        var exitedByProgress = previousRoute.DistanceMeters <= corridorWidth &&
                               currentRoute.DistanceMeters <= corridorWidth &&
                               previousRoute.ProgressMeters < transitEndProgress &&
                               currentRoute.ProgressMeters >= transitEndProgress;
        if (pitTransitActive &&
            (exitedByProgress ||
             currentRoute.DistanceMeters <= corridorWidth &&
             currentRoute.ProgressMeters >= transitEndProgress))
        {
            pitTransitActive = false;
            // The next racing-line projection must not be compared with the
            // stale progress that preceded the pit split. Re-arm continuity at
            // the actual pit exit instead.
            previousProjectedProgress = null;
            projectionIndex = 0;
            accumulatedReverseProgress = 0;
            invalidProjectionStartedAt = null;
            LogIfInitialized("Estate pit transit exited.");
        }
        return activeAtFinish;
    }

    private static PitRouteProjection ProjectPitRoute(
        IReadOnlyList<EstateGatePoint> line,
        double x,
        double y,
        double z)
    {
        var bestDistance = double.PositiveInfinity;
        var bestProgress = 0d;
        var progress = 0d;
        for (var index = 0; index < line.Count - 1; index++)
        {
            var start = line[index];
            var end = line[index + 1];
            var dx = end.X - start.X;
            var dy = end.Y - start.Y;
            var dz = end.Z - start.Z;
            var lengthSquared = dx * dx + dy * dy + dz * dz;
            if (lengthSquared < 0.0001) continue;
            var length = Math.Sqrt(lengthSquared);
            var amount = Math.Clamp(
                ((x - start.X) * dx + (y - start.Y) * dy + (z - start.Z) * dz) /
                lengthSquared,
                0,
                1);
            var distance = Math.Sqrt(
                Math.Pow(x - (start.X + dx * amount), 2) +
                Math.Pow(y - (start.Y + dy * amount), 2) +
                Math.Pow(z - (start.Z + dz * amount), 2));
            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestProgress = progress + length * amount;
            }
            progress += length;
        }
        return new PitRouteProjection(bestDistance, bestProgress, progress);
    }

    private static double PitGateProgress(
        IReadOnlyList<EstateGatePoint> line,
        EstateTimingGate gate) =>
        ProjectPitRoute(
            line,
            (gate.Left.X + gate.Right.X) / 2,
            (gate.Left.Y + gate.Right.Y) / 2,
            (gate.Left.Z + gate.Right.Z) / 2).ProgressMeters;

    private void PreparePitTimingGeometry(EstatePitDefinition? pit)
    {
        previousPitRouteProjection = null;
        activePitEntryProgressMeters = pit is null ? null : PitGateProgress(pit.CenterLine, pit.EntryGate);
        activePitExitProgressMeters = pit is null ? null : PitGateProgress(pit.CenterLine, pit.ExitGate);
    }

    private void BeginLap(long timestamp, DateTimeOffset arrivalTime)
    {
        lapStartTimestamp = timestamp;
        lapStartedAt = arrivalTime;
        ResetLapTracking();
        ResetLiveCumulativeHistoricalDelta();
    }

    private double CurrentLapElapsedSeconds(long timestamp, DateTimeOffset arrivalTime) =>
        invalidateCurrentLapOnDriverIntervention
            ? Math.Max(0, (timestamp - lapStartTimestamp) / 1000d)
            : Math.Max(0, (arrivalTime - lapStartedAt).TotalSeconds);

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
        invalidProjectionStartedAt = null;
        trackDeviationDetected = false;
    }

    private bool IsLapInProgress() => state.Phase is
        EstateCircuitPhase.CapturingReferenceLap or
        EstateCircuitPhase.ValidatingLap or
        EstateCircuitPhase.TimingLap;

    private void AbandonCurrentLap(string reason)
    {
        var interruptedPhase = state.Phase;
        if (!IsLapInProgress()) return;

        if (interruptedPhase == EstateCircuitPhase.TimingLap)
        {
            var elapsed = CurrentLapElapsedSeconds(
                lastUnwrappedTimestamp,
                lastTelemetryArrival ?? DateTimeOffset.UtcNow);
            lastCompletedLap = new EstateCircuitCompletedLap(
                Guid.NewGuid(),
                completedLaps + 1,
                elapsed,
                [],
                false,
                $"estate-driver-intervention: {reason}");
        }

        if (interruptedPhase == EstateCircuitPhase.CapturingReferenceLap)
            routeCapture.Clear();

        ResetLapTracking();
        previousPosition = null;
        previousPitRouteProjection = null;
        pitTransitActive = false;
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

    private void ResynchronizeLapAfterDriverIntervention(string reason, bool resetTimestamp)
    {
        previousPosition = null;
        previousPitRouteProjection = null;
        previousProjectedProgress = null;
        accumulatedReverseProgress = 0;
        invalidProjectionStartedAt = null;
        if (resetTimestamp)
        {
            timestampUnwrapper.Reset();
            lastUnwrappedTimestamp = 0;
            lastHudUpdateTimestamp = long.MinValue;
        }
        LogIfInitialized($"Estate race lap retained after driver intervention: {reason}.");
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

    private static bool IsImplausibleForwardProgressJump(
        double forwardProgressMeters,
        EstateTimedPosition previous,
        EstateTimedPosition current)
    {
        var elapsedSeconds = (current.TimestampMilliseconds - previous.TimestampMilliseconds) / 1000d;
        if (elapsedSeconds is <= 0 or > 2) return false;
        var reportedSpeed = Math.Max(previous.SpeedMetersPerSecond, current.SpeedMetersPerSecond);
        var plausibleProgressMeters = Math.Max(60, reportedSpeed * elapsedSeconds * 3 + 30);
        return forwardProgressMeters > plausibleProgressMeters;
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

    private IReadOnlyList<SectorComparison> BuildCompletedComparisons(
        LapRecord lap,
        LapRecord? previousFastestLap)
    {
        return activeSectors.Select(sector =>
        {
            var segment = lap.Segments.FirstOrDefault(candidate => candidate.Index == sector.Index);
            var current = segment is { IsValid: true, TimeSeconds: > 0 }
                ? segment.TimeSeconds
                : (double?)null;
            var (sessionBest, historicalBest) = BestTimingSectorTimes(sector.Index, lap.Vehicle.CarClass);
            var previousFastestSegment = previousFastestLap?.Segments
                .FirstOrDefault(candidate => candidate.Index == sector.Index);
            var deltaReference = previousFastestSegment is { IsValid: true, TimeSeconds: > 0 }
                ? previousFastestSegment.TimeSeconds
                : (double?)null;
            if (lap.IsValid && current is not null)
            {
                sessionBest = sessionBest is null ? current : Math.Min(sessionBest.Value, current.Value);
                historicalBest = historicalBest is null ? current : Math.Min(historicalBest.Value, current.Value);
            }
            var state = SectorColorClassifier.Classify(current, lap.IsValid && current is not null, sessionBest, historicalBest);
            return new SectorComparison(
                sector.Index,
                current,
                sessionBest,
                historicalBest,
                current is not null && deltaReference is not null ? current - deltaReference : null,
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
        pitTransitActive = false;
        previousPitRouteProjection = null;
        activePitEntryProgressMeters = null;
        activePitExitProgressMeters = null;
        activeSectors = [];
        previousPosition = null;
        lastCrossingTimestamp = long.MinValue;
        lastUnwrappedTimestamp = 0;
        lastHudUpdateTimestamp = long.MinValue;
        referenceLapSeconds = 0;
        completedLaps = 0;
        lastLapSeconds = null;
        lastCompletedLap = null;
        timingHistory.Clear();
        timingSessionId = Guid.Empty;
        invalidateCurrentLapOnDriverIntervention = true;
        lastTelemetryArrival = null;
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
        startFinishEditTrackId = null;
        startFinishEditTrack = null;
        startFinishEditDefinition = null;
        startFinishEditSectors = [];
    }

    private void ObservePitEnrollment(TelemetryFrame frame, EstateTimedPosition position)
    {
        var point = new EstateGatePoint(position.X, position.Y, position.Z);
        recentPitSamples.Enqueue((frame.ArrivalTime, point, frame.Normalized.SpeedKph));
        var cutoff = frame.ArrivalTime - TimeSpan.FromSeconds(2);
        while (recentPitSamples.TryPeek(out var sample) && sample.Time < cutoff)
            recentPitSamples.Dequeue();
        if (pitState.Phase != EstatePitCapturePhase.CapturingLane || frame.Normalized.SpeedKph is < 1.5 or > 35)
            return;
        if (pitLaneCapture.Count == 0 || DistanceSquared(pitLaneCapture[^1], point) >= 0.35 * 0.35)
            pitLaneCapture.Add(point);
        if (pitLaneCapture.Count % 5 == 0)
            SetPitState(EstatePitCapturePhase.CapturingLane, pitEnrollment?.TrackId, pitState.TrackName,
                $"正在录入维修区通道：{pitLaneCapture.Count} 个样本。",
                "沿通道中心继续驶到出口后，停车并结束录入。", true);
    }

    private void SetPitState(
        EstatePitCapturePhase phase,
        Guid? trackId,
        string? trackName,
        string statusText,
        string instruction,
        bool active) =>
        Volatile.Write(ref pitState, new EstatePitEnrollmentState(
            DateTimeOffset.UtcNow,
            phase,
            trackId,
            trackName,
            pitLaneCapture.Count,
            pitServiceCorners.Count,
            pitEntryGate is not null,
            pitExitGate is not null,
            statusText,
            instruction,
            active));

    private void EnsurePitEnrollment()
    {
        if (pitEnrollment is null)
            throw new InvalidOperationException("请先选择地产环道并开始维修区录入。");
    }

    private EstateGatePoint StablePitPosition(string failureMessage)
    {
        var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(1.2);
        var recent = recentPitSamples
            .Where(sample => sample.Time >= cutoff && sample.SpeedKph <= 5)
            .ToArray();
        if (recent.Length == 0) throw new InvalidOperationException(failureMessage);
        var latest = recent[^1].Point;
        var samples = recent
            .Reverse()
            .TakeWhile(sample => DistanceSquared(sample.Point, latest) <= 0.5 * 0.5)
            .Select(sample => sample.Point)
            .ToArray();
        if (samples.Length < 5) throw new InvalidOperationException(failureMessage);
        return new EstateGatePoint(
            samples.Average(sample => sample.X),
            samples.Average(sample => sample.Y),
            samples.Average(sample => sample.Z));
    }

    private static (EstateTimingGate Gate, double ProgressMeters) CreatePitGateAtPoint(
        IReadOnlyList<EstateGatePoint> centerLine,
        EstateGatePoint requested,
        double halfWidth)
    {
        var bestDistanceSquared = double.MaxValue;
        var bestIndex = -1;
        var bestAmount = 0d;
        var progressBeforeSegment = 0d;
        var bestProgress = 0d;
        for (var index = 0; index < centerLine.Count - 1; index++)
        {
            var start = centerLine[index];
            var end = centerLine[index + 1];
            var dx = end.X - start.X;
            var dz = end.Z - start.Z;
            var lengthSquared = dx * dx + dz * dz;
            if (lengthSquared < 0.0001) continue;
            var amount = Math.Clamp(
                ((requested.X - start.X) * dx + (requested.Z - start.Z) * dz) / lengthSquared,
                0,
                1);
            var projectedX = start.X + dx * amount;
            var projectedZ = start.Z + dz * amount;
            var distanceSquared = Math.Pow(requested.X - projectedX, 2) + Math.Pow(requested.Z - projectedZ, 2);
            var segmentLength = Math.Sqrt(lengthSquared);
            if (distanceSquared < bestDistanceSquared)
            {
                bestDistanceSquared = distanceSquared;
                bestIndex = index;
                bestAmount = amount;
                bestProgress = progressBeforeSegment + segmentLength * amount;
            }
            progressBeforeSegment += segmentLength;
        }
        if (bestIndex < 0 || Math.Sqrt(bestDistanceSquared) > Math.Max(halfWidth + 2, 5))
            throw new InvalidOperationException("车辆不在已录入的维修区通道附近，请停到通道中心线后重试。");

        var segmentStart = centerLine[bestIndex];
        var segmentEnd = centerLine[bestIndex + 1];
        var center = new EstateGatePoint(
            segmentStart.X + (segmentEnd.X - segmentStart.X) * bestAmount,
            segmentStart.Y + (segmentEnd.Y - segmentStart.Y) * bestAmount,
            segmentStart.Z + (segmentEnd.Z - segmentStart.Z) * bestAmount);
        var tangentStart = centerLine[Math.Max(0, bestIndex - 2)];
        var tangentEnd = centerLine[Math.Min(centerLine.Count - 1, bestIndex + 3)];
        var dxLocal = tangentEnd.X - tangentStart.X;
        var dzLocal = tangentEnd.Z - tangentStart.Z;
        var length = Math.Sqrt(dxLocal * dxLocal + dzLocal * dzLocal);
        if (length < 0.25)
        {
            dxLocal = segmentEnd.X - segmentStart.X;
            dzLocal = segmentEnd.Z - segmentStart.Z;
            length = Math.Sqrt(dxLocal * dxLocal + dzLocal * dzLocal);
        }
        if (length < 0.1) throw new InvalidOperationException("维修区入口或出口方向样本不足。");
        dxLocal /= length;
        dzLocal /= length;
        var perpendicularX = -dzLocal * halfWidth;
        var perpendicularZ = dxLocal * halfWidth;
        return (new EstateTimingGate(
            new EstateGatePoint(center.X + perpendicularX, center.Y, center.Z + perpendicularZ),
            new EstateGatePoint(center.X - perpendicularX, center.Y, center.Z - perpendicularZ),
            dxLocal,
            dzLocal,
            0,
            0,
            0,
            3,
            0.75), bestProgress);
    }

    private static IReadOnlyList<EstateGatePoint> ResamplePitLane(
        IReadOnlyList<EstateGatePoint> source,
        double spacing)
    {
        var result = new List<EstateGatePoint> { source[0] };
        var distanceSinceLast = 0d;
        for (var index = 1; index < source.Count; index++)
        {
            distanceSinceLast += Math.Sqrt(DistanceSquared(source[index - 1], source[index]));
            if (distanceSinceLast < spacing && index != source.Count - 1) continue;
            result.Add(source[index]);
            distanceSinceLast = 0;
        }
        return result;
    }

    private static double PolylineLength(IReadOnlyList<EstateGatePoint> points)
    {
        var total = 0d;
        for (var index = 1; index < points.Count; index++)
            total += Math.Sqrt(DistanceSquared(points[index - 1], points[index]));
        return total;
    }

    private static double PolygonArea(IReadOnlyList<EstateGatePoint> points)
    {
        if (points.Count < 3) return 0;
        var area = 0d;
        for (var index = 0; index < points.Count; index++)
        {
            var next = points[(index + 1) % points.Count];
            area += points[index].X * next.Z - next.X * points[index].Z;
        }
        return Math.Abs(area) * 0.5;
    }

    private static EstatePitEnrollmentState EmptyPitState() => new(
        DateTimeOffset.UtcNow,
        EstatePitCapturePhase.Idle,
        null,
        null,
        0,
        0,
        false,
        false,
        "未开始维修区录入。",
        "在地产赛道列表中选择“配置维修区”。",
        false);

    private void ArmAfterLatestTelemetryFrame()
    {
        var latest = Context.Telemetry.Latest;
        minimumAcceptedFrameSequence = latest?.Sequence ?? long.MinValue;
        minimumAcceptedFrameArrival = latest?.ArrivalTime ?? DateTimeOffset.MinValue;
        previousPosition = null;
        previousPitRouteProjection = null;
    }

    private void ResetLiveCumulativeHistoricalDelta()
    {
        liveCumulativeHistoricalDeltaSeconds = null;
        liveCumulativeHistoricalDeltaUntil = DateTimeOffset.MinValue;
        liveCumulativeHistoricalDeltaSector = -1;
    }

    private readonly record struct PitRouteProjection(
        double DistanceMeters,
        double ProgressMeters,
        double TotalLengthMeters);

    private void ClearCumulativeHistoricalDeltaDisplay()
    {
        heldCumulativeHistoricalDeltaSeconds = null;
        heldCumulativeHistoricalDeltaUntil = DateTimeOffset.MinValue;
        ResetLiveCumulativeHistoricalDelta();
    }

    private void ResetAll(string statusText)
    {
        ResetWorkingState();
        pitEnrollment = null;
        pitLaneCapture.Clear();
        pitServiceCorners.Clear();
        pitEntryGate = null;
        pitExitGate = null;
        pitEntryProgressMeters = null;
        pitExitProgressMeters = null;
        originalPitDefinition = null;
        pitEditScope = EstatePitEditScope.All;
        recentPitSamples.Clear();
        Volatile.Write(ref pitState, EmptyPitState());
        Volatile.Write(ref hudSnapshot, null);
        Volatile.Write(ref state, EmptyState() with { UpdatedAt = DateTimeOffset.UtcNow, Status = statusText });
    }

    private void EnsureEnrollment()
    {
        if (enrollment is null) throw new InvalidOperationException("请先填写地图信息并开始录入。");
    }

    private static EstateEnrollmentRequest NormalizeEnrollment(EstateEnrollmentRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.MapName))
            throw new ArgumentException("请填写地产地图名称。", nameof(request));
        if (request.MapName.Trim().Length > 80)
            throw new ArgumentException("地产地图名称不能超过 80 个字符。", nameof(request));
        if (request.SectorCount is < TrackAlgorithms.MinimumSectorCount or > TrackAlgorithms.MaximumSectorCount)
            throw new ArgumentOutOfRangeException(
                nameof(request),
                $"分段数必须在 {TrackAlgorithms.MinimumSectorCount} 到 {TrackAlgorithms.MaximumSectorCount} 之间。");
        var revision = string.IsNullOrWhiteSpace(request.MapRevision) ? "1" : request.MapRevision.Trim();
        if (revision.Length > 32)
            throw new ArgumentException("地图修订号不能超过 32 个字符。", nameof(request));
        var creator = NullIfWhiteSpace(request.Creator);
        var shareCode = NullIfWhiteSpace(request.ShareCode);
        if (creator?.Length > 80) throw new ArgumentException("作者名称不能超过 80 个字符。", nameof(request));
        if (shareCode?.Length > 80) throw new ArgumentException("分享代码或地图标识不能超过 80 个字符。", nameof(request));
        return request with
        {
            MapName = request.MapName.Trim(),
            Creator = creator,
            ShareCode = shareCode,
            MapRevision = revision
        };
    }

    private static bool IsSafeDraftPhase(EstateCircuitPhase phase) => phase is
        EstateCircuitPhase.Idle or
        EstateCircuitPhase.AwaitingDirection or
        EstateCircuitPhase.AwaitingReferenceLap or
        EstateCircuitPhase.ValidationFailed;

    private static string ResumeInstruction(EstateCircuitPhase phase) => phase switch
    {
        EstateCircuitPhase.Idle => "继续终点线描摹。正在采集但未完成的单次描摹已丢弃。",
        EstateCircuitPhase.AwaitingDirection => "终点线已保留；继续采集正常比赛方向。",
        EstateCircuitPhase.AwaitingReferenceLap => "终点线和比赛方向已保留；重新开始完整参考圈。",
        EstateCircuitPhase.ValidationFailed => "参考路线已保留；重新开始完整验证圈。",
        _ => "继续完成当前录入。"
    };

    private EstatePitCapturePhase PitCapturePhaseForCurrent()
    {
        if (pitLaneCapture.Count < 2) return EstatePitCapturePhase.Idle;
        var serviceReady = pitEditScope.HasFlag(EstatePitEditScope.ServiceZone)
            ? pitServiceCorners.Count >= 4 && PolygonArea(pitServiceCorners) >= 4
            : originalPitDefinition is not null;
        return pitEntryGate is not null && pitExitGate is not null && serviceReady
            ? EstatePitCapturePhase.ReadyToSave
            : EstatePitCapturePhase.AwaitingServiceCorners;
    }

    private void RefreshPitGateProgress()
    {
        pitEntryProgressMeters = pitEntryGate is null
            ? null
            : CreatePitGateAtPoint(
                pitLaneCapture,
                GateCenter(pitEntryGate),
                pitEnrollment!.LaneHalfWidthMeters).ProgressMeters;
        pitExitProgressMeters = pitExitGate is null
            ? null
            : CreatePitGateAtPoint(
                pitLaneCapture,
                GateCenter(pitExitGate),
                pitEnrollment!.LaneHalfWidthMeters).ProgressMeters;
    }

    private static EstateGatePoint GateCenter(EstateTimingGate gate) => new(
        (gate.Left.X + gate.Right.X) / 2,
        (gate.Left.Y + gate.Right.Y) / 2,
        (gate.Left.Z + gate.Right.Z) / 2);

    private static string PitScopeText(EstatePitEditScope scope)
    {
        var parts = new List<string>();
        if (scope.HasFlag(EstatePitEditScope.Lane)) parts.Add("通道");
        if (scope.HasFlag(EstatePitEditScope.EntryGate)) parts.Add("入口线");
        if (scope.HasFlag(EstatePitEditScope.ExitGate)) parts.Add("出口线");
        if (scope.HasFlag(EstatePitEditScope.ServiceZone)) parts.Add("换胎区");
        if (scope.HasFlag(EstatePitEditScope.Settings)) parts.Add("规则参数");
        return parts.Count == 0 ? "规则参数" : string.Join("、", parts);
    }

    private string NextPitInstruction(EstatePitEditScope scope)
    {
        if (scope.HasFlag(EstatePitEditScope.Lane) && pitLaneCapture.Count < 2)
            return "按比赛方向，从分流点前开始完整录入通道，到并道点后再结束。";
        if (scope.HasFlag(EstatePitEditScope.EntryGate) && pitEntryGate is null)
            return "把车停在入口线中心约 1 秒，然后确认入口线。";
        if (scope.HasFlag(EstatePitEditScope.ExitGate) && pitExitGate is null)
            return "把车停在出口线中心约 1 秒，然后确认出口线。";
        if (scope.HasFlag(EstatePitEditScope.ServiceZone) &&
            (pitServiceCorners.Count < 4 || PolygonArea(pitServiceCorners) < 4))
            return "按同一绕行方向记录换胎区边界，至少 4 个点。";
        return "所选项目已完成，可以保存。未选择的维修区数据会原样保留。";
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
