using System.Reflection;
using LazyForza.Update;

namespace LazyForza.App;

internal static class ApplicationVersionInfo
{
    private static readonly IReadOnlyDictionary<string, string?> ReleaseNames = typeof(ApplicationVersionInfo).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>().Where(attribute => attribute.Key.StartsWith("ReleaseName.", StringComparison.Ordinal))
        .ToDictionary(attribute => attribute.Key, attribute => attribute.Value);

    public static string ReleaseName => ReleaseNameForLanguage(AppLocalization.CurrentLanguage);

    internal static string ReleaseNameForLanguage(string language) =>
        ReleaseNames.GetValueOrDefault(language.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? "ReleaseName.en" : "ReleaseName.zh-Hans")?.Trim() ?? "";

    public static string Informational
    {
        get
        {
            var assembly = typeof(ApplicationVersionInfo).Assembly;
            var informational = assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion
                .Split('+', 2)[0];
            if (!string.IsNullOrWhiteSpace(informational)) return informational;

            var version = assembly.GetName().Version;
            return version is null
                ? "0.0.0"
                : version.Build >= 0
                    ? version.ToString(3)
                    : $"{version.Major}.{version.Minor}.0";
        }
    }

    public static UpdateSemanticVersion UpdateVersion =>
        UpdateSemanticVersion.TryParse(Informational, out var version)
            ? version
            : UpdateSemanticVersion.Parse("0.0.0");

    public static string Display
    {
        get
        {
            var informational = Informational;
            return informational.EndsWith("-dev", StringComparison.OrdinalIgnoreCase)
                ? $"{informational[..^4]} dev"
                : informational;
        }
    }
}
