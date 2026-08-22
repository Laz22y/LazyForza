using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using LazyForza.Overlay;

namespace LazyForza.App;

internal sealed record AppLanguageOption(string Code, string NativeName, string EnglishName)
{
    public override string ToString() => NativeName;
}

internal static class AppLocalization
{
    private static readonly AppLanguageOption[] LanguageOptions =
    [
        new("zh-Hans", "简体中文", "Simplified Chinese"),
        new("en", "English", "English")
    ];

    private static IReadOnlyDictionary<string, string> translations =
        new Dictionary<string, string>(StringComparer.Ordinal);
    private static readonly Regex ConfirmingStartPattern = new(
        "^正在确认起点 · (?<count>[0-9]+) 个轨迹点$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex DriveThroughLapsPattern = new(
        "^还可跨越终点线 (?<count>[0-9]+) 次$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CandidateVerifiedPattern = new(
        "^候选：(?<name>.+) · 已验证 (?<meters>[0-9]+) m$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

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
        OverlayTextLocalization.Configure(Literal);
        var culture = option.Code == "en" ? CultureInfo.GetCultureInfo("en-US") : CultureInfo.GetCultureInfo("zh-CN");
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentCulture = culture;
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
        if (translations.TryGetValue($"literal:{text}", out var localized) &&
            !string.IsNullOrWhiteSpace(localized))
            return localized;
        var match = ConfirmingStartPattern.Match(text);
        if (match.Success)
            return Format(
                "template.confirmingStart",
                "正在确认起点 · {0} 个轨迹点",
                match.Groups["count"].Value);
        match = DriveThroughLapsPattern.Match(text);
        if (match.Success)
            return Format(
                "template.driveThroughLaps",
                "还可跨越终点线 {0} 次",
                match.Groups["count"].Value);
        match = CandidateVerifiedPattern.Match(text);
        if (match.Success)
            return Format(
                "template.candidateVerified",
                "候选：{0} · 已验证 {1} m",
                match.Groups["name"].Value,
                match.Groups["meters"].Value);
        return text;
    }

    public static void ApplyTo(DependencyObject root)
    {
        if (CurrentLanguage == StartupProfile.DefaultLanguage) return;
        switch (root)
        {
            case TextBlock textBlock:
                textBlock.Text = Literal(textBlock.Text);
                break;
            case ContentControl contentControl when contentControl.Content is string content:
                contentControl.Content = Literal(content);
                break;
            case HeaderedContentControl headered when headered.Header is string header:
                headered.Header = Literal(header);
                break;
            case ItemsControl itemsControl when itemsControl.ItemsSource is null:
                for (var index = 0; index < itemsControl.Items.Count; index++)
                {
                    if (itemsControl.Items[index] is string item)
                        itemsControl.Items[index] = Literal(item);
                }
                break;
        }

        if (root is FrameworkElement element && element.ToolTip is string tooltip)
            element.ToolTip = Literal(tooltip);
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            ApplyTo(child);
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
