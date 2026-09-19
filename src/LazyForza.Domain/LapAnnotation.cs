namespace LazyForza.Domain;

/// <summary>User-owned library metadata; never changes timing or telemetry evidence.</summary>
public sealed record LapAnnotation(string? Name = null, string? Notes = null, bool IsFavorite = false, bool IsReference = false)
{
    public const int MaximumNameLength = 80;
    public const int MaximumNotesLength = 2000;

    public static LapAnnotation Normalize(LapAnnotation? value) => value is null ? new() : value with
    {
        Name = Clean(value.Name, MaximumNameLength), Notes = Clean(value.Notes, MaximumNotesLength)
    };

    private static string? Clean(string? value, int limit) => string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(value.Trim().Length, limit)];
}

public static class LapReferenceContext
{
    public static bool Matches(LapSummary left, LapSummary right) =>
        left.TrackId == right.TrackId && left.Direction == right.Direction &&
        left.SectorSchemaVersion > 0 && left.SectorSchemaVersion == right.SectorSchemaVersion &&
        !string.IsNullOrWhiteSpace(left.TrackRevision) && left.TrackRevision == right.TrackRevision &&
        HasVehicleEvidence(left.Vehicle) && HasVehicleEvidence(right.Vehicle) &&
        VehicleTuneCompatibility.AreCompatible(left.Vehicle, right.Vehicle);

    private static bool HasVehicleEvidence(VehicleProfileFingerprint vehicle) => vehicle.CarOrdinal > 0 &&
        vehicle.CarClass >= 0 && vehicle.PerformanceIndex > 0 && vehicle.RoundedMaxRpm > 0 &&
        vehicle.DrivetrainType is >= 0 and <= 2 && vehicle.NumCylinders >= 0 &&
        !string.IsNullOrWhiteSpace(vehicle.GearSlopeSignature) && !string.IsNullOrWhiteSpace(vehicle.CurveSignature);
}
