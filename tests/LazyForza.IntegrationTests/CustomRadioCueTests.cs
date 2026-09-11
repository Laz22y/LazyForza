using System.Text.Json;
using LazyForza.App;
using LazyForza.Speech;
using LazyForza.Storage;

namespace LazyForza.IntegrationTests;

[TestClass]
public sealed class CustomRadioCueTests
{
    [TestMethod]
    public void ImportedCopySurvivesSettingsReopenAndSlotsCanResetIndependently()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lazyforza-cues-{Guid.NewGuid():N}.db");
        try
        {
            var connect = new CustomRadioCue("my-connect.wav", RadioCues.Connect);
            var disconnect = new CustomRadioCue("my-disconnect.mp3", RadioCues.Disconnect);
            using (var store = new LazyForzaStore(path))
            {
                Assert.IsNull(store.GetAppSetting("raceEngineer.connectCue"));
                store.SetAppSetting("raceEngineer.connectCue", connect.Serialize());
                store.SetAppSetting("raceEngineer.disconnectCue", disconnect.Serialize());
            }
            using (var store = new LazyForzaStore(path))
            {
                Assert.IsTrue(CustomRadioCue.TryDeserialize(store.GetAppSetting("raceEngineer.connectCue"), out var copy));
                Assert.AreEqual(connect.Name, copy!.Name);
                CollectionAssert.AreEqual(connect.Audio.CopySamples(), copy.Audio.CopySamples());
                store.SetAppSetting("raceEngineer.connectCue", "");
            }
            using (var store = new LazyForzaStore(path))
            {
                Assert.IsFalse(CustomRadioCue.TryDeserialize(store.GetAppSetting("raceEngineer.connectCue"), out _));
                Assert.IsTrue(CustomRadioCue.TryDeserialize(store.GetAppSetting("raceEngineer.disconnectCue"), out var copy));
                Assert.AreEqual(disconnect.Name, copy!.Name);
                CollectionAssert.AreEqual(disconnect.Audio.CopySamples(), copy.Audio.CopySamples());
            }
        }
        finally { File.Delete(path); File.Delete(path + "-wal"); File.Delete(path + "-shm"); }
    }

    [TestMethod]
    public void CorruptOversizedAndUnknownStoredCuesFailWithoutThrowing()
    {
        string Stored(object? name, string pcm, int version = 1) => JsonSerializer.Serialize(new { Version = version, Name = name, Pcm16 = pcm });
        foreach (var value in new string?[]
        {
            null, "", "{", "null", new('x', 400000),
            Stored("cue", "not base64"), Stored("cue", "AA=="), Stored("cue", ""),
            Stored(null, "AAA="), Stored("cue", "AAA=", 2), Stored("bad\nname", "AAA="),
            Stored("cue", Convert.ToBase64String(new byte[CustomRadioCue.MaximumPcmBytes + 2]))
        })
        {
            Assert.IsFalse(CustomRadioCue.TryDeserialize(value, out var cue));
            Assert.IsNull(cue);
        }
    }

    [TestMethod]
    public void CustomCuesEnforceDecodedFormatAndDurationBounds()
    {
        var maximum = new CustomRadioCue("five seconds", new SpeechAudio(new byte[CustomRadioCue.MaximumPcmBytes], 24000));
        Assert.IsTrue(CustomRadioCue.TryDeserialize(maximum.Serialize(), out _));
        Assert.ThrowsExactly<ArgumentException>(() => new CustomRadioCue("long", new SpeechAudio(new byte[CustomRadioCue.MaximumPcmBytes + 2], 24000)));
        Assert.ThrowsExactly<ArgumentException>(() => new CustomRadioCue("wrong rate", new SpeechAudio(new byte[4800], 48000)));
        Assert.ThrowsExactly<ArgumentException>(() => new CustomRadioCue("stereo", new SpeechAudio(new byte[4800], 24000, 2)));
        Assert.ThrowsExactly<ArgumentException>(() => (RadioTransmission.Default with { Connect = new SpeechAudio(new byte[288000], 24000) }).Validate());
    }

    [TestMethod]
    public void ImportRejectsUnsupportedOrOversizedFilesBeforeDecodingAndHonorsCancellation()
    {
        Assert.ThrowsExactly<InvalidDataException>(() => RadioCueImporter.Import("not-audio.txt", CancellationToken.None));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => RadioCueImporter.Import("missing.wav", cancelled.Token));
        var path = Path.Combine(Path.GetTempPath(), $"lazyforza-cue-{Guid.NewGuid():N}.wav");
        try
        {
            using (var file = File.Create(path)) file.SetLength(RadioCueImporter.MaximumFileBytes + 1L);
            Assert.ThrowsExactly<InvalidDataException>(() => RadioCueImporter.Import(path, CancellationToken.None));
            File.Delete(path); // The rejected file's handle has also been released.
        }
        finally { File.Delete(path); }
    }
}
