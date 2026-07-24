namespace LazyForza.Domain;

/// <summary>
/// Prevents short, undocumented FH6 gear-transition codes from flashing in the HUD.
/// The raw wire value remains untouched in telemetry diagnostics and recordings.
/// </summary>
public sealed class GearDisplayStabilizer
{
    private readonly TimeSpan holdDuration;
    private ResolvedGear? lastKnown;
    private DateTimeOffset lastKnownAt;

    public GearDisplayStabilizer(TimeSpan? holdDuration = null)
    {
        this.holdDuration = holdDuration ?? TimeSpan.FromMilliseconds(350);
        if (this.holdDuration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(holdDuration));
        }
    }

    public ResolvedGear Resolve(byte rawCode, DateTimeOffset observedAt, bool isDriving)
    {
        if (!isDriving)
        {
            Reset();
            return new ResolvedGear(null, "—", false);
        }

        if (ForzaGear.IsKnown(rawCode))
        {
            var resolved = new ResolvedGear(
                ForzaGear.ForwardNumber(rawCode),
                ForzaGear.Display(rawCode),
                false);
            lastKnown = resolved;
            lastKnownAt = observedAt;
            return resolved;
        }

        var age = observedAt - lastKnownAt;
        if (lastKnown is { } previous && age >= TimeSpan.Zero && age <= holdDuration)
        {
            return previous with { IsHeld = true };
        }

        lastKnown = null;
        return new ResolvedGear(null, "—", false);
    }

    public void Reset()
    {
        lastKnown = null;
        lastKnownAt = default;
    }
}

public readonly record struct ResolvedGear(int? ForwardGear, string Display, bool IsHeld);
