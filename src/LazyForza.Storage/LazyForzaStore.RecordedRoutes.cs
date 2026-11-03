using System.Text.Json;
using LazyForza.Domain;

namespace LazyForza.Storage;

public sealed partial class LazyForzaStore
{
    public IReadOnlyList<Guid> RemoveEmptyRecordedRoutes(string? source = null)
    {
        lock (lapLibraryGate)
        {
            var pending = LoadPendingLapCaptures(source).Where(capture => capture.ReferenceTrack is not null)
                .Select(capture => capture.ReferenceTrack!.Id).ToHashSet();
            var empty = ListTracks(source).Where(track => track.IsRecordOnly && track.Laps == 0 && !pending.Contains(track.Id))
                .Select(track => track.Id).ToArray();
            database.ExecuteTransaction(empty.Select(id => $"DELETE FROM TrackTemplates WHERE Id={Quote(id.ToString())};"));
            return empty;
        }
    }

    public void DeleteRecordedRoute(Guid trackId)
    {
        lock (lapLibraryGate)
        {
            if (LoadTrack(trackId) is not { Track.IsRecordOnly: true })
                throw new InvalidOperationException("只能删除未收录路线。");
            var checkpoints = LoadPendingLapCaptures(null).Where(capture => capture.ReferenceTrack?.Id == trackId || capture.Id == trackId);
            database.ExecuteTransaction(checkpoints.Select(capture => $"DELETE FROM PendingLapCaptures WHERE Id={Quote(capture.Id.ToString())};")
                .Concat([$"DELETE FROM Laps WHERE TrackId={Quote(trackId.ToString())};",
                    $"DELETE FROM TrackTemplates WHERE Id={Quote(trackId.ToString())};"]));
        }
    }

    /// <summary>Commit already-aligned records as one transaction; preserve IDs and all unrelated records.</summary>
    public void MoveRecordedLaps(TrackTemplate source, TrackTemplate destination, IReadOnlyList<LapRecord> aligned)
    {
        lock (lapLibraryGate)
        {
            if (LoadTrack(source.Id) is not { Track.IsRecordOnly: true } current ||
                LoadTrack(destination.Id) is not { } target || source.Id == destination.Id || source.Source != destination.Source ||
                target.Track.TimingKind != TrackTimingKind.GameEvent || current.Track.TimingKind != TrackTimingKind.GameEvent ||
                LapTrackRevision.Create(current.Track) != LapTrackRevision.Create(source) ||
                LapTrackRevision.Create(target.Track) != LapTrackRevision.Create(destination))
                throw new InvalidOperationException("路线已变化，请重新选择。");
            var original = LoadLapHistory(source.Id);
            var originalById = original.ToDictionary(lap => lap.Id);
            if (aligned.Count != original.Count || aligned.Select(lap => lap.Id).Distinct().Count() != original.Count ||
                !original.Select(lap => lap.Id).ToHashSet().SetEquals(aligned.Select(lap => lap.Id)) ||
                aligned.Any(lap => lap.TrackId != destination.Id || lap.TrackRevision != LapTrackRevision.Create(destination) &&
                    (lap.IsValid || lap.InvalidReason != "recording-route-mismatch" || lap.TrackRevision != originalById[lap.Id].TrackRevision)))
                throw new InvalidOperationException("圈记录已变化，请重新选择。");
            var references = LoadLapHistory(destination.Id).Where(lap => lap.Annotation.IsReference).ToList();
            var commands = new List<string>();
            foreach (var lap in aligned)
            {
                var selected = lap;
                if (lap.Annotation.IsReference && references.Any(reference => LapReferenceContext.Matches(reference, LapSummary.FromRecord(lap))))
                    selected = lap with { Annotation = lap.Annotation with { IsReference = false } };
                if (selected.Annotation.IsReference) references.Add(LapSummary.FromRecord(selected));
                commands.Add($"DELETE FROM Laps WHERE Id={Quote(selected.Id.ToString())};");
                commands.Add(BuildLapSql(selected));
            }
            commands.Add($"DELETE FROM TrackTemplates WHERE Id={Quote(source.Id.ToString())};");
            database.ExecuteTransaction(commands);
        }
    }

    public void SavePendingLapCapture(RecordedLapCapture capture) => database.Execute(
        $"INSERT INTO PendingLapCaptures(Id,Source,Payload) VALUES({Quote(capture.Id.ToString())}," +
        $"{Quote(TelemetryDataPartition.TrackSource(capture.Source))},{Quote(JsonSerializer.Serialize(capture))}) " +
        "ON CONFLICT(Id) DO UPDATE SET Payload=excluded.Payload;");

    public void DeletePendingLapCapture(Guid id) => database.Execute($"DELETE FROM PendingLapCaptures WHERE Id={Quote(id.ToString())};");

    public IReadOnlyList<RecordedLapCapture> LoadPendingLapCaptures(string? source)
    {
        var result = new List<RecordedLapCapture>();
        foreach (var row in database.QueryRows("SELECT Payload FROM PendingLapCaptures" +
                     (source is null ? ";" : $" WHERE Source={Quote(source)};")))
        {
            try
            {
                if (JsonSerializer.Deserialize<RecordedLapCapture>(row[0]!) is { Samples: not null } capture)
                    result.Add(capture);
            }
            catch (JsonException) { /* Keep unreadable checkpoints available for recovery. */ }
        }
        return result;
    }

    public void SaveRecordedLap(RecordedLapCapture capture, TrackTemplate track,
        IReadOnlyList<SectorDefinition> sectors, LapRecord lap)
    {
        lock (lapLibraryGate)
        {
            SavePendingLapCapture(capture);
            var commands = new List<string>();
            if (LoadLapsByIds([lap.Id]).Count == 0)
            {
                if (LoadTrack(track.Id) is not { } saved) commands.Add(BuildTrackSql(track, sectors));
                else if (LapTrackRevision.Create(saved.Track) != lap.TrackRevision)
                    throw new InvalidOperationException("记录的路线版本与本地路线不一致。");
                commands.Add(BuildLapSql(lap));
            }
            commands.Add($"DELETE FROM PendingLapCaptures WHERE Id={Quote(capture.Id.ToString())};");
            database.ExecuteTransaction(commands);
            PruneTrackLaps(track.Id);
        }
    }

    public bool EnrollRecordedRoute(Guid trackId)
    {
        lock (lapLibraryGate)
        {
            if (LoadTrack(trackId) is not { Track: { IsRecordOnly: true, TimingKind: TrackTimingKind.GameEvent } } saved)
                return false;
            if (!LoadLapHistory(trackId).Any(lap => lap.IsValid && lap.TrackRevision == LapTrackRevision.Create(saved.Track)))
                throw new InvalidOperationException("没有可用于收录的完整圈。");
            // Enrollment is library metadata, not a geometry revision.
            database.Execute($"UPDATE TrackTemplates SET IsRecordOnly=0 WHERE Id={Quote(trackId.ToString())};");
            return true;
        }
    }
}
