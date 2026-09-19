namespace LazyForza.Modules.EstateRace;

public enum EngineerDensity { Essential, Balanced, Detailed }

public sealed record EngineerPreferences(EngineerDensity Density = EngineerDensity.Balanced,
    bool Flags = true, bool Penalties = true, bool LapTimes = true, bool PitAdvice = true)
{
    public EngineerPreferences Normalize() => Enum.IsDefined(Density) ? this : this with { Density = EngineerDensity.Balanced };

    public bool AllowsCategory(string category) => category switch
    {
        "flag" => Flags,
        "best" => LapTimes,
        "pit" => PitAdvice,
        _ when category == "penalty" || category.StartsWith("penalty:", StringComparison.Ordinal) => Penalties,
        _ => true
    };

    public bool Allows(EngineerMessage message) => AllowsCategory(message.Category) &&
        (Density != EngineerDensity.Essential || message.Priority != EngineerPriority.Information);

    public TimeSpan Cooldown(EngineerMessage message) => Density == EngineerDensity.Detailed && message.Priority == EngineerPriority.Information
        ? TimeSpan.FromTicks(message.Cooldown.Ticks / 2) : message.Cooldown;
}

public enum EngineerDelivery { Speaking, Completed, Interrupted, Failed }
public enum EngineerRepeatState { Ready, NoHistory, Incomplete, Expired, StateChanged, NoSession, Disabled, Muted, CategoryDisabled, Busy, Unavailable }
public sealed record EngineerBroadcast(long Id, DateTimeOffset At, string Category, string Text,
    EngineerDelivery Delivery, bool IsRepeat, EngineerRepeatState Validity);
