using System.Reflection;

namespace LazyForza.App;

internal static class ApplicationVersionInfo
{
    public static string Display
    {
        get
        {
            var assembly = typeof(ApplicationVersionInfo).Assembly;
            var informational = assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion
                .Split('+', 2)[0];
            if (!string.IsNullOrWhiteSpace(informational))
                return informational.EndsWith("-dev", StringComparison.OrdinalIgnoreCase)
                    ? $"{informational[..^4]} dev"
                    : informational;

            var version = assembly.GetName().Version;
            return version is null
                ? AppLocalization.Literal("未知")
                : version.Build >= 0
                    ? version.ToString(3)
                    : version.ToString(2);
        }
    }
}
