using System.Net.Http.Json;
using System.Text.Json;

namespace LazyForza.Speech;

public sealed class MiniMaxSpeechProvider : ISpeechSynthesisProvider
{
    public const string DefaultModel = "speech-2.8-turbo";
    public static IReadOnlyList<string> Models { get; } = Array.AsReadOnly(new[]
        { DefaultModel, "speech-2.8-hd", "speech-2.6-turbo", "speech-2.6-hd", "speech-02-turbo", "speech-02-hd" });
    private const int SampleRate = 24000;
    private readonly string key, model, endpoint;
    private readonly SpeechServiceHttpClient client;
    public string Id => "minimax";
    public SpeechProviderLocation Location => SpeechProviderLocation.Online;

    public MiniMaxSpeechProvider(string key, string model = DefaultModel, string endpoint = "china",
        HttpMessageHandler? handler = null, TimeSpan? timeout = null)
    {
        this.key = key.Trim(); this.model = model; this.endpoint = endpoint;
        client = new(new Uri(endpoint == "international" ? "https://api.minimax.io/" : "https://api.minimax.cn/"), handler, timeout);
    }

    public static bool IsValidVoiceId(string? voice) => CloudSpeechProtocol.IsVoiceName(voice);

    public async Task<SpeechAudio> SynthesizeAsync(SpeechSynthesisRequest request, CancellationToken cancellationToken)
    {
        CloudSpeechProtocol.ValidateRequest(request, supportsRate: true);
        if (!Models.Contains(model)) throw new SpeechServiceException(SpeechServiceFailure.Configuration);
        using var message = Request("v1/t2a_v2");
        message.Content = JsonContent.Create(new
        {
            model, text = request.Text, stream = false, output_format = "hex",
            language_boost = request.Language == "en-US" ? "English" : "Chinese",
            voice_setting = new { voice_id = request.VoiceId, speed = request.Rate, vol = 1, pitch = 0 },
            audio_setting = new { sample_rate = SampleRate, format = "pcm", channel = 1 }
        });
        var bytes = await client.SendAsync(message, 3 * 1024 * 1024, cancellationToken, SpeechResponseFormat.Json).ConfigureAwait(false);
        try
        {
            using var json = CloudSpeechProtocol.Json(bytes);
            CheckStatus(json.RootElement);
            var data = json.RootElement.GetProperty("data");
            var info = json.RootElement.GetProperty("extra_info");
            if (data.GetProperty("status").GetInt32() != 2 || info.GetProperty("audio_sample_rate").GetInt32() != SampleRate ||
                info.GetProperty("audio_channel").GetInt32() != 1 || info.GetProperty("audio_format").GetString() != "pcm")
                throw new SpeechServiceException(SpeechServiceFailure.InvalidAudio);
            var hex = data.GetProperty("audio").GetString();
            if (string.IsNullOrEmpty(hex) || hex.Length > SampleRate * 4 * SpeechAudio.MaximumDurationSeconds)
                throw new SpeechServiceException(SpeechServiceFailure.InvalidAudio);
            return CloudSpeechProtocol.Pcm(Convert.FromHexString(hex), SampleRate);
        }
        catch (Exception error) when (CloudSpeechProtocol.IsMalformed(error))
        { throw new SpeechServiceException(SpeechServiceFailure.InvalidAudio); }
    }

    public async Task<IReadOnlyList<SpeechVoiceInfo>> GetVoicesAsync(CancellationToken cancellationToken)
    {
        using var message = Request("v1/get_voice");
        message.Content = JsonContent.Create(new { voice_type = "all" });
        var bytes = await client.SendAsync(message, 2 * 1024 * 1024, cancellationToken, SpeechResponseFormat.Json).ConfigureAwait(false);
        try
        {
            using var json = CloudSpeechProtocol.Json(bytes);
            CheckStatus(json.RootElement);
            var result = new Dictionary<string, SpeechVoiceInfo>(StringComparer.Ordinal);
            foreach (var category in new[] { "system_voice", "voice_cloning", "voice_generation" })
            {
                if (!json.RootElement.TryGetProperty(category, out var list) || list.ValueKind == JsonValueKind.Null) continue;
                foreach (var voice in list.EnumerateArray())
                {
                    var id = voice.GetProperty("voice_id").GetString();
                    var name = voice.TryGetProperty("voice_name", out var value) ? value.GetString() : id;
                    if (IsValidVoiceId(id)) result[id!] = new(id!, string.IsNullOrWhiteSpace(name) ? id! : name[..Math.Min(128, name.Length)]);
                    if (result.Count > 4000) throw new SpeechServiceException(SpeechServiceFailure.Unavailable);
                }
            }
            return result.Values.OrderBy(v => v.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        }
        catch (Exception error) when (CloudSpeechProtocol.IsMalformed(error))
        { throw new SpeechServiceException(SpeechServiceFailure.Unavailable); }
    }

    private static void CheckStatus(JsonElement root)
    {
        var code = root.GetProperty("base_resp").GetProperty("status_code").GetInt32();
        if (code != 0) throw new SpeechServiceException(code switch
        {
            1001 => SpeechServiceFailure.Timeout,
            1002 or 2056 => SpeechServiceFailure.RateLimited,
            1004 or 1008 or 2049 => SpeechServiceFailure.Authentication,
            1026 or 1027 or 2013 => SpeechServiceFailure.Configuration,
            _ => SpeechServiceFailure.Unavailable
        });
    }

    private HttpRequestMessage Request(string path)
    {
        if (endpoint is not ("china" or "international")) throw new SpeechServiceException(SpeechServiceFailure.Configuration);
        return client.CreateBearerRequest(HttpMethod.Post, path, key, 4096);
    }
    public ValueTask DisposeAsync() => client.DisposeAsync();
}
