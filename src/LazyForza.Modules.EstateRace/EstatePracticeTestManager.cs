using LazyForza.Domain;

namespace LazyForza.Modules.EstateRace;

internal sealed class EstatePracticeTestManager
{
    private const double LongRunTargetSeconds = 13 * 60;
    private const double LongRunFallbackDistanceMeters = 20_000;
    private const int MinimumLongRunLaps = 5;
    private const int MaximumLongRunLaps = 12;
    private static readonly TimeSpan TerminalHudDuration = TimeSpan.FromSeconds(5);
    private readonly Dictionary<EstatePracticeTestKind, TestResult> results = [];
    private readonly Queue<EstateStrategySample> pendingSamples = new();
    private ActiveTest? active;
    private bool driverInterventionDetected;
    private int currentLongRunTargetLaps = 10;
    private int storedSampleCount;

    public EstatePracticeTestPanelState Current { get; private set; } =
        EstatePracticeTestPanelState.Hidden;

    public void SetStoredSampleCount(int value)
    {
        storedSampleCount = Math.Max(0, value);
        if (Current.IsPracticeSession) Current = BuildPanel();
    }

    public void Start(
        EstatePracticeTestKind kind,
        EstateRaceSession session,
        EstateRaceParticipant local,
        EstateRaceTrackContext context,
        VehicleProfileFingerprint vehicle,
        EstatePitServiceState pitService)
    {
        if (session.Phase != RaceSessionPhase.Practice)
            throw new InvalidOperationException("只有练习赛阶段可以开始策略测试。 ");
        if (active is not null)
            throw new InvalidOperationException("请先结束当前测试项目。 ");

        var stage = kind switch
        {
            EstatePracticeTestKind.LongRun =>
                pitService.IsInPitLane ? TestStage.AwaitingPitExit : TestStage.Arming,
            EstatePracticeTestKind.PitStopSimulation =>
                pitService.IsInPitLane ? TestStage.AwaitingPitExit : TestStage.AwaitingPitEntry,
            EstatePracticeTestKind.QualifyingSimulation =>
                pitService.IsInPitLane ? TestStage.AwaitingPitExit : TestStage.Arming,
            _ => TestStage.Arming
        };
        currentLongRunTargetLaps = CalculateLongRunTargetLaps(context);
        active = new ActiveTest(
            Guid.NewGuid(),
            kind,
            stage,
            TrackIdentity(session, context),
            vehicle,
            context.LastCompletedLap?.EventId,
            local.CompletedLaps,
            local.TrackLimitWarnings,
            ActivePenaltyIds(local),
            local.CompletedPitServices,
            pitService.IsInPitLane,
            currentLongRunTargetLaps);
        driverInterventionDetected = false;
        results.Remove(kind);
        Current = BuildPanel();
    }

    public void Stop(string reason = "已由你手动结束。 ")
    {
        if (active is null) return;
        Finish(EstatePracticeTestStatus.Cancelled, reason.Trim(), null);
    }

    public void StopAutomatically(string reason)
    {
        if (active is null) return;
        FinishAutomatically(EstatePracticeTestStatus.Cancelled, reason);
    }

    public void NotifyDriverIntervention(EstatePitServiceState pitService)
    {
        if (active is null) return;
        if (active.Kind == EstatePracticeTestKind.PitStopSimulation &&
            active.Stage is TestStage.OutLapInPit or TestStage.Servicing or TestStage.FinalPitExit &&
            pitService.IsInServiceZone)
            return;
        driverInterventionDetected = true;
    }

