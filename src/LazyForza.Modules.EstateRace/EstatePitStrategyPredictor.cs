using System.Diagnostics;
using LazyForza.Domain;

namespace LazyForza.Modules.EstateRace;

/// <summary>
/// Builds a local, explainable one-stop forecast from race snapshots. It never
/// treats FH6 telemetry as a tire-wear sensor: lap-time trend and grip evidence
/// are kept separate, and disrupted laps are excluded before fitting pace.
/// </summary>
internal sealed class EstatePitStrategyPredictor
{
    private const int MinimumCleanLaps = 3;
    private const int MaximumRetainedLaps = 40;
    private const int MaximumRetainedPitVisits = 16;
    private static readonly long PredictionRefreshIntervalTicks = Stopwatch.Frequency;
    private readonly Dictionary<Guid, ParticipantObservation> observations = [];
    private readonly List<LapObservation> raceLaps = [];
    private readonly List<PitVisitObservation> pitVisits = [];
    private readonly List<EstateStrategySample> historicalSamples = [];
    private readonly Queue<EstateStrategySample> pendingSamples = new();
    private readonly Dictionary<int, Guid> raceStintSampleIds = [];
    private string? trackKey;
    private string? historicalTrackKey;
    private RaceSessionPhase? previousPhase;
    private bool previousWasRaceContext;
    private Guid? activeLocalParticipantId;
    private Guid raceRunId = Guid.NewGuid();
    private PredictionRefreshKey? lastPredictionRefreshKey;
    private long nextPredictionRefreshTimestamp;

    public EstatePitStrategyPrediction Current { get; private set; } = Unavailable("尚未进入正赛。 ");

    public EstatePitStrategyPrediction Observe(
        EstateRaceSession? session,
        Guid? localParticipantId,
        EstateRaceTrackContext? context,
        RaceGripCondition localGrip,
        bool isObserver,
        VehicleProfileFingerprint? vehicle = null)
    {
        if (session is null)
        {
            Current = Unavailable("尚未收到赛事数据。 ");
            return Current;
        }

        var nextTrackKey = TrackKey(session, context);
        if (!string.Equals(trackKey, nextTrackKey, StringComparison.Ordinal))
        {
            var retainedHistory = string.Equals(historicalTrackKey, nextTrackKey, StringComparison.Ordinal)
                ? historicalSamples.ToArray()
                : [];
            Reset();
            trackKey = nextTrackKey;
            if (retainedHistory.Length > 0)
            {
                historicalSamples.AddRange(retainedHistory);
                historicalTrackKey = nextTrackKey;
            }
        }

        var raceContext = IsRaceContext(session);
        if (previousPhase != session.Phase)
        {
            observations.Clear();
            lastPredictionRefreshKey = null;
        }
        if (raceContext && !previousWasRaceContext)
        {
            raceLaps.Clear();
            raceStintSampleIds.Clear();
            raceRunId = Guid.NewGuid();
            activeLocalParticipantId = localParticipantId;
        }
        previousPhase = session.Phase;
        previousWasRaceContext = raceContext;

        ObserveParticipants(session, localParticipantId, context, localGrip, vehicle);

        if (isObserver)
        {
            Current = Unavailable("OB 只接收转播数据，不生成本机进站建议。 ");
            return Current;
        }
        if (session.Phase == RaceSessionPhase.Finished)
        {
            Current = CreateTerminalPrediction(
                EstatePitStrategyDecision.Finished,
                "比赛已经结束",
                "本场不再计算新的进站窗口。 ");
            return Current;
        }
        if (session.Phase == RaceSessionPhase.Suspended &&
            session.SuspendedFromPhase == RaceSessionPhase.Race)
        {
            Current = Unavailable("正赛处于红旗暂停；恢复比赛后会沿用暂停前的代表圈继续计算。 ");
            return Current;
        }
        if (session.Phase != RaceSessionPhase.Race)
        {
            Current = BuildDataStatus(session, vehicle);
            return Current;
        }
        if (activeLocalParticipantId != localParticipantId)
        {
            raceLaps.Clear();
            activeLocalParticipantId = localParticipantId;
        }
        if (localParticipantId is not Guid localId ||
            session.Participants.FirstOrDefault(candidate => candidate.Id == localId) is not { } local)
        {
            Current = Unavailable("没有找到本机参赛车手，暂时无法预测。 ");
            return Current;
        }
        if (context?.Definition.Pit is null)
        {
            Current = Unavailable("当前地产环道没有完整的维修区定义，无法估算进站损失。 ");
            return Current;
        }

        var refreshKey = new PredictionRefreshKey(
            session.Flag,
            session.TotalRaceLaps,
            session.MinimumRequiredPitStops,
            local.Status,
            local.CompletedLaps,
            local.CompletedPitServices,
            local.IsInPitLane,
            raceLaps.Count,
            pitVisits.Count,
            historicalSamples.Count);
        var now = Stopwatch.GetTimestamp();
        if (refreshKey == lastPredictionRefreshKey && now < nextPredictionRefreshTimestamp)
        {
            return Current;
        }

        lastPredictionRefreshKey = refreshKey;
        nextPredictionRefreshTimestamp = now + PredictionRefreshIntervalTicks;
        Current = AttachPitRequirement(BuildPrediction(session, local, context, vehicle), session, local);
        return Current;
    }

    public void SetHistoricalSamples(IEnumerable<EstateStrategySample> samples)
    {
        var values = samples
            .OrderByDescending(sample => sample.CapturedAt)
            .DistinctBy(sample => sample.Id)
            .Take(192)
            .ToArray();
        historicalSamples.Clear();
        historicalSamples.AddRange(values);
        historicalTrackKey = values.FirstOrDefault()?.Track.Key;
        lastPredictionRefreshKey = null;
    }

    public IReadOnlyList<EstateStrategySample> DrainSamples()
    {
        var result = new List<EstateStrategySample>(pendingSamples.Count);
        while (pendingSamples.TryDequeue(out var sample)) result.Add(sample);
        return result;
    }

