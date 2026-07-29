using LazyForza.Analysis;
using LazyForza.Domain;
using LazyForza.Storage;

namespace LazyForza.Storage.Tests;

[TestClass]
public sealed class StorageTests
{
    [TestMethod]
    public async Task MigrationSettingsAndDatabasesAreIsolated()
    {
        var firstPath = TempDatabasePath();
        var secondPath = TempDatabasePath();
        try
        {
            using var first = new LazyForzaStore(firstPath);
            using var second = new LazyForzaStore(secondPath);
            Assert.AreEqual(9, first.SchemaVersion);
            Assert.AreEqual(9, second.SchemaVersion);
            await first.SetAsync("dashboard", "enabled", "True", CancellationToken.None);
            Assert.AreEqual("True", await first.GetAsync("dashboard", "enabled", CancellationToken.None));
            Assert.IsNull(await second.GetAsync("dashboard", "enabled", CancellationToken.None));
            var fingerprint = new VehicleProfileFingerprint(9, 5, 850, 2, 8, 8500, "gears", "curve");
            var learning = new ShiftLearningSnapshot(LearningState.Ready, 1, 0.9, fingerprint,
                [new EngineCurveBin(6000, 20, 300000, 500, 12, 3, 0.9)],
                [new GearModel(3, 180, 30, 0.9), new GearModel(4, 130, 30, 0.9)],
                [new ShiftTarget(3, 4, 7800, 7420, 5633, 0.85, false)],
                new Dictionary<string, int>(), "ready");
            await first.SaveShiftLearningAsync(learning, CancellationToken.None);
            Assert.AreEqual(1, first.CountVehicleProfiles());
            Assert.AreEqual(0, second.CountVehicleProfiles());
        }
        finally
        {
            DeleteDatabase(firstPath);
            DeleteDatabase(secondPath);
        }
    }

