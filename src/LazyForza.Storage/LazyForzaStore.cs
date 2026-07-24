using System.Globalization;
using System.Text;
using LazyForza.Domain;
using LazyForza.Modules.Abstractions;

namespace LazyForza.Storage;

public sealed record TrackSummary(
    Guid Id,
    string Name,
    string Source,
    double Length,
    int Laps,
    TrackLayoutKind LayoutKind,
    TrackCatalogKind CatalogKind,
    string? Category);

public sealed record VehicleProfileSummary(
    string Id,
    string? CustomName,
    VehicleProfileFingerprint Fingerprint,
    LearningState State,
    double Confidence,
    DateTimeOffset UpdatedAt,
    int CurveBins,
    int Gears,
    int ShiftTargets,
    bool ShiftRecommendationsEnabled);

public sealed class LazyForzaStore : IModuleSettingsStore, IAnalysisStore, IDisposable
{
    private const int CurrentSchemaVersion = 8;
    public const int MaxLapsPerTrack = 50;
    private readonly WinSqliteDatabase database;
    private bool disposed;

    public LazyForzaStore(string databasePath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        database = new WinSqliteDatabase(databasePath);
        Migrate();
    }

    public int SchemaVersion => int.Parse(database.QueryText("SELECT Version FROM SchemaVersion LIMIT 1;") ?? "0", CultureInfo.InvariantCulture);

    public ValueTask<string?> GetAsync(string moduleId, string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var value = database.QueryText($"SELECT Value FROM ModuleSettings WHERE ModuleId={Quote(moduleId)} AND Key={Quote(key)} LIMIT 1;");
        return ValueTask.FromResult(value);
    }

    public ValueTask SetAsync(string moduleId, string key, string value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        database.Execute("BEGIN IMMEDIATE;\n" +
            $"INSERT INTO ModuleSettings(ModuleId,Key,Value,UpdatedAt) VALUES({Quote(moduleId)},{Quote(key)},{Quote(value)},{Quote(DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture))}) " +
            "ON CONFLICT(ModuleId,Key) DO UPDATE SET Value=excluded.Value, UpdatedAt=excluded.UpdatedAt;\nCOMMIT;");
        return ValueTask.CompletedTask;
    }

    public void SetAppSetting(string key, string value) => database.Execute(
        $"INSERT INTO AppSettings(Key,Value,UpdatedAt) VALUES({Quote(key)},{Quote(value)},{Quote(DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture))}) " +
        "ON CONFLICT(Key) DO UPDATE SET Value=excluded.Value, UpdatedAt=excluded.UpdatedAt;");

    public string? GetAppSetting(string key) => database.QueryText($"SELECT Value FROM AppSettings WHERE Key={Quote(key)} LIMIT 1;");

    public ValueTask<string?> SaveShiftLearningAsync(
        ShiftLearningSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (snapshot.Fingerprint is null) return ValueTask.FromResult<string?>(null);
        if (!VehicleProfileIdentity.IsResolved(snapshot.Fingerprint))
            return ValueTask.FromResult<string?>(null);
        var fingerprint = FingerprintFromObservedModels(snapshot);

        var compatible = ListVehicleProfiles()
            .Where(profile =>
                VehicleTuneCompatibility.AreCompatible(profile.Fingerprint, fingerprint))
            .OrderByDescending(profile => !string.IsNullOrWhiteSpace(profile.CustomName))
            .ThenByDescending(profile => profile.CurveBins + profile.Gears + profile.ShiftTargets)
            .ThenByDescending(profile => profile.Confidence)
            .ThenByDescending(profile => profile.UpdatedAt)
            .FirstOrDefault();
        var idValue = compatible?.Id ?? VehicleProfileIdentity.Create(fingerprint);
        var storedFingerprint = compatible?.Fingerprint ?? fingerprint;
        var id = Quote(idValue);
        var sql = "BEGIN IMMEDIATE;\n" +
            $"INSERT INTO VehicleProfiles(Id,CarOrdinal,CarClass,PI,Drivetrain,Cylinders,MaxRpm,CurveSignature,GearSignature,State,Confidence,UpdatedAt,DisplayName,RecommendationsEnabled) VALUES(" +
            $"{id},{storedFingerprint.CarOrdinal},{storedFingerprint.CarClass},{storedFingerprint.PerformanceIndex},{storedFingerprint.DrivetrainType},{storedFingerprint.NumCylinders},{storedFingerprint.RoundedMaxRpm},{Quote(storedFingerprint.CurveSignature)},{Quote(storedFingerprint.GearSlopeSignature)},{Quote(snapshot.State.ToString())},{N(snapshot.Confidence)},{Quote(DateTimeOffset.UtcNow.ToString("O"))},NULL,1) " +
            "ON CONFLICT(Id) DO UPDATE SET State=excluded.State,Confidence=MAX(VehicleProfiles.Confidence,excluded.Confidence),UpdatedAt=excluded.UpdatedAt;\n";
        foreach (var bin in snapshot.Curve)
        {
            sql +=
                $"INSERT INTO EngineCurveBins(VehicleProfileId,RpmCenter,SampleCount,MedianPower,MedianTorque,MedianBoost,Deviation,Confidence) VALUES({id},{bin.RpmCenter},{bin.SampleCount},{N(bin.MedianPowerWatts)},{N(bin.MedianTorqueNm)},{N(bin.MedianBoostPsi)},{N(bin.MedianAbsoluteDeviation)},{N(bin.Confidence)}) " +
                "ON CONFLICT(VehicleProfileId,RpmCenter) DO UPDATE SET SampleCount=excluded.SampleCount,MedianPower=excluded.MedianPower,MedianTorque=excluded.MedianTorque,MedianBoost=excluded.MedianBoost,Deviation=excluded.Deviation,Confidence=excluded.Confidence " +
                "WHERE excluded.SampleCount>=EngineCurveBins.SampleCount;\n";
        }
        foreach (var gear in snapshot.Gears)
        {
            sql +=
                $"INSERT INTO GearModels(VehicleProfileId,Gear,Slope,SampleCount,Confidence) VALUES({id},{gear.Gear},{N(gear.RpmPerMeterPerSecond)},{gear.SampleCount},{N(gear.Confidence)}) " +
                "ON CONFLICT(VehicleProfileId,Gear) DO UPDATE SET Slope=excluded.Slope,SampleCount=excluded.SampleCount,Confidence=excluded.Confidence " +
                "WHERE excluded.SampleCount>=GearModels.SampleCount;\n";
        }
        foreach (var target in snapshot.Targets)
        {
            sql +=
                $"INSERT INTO ShiftTargets(VehicleProfileId,FromGear,ToGear,TargetRpm,CueRpm,AfterRpm,Confidence,AlgorithmVersion) VALUES({id},{target.FromGear},{target.ToGear},{N(target.TargetRpm)},{N(target.CueRpm)},{N(target.AfterShiftRpm)},{N(target.Confidence)},'shift-v1.0.0') " +
                "ON CONFLICT(VehicleProfileId,FromGear,ToGear) DO UPDATE SET TargetRpm=excluded.TargetRpm,CueRpm=excluded.CueRpm,AfterRpm=excluded.AfterRpm,Confidence=excluded.Confidence,AlgorithmVersion=excluded.AlgorithmVersion " +
                "WHERE excluded.Confidence>=ShiftTargets.Confidence;\n";
        }
        database.Execute(sql + "COMMIT;");
        RefreshVehicleProfileGearSignature(idValue);
        return ValueTask.FromResult<string?>(idValue);
    }