    public void Reset()
    {
        observations.Clear();
        raceLaps.Clear();
        pitVisits.Clear();
        historicalSamples.Clear();
        pendingSamples.Clear();
        raceStintSampleIds.Clear();
        trackKey = null;
        historicalTrackKey = null;
        previousPhase = null;
        previousWasRaceContext = false;
        activeLocalParticipantId = null;
        lastPredictionRefreshKey = null;
        nextPredictionRefreshTimestamp = 0;
        Current = Unavailable("尚未进入正赛。 ");
    }

    private void ObserveParticipants(
        EstateRaceSession session,
        Guid? localParticipantId,
        EstateRaceTrackContext? context,
        RaceGripCondition localGrip,
        VehicleProfileFingerprint? vehicle)
    {
        var present = new HashSet<Guid>();
        foreach (var participant in session.Participants)
        {
            present.Add(participant.Id);
            if (!observations.TryGetValue(participant.Id, out var observation))
            {
                observations[participant.Id] = ParticipantObservation.Start(participant, session.Flag);
                continue;
            }

            var phaseDisrupted = session.Flag is RaceControlFlag.Yellow or RaceControlFlag.Red;
            observation.DisruptedThisLap |= phaseDisrupted;
            ObservePitVisit(
                session,
                participant,
                observation,
                phaseDisrupted,
                participant.Id == localParticipantId,
                context,
                vehicle);

            if (participant.CompletedLaps < observation.CompletedLaps)
            {
                observations[participant.Id] = ParticipantObservation.Start(participant, session.Flag);
                continue;
            }

            var completedDelta = participant.CompletedLaps - observation.CompletedLaps;
            if (completedDelta == 1 &&
                session.Phase == RaceSessionPhase.Race &&
                participant.Id == localParticipantId &&
                participant.LastLapSeconds is double lapSeconds &&
                double.IsFinite(lapSeconds) && lapSeconds is >= 3 and <= 21_600)
            {
                var completedEvent = context?.LastCompletedLap;
                var eventMatchesLap = completedEvent?.LapNumber == participant.CompletedLaps;
                raceLaps.Add(new LapObservation(
                    participant.CompletedLaps,
                    lapSeconds,
                    participant.TrackLimitWarnings > observation.WarningsAtLapStart ||
                    eventMatchesLap && (!completedEvent!.IsValid || !completedEvent.IsBestLapEligible),
                    observation.PitSeenThisLap || participant.IsInPitLane,
                    observation.DisruptedThisLap,
                    participant.CompletedPitServices,
                    localGrip));
                if (raceLaps.Count > MaximumRetainedLaps)
                    raceLaps.RemoveRange(0, raceLaps.Count - MaximumRetainedLaps);
                QueueRaceStintSample(session, context, vehicle, participant.CompletedPitServices);
            }

            if (completedDelta != 0)
            {
                observation.CompletedLaps = participant.CompletedLaps;
                observation.WarningsAtLapStart = participant.TrackLimitWarnings;
                observation.PitSeenThisLap = participant.IsInPitLane;
                observation.DisruptedThisLap = phaseDisrupted;
            }
            else if (participant.IsInPitLane)
            {
                observation.PitSeenThisLap = true;
            }
        }

        foreach (var missing in observations.Keys.Where(id => !present.Contains(id)).ToArray())
            observations.Remove(missing);
    }

    private void ObservePitVisit(
        EstateRaceSession session,
        EstateRaceParticipant participant,
        ParticipantObservation observation,
        bool phaseDisrupted,
        bool isLocal,
        EstateRaceTrackContext? context,
        VehicleProfileFingerprint? vehicle)
    {
        if (!observation.WasInPitLane && participant.IsInPitLane)
        {
            observation.WasInPitLane = true;
            observation.PitElapsedMaximum = Math.Max(0, participant.PitLaneElapsedSeconds);
            observation.ServicesAtPitEntry = participant.CompletedPitServices;
            observation.PitVisitContaminated = phaseDisrupted || HasPenaltyInteraction(participant);
        }
        else if (observation.WasInPitLane && participant.IsInPitLane)
        {
            observation.PitElapsedMaximum = Math.Max(
                observation.PitElapsedMaximum,
                participant.PitLaneElapsedSeconds);
            observation.PitVisitContaminated |= phaseDisrupted || HasPenaltyInteraction(participant);
        }
        else if (observation.WasInPitLane)
        {
            var completedService = participant.CompletedPitServices > observation.ServicesAtPitEntry;
            if (completedService && !observation.PitVisitContaminated &&
                observation.PitElapsedMaximum is >= 3 and <= 600)
            {
                pitVisits.Add(new PitVisitObservation(observation.PitElapsedMaximum));
                if (pitVisits.Count > MaximumRetainedPitVisits)
                    pitVisits.RemoveRange(0, pitVisits.Count - MaximumRetainedPitVisits);
                if (isLocal && context is not null && vehicle is not null)
                {
                    pendingSamples.Enqueue(new EstateStrategySample(
                        Guid.NewGuid(),
                        TrackIdentity(session, context),
                        EstateStrategySampleKind.PitStop,
                        EstateStrategySampleSource.Race,
                        DateTimeOffset.UtcNow,
                        vehicle,
                        0,
                        null,
                        null,
                        null,
                        null,
                        observation.PitElapsedMaximum));
                }
            }
            observation.WasInPitLane = false;
            observation.PitElapsedMaximum = 0;
            observation.PitVisitContaminated = false;
        }
    }

