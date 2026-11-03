using LazyForza.Analysis;
using LazyForza.Domain;

namespace LazyForza.Modules.LapAnalysis;

public sealed partial class LapAnalysisModule
{
    public const string RecordUnmatchedTracksSettingKey = "lap.recordUnmatchedTracks";
    private bool recordUnmatchedTracks;
    private UnmatchedLapCapture? unmatchedCapture;
    private Guid? recordedSessionTrackId;
    private int recordedRoutesVersion;
    private readonly System.Collections.Concurrent.ConcurrentQueue<Guid> pendingEnrolledTracks = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, (TrackTemplate Track, IReadOnlyList<SectorDefinition> Sectors)> recordedReferences = new();
    private readonly object recordedRouteManagementGate = new();
    private bool managingRecordedRoutes;
    public int RecordedRoutesVersion => Volatile.Read(ref recordedRoutesVersion);
    public string? PersistenceFailure => persistence.FailureMessage;
    public bool RecordUnmatchedTracks => Volatile.Read(ref recordUnmatchedTracks);

    public void SetRecordUnmatchedTracks(bool enabled)
    {
        lock (recordedRouteManagementGate)
        {
            if (RecordUnmatchedTracks == enabled) return;
            // Enrollment may already have completed while its matcher update is still queued.
            ApplyEnrolledTrack();
            store.SetAppSetting(RecordUnmatchedTracksSettingKey, enabled.ToString());
            Volatile.Write(ref recordUnmatchedTracks, enabled);
            if (!enabled && (unmatchedCapture?.HasReference != true || track?.IsRecordOnly != false))
                DiscardUnmatchedCapture();
            if (!enabled && competitionActive && track?.IsRecordOnly == true && recordedSessionTrackId == track.Id)
            {
                recordedSessionTrackId = null;
                track = null;
                trackSpatialIndex = null;
                sectors = [];
                sectorStartTimes = [];
                completedSectorTimes = [];
                currentSamples.Clear();
                lapArmed = false;
                automaticMatchLocked = false;
                automaticMatchStarted = false;
                automaticMatchRejected = true;
                heldComparisons = null;
                ClearCumulativeHistoricalDeltaDisplay();
            }
            if (competitionActive && track is null && !forceTrackLearning &&
                (!automaticMatchStarted || automaticMatchRejected) && lastCompetitionFrame is { } frame)
                PublishAutomaticMatchRejected(frame);
        }
    }

    private void ObserveUnmatchedCapture(TelemetryFrame frame, bool crossed, bool rewound, bool lastLapUpdated)
    {
        if (!RecordUnmatchedTracks)
        {
            track = null;
            trackSpatialIndex = null;
            sectors = [];
            if (!automaticMatchStarted || automaticMatchRejected) PublishAutomaticMatchRejected(frame);
            return;
        }
        if (rewound)
        {
            FinishUnmatchedCapture(false, "recording-rewind");
            crossed = false;
        }
        if (unmatchedCapture is { Count: > 0 } pending && (crossed ||
            lastLapUpdated && frame.Raw.LastLap != pending.StartingLastLap &&
            Math.Abs(frame.Raw.LastLap - pending.LastTime) <= 1.5 && pending.LastTime >= 10))
        {
            var total = frame.Raw.LastLap >= pending.LastTime && frame.Raw.LastLap - pending.LastTime <= 1.5
                ? frame.Raw.LastLap : ResolveCompletedLapTime(frame.Raw, pending.LastTime).Seconds;
            pending.Observe(frame, total);
            var result = FinishUnmatchedCapture(true);
            if (result is { Lap.IsValid: true })
            {
                ActivateRecordedRoute(result);
                if (result.Track.LayoutKind == TrackLayoutKind.Circuit && crossed)
                {
                    BeginLap(frame.Raw.CurrentLap, frame.Raw.LastLap, frame.ArrivalTime);
                    lapArmed = true;
                    ObserveRecordedRouteCapture(frame, false, true);
                }
                else lapArmed = false;
                waitingForInitialStartLine = false;
                PublishComparison(frame, TrackMatchState.Confirmed, 0, true);
                return;
            }
        }
        unmatchedCapture ??= new(frame, sessionId, PlayerIdentitySettings.Normalize(playerCodeProvider()), crossed);
        if (unmatchedCapture.Observe(frame))
            QueuePersistence(default(LapPersistenceCommand) with { Capture = unmatchedCapture.Snapshot() });
        track = null;
        trackSpatialIndex = null;
        sectors = [];
        var matching = automaticMatchStarted && !automaticMatchRejected;
        PublishCompetitionState(new(frame.ArrivalTime, frame.Source, true,
            matching ? TrackLearningPhase.MatchingTrack : TrackLearningPhase.CapturingReferenceLap,
            "赛道未识别，正在记录。", "完成一圈后可分析或收录路线。", TrackMatchState.Unknown,
            0, matching ? "正在识别赛事" : "未识别赛事", 0, [], 0, VisibleLapCount, false));
    }

