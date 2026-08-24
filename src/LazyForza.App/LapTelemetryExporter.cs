using System.Globalization;
using System.IO;
using System.Text;
using LazyForza.Domain;

namespace LazyForza.App;

internal static class LapTelemetryExporter
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static void WriteCsv(
        string path,
        string trackName,
        LapRecord lap)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var writer = new StreamWriter(
            Path.GetFullPath(path),
            append: false,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        writer.WriteLine(AppLocalization.Literal("LazyForza 圈速遥测导出"));
        WriteMetadata(writer, AppLocalization.Literal("赛道"), trackName);
        WriteMetadata(writer, AppLocalization.Literal("圈速 ID"), lap.Id.ToString());
        WriteMetadata(writer, AppLocalization.Literal("开始时间"), lap.StartedAt.ToString("O"));
        WriteMetadata(writer, AppLocalization.Literal("总用时（秒）"), Number(lap.TotalSeconds));
        WriteMetadata(writer, AppLocalization.Literal("性能等级"), PerformanceClassCatalog.Name(lap.Vehicle.CarClass));
        WriteMetadata(writer, AppLocalization.Literal("性能指数"), lap.Vehicle.PerformanceIndex.ToString(Invariant));
        WriteMetadata(writer, AppLocalization.Literal("车辆序号"), lap.Vehicle.CarOrdinal.ToString(Invariant));
        WriteMetadata(
            writer,
            AppLocalization.Literal("有效性"),
            lap.IsValid
                ? AppLocalization.Literal("有效")
                : AppLocalization.Format(
                    "telemetry.export.invalid",
                    "无效：{0}",
                    AppLocalization.Literal(lap.InvalidReason ?? "原因未知")));
        WriteMetadata(
            writer,
            AppLocalization.Literal("动态遥测"),
            lap.Samples.Any(sample => sample.Dynamics is not null)
                ? AppLocalization.Literal("包含方向与轮胎滑移")
                : AppLocalization.Literal("旧版圈速，不包含方向与轮胎滑移"));
        writer.WriteLine();
        writer.WriteLine(
            "elapsed_s,distance_m,speed_kph,rpm,gear,throttle,brake,delta_s," +
            "position_x,position_y,position_z,steering," +
            "slip_ratio_fl,slip_ratio_fr,slip_ratio_rl,slip_ratio_rr," +
            "slip_angle_fl,slip_angle_fr,slip_angle_rl,slip_angle_rr," +
            "combined_slip_fl,combined_slip_fr,combined_slip_rl,combined_slip_rr");
        foreach (var sample in lap.Samples)
        {
            var values = new List<string>(24)
            {
                Number(sample.ElapsedSeconds),
                Number(sample.S),
                Number(sample.SpeedMps * 3.6),
                Number(sample.Rpm),
                sample.Gear.ToString(Invariant),
                Number(sample.Accel),
                Number(sample.Brake),
                Number(sample.DeltaSeconds),
                Number(sample.X),
                Number(sample.Y),
                Number(sample.Z)
            };
            AppendDynamics(values, sample.Dynamics);
            writer.WriteLine(string.Join(',', values));
        }
    }

    private static void AppendDynamics(
        ICollection<string> values,
        LapDynamics? dynamics)
    {
        if (dynamics is null)
        {
            for (var index = 0; index < 13; index++) values.Add(string.Empty);
            return;
        }
        values.Add(Number(dynamics.Steering));
        AppendWheel(values, dynamics.TireSlipRatio);
        AppendWheel(values, dynamics.TireSlipAngle);
        AppendWheel(values, dynamics.TireCombinedSlip);
    }

    private static void AppendWheel(
        ICollection<string> values,
        WheelValues wheel)
    {
        values.Add(Number(wheel.FrontLeft));
        values.Add(Number(wheel.FrontRight));
        values.Add(Number(wheel.RearLeft));
        values.Add(Number(wheel.RearRight));
    }

    private static void WriteMetadata(
        TextWriter writer,
        string key,
        string value) =>
        writer.WriteLine($"{Escape(key)},{Escape(value)}");

    private static string Number(double value) =>
        double.IsFinite(value)
            ? value.ToString("0.######", Invariant)
            : string.Empty;

    private static string Escape(string value) =>
        value.IndexOfAny([',', '"', '\r', '\n']) < 0
            ? value
            : $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
}
