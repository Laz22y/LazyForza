namespace LazyForza.Analysis;

public sealed record EstateCoordinateMarker(
    string Name,
    double X,
    double Y,
    double Z,
    double YawRadians = 0,
    double SpreadMeters = 0,
    int SampleCount = 1);

public enum EstateCoordinateCompatibility
{
    InsufficientEvidence,
    DirectMatch,
    RigidTransform,
    NeedsReview,
    Incompatible
}

public sealed record EstateCoordinateResidual(
    string Name,
    double DirectErrorMeters,
    double FittedErrorMeters);

public sealed record EstateCoordinateComparison(
    EstateCoordinateCompatibility Compatibility,
    int MatchedMarkerCount,
    double DirectRmsMeters,
    double FittedRmsMeters,
    double MaximumFittedErrorMeters,
    double RotationDegrees,
    double TranslationX,
    double TranslationY,
    double TranslationZ,
    double EstimatedScaleRatio,
    IReadOnlyList<EstateCoordinateResidual> Residuals,
    string Explanation);

/// <summary>
/// Compares two independently captured Estate coordinate sessions. The fitted transform maps
/// candidate X/Z coordinates into the reference frame using one yaw rotation and a translation;
/// Y uses a constant offset. Scale is measured rather than fitted so incompatible coordinate
/// spaces cannot be made to look valid by silently stretching a route.
/// </summary>
public static class EstateCoordinateAnalyzer
{
    public static EstateCoordinateComparison Compare(
        IReadOnlyCollection<EstateCoordinateMarker> reference,
        IReadOnlyCollection<EstateCoordinateMarker> candidate)
    {
        var referenceByName = reference
            .Where(marker => !string.IsNullOrWhiteSpace(marker.Name))
            .GroupBy(marker => marker.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);
        var pairs = candidate
            .Where(marker => !string.IsNullOrWhiteSpace(marker.Name))
            .GroupBy(marker => marker.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .Where(marker => referenceByName.ContainsKey(marker.Name.Trim()))
            .Select(marker => (Reference: referenceByName[marker.Name.Trim()], Candidate: marker))
            .OrderBy(pair => pair.Reference.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (pairs.Length < 2)
        {
            return Insufficient(pairs.Length, "至少需要两个同名标记点；三个以上非共线点才能可靠判断坐标关系。");
        }

        var referenceCentroid = Centroid(pairs.Select(pair => pair.Reference));
        var candidateCentroid = Centroid(pairs.Select(pair => pair.Candidate));
        var cosineTerm = 0d;
        var sineTerm = 0d;
        foreach (var pair in pairs)
        {
            var candidateX = pair.Candidate.X - candidateCentroid.X;
            var candidateZ = pair.Candidate.Z - candidateCentroid.Z;
            var referenceX = pair.Reference.X - referenceCentroid.X;
            var referenceZ = pair.Reference.Z - referenceCentroid.Z;
            cosineTerm += candidateX * referenceX + candidateZ * referenceZ;
            sineTerm += candidateX * referenceZ - candidateZ * referenceX;
        }

        if (Math.Abs(cosineTerm) + Math.Abs(sineTerm) < 1e-6)
        {
            return Insufficient(pairs.Length, "同名标记点之间的水平间距不足，无法拟合旋转；请在赛道不同位置重新采样。");
        }

        var rotation = Math.Atan2(sineTerm, cosineTerm);
        var cos = Math.Cos(rotation);
        var sin = Math.Sin(rotation);
        var translationX = referenceCentroid.X - (cos * candidateCentroid.X - sin * candidateCentroid.Z);
        var translationZ = referenceCentroid.Z - (sin * candidateCentroid.X + cos * candidateCentroid.Z);
        var translationY = referenceCentroid.Y - candidateCentroid.Y;

        var residuals = pairs.Select(pair =>
        {
            var fittedX = cos * pair.Candidate.X - sin * pair.Candidate.Z + translationX;
            var fittedY = pair.Candidate.Y + translationY;
            var fittedZ = sin * pair.Candidate.X + cos * pair.Candidate.Z + translationZ;
            return new EstateCoordinateResidual(
                pair.Reference.Name,
                Distance(pair.Reference.X, pair.Reference.Y, pair.Reference.Z,
                    pair.Candidate.X, pair.Candidate.Y, pair.Candidate.Z),
                Distance(pair.Reference.X, pair.Reference.Y, pair.Reference.Z,
                    fittedX, fittedY, fittedZ));
        }).ToArray();

        var directRms = Rms(residuals.Select(residual => residual.DirectErrorMeters));
        var fittedRms = Rms(residuals.Select(residual => residual.FittedErrorMeters));
        var maximumFitted = residuals.Max(residual => residual.FittedErrorMeters);
        var scaleRatio = EstimateScaleRatio(pairs);
        var scaleError = Math.Abs(scaleRatio - 1);
        var rotationDegrees = NormalizeDegrees(rotation * 180 / Math.PI);
        var translationMagnitude = Math.Sqrt(
            translationX * translationX + translationY * translationY + translationZ * translationZ);

        EstateCoordinateCompatibility compatibility;
        string explanation;
        if (directRms <= 1.5 && residuals.Max(residual => residual.DirectErrorMeters) <= 3 && scaleError <= 0.002)
        {
            compatibility = EstateCoordinateCompatibility.DirectMatch;
            explanation = "两份会话可直接共用同一套地产赛道坐标。";
        }
        else if (fittedRms <= 1.5 && maximumFitted <= 3 && scaleError <= 0.002)
        {
            compatibility = EstateCoordinateCompatibility.RigidTransform;
            explanation = "坐标存在稳定的平移、旋转或高度偏移，可通过一次标定共用赛道。";
        }
        else if (fittedRms <= 3 && maximumFitted <= 6 && scaleError <= 0.01)
        {
            compatibility = EstateCoordinateCompatibility.NeedsReview;
            explanation = "拟合结果接近可用，但误差偏大；建议增加标记点并重复进入地产验证。";
        }
        else
        {
            compatibility = EstateCoordinateCompatibility.Incompatible;
            explanation = "现有样本不能由稳定刚体变换解释，不应直接共享赛道模板。";
        }

        if (compatibility == EstateCoordinateCompatibility.RigidTransform &&
            Math.Abs(rotationDegrees) < 0.05 && translationMagnitude < 0.1)
        {
            compatibility = EstateCoordinateCompatibility.DirectMatch;
            explanation = "两份会话可直接共用同一套地产赛道坐标。";
        }

        return new EstateCoordinateComparison(
            compatibility,
            pairs.Length,
            directRms,
            fittedRms,
            maximumFitted,
            rotationDegrees,
            translationX,
            translationY,
            translationZ,
            scaleRatio,
            residuals,
            explanation);
    }

    private static EstateCoordinateComparison Insufficient(int count, string explanation) => new(
        EstateCoordinateCompatibility.InsufficientEvidence,
        count,
        double.NaN,
        double.NaN,
        double.NaN,
        0,
        0,
        0,
        0,
        double.NaN,
        [],
        explanation);

    private static (double X, double Y, double Z) Centroid(IEnumerable<EstateCoordinateMarker> markers)
    {
        var values = markers.ToArray();
        return (values.Average(marker => marker.X), values.Average(marker => marker.Y), values.Average(marker => marker.Z));
    }

    private static double EstimateScaleRatio(
        IReadOnlyList<(EstateCoordinateMarker Reference, EstateCoordinateMarker Candidate)> pairs)
    {
        var ratios = new List<double>();
        for (var left = 0; left < pairs.Count - 1; left++)
        {
            for (var right = left + 1; right < pairs.Count; right++)
            {
                var referenceDistance = Distance(
                    pairs[left].Reference.X, pairs[left].Reference.Y, pairs[left].Reference.Z,
                    pairs[right].Reference.X, pairs[right].Reference.Y, pairs[right].Reference.Z);
                var candidateDistance = Distance(
                    pairs[left].Candidate.X, pairs[left].Candidate.Y, pairs[left].Candidate.Z,
                    pairs[right].Candidate.X, pairs[right].Candidate.Y, pairs[right].Candidate.Z);
                if (candidateDistance > 1 && referenceDistance > 1)
                    ratios.Add(referenceDistance / candidateDistance);
            }
        }

        if (ratios.Count == 0) return double.NaN;
        ratios.Sort();
        var middle = ratios.Count / 2;
        return ratios.Count % 2 == 0
            ? (ratios[middle - 1] + ratios[middle]) / 2
            : ratios[middle];
    }

    private static double Distance(double ax, double ay, double az, double bx, double by, double bz)
    {
        var dx = ax - bx;
        var dy = ay - by;
        var dz = az - bz;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    private static double Rms(IEnumerable<double> values)
    {
        var samples = values.ToArray();
        return Math.Sqrt(samples.Average(value => value * value));
    }

    private static double NormalizeDegrees(double value)
    {
        while (value > 180) value -= 360;
        while (value <= -180) value += 360;
        return value;
    }
}
