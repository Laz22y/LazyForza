using System.Windows;
using System.Windows.Media;

namespace LazyForza.App;

internal enum AppAccentColor
{
    DefaultBlue,
    MidnightPurple,
    FreshGreen,
    VividRed,
    PureWhite,
    SubtleGray
}

internal sealed record AppAccentColorDefinition(
    AppAccentColor Value,
    string LocalizationKey,
    string ChineseName,
    Color Color)
{
    public string DisplayName => AppLocalization.Text(LocalizationKey, ChineseName);
}

internal static class AppAccentColors
{
    private static readonly AppAccentColorDefinition[] AllDefinitions =
    [
        new(AppAccentColor.DefaultBlue, "settings.app.accent.defaultBlue", "默认蓝", Color.FromRgb(32, 184, 207)),
        new(AppAccentColor.MidnightPurple, "settings.app.accent.midnightPurple", "暗夜紫", Color.FromRgb(139, 92, 246)),
        new(AppAccentColor.FreshGreen, "settings.app.accent.freshGreen", "清新绿", Color.FromRgb(57, 217, 138)),
        new(AppAccentColor.VividRed, "settings.app.accent.vividRed", "鲜艳红", Color.FromRgb(255, 78, 96)),
        new(AppAccentColor.PureWhite, "settings.app.accent.pureWhite", "纯粹白", Colors.White),
        new(AppAccentColor.SubtleGray, "settings.app.accent.subtleGray", "低调灰", Color.FromRgb(130, 142, 156))
    ];

    public static IReadOnlyList<AppAccentColorDefinition> Definitions => AllDefinitions;

    public static bool IsDefined(AppAccentColor value) =>
        AllDefinitions.Any(definition => definition.Value == value);

    public static AppAccentColorDefinition Definition(AppAccentColor value) =>
        AllDefinitions.FirstOrDefault(definition => definition.Value == value) ?? AllDefinitions[0];

    public static void Apply(AppAccentColor value)
    {
        if (Application.Current is null) return;
        var color = Definition(value).Color;
        ApplyBrush("AccentBrush", color);
        ApplyBrush("AccentSoftBrush", Color.FromArgb(38, color.R, color.G, color.B));
        ApplyBrush("AccentFaintBrush", Color.FromArgb(24, color.R, color.G, color.B));
    }

    private static void ApplyBrush(string key, Color color)
    {
        if (Application.Current.Resources[key] is SolidColorBrush { IsFrozen: false } brush)
        {
            brush.Color = color;
            return;
        }

        Application.Current.Resources[key] = new SolidColorBrush(color);
    }
}
