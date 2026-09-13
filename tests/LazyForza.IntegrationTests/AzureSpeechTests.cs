using System.IO;
using System.Net;
using System.Text;
using System.Xml.Linq;
using LazyForza.App;
using LazyForza.Speech;

namespace LazyForza.IntegrationTests;

[TestClass]
public sealed class AzureSpeechTests
{
    [TestMethod]
    [DataRow(1.0)]
    [DataRow(1.25)]
    public async Task RegionalRequestEscapesSsmlAndProducesPcm(double rate)
    {
        const string spoken = "罚时五秒。<voice name='injection'> & \"车手\"";
        var handler = new Handler(async (request, token) =>
        {
            Assert.AreEqual("https://eastasia.tts.speech.microsoft.com/cognitiveservices/v1", request.RequestUri!.ToString());
            Assert.AreEqual("azure-private-key", request.Headers.GetValues("Ocp-Apim-Subscription-Key").Single());
            Assert.IsFalse(request.Headers.Contains("xi-api-key"));
            Assert.AreEqual("raw-24khz-16bit-mono-pcm", request.Headers.GetValues("X-Microsoft-OutputFormat").Single());
            Assert.IsTrue(request.Headers.UserAgent.Any());
            Assert.AreEqual("application/ssml+xml", request.Content!.Headers.ContentType!.MediaType);
            var body = await request.Content.ReadAsStringAsync(token);
            Assert.IsFalse(body.Contains("azure-private-key"));
            var xml = XDocument.Parse(body);
            XNamespace ssml = "http://www.w3.org/2001/10/synthesis";
            Assert.AreEqual(ssml + "speak", xml.Root!.Name);
            Assert.AreEqual("1.0", (string?)xml.Root.Attribute("version"));
            Assert.AreEqual("zh-CN", (string?)xml.Root.Attribute(XNamespace.Xml + "lang"));
            var voice = xml.Descendants(ssml + "voice").Single();
            Assert.AreEqual("zh-CN-XiaoxiaoNeural", (string?)voice.Attribute("name"));
            Assert.AreEqual(spoken, voice.Value);
            Assert.AreEqual(rate == 1 ? null : "1.25", (string?)voice.Element(ssml + "prosody")?.Attribute("rate"));
            return Pcm();
        });
        await using var provider = new AzureSpeechProvider(" azure-private-key ", " EastAsia ", handler);
        var audio = await provider.SynthesizeAsync(new(spoken, "zh-CN", "zh-CN-XiaoxiaoNeural", rate), CancellationToken.None);
        Assert.AreEqual(24000, audio.SampleRate);
        Assert.AreEqual(1, audio.Channels);
        Assert.AreEqual(4800, audio.Samples.Length);
        Assert.AreEqual("azure", provider.Id);
        Assert.AreEqual(SpeechProviderLocation.Online, provider.Location);
    }