    public ValueTask<bool> GetShiftRecommendationsEnabledAsync(
        string vehicleProfileId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var value = database.QueryText(
            $"SELECT RecommendationsEnabled FROM VehicleProfiles WHERE Id={Quote(vehicleProfileId)} LIMIT 1;");
        return ValueTask.FromResult(value is null || value != "0");
    }

    public IReadOnlyList<VehicleProfileSummary> ListVehicleProfiles() =>
        database.QueryRows(
            "SELECT p.Id,p.DisplayName,p.CarOrdinal,p.CarClass,p.PI,p.Drivetrain,p.Cylinders,p.MaxRpm," +
            "p.GearSignature,p.CurveSignature,p.State,p.Confidence,p.UpdatedAt,p.RecommendationsEnabled," +
            "(SELECT COUNT(*) FROM EngineCurveBins b WHERE b.VehicleProfileId=p.Id)," +
            "(SELECT COUNT(*) FROM GearModels g WHERE g.VehicleProfileId=p.Id)," +
            "(SELECT COUNT(*) FROM ShiftTargets t WHERE t.VehicleProfileId=p.Id) " +
            "FROM VehicleProfiles p ORDER BY p.UpdatedAt DESC;")
        .Select(row => new VehicleProfileSummary(
            row[0] ?? string.Empty,
            string.IsNullOrWhiteSpace(row[1]) ? null : row[1],
            new VehicleProfileFingerprint(
                ParseInt(row[2]),
                PerformanceClassCatalog.Resolve(ParseInt(row[3]), ParseInt(row[4])),
                ParseInt(row[4]),
                ParseInt(row[5]),
                ParseInt(row[6]),
                ParseInt(row[7]),
                row[8] ?? VehicleProfileIdentity.PendingSignature,
                row[9] ?? VehicleProfileIdentity.PendingSignature),
            Enum.TryParse<LearningState>(row[10], true, out var state) ? state : LearningState.Error,
            ParseDouble(row[11]),
            DateTimeOffset.TryParse(row[12], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var updatedAt)
                ? updatedAt
                : DateTimeOffset.MinValue,
            ParseInt(row[14]),
            ParseInt(row[15]),
            ParseInt(row[16]),
            row[13] != "0"))
        .ToArray();

    public void RenameVehicleProfile(string vehicleProfileId, string name)
    {
        var normalized = name.Trim();
        if (normalized.Length is < 1 or > 80)
            throw new ArgumentOutOfRangeException(nameof(name), "车辆配置名称长度应为 1–80 个字符。");
        database.Execute(
            $"UPDATE VehicleProfiles SET DisplayName={Quote(normalized)} WHERE Id={Quote(vehicleProfileId)};");
    }

    public void SetShiftRecommendationsEnabled(string vehicleProfileId, bool enabled) =>
        database.Execute(
            $"UPDATE VehicleProfiles SET RecommendationsEnabled={(enabled ? 1 : 0)} WHERE Id={Quote(vehicleProfileId)};");

    public void DeleteVehicleProfile(string vehicleProfileId) =>
        database.Execute(
            $"DELETE FROM VehicleProfiles WHERE Id={Quote(vehicleProfileId)};");

    public int CountVehicleProfiles() => int.Parse(database.QueryText("SELECT COUNT(*) FROM VehicleProfiles;") ?? "0", CultureInfo.InvariantCulture);

    public void SaveTrack(TrackTemplate track, IReadOnlyList<SectorDefinition> sectors)
    {
        var trackId = Quote(track.Id.ToString());
        var sql = new StringBuilder(32_768)
            .Append("BEGIN IMMEDIATE;\n")
            .Append("INSERT INTO TrackTemplates(Id,Name,Direction,Source,GameBuild,LengthMeters,ToleranceMeters,Confidence,CaptureLapCount,CreatedAt,UpdatedAt,LayoutKind,CatalogKind,Category) VALUES(")
            .Append(trackId).Append(',').Append(Quote(track.Name)).Append(',').Append(track.Direction).Append(',')
            .Append(Quote(track.Source)).Append(',').Append(Quote(track.GameBuild)).Append(',')
            .Append(N(track.LengthMeters)).Append(',').Append(N(track.MatchingToleranceMeters)).Append(',')
            .Append(N(track.Confidence)).Append(',').Append(track.CaptureLapCount).Append(',')
            .Append(Quote(track.CreatedAt.ToString("O"))).Append(',').Append(Quote(track.UpdatedAt.ToString("O"))).Append(',')
            .Append(Quote(track.LayoutKind.ToString())).Append(',').Append(Quote(track.CatalogKind.ToString())).Append(',')
            .Append(Quote(track.Category)).Append(") ")
            .Append("ON CONFLICT(Id) DO UPDATE SET Name=excluded.Name,Direction=excluded.Direction,Source=excluded.Source,GameBuild=excluded.GameBuild,")
            .Append("LengthMeters=excluded.LengthMeters,ToleranceMeters=excluded.ToleranceMeters,Confidence=excluded.Confidence,")
            .Append("CaptureLapCount=excluded.CaptureLapCount,UpdatedAt=excluded.UpdatedAt,LayoutKind=excluded.LayoutKind,")
            .Append("CatalogKind=excluded.CatalogKind,Category=excluded.Category;\n")
            .Append("DELETE FROM TrackPoints WHERE TrackId=").Append(trackId).Append(";\n")
            .Append("DELETE FROM SectorDefinitions WHERE TrackId=").Append(trackId).Append(";\n");
        for (var index = 0; index < track.Points.Count; index++)
        {
            var point = track.Points[index];
            sql.Append("INSERT INTO TrackPoints(TrackId,PointIndex,X,Y,Z,S,TangentX,TangentZ) VALUES(")
                .Append(trackId).Append(',').Append(index).Append(',')
                .Append(N(point.X)).Append(',').Append(N(point.Y)).Append(',').Append(N(point.Z)).Append(',')
                .Append(N(point.S)).Append(',').Append(N(point.TangentX)).Append(',').Append(N(point.TangentZ))
                .Append(");\n");
        }

        foreach (var sector in sectors)
        {
            sql.Append("INSERT INTO SectorDefinitions(TrackId,SectorSchemaVersion,SectorIndex,StartS,EndS,FeatureType,AlgorithmVersion) VALUES(")
                .Append(trackId).Append(',').Append(sector.SectorSchemaVersion).Append(',').Append(sector.Index).Append(',')
                .Append(N(sector.StartS)).Append(',').Append(N(sector.EndS)).Append(',')
                .Append(Quote(sector.FeatureType.ToString())).Append(',').Append(Quote(sector.AlgorithmVersion))
                .Append(");\n");
        }

        database.Execute(sql.Append("COMMIT;").ToString());
    }