    private EstatePitStrategyPrediction BuildPrediction(
        EstateRaceSession session,
        EstateRaceParticipant local,
        EstateRaceTrackContext context,
        VehicleProfileFingerprint? vehicle)
    {
        var classified = ClassifyLaps();
        if (local.Status is RaceParticipantStatus.Finished or RaceParticipantStatus.DidNotFinish or
            RaceParticipantStatus.Disqualified)
        {
            return CreateTerminalPrediction(
                EstatePitStrategyDecision.Finished,
                "本机比赛已经结束",
                "不再计算新的进站窗口。 ",
                classified);
        }
        if (session.Flag == RaceControlFlag.Chequered)
        {
            return CreateTerminalPrediction(
                EstatePitStrategyDecision.StayOut,
                "方格旗已出示",
                "继续完成当前圈，不再安排额外进站。 ",
                classified);
        }
        var remainingLaps = Math.Max(0, session.TotalRaceLaps - local.CompletedLaps);
        var minimumRequiredPitStops = Math.Clamp(session.MinimumRequiredPitStops, 0, 20);
        var remainingRequiredPitStops = Math.Max(
            0,
            minimumRequiredPitStops - local.CompletedPitServices);
        if (remainingLaps == 0)
        {
            return CreateTerminalPrediction(
                EstatePitStrategyDecision.StayOut,
                "继续完成比赛",
                "已没有可安排的完整比赛圈。 ",
                classified);
        }
        if (remainingRequiredPitStops > 0 && remainingLaps <= remainingRequiredPitStops + 1)
        {
            var currentLap = local.CompletedLaps + 1;
            return new EstatePitStrategyPrediction(
                EstatePitStrategyDecision.PitThisLap,
                "本圈必须进站",
                $"本场至少要求 {minimumRequiredPitStops} 次有效维修停留，目前还差 {remainingRequiredPitStops} 次；若继续留在赛道，将没有足够圈数完成规定进站。",
                currentLap,
                currentLap,
                null,
                false,
                null,
                null,
                null,
                EstatePitStrategyConfidence.High,
                classified.Clean.Count,
                classified.ExcludedCount,
                classified.BoundaryCount,
                classified.AnomalousCount,
                classified.PitCount,
                pitVisits.Count);
        }
        if (remainingLaps <= 1)
        {
            return CreateTerminalPrediction(
                EstatePitStrategyDecision.StayOut,
                "继续完成比赛",
                "剩余赛程不足以回收一次额外进站的损失。 ",
                classified);
        }

        var latestServiceCount = classified.Clean.Count == 0
            ? local.CompletedPitServices
            : classified.Clean.Max(lap => lap.CompletedServices);
        var stintLaps = classified.Clean
            .Where(lap => lap.CompletedServices == latestServiceCount)
            .OrderBy(lap => lap.LapNumber)
            .ToArray();
        var historicalStints = HistoricalMatches(
            vehicle,
            EstateStrategySampleKind.Stint,
            minimumEvidence: 8);
        var historicalPitStops = HistoricalMatches(
            vehicle,
            EstateStrategySampleKind.PitStop,
            minimumEvidence: 3);
        var historicalFlyingLaps = HistoricalMatches(
            vehicle,
            EstateStrategySampleKind.FlyingLap,
            minimumEvidence: 3);
        var historicalEvidenceLaps = historicalStints.Sum(match => match.Sample.LapCount);
        var historicalPaceStints = historicalStints
            .Where(match => match.Tier is EstateStrategyMatchTier.SameCarAndTune or EstateStrategyMatchTier.SameCar)
            .ToArray();
        var historicalMatchDescription = HistoricalMatchDescription(
            historicalStints.Concat(historicalPitStops).Concat(historicalFlyingLaps));
        var usesHistoricalPace = stintLaps.Length < MinimumCleanLaps && historicalPaceStints.Length > 0;
        if (stintLaps.Length < MinimumCleanLaps && historicalPaceStints.Length == 0)
        {
            var flyingPaceMatches = historicalFlyingLaps
                .Where(match => match.Tier is EstateStrategyMatchTier.SameCarAndTune or EstateStrategyMatchTier.SameCar)
                .ToArray();
            var flyingPace = flyingPaceMatches.Length == 0
                ? (double?)null
                : WeightedMedian(flyingPaceMatches
                    .Where(match => match.Sample.RepresentativeLapSeconds is not null)
                    .Select(match => (match.Sample.RepresentativeLapSeconds!.Value, match.Weight)));
            return new EstatePitStrategyPrediction(
                EstatePitStrategyDecision.Collecting,
                flyingPace is null ? "正在建立代表配速" : "已有飞驰圈基线，等待长距离样本",
                flyingPace is null
                    ? $"当前轮胎周期还需要 {MinimumCleanLaps - stintLaps.Length} 个干净圈。进站圈、赛道边界事件和异常离群圈不会用于趋势判断。 "
                    : "历史飞驰圈可用于核对基础速度，但不能代替长距离轮胎衰退数据；建议在练习赛完成长距离轮胎管理。 ",
                null,
                null,
                null,
                false,
                stintLaps.Length == 0
                    ? flyingPace
                    : Median(stintLaps.Select(lap => lap.Seconds)),
                null,
                null,
                EstatePitStrategyConfidence.Low,
                stintLaps.Length,
                classified.ExcludedCount,
                classified.BoundaryCount,
                classified.AnomalousCount,
                classified.PitCount,
                pitVisits.Count,
                EstatePitLossSource.None,
                historicalStints.Count + historicalPitStops.Count + historicalFlyingLaps.Count,
                historicalEvidenceLaps,
                historicalMatchDescription,
                false);
        }

        var historicalRepresentative = WeightedMedian(historicalPaceStints
            .Where(match => match.Sample.RepresentativeLapSeconds is not null)
            .Select(match => (match.Sample.RepresentativeLapSeconds!.Value, match.Weight * Math.Max(1, match.Sample.LapCount))));
        var historicalFreshPace = WeightedMedian(historicalPaceStints
            .Where(match => match.Sample.FreshLapSeconds is not null)
            .Select(match => (match.Sample.FreshLapSeconds!.Value, match.Weight * Math.Max(1, match.Sample.LapCount))));
        var representative = stintLaps.Length >= MinimumCleanLaps
            ? Median(stintLaps.TakeLast(Math.Min(3, stintLaps.Length)).Select(lap => lap.Seconds))
            : historicalRepresentative ?? 0;
        var freshPace = stintLaps.Length >= MinimumCleanLaps
            ? Median(stintLaps.Take(Math.Min(3, stintLaps.Length)).Select(lap => lap.Seconds))
            : historicalFreshPace ?? representative;
        var historicalDegradationFraction = WeightedMedian(historicalStints
            .Where(match => match.Sample.DegradationPerLapSeconds is not null &&
                            match.Sample.RepresentativeLapSeconds is > 3)
            .Select(match => (
                match.Sample.DegradationPerLapSeconds!.Value /
                match.Sample.RepresentativeLapSeconds!.Value,
                match.Weight * Math.Max(1, match.Sample.LapCount))));
        var historicalDegradation = historicalDegradationFraction is double fraction
            ? Math.Max(0, representative * fraction)
            : (double?)null;
        var (rawSlope, slopeSpread) = stintLaps.Length >= MinimumCleanLaps
            ? TheilSen(stintLaps)
            : (historicalDegradation ?? 0, HistoricalDegradationSpread(historicalStints));
        var latestGrip = stintLaps.LastOrDefault(lap => lap.Grip != RaceGripCondition.Unknown)?.Grip ??
                         RaceGripCondition.Unknown;
        var gripEvidence = representative * GripTrendFraction(latestGrip);
        var degradation = Math.Max(0, rawSlope);
        degradation = rawSlope > 0
            ? degradation * 0.78 + gripEvidence * 0.22
            : gripEvidence * 0.35;
        if (stintLaps.Length >= MinimumCleanLaps && historicalDegradation is double historyTrend)
        {
            var currentWeight = Math.Clamp(stintLaps.Length / 8d, 0.35, 1);
            degradation = degradation * currentWeight + Math.Max(0, historyTrend) * (1 - currentWeight);
        }
        degradation = Math.Clamp(degradation, 0, representative * 0.03);

        var normalPitSegmentSeconds = EstimateNormalPitSegmentSeconds(context, representative);
        var currentPitElapsed = pitVisits.Count == 0
            ? (double?)null
            : Median(pitVisits.Select(visit => visit.ElapsedSeconds));
        var historicalPitElapsed = WeightedMedian(historicalPitStops
            .Where(match => match.Sample.PitLaneElapsedSeconds is not null)
            .Select(match => (match.Sample.PitLaneElapsedSeconds!.Value, match.Weight)));
        var configuredPitElapsed = EstimateConfiguredPitElapsedSeconds(context);
        var fullPitElapsed = currentPitElapsed ?? historicalPitElapsed ?? configuredPitElapsed;
        var pitLossSource = currentPitElapsed is not null
            ? EstatePitLossSource.CurrentSession
            : historicalPitElapsed is not null
                ? EstatePitLossSource.Historical
                : configuredPitElapsed is not null
                    ? EstatePitLossSource.ConfiguredGeometry
                    : EstatePitLossSource.None;
        if (fullPitElapsed is not double elapsed || normalPitSegmentSeconds is not double normalSegment)
        {
            return new EstatePitStrategyPrediction(
                EstatePitStrategyDecision.Collecting,
                "等待维修区样本",
                "维修区几何或完整进站时间不足，暂时不输出容易误导的窗口。 ",
                null,
                null,
                null,
                false,
                representative,
                degradation,
                null,
                EstatePitStrategyConfidence.Low,
                stintLaps.Length,
                classified.ExcludedCount,
                classified.BoundaryCount,
                classified.AnomalousCount,
                classified.PitCount,
                pitVisits.Count,
                pitLossSource,
                historicalStints.Count + historicalPitStops.Count + historicalFlyingLaps.Count,
                historicalEvidenceLaps,
                historicalMatchDescription,
                usesHistoricalPace);
        }

        var pitLoss = Math.Clamp(elapsed - normalSegment, 1, 300);
        var noStopCost = ProjectStintCost(representative, degradation, remainingLaps);
        var plannedStopCount = Math.Max(1, remainingRequiredPitStops);
        var candidates = new List<StrategyCandidate>();
        var maximumFirstStopDelay = remainingLaps - plannedStopCount;
        for (var lapsBeforeStop = 1; lapsBeforeStop <= maximumFirstStopDelay; lapsBeforeStop++)
        {
            candidates.Add(new StrategyCandidate(
                lapsBeforeStop,
                ProjectRequiredStopsCost(
                    representative,
                    freshPace,
                    degradation,
                    remainingLaps,
                    plannedStopCount,
                    lapsBeforeStop,
                    pitLoss)));
        }
        var best = candidates.MinBy(candidate => candidate.Cost)!;
        var advantage = remainingRequiredPitStops > 0 ? (double?)null : noStopCost - best.Cost;
        var paceSpread = stintLaps.Length >= MinimumCleanLaps
            ? RobustSpread(stintLaps.Select(lap => lap.Seconds))
            : HistoricalPaceSpread(historicalStints);
        var pitSpread = currentPitElapsed is not null
            ? RobustSpread(pitVisits.Select(visit => visit.ElapsedSeconds))
            : historicalPitElapsed is not null
                ? HistoricalPitSpread(historicalPitStops)
            : Math.Max(3, pitLoss * 0.18);
        var tierPenalty = LowestConfidencePenalty(historicalStints.Concat(historicalPitStops));
        var uncertainty = Math.Max(1,
            paceSpread * Math.Sqrt(remainingLaps) +
            slopeSpread * remainingLaps * Math.Max(0, remainingLaps - 1) / 2 +
            pitSpread + tierPenalty);
        var decisionMargin = Math.Max(1.5, uncertainty * 0.25);
        var confidence = Confidence(
            stintLaps.Length,
            pitVisits.Count,
            slopeSpread,
            latestGrip,
            historicalEvidenceLaps,
            historicalPitStops.Count,
            historicalStints.FirstOrDefault()?.Tier);
        var penaltyNote = HasPenaltyInteraction(local)
            ? " 当前还有待执行处罚；预测没有把处罚时间当作正常进站损失。"
            : string.Empty;

        if (local.IsInPitLane)
        {
            return Prediction(
                EstatePitStrategyDecision.InPit,
                "正在维修区内",
                "本次进站已经开始，窗口预测将在出站并取得新的代表圈后重新评估。" + penaltyNote,
                null,
                null,
                pitLoss,
                pitLossSource is EstatePitLossSource.CurrentSession or EstatePitLossSource.Historical,
                representative,
                degradation,
                advantage,
                confidence,
                classified,
                stintLaps.Length,
                pitLossSource,
                historicalStints.Count + historicalPitStops.Count + historicalFlyingLaps.Count,
                historicalEvidenceLaps,
                historicalMatchDescription,
                usesHistoricalPace);
        }

        if (remainingRequiredPitStops == 0 && advantage <= decisionMargin)
        {
            return Prediction(
                EstatePitStrategyDecision.StayOut,
                "建议继续跑",
                $"在当前趋势下，一次额外进站尚不能稳定回收约 {pitLoss:0.0} 秒损失；预测优势没有超过误差余量。" + penaltyNote,
                null,
                null,
                pitLoss,
                pitLossSource is EstatePitLossSource.CurrentSession or EstatePitLossSource.Historical,
                representative,
                degradation,
                Math.Max(0, advantage ?? 0),
                confidence,
                classified,
                stintLaps.Length,
                pitLossSource,
                historicalStints.Count + historicalPitStops.Count + historicalFlyingLaps.Count,
                historicalEvidenceLaps,
                historicalMatchDescription,
                usesHistoricalPace);
        }

        var nearBestAllowance = Math.Max(1, uncertainty * 0.20);
        var competitive = candidates
            .Where(candidate => candidate.Cost <= best.Cost + nearBestAllowance)
            .OrderBy(candidate => candidate.LapsBeforeStop)
            .ToArray();
        var firstStopLap = local.CompletedLaps + competitive.First().LapsBeforeStop;
        var lastStopLap = local.CompletedLaps + competitive.Last().LapsBeforeStop;
        var thisLap = competitive.Any(candidate => candidate.LapsBeforeStop == 1);
        var mustPitThisLap = remainingRequiredPitStops > 0 &&
                             competitive.All(candidate => candidate.LapsBeforeStop == 1);
        return Prediction(
            mustPitThisLap || thisLap ? EstatePitStrategyDecision.PitThisLap : EstatePitStrategyDecision.PitWindow,
            mustPitThisLap
                ? "本圈必须进站"
                : thisLap
                    ? "本圈可以进站"
                    : $"建议第 {firstStopLap}–{lastStopLap} 圈末进站",
            remainingRequiredPitStops > 0
                ? $"本场至少要求 {minimumRequiredPitStops} 次有效维修停留，目前还差 {remainingRequiredPitStops} 次；窗口按规定进站次数与当前配速衰减共同计算。" + penaltyNote
                : $"与继续跑相比，一停方案预计可回收约 {advantage:0.0} 秒；窗口内差异小于当前模型误差。" + penaltyNote,
            firstStopLap,
            lastStopLap,
            pitLoss,
            pitLossSource is EstatePitLossSource.CurrentSession or EstatePitLossSource.Historical,
            representative,
            degradation,
            advantage,
            confidence,
            classified,
            stintLaps.Length,
            pitLossSource,
            historicalStints.Count + historicalPitStops.Count + historicalFlyingLaps.Count,
            historicalEvidenceLaps,
            historicalMatchDescription,
            usesHistoricalPace);
    }

