using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;

namespace LazyForza.Speech;

/// <summary>Alibaba Intelligent Speech Interaction in Shanghai. AppKey is not a DashScope API key.</summary>
public sealed class AlibabaSpeechProvider : ISpeechSynthesisProvider
{
    private const int SampleRate = 16000;
    private readonly string appKey, accessKeyId, accessKeySecret;
    private readonly SpeechServiceHttpClient client, tokenClient;
    private readonly SemaphoreSlim tokenGate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly TimeSpan timeout;
    private readonly Func<DateTimeOffset> clock;
    private string token = "";
    private DateTimeOffset expiresAt;
    private int tokenInvalidated;
    private int disposed;
    public string Id => "alibaba";
    public SpeechProviderLocation Location => SpeechProviderLocation.Online;

    public AlibabaSpeechProvider(string appKey, string accessKeyId, string accessKeySecret,
        HttpMessageHandler? handler = null, HttpMessageHandler? tokenHandler = null, TimeSpan? timeout = null,
        Func<DateTimeOffset>? clock = null)
    {
        this.appKey = appKey.Trim(); this.accessKeyId = accessKeyId.Trim(); this.accessKeySecret = accessKeySecret.Trim();
        this.timeout = timeout ?? TimeSpan.FromSeconds(6); this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        client = new(new Uri("https://nls-gateway-cn-shanghai.aliyuncs.com/"), handler, timeout);
        tokenClient = new(new Uri("https://nls-meta.cn-shanghai.aliyuncs.com/"), tokenHandler, timeout);
    }

    public static bool IsValidVoiceId(string? voice) => CloudSpeechProtocol.IsIdentifier(voice);
    public static bool IsValidCredential(string? value) => CloudSpeechProtocol.IsIdentifier(value, 256);

    public async Task<SpeechAudio> SynthesizeAsync(SpeechSynthesisRequest request, CancellationToken cancellationToken)
    {
        CloudSpeechProtocol.ValidateRequest(request, 300);
        if (!IsValidVoiceId(request.VoiceId) || !IsValidCredential(appKey) || !IsValidCredential(accessKeyId) || !IsValidCredential(accessKeySecret))
            throw new SpeechServiceException(SpeechServiceFailure.Configuration);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        deadline.CancelAfter(timeout);
        try
        {
            var accessToken = await GetTokenAsync(deadline.Token).ConfigureAwait(false);
            using var message = client.CreateRequest(HttpMethod.Post, "stream/v1/tts", "X-NLS-Token", accessToken);
            message.Content = JsonContent.Create(new
            { appkey = appKey, text = request.Text, voice = request.VoiceId, format = "pcm", sample_rate = SampleRate, volume = 50, speech_rate = 0, pitch_rate = 0 });
            var response = await client.SendResponseAsync(message, SampleRate * 2 * SpeechAudio.MaximumDurationSeconds,
                deadline.Token, SpeechResponseFormat.NlsPcm).ConfigureAwait(false);
            if (response.MediaType == "application/json")
            {
                using var json = CloudSpeechProtocol.Json(response.Body);
                var status = json.RootElement.TryGetProperty("status", out var value) ? value.GetInt32() : 0;
                throw new SpeechServiceException(status switch
                {
                    40000001 => SpeechServiceFailure.Authentication,
                    40000005 => SpeechServiceFailure.RateLimited,
                    40000004 => SpeechServiceFailure.Timeout,
                    40000000 or 40000002 or 40000003 => SpeechServiceFailure.Configuration,
                    _ => SpeechServiceFailure.Unavailable
                });
            }
            return CloudSpeechProtocol.Pcm(response.Body, SampleRate);
        }
        catch (SpeechServiceException error) when (error.Failure == SpeechServiceFailure.Authentication)
        {
            // Do not resubmit a paid synthesis. Refresh credentials on the next eligible request.
            Interlocked.Exchange(ref tokenInvalidated, 1);
            throw;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !lifetime.IsCancellationRequested)
        { throw new SpeechServiceException(SpeechServiceFailure.Timeout); }
        catch (Exception error) when (CloudSpeechProtocol.IsMalformed(error))
        { throw new SpeechServiceException(SpeechServiceFailure.InvalidAudio); }
    }

    private async Task<string> GetTokenAsync(CancellationToken cancellationToken)
    {
        await tokenGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref tokenInvalidated) == 0 && token.Length > 0 && expiresAt > clock().AddMinutes(1)) return token;
            var parameters = new SortedDictionary<string, string>(StringComparer.Ordinal)
            {
                ["AccessKeyId"] = accessKeyId, ["Action"] = "CreateToken", ["Format"] = "JSON", ["RegionId"] = "cn-shanghai",
                ["SignatureMethod"] = "HMAC-SHA1", ["SignatureNonce"] = Guid.NewGuid().ToString(), ["SignatureVersion"] = "1.0",
                ["Timestamp"] = clock().UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture), ["Version"] = "2019-02-28"
            };
            var query = string.Join("&", parameters.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));
            var signingKey = Encoding.UTF8.GetBytes(accessKeySecret + "&");
            try
            {
                parameters["Signature"] = Convert.ToBase64String(HMACSHA1.HashData(signingKey,
                    Encoding.UTF8.GetBytes("POST&%2F&" + Uri.EscapeDataString(query))));
            }
            finally { CryptographicOperations.ZeroMemory(signingKey); }
            // POST keeps all credential identifiers and signatures out of request URLs.
            using var message = new HttpRequestMessage(HttpMethod.Post, "/") { Content = new FormUrlEncodedContent(parameters) };
            var bytes = await tokenClient.SendAsync(message, 64 * 1024, cancellationToken, SpeechResponseFormat.Json).ConfigureAwait(false);
            using var json = CloudSpeechProtocol.Json(bytes);
            if (json.RootElement.TryGetProperty("Code", out var code))
                throw new SpeechServiceException(CloudSpeechProtocol.ErrorCode(code.GetString() ?? ""));
            var result = json.RootElement.GetProperty("Token");
            var next = result.GetProperty("Id").GetString();
            var expiry = DateTimeOffset.FromUnixTimeSeconds(result.GetProperty("ExpireTime").GetInt64());
            if (!IsValidCredential(next) || expiry <= clock().AddSeconds(5))
                throw new SpeechServiceException(SpeechServiceFailure.Authentication);
            token = next!; expiresAt = expiry;
            Interlocked.Exchange(ref tokenInvalidated, 0);
            return token;
        }
        finally { tokenGate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        lifetime.Cancel();
        await client.DisposeAsync().ConfigureAwait(false);
        await tokenClient.DisposeAsync().ConfigureAwait(false);
    }
}