    private void ObserveRecordedRouteCapture(TelemetryFrame frame, bool rewound, bool startConfirmed)
    {
        if (track is null || track.Id != recordedSessionTrackId || !lapArmed) return;
        if (track.IsRecordOnly && !RecordUnmatchedTracks) return;
        if (rewound) FinishUnmatchedCapture(false, "recording-rewind");
        unmatchedCapture ??= new(frame, sessionId, PlayerIdentitySettings.Normalize(playerCodeProvider()),
            startConfirmed && !rewound, track, sectors);
        if (unmatchedCapture.Observe(frame))
            QueuePersistence(default(LapPersistenceCommand) with { Capture = unmatchedCapture.Snapshot() });
    }

    private LapRecord? CompleteRecordedLap(TelemetryFrame frame)
    {
        if (unmatchedCapture is not { HasReference: true } pending) return null;
        pending.Observe(frame, ResolveCompletedLapTime(frame.Raw, pending.LastTime).Seconds);
        var result = FinishUnmatchedCapture(true);
        if (result is null) return null;
        RegisterVisibleLap(result.Lap);
        return result.Lap;
    }

    private RecordedRouteResult? FinishUnmatchedCapture(bool complete, string? reason = null)
    {
        if (unmatchedCapture is not { } pending) return null;
        unmatchedCapture = null;
        var captureSnapshot = pending.Snapshot(reason) with { FinishConfirmed = complete };
        var result = RecordedRouteAnalysis.Build(captureSnapshot, complete);
        if (result is null)
        {
            QueuePersistence(default(LapPersistenceCommand) with { DiscardCaptureId = pending.Id });
            return null;
        }
        if (!pending.HasReference && result.Lap.IsValid)
        {
            var match = FindRecordedReference(result.Track);
            if (match is { } existing)
            {
                var aligned = RecordedRouteAssociation.Reproject(result.Lap, result.Track, existing.Track, existing.Sectors,
                    RecordedRouteAssociation.Compare(result.Track, existing.Track));
                if (aligned.IsValid) result = result with { Track = existing.Track, Sectors = existing.Sectors, Lap = aligned };
            }
            if (result.Lap.IsValid) recordedReferences[result.Track.Id] = (result.Track, result.Sectors);
        }
        QueuePersistence(LapPersistenceCommand.Save(result.Lap) with
        { Capture = captureSnapshot, NewTrack = pending.HasReference ? null : result.Track, NewSectors = result.Sectors });
        if (!complete && track?.Id == result.Track.Id) RegisterVisibleLap(result.Lap);
        Interlocked.Increment(ref recordedRoutesVersion);
        return result;
    }

    private void ActivateRecordedRoute(RecordedRouteResult result)
    {
        track = result.Track;
        recordedSessionTrackId = track.Id;
        sectors = result.Sectors;
        trackSpatialIndex = new CompatibleTrack(track, sectors).RouteSpatialIndex;
        sectorStartTimes = new double[sectors.Count];
        completedSectorTimes = new double?[sectors.Count];
        automaticMatchLocked = true;
        automaticMatchRejected = false;
        automaticMatchStarted = false;
        automaticMatchFrames.Clear();
        automaticMatchCandidates.Clear();
        currentTrackGeometryPlausible = true;
        trackMatchEverPlausible = true;
        confidentProjectionCount = 30;
        ReloadVisibleLaps();
        RegisterVisibleLap(result.Lap);
    }

    private (TrackTemplate Track, IReadOnlyList<SectorDefinition> Sectors)? FindRecordedReference(TrackTemplate source) =>
        recordedReferences.Values.Where(saved => saved.Track.Id != source.Id && saved.Track.Source == source.Source &&
                saved.Track.LayoutKind == source.LayoutKind &&
                Math.Abs(saved.Track.LengthMeters - source.LengthMeters) / Math.Max(1, source.LengthMeters) <= .07 &&
                saved.Track.Points[0].DistanceSquaredTo(source.Points[0]) <= 40 * 40)
            .Select(saved => (Saved: saved, Match: RecordedRouteAssociation.Compare(source, saved.Track)))
            .Where(candidate => candidate.Match.CanAutoMerge)
            .OrderBy(candidate => candidate.Match.MeanDistanceMeters)
            .Select(candidate => ((TrackTemplate Track, IReadOnlyList<SectorDefinition> Sectors)?)candidate.Saved).FirstOrDefault();

