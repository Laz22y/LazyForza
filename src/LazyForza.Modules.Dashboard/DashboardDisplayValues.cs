using LazyForza.Domain;

namespace LazyForza.Modules.Dashboard;

public static class DashboardDisplayValues
{
    public static double NonNegativeOutput(double value) =>
        double.IsFinite(value) && value > 0 ? value : 0d;

    public static double FahrenheitToCelsius(double fahrenheit) =>
        double.IsFinite(fahrenheit) ? (fahrenheit - 32d) * 5d / 9d : double.NaN;

    public static WheelValues TireTemperatureCelsius(WheelValues fahrenheit) => new(
        (float)FahrenheitToCelsius(fahrenheit.FrontLeft),
        (float)FahrenheitToCelsius(fahrenheit.FrontRight),
        (float)FahrenheitToCelsius(fahrenheit.RearLeft),
        (float)FahrenheitToCelsius(fahrenheit.RearRight));

    public static double TireHeatIntensityCelsius(double temperature)
    {
        if (!double.IsFinite(temperature)) return 0;
        var normalized = Math.Clamp((temperature - 45d) / 75d, 0, 1);
        return normalized * normalized * (3 - 2 * normalized);
    }
}
