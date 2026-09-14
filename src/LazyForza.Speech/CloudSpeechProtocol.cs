using System.Text.Json;

namespace LazyForza.Speech;

internal static class CloudSpeechProtocol
{
    public static bool IsIdentifier(string? value, int maximum = 128) => !string.IsNullOrWhiteSpace(value) &&
        value.Length <= maximum && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
    public static bool IsVoiceName(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 128 &&
        value == value.Trim() && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ' ' or '(' or ')');

    public static void ValidateRequest(SpeechSynthesisRequest request, int maximum = 512, bool supportsRate = false)
    {
        if (string.IsNullOrWhiteSpace(request.Text) || request.Text.Length > maximum ||
            request.Text.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t')) ||
            request.Language is not ("zh-CN" or "en-US") || !IsVoiceName(request.VoiceId) ||
            !double.IsFinite(request.Rate) || (supportsRate ? request.Rate is < .5 or > 2 : request.Rate != 1))
            throw new SpeechServiceException(SpeechServiceFailure.Configuration);
    }

    public static JsonDocument Json(byte[] bytes)
    {
        try { return JsonDocument.Parse(bytes); }
        catch (JsonException) { throw new SpeechServiceException(SpeechServiceFailure.InvalidAudio); }
    }

    public static bool IsMalformed(Exception error) => error is JsonException or InvalidOperationException or
        KeyNotFoundException or FormatException or OverflowException or ArgumentException;

    public static SpeechAudio Pcm(byte[] bytes, int sampleRate)
    {
        if (bytes.Length == 0 || bytes.Length % 2 != 0 || bytes.Length > sampleRate * 2 * SpeechAudio.MaximumDurationSeconds)
            throw new SpeechServiceException(SpeechServiceFailure.InvalidAudio);
        return new SpeechAudio(bytes, sampleRate);
    }

    public static byte[] Base64(string? value, int maximum)
    {
        if (string.IsNullOrEmpty(value) || value.Length > (maximum + 2) / 3 * 4 + 8)
            throw new SpeechServiceException(SpeechServiceFailure.InvalidAudio);
        try
        {
            var bytes = Convert.FromBase64String(value);
            if (bytes.Length > maximum) throw new SpeechServiceException(SpeechServiceFailure.InvalidAudio);
            return bytes;
        }
        catch (FormatException) { throw new SpeechServiceException(SpeechServiceFailure.InvalidAudio); }
    }

    public static SpeechServiceFailure ErrorCode(string code)
    {
        if (code.Contains("Throttl", StringComparison.OrdinalIgnoreCase) || code.Contains("LimitExceeded", StringComparison.OrdinalIgnoreCase) ||
            code is "InternalError.ExceedMaxLimit") return SpeechServiceFailure.RateLimited;
        if (code.Contains("Auth", StringComparison.OrdinalIgnoreCase) || code.Contains("AccessKey", StringComparison.OrdinalIgnoreCase) ||
            code.Contains("Signature", StringComparison.OrdinalIgnoreCase) || code.Contains("Forbidden", StringComparison.OrdinalIgnoreCase) ||
            code.Contains("Arrears", StringComparison.OrdinalIgnoreCase) || code.Contains("InvalidApiKey", StringComparison.OrdinalIgnoreCase) ||
            code.Contains("Exhausted", StringComparison.OrdinalIgnoreCase) || code.Contains("NoFreeAccount", StringComparison.OrdinalIgnoreCase))
            return SpeechServiceFailure.Authentication;
        if (code.StartsWith("Invalid", StringComparison.OrdinalIgnoreCase) || code.StartsWith("Missing", StringComparison.OrdinalIgnoreCase) ||
            code == "UnsupportedOperation.TextTooLong") return SpeechServiceFailure.Configuration;
        return SpeechServiceFailure.Unavailable;
    }
}