    public void SaveLap(LapRecord lap)
    {
        var performanceClass = PerformanceClassCatalog.Resolve(
            lap.Vehicle.CarClass,
            lap.Vehicle.PerformanceIndex);
        var vehicleKey = Quote($"{lap.Vehicle.CarOrdinal}:{lap.Vehicle.PerformanceIndex}:{lap.Vehicle.RoundedMaxRpm}");
        var sql = "BEGIN IMMEDIATE;\n" +
            $"INSERT INTO Sessions(Id,Source,StartedAt,RawRecordingPath) VALUES({Quote(lap.SessionId.ToString())},'Replay',{Quote(lap.StartedAt.ToString("O"))},NULL) ON CONFLICT(Id) DO NOTHING;\n" +
            $"INSERT INTO Laps(Id,TrackId,Direction,SectorSchemaVersion,SessionId,VehicleFingerprint,CarClass,PerformanceIndex,StartedAt,TotalSeconds,IsValid,InvalidReason) VALUES(" +
            $"{Quote(lap.Id.ToString())},{Quote(lap.TrackId.ToString())},{lap.Direction},{lap.SectorSchemaVersion},{Quote(lap.SessionId.ToString())},{vehicleKey},{performanceClass},{lap.Vehicle.PerformanceIndex},{Quote(lap.StartedAt.ToString("O"))},{N(lap.TotalSeconds)},{(lap.IsValid ? 1 : 0)},{Quote(lap.InvalidReason)});\n";
        foreach (var segment in lap.Segments)
        {
            sql += $"INSERT INTO LapSegments(LapId,SectorIndex,TimeSeconds,IsValid) VALUES({Quote(lap.Id.ToString())},{segment.Index},{N(segment.TimeSeconds)},{(segment.IsValid ? 1 : 0)});\n";
        }

        foreach (var sample in lap.Samples)
        {
            sql += $"INSERT INTO LapSamples(LapId,S,ElapsedSeconds,SpeedMps,Rpm,Gear,Accel,Brake,DeltaSeconds,X,Y,Z) VALUES({Quote(lap.Id.ToString())},{N(sample.S)},{N(sample.ElapsedSeconds)},{N(sample.SpeedMps)},{N(sample.Rpm)},{sample.Gear},{N(sample.Accel)},{N(sample.Brake)},{N(sample.DeltaSeconds)},{N(sample.X)},{N(sample.Y)},{N(sample.Z)});\n";
        }

        database.Execute(sql + "COMMIT;");
        PruneTrackLaps(lap.TrackId, MaxLapsPerTrack);
    }

    public int CountTracks(string? source = null)
    {
        var filter = source is null ? string.Empty : $" WHERE Source={Quote(source)}";
        return int.Parse(database.QueryText($"SELECT COUNT(*) FROM TrackTemplates{filter};") ?? "0", CultureInfo.InvariantCulture);
    }

    public int CountLaps(string? source = null)
    {
        var filter = source is null ? string.Empty : $" WHERE t.Source={Quote(source)}";
        return int.Parse(database.QueryText($"SELECT COUNT(*) FROM Laps l JOIN TrackTemplates t ON t.Id=l.TrackId{filter};") ?? "0", CultureInfo.InvariantCulture);
    }

    public IReadOnlyList<TrackSummary> ListTracks(string? source = null)
    {
        var filter = source is null ? string.Empty : $" WHERE t.Source={Quote(source)}";
        return database.QueryRows(
        $"SELECT t.Id,t.Name,t.Source,t.LengthMeters,COUNT(l.Id),t.LayoutKind,t.CatalogKind,t.Category FROM TrackTemplates t LEFT JOIN Laps l ON l.TrackId=t.Id{filter} GROUP BY t.Id ORDER BY t.CatalogKind,t.Category,t.Name;")
        .Select(row => new TrackSummary(
            Guid.Parse(row[0]!),
            row[1] ?? "Unnamed",
            row[2] ?? "user_learned",
            double.Parse(row[3]!, CultureInfo.InvariantCulture),
            int.Parse(row[4]!, CultureInfo.InvariantCulture),
            ParseLayoutKind(row[5]),
            ParseCatalogKind(row[6]),
            row[7]))
        .ToArray();
    }

    public int CountTracks(TrackCatalogKind catalogKind) => int.Parse(
        database.QueryText($"SELECT COUNT(*) FROM TrackTemplates WHERE CatalogKind={Quote(catalogKind.ToString())};") ?? "0",
        CultureInfo.InvariantCulture);