    private void ReconcileRecordedRoutes()
    {
        store.RemoveEmptyRecordedRoutes(expectedTrackSource);
        recordedReferences.Clear();
        foreach (var summary in store.ListTracks(expectedTrackSource).Where(summary => summary.IsRecordOnly && summary.Laps > 0)
                     .OrderBy(summary => store.LoadTrack(summary.Id)!.Value.Track.CreatedAt))
        {
            if (store.LoadTrack(summary.Id) is not { } saved || !IsAnalysisCompatible(saved.Track, saved.Sectors) ||
                saved.Track.Points.Count < 4 || saved.Track.TimingKind != TrackTimingKind.GameEvent) continue;
            var originals = store.LoadLapHistory(saved.Track.Id);
            var revision = LapTrackRevision.Create(saved.Track);
            if (!originals.Any(lap => lap.IsValid) || originals.Any(lap => lap.IsValid && lap.TrackRevision != revision)) continue;
            var existing = FindRecordedReference(saved.Track);
            if (existing is null) { recordedReferences[saved.Track.Id] = saved; continue; }
            var match = RecordedRouteAssociation.Compare(saved.Track, existing.Value.Track);
            var aligned = store.LoadLapsByIds(originals.Select(lap => lap.Id).ToArray())
                .Select(lap => RecordedRouteAssociation.Reproject(lap, saved.Track, existing.Value.Track, existing.Value.Sectors, match)).ToArray();
            // Do not automatically degrade a complete record when its samples cannot align to the older route.
            if (aligned.Any(lap => !lap.IsValid && originals.Any(original => original.Id == lap.Id && original.IsValid)))
            { recordedReferences[saved.Track.Id] = saved; continue; }
            try { store.MoveRecordedLaps(saved.Track, existing.Value.Track, aligned); }
            catch (InvalidOperationException exception)
            {
                recordedReferences[saved.Track.Id] = saved;
                RecordDiagnostic("lap.route-merge-failed", exception.Message, true, DateTimeOffset.UtcNow);
            }
        }
    }

    public Task DeleteRecordedRouteAsync(Guid trackId) => ManageRecordedRoutesAsync(() =>
    {
        store.DeleteRecordedRoute(trackId);
        recordedReferences.TryRemove(trackId, out _);
        if (track?.Id == trackId) ClearTrackSelection();
    });

    public Task AssociateRecordedRouteAsync(Guid sourceId, Guid targetId, bool force) => ManageRecordedRoutesAsync(() =>
    {
        var source = store.LoadTrack(sourceId) ?? throw new InvalidOperationException("路线已不存在。");
        var target = store.LoadTrack(targetId) ?? throw new InvalidOperationException("赛道已不存在。");
        if (!source.Track.IsRecordOnly || target.Track.IsRecordOnly || !IsCompatible(target.Track, target.Sectors))
            throw new InvalidOperationException("请选择已收录的普通赛道。");
        var history = store.LoadLapHistory(sourceId);
        var match = RecordedRouteAssociation.Compare(source.Track, target.Track, partial: !history.Any(lap => lap.IsValid));
        if (match.IsSevereMismatch && !force) throw new InvalidOperationException("路线与目标赛道不匹配。");
        var aligned = store.LoadLapsByIds(history.Select(lap => lap.Id).ToArray())
            .Select(lap => RecordedRouteAssociation.Reproject(lap, source.Track, target.Track, target.Sectors, match)).ToArray();
        store.MoveRecordedLaps(source.Track, target.Track, aligned);
        recordedReferences.TryRemove(sourceId, out _);
        if (track?.Id == sourceId) SelectTrack(targetId);
    });

    private async Task ManageRecordedRoutesAsync(Action action)
    {
        lock (recordedRouteManagementGate)
        {
            if (competitionActive || managingRecordedRoutes) throw new InvalidOperationException("请在比赛结束后管理路线记录。");
            managingRecordedRoutes = true;
        }
        try
        {
            await FlushPendingLapsAsync();
            await Task.Run(action);
            Interlocked.Increment(ref recordedRoutesVersion);
        }
        finally { lock (recordedRouteManagementGate) managingRecordedRoutes = false; }
    }

    private void DiscardUnmatchedCapture()
    {
        if (unmatchedCapture is not { } pending) return;
        unmatchedCapture = null;
        QueuePersistence(default(LapPersistenceCommand) with { DiscardCaptureId = pending.Id });
    }

    private void RecoverUnmatchedCaptures()
    {
        foreach (var captureSnapshot in store.LoadPendingLapCaptures(expectedTrackSource))
        {
            try
            {
                var result = RecordedRouteAnalysis.Build(captureSnapshot, captureSnapshot.FinishConfirmed);
                if (result is not null)
                    store.SaveRecordedLap(captureSnapshot, result.Track, result.Sectors, result.Lap);
                else store.DeletePendingLapCapture(captureSnapshot.Id);
            }
            catch (Exception exception)
            {
                RecordDiagnostic("lap.recovery-failed", $"圈速恢复失败：{exception.Message}", true, DateTimeOffset.UtcNow);
            }
        }
    }

    public async Task<bool> EnrollRecordedRouteAsync(Guid trackId)
    {
        await FlushPendingLapsAsync();
        if (!store.EnrollRecordedRoute(trackId)) return false;
        // Apply matcher changes under the same gate as telemetry processing and recording preferences.
        pendingEnrolledTracks.Enqueue(trackId);
        Interlocked.Increment(ref recordedRoutesVersion);
        return true;
    }

    private void ApplyEnrolledTrack()
    {
        while (pendingEnrolledTracks.TryDequeue(out var id))
        {
            if (store.LoadTrack(id) is not { } saved) continue;
            compatibleTracks.RemoveAll(candidate => candidate.Track.Id == id);
            compatibleTracks.Add(new(saved.Track, saved.Sectors));
            recordedReferences.TryRemove(id, out _);
            if (track?.Id == id) track = saved.Track;
        }
    }
}