    private static double ProjectRequiredStopsCost(
        double currentPace,
        double freshPace,
        double degradation,
        int remainingLaps,
        int stopCount,
        int lapsBeforeFirstStop,
        double pitLoss)
    {
        var cost = ProjectStintCost(currentPace, degradation, lapsBeforeFirstStop) + pitLoss * stopCount;
        var freshLaps = remainingLaps - lapsBeforeFirstStop;
        var freshStints = Math.Max(1, stopCount);
        var baseLength = freshLaps / freshStints;
        var remainder = freshLaps % freshStints;
        for (var stint = 0; stint < freshStints; stint++)
        {
            var length = baseLength + (stint < remainder ? 1 : 0);
            cost += ProjectStintCost(freshPace, degradation, length);
        }
        return cost;
    }

    private static EstatePitStrategyPrediction AttachPitRequirement(
        EstatePitStrategyPrediction prediction,
        EstateRaceSession session,
        EstateRaceParticipant local)
    {
        var minimum = Math.Clamp(session.MinimumRequiredPitStops, 0, 20);
        var completed = Math.Max(0, local.CompletedPitServices);
        var remaining = Math.Max(0, minimum - completed);
        var summary = prediction.Summary;
        if (remaining > 0 && !summary.Contains("至少要求", StringComparison.Ordinal))
            summary = $"{summary.Trim()} 本场至少要求 {minimum} 次有效维修停留，目前还差 {remaining} 次。";
        return prediction with
        {
            Summary = summary,
            MinimumRequiredPitStops = minimum,
            CompletedPitStops = completed,
            RemainingRequiredPitStops = remaining
        };
    }

