namespace LazyForza.Speech;

/// <summary>One immutable cue/timing snapshot for an entire transmission.</summary>
public sealed record RadioTransmission
{
    public static RadioTransmission Default { get; } = new();
    public SpeechAudio Connect { get; init; } = RadioCues.Connect;
    public SpeechAudio Disconnect { get; init; } = RadioCues.Disconnect;
    public TimeSpan AfterConnect { get; init; } = TimeSpan.FromMilliseconds(180);
    public TimeSpan BeforeDisconnect { get; init; } = TimeSpan.FromMilliseconds(220);

    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Connect);
        ArgumentNullException.ThrowIfNull(Disconnect);
        if (Connect.Duration.TotalSeconds > 5 || Disconnect.Duration.TotalSeconds > 5)
            throw new ArgumentException("Radio cues must be no longer than 5 seconds.");
        if (AfterConnect < TimeSpan.Zero || AfterConnect > TimeSpan.FromSeconds(2) ||
            BeforeDisconnect < TimeSpan.Zero || BeforeDisconnect > TimeSpan.FromSeconds(2))
            throw new ArgumentOutOfRangeException(nameof(AfterConnect));
    }
}