    public void Observe(
        EstateRaceSession? session,
        Guid? localParticipantId,
        EstateRaceTrackContext? context,
        EstatePitServiceState pitService,
        RaceGripCondition grip,
        VehicleProfileFingerprint vehicle,
        bool isObserver)
    {
        if (session is null || context is null || isObserver ||
            localParticipantId is not Guid localId ||
            session.Participants.FirstOrDefault(participant => participant.Id == localId) is not { } local)
        {
            if (active is not null)
                FinishAutomatically(EstatePracticeTestStatus.Failed, "本机车手或赛道数据已中断。 ");
            Current = EstatePracticeTestPanelState.Hidden;
            return;
        }

        if (session.Phase != RaceSessionPhase.Practice)
        {
            if (active is not null)
                FinishAutomatically(EstatePracticeTestStatus.Cancelled, "练习赛已经结束，测试已自动关闭。 ");
            Current = EstatePracticeTestPanelState.Hidden;
            return;
        }

        if (active is null)
        {
            currentLongRunTargetLaps = CalculateLongRunTargetLaps(context);
            Current = BuildPanel();
            return;
        }

        active.Vehicle = vehicle;
        if (driverInterventionDetected)
        {
            FinishAutomatically(EstatePracticeTestStatus.Failed, "检测到暂停或回转，测试已终止。 ");
            return;
        }
        if (session.Flag is RaceControlFlag.Yellow or RaceControlFlag.Red)
        {
            FinishAutomatically(EstatePracticeTestStatus.Failed, "测试期间出现黄旗或红旗，测试已终止。 ");
            return;
        }
        if (local.TrackLimitWarnings > active.WarningBaseline)
        {
            FinishAutomatically(EstatePracticeTestStatus.Failed, "检测到赛道边界事件，测试已终止。 ");
            return;
        }
        if (ActivePenaltyIds(local).Except(active.PenaltyBaseline).Any())
        {
            FinishAutomatically(EstatePracticeTestStatus.Failed, "测试期间收到新的警告或处罚，测试已终止。 ");
            return;
        }
        if (local.Status is RaceParticipantStatus.Disconnected or RaceParticipantStatus.Disqualified)
        {
            FinishAutomatically(EstatePracticeTestStatus.Failed, "车手状态已中断，测试已终止。 ");
            return;
        }

        switch (active.Kind)
        {
            case EstatePracticeTestKind.LongRun:
                ObserveLongRun(local, context, pitService, grip);
                break;
            case EstatePracticeTestKind.PitStopSimulation:
                ObservePitSimulation(local, context, pitService);
                break;
            case EstatePracticeTestKind.QualifyingSimulation:
                ObserveQualifying(local, context, pitService, grip);
                break;
        }

        if (active is not null) Current = BuildPanel();
    }

    public IReadOnlyList<EstateStrategySample> DrainSamples()
    {
        var result = new List<EstateStrategySample>(pendingSamples.Count);
        while (pendingSamples.TryDequeue(out var sample)) result.Add(sample);
        return result;
    }

    public void Reset()
    {
        active = null;
        driverInterventionDetected = false;
        pendingSamples.Clear();
        Current = EstatePracticeTestPanelState.Hidden;
    }

    private void ObserveLongRun(
        EstateRaceParticipant local,
        EstateRaceTrackContext context,
        EstatePitServiceState pitService,
        RaceGripCondition grip)
    {
        if (active is null) return;
        if (active.Stage == TestStage.AwaitingPitExit)
        {
            if (!pitService.IsInPitLane && active.WasInPitLane)
            {
                active.Stage = TestStage.Arming;
                active.LastLapEventId = context.LastCompletedLap?.EventId;
            }
            active.WasInPitLane = pitService.IsInPitLane;
            return;
        }
        if (pitService.IsInPitLane)
        {
            FinishAutomatically(EstatePracticeTestStatus.Failed, "长距离测试中进入了维修区，测试已终止。 ");
            return;
        }
        if (!TryTakeLap(context, out var lap)) return;
        if (!ValidLap(lap, out var reason))
        {
            FinishAutomatically(EstatePracticeTestStatus.Failed, reason);
            return;
        }
        if (active.Stage == TestStage.Arming)
        {
            active.Stage = TestStage.Collecting;
            return;
        }

        active.Laps.Add(new PracticeLap(lap.LapNumber, lap.LapSeconds, grip));
        if (active.Laps.Count < active.LongRunTargetLaps) return;
        var sample = CreateStintSample(active);
        Finish(
            EstatePracticeTestStatus.Completed,
            $"已完成 {active.LongRunTargetLaps} 个连续干净圈，轮胎周期样本已保存。 ",
            sample);
    }