    private static ClassifiedLaps ClassifyLaps(IReadOnlyList<LapObservation>? source = null)
    {
        source ??= [];
        var baseEligible = source.Where(lap =>
            lap.LapNumber > 1 && !lap.HasBoundaryIncident && !lap.PitAffected && !lap.Disrupted).ToArray();
        var globalCenter = baseEligible.Length == 0 ? 0 : Median(baseEligible.Select(lap => lap.Seconds));
        var globalSpread = baseEligible.Length == 0 ? 0 : RobustSpread(baseEligible.Select(lap => lap.Seconds));
        var anomalies = new HashSet<LapObservation>();
        foreach (var group in baseEligible.GroupBy(lap => lap.CompletedServices))
        {
            var groupLaps = group.ToArray();
            var center = groupLaps.Length >= 3 ? Median(groupLaps.Select(lap => lap.Seconds)) : globalCenter;
            var spread = groupLaps.Length >= 3 ? RobustSpread(groupLaps.Select(lap => lap.Seconds)) : globalSpread;
            var slowAllowance = Math.Max(Math.Max(4, center * 0.08), spread * 4);
            var fastAllowance = Math.Max(Math.Max(3, center * 0.04), spread * 4);
            foreach (var lap in groupLaps)
            {
                if (lap.Seconds > center + slowAllowance || lap.Seconds < center - fastAllowance)
                    anomalies.Add(lap);
            }
        }
        var clean = baseEligible.Where(lap => !anomalies.Contains(lap)).ToArray();
        var boundary = source.Count(lap => lap.HasBoundaryIncident);
        var pit = source.Count(lap => lap.PitAffected);
        var anomalyCount = anomalies.Count;
        return new ClassifiedLaps(
            clean,
            source.Count - clean.Length,
            boundary,
            anomalyCount,
            pit);
    }

