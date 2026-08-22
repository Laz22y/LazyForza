using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace LazyForza.App;

internal sealed record AppLanguageOption(string Code, string NativeName, string EnglishName);

internal static class AppLocalization
{
    private static readonly AppLanguageOption[] LanguageOptions =
    [
        new("zh-Hans", "简体中文", "Simplified Chinese"),
        new("en", "English", "English")
    ];

    private static IReadOnlyDictionary<string, string> translations =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public static string CurrentLanguage { get; private set; } = StartupProfile.DefaultLanguage;

    public static IReadOnlyList<AppLanguageOption> SupportedLanguages => LanguageOptions;

    public static bool IsSupported(string? language) =>
        LanguageOptions.Any(option => option.Code.Equals(language, StringComparison.OrdinalIgnoreCase));

    public static void UseLanguage(string? language)
    {
        var option = LanguageOptions.FirstOrDefault(item =>
            item.Code.Equals(language, StringComparison.OrdinalIgnoreCase)) ?? LanguageOptions[0];
        CurrentLanguage = option.Code;
        translations = LoadTranslations(option.Code);
        var culture = option.Code == "en" ? CultureInfo.GetCultureInfo("en-US") : CultureInfo.GetCultureInfo("zh-CN");
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
    }

    public static string Text(string key, string chineseFallback)
    {
        if (CurrentLanguage == StartupProfile.DefaultLanguage) return chineseFallback;
        return translations.TryGetValue(key, out var localized) && !string.IsNullOrWhiteSpace(localized)
            ? localized
            : chineseFallback;
    }

    public static string Format(string key, string chineseFallback, params object?[] arguments) =>
        string.Format(CultureInfo.CurrentCulture, Text(key, chineseFallback), arguments);

    public static string Literal(string text)
    {
        if (string.IsNullOrEmpty(text) || CurrentLanguage == StartupProfile.DefaultLanguage) return text;
        return translations.TryGetValue($"literal:{text}", out var localized) && !string.IsNullOrWhiteSpace(localized)
            ? localized
            : text;
    }

    private static IReadOnlyDictionary<string, string> LoadTranslations(string language)
    {
        if (language == StartupProfile.DefaultLanguage)
            return new Dictionary<string, string>(StringComparer.Ordinal);

        var assembly = typeof(AppLocalization).Assembly;
        var suffix = $".Localization.{language}.json";
        var resourceName = assembly.GetManifestResourceNames().SingleOrDefault(name =>
            name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
        if (resourceName is null) return new Dictionary<string, string>(StringComparer.Ordinal);
        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null) return new Dictionary<string, string>(StringComparer.Ordinal);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ??
               new Dictionary<string, string>(StringComparer.Ordinal);
    }
}