    private void ObservePitSimulation(
        EstateRaceParticipant local,
        EstateRaceTrackContext context,
        EstatePitServiceState pitService)
    {
        if (active is null) return;
        active.PitElapsedMaximum = Math.Max(
            active.PitElapsedMaximum,
            Math.Max(local.PitLaneElapsedSeconds, pitService.PitLaneElapsedSeconds));
        if (pitService.IsSpeeding)
        {
            FinishAutomatically(EstatePracticeTestStatus.Failed, "维修区内发生超速，测试已终止。 ");
            return;
        }

        switch (active.Stage)
        {
            case TestStage.AwaitingPitEntry:
                if (pitService.IsInPitLane)
                {
                    active.Stage = TestStage.AwaitingPitExit;
                    active.ServiceBaseline = Math.Max(local.CompletedPitServices, pitService.CompletedServices);
                    active.PitElapsedMaximum = 0;
                    active.WasInPitLane = true;
                }
                return;
            case TestStage.AwaitingPitExit:
                if (!pitService.IsInPitLane && active.WasInPitLane)
                {
                    active.Stage = TestStage.OutLap;
                    active.LastLapEventId = context.LastCompletedLap?.EventId;
                    active.OutLapStartCompletedLaps = local.CompletedLaps;
                    active.ServiceBaseline = Math.Max(local.CompletedPitServices, pitService.CompletedServices);
                    active.PitElapsedMaximum = 0;
                }
                active.WasInPitLane = pitService.IsInPitLane;
                return;
            case TestStage.OutLap:
                if (TryTakeLap(context, out var outLap))
                {
                    if (!ValidLap(outLap, out var outLapReason))
                    {
                        FinishAutomatically(EstatePracticeTestStatus.Failed, outLapReason);
                        return;
                    }
                    active.OutLapCompletedLaps = local.CompletedLaps;
                    if (pitService.IsInPitLane)
                    {
                        BeginPitSimulationVisit(active, local, pitService);
                        active.Stage = HasCompletedService(active, local, pitService)
                            ? TestStage.FinalPitExit
                            : TestStage.Servicing;
                    }
                    else
                    {
                        active.Stage = TestStage.ReturnToPit;
                    }
                    return;
                }
                if (pitService.IsInPitLane)
                {
                    if (!CanCompleteOutLapThroughPit(context))
                    {
                        FinishAutomatically(EstatePracticeTestStatus.Failed, "出站圈尚未完成就再次进站，测试已终止。 ");
                        return;
                    }
                    BeginPitSimulationVisit(active, local, pitService);
                    active.Stage = TestStage.OutLapInPit;
                    return;
                }
                return;
            case TestStage.OutLapInPit:
                if (!pitService.IsInPitLane)
                {
                    FinishAutomatically(EstatePracticeTestStatus.Failed, "尚未通过维修区内的起终点线并完成模拟换胎就驶离维修区，测试已终止。 ");
                    return;
                }
                active.ServiceCompletedDuringPitVisit |= HasCompletedService(active, local, pitService);
                if (!TryTakeLap(context, out var pitOutLap)) return;
                if (!ValidLap(pitOutLap, out var pitOutLapReason))
                {
                    FinishAutomatically(EstatePracticeTestStatus.Failed, pitOutLapReason);
                    return;
                }
                active.OutLapCompletedLaps = local.CompletedLaps;
                active.Stage = active.ServiceCompletedDuringPitVisit
                    ? TestStage.FinalPitExit
                    : TestStage.Servicing;
                return;
            case TestStage.ReturnToPit:
                if (pitService.IsInPitLane)
                {
                    BeginPitSimulationVisit(active, local, pitService);
                    active.Stage = HasCompletedService(active, local, pitService)
                        ? TestStage.FinalPitExit
                        : TestStage.Servicing;
                    return;
                }
                if (local.CompletedLaps > active.OutLapCompletedLaps)
                    FinishAutomatically(EstatePracticeTestStatus.Failed, "完成出站圈后没有立即进站，测试已终止。 ");
                return;
            case TestStage.Servicing:
                if (!pitService.IsInPitLane)
                {
                    FinishAutomatically(EstatePracticeTestStatus.Failed, "尚未完成模拟换胎就离开维修区，测试已终止。 ");
                    return;
                }
                if (local.CompletedPitServices > active.ServiceBaseline ||
                    pitService.CompletedServices > active.ServiceBaseline)
                {
                    active.ServiceCompletedDuringPitVisit = true;
                    active.Stage = TestStage.FinalPitExit;
                }
                return;
            case TestStage.FinalPitExit:
                if (pitService.IsInPitLane) return;
                if (active.PitElapsedMaximum is < 3 or > 600)
                {
                    FinishAutomatically(EstatePracticeTestStatus.Failed, "没有取得完整、可信的维修区用时。 ");
                    return;
                }
                Finish(
                    EstatePracticeTestStatus.Completed,
                    $"模拟换胎完成，记录的维修区总用时为 {active.PitElapsedMaximum:0.0} 秒。 ",
                    CreatePitSample(active));
                return;
        }
    }