    [TestMethod]
    public async Task RegionalVoicesUseShortNamesAndFilterUnsupportedLanguagesAndDuplicates()
    {
        var handler = new Handler((request, _) =>
        {
            Assert.AreEqual("https://westus2.tts.speech.microsoft.com/cognitiveservices/voices/list", request.RequestUri!.ToString());
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("key", request.Headers.GetValues("Ocp-Apim-Subscription-Key").Single());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""
                [
                  {"ShortName":"zh-CN-XiaoxiaoNeural","Locale":"zh-CN","LocalName":"晓晓"},
                  {"ShortName":"en-US-JennyNeural","Locale":"en-US","LocalName":"Jenny"},
                  {"ShortName":"en-US-JennyNeural","Locale":"en-US","LocalName":"Jenny"},
                  {"ShortName":"en-US-Ava:DragonHDLatestNeural","Locale":"en-US"},
                  {"ShortName":"fr-FR-DeniseNeural","Locale":"fr-FR","LocalName":"Denise"},
                  {"ShortName":"zh-CN-MismatchedNeural","Locale":"en-US"},
                  {"ShortName":"<voice>","Locale":"zh-CN"}
                ]
                """, Encoding.UTF8, "application/json") });
        });
        await using var provider = new AzureSpeechProvider("key", "westus2", handler);
        var voices = await provider.GetVoicesAsync(CancellationToken.None);
        Assert.AreEqual(3, voices.Count);
        Assert.IsTrue(voices.Any(v => v.Id == "zh-CN-XiaoxiaoNeural" && v.Name == "晓晓 · zh-CN"));
        Assert.IsTrue(voices.Any(v => v.Id == "en-US-Ava:DragonHDLatestNeural"));
    }

    [TestMethod]
    [DataRow("https://example.com")]
    [DataRow("eastasia.example.com")]
    [DataRow("eastasia@evil")]
    [DataRow("eastasia/path")]
    [DataRow("chinanorth2")]
    [DataRow("usgovvirginia")]
    [DataRow("")]
    [DataRow(null)]
    public async Task InvalidOrUnsupportedRegionCannotSendCredentials(string? region)
    {
        var calls = 0;
        var handler = new Handler((_, _) => { calls++; return Task.FromResult(Pcm()); });
        await using var provider = new AzureSpeechProvider("key", region!, handler);
        var error = await Assert.ThrowsExactlyAsync<SpeechServiceException>(() => provider.GetVoicesAsync(CancellationToken.None));
        Assert.AreEqual(SpeechServiceFailure.Configuration, error.Failure);
        Assert.AreEqual(0, calls);
    }

    [TestMethod]
    [DataRow("<voice>", "sample")]
    [DataRow("en-US-", "sample")]
    [DataRow("en-US-JennyNeural", "bad\u0001text")]
    public async Task InvalidVoiceOrXmlTextFailsBeforeHttp(string voice, string text)
    {
        var calls = 0;
        await using var provider = new AzureSpeechProvider("key", "eastasia", new Handler((_, _) => { calls++; return Task.FromResult(Pcm()); }));
        await Assert.ThrowsExactlyAsync<SpeechServiceException>(() => provider.SynthesizeAsync(new(text, "en-US", voice), CancellationToken.None));
        Assert.AreEqual(0, calls);
    }

    [TestMethod]
    [DataRow(401, SpeechServiceFailure.Authentication)]
    [DataRow(429, SpeechServiceFailure.RateLimited)]
    [DataRow(415, SpeechServiceFailure.Configuration)]
    [DataRow(503, SpeechServiceFailure.Unavailable)]
    public async Task ServiceErrorsAreSanitizedAndNotRetried(int status, SpeechServiceFailure failure)
    {
        var calls = 0;
        await using var provider = new AzureSpeechProvider("private-key", "eastasia", new Handler((_, _) =>
        {
            calls++;
            var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("echoed-private-key") };
            response.Headers.RetryAfter = new(TimeSpan.FromSeconds(90));
            return Task.FromResult(response);
        }));
        var error = await Assert.ThrowsExactlyAsync<SpeechServiceException>(() => provider.SynthesizeAsync(Request, CancellationToken.None));
        Assert.AreEqual(failure, error.Failure);
        Assert.AreEqual(TimeSpan.FromSeconds(90), error.RetryAfter);
        Assert.IsFalse(error.ToString().Contains("private-key"));
        Assert.AreEqual(1, calls);
    }

    [TestMethod]
    [DataRow(0, "application/octet-stream")]
    [DataRow(3, "application/octet-stream")]
    [DataRow(1440002, "application/octet-stream")]
    [DataRow(40, "application/json")]
    public async Task InvalidAudioAndUnboundedChunkedResponseAreRejected(int length, string type)
    {
        // No Content-Length: the streaming limit must protect playback too.
        var content = new StreamContent(new NonSeekableStream(new byte[length]));
        content.Headers.ContentType = new(type);
        await using var provider = new AzureSpeechProvider("key", "eastasia", new Handler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content })));
        var error = await Assert.ThrowsExactlyAsync<SpeechServiceException>(() => provider.SynthesizeAsync(Request, CancellationToken.None));
        Assert.AreEqual(SpeechServiceFailure.InvalidAudio, error.Failure);
    }

    [TestMethod]
    public async Task CancellationAndDisposalAbortAzureRequests()
    {
        foreach (var dispose in new[] { false, true })
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var provider = new AzureSpeechProvider("key", "eastasia", new Handler(async (_, token) =>
            {
                entered.SetResult(); await Task.Delay(Timeout.Infinite, token); return Pcm();
            }));
            using var cancellation = new CancellationTokenSource();
            var pending = provider.SynthesizeAsync(Request, cancellation.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            if (dispose) await provider.DisposeAsync(); else cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(2)));
        }
    }

    [TestMethod]
    public async Task AzureTimeoutUsesLocalVoiceWithoutCachingItOrRetryingDuringCooldown()
    {
        var calls = 0;
        var local = new LocalProvider();
        await using var provider = new FallbackSpeechProvider(new AzureSpeechProvider("key", "eastasia", new Handler(async (_, token) =>
        { calls++; await Task.Delay(Timeout.Infinite, token); return Pcm(); }), TimeSpan.FromMilliseconds(60)), local);
        Assert.IsFalse((await provider.SynthesizeAsync(Request, CancellationToken.None)).Cacheable);
        Assert.AreEqual(SpeechServiceFailure.Timeout, provider.LastFailure);
        await provider.SynthesizeAsync(Request, CancellationToken.None);
        Assert.AreEqual(2, local.Calls);
        Assert.AreEqual(1, calls);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => provider.SynthesizeAsync(Request, canceled.Token));
        Assert.AreEqual(2, local.Calls);
    }

    [TestMethod]
    public void SavedSettingsKeepBothCredentialsAndMigrateLegacyProviderSafely()
    {
        Assert.AreEqual(EngineerSpeechSettings.Windows, EngineerSpeechSettings.Load(null).ActiveProvider);
        Assert.AreEqual(EngineerSpeechSettings.ElevenLabs, EngineerSpeechSettings.Load("""{"UseElevenLabs":true,"VoiceId":"legacy"}""").ActiveProvider);
        var settings = new EngineerSpeechSettings(ProtectedApiKey: EngineerCredentialProtection.Protect("eleven-secret"),
            VoiceId: "eleven-voice", Language: "en-US", ProviderId: EngineerSpeechSettings.Azure,
            AzureProtectedApiKey: EngineerCredentialProtection.Protect("azure-secret"), AzureRegion: "westus2", AzureVoiceId: "zh-CN-XiaoxiaoNeural");
        var json = settings.Serialize();
        Assert.IsFalse(json.Contains("eleven-secret")); Assert.IsFalse(json.Contains("azure-secret"));
        var restored = EngineerSpeechSettings.Load(json);
        Assert.AreEqual(settings, restored);
        Assert.AreEqual(EngineerSpeechSettings.Azure, restored.ActiveProvider);
        Assert.IsFalse(restored.UseElevenLabs, "An old application must not treat Azure credentials as ElevenLabs credentials.");
        Assert.IsTrue(EngineerCredentialProtection.TryUnprotect(restored.AzureProtectedApiKey, out var azure));
        Assert.IsTrue(EngineerCredentialProtection.TryUnprotect(restored.ProtectedApiKey, out var eleven));
        Assert.AreEqual("azure-secret", azure); Assert.AreEqual("eleven-secret", eleven);
        Assert.AreEqual("zh-CN", restored.SpeechLanguage);
        Assert.AreEqual("en-US", (restored with { ProviderId = EngineerSpeechSettings.ElevenLabs }).SpeechLanguage);
        Assert.AreEqual("en-GB", (restored with { AzureVoiceId = "en-GB-SoniaNeural" }).SpeechLanguage);
        Assert.AreEqual(EngineerSpeechSettings.Windows, (restored with { ProviderId = "unknown", UseElevenLabs = true }).ActiveProvider);
        Assert.AreEqual("auto", EngineerSpeechSettings.Load("""{"ProviderId":"azure","AzureLanguage":null}""").SpeechLanguage);
    }

    private static SpeechSynthesisRequest Request => new("Blue flag.", "en-US", "en-US-JennyNeural");
    private static HttpResponseMessage Pcm() => new(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[4800]) };
    private sealed class NonSeekableStream(byte[] bytes) : MemoryStream(bytes)
    { public override bool CanSeek => false; }
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
            Assert.IsNull(request.VoiceId); Assert.AreEqual("en-US", request.Language);
            Calls++; return Task.FromResult(new SpeechAudio(new byte[4800], 24000));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