    private ClassifiedLaps ClassifyLaps() => ClassifyLaps(raceLaps);

    private EstatePitStrategyPrediction Prediction(
        EstatePitStrategyDecision decision,
        string title,
        string summary,
        int? windowStart,
        int? windowEnd,
        double pitLoss,
        bool observedPitLoss,
        double representative,
        double degradation,
        double? advantage,
        EstatePitStrategyConfidence confidence,
        ClassifiedLaps classified,
        int stintCleanLapCount,
        EstatePitLossSource pitLossSource,
        int historicalSampleCount,
        int historicalEvidenceLapCount,
        string? historicalMatchDescription,
        bool usesHistoricalPace) => new(
        decision,
        title,
        summary,
        windowStart,
        windowEnd,
        pitLoss,
        observedPitLoss,
        representative,
        degradation,
        advantage,
        confidence,
        stintCleanLapCount,
        classified.ExcludedCount,
        classified.BoundaryCount,
        classified.AnomalousCount,
        classified.PitCount,
        pitVisits.Count,
        pitLossSource,
        historicalSampleCount,
        historicalEvidenceLapCount,
        historicalMatchDescription,
        usesHistoricalPace);

    private static EstatePitStrategyPrediction Unavailable(string summary) => new(
        EstatePitStrategyDecision.Unavailable,
        "暂不预测",
        summary.Trim(),
        null,
        null,
        null,
        false,
        null,
        null,
        null,
        EstatePitStrategyConfidence.Low,
        0,
        0,
        0,
        0,
        0,
        0);

    private static EstatePitStrategyPrediction CreateTerminalPrediction(
        EstatePitStrategyDecision decision,
        string title,
        string summary,
        ClassifiedLaps? classified = null)
    {
        var value = classified ?? new ClassifiedLaps([], 0, 0, 0, 0);
        return new EstatePitStrategyPrediction(
            decision,
            title,
            summary.Trim(),
            null,
            null,
            null,
            false,
            null,
            null,
            null,
            EstatePitStrategyConfidence.Low,
            value.Clean.Count,
            value.ExcludedCount,
            value.BoundaryCount,
            value.AnomalousCount,
            value.PitCount,
            0);
    }

    private static bool HasPenaltyInteraction(EstateRaceParticipant participant) =>
        participant.PendingTimePenaltySeconds > 0 ||
        participant.IsServingTimePenalty ||
        participant.HasPendingDriveThrough ||
        participant.IsServingDriveThrough;

    private static bool IsRaceContext(EstateRaceSession session) =>
        session.Phase == RaceSessionPhase.Race ||
        session.Phase == RaceSessionPhase.Suspended &&
        session.SuspendedFromPhase == RaceSessionPhase.Race;

    private static double ProjectStintCost(double startingPace, double degradation, int laps)
    {
        if (laps <= 0) return 0;
        return laps * startingPace + degradation * laps * (laps - 1) / 2;
    }

    private static double? EstimateConfiguredPitElapsedSeconds(EstateRaceTrackContext context)
    {
        var pit = context.Definition.Pit;
        if (pit?.CenterLine is not { Count: >= 2 } || pit.SpeedLimitKph <= 0) return null;
        var entry = EstateRaceGeometry.PitGateProgress(pit, pit.EntryGate);
        var exit = EstateRaceGeometry.PitGateProgress(pit, pit.ExitGate);
        var distance = exit - entry;
        if (!double.IsFinite(distance) || distance <= 1) return null;
        var averageMetersPerSecond = Math.Max(2, pit.SpeedLimitKph / 3.6 * 0.86);
        return distance / averageMetersPerSecond + Math.Clamp(pit.MinimumServiceSeconds, 1, 60);
    }

    private static double? EstimateNormalPitSegmentSeconds(
        EstateRaceTrackContext context,
        double representativeLapSeconds)
    {
        var pit = context.Definition.Pit;
        if (pit is null || context.Track.LengthMeters <= 0) return null;
        static Vector3F Center(EstateTimingGate gate) => new(
            (float)((gate.Left.X + gate.Right.X) / 2),
            (float)((gate.Left.Y + gate.Right.Y) / 2),
            (float)((gate.Left.Z + gate.Right.Z) / 2));
        var entry = EstateRaceGeometry.Project(context.Track, Center(pit.EntryGate)).Progress;
        var exit = EstateRaceGeometry.Project(context.Track, Center(pit.ExitGate)).Progress;
        var progress = exit - entry;
        if (progress < 0) progress += 1;
        if (!double.IsFinite(progress) || progress is <= 0.001 or > 0.75) return null;
        return representativeLapSeconds * progress;
    }

    private static (double Slope, double Spread) TheilSen(IReadOnlyList<LapObservation> laps)
    {
        var slopes = new List<double>();
        for (var left = 0; left < laps.Count - 1; left++)
        for (var right = left + 1; right < laps.Count; right++)
        {
            var lapDelta = laps[right].LapNumber - laps[left].LapNumber;
            if (lapDelta > 0)
                slopes.Add((laps[right].Seconds - laps[left].Seconds) / lapDelta);
        }
        if (slopes.Count == 0) return (0, 0);
        return (Math.Clamp(Median(slopes), -2, 5), RobustSpread(slopes));
    }