    private static void BeginPitSimulationVisit(
        ActiveTest test,
        EstateRaceParticipant local,
        EstatePitServiceState pitService)
    {
        test.ServiceBaseline = Math.Max(local.CompletedPitServices, pitService.CompletedServices);
        test.PitElapsedMaximum = Math.Max(local.PitLaneElapsedSeconds, pitService.PitLaneElapsedSeconds);
        test.WasInPitLane = true;
        test.ServiceCompletedDuringPitVisit = false;
    }

    private static bool HasCompletedService(
        ActiveTest test,
        EstateRaceParticipant local,
        EstatePitServiceState pitService) =>
        local.CompletedPitServices > test.ServiceBaseline ||
        pitService.CompletedServices > test.ServiceBaseline;

    private static bool CanCompleteOutLapThroughPit(EstateRaceTrackContext context)
    {
        var pit = context.Definition.Pit;
        if (pit?.StartFinishGate is not EstateTimingGate pitFinish || pit.CenterLine.Count < 2)
            return false;
        var entryProgress = EstateRaceGeometry.PitGateProgress(pit, pit.EntryGate);
        var finishProgress = EstateRaceGeometry.PitGateProgress(pit, pitFinish);
        var exitProgress = EstateRaceGeometry.PitGateProgress(pit, pit.ExitGate);
        var upperBound = Math.Max(exitProgress, EstateRaceGeometry.ProjectPitRoute(
            pit,
            new Vector3F(
                (float)pit.CenterLine[^1].X,
                (float)pit.CenterLine[^1].Y,
                (float)pit.CenterLine[^1].Z)).TotalLengthMeters);
        return finishProgress >= entryProgress - 0.75 && finishProgress <= upperBound + 0.75;
    }

    private void ObserveQualifying(
        EstateRaceParticipant local,
        EstateRaceTrackContext context,
        EstatePitServiceState pitService,
        RaceGripCondition grip)
    {
        if (active is null) return;
        if (active.Stage == TestStage.AwaitingPitExit)
        {
            if (!pitService.IsInPitLane && active.WasInPitLane)
            {
                active.Stage = TestStage.Arming;
                active.LastLapEventId = context.LastCompletedLap?.EventId;
            }
            active.WasInPitLane = pitService.IsInPitLane;
            return;
        }
        if (pitService.IsInPitLane)
        {
            FinishAutomatically(EstatePracticeTestStatus.Failed, "排位模拟过程中进入了维修区，测试已终止。 ");
            return;
        }
        if (!TryTakeLap(context, out var lap)) return;
        if (!ValidLap(lap, out var reason))
        {
            FinishAutomatically(EstatePracticeTestStatus.Failed, reason);
            return;
        }
        if (active.Stage == TestStage.Arming)
        {
            active.Stage = TestStage.FlyingLap;
            return;
        }
        if (active.Stage != TestStage.FlyingLap) return;
        var sample = new EstateStrategySample(
            active.Id,
            active.Track,
            EstateStrategySampleKind.FlyingLap,
            EstateStrategySampleSource.PracticeQualifyingSimulation,
            DateTimeOffset.UtcNow,
            active.Vehicle,
            1,
            lap.LapSeconds,
            lap.LapSeconds,
            null,
            null,
            null);
        Finish(EstatePracticeTestStatus.Completed, $"飞驰圈完成：{FormatLap(lap.LapSeconds)}。 ", sample);
    }

    private bool TryTakeLap(EstateRaceTrackContext context, out EstateCompletedLapEvent lap)
    {
        lap = null!;
        if (active is null || context.LastCompletedLap is not { } completed ||
            completed.EventId == active.LastLapEventId)
            return false;
        active.LastLapEventId = completed.EventId;
        lap = completed;
        return true;
    }

    private static bool ValidLap(EstateCompletedLapEvent lap, out string reason)
    {
        if (lap.IsValid && lap.IsBestLapEligible &&
            double.IsFinite(lap.LapSeconds) && lap.LapSeconds is >= 3 and <= 21_600)
        {
            reason = string.Empty;
            return true;
        }
        reason = string.IsNullOrWhiteSpace(lap.InvalidReason)
            ? "检测到无效单圈，测试已终止。 "
            : $"单圈无效：{lap.InvalidReason}。测试已终止。 ";
        return false;
    }

