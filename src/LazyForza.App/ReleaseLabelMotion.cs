namespace LazyForza.App;

internal enum ReleaseLabelMode { Combined, Alternate, Scroll }

internal readonly record struct ReleaseLabelFrame(int Item, double Offset, double Opacity, double NextUpdateSeconds);

/// <summary>Presentation timing in logical pixels. The view supplies measured text widths and active elapsed time.</summary>
internal sealed class ReleaseLabelMotion
{
    private const double Fade = .1, Hold = 5, EndHold = 1.5, Speed = 24, FitMargin = 4;
    private double available, versionWidth, nameWidth;
    private bool hasName, reduced;
    private double elapsed = Fade;
    public ReleaseLabelMode Mode { get; private set; }

    public void Configure(double width, double version, double name, double combined, bool named, bool reduceMotion)
    {
        available = Math.Max(0, width);
        versionWidth = version;
        nameWidth = name;
        hasName = named;
        var fits = combined <= available - (Mode == ReleaseLabelMode.Combined ? 0 : FitMargin);
        var next = fits ? ReleaseLabelMode.Combined
            : Math.Max(version, named ? name : 0) > available ? ReleaseLabelMode.Scroll : ReleaseLabelMode.Alternate;
        if (next != Mode || reduced != reduceMotion) Reset();
        Mode = next;
        reduced = reduceMotion;
    }

    public void Reset() => elapsed = Fade;

    public void Advance(double seconds, bool paused = false)
    {
        if (paused || reduced || Mode == ReleaseLabelMode.Combined || !double.IsFinite(seconds) || seconds <= 0) return;
        elapsed = (elapsed + seconds) % (Duration(versionWidth) + (hasName ? Duration(nameWidth) : 0));
    }

    public ReleaseLabelFrame Frame
    {
        get
        {
            if (reduced || Mode == ReleaseLabelMode.Combined || available <= 0)
                return new(-1, 0, 1, double.PositiveInfinity);
            var time = elapsed;
            var item = 0;
            if (hasName && time >= Duration(versionWidth)) { time -= Duration(versionWidth); item = 1; }
            var overflow = Math.Max(0, (item == 0 ? versionWidth : nameWidth) - available);
            var scrollTime = overflow / Speed;
            if (time < Fade) return new(item, 0, time / Fade, 1d / 30);
            time -= Fade;
            if (time < Hold) return new(item, 0, 1, Hold - time);
            time -= Hold;
            if (time < scrollTime) return new(item, -time * Speed, 1, 1d / 30);
            time -= scrollTime;
            if (overflow > 0 && time < EndHold) return new(item, -overflow, 1, EndHold - time);
            if (overflow > 0) time -= EndHold;
            return new(item, -overflow, Math.Clamp(1 - time / Fade, 0, 1), 1d / 30);
        }
    }

    private double Duration(double textWidth) => Fade * 2 + Hold +
        (textWidth > available ? (textWidth - available) / Speed + EndHold : 0);
}
