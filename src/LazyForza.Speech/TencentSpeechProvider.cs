using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LazyForza.Speech;

/// <summary>Tencent Cloud TextToVoice, API 2019-08-23, TC3 signature and bounded PCM16.</summary>
public sealed class TencentSpeechProvider : ISpeechSynthesisProvider
{
    public const string DefaultVoice = "101001";
    private const string Host = "tts.tencentcloudapi.com";
    private const int SampleRate = 16000;
    private readonly string secretId, secretKey;
    private readonly SpeechServiceHttpClient client;
    public string Id => "tencent";
    public SpeechProviderLocation Location => SpeechProviderLocation.Online;

    public TencentSpeechProvider(string secretId, string secretKey, HttpMessageHandler? handler = null, TimeSpan? timeout = null)
    {
        this.secretId = secretId.Trim(); this.secretKey = secretKey.Trim();
        client = new(new Uri($"https://{Host}/"), handler, timeout);
    }

    public static bool IsValidVoiceId(string? voice) => voice is { Length: > 0 and <= 10 } &&
        voice.All(char.IsAsciiDigit) && int.TryParse(voice, out var id) && id >= 0 && id != 200000000;
    public static bool IsValidCredentials(string? id, string? key) => CloudSpeechProtocol.IsIdentifier(id) &&
        key is { Length: > 0 and <= 512 } && key.All(c => c is >= '!' and <= '~');

    public async Task<SpeechAudio> SynthesizeAsync(SpeechSynthesisRequest request, CancellationToken cancellationToken)
    {
        CloudSpeechProtocol.ValidateRequest(request, request.Text?.Any(c => c > 127) == true ? 150 : 500);
        if (!IsValidVoiceId(request.VoiceId) || !IsValidCredentials(secretId, secretKey))
            throw new SpeechServiceException(SpeechServiceFailure.Configuration);
        var session = Guid.NewGuid().ToString();
        var body = JsonSerializer.Serialize(new
        {
            Text = request.Text, SessionId = session, VoiceType = int.Parse(request.VoiceId!, CultureInfo.InvariantCulture),
            ModelType = 1, PrimaryLanguage = request.Language == "en-US" ? 2 : 1, SampleRate, Codec = "pcm", Speed = 0, Volume = 0
        });
        var now = DateTimeOffset.UtcNow;
        using var message = new HttpRequestMessage(HttpMethod.Post, "/")
        { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        message.Headers.Host = Host;
        message.Headers.Add("X-TC-Action", "TextToVoice");
        message.Headers.Add("X-TC-Version", "2019-08-23");
        message.Headers.Add("X-TC-Timestamp", now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
        message.Headers.TryAddWithoutValidation("Authorization", Sign(body, now));
        var bytes = await client.SendAsync(message, 2 * 1024 * 1024, cancellationToken, SpeechResponseFormat.Json).ConfigureAwait(false);
        try
        {
            using var json = CloudSpeechProtocol.Json(bytes);
            var response = json.RootElement.GetProperty("Response");
            if (response.TryGetProperty("Error", out var error))
                throw new SpeechServiceException(CloudSpeechProtocol.ErrorCode(error.GetProperty("Code").GetString() ?? ""));
            if (response.GetProperty("SessionId").GetString() != session)
                throw new SpeechServiceException(SpeechServiceFailure.InvalidAudio);
            return CloudSpeechProtocol.Pcm(CloudSpeechProtocol.Base64(response.GetProperty("Audio").GetString(),
                SampleRate * 2 * SpeechAudio.MaximumDurationSeconds), SampleRate);
        }
        catch (Exception error) when (CloudSpeechProtocol.IsMalformed(error))
        { throw new SpeechServiceException(SpeechServiceFailure.InvalidAudio); }
    }

    private string Sign(string body, DateTimeOffset now)
    {
        static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
        static byte[] Hmac(byte[] key, string value) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(value));
        const string headers = "content-type;host;x-tc-action";
        var canonical = $"POST\n/\n\ncontent-type:application/json; charset=utf-8\nhost:{Host}\nx-tc-action:texttovoice\n\n{headers}\n{Hash(body)}";
        var date = now.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var scope = $"{date}/tts/tc3_request";
        var toSign = $"TC3-HMAC-SHA256\n{now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)}\n{scope}\n{Hash(canonical)}";
        var dateKey = Hmac(Encoding.UTF8.GetBytes("TC3" + secretKey), date);
        var serviceKey = Hmac(dateKey, "tts");
        var signingKey = Hmac(serviceKey, "tc3_request");
        try
        {
            return $"TC3-HMAC-SHA256 Credential={secretId}/{scope}, SignedHeaders={headers}, Signature={Convert.ToHexStringLower(Hmac(signingKey, toSign))}";
        }
        finally { CryptographicOperations.ZeroMemory(dateKey); CryptographicOperations.ZeroMemory(serviceKey); CryptographicOperations.ZeroMemory(signingKey); }
    }

    public ValueTask DisposeAsync() => client.DisposeAsync();
}