    private EstateStrategySample CreateStintSample(ActiveTest test)
    {
        var laps = test.Laps.OrderBy(lap => lap.LapNumber).ToArray();
        var first = Median(laps.Take(Math.Min(3, laps.Length)).Select(lap => lap.Seconds));
        var representative = Median(laps.TakeLast(Math.Min(3, laps.Length)).Select(lap => lap.Seconds));
        return new EstateStrategySample(
            test.Id,
            test.Track,
            EstateStrategySampleKind.Stint,
            EstateStrategySampleSource.PracticeLongRun,
            DateTimeOffset.UtcNow,
            test.Vehicle,
            laps.Length,
            first,
            representative,
            Math.Max(0, TheilSenSlope(laps)),
            RobustSpread(laps.Select(lap => lap.Seconds)),
            null);
    }

    private static EstateStrategySample CreatePitSample(ActiveTest test) => new(
        test.Id,
        test.Track,
        EstateStrategySampleKind.PitStop,
        EstateStrategySampleSource.PracticePitSimulation,
        DateTimeOffset.UtcNow,
        test.Vehicle,
        0,
        null,
        null,
        null,
        null,
        test.PitElapsedMaximum);

    private void Finish(
        EstatePracticeTestStatus status,
        string message,
        EstateStrategySample? sample)
    {
        if (active is null) return;
        var kind = active.Kind;
        var hudVisibleUntil = status is EstatePracticeTestStatus.Completed or EstatePracticeTestStatus.Failed
            ? DateTimeOffset.UtcNow + TerminalHudDuration
            : (DateTimeOffset?)null;
        results[kind] = new TestResult(status, message.Trim(), hudVisibleUntil);
        if (sample is not null) pendingSamples.Enqueue(sample);
        active = null;
        driverInterventionDetected = false;
        Current = BuildPanel();
    }

    private void FinishAutomatically(EstatePracticeTestStatus status, string message)
    {
        if (active is null) return;
        var partialSample = CreatePartialSample(active);
        var dataMessage = partialSample is { LapCount: > 0 }
            ? $"已保存终止前的 {partialSample.LapCount} 个完整干净圈；异常圈和未完成阶段未写入样本。"
            : "终止前尚未形成可用于策略计算的完整数据。";
        Finish(status, $"{message.Trim()} {dataMessage}", partialSample);
    }

    private EstateStrategySample? CreatePartialSample(ActiveTest test) =>
        test.Kind == EstatePracticeTestKind.LongRun && test.Laps.Count > 0
            ? CreateStintSample(test)
            : null;

    private EstatePracticeTestPanelState BuildPanel() => new(
        true,
        active?.Kind,
        Enum.GetValues<EstatePracticeTestKind>().Select(BuildItem).ToArray(),
        storedSampleCount);

    private EstatePracticeTestItemState BuildItem(EstatePracticeTestKind kind)
    {
        var metadata = Metadata(kind, currentLongRunTargetLaps);
        if (active?.Kind == kind)
        {
            return new EstatePracticeTestItemState(
                kind,
                metadata.Title,
                metadata.Description,
                EstatePracticeTestStatus.Active,
                Guidance(active),
                Progress(active),
                Target(active));
        }
        if (results.TryGetValue(kind, out var result))
        {
            return new EstatePracticeTestItemState(
                kind,
                metadata.Title,
                metadata.Description,
                result.Status,
                TerminalGuidance(result),
                result.Status == EstatePracticeTestStatus.Completed ? TargetFor(kind) : 0,
                TargetFor(kind),
                result.Message,
                result.HudVisibleUntil);
        }
        return new EstatePracticeTestItemState(
            kind,
            metadata.Title,
            metadata.Description,
            EstatePracticeTestStatus.Ready,
            metadata.ReadyGuidance,
            0,
            TargetFor(kind));
    }

    private static string TerminalGuidance(TestResult result) => result.Status switch
    {
        EstatePracticeTestStatus.Completed =>
            $"项目成功：{result.Message} 请返回维修区，等待下一项安排。",
        EstatePracticeTestStatus.Failed =>
            $"项目失败：{result.Message} 请返回维修区，确认车辆和赛道状态后可重新开始。",
        _ => result.Message
    };

