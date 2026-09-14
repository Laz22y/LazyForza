using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LazyForza.Speech;

namespace LazyForza.App;

internal sealed record EngineerSpeechSettings(
    bool UseElevenLabs = false,
    string ProtectedApiKey = "",
    string VoiceId = "",
    string ModelId = ElevenLabsSpeechProvider.DefaultModel,
    string Language = "auto",
    bool FallbackToWindows = true,
    string? WindowsVoiceId = null,
    string? ProviderId = null,
    string AzureProtectedApiKey = "",
    string AzureRegion = "eastasia",
    string AzureVoiceId = "",
    string AzureLanguage = "auto",
    TencentEngineerSpeechSettings? TencentSettings = null,
    AlibabaEngineerSpeechSettings? AlibabaSettings = null,
    QwenEngineerSpeechSettings? QwenSettings = null,
    MiniMaxEngineerSpeechSettings? MiniMaxSettings = null)
{
    public const string Windows = "windows", ElevenLabs = "elevenlabs", Azure = "azure",
        Tencent = "tencent", Alibaba = "alibaba", Qwen = "qwen", MiniMax = "minimax";
    public const string StoreKey = "raceEngineer.speechService.v1";
    [JsonIgnore]
    public string ActiveProvider => ProviderId is null ? (UseElevenLabs ? ElevenLabs : Windows)
        : ProviderId is ElevenLabs or Azure or Tencent or Alibaba or Qwen or MiniMax ? ProviderId : Windows;
    [JsonIgnore]
    public bool IsOnline => ActiveProvider != Windows;
    [JsonIgnore]
    public string ServiceName => ActiveProvider switch
    {
        Azure => "Azure Speech", ElevenLabs => "ElevenLabs", Tencent => "Tencent Cloud",
        Alibaba => "Alibaba Cloud NLS", Qwen => "Qianwen AI", MiniMax => "MiniMax", _ => "Windows"
    };
    [JsonIgnore]
    public string SpeechLanguage => (ActiveProvider switch
    {
        Azure => AzureLanguage == "auto" ? AzureSpeechProvider.VoiceLocale(AzureVoiceId) ?? "auto" : AzureLanguage,
        Tencent => TencentSettings?.Language, Alibaba => AlibabaSettings?.Language,
        Qwen => QwenSettings?.Language, MiniMax => MiniMaxSettings?.Language, _ => Language
    }) ?? "auto";
    [JsonIgnore]
    public string ActiveVoiceId => ActiveProvider switch
    {
        Azure => AzureVoiceId, Tencent => (TencentSettings ?? new()).VoiceId, Alibaba => (AlibabaSettings ?? new()).VoiceId,
        Qwen => (QwenSettings ?? new()).VoiceId, MiniMax => (MiniMaxSettings ?? new()).VoiceId, _ => VoiceId
    };
    public static EngineerSpeechSettings Load(string? json)
    {
        try { return (string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<EngineerSpeechSettings>(json)) ?? new(); }
        catch (JsonException) { return new(); }
    }
    public string Serialize() => JsonSerializer.Serialize(this);
    public override string ToString() => ServiceName;
}

internal sealed record TencentEngineerSpeechSettings(string ProtectedSecretId = "", string ProtectedSecretKey = "",
    string VoiceId = TencentSpeechProvider.DefaultVoice, string Language = "auto")
{ public override string ToString() => "Tencent Cloud"; }
internal sealed record AlibabaEngineerSpeechSettings(string ProtectedAppKey = "", string ProtectedAccessKeyId = "",
    string ProtectedAccessKeySecret = "", string VoiceId = "xiaoyun", string Language = "auto")
{ public override string ToString() => "Alibaba Cloud NLS"; }
internal sealed record QwenEngineerSpeechSettings(string ProtectedApiKey = "", string VoiceId = "Cherry",
    string ModelId = QwenSpeechProvider.DefaultModel, string Language = "auto")
{ public override string ToString() => "Qianwen AI"; }
internal sealed record MiniMaxEngineerSpeechSettings(string ProtectedApiKey = "", string VoiceId = "male-qn-qingse",
    string ModelId = MiniMaxSpeechProvider.DefaultModel, string Endpoint = "china", string Language = "auto")
{ public override string ToString() => "MiniMax"; }

