using System.Net;
using System.Text;
using System.Text.Json;
using LazyForza.App;
using LazyForza.Speech;

namespace LazyForza.IntegrationTests;

[TestClass]
public sealed class ElevenLabsSpeechTests
{
    [TestMethod]
    public async Task OfficialRequestProducesPcmAndDoesNotPutCredentialsInBodyOrUri()
    {
        var handler = new Handler(async (request, token) =>
        {
            Assert.AreEqual("https://api.elevenlabs.io/v1/text-to-speech/voice_1?output_format=pcm_24000", request.RequestUri!.ToString());
            Assert.AreEqual("test-private-key", request.Headers.GetValues("xi-api-key").Single());
            var body = await request.Content!.ReadAsStringAsync(token);
            Assert.IsFalse(body.Contains("test-private-key"));
            using var json = JsonDocument.Parse(body);
            Assert.AreEqual("罚时五秒。", json.RootElement.GetProperty("text").GetString());
            Assert.AreEqual("eleven_flash_v2_5", json.RootElement.GetProperty("model_id").GetString());
            Assert.AreEqual(1.0, json.RootElement.GetProperty("voice_settings").GetProperty("speed").GetDouble());
            Assert.IsFalse(json.RootElement.TryGetProperty("language_code", out _));
            return Pcm();
        });
        await using var provider = new ElevenLabsSpeechProvider("test-private-key", handler: handler);
        var audio = await provider.SynthesizeAsync(new("罚时五秒。", "zh-CN", "voice_1"), CancellationToken.None);
        Assert.AreEqual(24000, audio.SampleRate);
        Assert.AreEqual(1, audio.Channels);
        Assert.AreEqual(4800, audio.Samples.Length);
        Assert.AreEqual(SpeechProviderLocation.Online, provider.Location);
    }

    [TestMethod]
    public async Task VoicePaginationUsesReturnedTokenAndDeduplicatesIds()
    {
        var count = 0;
        var handler = new Handler((request, _) =>
        {
            count++;
            if (count == 2) StringAssert.Contains(request.RequestUri!.Query, "next_page_token=next%2Fpage");
            return Task.FromResult(Json(count == 1
                ? """{"voices":[{"voice_id":"v1","name":"One"}],"has_more":true,"next_page_token":"next/page"}"""
                : """{"voices":[{"voice_id":"v1","name":"One"},{"voice_id":"v2","name":"Two"}],"has_more":false}"""));
        });
        await using var provider = new ElevenLabsSpeechProvider("test", handler: handler);
        var voices = await provider.GetVoicesAsync(CancellationToken.None);
        Assert.AreEqual(2, voices.Count);
        Assert.AreEqual(2, count);
    }