    private static (string Title, string Description, string ReadyGuidance) Metadata(
        EstatePracticeTestKind kind,
        int longRunTargetLaps) => kind switch
    {
        EstatePracticeTestKind.LongRun => (
            "长距离轮胎管理",
            $"连续完成 {longRunTargetLaps} 个干净圈，建立本车在本赛道的配速衰退样本。",
            "开始后先完整通过一次终点线，随后连续跑完系统指定圈数。"),
        EstatePracticeTestKind.PitStopSimulation => (
            "进站换胎模拟",
            "从维修区驶出，完成出站圈后立即进站并完成一次模拟换胎。",
            "系统会检查维修区超速、处罚、出站圈和完整维修区用时。"),
        _ => (
            "排位赛模拟",
            "完成准备圈后跑一个有效飞驰圈，熟悉排位流程并留下单圈基线。",
            "开始后先跑完准备圈；下一圈才是需要完成的飞驰圈。")
    };

    private static string Guidance(ActiveTest test) => test.Kind switch
    {
        EstatePracticeTestKind.LongRun when test.Stage == TestStage.Arming =>
            "准备圈：正常通过终点线后开始计算长距离圈数。",
        EstatePracticeTestKind.LongRun when test.Stage == TestStage.AwaitingPitExit =>
            "先驶出维修区，正常完成准备圈后开始长距离测试。",
        EstatePracticeTestKind.LongRun =>
            $"长距离进行中：已完成 {test.Laps.Count}/{test.LongRunTargetLaps} 圈。不要进站、暂停、回转或越过赛道边界。",
        EstatePracticeTestKind.PitStopSimulation when test.Stage == TestStage.AwaitingPitEntry =>
            "先正常进入维修区，再从出口驶出。",
        EstatePracticeTestKind.PitStopSimulation when test.Stage == TestStage.AwaitingPitExit =>
            "从维修区出口驶出，出站圈即将开始。",
        EstatePracticeTestKind.PitStopSimulation when test.Stage == TestStage.OutLap =>
            "完成当前出站圈并立即进站；若终点位于维修区路径中，可先越过入口线再通过终点。",
        EstatePracticeTestKind.PitStopSimulation when test.Stage == TestStage.OutLapInPit =>
            "已进入本圈的维修区路径；继续通过维修区内终点线并完成模拟换胎。",
        EstatePracticeTestKind.PitStopSimulation when test.Stage == TestStage.ReturnToPit =>
            "现在立即进入维修区，不要再完成一圈。",
        EstatePracticeTestKind.PitStopSimulation when test.Stage == TestStage.Servicing =>
            "停入换胎区并完成模拟换胎，期间不要超速或接受处罚。",
        EstatePracticeTestKind.PitStopSimulation =>
            "换胎已完成，保持限速并驶出维修区出口。",
        EstatePracticeTestKind.QualifyingSimulation when test.Stage == TestStage.AwaitingPitExit =>
            "先驶出维修区，完成一个准备圈。",
        EstatePracticeTestKind.QualifyingSimulation when test.Stage == TestStage.Arming =>
            "准备圈：正常通过终点线，下一圈开始飞驰。",
        EstatePracticeTestKind.QualifyingSimulation =>
            "飞驰圈进行中：完成一个有效圈，不要暂停、回转、进站或越过赛道边界。",
        _ => string.Empty
    };

    private static int Progress(ActiveTest test) => test.Kind switch
    {
        EstatePracticeTestKind.LongRun => test.Laps.Count,
        EstatePracticeTestKind.PitStopSimulation => test.Stage switch
        {
            TestStage.AwaitingPitEntry => 0,
            TestStage.AwaitingPitExit => 1,
            TestStage.OutLap => 2,
            TestStage.OutLapInPit => 3,
            TestStage.ReturnToPit => 3,
            TestStage.Servicing => 4,
            TestStage.FinalPitExit => 5,
            _ => 0
        },
        EstatePracticeTestKind.QualifyingSimulation => test.Stage == TestStage.FlyingLap ? 1 : 0,
        _ => 0
    };

    private int Target(ActiveTest test) => test.Kind == EstatePracticeTestKind.LongRun
        ? test.LongRunTargetLaps
        : TargetFor(test.Kind);
    private int TargetFor(EstatePracticeTestKind kind) => kind switch
    {
        EstatePracticeTestKind.LongRun => currentLongRunTargetLaps,
        EstatePracticeTestKind.PitStopSimulation => 6,
        EstatePracticeTestKind.QualifyingSimulation => 2,
        _ => 1
    };

