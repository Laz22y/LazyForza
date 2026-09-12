using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Threading.Channels;
using LazyForza.Modules.EstateRace;
using LazyForza.Speech;

namespace LazyForza.IntegrationTests;

[TestClass]
public sealed class SpeechPipelineTests
{
    private static readonly SpeechAudio Audio = new(new byte[4800], 24000);

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task RadioSwitchesIndependentlySkipCueAndItsPause(bool connect, bool disconnect)
    {
        var player = new Player();
        var pauses = new List<TimeSpan>();
        var settings = RadioTransmission.Default with { ConnectEnabled = connect, DisconnectEnabled = disconnect };
        await using var output = new RadioSpeechOutput(new Provider(), player, "zh-CN",
            transmissionSettings: () => settings,
            pause: (delay, _) => { pauses.Add(delay); return Task.CompletedTask; });
        await output.SpeakAsync("示例", 60, CancellationToken.None);
        CollectionAssert.AreEqual(new[] { connect ? RadioCues.Connect : null, Audio, disconnect ? RadioCues.Disconnect : null }
            .Where(item => item is not null).ToArray(), player.Played.Select(item => item.Audio).ToArray());
        CollectionAssert.AreEqual(new[] { connect ? settings.AfterConnect : TimeSpan.Zero, disconnect ? settings.BeforeDisconnect : TimeSpan.Zero }
            .Where(delay => delay > TimeSpan.Zero).ToArray(), pauses.ToArray());
    }

    [TestMethod]
    public async Task ProviderReceivesPlainTextVoiceLanguageAndRateAndCacheDoesNotBakeVolume()
    {
        var provider = new Provider();
        var player = new Player();
        await using var output = new RadioSpeechOutput(provider, player, "zh-CN", "voice-1", 1.1);
        const string text = "<driver> 罚时 5 秒。";
        await output.SpeakAsync(text, 70, CancellationToken.None);
        var request = provider.Requests.Single();
        Assert.AreEqual(text, request.Text);
        Assert.AreEqual("zh-CN", request.Language);
        Assert.AreEqual("voice-1", request.VoiceId);
        Assert.AreEqual(1.1, request.Rate);
        await output.SpeakAsync(text, 20, CancellationToken.None);
        Assert.AreEqual(1, provider.Requests.Count);
        var played = player.Played.ToArray();
        Assert.AreEqual(6, played.Length);
        Assert.AreSame(RadioCues.Connect, played[0].Audio);
        Assert.AreSame(Audio, played[1].Audio);
        Assert.AreSame(RadioCues.Disconnect, played[2].Audio);
        Assert.IsTrue(played.Take(3).All(item => item.Volume == 70));
        Assert.IsTrue(played.Skip(3).All(item => item.Volume == 20));
    }