    [TestMethod]
    [DataRow(401, SpeechServiceFailure.Authentication)]
    [DataRow(403, SpeechServiceFailure.Authentication)]
    [DataRow(429, SpeechServiceFailure.RateLimited)]
    [DataRow(422, SpeechServiceFailure.Configuration)]
    [DataRow(500, SpeechServiceFailure.Unavailable)]
    [DataRow(302, SpeechServiceFailure.Unavailable)]
    public async Task FailuresAreSanitizedAndDoNotRetryPaidRequests(int status, SpeechServiceFailure expected)
    {
        var count = 0;
        var handler = new Handler((_, _) =>
        {
            count++;
            var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("echoed-private-key") };
            response.Headers.RetryAfter = new(TimeSpan.FromSeconds(120));
            return Task.FromResult(response);
        });
        await using var provider = new ElevenLabsSpeechProvider("private-key", handler: handler);
        var error = await Assert.ThrowsExactlyAsync<SpeechServiceException>(() => provider.SynthesizeAsync(new("sample", "en-US", "v1"), CancellationToken.None));
        Assert.AreEqual(expected, error.Failure);
        Assert.AreEqual(TimeSpan.FromSeconds(120), error.RetryAfter);
        Assert.IsFalse(error.ToString().Contains("private-key"));
        Assert.AreEqual(1, count);
    }

    [TestMethod]
    [DataRow(0, "application/octet-stream")]
    [DataRow(3, "application/octet-stream")]
    [DataRow(1440002, "application/octet-stream")]
    [DataRow(40, "application/json")]
    public async Task InvalidAndOversizedAudioNeverReachesPlayback(int length, string contentType)
    {
        var handler = new Handler((_, _) => Task.FromResult(Pcm(length, contentType)));
        await using var provider = new ElevenLabsSpeechProvider("test", handler: handler);
        await Assert.ThrowsExactlyAsync<SpeechServiceException>(() => provider.SynthesizeAsync(new("sample", "en-US", "v1"), CancellationToken.None));
    }

    [TestMethod]
    public async Task CancellationAndDisposalAbortTheHttpRequest()
    {
        foreach (var dispose in new[] { false, true })
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var handler = new Handler(async (_, token) =>
            {
                entered.SetResult(); await Task.Delay(Timeout.Infinite, token); return Pcm();
            });
            await using var provider = new ElevenLabsSpeechProvider("test", handler: handler);
            using var cancellation = new CancellationTokenSource();
            var pending = provider.SynthesizeAsync(new("sample", "en-US", "v1"), cancellation.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            if (dispose) await provider.DisposeAsync(); else cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(2)));
        }
    }

    [TestMethod]
    public async Task RateLimitFallsBackAndRetryAfterSuppressesFurtherRequestsWithoutCachingFallbackVoice()
    {
        var now = DateTimeOffset.UtcNow;
        var count = 0;
        var handler = new Handler((_, _) =>
        {
            count++;
            if (count > 1) return Task.FromResult(Pcm());
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new(TimeSpan.FromSeconds(120));
            return Task.FromResult(response);
        });
        var local = new LocalProvider();
        await using var provider = new FallbackSpeechProvider(new ElevenLabsSpeechProvider("test", handler: handler), local, () => now);
        var request = new SpeechSynthesisRequest("sample", "en-US", "v1");
        Assert.IsFalse((await provider.SynthesizeAsync(request, CancellationToken.None)).Cacheable);
        now = now.AddSeconds(70);
        await provider.SynthesizeAsync(request, CancellationToken.None);
        Assert.AreEqual(1, count);
        Assert.AreEqual(2, local.Calls);
        Assert.AreEqual(SpeechServiceFailure.RateLimited, provider.LastFailure);
        now = now.AddSeconds(51);
        Assert.IsTrue((await provider.SynthesizeAsync(request, CancellationToken.None)).Cacheable);
        Assert.AreEqual(2, count);
        Assert.IsNull(provider.LastFailure);
    }

    [TestMethod]
    public async Task TimeoutIsRecoverableButExplicitCancellationDoesNotStartLocalSpeech()
    {
        var handler = new Handler(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return Pcm(); });
        var local = new LocalProvider();
        await using var provider = new FallbackSpeechProvider(
            new ElevenLabsSpeechProvider("test", handler: handler, timeout: TimeSpan.FromMilliseconds(60)), local);
        var request = new SpeechSynthesisRequest("sample", "en-US", "v1");
        await provider.SynthesizeAsync(request, CancellationToken.None);
        Assert.AreEqual(SpeechServiceFailure.Timeout, provider.LastFailure);
        Assert.AreEqual(1, local.Calls);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => provider.SynthesizeAsync(request, cancelled.Token));
        Assert.AreEqual(1, local.Calls);
    }

    [TestMethod]
    public void SettingsRoundTripProtectsKeyAndKeepsWindowsAsTheDefault()
    {
        Assert.IsFalse(EngineerSpeechSettings.Load(null).UseElevenLabs);
        var protectedKey = EngineerCredentialProtection.Protect("test-secret-only");
        var settings = new EngineerSpeechSettings(true, protectedKey, "v1", Language: "zh-CN", FallbackToWindows: false);
        var stored = settings.Serialize();
        Assert.IsFalse(stored.Contains("test-secret-only"));
        var restored = EngineerSpeechSettings.Load(stored);
        Assert.AreEqual(settings, restored);
        Assert.IsTrue(EngineerCredentialProtection.TryUnprotect(restored.ProtectedApiKey, out var secret));
        Assert.AreEqual("test-secret-only", secret);
        Assert.IsFalse(EngineerCredentialProtection.TryUnprotect("not-dpapi", out _));
        Assert.IsFalse(EngineerSpeechSettings.Load("{broken").UseElevenLabs);
    }

    private static HttpResponseMessage Pcm(int length = 4800, string type = "application/octet-stream")
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[length]) };
        response.Content.Headers.ContentType = new(type); return response;
    }
    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
    private sealed class LocalProvider : ISpeechSynthesisProvider
    {
        public int Calls { get; private set; }
        public string Id => "local";
        public SpeechProviderLocation Location => SpeechProviderLocation.Local;
        public Task<SpeechAudio> SynthesizeAsync(SpeechSynthesisRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); Assert.IsNull(request.VoiceId);
            Calls++; return Task.FromResult(new SpeechAudio(new byte[4800], 24000));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