    internal static int CalculateLongRunTargetLaps(EstateRaceTrackContext context)
    {
        var referenceLapSeconds = context.Definition.ReferenceLapSeconds;
        if (double.IsFinite(referenceLapSeconds) && referenceLapSeconds is >= 20 and <= 1_800)
            return Math.Clamp(
                (int)Math.Round(LongRunTargetSeconds / referenceLapSeconds, MidpointRounding.AwayFromZero),
                MinimumLongRunLaps,
                MaximumLongRunLaps);

        var lengthMeters = context.Track.LengthMeters;
        if (!double.IsFinite(lengthMeters) || lengthMeters <= 0) return 10;
        return Math.Clamp(
            (int)Math.Round(LongRunFallbackDistanceMeters / lengthMeters, MidpointRounding.AwayFromZero),
            MinimumLongRunLaps,
            MaximumLongRunLaps);
    }

    private static EstateStrategyTrackIdentity TrackIdentity(
        EstateRaceSession session,
        EstateRaceTrackContext context) => new(
        session.TrackId ?? context.Definition.TrackId.ToString("D"),
        session.TrackRevision ?? context.Definition.MapRevision,
        session.TrackPackageHash ?? context.TrackPackageHash ?? string.Empty);

    private static HashSet<Guid> ActivePenaltyIds(EstateRaceParticipant participant) =>
        participant.Penalties.Where(penalty => !penalty.IsRevoked).Select(penalty => penalty.Id).ToHashSet();

    private static string FormatLap(double seconds) =>
        $"{(int)(seconds / 60)}:{seconds % 60:00.000}";

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

    private static double TheilSenSlope(IReadOnlyList<PracticeLap> laps)
    {
        var slopes = new List<double>();
        for (var left = 0; left < laps.Count - 1; left++)
        for (var right = left + 1; right < laps.Count; right++)
        {
            var distance = laps[right].LapNumber - laps[left].LapNumber;
            if (distance > 0) slopes.Add((laps[right].Seconds - laps[left].Seconds) / distance);
        }
        return slopes.Count == 0 ? 0 : Math.Clamp(Median(slopes), -2, 5);
    }

    private enum TestStage
    {
        Arming,
        Collecting,
        AwaitingPitEntry,
        AwaitingPitExit,
        OutLap,
        OutLapInPit,
        ReturnToPit,
        Servicing,
        FinalPitExit,
        FlyingLap
    }

    private sealed class ActiveTest
    {
        public ActiveTest(
            Guid id,
            EstatePracticeTestKind kind,
            TestStage stage,
            EstateStrategyTrackIdentity track,
            VehicleProfileFingerprint vehicle,
            Guid? lastLapEventId,
            int completedLaps,
            int warningBaseline,
            HashSet<Guid> penaltyBaseline,
            int serviceBaseline,
            bool wasInPitLane,
            int longRunTargetLaps)
        {
            Id = id;
            Kind = kind;
            Stage = stage;
            Track = track;
            Vehicle = vehicle;
            LastLapEventId = lastLapEventId;
            OutLapStartCompletedLaps = completedLaps;
            OutLapCompletedLaps = completedLaps;
            WarningBaseline = warningBaseline;
            PenaltyBaseline = penaltyBaseline;
            ServiceBaseline = serviceBaseline;
            WasInPitLane = wasInPitLane;
            LongRunTargetLaps = longRunTargetLaps;
        }

        public Guid Id { get; }
        public EstatePracticeTestKind Kind { get; }
        public TestStage Stage { get; set; }
        public EstateStrategyTrackIdentity Track { get; }
        public VehicleProfileFingerprint Vehicle { get; set; }
        public Guid? LastLapEventId { get; set; }
        public int WarningBaseline { get; }
        public HashSet<Guid> PenaltyBaseline { get; }
        public int ServiceBaseline { get; set; }
        public bool WasInPitLane { get; set; }
        public int OutLapStartCompletedLaps { get; set; }
        public int OutLapCompletedLaps { get; set; }
        public double PitElapsedMaximum { get; set; }
        public int LongRunTargetLaps { get; }
        public bool ServiceCompletedDuringPitVisit { get; set; }
        public List<PracticeLap> Laps { get; } = [];
    }

    private sealed record PracticeLap(int LapNumber, double Seconds, RaceGripCondition Grip);
    private sealed record TestResult(
        EstatePracticeTestStatus Status,
        string Message,
        DateTimeOffset? HudVisibleUntil);
}
