using System.Globalization;
using System.Text.Json;
using LazyForza.Domain;

namespace LazyForza.Storage;

public sealed record LapStorageUsage(int Laps, long EstimatedLapBytes, long DatabaseBytes, long ReusableBytes);

public sealed partial class LazyForzaStore
{
    public const int DefaultLapCapacity = 500;
    public const int MaximumLapCapacity = 10000;
    public const string LapCapacitySettingKey = "lapLibrary.capacityPerTrack";
    private readonly object lapLibraryGate = new();
    private int lapCapacity = DefaultLapCapacity;

    public int LapCapacity => Volatile.Read(ref lapCapacity);

    private void RefreshLapCapacity()
    {
        var saved = database.QueryText("SELECT name FROM sqlite_master WHERE type='table' AND name='AppSettings';") is null
            ? null : GetAppSetting(LapCapacitySettingKey);
        Volatile.Write(ref lapCapacity, int.TryParse(saved, out var value) && value is >= 1 and <= MaximumLapCapacity ? value : DefaultLapCapacity);
    }

    /// <summary>Applies to subsequent saves; changing a preference alone never deletes existing records.</summary>
    public void SetLapCapacity(int value)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value, MaximumLapCapacity);
        SetAppSetting(LapCapacitySettingKey, value.ToString(CultureInfo.InvariantCulture));
        Volatile.Write(ref lapCapacity, value);
    }

    public void UpdateLapAnnotation(Guid lapId, LapAnnotation annotation)
    {
        lock (lapLibraryGate)
        {
            var track = database.QueryText($"SELECT TrackId FROM Laps WHERE Id={Quote(lapId.ToString())};")
                ?? throw new InvalidOperationException("圈记录已不存在。");
            var laps = LoadLapHistory(Guid.Parse(track));
            var selected = laps.Single(lap => lap.Id == lapId);
            var normalized = LapAnnotation.Normalize(annotation);
            if (normalized.IsReference && (!selected.IsValid || !double.IsFinite(selected.TotalSeconds) || selected.TotalSeconds <= 0))
                throw new InvalidOperationException("仅有效完整圈可以固定为参考。");
            var commands = new List<string>();
            if (normalized.IsReference)
                foreach (var previous in laps.Where(lap => lap.Id != lapId && lap.Annotation.IsReference && LapReferenceContext.Matches(selected, lap)))
                    commands.Add(AnnotationSql(previous.Id, previous.Annotation with { IsReference = false }));
            commands.Add(AnnotationSql(lapId, normalized));
            database.ExecuteTransaction(commands);
        }
    }

    private static string AnnotationSql(Guid id, LapAnnotation annotation) =>
        $"UPDATE Laps SET Annotation={Quote(JsonSerializer.Serialize(annotation))} WHERE Id={Quote(id.ToString())};";

    private static LapAnnotation ReadLapAnnotation(string? json)
    {
        try { return LapAnnotation.Normalize(json is null ? null : JsonSerializer.Deserialize<LapAnnotation>(json)); }
        catch (JsonException) { return new(); }
    }

    public IReadOnlyList<Guid> PruneTrackLaps(Guid trackId, int? maximum = null)
    {
        lock (lapLibraryGate)
        {
            var capacity = maximum ?? LapCapacity;
            ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
            var laps = LoadLapHistory(trackId);
            if (laps.Count <= capacity) return [];
            var keep = SelectRetainedLapIds(laps, capacity);
            var removed = laps.Where(lap => !keep.Contains(lap.Id)).Select(lap => lap.Id).ToArray();
            if (removed.Length > 0)
                database.ExecuteTransaction(removed.Select(id => $"DELETE FROM Laps WHERE Id={Quote(id.ToString())};"));
            return removed;
        }
    }

    /// <summary>Soft lap budget: keep protected and newest sessions whole, then fill with recent sessions.</summary>
    public static HashSet<Guid> SelectRetainedLapIds(IReadOnlyList<LapSummary> laps, int maximum = DefaultLapCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximum, 1);
        if (laps.Count <= maximum) return laps.Select(lap => lap.Id).ToHashSet();
        var sessions = laps.GroupBy(LapSessionKey.FromLap)
            .OrderByDescending(group => group.Max(lap => lap.StartedAt)).ThenBy(group => group.Key.SessionId).ToArray();
        var protectedIds = RepresentativeLapIds(laps);
        protectedIds.UnionWith(laps.Where(lap => lap.Annotation.IsFavorite || lap.Annotation.IsReference).Select(lap => lap.Id));
        var keep = sessions.Where(group => group == sessions[0] || group.Any(lap => protectedIds.Contains(lap.Id)))
            .SelectMany(group => group).Select(lap => lap.Id).ToHashSet();
        foreach (var session in sessions)
        {
            if (keep.Count >= maximum) break;
            keep.UnionWith(session.Select(lap => lap.Id));
        }
        return keep;
    }

    public static HashSet<Guid> RepresentativeLapIds(IEnumerable<LapSummary> laps)
    {
        // Compare measured configurations conservatively. Unresolved and resolved signatures never
        // replace one another; this does not claim to identify an actual in-game tune or weather.
        var result = new HashSet<Guid>();
        foreach (var group in laps.Where(lap => lap.IsValid && double.IsFinite(lap.TotalSeconds) && lap.TotalSeconds > 0)
                     .GroupBy(lap => (lap.TrackId, lap.Direction, lap.SectorSchemaVersion, lap.TrackRevision,
                         PlayerIdentitySettings.Normalize(lap.PlayerCode), lap.Vehicle.CarOrdinal, lap.Vehicle.CarClass,
                         lap.Vehicle.PerformanceIndex, lap.Vehicle.DrivetrainType, lap.Vehicle.NumCylinders)))
        {
            var representatives = new List<LapSummary>();
            foreach (var lap in group.OrderBy(lap => lap.TotalSeconds).ThenBy(lap => lap.StartedAt).ThenBy(lap => lap.Id))
            {
                if (representatives.Any(existing => VehicleTuneCompatibility.AreCompatible(existing.Vehicle, lap.Vehicle))) continue;
                representatives.Add(lap);
                result.Add(lap.Id);
            }
        }
        return result;
    }

    public LapStorageUsage GetLapStorageUsage()
    {
        // Logical payload estimate excludes SQLite record/index overhead and free pages. Scan in SQL
        // without materializing sample arrays; page counts separately show actual database allocation.
        var values = database.QueryRowsSnapshot([
            "SELECT COUNT(*),COALESCE(SUM(200+COALESCE(length(CAST(VehicleSnapshot AS BLOB)),0)+COALESCE(length(CAST(SessionInfo AS BLOB)),0)+COALESCE(length(CAST(Annotation AS BLOB)),0)),0) FROM Laps;",
            "SELECT COALESCE(SUM(125+COALESCE(length(Dynamics),0)),0) FROM LapSamples;",
            "SELECT COUNT(*)*56 FROM LapSegments;",
            "PRAGMA page_size;", "PRAGMA page_count;", "PRAGMA freelist_count;"
        ]);
        long Number(int table, int column = 0) => long.Parse(values[table][0][column] ?? "0", CultureInfo.InvariantCulture);
        return new(checked((int)Number(0)), Number(0, 1) + Number(1) + Number(2), Number(3) * Number(4), Number(3) * Number(5));
    }
}