    public (TrackTemplate Track, IReadOnlyList<SectorDefinition> Sectors)? LoadLatestTrack(string? source = null)
    {
        var sourceFilter = source is null ? string.Empty : $" WHERE Source={Quote(source)}";
        var row = database.QueryRows($"SELECT Id,Name,Direction,Source,GameBuild,LengthMeters,ToleranceMeters,Confidence,CaptureLapCount,CreatedAt,UpdatedAt,LayoutKind,CatalogKind,Category FROM TrackTemplates{sourceFilter} ORDER BY UpdatedAt DESC LIMIT 1;").SingleOrDefault();
        if (row is null) return null;
        var id = Guid.Parse(row[0]!);
        var points = database.QueryRows($"SELECT X,Y,Z,S,TangentX,TangentZ FROM TrackPoints WHERE TrackId={Quote(id.ToString())} ORDER BY PointIndex;")
            .Select(point => new TrackPoint(
                double.Parse(point[0]!, CultureInfo.InvariantCulture), double.Parse(point[1]!, CultureInfo.InvariantCulture),
                double.Parse(point[2]!, CultureInfo.InvariantCulture), double.Parse(point[3]!, CultureInfo.InvariantCulture),
                double.Parse(point[4]!, CultureInfo.InvariantCulture), double.Parse(point[5]!, CultureInfo.InvariantCulture)))
            .ToArray();
        if (points.Length < 4) return null;
        var track = new TrackTemplate(
            id, row[1] ?? "Unnamed", int.Parse(row[2]!, CultureInfo.InvariantCulture), row[3] ?? "user_learned", row[4],
            points, double.Parse(row[5]!, CultureInfo.InvariantCulture),
            points.Min(point => point.X), points.Min(point => point.Y), points.Min(point => point.Z),
            points.Max(point => point.X), points.Max(point => point.Y), points.Max(point => point.Z),
            double.Parse(row[6]!, CultureInfo.InvariantCulture), double.Parse(row[7]!, CultureInfo.InvariantCulture),
            int.Parse(row[8]!, CultureInfo.InvariantCulture), DateTimeOffset.Parse(row[9]!, CultureInfo.InvariantCulture), DateTimeOffset.Parse(row[10]!, CultureInfo.InvariantCulture))
        {
            LayoutKind = ParseLayoutKind(row[11]),
            CatalogKind = ParseCatalogKind(row[12]),
            Category = row[13]
        };
        var sectors = database.QueryRows($"SELECT SectorSchemaVersion,SectorIndex,StartS,EndS,FeatureType,AlgorithmVersion FROM SectorDefinitions WHERE TrackId={Quote(id.ToString())} ORDER BY SectorIndex;")
            .Select(sector => new SectorDefinition(id, int.Parse(sector[0]!, CultureInfo.InvariantCulture), int.Parse(sector[1]!, CultureInfo.InvariantCulture),
                double.Parse(sector[2]!, CultureInfo.InvariantCulture), double.Parse(sector[3]!, CultureInfo.InvariantCulture),
                Enum.Parse<SectorFeatureType>(sector[4]!), sector[5] ?? "unknown"))
            .ToArray();
        return (track, sectors);
    }

    public (TrackTemplate Track, IReadOnlyList<SectorDefinition> Sectors)? LoadTrack(Guid trackId)
    {
        var row = database.QueryRows($"SELECT Id,Name,Direction,Source,GameBuild,LengthMeters,ToleranceMeters,Confidence,CaptureLapCount,CreatedAt,UpdatedAt,LayoutKind,CatalogKind,Category FROM TrackTemplates WHERE Id={Quote(trackId.ToString())} LIMIT 1;").SingleOrDefault();
        if (row is null) return null;
        var id = Guid.Parse(row[0]!);
        var points = database.QueryRows($"SELECT X,Y,Z,S,TangentX,TangentZ FROM TrackPoints WHERE TrackId={Quote(id.ToString())} ORDER BY PointIndex;")
            .Select(point => new TrackPoint(
                double.Parse(point[0]!, CultureInfo.InvariantCulture), double.Parse(point[1]!, CultureInfo.InvariantCulture),
                double.Parse(point[2]!, CultureInfo.InvariantCulture), double.Parse(point[3]!, CultureInfo.InvariantCulture),
                double.Parse(point[4]!, CultureInfo.InvariantCulture), double.Parse(point[5]!, CultureInfo.InvariantCulture)))
            .ToArray();
        if (points.Length < 4) return null;
        var track = new TrackTemplate(
            id, row[1] ?? "Unnamed", int.Parse(row[2]!, CultureInfo.InvariantCulture), row[3] ?? "user_learned", row[4],
            points, double.Parse(row[5]!, CultureInfo.InvariantCulture),
            points.Min(point => point.X), points.Min(point => point.Y), points.Min(point => point.Z),
            points.Max(point => point.X), points.Max(point => point.Y), points.Max(point => point.Z),
            double.Parse(row[6]!, CultureInfo.InvariantCulture), double.Parse(row[7]!, CultureInfo.InvariantCulture),
            int.Parse(row[8]!, CultureInfo.InvariantCulture), DateTimeOffset.Parse(row[9]!, CultureInfo.InvariantCulture), DateTimeOffset.Parse(row[10]!, CultureInfo.InvariantCulture))
        {
            LayoutKind = ParseLayoutKind(row[11]),
            CatalogKind = ParseCatalogKind(row[12]),
            Category = row[13]
        };
        var sectors = database.QueryRows($"SELECT SectorSchemaVersion,SectorIndex,StartS,EndS,FeatureType,AlgorithmVersion FROM SectorDefinitions WHERE TrackId={Quote(id.ToString())} ORDER BY SectorIndex;")
            .Select(sector => new SectorDefinition(id, int.Parse(sector[0]!, CultureInfo.InvariantCulture), int.Parse(sector[1]!, CultureInfo.InvariantCulture),
                double.Parse(sector[2]!, CultureInfo.InvariantCulture), double.Parse(sector[3]!, CultureInfo.InvariantCulture),
                Enum.Parse<SectorFeatureType>(sector[4]!), sector[5] ?? "unknown"))
            .ToArray();
        return (track, sectors);
    }

    public int CountLaps(Guid trackId) => int.Parse(database.QueryText($"SELECT COUNT(*) FROM Laps WHERE TrackId={Quote(trackId.ToString())};") ?? "0", CultureInfo.InvariantCulture);

