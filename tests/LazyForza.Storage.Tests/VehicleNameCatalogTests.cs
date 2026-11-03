using System.Text.Json;
using LazyForza.Storage;

namespace LazyForza.Storage.Tests;

[TestClass]
public sealed class VehicleNameCatalogTests
{
    [TestMethod]
    public async Task CacheRoundTripsAndFallsBackForMissingCorruptOrOlderData()
    {
        var root = Path.Combine(Path.GetTempPath(), "lfz-names-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "names.json");
        try
        {
            var cache = new VehicleNameCatalogCache(path);
            var bundled = VehicleNameCatalog.EmbeddedSnapshot;
            Assert.AreSame(bundled, cache.Load(bundled));
            var snapshot = Snapshot('a', bundled.Info!.UpdatedAt.AddDays(1));
            await cache.SaveAsync(snapshot, CancellationToken.None);
            var loaded = cache.Load(bundled);
            Assert.AreEqual(snapshot.Info, loaded.Info);
            Assert.AreEqual("Car 100", loaded.Names[100]);
            var newerBundled = Snapshot('b', snapshot.Info!.UpdatedAt.AddDays(1));
            Assert.AreSame(newerBundled, cache.Load(newerBundled));
            foreach (var broken in new[] { "not json", "{}", "{\"SchemaVersion\":99}",
                         JsonSerializer.Serialize(new { SchemaVersion = 1, Info = snapshot.Info with { Revision = (string)null! }, Names = snapshot.Names }),
                         JsonSerializer.Serialize(new { SchemaVersion = 1, Info = snapshot.Info, Names = new Dictionary<int, string> { [1] = null! } }) })
            {
                await File.WriteAllTextAsync(path, broken);
                Assert.AreSame(bundled, cache.Load(bundled));
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task CancellationAndInvalidReplacementLeaveThePreviousCacheIntact()
    {
        var root = Path.Combine(Path.GetTempPath(), "lfz-names-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "names.json");
        try
        {
            var cache = new VehicleNameCatalogCache(path);
            var original = Snapshot('a', DateTimeOffset.UtcNow);
            await cache.SaveAsync(original, CancellationToken.None);
            var before = await File.ReadAllBytesAsync(path);
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => cache.SaveAsync(Snapshot('b', DateTimeOffset.UtcNow), cancelled.Token));
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => cache.SaveAsync(new(null, new Dictionary<int, string>()), CancellationToken.None));
            CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(path));
            Assert.AreEqual(1, Directory.GetFiles(root).Length);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public void UpstreamRejectsDuplicateOrdinalsIncompleteMapsAndUnsafeNames()
    {
        var valid = Enumerable.Range(1, 100).ToDictionary(i => "Car " + i, i => i.ToString());
        var revision = new string('a', 40);
        Assert.AreEqual(100, VehicleNameSnapshot.FromUpstream(JsonSerializer.Serialize(valid), revision, DateTimeOffset.UtcNow).Names.Count);
        foreach (var invalid in new[] { "{}", "[]", JsonSerializer.Serialize(valid.Append(new KeyValuePair<string, string>("Other", "1")).ToDictionary()),
            JsonSerializer.Serialize(valid.ToDictionary(pair => pair.Key == "Car 1" ? "Bad\u202ename" : pair.Key, pair => pair.Value)) })
            Assert.ThrowsExactly<InvalidDataException>(() => VehicleNameSnapshot.FromUpstream(invalid, revision, DateTimeOffset.UtcNow));
    }

    private static VehicleNameSnapshot Snapshot(char revision, DateTimeOffset at) => VehicleNameSnapshot.FromUpstream(
        JsonSerializer.Serialize(Enumerable.Range(1, 100).ToDictionary(i => "Car " + i, i => i)), new string(revision, 40), at);
}