    private static EstatePitStrategyConfidence Confidence(
        int cleanLaps,
        int observedPitVisits,
        double slopeSpread,
        RaceGripCondition grip,
        int historicalLaps,
        int historicalPitStops,
        EstateStrategyMatchTier? bestHistoricalTier)
    {
        var score = 0.22 + Math.Min(1, cleanLaps / 8d) * 0.38;
        score += observedPitVisits > 0 ? Math.Min(1, observedPitVisits / 3d) * 0.27 : 0.05;
        if (cleanLaps < MinimumCleanLaps)
            score += Math.Min(1, historicalLaps / 12d) * 0.24;
        if (observedPitVisits == 0)
            score += Math.Min(1, historicalPitStops / 3d) * 0.16;
        score += slopeSpread <= 0.35 ? 0.08 : slopeSpread <= 0.8 ? 0.04 : 0;
        if (grip != RaceGripCondition.Unknown) score += 0.05;
        score -= bestHistoricalTier switch
        {
            EstateStrategyMatchTier.SameCarAndTune => 0,
            EstateStrategyMatchTier.SameCar => 0.02,
            EstateStrategyMatchTier.SamePerformanceIndex => 0.05,
            EstateStrategyMatchTier.NearbyPerformanceIndex => 0.08,
            EstateStrategyMatchTier.SamePerformanceClass => 0.13,
            EstateStrategyMatchTier.NearbyPerformanceIndexAcrossClass => 0.20,
            _ => 0
        };
        return score >= 0.76
            ? EstatePitStrategyConfidence.High
            : score >= 0.52
                ? EstatePitStrategyConfidence.Medium
                : EstatePitStrategyConfidence.Low;
    }

    private EstatePitStrategyPrediction BuildDataStatus(
        EstateRaceSession session,
        VehicleProfileFingerprint? vehicle)
    {
        if (vehicle is null)
            return Unavailable("已进入房间；正在识别本机车辆，随后会载入同赛道策略样本。 ");
        var stints = HistoricalMatches(vehicle, EstateStrategySampleKind.Stint, 8);
        var pits = HistoricalMatches(vehicle, EstateStrategySampleKind.PitStop, 3);
        var flying = HistoricalMatches(vehicle, EstateStrategySampleKind.FlyingLap, 3);
        var all = stints.Concat(pits).Concat(flying).ToArray();
        var phaseText = session.Phase == RaceSessionPhase.Practice
            ? "练习赛测试获得的新样本会立即保存并刷新预测基线。"
            : session.Phase == RaceSessionPhase.Qualifying
                ? "排位赛期间继续保留已载入基线，正赛开始后自动计算进站窗口。"
                : "正赛开始后会结合本场新数据持续刷新进站窗口。";
        if (all.Length == 0)
        {
            return new EstatePitStrategyPrediction(
                EstatePitStrategyDecision.Collecting,
                "等待同赛道策略样本",
                $"当前还没有适合本车的历史样本。{phaseText}",
                null, null, null, false, null, null, null,
                EstatePitStrategyConfidence.Low,
                0, 0, 0, 0, 0, 0);
        }
        var description = HistoricalMatchDescription(all);
        return new EstatePitStrategyPrediction(
            EstatePitStrategyDecision.Collecting,
            "已载入同赛道策略基线",
            $"已匹配 {all.Length} 条压缩样本（{description}）。{phaseText}",
            null, null, null,
            pits.Count > 0,
            WeightedMedian(stints
                .Where(match => match.Sample.RepresentativeLapSeconds is not null)
                .Select(match => (match.Sample.RepresentativeLapSeconds!.Value, match.Weight))),
            WeightedMedian(stints
                .Where(match => match.Sample.DegradationPerLapSeconds is not null)
                .Select(match => (match.Sample.DegradationPerLapSeconds!.Value, match.Weight))),
            null,
            EstatePitStrategyConfidence.Low,
            0, 0, 0, 0, 0, 0,
            pits.Count > 0 ? EstatePitLossSource.Historical : EstatePitLossSource.None,
            all.Length,
            stints.Sum(match => match.Sample.LapCount),
            description,
            stints.Count > 0);
    }

    private IReadOnlyList<EstateStrategyMatchedSample> HistoricalMatches(
        VehicleProfileFingerprint? vehicle,
        EstateStrategySampleKind kind,
        int minimumEvidence) => vehicle is null
        ? []
        : EstateStrategySampleMatcher.Select(historicalSamples, vehicle, kind, minimumEvidence);

    private void QueueRaceStintSample(
        EstateRaceSession session,
        EstateRaceTrackContext? context,
        VehicleProfileFingerprint? vehicle,
        int completedServices)
    {
        if (context is null || vehicle is null) return;
        var classified = ClassifyLaps();
        var stint = classified.Clean
            .Where(lap => lap.CompletedServices == completedServices)
            .OrderBy(lap => lap.LapNumber)
            .ToArray();
        if (stint.Length < MinimumCleanLaps) return;
        if (!raceStintSampleIds.TryGetValue(completedServices, out var id))
        {
            id = DeterministicStintId(raceRunId, completedServices);
            raceStintSampleIds[completedServices] = id;
        }
        var (slope, _) = TheilSen(stint);
        pendingSamples.Enqueue(new EstateStrategySample(
            id,
            TrackIdentity(session, context),
            EstateStrategySampleKind.Stint,
            EstateStrategySampleSource.Race,
            DateTimeOffset.UtcNow,
            vehicle,
            stint.Length,
            Median(stint.Take(Math.Min(3, stint.Length)).Select(lap => lap.Seconds)),
            Median(stint.TakeLast(Math.Min(3, stint.Length)).Select(lap => lap.Seconds)),
            Math.Max(0, slope),
            RobustSpread(stint.Select(lap => lap.Seconds)),
            null));
    }

    private static EstateStrategyTrackIdentity TrackIdentity(
        EstateRaceSession session,
        EstateRaceTrackContext context) => new(
        session.TrackId ?? context.Definition.TrackId.ToString("D"),
        session.TrackRevision ?? context.Definition.MapRevision,
        session.TrackPackageHash ?? context.TrackPackageHash ?? string.Empty);

    private static Guid DeterministicStintId(Guid runId, int completedServices)
    {
        var bytes = runId.ToByteArray();
        var serviceBytes = BitConverter.GetBytes(completedServices);
        for (var index = 0; index < serviceBytes.Length; index++) bytes[index] ^= serviceBytes[index];
        return new Guid(bytes);
    }

