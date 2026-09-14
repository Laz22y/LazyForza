using System.Globalization;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LazyForza.App;
using LazyForza.Speech;

namespace LazyForza.IntegrationTests;

[TestClass]
public sealed class CloudSpeechProviderTests
{
    private static readonly string[] Providers = ["tencent", "alibaba", "qwen", "minimax"];
    private static readonly byte[] Samples = [0, 0, 25, 0, 50, 0, 0, 0];
    private static SpeechSynthesisRequest Phrase(string provider) => new("进站窗口已开启。", "zh-CN", provider switch
    { "tencent" => "1001", "alibaba" => "xiaoyun", "qwen" => "Cherry", _ => "Chinese (Mandarin)_Reliable_Executive" });

    [TestMethod]
    public async Task TencentSignatureAuthenticatesTheExactUtf8BodyAndSession()
    {
        var handler = new Handler(async (message, ct) =>
        {
            Assert.AreEqual("https://tts.tencentcloudapi.com/", message.RequestUri!.ToString());
            Assert.AreEqual("TextToVoice", message.Headers.GetValues("X-TC-Action").Single());
            Assert.AreEqual("2019-08-23", message.Headers.GetValues("X-TC-Version").Single());
            var bytes = await message.Content!.ReadAsByteArrayAsync(ct);
            var authorization = message.Headers.GetValues("Authorization").Single();
            var parts = authorization["TC3-HMAC-SHA256 ".Length..].Split(", ").Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => p[1]);
            var credential = parts["Credential"].Split('/');
            Assert.AreEqual("AKIDtest", credential[0]); Assert.AreEqual("tts", credential[2]);
            var signed = parts["SignedHeaders"].Split(';');
            string Header(string name) => name == "content-type" ? message.Content.Headers.ContentType!.ToString() : message.Headers.GetValues(name).Single();
            var canonical = string.Join('\n', message.Method.ToString(), message.RequestUri.AbsolutePath, "",
                string.Concat(signed.Select(name => name + ":" + Header(name).Trim().ToLowerInvariant() + "\n")),
                parts["SignedHeaders"], Convert.ToHexStringLower(SHA256.HashData(bytes)));
            var timestamp = message.Headers.GetValues("X-TC-Timestamp").Single();
            var date = DateTimeOffset.FromUnixTimeSeconds(long.Parse(timestamp, CultureInfo.InvariantCulture));
            Assert.AreEqual(date.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), credential[1]);
            var scope = string.Join('/', credential.Skip(1));
            var toSign = $"TC3-HMAC-SHA256\n{timestamp}\n{scope}\n{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))}";
            var derived = Encoding.UTF8.GetBytes("TC3privateSecret");
            foreach (var step in credential.Skip(1)) derived = HMACSHA256.HashData(derived, Encoding.UTF8.GetBytes(step));
            Assert.AreEqual(Convert.ToHexStringLower(HMACSHA256.HashData(derived, Encoding.UTF8.GetBytes(toSign))), parts["Signature"]);
            using var json = JsonDocument.Parse(bytes);
            Assert.AreEqual("进站窗口已开启。", json.RootElement.GetProperty("Text").GetString());
            Assert.AreEqual("pcm", json.RootElement.GetProperty("Codec").GetString());
            Assert.AreEqual(16000, json.RootElement.GetProperty("SampleRate").GetInt32());
            Assert.IsFalse(Encoding.UTF8.GetString(bytes).Contains("privateSecret"));
            return Json(new { Response = new { Audio = Convert.ToBase64String(Samples), SessionId = json.RootElement.GetProperty("SessionId").GetString() } });
        });
        await using var provider = new TencentSpeechProvider("AKIDtest", "privateSecret", handler);
        var audio = await provider.SynthesizeAsync(Phrase("tencent"), CancellationToken.None);
        Assert.AreEqual(16000, audio.SampleRate); CollectionAssert.AreEqual(Samples, audio.Samples.ToArray());
    }

    [TestMethod]
    public async Task AlibabaSignsTokenPostAndSharesRefreshAcrossConcurrentSynthesis()
    {
        var now = DateTimeOffset.UtcNow;
        var tokenHandler = new Handler(async (message, ct) =>
        {
            await Task.Yield();
            Assert.AreEqual("https://nls-meta.cn-shanghai.aliyuncs.com/", message.RequestUri!.ToString());
            Assert.AreEqual(HttpMethod.Post, message.Method);
            var fields = (await message.Content!.ReadAsStringAsync(ct)).Split('&').Select(p => p.Split('=', 2))
                .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1].Replace("+", " ")));
            Assert.AreEqual("CreateToken", fields["Action"]); Assert.AreEqual("access-id", fields["AccessKeyId"]);
            Assert.AreEqual("cn-shanghai", fields["RegionId"]);
            var signature = fields["Signature"]; fields.Remove("Signature");
            var query = string.Join('&', fields.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));
            var expected = HMACSHA1.HashData("access-secret&"u8.ToArray(), Encoding.UTF8.GetBytes("POST&%2F&" + Uri.EscapeDataString(query)));
            Assert.AreEqual(Convert.ToBase64String(expected), signature);
            Assert.IsFalse((await message.Content.ReadAsStringAsync(ct)).Contains("access-secret"));
            return Json(new { Token = new { Id = "nls-token", ExpireTime = now.AddMinutes(4).ToUnixTimeSeconds() } });
        });
        var handler = new Handler(async (message, ct) =>
        {
            Assert.AreEqual("https://nls-gateway-cn-shanghai.aliyuncs.com/stream/v1/tts", message.RequestUri!.ToString());
            Assert.AreEqual("nls-token", message.Headers.GetValues("X-NLS-Token").Single());
            Assert.IsNull(message.Headers.Authorization);
            using var json = JsonDocument.Parse(await message.Content!.ReadAsByteArrayAsync(ct));
            Assert.AreEqual("project-app", json.RootElement.GetProperty("appkey").GetString());
            Assert.AreEqual("进站窗口已开启。", json.RootElement.GetProperty("text").GetString());
            Assert.AreEqual("pcm", json.RootElement.GetProperty("format").GetString());
            Assert.AreEqual(16000, json.RootElement.GetProperty("sample_rate").GetInt32());
            return Raw(Samples, "audio/mpeg");
        });
        await using var provider = new AlibabaSpeechProvider("project-app", "access-id", "access-secret", handler, tokenHandler, clock: () => now);
        var result = await Task.WhenAll(provider.SynthesizeAsync(Phrase("alibaba"), CancellationToken.None), provider.SynthesizeAsync(Phrase("alibaba"), CancellationToken.None));
        Assert.AreEqual(1, tokenHandler.Calls); Assert.AreEqual(2, handler.Calls);
        CollectionAssert.AreEqual(Samples, result[0].Samples.ToArray());
        now = now.AddSeconds(200);
        await provider.SynthesizeAsync(Phrase("alibaba"), CancellationToken.None);
        Assert.AreEqual(2, tokenHandler.Calls, "Refresh before expiry instead of storing a permanent token.");
    }

    [TestMethod]
    public async Task QwenUsesItsOwnApiKeyAndCollectsSsePcmWithoutFetchingUrls()
    {
        var handler = new Handler(async (message, ct) =>
        {
            Assert.AreEqual("https://dashscope.aliyuncs.com/api/v1/services/aigc/multimodal-generation/generation", message.RequestUri!.ToString());
            Assert.AreEqual("Bearer", message.Headers.Authorization!.Scheme);
            Assert.AreEqual("qwen-private", message.Headers.Authorization.Parameter);
            Assert.IsFalse(message.Headers.Contains("X-NLS-Token"));
            Assert.AreEqual("enable", message.Headers.GetValues("X-DashScope-SSE").Single());
            using var json = JsonDocument.Parse(await message.Content!.ReadAsByteArrayAsync(ct));
            Assert.AreEqual("qwen3-tts-flash", json.RootElement.GetProperty("model").GetString());
            Assert.AreEqual("Eldric Sage", json.RootElement.GetProperty("input").GetProperty("voice").GetString());
            Assert.AreEqual("Chinese", json.RootElement.GetProperty("input").GetProperty("language_type").GetString());
            Assert.IsFalse(json.RootElement.GetProperty("input").TryGetProperty("appkey", out _));
            return Sse(Samples);
        });
        await using var provider = new QwenSpeechProvider("qwen-private", handler: handler);
        var audio = await provider.SynthesizeAsync(Phrase("qwen") with { VoiceId = "Eldric Sage" }, CancellationToken.None);
        Assert.AreEqual(24000, audio.SampleRate); CollectionAssert.AreEqual(Samples, audio.Samples.ToArray());
        Assert.AreEqual(1, handler.Calls, "The result URL must never trigger another request.");
    }

    [TestMethod]
    [DataRow("china", "api.minimax.cn")]
    [DataRow("international", "api.minimax.io")]
    public async Task MiniMaxUsesSelectedSiteHexPcmAndDocumentedVoiceNames(string site, string host)
    {
        var key = new string('k', 900); // Some MiniMax credentials are long JWTs.
        var handler = new Handler(async (message, ct) =>
        {
            Assert.AreEqual(host, message.RequestUri!.Host);
            Assert.AreEqual("Bearer", message.Headers.Authorization!.Scheme);
            Assert.AreEqual(key, message.Headers.Authorization.Parameter);
            using var json = JsonDocument.Parse(await message.Content!.ReadAsByteArrayAsync(ct));
            Assert.AreEqual("hex", json.RootElement.GetProperty("output_format").GetString());
            Assert.IsFalse(json.RootElement.GetProperty("stream").GetBoolean());
            Assert.AreEqual("pcm", json.RootElement.GetProperty("audio_setting").GetProperty("format").GetString());
            Assert.AreEqual("Chinese (Mandarin)_Reliable_Executive", json.RootElement.GetProperty("voice_setting").GetProperty("voice_id").GetString());
            Assert.AreEqual(1.1, json.RootElement.GetProperty("voice_setting").GetProperty("speed").GetDouble());
            return MiniMaxAudio(Samples);
        });
        await using var provider = new MiniMaxSpeechProvider(key, endpoint: site, handler: handler);
        var audio = await provider.SynthesizeAsync(Phrase("minimax") with { Rate = 1.1 }, CancellationToken.None);
        CollectionAssert.AreEqual(Samples, audio.Samples.ToArray()); Assert.AreEqual(24000, audio.SampleRate);
    }

    [TestMethod]
    public async Task MiniMaxVoiceDirectoryIncludesSystemAndCustomVoicesWithoutDuplicates()
    {
        await using var provider = new MiniMaxSpeechProvider("key", handler: new Handler(async (message, ct) =>
        {
            Assert.AreEqual("/v1/get_voice", message.RequestUri!.AbsolutePath);
            Assert.AreEqual("all", JsonDocument.Parse(await message.Content!.ReadAsByteArrayAsync(ct)).RootElement.GetProperty("voice_type").GetString());
            return Json(new
            {
                base_resp = new { status_code = 0 },
                system_voice = new[] { new { voice_id = "Chinese (Mandarin)_Reliable_Executive", voice_name = "沉稳高管" } },
                voice_cloning = new[] { new { voice_id = "custom-voice" }, new { voice_id = "custom-voice" } },
                voice_generation = new[] { new { voice_id = "designed-voice" } }
            });
        }));
        var voices = await provider.GetVoicesAsync(CancellationToken.None);
        Assert.AreEqual(3, voices.Count); Assert.IsTrue(voices.Any(v => v.Name == "沉稳高管"));
    }

    [TestMethod]
    [DataRow(401, SpeechServiceFailure.Authentication)]
    [DataRow(429, SpeechServiceFailure.RateLimited)]
    [DataRow(400, SpeechServiceFailure.Configuration)]
    [DataRow(503, SpeechServiceFailure.Unavailable)]
    [DataRow(302, SpeechServiceFailure.Unavailable)]
    public async Task EveryProviderSanitizesHttpErrorsWithoutRetryOrRedirect(int code, SpeechServiceFailure failure)
    {
        foreach (var id in Providers)
        {
            var handler = new Handler((_, _) =>
            {
                var response = new HttpResponseMessage((HttpStatusCode)code) { Content = new StringContent("private-key echoed-secret") };
                response.Headers.RetryAfter = new(TimeSpan.FromSeconds(90));
                response.Headers.Location = new Uri("https://invalid.example/steal-key");
                return Task.FromResult(response);
            });
            await using var provider = Create(id, handler);
            var error = await Assert.ThrowsExactlyAsync<SpeechServiceException>(() => provider.SynthesizeAsync(Phrase(id), CancellationToken.None));
            Assert.AreEqual(failure, error.Failure, id); Assert.AreEqual(TimeSpan.FromSeconds(90), error.RetryAfter);
            Assert.IsFalse(error.ToString().Contains("private-key")); Assert.AreEqual(1, handler.Calls, id);
        }
    }

    [TestMethod]
    [DataRow("tencent", "AuthFailure.SignatureFailure", SpeechServiceFailure.Authentication)]
    [DataRow("tencent", "LimitExceeded.AccessLimit", SpeechServiceFailure.RateLimited)]
    [DataRow("tencent", "UnsupportedOperation.AccountArrears", SpeechServiceFailure.Authentication)]
    [DataRow("alibaba", "40000001", SpeechServiceFailure.Authentication)]
    [DataRow("alibaba", "40000003", SpeechServiceFailure.Configuration)]
    [DataRow("alibaba", "40000005", SpeechServiceFailure.RateLimited)]
    [DataRow("qwen", "InvalidApiKey", SpeechServiceFailure.Authentication)]
    [DataRow("qwen", "Throttling.RateQuota", SpeechServiceFailure.RateLimited)]
    [DataRow("minimax", "1004", SpeechServiceFailure.Authentication)]
    [DataRow("minimax", "1008", SpeechServiceFailure.Authentication)]
    [DataRow("minimax", "1002", SpeechServiceFailure.RateLimited)]
    public async Task Http200BusinessErrorsAreNotPlayedOrRetried(string id, string code, SpeechServiceFailure expected)
    {
        var handler = new Handler((_, _) => Task.FromResult(id switch
        {
            "tencent" => Json(new { Response = new { Error = new { Code = code, Message = "private-key" } } }),
            "alibaba" => Json(new { status = int.Parse(code, CultureInfo.InvariantCulture), message = "private-key" }),
            "qwen" => Json(new { code, message = "private-key" }),
            _ => Json(new { base_resp = new { status_code = int.Parse(code, CultureInfo.InvariantCulture), status_msg = "private-key" } })
        }));
        await using var provider = Create(id, handler);
        var error = await Assert.ThrowsExactlyAsync<SpeechServiceException>(() => provider.SynthesizeAsync(Phrase(id), CancellationToken.None));
        Assert.AreEqual(expected, error.Failure); Assert.IsFalse(error.ToString().Contains("private-key")); Assert.AreEqual(1, handler.Calls);
    }

    [TestMethod]
    public async Task EveryProviderCancelsAndDisposesOutstandingWork()
    {
        foreach (var id in Providers)
        foreach (var dispose in new[] { false, true })
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var handler = new Handler(async (_, token) => { entered.SetResult(); await Task.Delay(Timeout.Infinite, token); return Raw(Samples); });
            var provider = Create(id, handler);
            using var cancellation = new CancellationTokenSource();
            var pending = provider.SynthesizeAsync(Phrase(id), cancellation.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            if (dispose) await provider.DisposeAsync(); else cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(2)));
            await provider.DisposeAsync(); Assert.IsTrue(handler.Disposed, id);
        }
    }

    [TestMethod]
    public async Task EveryProviderHasABoundedTimeout()
    {
        foreach (var id in Providers)
        {
            var handler = new Handler(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return Raw(Samples); });
            await using var provider = Create(id, handler, TimeSpan.FromMilliseconds(60));
            var error = await Assert.ThrowsExactlyAsync<SpeechServiceException>(() => provider.SynthesizeAsync(Phrase(id), CancellationToken.None));
            Assert.AreEqual(SpeechServiceFailure.Timeout, error.Failure, id); Assert.AreEqual(1, handler.Calls);
        }
    }

    [TestMethod]
    public async Task EveryProviderBoundsResponsesWithoutContentLength()
    {
        foreach (var id in Providers)
        {
            var handler = new Handler((_, _) =>
            {
                var content = new StreamContent(new UnseekableStream(new byte[5 * 1024 * 1024]));
                content.Headers.ContentType = new(id == "qwen" ? "text/event-stream" : id == "alibaba" ? "audio/mpeg" : "application/json");
                Assert.IsNull(content.Headers.ContentLength);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
            });
            await using var provider = Create(id, handler);
            var error = await Assert.ThrowsExactlyAsync<SpeechServiceException>(() => provider.SynthesizeAsync(Phrase(id), CancellationToken.None));
            Assert.AreEqual(SpeechServiceFailure.InvalidAudio, error.Failure, id);
        }
    }

    [TestMethod]
    public async Task DecodedAudioLongerThanThirtySecondsIsRejectedByEveryProvider()
    {
        foreach (var id in Providers)
        {
            var pcm = new byte[(id is "tencent" or "alibaba" ? 16000 : 24000) * 2 * 31];
            var handler = new Handler(async (message, ct) =>
            {
                if (id == "alibaba") return Raw(pcm, "audio/mpeg");
                if (id == "qwen") return Sse(pcm);
                if (id == "minimax") return MiniMaxAudio(pcm);
                using var json = JsonDocument.Parse(await message.Content!.ReadAsByteArrayAsync(ct));
                return Json(new { Response = new { Audio = Convert.ToBase64String(pcm), SessionId = json.RootElement.GetProperty("SessionId").GetString() } });
            });
            await using var provider = Create(id, handler);
            var error = await Assert.ThrowsExactlyAsync<SpeechServiceException>(() => provider.SynthesizeAsync(Phrase(id), CancellationToken.None));
            Assert.AreEqual(SpeechServiceFailure.InvalidAudio, error.Failure, id);
        }
    }

    [TestMethod]
    public async Task AlibabaExpiredTokenRefreshesOnNextCallWithoutRepeatingFailedSynthesis()
    {
        var tokens = new Handler((_, _) => Task.FromResult(Json(new { Token = new { Id = "token", ExpireTime = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds() } })));
        var fail = true;
        var speech = new Handler((_, _) => Task.FromResult(fail ? Json(new { status = 40000001, message = "token expired" }) : Raw(Samples, "audio/mpeg")));
        await using var provider = new AlibabaSpeechProvider("app", "id", "key", speech, tokens);
        await Assert.ThrowsExactlyAsync<SpeechServiceException>(() => provider.SynthesizeAsync(Phrase("alibaba"), CancellationToken.None));
        Assert.AreEqual(1, speech.Calls); Assert.AreEqual(1, tokens.Calls);
        fail = false;
        await provider.SynthesizeAsync(Phrase("alibaba"), CancellationToken.None);
        Assert.AreEqual(2, speech.Calls); Assert.AreEqual(2, tokens.Calls);
    }

    [TestMethod]
    public async Task AlibabaCancellationReleasesTokenWaitersWithoutStartingSpeech()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tokens = new Handler(async (_, ct) => { entered.SetResult(); await Task.Delay(Timeout.Infinite, ct); return Json(new { }); });
        var speech = new Handler((_, _) => throw new AssertFailedException("Cancelled token work must not start speech."));
        await using var provider = new AlibabaSpeechProvider("app", "id", "key", speech, tokens);
        var first = provider.SynthesizeAsync(Phrase("alibaba"), CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using var cancellation = new CancellationTokenSource();
        var second = provider.SynthesizeAsync(Phrase("alibaba"), cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => second.WaitAsync(TimeSpan.FromSeconds(2)));
        await provider.DisposeAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => first.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(1, tokens.Calls); Assert.AreEqual(0, speech.Calls);
        Assert.IsTrue(tokens.Disposed); Assert.IsTrue(speech.Disposed);
    }

    [TestMethod]
    public async Task QwenErrorAfterAudioDiscardsThePartialUtterance()
    {
        var sse = "data: {\"output\":{\"audio\":{\"data\":\"AAA=\"}}}\n\n" +
            "event: error\ndata: {\"code\":\"Throttling\",\"message\":\"private-key\"}\n\n";
        await using var provider = new QwenSpeechProvider("key", handler: new Handler((_, _) => Task.FromResult(Raw(Encoding.UTF8.GetBytes(sse), "text/event-stream"))));
        var error = await Assert.ThrowsExactlyAsync<SpeechServiceException>(() => provider.SynthesizeAsync(Phrase("qwen"), CancellationToken.None));
        Assert.AreEqual(SpeechServiceFailure.RateLimited, error.Failure); Assert.IsFalse(error.ToString().Contains("private-key"));
    }

    [TestMethod]
    [DataRow("mp3", 24000, 1)]
    [DataRow("pcm", 16000, 1)]
    [DataRow("pcm", 24000, 2)]
    public async Task MiniMaxRejectsUnexpectedAudioEncoding(string format, int rate, int channels)
    {
        await using var provider = new MiniMaxSpeechProvider("key", handler: new Handler((_, _) => Task.FromResult(Json(new
        {
            base_resp = new { status_code = 0 }, data = new { status = 2, audio = "0000" },
            extra_info = new { audio_format = format, audio_sample_rate = rate, audio_channel = channels }
        }))));
        var error = await Assert.ThrowsExactlyAsync<SpeechServiceException>(() => provider.SynthesizeAsync(Phrase("minimax"), CancellationToken.None));
        Assert.AreEqual(SpeechServiceFailure.InvalidAudio, error.Failure);
    }

    [TestMethod]
    [DataRow("tencent")]
    [DataRow("minimax")]
    public async Task NullAudioIsAServiceFailureEligibleForLocalFallback(string id)
    {
        var handler = new Handler(async (message, ct) =>
        {
            if (id == "minimax") return Json(new
            {
                base_resp = new { status_code = 0 }, data = new { status = 2, audio = (string?)null },
                extra_info = new { audio_format = "pcm", audio_sample_rate = 24000, audio_channel = 1 }
            });
            using var body = JsonDocument.Parse(await message.Content!.ReadAsByteArrayAsync(ct));
            return Json(new { Response = new { Audio = (string?)null, SessionId = body.RootElement.GetProperty("SessionId").GetString() } });
        });
        await using var provider = Create(id, handler);
        var error = await Assert.ThrowsExactlyAsync<SpeechServiceException>(() => provider.SynthesizeAsync(Phrase(id), CancellationToken.None));
        Assert.AreEqual(SpeechServiceFailure.InvalidAudio, error.Failure);
        Assert.AreEqual(1, handler.Calls);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(3)]
    public async Task EmptyOrMisalignedPcmNeverReachesPlayback(int size)
    {
        foreach (var id in Providers)
        {
            var pcm = new byte[size];
            var handler = new Handler(async (message, ct) => id switch
            {
                "alibaba" => Raw(pcm, "audio/mpeg"), "qwen" => Sse(pcm), "minimax" => MiniMaxAudio(pcm),
                _ => Json(new { Response = new { Audio = Convert.ToBase64String(pcm), SessionId = JsonDocument.Parse(await message.Content!.ReadAsByteArrayAsync(ct)).RootElement.GetProperty("SessionId").GetString() } })
            });
            await using var provider = Create(id, handler);
            var error = await Assert.ThrowsExactlyAsync<SpeechServiceException>(() => provider.SynthesizeAsync(Phrase(id), CancellationToken.None));
            Assert.AreEqual(SpeechServiceFailure.InvalidAudio, error.Failure, id);
        }
    }

    [TestMethod]
    [DataRow("data: {\"output\":{\"audio\":{\"data\":\"AAA=\"}}}\n\n")]
    [DataRow("data: {\"output\":{\"audio\":{\"data\":\"bad!\"},\"finish_reason\":\"stop\"}}\n\n")]
    [DataRow("data: {\"output\":{\"audio\":{\"url\":\"https://invalid.example/audio\"},\"finish_reason\":\"stop\"}}\n\n")]
    public async Task QwenRejectsTruncatedMalformedOrUrlOnlyStreams(string sse)
    {
        var handler = new Handler((_, _) => Task.FromResult(Raw(Encoding.UTF8.GetBytes(sse), "text/event-stream")));
        await using var provider = new QwenSpeechProvider("key", handler: handler);
        var error = await Assert.ThrowsExactlyAsync<SpeechServiceException>(() => provider.SynthesizeAsync(Phrase("qwen"), CancellationToken.None));
        Assert.AreEqual(SpeechServiceFailure.InvalidAudio, error.Failure); Assert.AreEqual(1, handler.Calls);
    }

    [TestMethod]
    public async Task InvalidConfigurationCannotContactAnyProvider()
    {
        var handler = new Handler((_, _) => throw new AssertFailedException("Invalid configuration reached HTTP."));
        await using var qwen = new QwenSpeechProvider("sk-sp-token-plan", handler: handler);
        await Assert.ThrowsExactlyAsync<SpeechServiceException>(() => qwen.SynthesizeAsync(Phrase("qwen"), CancellationToken.None));
        await using var minimax = new MiniMaxSpeechProvider("key", endpoint: "https://invalid.example", handler: new Handler((_, _) => throw new AssertFailedException()));
        await Assert.ThrowsExactlyAsync<SpeechServiceException>(() => minimax.SynthesizeAsync(Phrase("minimax"), CancellationToken.None));
        foreach (var id in Providers)
        {
            await using var provider = Create(id, new Handler((_, _) => throw new AssertFailedException()));
            await Assert.ThrowsExactlyAsync<SpeechServiceException>(() => provider.SynthesizeAsync(Phrase(id) with { Text = new string('中', 601) }, CancellationToken.None));
            await Assert.ThrowsExactlyAsync<SpeechServiceException>(() => provider.SynthesizeAsync(Phrase(id) with { VoiceId = "bad\nvoice" }, CancellationToken.None));
        }
    }

    [TestMethod]
    public async Task SettingsKeepProviderCredentialsIndependentAndBackwardCompatible()
    {
        Assert.AreEqual(EngineerSpeechSettings.ElevenLabs, EngineerSpeechSettings.Load("{\"UseElevenLabs\":true}").ActiveProvider);
        var stored = new EngineerSpeechSettings(ProviderId: "qwen", ProtectedApiKey: EngineerCredentialProtection.Protect("eleven-key"),
            TencentSettings: new(EngineerCredentialProtection.Protect("tencent-id"), EngineerCredentialProtection.Protect("tencent-secret")),
            AlibabaSettings: new(EngineerCredentialProtection.Protect("nls-app"), EngineerCredentialProtection.Protect("nls-id"), EngineerCredentialProtection.Protect("nls-secret")),
            QwenSettings: new(EngineerCredentialProtection.Protect("qwen-key"), Language: "en-US"),
            MiniMaxSettings: new(EngineerCredentialProtection.Protect("minimax-key")));
        var serialized = stored.Serialize();
        foreach (var secret in new[] { "eleven-key", "tencent-secret", "nls-app", "nls-id", "nls-secret", "qwen-key", "minimax-key" })
            Assert.IsFalse(serialized.Contains(secret), secret);
        var restored = EngineerSpeechSettings.Load(serialized);
        Assert.AreEqual(stored, restored); Assert.AreEqual("en-US", restored.SpeechLanguage);
        Assert.IsFalse(restored.UseElevenLabs, "Older clients must not send another provider's credentials to ElevenLabs.");
        foreach (var id in Providers)
        {
            var settings = restored with { ProviderId = id };
            var (provider, readable) = EngineerSpeechProviderFactory.CreateOnline(settings);
            await using (provider) { Assert.AreEqual(id, provider.Id); Assert.IsTrue(readable); }
        }
        var broken = restored with { ProviderId = "qwen", QwenSettings = new("not-a-dpapi-value") };
        var (invalid, decrypted) = EngineerSpeechProviderFactory.CreateOnline(broken);
        await using (invalid) Assert.IsFalse(decrypted);
        Assert.AreEqual("windows", (restored with { ProviderId = "future-provider" }).ActiveProvider);
    }

    private static ISpeechSynthesisProvider Create(string id, Handler handler, TimeSpan? timeout = null) => id switch
    {
        "tencent" => new TencentSpeechProvider("id", "key", handler, timeout),
        "alibaba" => new AlibabaSpeechProvider("app", "id", "key", handler,
            new Handler((_, _) => Task.FromResult(Json(new { Token = new { Id = "token", ExpireTime = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds() } }))), timeout),
        "qwen" => new QwenSpeechProvider("key", handler: handler, timeout: timeout),
        _ => new MiniMaxSpeechProvider("key", handler: handler, timeout: timeout)
    };
    private static HttpResponseMessage Json(object value) => Raw(JsonSerializer.SerializeToUtf8Bytes(value), "application/json");
    private static HttpResponseMessage Raw(byte[] bytes, string type = "application/octet-stream")
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        response.Content.Headers.ContentType = new(type); return response;
    }
    private static HttpResponseMessage MiniMaxAudio(byte[] pcm) => Json(new
    {
        base_resp = new { status_code = 0 }, data = new { status = 2, audio = Convert.ToHexString(pcm) },
        extra_info = new { audio_format = "pcm", audio_sample_rate = 24000, audio_channel = 1 }
    });
    private static HttpResponseMessage Sse(byte[] pcm) => Raw(Encoding.UTF8.GetBytes(
        ": keepalive\r\nid: 1\r\nevent: result\r\ndata: " + JsonSerializer.Serialize(new { output = new { audio = new { data = Convert.ToBase64String(pcm) } } }) + "\r\n\r\n" +
        "data: {\"output\":{\"finish_reason\":\"stop\",\"audio\":{\"data\":\"\",\"url\":\"https://invalid.example/do-not-fetch\"}}}\n\n"), "text/event-stream");
    private sealed class UnseekableStream(byte[] buffer) : MemoryStream(buffer) { public override bool CanSeek => false; }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        private int calls;
        public int Calls => Volatile.Read(ref calls);
        public bool Disposed { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) { Interlocked.Increment(ref calls); return send(request, cancellationToken); }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}
