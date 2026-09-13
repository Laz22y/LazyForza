using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace LazyForza.Speech;

public enum SpeechServiceFailure { Configuration, Authentication, RateLimited, Unavailable, InvalidAudio, Timeout }

/// <summary>Safe error metadata only: never retain service response bodies, headers or credentials.</summary>
public sealed class SpeechServiceException(SpeechServiceFailure failure, TimeSpan? retryAfter = null)
    : Exception($"Speech service: {failure}")
{
    public SpeechServiceFailure Failure { get; } = failure;
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

public sealed record SpeechVoiceInfo(string Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>Official HTTPS API; short requests return bounded raw PCM16, never a remote playback URL.</summary>
public sealed class ElevenLabsSpeechProvider : ISpeechSynthesisProvider
{
    public const string DefaultModel = "eleven_flash_v2_5";
    public static IReadOnlyList<string> Models { get; } = Array.AsReadOnly(new[]
        { DefaultModel, "eleven_multilingual_v2", "eleven_v3" });
    private const int SampleRate = 24000;
    private readonly HttpClient client;
    private readonly string apiKey;
    private readonly string model;
    private readonly TimeSpan timeout;
    private readonly CancellationTokenSource lifetime = new();
    private int disposed;
    public string Id => "elevenlabs";
    public SpeechProviderLocation Location => SpeechProviderLocation.Online;

    public ElevenLabsSpeechProvider(string apiKey, string model = DefaultModel,
        HttpMessageHandler? handler = null, TimeSpan? timeout = null)
    {
        this.apiKey = apiKey.Trim();
        this.model = model;
        this.timeout = timeout ?? TimeSpan.FromSeconds(6);
        if (this.timeout <= TimeSpan.Zero || this.timeout > TimeSpan.FromSeconds(10))
            throw new ArgumentOutOfRangeException(nameof(timeout));
        client = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false }, disposeHandler: true)
        { BaseAddress = new Uri("https://api.elevenlabs.io/"), Timeout = Timeout.InfiniteTimeSpan };
    }

    public static bool IsValidVoiceId(string? id) => !string.IsNullOrWhiteSpace(id) && id.Length <= 128 &&
        id.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    public async Task<SpeechAudio> SynthesizeAsync(SpeechSynthesisRequest request, CancellationToken cancellationToken)
    {
        if (!IsValidVoiceId(request.VoiceId) || !Models.Contains(model) ||
            string.IsNullOrWhiteSpace(request.Text) || request.Text.Length > 512 ||
            !double.IsFinite(request.Rate) || request.Rate is < .7 or > 1.2)
            throw new SpeechServiceException(SpeechServiceFailure.Configuration);
        using var message = CreateRequest(HttpMethod.Post,
            $"v1/text-to-speech/{request.VoiceId}?output_format=pcm_24000");
        // Language follows the actual phrase. Multilingual v2 does not support language_code.
        message.Content = JsonContent.Create(new
        {
            text = request.Text,
            model_id = model,
            voice_settings = new { speed = request.Rate }
        });
        var bytes = await SendAsync(message, SampleRate * 2 * SpeechAudio.MaximumDurationSeconds,
            cancellationToken).ConfigureAwait(false);
        if (bytes.Length == 0 || bytes.Length % 2 != 0)
            throw new SpeechServiceException(SpeechServiceFailure.InvalidAudio);
        return new SpeechAudio(bytes, SampleRate);
    }

    public async Task<IReadOnlyList<SpeechVoiceInfo>> GetVoicesAsync(CancellationToken cancellationToken)
    {
        var voices = new Dictionary<string, SpeechVoiceInfo>(StringComparer.Ordinal);
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        string? next = null;
        // Bound pagination even if a malformed upstream repeatedly claims more pages.
        for (var page = 0; page < 20; page++)
        {
            using var message = CreateRequest(HttpMethod.Get, "v2/voices?page_size=100&include_total_count=false" +
                (next is null ? "" : "&next_page_token=" + Uri.EscapeDataString(next)));
            var bytes = await SendAsync(message, 1024 * 1024, cancellationToken).ConfigureAwait(false);
            try
            {
                using var json = JsonDocument.Parse(bytes);
                foreach (var voice in json.RootElement.GetProperty("voices").EnumerateArray())
                {
                    var id = voice.GetProperty("voice_id").GetString();
                    var name = voice.GetProperty("name").GetString();
                    if (IsValidVoiceId(id) && !string.IsNullOrWhiteSpace(name))
                        voices[id!] = new SpeechVoiceInfo(id!, name[..Math.Min(name.Length, 160)]);
                }
                if (!json.RootElement.GetProperty("has_more").GetBoolean())
                    return voices.Values.OrderBy(v => v.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
                next = json.RootElement.GetProperty("next_page_token").GetString();
                if (string.IsNullOrEmpty(next) || next.Length > 2048 || !tokens.Add(next)) break;
            }
            catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException)
            { throw new SpeechServiceException(SpeechServiceFailure.Unavailable); }
        }
        throw new SpeechServiceException(SpeechServiceFailure.Unavailable);
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (apiKey.Length is < 1 or > 512 || apiKey.Any(c => c < '!' || c > '~'))
            throw new SpeechServiceException(SpeechServiceFailure.Configuration);
        var message = new HttpRequestMessage(method, path);
        message.Headers.Add("xi-api-key", apiKey);
        return message;
    }

    private async Task<byte[]> SendAsync(HttpRequestMessage request, int maximum, CancellationToken cancellationToken)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        cancellation.CancelAfter(timeout);
        var token = cancellation.Token;
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var failure = response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => SpeechServiceFailure.Authentication,
                    HttpStatusCode.TooManyRequests => SpeechServiceFailure.RateLimited,
                    HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.UnprocessableEntity => SpeechServiceFailure.Configuration,
                    _ => SpeechServiceFailure.Unavailable
                };
                var retry = response.Headers.RetryAfter?.Delta ??
                    (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow);
                throw new SpeechServiceException(failure, retry);
            }
            var type = response.Content.Headers.ContentType?.MediaType;
            if (request.Method == HttpMethod.Post && type is not null &&
                type != "application/octet-stream" && type != "audio/pcm" && type != "audio/raw" && type != "audio/x-pcm")
                throw new SpeechServiceException(SpeechServiceFailure.InvalidAudio);
            if (response.Content.Headers.ContentLength > maximum)
                throw new SpeechServiceException(SpeechServiceFailure.InvalidAudio);
            await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var output = new MemoryStream();
            var buffer = new byte[8192];
            int count;
            while ((count = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
            {
                if (output.Length + count > maximum)
                    throw new SpeechServiceException(SpeechServiceFailure.InvalidAudio);
                output.Write(buffer, 0, count);
            }
            return output.ToArray();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !lifetime.IsCancellationRequested)
        { throw new SpeechServiceException(SpeechServiceFailure.Timeout); }
        catch (HttpRequestException) { throw new SpeechServiceException(SpeechServiceFailure.Unavailable); }
        catch (IOException) { throw new SpeechServiceException(SpeechServiceFailure.Unavailable); }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            lifetime.Cancel();
            client.Dispose();
        }
        return ValueTask.CompletedTask;
    }
}