    public IReadOnlyList<LapRecord> LoadLaps(Guid trackId, int limit = 50)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        var rows = database.QueryRows(
            $"SELECT Id,Direction,SectorSchemaVersion,SessionId,VehicleFingerprint,CarClass,PerformanceIndex,StartedAt,TotalSeconds,IsValid,InvalidReason FROM Laps WHERE TrackId={Quote(trackId.ToString())} ORDER BY StartedAt DESC LIMIT {limit};");
        var laps = new List<LapRecord>(rows.Count);
        foreach (var row in rows)
        {
            var lapId = Guid.Parse(row[0]!);
            var segments = database.QueryRows($"SELECT SectorIndex,TimeSeconds,IsValid FROM LapSegments WHERE LapId={Quote(lapId.ToString())} ORDER BY SectorIndex;")
                .Select(segment => new LapSegment(
                    int.Parse(segment[0]!, CultureInfo.InvariantCulture),
                    double.Parse(segment[1]!, CultureInfo.InvariantCulture),
                    segment[2] == "1"))
                .ToArray();
            var samples = database.QueryRows($"SELECT S,ElapsedSeconds,SpeedMps,Rpm,Gear,Accel,Brake,DeltaSeconds,X,Y,Z FROM LapSamples WHERE LapId={Quote(lapId.ToString())} ORDER BY S;")
                .Select(sample => new LapSample(
                    double.Parse(sample[0]!, CultureInfo.InvariantCulture),
                    double.Parse(sample[1]!, CultureInfo.InvariantCulture),
                    double.Parse(sample[2]!, CultureInfo.InvariantCulture),
                    double.Parse(sample[3]!, CultureInfo.InvariantCulture),
                    checked((byte)int.Parse(sample[4]!, CultureInfo.InvariantCulture)),
                    double.Parse(sample[5]!, CultureInfo.InvariantCulture),
                    double.Parse(sample[6]!, CultureInfo.InvariantCulture),
                    double.Parse(sample[7]!, CultureInfo.InvariantCulture),
                    double.Parse(sample[8]!, CultureInfo.InvariantCulture),
                    double.Parse(sample[9]!, CultureInfo.InvariantCulture),
                    double.Parse(sample[10]!, CultureInfo.InvariantCulture)))
                .ToArray();
            laps.Add(new LapRecord(
                lapId,
                trackId,
                int.Parse(row[1]!, CultureInfo.InvariantCulture),
                int.Parse(row[2]!, CultureInfo.InvariantCulture),
                Guid.Parse(row[3]!),
                ParseStoredVehicle(
                    row[4],
                    int.Parse(row[5]!, CultureInfo.InvariantCulture),
                    int.Parse(row[6]!, CultureInfo.InvariantCulture)),
                DateTimeOffset.Parse(row[7]!, CultureInfo.InvariantCulture),
                double.Parse(row[8]!, CultureInfo.InvariantCulture),
                row[9] == "1",
                row[10],
                segments,
                samples));
        }