    [TestMethod]
    public async Task CancelledSlowProviderCannotPlayLateAudioOrBlockItsReplacement()
    {
        var pending = Channel.CreateUnbounded<TaskCompletionSource<SpeechAudio>>();
        var provider = new Provider((_, _) =>
        {
            var reply = new TaskCompletionSource<SpeechAudio>(TaskCreationOptions.RunContinuationsAsynchronously);
            pending.Writer.TryWrite(reply);
            return reply.Task; // Deliberately ignores cancellation.
        });
        var player = new Player();
        await using var output = new RadioSpeechOutput(provider, player, "zh-CN");
        using var cancelled = new CancellationTokenSource();
        var old = output.SpeakAsync("old stage", 70, cancelled.Token);
        var oldReply = await Read(pending.Reader);
        cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => old.WaitAsync(TimeSpan.FromSeconds(2)));
        var fresh = output.SpeakAsync("new stage", 70, CancellationToken.None);
        var newReply = await Read(pending.Reader);
        newReply.SetResult(Audio);
        await fresh.WaitAsync(TimeSpan.FromSeconds(2));
        oldReply.SetResult(new SpeechAudio(new byte[2400], 24000));
        await Task.Delay(30);
        Assert.AreEqual(3, player.Played.Count);
        Assert.AreSame(Audio, player.Played.ElementAt(1).Audio);
    }

    [TestMethod]
    public async Task TimeoutCancelsProviderWithoutPlayingAnEmptyRadioTransmission()
    {
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new Provider(async (_, token) =>
        {
            using var registration = token.Register(() => cancelled.TrySetResult());
            await Task.Delay(Timeout.Infinite, token);
            return Audio;
        });
        var player = new Player();
        await using var output = new RadioSpeechOutput(provider, player, "en-US", synthesisTimeout: TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsExactlyAsync<TimeoutException>(() => output.SpeakAsync("timeout", 70, CancellationToken.None));
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual(0, player.Played.Count);
    }

    [TestMethod]
    [DataRow("stage")]
    [DataRow("mute")]
    [DataRow("emergency")]
    public async Task EngineerCanReplaceSlowSynthesisWithoutPlayingStaleAudio(string transition)
    {
        var pending = Channel.CreateUnbounded<TaskCompletionSource<SpeechAudio>>();
        var provider = new Provider((_, _) =>
        {
            var reply = new TaskCompletionSource<SpeechAudio>(TaskCreationOptions.RunContinuationsAsynchronously);
            pending.Writer.TryWrite(reply);
            return reply.Task;
        });
        var transmitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var player = new Player((index, _) =>
        {
            if (index == 2) transmitted.TrySetResult();
            return Task.CompletedTask;
        });
        using var engineer = new RaceEngineer(new RadioSpeechOutput(provider, player, "zh-CN"));
        engineer.Configure(true, false, 70);
        engineer.SetStage("race");
        var message = new EngineerMessage("old", "old", "old", EngineerPriority.Information,
            TimeSpan.Zero, DateTimeOffset.UtcNow.AddSeconds(30));
        engineer.Enqueue(message);
        var oldReply = await Read(pending.Reader);
        if (transition == "stage") engineer.SetStage("new race");
        if (transition == "mute")
        {
            engineer.Configure(true, true, 70);
            engineer.Configure(true, false, 70);
        }
        engineer.Enqueue(message with { Key = "fresh", Text = "fresh", Category = "fresh", Priority = EngineerPriority.Emergency });
        var freshReply = await Read(pending.Reader);
        freshReply.SetResult(Audio);
        await transmitted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        // A late failure from the cancelled stage/request cannot disable the replacement.
        oldReply.SetException(new InvalidOperationException("Late provider failure"));
        engineer.Dispose();
        await engineer.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsNull(engineer.Error);
        Assert.AreEqual(3, player.Played.Count);
        Assert.AreSame(Audio, player.Played.ElementAt(1).Audio);
        Assert.AreEqual(1, provider.DisposeCount);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public async Task CancelAtAnyPlaybackPartStopsWithoutStartingTheNextPart(int blockedPart)
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var player = new Player(async (index, token) =>
        {
            if (index != blockedPart) return;
            reached.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        });
        await using var output = new RadioSpeechOutput(new Provider(), player, "zh-CN");
        using var cancellation = new CancellationTokenSource();
        var speaking = output.SpeakAsync("stop", 70, cancellation.Token);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => speaking.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(blockedPart + 1, player.Played.Count);
    }

    [TestMethod]
    public async Task ProviderFailuresAreNotCachedAndSilentOutputDoesNotContactProvider()
    {
        var attempts = 0;
        var provider = new Provider((_, _) => ++attempts == 1
            ? Task.FromException<SpeechAudio>(new InvalidOperationException("failed")) : Task.FromResult(Audio));
        var player = new Player();
        await using var output = new RadioSpeechOutput(provider, player, "zh-CN");
        await output.SpeakAsync("same", 0, CancellationToken.None);
        Assert.AreEqual(0, attempts);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => output.SpeakAsync("same", 70, CancellationToken.None));
        Assert.AreEqual(0, player.Played.Count);
        await output.SpeakAsync("same", 70, CancellationToken.None);
        Assert.AreEqual(2, attempts);
        Assert.AreEqual(3, player.Played.Count);
    }

    [TestMethod]
    public async Task CacheEvictsOldPhrasesAndRespectsItsByteBudget()
    {
        var provider = new Provider();
        await using (var output = new RadioSpeechOutput(provider, new Player(), "zh-CN"))
        {
            for (var i = 0; i < 33; i++) await output.SpeakAsync($"phrase {i}", 70, CancellationToken.None);
            await output.SpeakAsync("phrase 32", 70, CancellationToken.None);
            Assert.AreEqual(33, provider.Requests.Count);
            await output.SpeakAsync("phrase 0", 70, CancellationToken.None);
            Assert.AreEqual(34, provider.Requests.Count);
        }
        var large = new SpeechAudio(new byte[2 * 1024 * 1024], 48000);
        var largeProvider = new Provider((_, _) => Task.FromResult(large));
        await using var bounded = new RadioSpeechOutput(largeProvider, new Player(), "zh-CN");
        foreach (var text in new[] { "a", "b", "c", "b", "a" })
            await bounded.SpeakAsync(text, 70, CancellationToken.None);
        Assert.AreEqual(4, largeProvider.Requests.Count);
    }

    [TestMethod]
    public async Task MisbehavingProvidersCannotAccumulateUnlimitedCancelledRequests()
    {
        var pending = Channel.CreateUnbounded<TaskCompletionSource<SpeechAudio>>();
        var provider = new Provider((_, _) =>
        {
            var result = new TaskCompletionSource<SpeechAudio>(TaskCreationOptions.RunContinuationsAsynchronously);
            pending.Writer.TryWrite(result);
            return result.Task;
        });
        var player = new Player();
        await using var output = new RadioSpeechOutput(provider, player, "zh-CN");
        var replies = new List<TaskCompletionSource<SpeechAudio>>();
        for (var i = 0; i < 2; i++)
        {
            using var cancellation = new CancellationTokenSource();
            var work = output.SpeakAsync($"cancel {i}", 70, cancellation.Token);
            replies.Add(await Read(pending.Reader));
            cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => work);
        }
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => output.SpeakAsync("overflow", 70, CancellationToken.None));
        Assert.AreEqual(2, provider.Requests.Count);
        Assert.AreEqual(0, player.Played.Count);
        foreach (var reply in replies) reply.SetResult(Audio);
    }

    [TestMethod]
    public async Task DisposeCancelsOutputReleasesBothDependenciesOnceAndRejectsNewSpeech()
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var player = new Player(async (_, token) =>
        {
            reached.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        });
        var provider = new Provider();
        var output = new RadioSpeechOutput(provider, player, "zh-CN");
        var work = output.SpeakAsync("dispose", 70, CancellationToken.None);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await output.DisposeAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => work);
        await output.DisposeAsync();
        Assert.AreEqual(1, provider.DisposeCount);
        Assert.AreEqual(1, player.DisposeCount);
        await Assert.ThrowsAsync<OperationCanceledException>(() => output.SpeakAsync("after", 70, CancellationToken.None));
    }

    [TestMethod]
    public void PcmValidationOwnershipAndGainProtectThePlayer()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new SpeechAudio([], 24000));
        Assert.ThrowsExactly<ArgumentException>(() => new SpeechAudio([0], 24000));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new SpeechAudio([0, 0], 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new SpeechAudio([0, 0], 24000, 3));
        Assert.ThrowsExactly<ArgumentException>(() => new SpeechAudio(new byte[24000 * 2 * 31], 24000));
        byte[] original = [0xFF, 0x7F, 0x00, 0x80];
        var audio = new SpeechAudio(original, 24000);
        original[0] = 0;
        Assert.AreEqual(short.MaxValue, BinaryPrimitives.ReadInt16LittleEndian(audio.Samples.Span));
        Assert.IsTrue(audio.CopySamples(0).All(value => value == 0));
        CollectionAssert.AreEqual(audio.CopySamples(100), audio.CopySamples(200));
        Assert.AreEqual(16384, BinaryPrimitives.ReadInt16LittleEndian(audio.CopySamples(50)));
    }

    [TestMethod]
    public void OriginalCuesAreShortDistinctFadedAndBelowClipping()
    {
        foreach (var cue in new[] { RadioCues.Connect, RadioCues.Disconnect })
        {
            Assert.IsTrue(cue.Duration.TotalMilliseconds is >= 100 and <= 250);
            var samples = cue.Samples.ToArray();
            var values = Enumerable.Range(0, samples.Length / 2)
                .Select(i => (int)BinaryPrimitives.ReadInt16LittleEndian(samples.AsSpan(i * 2))).ToArray();
            Assert.AreEqual(0, values[0]);
            Assert.AreEqual(0, values[^1]);
            Assert.IsTrue(values.Max(Math.Abs) < short.MaxValue / 3);
            Assert.IsTrue(values.Any(value => Math.Abs(value) > 2000));
            Assert.IsTrue(Math.Abs(values.Average()) < 20, "No material DC offset.");
        }
        Assert.AreNotEqual(RadioCues.Connect.Duration, RadioCues.Disconnect.Duration);
    }

    private static async Task<T> Read<T>(ChannelReader<T> reader) =>
        await reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

    [TestMethod]
    public async Task CustomCuesAndPausesUseOneSnapshotForEachTransmission()
    {
        var connect = new SpeechAudio(new byte[2400], 24000);
        var disconnect = new SpeechAudio(new byte[1200], 24000);
        var settings = RadioTransmission.Default with { Connect = connect, Disconnect = disconnect };
        var player = new Player();
        var pauses = new List<(TimeSpan Duration, int Played)>();
        Task Pause(TimeSpan duration, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            pauses.Add((duration, player.Played.Count));
            settings = RadioTransmission.Default; // A settings change must not mix cue pairs mid-transmission.
            return Task.CompletedTask;
        }
        await using var output = new RadioSpeechOutput(new Provider(), player, "zh-CN",
            transmissionSettings: () => settings, pause: Pause);
        await output.SpeakAsync("sample", 42, CancellationToken.None);
        Assert.AreSame(connect, player.Played.First().Audio);
        Assert.AreSame(disconnect, player.Played.Last().Audio);
        Assert.AreEqual((TimeSpan.FromMilliseconds(180), 1), pauses[0]);
        Assert.AreEqual((TimeSpan.FromMilliseconds(220), 2), pauses[1]);
        Assert.IsTrue(player.Played.All(item => item.Volume == 42));
        await output.SpeakAsync("sample", 31, CancellationToken.None);
        Assert.AreSame(RadioCues.Connect, player.Played.ElementAt(3).Audio);
        Assert.AreSame(RadioCues.Disconnect, player.Played.Last().Audio);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    public async Task CancellationDuringEitherPauseStopsBeforeTheNextAudio(int blockedPause)
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var index = 0;
        async Task Pause(TimeSpan _, CancellationToken token)
        {
            if (index++ != blockedPause) return;
            reached.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        }
        var player = new Player();
        await using var output = new RadioSpeechOutput(new Provider(), player, "zh-CN", pause: Pause);
        using var cancel = new CancellationTokenSource();
        var speaking = output.SpeakAsync("sample", 70, cancel.Token);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancel.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => speaking);
        Assert.AreEqual(blockedPause + 1, player.Played.Count);
    }

    private sealed class Provider(Func<SpeechSynthesisRequest, CancellationToken, Task<SpeechAudio>>? action = null) : ISpeechSynthesisProvider
    {
        public string Id => "test";
        public SpeechProviderLocation Location => SpeechProviderLocation.Local;
        public ConcurrentQueue<SpeechSynthesisRequest> Requests { get; } = new();
        public int DisposeCount { get; private set; }
        public Task<SpeechAudio> SynthesizeAsync(SpeechSynthesisRequest request, CancellationToken token)
        {
            Requests.Enqueue(request);
            return action?.Invoke(request, token) ?? Task.FromResult(Audio);
        }
        public ValueTask DisposeAsync() { DisposeCount++; return ValueTask.CompletedTask; }
    }

    private sealed class Player(Func<int, CancellationToken, Task>? action = null) : ISpeechAudioPlayer
    {
        public ConcurrentQueue<(SpeechAudio Audio, int Volume)> Played { get; } = new();
        public int DisposeCount { get; private set; }
        public Task PlayAsync(SpeechAudio audio, int volume, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var index = Played.Count;
            Played.Enqueue((audio, volume));
            return action?.Invoke(index, token) ?? Task.CompletedTask;
        }
        public ValueTask DisposeAsync() { DisposeCount++; return ValueTask.CompletedTask; }
    }
}