    [TestMethod]
    public async Task VehicleProfileCatalogSupportsMultipleTunesRenameToggleAndDelete()
    {
        var path = TempDatabasePath();
        try
        {
            using var store = new LazyForzaStore(path);
            var streetTune = new VehicleProfileFingerprint(6001, 5, 850, 2, 6, 8_000, "g2_210-g3_150", "p60_t48_r7000");
            var raceTune = streetTune with { GearSlopeSignature = "g2_224-g3_160", CurveSignature = "p68_t52_r7200" };
            static ShiftLearningSnapshot Snapshot(VehicleProfileFingerprint fingerprint)
            {
                var gears = VehicleTuneCompatibility
                    .ParseGearSignature(fingerprint.GearSlopeSignature)
                    .Select(pair => new GearModel(pair.Key, pair.Value, 30, 0.9))
                    .ToArray();
                return new ShiftLearningSnapshot(
                    LearningState.Ready, 1, 0.88, fingerprint,
                    [new EngineCurveBin(7_000, 20, 300_000, 480, 10, 3, 0.9)],
                    gears,
                    [new ShiftTarget(2, 3, 7_600, 7_200, 5_400, 0.85, false)],
                    new Dictionary<string, int>(),
                    "ready");
            }

            await store.SaveShiftLearningAsync(Snapshot(streetTune), CancellationToken.None);
            await store.SaveShiftLearningAsync(Snapshot(raceTune), CancellationToken.None);

            var profiles = store.ListVehicleProfiles();
            Assert.HasCount(2, profiles);
            Assert.AreEqual(1, profiles[0].CurveBins);
            Assert.AreEqual(2, profiles[0].Gears);
            Assert.AreEqual(1, profiles[0].ShiftTargets);
            Assert.AreNotEqual(profiles[0].Id, profiles[1].Id,
                "同一 CarOrdinal 与 PI 的可观察调校差异必须形成独立配置。");

            var selected = profiles.Single(profile =>
                profile.Fingerprint.GearSlopeSignature == streetTune.GearSlopeSignature);
            store.RenameVehicleProfile(selected.Id, "公路调校");
            store.SetShiftRecommendationsEnabled(selected.Id, false);
            var renamed = store.ListVehicleProfiles().Single(profile => profile.Id == selected.Id);
            Assert.AreEqual("公路调校", renamed.CustomName);
            Assert.IsFalse(renamed.ShiftRecommendationsEnabled);
            Assert.IsFalse(await store.GetShiftRecommendationsEnabledAsync(selected.Id, CancellationToken.None));

            store.DeleteVehicleProfile(selected.Id);
            Assert.AreEqual(1, store.CountVehicleProfiles());
            Assert.IsFalse(store.ListVehicleProfiles().Any(profile => profile.Id == selected.Id));
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [TestMethod]
    public async Task CompatiblePartialGearProfilesReuseOneCanonicalVehicleProfile()
    {
        var path = TempDatabasePath();
        try
        {
            using var store = new LazyForzaStore(path);
            var first = new VehicleProfileFingerprint(
                2038, 4, 800, 2, 8, 9_000,
                "g2_264-g3_198",
                "p77_t53_r7200");
            var second = first with { GearSlopeSignature = "g3_198-g4_156" };
            var third = first with { GearSlopeSignature = "g4_156-g5_126" };

            var firstId = await store.SaveShiftLearningAsync(
                VehicleSnapshot(first, (2, 264d), (3, 198d)),
                CancellationToken.None);
            var secondId = await store.SaveShiftLearningAsync(
                VehicleSnapshot(second, (3, 198.4d), (4, 156d)),
                CancellationToken.None);
            var thirdId = await store.SaveShiftLearningAsync(
                VehicleSnapshot(third, (4, 155.8d), (5, 126d)),
                CancellationToken.None);

            Assert.IsNotNull(firstId);
            Assert.AreEqual(firstId, secondId);
            Assert.AreEqual(firstId, thirdId);
            Assert.AreEqual(1, store.CountVehicleProfiles());
            var profile = store.ListVehicleProfiles().Single();
            Assert.AreEqual(4, profile.Gears);
            StringAssert.Contains(profile.Fingerprint.GearSlopeSignature, "g2_264");
            StringAssert.Contains(profile.Fingerprint.GearSlopeSignature, "g5_126");
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [TestMethod]
    public async Task LaterObservedGearDifferenceSeparatesSamePiTunes()
    {
        var path = TempDatabasePath();
        try
        {
            using var store = new LazyForzaStore(path);
            var initiallyIdentical = new VehicleProfileFingerprint(
                2038, 4, 800, 2, 8, 9_000,
                "g2_264-g3_198",
                "p77_t53_r7200");

            var roadId = await store.SaveShiftLearningAsync(
                VehicleSnapshot(
                    initiallyIdentical,
                    (2, 264d),
                    (3, 198d),
                    (5, 126d)),
                CancellationToken.None);
            var raceId = await store.SaveShiftLearningAsync(
                VehicleSnapshot(
                    initiallyIdentical,
                    (2, 264d),
                    (3, 198d),
                    (5, 140d)),
                CancellationToken.None);

            Assert.IsNotNull(roadId);
            Assert.IsNotNull(raceId);
            Assert.AreNotEqual(roadId, raceId,
                "相同 PI 的调校在后续挡位出现稳定显著差异时必须分开。");
            Assert.AreEqual(2, store.CountVehicleProfiles());
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [TestMethod]
    public void SchemaEightConsolidatesConnectedPartialProfilesWithoutLosingUserSettings()
    {
        var path = TempDatabasePath();
        try
        {
            using (var initialized = new LazyForzaStore(path))
                Assert.AreEqual(9, initialized.SchemaVersion);

            using (var raw = new WinSqliteDatabase(path))
            {
                raw.Execute(
                    "BEGIN IMMEDIATE;" +
                    "INSERT INTO VehicleProfiles VALUES('a',2038,4,800,2,8,9000,'p77_t53_r7200','g3_198-g4_156','Ready',0.70,'2026-07-20T00:00:00Z',NULL,1);" +
                    "INSERT INTO VehicleProfiles VALUES('b',2038,4,800,2,8,9000,'p77_t53_r7200','g4_156-g5_126','Ready',0.75,'2026-07-21T00:00:00Z',NULL,0);" +
                    "INSERT INTO VehicleProfiles VALUES('c',2038,4,800,2,8,9000,'p77_t53_r7200','g2_262-g3_198-g7_90','Ready',0.80,'2026-07-22T00:00:00Z',NULL,1);" +
                    "INSERT INTO VehicleProfiles VALUES('d',2038,4,800,2,8,9000,'p77_t53_r7200','g2_264-g3_198','Ready',0.85,'2026-07-23T00:00:00Z','2014 Alfa Romeo 4C',1);" +
                    "INSERT INTO GearModels VALUES('a',3,198,20,0.8),('a',4,156,20,0.8);" +
                    "INSERT INTO GearModels VALUES('b',4,156,22,0.9),('b',5,126,22,0.9);" +
                    "INSERT INTO GearModels VALUES('c',2,262,18,0.7),('c',3,198,18,0.7),('c',7,90,18,0.7);" +
                    "INSERT INTO GearModels VALUES('d',2,264,30,0.95),('d',3,198,30,0.95);" +
                    "UPDATE SchemaVersion SET Version=7;" +
                    "COMMIT;");
            }

            using var migrated = new LazyForzaStore(path);
            Assert.AreEqual(9, migrated.SchemaVersion);
            var profile = migrated.ListVehicleProfiles().Single();
            Assert.AreEqual("2014 Alfa Romeo 4C", profile.CustomName);
            Assert.IsFalse(profile.ShiftRecommendationsEnabled);
            Assert.AreEqual(5, profile.Gears);
            StringAssert.Contains(profile.Fingerprint.GearSlopeSignature, "g5_126");
            StringAssert.Contains(profile.Fingerprint.GearSlopeSignature, "g7_90");
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [TestMethod]
    public void EmbeddedVehicleNameCatalogWorksWithoutNetwork()
    {
        Assert.AreEqual("2014 Alfa Romeo 4C", VehicleNameCatalog.TryGetName(2038));
        Assert.AreEqual("车辆 999999", VehicleNameCatalog.DisplayName(999999));
        Assert.IsNotNull(VehicleNameCatalog.Info);
        Assert.IsTrue(VehicleNameCatalog.Info!.VehicleCount >= 600);
    }

    [TestMethod]
    [DataRow(-1, 100, 0)]
    [DataRow(-1, 400, 0)]
    [DataRow(-1, 401, 1)]
    [DataRow(-1, 500, 1)]
    [DataRow(-1, 501, 2)]
    [DataRow(-1, 600, 2)]
    [DataRow(-1, 601, 3)]
    [DataRow(-1, 700, 3)]
    [DataRow(-1, 701, 4)]
    [DataRow(-1, 800, 4)]
    [DataRow(-1, 801, 5)]
    [DataRow(-1, 900, 5)]
    [DataRow(-1, 901, 6)]
    [DataRow(-1, 998, 6)]
    [DataRow(-1, 999, 7)]
    [DataRow(4, 998, 4)]
    public void ResolvesInvalidPerformanceClassFromPiAndPreservesOfficialClass(
        int storedClass,
        int performanceIndex,
        int expectedClass)
    {
        Assert.AreEqual(expectedClass, PerformanceClassCatalog.Resolve(storedClass, performanceIndex));
        Assert.AreNotEqual("?", PerformanceClassCatalog.Name(expectedClass));
    }

    [TestMethod]
    public void MigratesSchemaThreeLegacyFingerprintsToPiAndPerformanceClass()
    {
        var path = TempDatabasePath();
        try
        {
            using (var legacy = new WinSqliteDatabase(path))
            {
                legacy.Execute(
                    "CREATE TABLE SchemaVersion(Version INTEGER NOT NULL);" +
                    "INSERT INTO SchemaVersion VALUES(3);" +
                    "CREATE TABLE Laps(Id TEXT PRIMARY KEY,TrackId TEXT NOT NULL,Direction INTEGER NOT NULL," +
                    "SectorSchemaVersion INTEGER NOT NULL,SessionId TEXT NOT NULL,VehicleFingerprint TEXT NOT NULL," +
                    "StartedAt TEXT NOT NULL,TotalSeconds REAL NOT NULL,IsValid INTEGER NOT NULL,InvalidReason TEXT," +
                    "CarClass INTEGER NOT NULL,PerformanceIndex INTEGER NOT NULL);" +
                    "CREATE TABLE VehicleProfiles(Id TEXT PRIMARY KEY,CarOrdinal INTEGER NOT NULL,CarClass INTEGER NOT NULL," +
                    "PI INTEGER NOT NULL,Drivetrain INTEGER NOT NULL,Cylinders INTEGER NOT NULL,MaxRpm INTEGER NOT NULL," +
                    "CurveSignature TEXT NOT NULL,GearSignature TEXT NOT NULL,State TEXT NOT NULL,Confidence REAL NOT NULL,UpdatedAt TEXT NOT NULL);" +
                    "INSERT INTO VehicleProfiles VALUES('legacy-profile',6001,5,850,2,6,8000,'learning','learning','Ready',0.8,'2026-01-01T00:00:00Z');" +
                    "CREATE TABLE EngineCurveBins(VehicleProfileId TEXT NOT NULL,RpmCenter INTEGER NOT NULL,SampleCount INTEGER NOT NULL," +
                    "MedianPower REAL NOT NULL,MedianTorque REAL NOT NULL,MedianBoost REAL NOT NULL,Deviation REAL NOT NULL,Confidence REAL NOT NULL," +
                    "PRIMARY KEY(VehicleProfileId,RpmCenter));" +
                    "CREATE TABLE GearModels(VehicleProfileId TEXT NOT NULL,Gear INTEGER NOT NULL,Slope REAL NOT NULL,SampleCount INTEGER NOT NULL," +
                    "Confidence REAL NOT NULL,PRIMARY KEY(VehicleProfileId,Gear));" +
                    "CREATE TABLE ShiftTargets(VehicleProfileId TEXT NOT NULL,FromGear INTEGER NOT NULL,ToGear INTEGER NOT NULL,TargetRpm REAL NOT NULL," +
                    "CueRpm REAL NOT NULL,AfterRpm REAL NOT NULL,Confidence REAL NOT NULL,AlgorithmVersion TEXT NOT NULL," +
                    "PRIMARY KEY(VehicleProfileId,FromGear,ToGear));" +
                    "CREATE TABLE LapSamples(LapId TEXT NOT NULL,S REAL NOT NULL,ElapsedSeconds REAL NOT NULL," +
                    "SpeedMps REAL NOT NULL,Rpm REAL NOT NULL,Gear INTEGER NOT NULL,Accel REAL NOT NULL," +
                    "Brake REAL NOT NULL,DeltaSeconds REAL NOT NULL,X REAL NOT NULL,Y REAL NOT NULL,Z REAL NOT NULL);" +
                    "CREATE TABLE TrackTemplates(Id TEXT PRIMARY KEY,Name TEXT NOT NULL,Direction INTEGER NOT NULL," +
                    "Source TEXT NOT NULL,GameBuild TEXT,LengthMeters REAL NOT NULL,ToleranceMeters REAL NOT NULL," +
                    "Confidence REAL NOT NULL,CaptureLapCount INTEGER NOT NULL,CreatedAt TEXT NOT NULL,UpdatedAt TEXT NOT NULL);" +
                    "INSERT INTO TrackTemplates VALUES('legacy-track','Legacy circuit',1,'simulator',NULL,1000,18,0.5,1,'2026-01-01T00:00:00Z','2026-01-01T00:00:00Z');" +
                    "INSERT INTO Laps VALUES('a','track',0,1,'session','1229:998:10000','2026-01-01T00:00:00Z',60,1,NULL,-1,-1);" +
                    "INSERT INTO Laps VALUES('b','track',0,1,'session','6001:917:8500','2026-01-01T00:01:00Z',61,1,NULL,-1,-1);" +
                    "INSERT INTO Laps VALUES('c','track',0,1,'session','7:800:8000','2026-01-01T00:02:00Z',62,1,NULL,3,-1);");
            }

            using (var store = new LazyForzaStore(path))
            {
                Assert.AreEqual(9, store.SchemaVersion);
                var databaseField = typeof(LazyForzaStore).GetField(
                    "database",
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic);
                Assert.IsNotNull(databaseField);
                var migrated = (WinSqliteDatabase?)databaseField.GetValue(store);
                Assert.IsNotNull(migrated);
                var rows = migrated.QueryRows("SELECT Id,CarClass,PerformanceIndex FROM Laps ORDER BY Id;");
                Assert.HasCount(3, rows);
                CollectionAssert.AreEqual(new[] { "a", "6", "998" }, rows[0].ToArray());
                CollectionAssert.AreEqual(new[] { "b", "6", "917" }, rows[1].ToArray());
                CollectionAssert.AreEqual(new[] { "c", "3", "800" }, rows[2].ToArray(),
                    "有效的官方 CarClass 必须优先于按 PI 推断的结果。");
                Assert.AreEqual("Circuit", migrated.QueryText("SELECT LayoutKind FROM TrackTemplates WHERE Id='legacy-track';"),
                    "Schema 5 must preserve legacy templates as circuits instead of guessing a new topology.");
                Assert.AreEqual("UserCustom", migrated.QueryText("SELECT CatalogKind FROM TrackTemplates WHERE Id='legacy-track';"),
                    "Schema 6 must preserve legacy templates as mutable user data until an embedded catalog explicitly claims them.");
                Assert.AreEqual("1", migrated.QueryText("SELECT RecommendationsEnabled FROM VehicleProfiles WHERE Id='legacy-profile';"),
                    "Schema 7 must keep existing profiles enabled by default.");
                Assert.IsNull(migrated.QueryText("SELECT DisplayName FROM VehicleProfiles WHERE Id='legacy-profile';"));
            }
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [TestMethod]
    public void SavesTrackSectorsLapSegmentsAndSamplesTransactionally()
    {
        var path = TempDatabasePath();
        try
        {
            using var store = new LazyForzaStore(path);
            var raw = Enumerable.Range(0, 126).Select(index =>
            {
                var angle = index / 125d * Math.PI * 2;
                return new TrackPoint(100 * Math.Cos(angle), 0, 100 * Math.Sin(angle), 0, 0, 0);
            }).ToArray();
            var track = TrackAlgorithms.BuildTemplate("Quote ' safe", raw);
            var sectors = TrackAlgorithms.CreateSectors(track);
            store.SaveTrack(track, sectors);
            var vehicle = new VehicleProfileFingerprint(1, 4, 780, 2, 6, 8000, "g", "c");
            var samples = track.Points.Take(40).Select((point, index) => new LapSample(point.S, index * 0.1, 40, 5000, 4, 1, 0, 0, point.X, point.Y, point.Z)).ToArray();
            var lap = new LapRecord(Guid.NewGuid(), track.Id, track.Direction, 1, Guid.NewGuid(), vehicle, DateTimeOffset.UtcNow, 50, true, null,
                sectors.Select(sector => new LapSegment(sector.Index, 50d / sectors.Count, true)).ToArray(), samples);
            store.SaveLap(lap);
            Assert.AreEqual(1, store.CountTracks());
            Assert.AreEqual(1, store.CountLaps());
            var listed = store.ListTracks().Single();
            Assert.AreEqual(TrackLayoutKind.Circuit, listed.LayoutKind);
            Assert.AreEqual("Quote ' safe", listed.Name);
            Assert.AreEqual(1, listed.Laps);
            var loadedLaps = store.LoadLaps(track.Id);
            Assert.HasCount(1, loadedLaps);
            Assert.AreEqual(lap.Id, loadedLaps[0].Id);
            Assert.AreEqual(vehicle.CarOrdinal, loadedLaps[0].Vehicle.CarOrdinal);
            Assert.AreEqual(vehicle.CarClass, loadedLaps[0].Vehicle.CarClass);
            Assert.AreEqual(vehicle.PerformanceIndex, loadedLaps[0].Vehicle.PerformanceIndex);
            Assert.AreEqual(vehicle.RoundedMaxRpm, loadedLaps[0].Vehicle.RoundedMaxRpm);
            Assert.HasCount(lap.Segments.Count, loadedLaps[0].Segments);
            Assert.HasCount(lap.Samples.Count, loadedLaps[0].Samples);
            var loaded = store.LoadLatestTrack();
            Assert.IsNotNull(loaded);
            Assert.AreEqual(track.Id, loaded.Value.Track.Id);
            Assert.AreEqual(sectors.Count, loaded.Value.Sectors.Count);
            store.RenameTrack(track.Id, "Renamed");
            Assert.AreEqual("Renamed", store.ListTracks().Single().Name);
            store.DeleteTrack(track.Id);
            Assert.AreEqual(0, store.CountTracks());
            Assert.AreEqual(0, store.CountLaps());
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [TestMethod]
    public void SavesQuantizedDynamicsAndKeepsLegacySamplesReadable()
    {
        var path = TempDatabasePath();
        try
        {
            using var store = new LazyForzaStore(path);
            var raw = Enumerable.Range(0, 80)
                .Select(index => new TrackPoint(index * 8, 0, Math.Sin(index / 8d) * 15, 0, 0, 0))
                .ToArray();
            var track = TrackAlgorithms.BuildTemplate("Dynamics storage", raw);
            var sectors = TrackAlgorithms.CreateSectors(track);
            store.SaveTrack(track, sectors);
            var vehicle = new VehicleProfileFingerprint(10, 5, 850, 1, 8, 8_000, "g", "c");
            var dynamics = new LapDynamics(
                -0.376,
                new WheelValues(0.1f, 0.2f, 0.3f, 0.4f),
                new WheelValues(-0.05f, 0.06f, -0.07f, 0.08f),
                new WheelValues(0.2f, 0.3f, 0.4f, 0.5f));
            var samples = new[]
            {
                new LapSample(0, 0, 20, 4_000, 2, 0.4, 0, 0, 0, 0, 0),
                new LapSample(10, 0.1, 22, 4_500, 2, 0.6, 0, 0, 10, 0, 1, dynamics)
            };
            var lap = new LapRecord(
                Guid.NewGuid(),
                track.Id,
                track.Direction,
                TrackAlgorithms.SectorSchemaVersion,
                Guid.NewGuid(),
                vehicle,
                DateTimeOffset.UtcNow,
                1,
                true,
                null,
                [],
                samples);
            store.SaveLap(lap);

            var loaded = store.LoadLap(lap.Id);
            Assert.IsNotNull(loaded);
            var loadedLap = loaded!;
            Assert.IsNull(loadedLap.Samples[0].Dynamics);
            Assert.IsNotNull(loadedLap.Samples[1].Dynamics);
            var loadedDynamics = loadedLap.Samples[1].Dynamics!;
            Assert.AreEqual(dynamics.Steering, loadedDynamics.Steering, 0.0001);
            Assert.AreEqual(
                dynamics.TireCombinedSlip.RearRight,
                loadedDynamics.TireCombinedSlip.RearRight,
                0.0003);
            var databaseField = typeof(LazyForzaStore).GetField(
                "database",
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic);
            var database = (WinSqliteDatabase?)databaseField?.GetValue(store);
            Assert.IsNotNull(database);
            Assert.AreEqual(
                LapDynamicsCodec.EncodedSize.ToString(),
                database.QueryText("SELECT length(Dynamics) FROM LapSamples WHERE Dynamics IS NOT NULL;"));
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [TestMethod]
    public void MigratesSchemaEightLapSamplesWithoutBackfillingDynamics()
    {
        var path = TempDatabasePath();
        try
        {
            using (var store = new LazyForzaStore(path))
            {
                var databaseField = typeof(LazyForzaStore).GetField(
                    "database",
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic);
                var database = (WinSqliteDatabase?)databaseField?.GetValue(store);
                Assert.IsNotNull(database);
                database.Execute(
                    "ALTER TABLE LapSamples DROP COLUMN Dynamics;" +
                    "UPDATE SchemaVersion SET Version=8;");
            }

            using var migrated = new LazyForzaStore(path);
            Assert.AreEqual(9, migrated.SchemaVersion);
            var migratedField = typeof(LazyForzaStore).GetField(
                "database",
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic);
            var migratedDatabase = (WinSqliteDatabase?)migratedField?.GetValue(migrated);
            Assert.IsNotNull(migratedDatabase);
            Assert.AreEqual(
                "Dynamics",
                migratedDatabase.QueryText(
                    "SELECT name FROM pragma_table_info('LapSamples') WHERE name='Dynamics';"));
            Assert.AreEqual(
                "0",
                migratedDatabase.QueryText(
                    "SELECT COUNT(*) FROM LapSamples WHERE Dynamics IS NOT NULL;"));
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [TestMethod]
    public void LoadsLightweightLapSummariesAndHydratesOnlySelectedLaps()
    {
        var path = TempDatabasePath();
        try
        {
            using var store = new LazyForzaStore(path);
            var raw = Enumerable.Range(0, 80)
                .Select(index => new TrackPoint(index * 10, 0, Math.Sin(index / 8d) * 20, 0, 0, 0))
                .ToArray();
            var track = TrackAlgorithms.BuildTemplate("Lazy loading", raw);
            var sectors = TrackAlgorithms.CreateSectors(track);
            store.SaveTrack(track, sectors);
            var vehicle = new VehicleProfileFingerprint(10, 5, 850, 2, 8, 7_500, "g", "c");
            var lapIds = new[] { Guid.NewGuid(), Guid.NewGuid() };

            for (var lapIndex = 0; lapIndex < lapIds.Length; lapIndex++)
            {
                var samples = track.Points.Take(60)
                    .Select((point, index) => new LapSample(
                        point.S,
                        index * 0.1,
                        40 + lapIndex,
                        5_000,
                        4,
                        1,
                        0,
                        0,
                        point.X,
                        point.Y,
                        point.Z))
                    .ToArray();
                store.SaveLap(new LapRecord(
                    lapIds[lapIndex],
                    track.Id,
                    track.Direction,
                    TrackAlgorithms.SectorSchemaVersion,
                    Guid.NewGuid(),
                    vehicle,
                    DateTimeOffset.UnixEpoch.AddMinutes(lapIndex),
                    50 + lapIndex,
                    true,
                    null,
                    sectors.Select(sector => new LapSegment(
                        sector.Index,
                        (50d + lapIndex) / sectors.Count,
                        true)).ToArray(),
                    samples));
            }

            var summaries = store.LoadLapSummaries(track.Id);
            Assert.HasCount(2, summaries);
            Assert.AreEqual(lapIds[0], summaries[0].Id);
            Assert.HasCount(sectors.Count, summaries[0].Segments);

            var selected = store.LoadLapsByIds([lapIds[1]]);
            Assert.HasCount(1, selected);
            Assert.AreEqual(lapIds[1], selected[0].Id);
            Assert.HasCount(60, selected[0].Samples);
            Assert.IsNull(store.LoadLap(Guid.NewGuid()));
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [TestMethod]
    public void KeepsFiftyLapsPreservesEveryClassBestAndSupportsClassScopedDeletion()
    {
        var path = TempDatabasePath();
        try
        {
            using var store = new LazyForzaStore(path);
            var raw = Enumerable.Range(0, 80)
                .Select(index => new TrackPoint(index * 10, 0, 0, 0, 0, 0))
                .ToArray();
            var track = TrackAlgorithms.BuildTemplate("Retention test", raw);
            var sectors = TrackAlgorithms.CreateSectors(track);
            store.SaveTrack(track, sectors);
            var lapIds = new List<Guid>();
            for (var index = 0; index < 52; index++)
            {
                var lapId = Guid.NewGuid();
                lapIds.Add(lapId);
                var performanceClass = index % 2 == 0 ? 4 : 5;
                var vehicle = new VehicleProfileFingerprint(1, performanceClass, performanceClass == 4 ? 800 : 900, 1, 6, 8000, "g", "c");
                var seconds = index switch { 0 => 40, 1 => 41, _ => 100 + index };
                store.SaveLap(new LapRecord(
                    lapId, track.Id, track.Direction, TrackAlgorithms.SectorSchemaVersion, Guid.NewGuid(), vehicle,
                    DateTimeOffset.UnixEpoch.AddMinutes(index), seconds, true, null,
                    sectors.Select(sector => new LapSegment(sector.Index, seconds / sectors.Count, true)).ToArray(), []));
            }

            var retained = store.LoadLaps(track.Id, 100);
            Assert.HasCount(LazyForzaStore.MaxLapsPerTrack, retained);
            Assert.IsTrue(retained.Any(lap => lap.Id == lapIds[0]), "S1 最旧但为该等级历史最快的圈必须受自动清理保护。");
            Assert.IsTrue(retained.Any(lap => lap.Id == lapIds[1]), "S2 最旧但为该等级历史最快的圈必须受自动清理保护。");
            Assert.IsFalse(retained.Any(lap => lap.Id == lapIds[2]), "最旧的 S1 非最快圈应优先自动删除。");
            Assert.IsFalse(retained.Any(lap => lap.Id == lapIds[3]), "最旧的 S2 非最快圈应优先自动删除。");

            store.DeleteLap(lapIds[0]);
            Assert.AreEqual(LazyForzaStore.MaxLapsPerTrack - 1, store.CountLaps(track.Id));
            Assert.IsFalse(store.LoadLaps(track.Id, 100).Any(lap => lap.Id == lapIds[0]));

            var protectedClassBests = store.LoadLaps(track.Id, 100)
                .Where(lap => lap.IsValid)
                .GroupBy(lap => lap.Vehicle.CarClass)
                .Select(group => group.OrderBy(lap => lap.TotalSeconds).First().Id)
                .ToArray();
            store.DeleteTrackLaps(track.Id, preserveLapIds: protectedClassBests);
            var protectedLaps = store.LoadLaps(track.Id, 100);
            Assert.HasCount(2, protectedLaps);
            CollectionAssert.AreEquivalent(protectedClassBests, protectedLaps.Select(lap => lap.Id).ToArray(),
                "批量删除未选择历史最快时，数据库必须保留每个性能等级最快的有效圈。");
            Assert.IsNotNull(store.LoadTrack(track.Id), "保留历史最快时不能删除赛道模板。");

            store.DeleteTrackLaps(track.Id, performanceClasses: [4]);
            var classScopedRemaining = store.LoadLaps(track.Id, 100);
            Assert.HasCount(1, classScopedRemaining);
            Assert.AreEqual(5, classScopedRemaining[0].Vehicle.CarClass,
                "仅删除选中性能等级时，其他等级的圈速必须完整保留。");

            store.DeleteTrackLaps(track.Id);
            Assert.AreEqual(0, store.CountLaps(track.Id));
            Assert.IsNotNull(store.LoadTrack(track.Id), "批量删除圈速记录不能一并删除赛道模板。");
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [TestMethod]
    public void EmbeddedPlaygroundCatalogIsCompleteIdempotentAndReadOnly()
    {
        var path = TempDatabasePath();
        try
        {
            using var store = new LazyForzaStore(path);
            var firstImport = PlaygroundOfficialTrackCatalog.EnsureImported(store);
            Assert.AreEqual("2026.07.23.1", firstImport.Version);
            Assert.AreEqual(85, firstImport.TotalTracks);
            Assert.AreEqual(85, firstImport.ImportedTracks);
            Assert.AreEqual(85, store.CountTracks(TrackCatalogKind.PlaygroundOfficial));

            var official = store.ListTracks()
                .First(track => track.CatalogKind == TrackCatalogKind.PlaygroundOfficial);
            Assert.IsFalse(string.IsNullOrWhiteSpace(official.Category));
            Assert.IsFalse(official.Name.Contains('|'),
                "The display name must not repeat the category prefix stored separately for grid grouping.");
            var loaded = store.LoadTrack(official.Id);
            Assert.IsNotNull(loaded);
            Assert.AreEqual(TrackCatalogKind.PlaygroundOfficial, loaded.Value.Track.CatalogKind);
            Assert.IsTrue(loaded.Value.Track.Points.Count >= 4);
            Assert.IsTrue(loaded.Value.Sectors.Count > 0);

            Assert.ThrowsExactly<InvalidOperationException>(() => store.RenameTrack(official.Id, "不可修改"));
            Assert.ThrowsExactly<InvalidOperationException>(() => store.DeleteTrack(official.Id));
            Assert.IsNotNull(store.LoadTrack(official.Id));

            var secondImport = PlaygroundOfficialTrackCatalog.EnsureImported(store);
            Assert.AreEqual(0, secondImport.ImportedTracks);
            Assert.AreEqual(85, store.CountTracks(TrackCatalogKind.PlaygroundOfficial));
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    private static ShiftLearningSnapshot VehicleSnapshot(
        VehicleProfileFingerprint fingerprint,
        params (int Gear, double Slope)[] gears) =>
        new(
            LearningState.Ready,
            1,
            0.9,
            fingerprint,
            [new EngineCurveBin(7_200, 20, 385_000, 530, 12, 3, 0.9)],
            gears
                .Select(gear => new GearModel(gear.Gear, gear.Slope, 30, 0.9))
                .ToArray(),
            gears.Length >= 2
                ? [new ShiftTarget(
                    gears[0].Gear,
                    gears[1].Gear,
                    8_200,
                    7_800,
                    6_000,
                    0.88,
                    false)]
                : [],
            new Dictionary<string, int>(),
            "ready");

    private static string TempDatabasePath() => Path.Combine(Path.GetTempPath(), $"lazyforza-{Guid.NewGuid():N}.db");

    private static void DeleteDatabase(string path)
    {
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            var file = path + suffix;
            for (var attempt = 0; attempt < 8 && File.Exists(file); attempt++)
            {
                try
                {
                    File.Delete(file);
                }
                catch (IOException) when (attempt < 7)
                {
                    Thread.Sleep(25);
                }
                catch (IOException)
                {
                    // A prior connection in the same Windows test host can retain
                    // the migrated file until process exit. The database is unique
                    // test data and remains eligible for OS temp cleanup.
                    break;
                }
            }
        }
    }
}
