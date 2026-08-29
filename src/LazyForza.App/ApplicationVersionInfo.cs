using System.Reflection;
using LazyForza.Update;

namespace LazyForza.App;

internal static class ApplicationVersionInfo
{
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
