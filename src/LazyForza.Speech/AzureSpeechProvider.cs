using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace LazyForza.Speech;

/// <summary>Azure public-cloud regional Speech REST API; user text is escaped as SSML text content.</summary>
public sealed class AzureSpeechProvider : ISpeechSynthesisProvider
{
    private const int SampleRate = 24000;
    private readonly SpeechServiceHttpClient client;
    private readonly string key;
    private readonly string region;
    public string Id => "azure";
    public SpeechProviderLocation Location => SpeechProviderLocation.Online;

    public AzureSpeechProvider(string key, string region, HttpMessageHandler? handler = null, TimeSpan? timeout = null)
    {
        this.key = key?.Trim() ?? "";
        this.region = region?.Trim().ToLowerInvariant() ?? "";
        // Invalid persisted settings fail at synthesis, where optional local fallback can handle them.
        var hostRegion = IsValidRegion(this.region) ? this.region : "invalid";
        client = new SpeechServiceHttpClient(new Uri($"https://{hostRegion}.tts.speech.microsoft.com/"), handler, timeout);
    }

    public static bool IsValidRegion(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 40 &&
        value.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9') &&
        !value.StartsWith("china", StringComparison.Ordinal) && !value.StartsWith("usgov", StringComparison.Ordinal);

    public static bool IsValidVoiceId(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 128 &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or ':') && VoiceLocale(value) is not null;

    public static string? VoiceLocale(string? id)
    {
        var parts = id?.Split('-', 3);
        if (parts is not { Length: 3 } || parts[0] is not ("zh" or "en") || parts[1].Length != 2 || parts[2].Length == 0) return null;
        try { return CultureInfo.GetCultureInfo(parts[0] + "-" + parts[1]).Name; }
        catch (CultureNotFoundException) { return null; }
    }

    public async Task<SpeechAudio> SynthesizeAsync(SpeechSynthesisRequest request, CancellationToken cancellationToken)
    {
        if (!IsValidVoiceId(request.VoiceId) || string.IsNullOrWhiteSpace(request.Text) || request.Text.Length > 512 ||
            !double.IsFinite(request.Rate) || request.Rate is < .5 or > 2 ||
            request.Language is not ("en-US" or "zh-CN"))
            throw new SpeechServiceException(SpeechServiceFailure.Configuration);
        XNamespace ssml = "http://www.w3.org/2001/10/synthesis";
        string text;
        try
        {
            XmlConvert.VerifyXmlChars(request.Text);
            text = new XElement(ssml + "speak", new XAttribute("version", "1.0"),
                new XAttribute(XNamespace.Xml + "lang", request.Language),
                new XElement(ssml + "voice", new XAttribute("name", request.VoiceId!),
                    request.Rate == 1 ? (object)new XText(request.Text) :
                    new XElement(ssml + "prosody", new XAttribute("rate", request.Rate.ToString("0.###", CultureInfo.InvariantCulture)), request.Text)))
                .ToString(SaveOptions.DisableFormatting);
        }
        catch (XmlException) { throw new SpeechServiceException(SpeechServiceFailure.Configuration); }
        using var message = CreateRequest(HttpMethod.Post, "cognitiveservices/v1");
        message.Headers.Add("X-Microsoft-OutputFormat", "raw-24khz-16bit-mono-pcm");
        message.Content = new StringContent(text, Encoding.UTF8, "application/ssml+xml");
        var bytes = await client.SendAsync(message, SampleRate * 2 * SpeechAudio.MaximumDurationSeconds, cancellationToken).ConfigureAwait(false);
        if (bytes.Length == 0 || bytes.Length % 2 != 0) throw new SpeechServiceException(SpeechServiceFailure.InvalidAudio);
        return new SpeechAudio(bytes, SampleRate);
    }

    public async Task<IReadOnlyList<SpeechVoiceInfo>> GetVoicesAsync(CancellationToken cancellationToken)
    {
        using var message = CreateRequest(HttpMethod.Get, "cognitiveservices/voices/list");
        var bytes = await client.SendAsync(message, 4 * 1024 * 1024, cancellationToken).ConfigureAwait(false);
        try
        {
            using var json = JsonDocument.Parse(bytes);
            var result = new Dictionary<string, SpeechVoiceInfo>(StringComparer.Ordinal);
            foreach (var voice in json.RootElement.EnumerateArray())
            {
                var id = voice.GetProperty("ShortName").GetString();
                var locale = voice.GetProperty("Locale").GetString();
                var name = voice.TryGetProperty("LocalName", out var localName) ? localName.GetString() : id;
                if (!IsValidVoiceId(id) || VoiceLocale(id) != locale) continue;
                name = string.IsNullOrWhiteSpace(name) ? id! : name;
                result[id!] = new SpeechVoiceInfo(id!, name[..Math.Min(name.Length, 128)] + " · " + locale);
                if (result.Count > 2000) throw new SpeechServiceException(SpeechServiceFailure.Unavailable);
            }
            return result.Values.OrderBy(v => v.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException)
        { throw new SpeechServiceException(SpeechServiceFailure.Unavailable); }
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        if (!IsValidRegion(region)) throw new SpeechServiceException(SpeechServiceFailure.Configuration);
        var message = client.CreateRequest(method, path, "Ocp-Apim-Subscription-Key", key);
        message.Headers.UserAgent.ParseAdd("LazyForza/1.0");
        return message;
    }

    public ValueTask DisposeAsync() => client.DisposeAsync();
}
