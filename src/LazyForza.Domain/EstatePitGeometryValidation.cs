namespace LazyForza.Domain;

public static class EstatePitGeometryValidation
{
    public const double MaximumCaptureSegmentMeters = 8;
    public const double MaximumPortableSegmentMeters = 25;

    public static double MaximumSegmentMeters(IReadOnlyList<EstateGatePoint> centerLine)
    {
        ArgumentNullException.ThrowIfNull(centerLine);
        var maximum = 0d;
        for (var index = 1; index < centerLine.Count; index++)
        {
            var previous = centerLine[index - 1];
            var current = centerLine[index];
            var dx = current.X - previous.X;
            var dy = current.Y - previous.Y;
            var dz = current.Z - previous.Z;
            maximum = Math.Max(maximum, Math.Sqrt(dx * dx + dy * dy + dz * dz));
        }
        return maximum;
    }
}
