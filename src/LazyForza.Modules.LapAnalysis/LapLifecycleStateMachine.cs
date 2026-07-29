using LazyForza.Domain;

namespace LazyForza.Modules.LapAnalysis;

/// <summary>
/// Owns competition and lap-capture transitions. Track identification remains
/// an independent state machine and feeds a confirmed route into this one.
/// </summary>
internal sealed class LapLifecycleStateMachine
{
    public List<LapSample> Samples { get; } = [];
    public Guid SessionId { get; set; } = Guid.NewGuid();
    public bool CompetitionActive { get; set; }
    public bool CompetitionSignalSuspended { get; set; }
    public bool LapArmed { get; set; }
    public bool WaitingForInitialStartLine { get; set; }
    public ushort? PreviousLapNumber { get; set; }
    public float? PreviousCurrentLap { get; set; }
    public float? PreviousCurrentRaceTime { get; set; }
    public float? PreviousLastLap { get; set; }
    public float LastLapValueAtLapStart { get; set; }
    public float? LastRewindLapTime { get; set; }
    public TelemetryFrame? LastCompetitionFrame { get; set; }
    public DateTimeOffset LastCrossingAt { get; set; } = DateTimeOffset.MinValue;
    public DateTimeOffset? NonCompetitionDrivingSince { get; set; }
    public int ProjectionIndex { get; set; }
    public int ConfidentProjectionCount { get; set; }
    public double LastS { get; set; }
    public double[] SectorStartTimes { get; set; } = [];
    public double?[] CompletedSectorTimes { get; set; } = [];
    public int ValidProjectionSamples { get; set; }
    public int InvalidProjectionSamples { get; set; }
    public DateTimeOffset LapStartedAt { get; set; }
    public bool CurrentLapInvalidated { get; set; }
    public int ObservedSectorIndex { get; set; }

    public void BeginCompetition(Fh6RawTelemetry raw, bool trackNeedsIdentification)
    {
        ResetSession();
        CompetitionActive = true;
        SessionId = Guid.NewGuid();
        PreviousLapNumber = raw.LapNumber;
        PreviousCurrentLap = raw.CurrentLap;
        PreviousCurrentRaceTime = raw.CurrentRaceTime;
        PreviousLastLap = raw.LastLap;
        LastLapValueAtLapStart = raw.LastLap;
        WaitingForInitialStartLine = true;
        CurrentLapInvalidated = false;
        // With no route selected, geometry starts as unknown and is established
        // by the track-identification state machine.
        _ = trackNeedsIdentification;
    }

    public void BeginLap(
        double currentLapSeconds,
        float lastLapSeconds,
        DateTimeOffset arrivalTime,
        int sectorCount)
    {
        Samples.Clear();
        ProjectionIndex = 0;
        LastS = 0;
        SectorStartTimes = new double[sectorCount];
        CompletedSectorTimes = new double?[sectorCount];
        if (SectorStartTimes.Length > 0) SectorStartTimes[0] = currentLapSeconds;
        ObservedSectorIndex = 0;
        ValidProjectionSamples = 0;
        InvalidProjectionSamples = 0;
        CurrentLapInvalidated = false;
        LastLapValueAtLapStart = lastLapSeconds;
        LastRewindLapTime = null;
        LapStartedAt = arrivalTime - TimeSpan.FromSeconds(currentLapSeconds);
    }

    public void UpdatePrevious(Fh6RawTelemetry raw)
    {
        PreviousLapNumber = raw.LapNumber;
        PreviousCurrentLap = raw.CurrentLap;
        PreviousCurrentRaceTime = raw.CurrentRaceTime;
        PreviousLastLap = raw.LastLap;
    }

    public void ResetSession()
    {
        CompetitionSignalSuspended = false;
        NonCompetitionDrivingSince = null;
        LapArmed = false;
        PreviousLapNumber = null;
        PreviousCurrentLap = null;
        PreviousCurrentRaceTime = null;
        PreviousLastLap = null;
        LastLapValueAtLapStart = 0;
        LastRewindLapTime = null;
        LastCompetitionFrame = null;
        LastCrossingAt = DateTimeOffset.MinValue;
        WaitingForInitialStartLine = false;
        Samples.Clear();
        ProjectionIndex = 0;
        ConfidentProjectionCount = 0;
        LastS = 0;
        SectorStartTimes = [];
        CompletedSectorTimes = [];
        ValidProjectionSamples = 0;
        InvalidProjectionSamples = 0;
        CurrentLapInvalidated = false;
        ObservedSectorIndex = 0;
    }
}
