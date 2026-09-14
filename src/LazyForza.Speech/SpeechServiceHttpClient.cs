using System.Net;

namespace LazyForza.Speech;

internal enum SpeechResponseFormat { RawPcm, Json, NlsPcm, ServerSentEvents }
internal sealed record SpeechServiceResponse(byte[] Body, string? MediaType);

/// <summary>Shared bounded transport and cancellation/error semantics for configured online providers.</summary>
internal sealed class SpeechServiceHttpClient : IAsyncDisposable
{
    private readonly HttpClient client;
    private readonly TimeSpan timeout;
    private readonly CancellationTokenSource lifetime = new();
    private int disposed;

    public SpeechServiceHttpClient(Uri baseAddress, HttpMessageHandler? handler, TimeSpan? timeout)
    {
        this.timeout = timeout ?? TimeSpan.FromSeconds(6);
        if (this.timeout <= TimeSpan.Zero || this.timeout > TimeSpan.FromSeconds(10))
            throw new ArgumentOutOfRangeException(nameof(timeout));
        client = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false }, disposeHandler: true)
        { BaseAddress = baseAddress, Timeout = Timeout.InfiniteTimeSpan };
    }

    public HttpRequestMessage CreateRequest(HttpMethod method, string path, string keyHeader, string apiKey, int maximumKeyLength = 512)
    {
        ValidateKey(apiKey, maximumKeyLength);
        var message = new HttpRequestMessage(method, path);
        message.Headers.Add(keyHeader, apiKey);
        return message;
    }

    public HttpRequestMessage CreateBearerRequest(HttpMethod method, string path, string apiKey, int maximumKeyLength = 512)
    {
        ValidateKey(apiKey, maximumKeyLength);
        var message = new HttpRequestMessage(method, path);
        message.Headers.Authorization = new("Bearer", apiKey);
        return message;
    }

    private void ValidateKey(string apiKey, int maximumKeyLength)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (apiKey.Length < 1 || apiKey.Length > maximumKeyLength || apiKey.Any(c => c < '!' || c > '~'))
            throw new SpeechServiceException(SpeechServiceFailure.Configuration);
    }

    public async Task<byte[]> SendAsync(HttpRequestMessage request, int maximum, CancellationToken cancellationToken,
        SpeechResponseFormat format = SpeechResponseFormat.RawPcm) =>
        (await SendResponseAsync(request, maximum, cancellationToken, format).ConfigureAwait(false)).Body;

    public async Task<SpeechServiceResponse> SendResponseAsync(HttpRequestMessage request, int maximum, CancellationToken cancellationToken,
        SpeechResponseFormat format = SpeechResponseFormat.RawPcm)
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
                    HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.UnprocessableEntity or HttpStatusCode.UnsupportedMediaType => SpeechServiceFailure.Configuration,
                    _ => SpeechServiceFailure.Unavailable
                };
                var retry = response.Headers.RetryAfter?.Delta ??
                    (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow);
                throw new SpeechServiceException(failure, retry);
            }
            var type = response.Content.Headers.ContentType?.MediaType;
            var validType = format switch
            {
                SpeechResponseFormat.Json => type is "application/json",
                SpeechResponseFormat.ServerSentEvents => type is "text/event-stream" or "application/json",
                // NLS labels successful PCM responses audio/mpeg; errors are returned as JSON.
                SpeechResponseFormat.NlsPcm => type is "audio/mpeg" or "application/octet-stream" or "audio/pcm" or "application/json",
                _ => request.Method != HttpMethod.Post || type is null or "application/octet-stream" or "audio/pcm" or "audio/raw" or "audio/x-pcm"
            };
            if (!validType)
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
            return new(output.ToArray(), type);
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