internal static class EngineerSpeechProviderFactory
{
    public static (ISpeechSynthesisProvider Provider, bool CredentialsReadable) CreateOnline(EngineerSpeechSettings settings)
    {
        var readable = true;
        string Secret(string cipher)
        {
            var success = EngineerCredentialProtection.TryUnprotect(cipher, out var value);
            readable &= success;
            return value;
        }
        ISpeechSynthesisProvider provider;
        switch (settings.ActiveProvider)
        {
            case EngineerSpeechSettings.ElevenLabs:
                provider = new ElevenLabsSpeechProvider(Secret(settings.ProtectedApiKey), settings.ModelId); break;
            case EngineerSpeechSettings.Azure:
                provider = new AzureSpeechProvider(Secret(settings.AzureProtectedApiKey), settings.AzureRegion); break;
            case EngineerSpeechSettings.Tencent:
                var tencent = settings.TencentSettings ?? new();
                provider = new TencentSpeechProvider(Secret(tencent.ProtectedSecretId), Secret(tencent.ProtectedSecretKey)); break;
            case EngineerSpeechSettings.Alibaba:
                var alibaba = settings.AlibabaSettings ?? new();
                provider = new AlibabaSpeechProvider(Secret(alibaba.ProtectedAppKey), Secret(alibaba.ProtectedAccessKeyId), Secret(alibaba.ProtectedAccessKeySecret)); break;
            case EngineerSpeechSettings.Qwen:
                var qwen = settings.QwenSettings ?? new();
                provider = new QwenSpeechProvider(Secret(qwen.ProtectedApiKey), qwen.ModelId); break;
            case EngineerSpeechSettings.MiniMax:
                var minimax = settings.MiniMaxSettings ?? new();
                provider = new MiniMaxSpeechProvider(Secret(minimax.ProtectedApiKey), minimax.ModelId, minimax.Endpoint); break;
            default: throw new InvalidOperationException("No online speech provider selected.");
        }
        return (provider, readable);
    }
}

/// <summary>User-supplied credentials are protected with Windows DPAPI for this user, not stored as plaintext.</summary>
internal static class EngineerCredentialProtection
{
    public static string Protect(string secret)
    {
        if (string.IsNullOrWhiteSpace(secret)) return "";
        var bytes = Encoding.UTF8.GetBytes(secret);
        try { return Convert.ToBase64String(Transform(bytes, protect: true)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public static bool TryUnprotect(string? protectedValue, out string secret)
    {
        secret = "";
        if (string.IsNullOrEmpty(protectedValue)) return true;
        try
        {
            var bytes = Transform(Convert.FromBase64String(protectedValue), protect: false);
            try { secret = Encoding.UTF8.GetString(bytes); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
            return true;
        }
        catch (Exception error) when (error is FormatException or CryptographicException) { return false; }
    }

    private static byte[] Transform(byte[] bytes, bool protect)
    {
        var input = new Blob { Size = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        var output = new Blob();
        try
        {
            Marshal.Copy(bytes, 0, input.Data, bytes.Length);
            var succeeded = protect
                ? CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!succeeded) throw new CryptographicException("Windows credential protection unavailable.");
            var result = new byte[output.Size];
            Marshal.Copy(output.Data, result, 0, result.Length);
            return result;
        }
        finally
        {
            for (var index = 0; index < input.Size; index++) Marshal.WriteByte(input.Data, index, 0);
            Marshal.FreeHGlobal(input.Data);
            if (output.Data != IntPtr.Zero)
            {
                for (var index = 0; index < output.Size; index++) Marshal.WriteByte(output.Data, index, 0);
                _ = LocalFree(output.Data);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Blob { public int Size; public IntPtr Data; }
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref Blob input, string? description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