    private static string HistoricalMatchDescription(
        IEnumerable<EstateStrategyMatchedSample> matches)
    {
        var tiers = matches.Select(match => match.Tier).Distinct().Order().ToArray();
        return tiers.Length == 0
            ? "无历史匹配"
            : string.Join("、", tiers.Select(EstateStrategySampleMatcher.TierLabel));
    }

    private static double HistoricalPaceSpread(
        IReadOnlyList<EstateStrategyMatchedSample> samples)
    {
        var recorded = samples
            .Where(match => match.Sample.PaceSpreadSeconds is not null)
            .Select(match => match.Sample.PaceSpreadSeconds!.Value)
            .ToArray();
        return recorded.Length == 0 ? 1.5 : Math.Max(0.2, Median(recorded));
    }

    private static double HistoricalDegradationSpread(
        IReadOnlyList<EstateStrategyMatchedSample> samples) => RobustSpread(samples
            .Where(match => match.Sample.DegradationPerLapSeconds is not null)
            .Select(match => match.Sample.DegradationPerLapSeconds!.Value));

    private static double HistoricalPitSpread(
        IReadOnlyList<EstateStrategyMatchedSample> samples)
    {
        var values = samples
            .Where(match => match.Sample.PitLaneElapsedSeconds is not null)
            .Select(match => match.Sample.PitLaneElapsedSeconds!.Value)
            .ToArray();
        return values.Length < 2 ? 2.5 : Math.Max(1, RobustSpread(values));
    }

    private static double LowestConfidencePenalty(
        IEnumerable<EstateStrategyMatchedSample> samples)
    {
        var worstTier = samples.Select(match => (int)match.Tier).DefaultIfEmpty(0).Max();
        return worstTier switch
        {
            <= 0 => 0,
            1 => 0.5,
            2 => 1,
            3 => 2,
            4 => 3.5,
            _ => 5
        };
    }

    private static double? WeightedMedian(IEnumerable<(double Value, double Weight)> source)
    {
        var values = source
            .Where(item => double.IsFinite(item.Value) && double.IsFinite(item.Weight) && item.Weight > 0)
            .OrderBy(item => item.Value)
            .ToArray();
        if (values.Length == 0) return null;
        var half = values.Sum(item => item.Weight) / 2;
        var cumulative = 0d;
        foreach (var item in values)
        {
            cumulative += item.Weight;
            if (cumulative >= half) return item.Value;
        }
        return values[^1].Value;
    }

    private static double GripTrendFraction(RaceGripCondition condition) => condition switch
    {
        RaceGripCondition.SlightlyReduced => 0.0010,
        RaceGripCondition.ModeratelyReduced => 0.0025,
        RaceGripCondition.SeverelyReduced => 0.0045,
        RaceGripCondition.AtLimit => 0.0075,
        _ => 0
    };

    private static double Median(IEnumerable<double> source)
    {
        var values = source.Where(double.IsFinite).OrderBy(value => value).ToArray();
        if (values.Length == 0) return 0;
        var middle = values.Length / 2;
        return values.Length % 2 == 0
            ? (values[middle - 1] + values[middle]) / 2
            : values[middle];
    }

    private static double RobustSpread(IEnumerable<double> source)
    {
        var values = source.Where(double.IsFinite).ToArray();
        if (values.Length < 2) return 0;
        var center = Median(values);
        return Median(values.Select(value => Math.Abs(value - center))) * 1.4826;
    }

    private static string TrackKey(EstateRaceSession session, EstateRaceTrackContext? context) =>
        new EstateStrategyTrackIdentity(
            session.TrackId ?? context?.Definition.TrackId.ToString("D") ?? string.Empty,
            session.TrackRevision ?? context?.Definition.MapRevision ?? string.Empty,
            session.TrackPackageHash ?? context?.TrackPackageHash ?? string.Empty).Key;

    private sealed class ParticipantObservation
    {
        public int CompletedLaps { get; set; }
        public int WarningsAtLapStart { get; set; }
        public bool PitSeenThisLap { get; set; }
        public bool DisruptedThisLap { get; set; }
        public bool WasInPitLane { get; set; }
        public double PitElapsedMaximum { get; set; }
        public int ServicesAtPitEntry { get; set; }
        public bool PitVisitContaminated { get; set; }

        public static ParticipantObservation Start(
            EstateRaceParticipant participant,
            RaceControlFlag flag) => new()
        {
            CompletedLaps = participant.CompletedLaps,
            WarningsAtLapStart = participant.TrackLimitWarnings,
            PitSeenThisLap = participant.IsInPitLane,
            DisruptedThisLap = flag is RaceControlFlag.Yellow or RaceControlFlag.Red,
            WasInPitLane = participant.IsInPitLane,
            PitElapsedMaximum = participant.IsInPitLane ? participant.PitLaneElapsedSeconds : 0,
            ServicesAtPitEntry = participant.CompletedPitServices,
            PitVisitContaminated = participant.IsInPitLane && HasPenaltyInteraction(participant)
        };
    }

    private sealed record LapObservation(
        int LapNumber,
        double Seconds,
        bool HasBoundaryIncident,
        bool PitAffected,
        bool Disrupted,
        int CompletedServices,
        RaceGripCondition Grip);

    private sealed record PitVisitObservation(double ElapsedSeconds);

    private sealed record ClassifiedLaps(
        IReadOnlyList<LapObservation> Clean,
        int ExcludedCount,
        int BoundaryCount,
        int AnomalousCount,
        int PitCount);

    private sealed record PredictionRefreshKey(
        RaceControlFlag Flag,
        int TotalRaceLaps,
        int MinimumRequiredPitStops,
        RaceParticipantStatus Status,
        int CompletedLaps,
        int CompletedPitServices,
        bool IsInPitLane,
        int RaceLapSamples,
        int PitVisitSamples,
        int HistoricalSamples);

    private sealed record StrategyCandidate(int LapsBeforeStop, double Cost);
}
