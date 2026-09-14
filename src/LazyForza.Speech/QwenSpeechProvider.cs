using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace LazyForza.Speech;

/// <summary>Qianwen AI platform Qwen-TTS over DashScope SSE. Audio URLs are never followed.</summary>
public sealed class QwenSpeechProvider : ISpeechSynthesisProvider
{
    public const string DefaultModel = "qwen3-tts-flash";
    public static IReadOnlyList<string> Models { get; } = Array.AsReadOnly(new[] { DefaultModel, "qwen3-tts-instruct-flash" });
    private const int SampleRate = 24000, MaximumPcm = SampleRate * 2 * SpeechAudio.MaximumDurationSeconds;
    private readonly string key, model;
    private readonly SpeechServiceHttpClient client;
    public string Id => "qwen";
    public SpeechProviderLocation Location => SpeechProviderLocation.Online;

    public QwenSpeechProvider(string key, string model = DefaultModel, HttpMessageHandler? handler = null, TimeSpan? timeout = null)
    {
        this.key = key.Trim(); this.model = model;
        client = new(new Uri("https://dashscope.aliyuncs.com/"), handler, timeout);
    }

    public static bool IsValidVoiceId(string? voice) => CloudSpeechProtocol.IsVoiceName(voice);

    public async Task<SpeechAudio> SynthesizeAsync(SpeechSynthesisRequest request, CancellationToken cancellationToken)
    {
        CloudSpeechProtocol.ValidateRequest(request);
        if (!Models.Contains(model) || key.StartsWith("sk-sp-", StringComparison.Ordinal))
            throw new SpeechServiceException(SpeechServiceFailure.Configuration);
        using var message = client.CreateBearerRequest(HttpMethod.Post, "api/v1/services/aigc/multimodal-generation/generation", key);
        message.Headers.Add("X-DashScope-SSE", "enable");
        message.Content = JsonContent.Create(new
        {
            model,
            input = new { text = request.Text, voice = request.VoiceId, language_type = request.Language == "en-US" ? "English" : "Chinese" }
        });
        var response = await client.SendResponseAsync(message, 4 * 1024 * 1024, cancellationToken,
            SpeechResponseFormat.ServerSentEvents).ConfigureAwait(false);
        try
        {
            if (response.MediaType == "application/json")
            {
                using var json = CloudSpeechProtocol.Json(response.Body);
                CheckError(json.RootElement);
                throw new SpeechServiceException(SpeechServiceFailure.InvalidAudio);
            }
            using var reader = new StringReader(new UTF8Encoding(false, true).GetString(response.Body));
            using var audio = new MemoryStream();
            var data = new StringBuilder();
            var complete = false;
            void Consume()
            {
                if (data.Length == 0) return;
                var value = data.ToString(); data.Clear();
                if (value.Trim() == "[DONE]") return;
                using var json = JsonDocument.Parse(value);
                CheckError(json.RootElement);
                if (complete) throw new SpeechServiceException(SpeechServiceFailure.InvalidAudio);
                var output = json.RootElement.GetProperty("output");
                if (output.TryGetProperty("audio", out var chunk) && chunk.ValueKind == JsonValueKind.Object &&
                    chunk.TryGetProperty("data", out var encoded) && encoded.ValueKind == JsonValueKind.String &&
                    encoded.GetString() is { Length: > 0 } pcm)
                {
                    var bytes = CloudSpeechProtocol.Base64(pcm, MaximumPcm - (int)audio.Length);
                    audio.Write(bytes);
                }
                complete = output.TryGetProperty("finish_reason", out var reason) && reason.GetString() == "stop";
            }
            while (reader.ReadLine() is { } line)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (line.Length == 0) Consume();
                else if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    if (data.Length != 0) data.Append('\n');
                    data.Append(line.AsSpan(5).TrimStart());
                }
            }
            Consume();
            if (!complete) throw new SpeechServiceException(SpeechServiceFailure.InvalidAudio);
            return CloudSpeechProtocol.Pcm(audio.ToArray(), SampleRate);
        }
        catch (Exception error) when (CloudSpeechProtocol.IsMalformed(error))
        { throw new SpeechServiceException(SpeechServiceFailure.InvalidAudio); }
    }

    private static void CheckError(JsonElement root)
    {
        if (root.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(code.GetString()))
            throw new SpeechServiceException(CloudSpeechProtocol.ErrorCode(code.GetString()!));
    }

    public ValueTask DisposeAsync() => client.DisposeAsync();
}
