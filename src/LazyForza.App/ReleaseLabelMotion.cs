namespace LazyForza.App;

internal enum ReleaseLabelMode { Combined, Alternate, Scroll }

// Zero delay requests composition frames; a positive delay sleeps until the next motion boundary.
internal readonly record struct ReleaseLabelFrame(int Item, double Offset, double Opacity, double NextUpdateSeconds);

/// <summary>Presentation timing in logical pixels. The view supplies measured text widths and active elapsed time.</summary>
internal sealed class ReleaseLabelMotion
{
    private const double FadeOut = .24, FadeIn = .32, Hold = 5, EndHold = 1.8, Speed = 20, Ramp = .65, FitMargin = 4;
    private double available, versionWidth, nameWidth;
    private bool hasName, reduced;
    private double elapsed;
    public ReleaseLabelMode Mode { get; private set; }

    public void Configure(double width, double version, double name, double combined, bool named, bool reduceMotion)
    {
        var metricsChanged = available != Math.Max(0, width) || versionWidth != version || nameWidth != name || hasName != named;
        available = Math.Max(0, width);
        versionWidth = version;
        nameWidth = name;
        hasName = named;
        var fits = combined <= available - (Mode == ReleaseLabelMode.Combined ? 0 : FitMargin);
        var next = fits ? ReleaseLabelMode.Combined
            : Math.Max(version, named ? name : 0) > available ? ReleaseLabelMode.Scroll : ReleaseLabelMode.Alternate;
        if (metricsChanged || next != Mode || reduced != reduceMotion) Reset();
        Mode = next;
        reduced = reduceMotion;
    }

    public void Reset() => elapsed = 0;

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
            var scrollTime = ScrollDuration(overflow);
            if (time < Hold) return new(item, 0, 1, Hold - time);
            time -= Hold;
            if (time < scrollTime) return new(item, -ScrollDistance(time, overflow, scrollTime), 1, 0);
            time -= scrollTime;
            if (overflow > 0 && time < EndHold) return new(item, -overflow, 1, EndHold - time);
            if (overflow > 0) time -= EndHold;
            // Small text becomes illegible when crossfaded on the same baseline. Swap only at zero opacity,
            // with no extra blank hold; give the incoming text slightly longer to settle into view.
            if (time < FadeOut) return new(item, -overflow, 1 - Ease(time / FadeOut), 0);
            return new(hasName ? 1 - item : 0, 0, Ease((time - FadeOut) / FadeIn), 0);
        }
    }

    private static double Ease(double progress) => (1 - Math.Cos(Math.PI * Math.Clamp(progress, 0, 1))) / 2;

    private double Duration(double textWidth) => Hold + FadeOut + FadeIn +
        (textWidth > available ? ScrollDuration(textWidth - available) + EndHold : 0);

    private static double ScrollDuration(double distance) => distance <= 0 ? 0 : Math.Max(Ramp * 2, distance / Speed + Ramp);

    private static double ScrollDistance(double time, double distance, double duration)
    {
        // Integrating a cosine velocity ramp keeps speed and acceleration continuous at both ends.
        // Long text cruises at a readable speed; short overflows use a lower peak speed, never a snap.
        var speed = distance / (duration - Ramp);
        double Start(double t) => speed * (t - Ramp / Math.PI * Math.Sin(Math.PI * t / Ramp)) / 2;
        if (time < Ramp) return Start(time);
        if (time > duration - Ramp) return distance - Start(duration - time);
        return speed * (time - Ramp / 2);
    }
}