        laps.Reverse();
        return laps;
    }

    public void DeleteLap(Guid lapId) => database.Execute(
        $"DELETE FROM Laps WHERE Id={Quote(lapId.ToString())};");

    public void DeleteTrackLaps(
        Guid trackId,
        IReadOnlyCollection<int>? performanceClasses = null,
        IReadOnlyCollection<Guid>? preserveLapIds = null)
    {
        if (performanceClasses is { Count: 0 }) return;
        var classClause = performanceClasses is { Count: > 0 }
            ? $" AND CarClass IN ({string.Join(',', performanceClasses.Order())})"
            : string.Empty;
        var preserveClause = preserveLapIds is { Count: > 0 }
            ? $" AND Id NOT IN ({string.Join(',', preserveLapIds.Select(id => Quote(id.ToString())))})"
            : string.Empty;
        database.Execute($"DELETE FROM Laps WHERE TrackId={Quote(trackId.ToString())}{classClause}{preserveClause};");
    }

    public IReadOnlyList<Guid> PruneTrackLaps(Guid trackId, int maximum = MaxLapsPerTrack)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximum, 1);
        var rows = database.QueryRows(
            $"SELECT Id,TotalSeconds,IsValid,CarClass,StartedAt FROM Laps WHERE TrackId={Quote(trackId.ToString())} ORDER BY StartedAt DESC;");
        if (rows.Count <= maximum) return [];

        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var historicalBestId in rows
                     .Where(row => row[2] == "1")
                     .GroupBy(row => int.Parse(row[3]!, CultureInfo.InvariantCulture))
                     .Select(group => group
                         .OrderBy(row => double.Parse(row[1]!, CultureInfo.InvariantCulture))
                         .ThenBy(row => DateTimeOffset.Parse(row[4]!, CultureInfo.InvariantCulture))
                         .ThenBy(row => row[0], StringComparer.OrdinalIgnoreCase)
                         .First()[0]!))
        {
            keep.Add(historicalBestId);
        }
        foreach (var row in rows)
        {
            if (keep.Count >= maximum) break;
            keep.Add(row[0]!);
        }

        var removed = rows.Where(row => !keep.Contains(row[0]!)).Select(row => Guid.Parse(row[0]!)).ToArray();
        if (removed.Length == 0) return removed;
        var sql = "BEGIN IMMEDIATE;\n" +
            string.Join('\n', removed.Select(id => $"DELETE FROM Laps WHERE Id={Quote(id.ToString())};")) +
            "\nCOMMIT;";
        database.Execute(sql);
        return removed;
    }

    public void RenameTrack(Guid trackId, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        EnsureTrackIsMutable(trackId);
        database.Execute($"UPDATE TrackTemplates SET Name={Quote(name.Trim())},UpdatedAt={Quote(DateTimeOffset.UtcNow.ToString("O"))} WHERE Id={Quote(trackId.ToString())};");
    }

    public void DeleteTrack(Guid trackId)
    {
        EnsureTrackIsMutable(trackId);
        var id = Quote(trackId.ToString());
        database.Execute("BEGIN IMMEDIATE;\n" +
            $"DELETE FROM Laps WHERE TrackId={id};\n" +
            $"DELETE FROM TrackTemplates WHERE Id={id};\nCOMMIT;");
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        database.Dispose();
    }

    private void Migrate()
    {
        database.Execute("PRAGMA foreign_keys=ON; PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; " +
            "CREATE TABLE IF NOT EXISTS SchemaVersion(Version INTEGER NOT NULL); " +
            "INSERT INTO SchemaVersion(Version) SELECT 0 WHERE NOT EXISTS(SELECT 1 FROM SchemaVersion);");
        var version = SchemaVersion;
        if (version < 1)
        {
            database.Execute("BEGIN IMMEDIATE;\n" +
                "CREATE TABLE AppSettings(Key TEXT PRIMARY KEY,Value TEXT NOT NULL,UpdatedAt TEXT NOT NULL);\n" +
                "CREATE TABLE ModuleSettings(ModuleId TEXT NOT NULL,Key TEXT NOT NULL,Value TEXT NOT NULL,UpdatedAt TEXT NOT NULL,PRIMARY KEY(ModuleId,Key));\n" +
                "CREATE TABLE Sessions(Id TEXT PRIMARY KEY,Source TEXT NOT NULL,StartedAt TEXT NOT NULL,RawRecordingPath TEXT);\n" +
                "UPDATE SchemaVersion SET Version=1;\nCOMMIT;");
            version = 1;
        }

        if (version < 2)
        {
            database.Execute("BEGIN IMMEDIATE;\n" +
                "CREATE TABLE TrackTemplates(Id TEXT PRIMARY KEY,Name TEXT NOT NULL,Direction INTEGER NOT NULL,Source TEXT NOT NULL,GameBuild TEXT,LengthMeters REAL NOT NULL,ToleranceMeters REAL NOT NULL,Confidence REAL NOT NULL,CaptureLapCount INTEGER NOT NULL,CreatedAt TEXT NOT NULL,UpdatedAt TEXT NOT NULL);\n" +
                "CREATE TABLE TrackPoints(TrackId TEXT NOT NULL,PointIndex INTEGER NOT NULL,X REAL NOT NULL,Y REAL NOT NULL,Z REAL NOT NULL,S REAL NOT NULL,TangentX REAL NOT NULL,TangentZ REAL NOT NULL,PRIMARY KEY(TrackId,PointIndex),FOREIGN KEY(TrackId) REFERENCES TrackTemplates(Id) ON DELETE CASCADE);\n" +
                "CREATE INDEX IX_TrackPoints_Track_S ON TrackPoints(TrackId,S);\n" +
                "CREATE TABLE SectorDefinitions(TrackId TEXT NOT NULL,SectorSchemaVersion INTEGER NOT NULL,SectorIndex INTEGER NOT NULL,StartS REAL NOT NULL,EndS REAL NOT NULL,FeatureType TEXT NOT NULL,AlgorithmVersion TEXT NOT NULL,PRIMARY KEY(TrackId,SectorSchemaVersion,SectorIndex),FOREIGN KEY(TrackId) REFERENCES TrackTemplates(Id) ON DELETE CASCADE);\n" +
                "CREATE TABLE VehicleProfiles(Id TEXT PRIMARY KEY,CarOrdinal INTEGER NOT NULL,CarClass INTEGER NOT NULL,PI INTEGER NOT NULL,Drivetrain INTEGER NOT NULL,Cylinders INTEGER NOT NULL,MaxRpm INTEGER NOT NULL,CurveSignature TEXT NOT NULL,GearSignature TEXT NOT NULL,State TEXT NOT NULL,Confidence REAL NOT NULL,UpdatedAt TEXT NOT NULL);\n" +
                "CREATE TABLE EngineCurveBins(VehicleProfileId TEXT NOT NULL,RpmCenter INTEGER NOT NULL,SampleCount INTEGER NOT NULL,MedianPower REAL NOT NULL,MedianTorque REAL NOT NULL,MedianBoost REAL NOT NULL,Deviation REAL NOT NULL,Confidence REAL NOT NULL,PRIMARY KEY(VehicleProfileId,RpmCenter),FOREIGN KEY(VehicleProfileId) REFERENCES VehicleProfiles(Id) ON DELETE CASCADE);\n" +
                "CREATE TABLE GearModels(VehicleProfileId TEXT NOT NULL,Gear INTEGER NOT NULL,Slope REAL NOT NULL,SampleCount INTEGER NOT NULL,Confidence REAL NOT NULL,PRIMARY KEY(VehicleProfileId,Gear),FOREIGN KEY(VehicleProfileId) REFERENCES VehicleProfiles(Id) ON DELETE CASCADE);\n" +
                "CREATE TABLE ShiftTargets(VehicleProfileId TEXT NOT NULL,FromGear INTEGER NOT NULL,ToGear INTEGER NOT NULL,TargetRpm REAL NOT NULL,CueRpm REAL NOT NULL,AfterRpm REAL NOT NULL,Confidence REAL NOT NULL,AlgorithmVersion TEXT NOT NULL,PRIMARY KEY(VehicleProfileId,FromGear,ToGear),FOREIGN KEY(VehicleProfileId) REFERENCES VehicleProfiles(Id) ON DELETE CASCADE);\n" +
                "CREATE TABLE Laps(Id TEXT PRIMARY KEY,TrackId TEXT NOT NULL,Direction INTEGER NOT NULL,SectorSchemaVersion INTEGER NOT NULL,SessionId TEXT NOT NULL,VehicleFingerprint TEXT NOT NULL,StartedAt TEXT NOT NULL,TotalSeconds REAL NOT NULL,IsValid INTEGER NOT NULL,InvalidReason TEXT,FOREIGN KEY(TrackId) REFERENCES TrackTemplates(Id),FOREIGN KEY(SessionId) REFERENCES Sessions(Id));\n" +
                "CREATE INDEX IX_Laps_Comparable ON Laps(TrackId,Direction,SectorSchemaVersion,IsValid);\n" +
                "CREATE TABLE LapSegments(LapId TEXT NOT NULL,SectorIndex INTEGER NOT NULL,TimeSeconds REAL NOT NULL,IsValid INTEGER NOT NULL,PRIMARY KEY(LapId,SectorIndex),FOREIGN KEY(LapId) REFERENCES Laps(Id) ON DELETE CASCADE);\n" +
                "CREATE TABLE LapSamples(LapId TEXT NOT NULL,S REAL NOT NULL,ElapsedSeconds REAL NOT NULL,SpeedMps REAL NOT NULL,Rpm REAL NOT NULL,Gear INTEGER NOT NULL,Accel REAL NOT NULL,Brake REAL NOT NULL,DeltaSeconds REAL NOT NULL,X REAL NOT NULL,Y REAL NOT NULL,Z REAL NOT NULL,FOREIGN KEY(LapId) REFERENCES Laps(Id) ON DELETE CASCADE);\n" +
                "CREATE INDEX IX_LapSamples_Lap_S ON LapSamples(LapId,S);\n" +
                "UPDATE SchemaVersion SET Version=2;\nCOMMIT;");
        }

        if (version < 3)
        {
            database.Execute("BEGIN IMMEDIATE;\n" +
                "ALTER TABLE Laps ADD COLUMN CarClass INTEGER NOT NULL DEFAULT -1;\n" +
                "ALTER TABLE Laps ADD COLUMN PerformanceIndex INTEGER NOT NULL DEFAULT -1;\n" +
                "CREATE INDEX IX_Laps_Track_Class ON Laps(TrackId,CarClass,IsValid,TotalSeconds);\n" +
                "UPDATE SchemaVersion SET Version=3;\nCOMMIT;");
            version = 3;
        }

        if (version < 4)
        {
            database.Execute("BEGIN IMMEDIATE;\n" +
                // Schema 2 stored the PI in VehicleFingerprint as CarOrdinal:PI:MaxRpm.
                // Schema 3 added explicit columns but initialized both of them to -1.
                "UPDATE Laps SET PerformanceIndex = CASE " +
                "WHEN PerformanceIndex BETWEEN 1 AND 999 THEN PerformanceIndex " +
                "WHEN VehicleFingerprint GLOB '*:*:*' THEN CAST(substr(VehicleFingerprint,instr(VehicleFingerprint,':')+1,instr(substr(VehicleFingerprint,instr(VehicleFingerprint,':')+1),':')-1) AS INTEGER) " +
                "ELSE 0 END WHERE PerformanceIndex NOT BETWEEN 1 AND 999;\n" +
                "UPDATE Laps SET CarClass = CASE " +
                "WHEN PerformanceIndex <= 400 THEN 0 " +
                "WHEN PerformanceIndex <= 500 THEN 1 " +
                "WHEN PerformanceIndex <= 600 THEN 2 " +
                "WHEN PerformanceIndex <= 700 THEN 3 " +
                "WHEN PerformanceIndex <= 800 THEN 4 " +
                "WHEN PerformanceIndex <= 900 THEN 5 " +
                "WHEN PerformanceIndex <= 998 THEN 6 " +
                "ELSE 7 END WHERE CarClass NOT BETWEEN 0 AND 7;\n" +
                "UPDATE SchemaVersion SET Version=4;\nCOMMIT;");
            version = 4;
        }

        if (version < 5)
        {
            database.Execute("BEGIN IMMEDIATE;\n" +
                "ALTER TABLE TrackTemplates ADD COLUMN LayoutKind TEXT NOT NULL DEFAULT 'Circuit';\n" +
                "UPDATE SchemaVersion SET Version=5;\nCOMMIT;");
            version = 5;
        }

        if (version < 6)
        {
            database.Execute("BEGIN IMMEDIATE;\n" +
                "ALTER TABLE TrackTemplates ADD COLUMN CatalogKind TEXT NOT NULL DEFAULT 'UserCustom';\n" +
                "ALTER TABLE TrackTemplates ADD COLUMN Category TEXT;\n" +
                "CREATE INDEX IX_TrackTemplates_Catalog ON TrackTemplates(CatalogKind,Category,Name);\n" +
                "UPDATE SchemaVersion SET Version=6;\nCOMMIT;");
            version = 6;
        }

        if (version < 7)
        {
            database.Execute("BEGIN IMMEDIATE;\n" +
                "ALTER TABLE VehicleProfiles ADD COLUMN DisplayName TEXT;\n" +
                "ALTER TABLE VehicleProfiles ADD COLUMN RecommendationsEnabled INTEGER NOT NULL DEFAULT 1;\n" +
                "CREATE INDEX IX_VehicleProfiles_CarOrdinal ON VehicleProfiles(CarOrdinal,UpdatedAt);\n" +
                "UPDATE SchemaVersion SET Version=7;\nCOMMIT;");
            version = 7;
        }

        if (version < 8)
        {
            ConsolidateCompatibleVehicleProfiles();
            database.Execute(
                "BEGIN IMMEDIATE;\n" +
                "UPDATE SchemaVersion SET Version=8;\n" +
                "COMMIT;");
            version = 8;
        }

        if (SchemaVersion != CurrentSchemaVersion) throw new InvalidOperationException("Database schema version is newer than this LazyForza build.");
    }

    private void ConsolidateCompatibleVehicleProfiles()
    {
        var remaining = ListVehicleProfiles().ToList();
        while (remaining.Count > 0)
        {
            var survivor = remaining
                .OrderByDescending(profile => !string.IsNullOrWhiteSpace(profile.CustomName))
                .ThenByDescending(profile =>
                    profile.CurveBins + profile.Gears + profile.ShiftTargets)
                .ThenByDescending(profile => profile.Confidence)
                .ThenByDescending(profile => profile.UpdatedAt)
                .First();
            remaining.Remove(survivor);
            var currentSurvivor = survivor;
            while (true)
            {
                var duplicate = remaining
                    .Where(profile =>
                        VehicleTuneCompatibility.AreCompatible(
                            currentSurvivor.Fingerprint,
                            profile.Fingerprint))
                    .OrderByDescending(profile =>
                        profile.CurveBins + profile.Gears + profile.ShiftTargets)
                    .ThenByDescending(profile => profile.Confidence)
                    .FirstOrDefault();
                if (duplicate is null) break;

                MergeVehicleProfile(survivor.Id, duplicate.Id);
                remaining.Remove(duplicate);
                RefreshVehicleProfileGearSignature(survivor.Id);
                currentSurvivor = ListVehicleProfiles()
                    .Single(profile =>
                        string.Equals(
                            profile.Id,
                            survivor.Id,
                            StringComparison.Ordinal));
            }
        }
    }

    private void MergeVehicleProfile(string survivorId, string duplicateId)
    {
        var survivor = Quote(survivorId);
        var duplicate = Quote(duplicateId);
        database.Execute(
            "BEGIN IMMEDIATE;\n" +
            "INSERT INTO EngineCurveBins(VehicleProfileId,RpmCenter,SampleCount,MedianPower,MedianTorque,MedianBoost,Deviation,Confidence) " +
            $"SELECT {survivor},RpmCenter,SampleCount,MedianPower,MedianTorque,MedianBoost,Deviation,Confidence FROM EngineCurveBins WHERE VehicleProfileId={duplicate} AND 1 " +
            "ON CONFLICT(VehicleProfileId,RpmCenter) DO UPDATE SET SampleCount=excluded.SampleCount,MedianPower=excluded.MedianPower,MedianTorque=excluded.MedianTorque,MedianBoost=excluded.MedianBoost,Deviation=excluded.Deviation,Confidence=excluded.Confidence " +
            "WHERE excluded.SampleCount>=EngineCurveBins.SampleCount;\n" +
            "INSERT INTO GearModels(VehicleProfileId,Gear,Slope,SampleCount,Confidence) " +
            $"SELECT {survivor},Gear,Slope,SampleCount,Confidence FROM GearModels WHERE VehicleProfileId={duplicate} AND 1 " +
            "ON CONFLICT(VehicleProfileId,Gear) DO UPDATE SET Slope=excluded.Slope,SampleCount=excluded.SampleCount,Confidence=excluded.Confidence " +
            "WHERE excluded.SampleCount>=GearModels.SampleCount;\n" +
            "INSERT INTO ShiftTargets(VehicleProfileId,FromGear,ToGear,TargetRpm,CueRpm,AfterRpm,Confidence,AlgorithmVersion) " +
            $"SELECT {survivor},FromGear,ToGear,TargetRpm,CueRpm,AfterRpm,Confidence,AlgorithmVersion FROM ShiftTargets WHERE VehicleProfileId={duplicate} AND 1 " +
            "ON CONFLICT(VehicleProfileId,FromGear,ToGear) DO UPDATE SET TargetRpm=excluded.TargetRpm,CueRpm=excluded.CueRpm,AfterRpm=excluded.AfterRpm,Confidence=excluded.Confidence,AlgorithmVersion=excluded.AlgorithmVersion " +
            "WHERE excluded.Confidence>=ShiftTargets.Confidence;\n" +
            $"UPDATE VehicleProfiles SET DisplayName=COALESCE(DisplayName,(SELECT DisplayName FROM VehicleProfiles WHERE Id={duplicate}))," +
            $"RecommendationsEnabled=MIN(RecommendationsEnabled,(SELECT RecommendationsEnabled FROM VehicleProfiles WHERE Id={duplicate}))," +
            $"Confidence=MAX(Confidence,(SELECT Confidence FROM VehicleProfiles WHERE Id={duplicate}))," +
            $"UpdatedAt=MAX(UpdatedAt,(SELECT UpdatedAt FROM VehicleProfiles WHERE Id={duplicate})) WHERE Id={survivor};\n" +
            $"DELETE FROM VehicleProfiles WHERE Id={duplicate};\n" +
            "COMMIT;");
    }

    private void RefreshVehicleProfileGearSignature(string profileId)
    {
        var rows = database.QueryRows(
            "SELECT Gear,Slope FROM GearModels " +
            $"WHERE VehicleProfileId={Quote(profileId)} AND Gear>0 AND Slope>0 ORDER BY Gear;");
        if (rows.Count < 2) return;
        var signature = string.Join(
            '-',
            rows.Select(row =>
            {
                var gear = ParseInt(row[0]);
                var slope = ParseDouble(row[1]);
                var rounded = Math.Round(
                    slope / 2d,
                    MidpointRounding.AwayFromZero) * 2;
                return $"g{gear}_{rounded:0}";
            }));
        database.Execute(
            $"UPDATE VehicleProfiles SET GearSignature={Quote(signature)} " +
            $"WHERE Id={Quote(profileId)};");
    }

    private static VehicleProfileFingerprint FingerprintFromObservedModels(
        ShiftLearningSnapshot snapshot)
    {
        var fingerprint = snapshot.Fingerprint!;
        if (snapshot.Gears.Count < 2) return fingerprint;
        var gearSignature = string.Join(
            '-',
            snapshot.Gears
                .Where(gear =>
                    gear.Gear > 0 &&
                    double.IsFinite(gear.RpmPerMeterPerSecond) &&
                    gear.RpmPerMeterPerSecond > 0)
                .OrderBy(gear => gear.Gear)
                .Select(gear =>
                {
                    var rounded = Math.Round(
                        gear.RpmPerMeterPerSecond / 2d,
                        MidpointRounding.AwayFromZero) * 2;
                    return $"g{gear.Gear}_{rounded:0}";
                }));
        return string.IsNullOrWhiteSpace(gearSignature)
            ? fingerprint
            : fingerprint with { GearSlopeSignature = gearSignature };
    }

    private static string Quote(string? value) => value is null ? "NULL" : "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
    private static string N(double value) => value.ToString("R", CultureInfo.InvariantCulture);
    private static int ParseInt(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
    private static double ParseDouble(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
    private static TrackLayoutKind ParseLayoutKind(string? value) =>
        Enum.TryParse<TrackLayoutKind>(value, true, out var parsed) ? parsed : TrackLayoutKind.Circuit;
    private static TrackCatalogKind ParseCatalogKind(string? value) =>
        Enum.TryParse<TrackCatalogKind>(value, true, out var parsed) ? parsed : TrackCatalogKind.UserCustom;

    private void EnsureTrackIsMutable(Guid trackId)
    {
        var catalogKind = database.QueryText(
            $"SELECT CatalogKind FROM TrackTemplates WHERE Id={Quote(trackId.ToString())} LIMIT 1;");
        if (ParseCatalogKind(catalogKind) == TrackCatalogKind.PlaygroundOfficial)
            throw new InvalidOperationException("Playground 官方赛事属于内置必要数据，不能重命名或删除。");
    }

    private static VehicleProfileFingerprint ParseStoredVehicle(string? value, int carClass, int storedPerformanceIndex)
    {
        var parts = value?.Split(':');
        var ordinal = -1;
        var legacyPerformanceIndex = 0;
        var maxRpm = 0;
        var parsed = parts is { Length: 3 } &&
                     int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out ordinal) &&
                     int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out legacyPerformanceIndex) &&
                     int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out maxRpm);
        var performanceIndex = storedPerformanceIndex is >= 1 and <= 999
            ? storedPerformanceIndex
            : parsed ? legacyPerformanceIndex : 0;
        return new VehicleProfileFingerprint(
            parsed ? ordinal : -1,
            PerformanceClassCatalog.Resolve(carClass, performanceIndex),
            performanceIndex,
            -1, -1, parsed ? maxRpm : 0,
            parsed ? "stored" : "unknown",
            parsed ? "stored" : "unknown");
    }
}
