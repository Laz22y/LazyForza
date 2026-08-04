using LazyForza.Domain;

namespace LazyForza.Analysis;

public sealed record EstateLineFitResult(
    bool IsAccepted,
    EstateTimingGate? Gate,
    int SampleCount,
    double FitRmsMeters,
    double TraceOffsetMeters,
    double TraceAngleDifferenceDegrees,
    string Explanation);

public readonly record struct EstateTimedPosition(
    double X,
    double Y,
    double Z,
    double SpeedMetersPerSecond,
    long TimestampMilliseconds);

public readonly record struct EstateGateCrossing(
    long TimestampMilliseconds,
    double Interpolation,
    double X,
    double Y,
    double Z,
    double AlongGateMeters);

public static class EstateTrackAlgorithms
{
    public const double MaximumFitRmsMeters = 0.25;
    public const double MaximumTraceOffsetMeters = 0.30;
    public const double MaximumTraceAngleDifferenceDegrees = 0.50;
    public const double MinimumGateWidthMeters = 3;
    public const double MinimumFinishEndpointMarginMeters = 2;
    public const double MinimumDirectionCaptureSideDistanceMeters = 5;

    public static EstateLineFitResult FitStartFinishGate(
        IReadOnlyList<EstateGatePoint> firstTrace,
        IReadOnlyList<EstateGatePoint> secondTrace)
    {
        if (firstTrace.Count < 10 || secondTrace.Count < 10)
            return Rejected(firstTrace.Count + secondTrace.Count, "每次描摹至少需要 10 个有效坐标样本。");

        var first = FitLine(firstTrace);
        var second = FitLine(secondTrace);
        if (first.SpanMeters < MinimumGateWidthMeters || second.SpanMeters < MinimumGateWidthMeters)
            return Rejected(firstTrace.Count + secondTrace.Count, "描摹宽度不足 3 米，请沿棋盘格横线覆盖更多赛道路面。");

        var directionDot = first.TangentX * second.TangentX + first.TangentZ * second.TangentZ;
        var secondTangentX = directionDot < 0 ? -second.TangentX : second.TangentX;
        var secondTangentZ = directionDot < 0 ? -second.TangentZ : second.TangentZ;
        var angleDifference = Math.Acos(Math.Clamp(
            first.TangentX * secondTangentX + first.TangentZ * secondTangentZ,
            -1,
            1)) * 180 / Math.PI;

        var combined = FitLine(firstTrace.Concat(secondTrace).ToArray());
        var normalX = -combined.TangentZ;
        var normalZ = combined.TangentX;
        var traceOffset = Math.Abs(
            (first.CenterX - second.CenterX) * normalX +
            (first.CenterZ - second.CenterZ) * normalZ);

        var projections = firstTrace.Concat(secondTrace)
            .Select(point => (point.X - combined.CenterX) * combined.TangentX +
                             (point.Z - combined.CenterZ) * combined.TangentZ)
            .Order()
            .ToArray();
        var lower = Percentile(projections, 0.02);
        var upper = Percentile(projections, 0.98);
        var width = upper - lower;
        if (width < MinimumGateWidthMeters)
            return Rejected(firstTrace.Count + secondTrace.Count, "剔除转向样本后，终点门有效宽度不足 3 米。");

        var points = firstTrace.Concat(secondTrace).ToArray();
        var meanProjection = points.Average(point =>
            (point.X - combined.CenterX) * combined.TangentX +
            (point.Z - combined.CenterZ) * combined.TangentZ);
        var meanY = points.Average(point => point.Y);
        var yNumerator = 0d;
        var yDenominator = 0d;
        foreach (var point in points)
        {
            var projection = (point.X - combined.CenterX) * combined.TangentX +
                             (point.Z - combined.CenterZ) * combined.TangentZ;
            yNumerator += (projection - meanProjection) * (point.Y - meanY);
            yDenominator += (projection - meanProjection) * (projection - meanProjection);
        }
        var ySlope = yDenominator > 1e-6 ? yNumerator / yDenominator : 0;
        double YAt(double projection) => meanY + ySlope * (projection - meanProjection);

        var left = new EstateGatePoint(
            combined.CenterX + lower * combined.TangentX,
            YAt(lower),
            combined.CenterZ + lower * combined.TangentZ);
        var right = new EstateGatePoint(
            combined.CenterX + upper * combined.TangentX,
            YAt(upper),
            combined.CenterZ + upper * combined.TangentZ);
        var gate = new EstateTimingGate(
            left,
            right,
            0,
            0,
            combined.RmsMeters,
            traceOffset,
            angleDifference,
            EndpointMarginMeters: MinimumFinishEndpointMarginMeters);

        var accepted = combined.RmsMeters <= MaximumFitRmsMeters &&
                       traceOffset <= MaximumTraceOffsetMeters &&
                       angleDifference <= MaximumTraceAngleDifferenceDegrees;
        var explanation = accepted
            ? $"终点门拟合通过：宽 {width:0.00} m，RMS {combined.RmsMeters:0.00} m。"
            : $"拟合未通过：RMS {combined.RmsMeters:0.00} m，双向偏移 {traceOffset:0.00} m，角度差 {angleDifference:0.00}°。";
        return new EstateLineFitResult(
            accepted,
            gate,
            points.Length,
            combined.RmsMeters,
            traceOffset,
            angleDifference,
            explanation);
    }

    public static EstateTimingGate WithForwardDirection(
        EstateTimingGate gate,
        EstateGatePoint approach,
        EstateGatePoint departure)
    {
        var motionX = departure.X - approach.X;
        var motionZ = departure.Z - approach.Z;
        var motionMagnitude = Math.Sqrt(motionX * motionX + motionZ * motionZ);
        if (motionMagnitude < 2)
            throw new ArgumentException("比赛方向采样至少需要 2 米位移。", nameof(departure));

        motionX /= motionMagnitude;
        motionZ /= motionMagnitude;
        var tangent = GateTangent(gate);
        var normalX = -tangent.Z;
        var normalZ = tangent.X;
        if (normalX * motionX + normalZ * motionZ < 0)
        {
            normalX = -normalX;
            normalZ = -normalZ;
        }
        return gate with { ForwardX = normalX, ForwardZ = normalZ };
    }

    public static bool TryApplyForwardDirection(
        EstateTimingGate gate,
        IReadOnlyList<EstateGatePoint> trace,
        out EstateTimingGate directedGate,
        out string explanation)
    {
        directedGate = gate;
        if (trace.Count < 2)
        {
            explanation = "比赛方向样本不足，请从终点线前方重新采集。";
            return false;
        }

        try
        {
            directedGate = WithForwardDirection(gate, trace[0], trace[^1]);
        }
        catch (ArgumentException)
        {
            explanation = "比赛方向采样位移不足，请直穿终点线后再停止。";
            return false;
        }

        var approachDistance = SignedDistanceToGate(directedGate, trace[0]);
        var departureDistance = SignedDistanceToGate(directedGate, trace[^1]);
        if (approachDistance > -MinimumDirectionCaptureSideDistanceMeters ||
            departureDistance < MinimumDirectionCaptureSideDistanceMeters)
        {
            explanation =
                $"比赛方向采样必须从终点线前至少 {MinimumDirectionCaptureSideDistanceMeters:0} 米开始，" +
                $"并在线后至少 {MinimumDirectionCaptureSideDistanceMeters:0} 米结束；不要从终点线上直接开始采样。";
            return false;
        }

        for (var index = 1; index < trace.Count; index++)
        {
            var previous = new EstateTimedPosition(
                trace[index - 1].X,
                trace[index - 1].Y,
                trace[index - 1].Z,
                5,
                index - 1);
            var current = new EstateTimedPosition(
                trace[index].X,
                trace[index].Y,
                trace[index].Z,
                5,
                index);
            if (TryDetectForwardCrossing(directedGate, previous, current, out _, sideDeadbandMeters: 0))
            {
                explanation = "比赛方向采样通过。";
                return true;
            }
        }

        explanation = "方向轨迹没有穿过已拟合终点线，请从线前直穿至线后。";
        return false;
    }

    public static bool TryDetectForwardCrossing(
        EstateTimingGate gate,
        EstateTimedPosition previous,
        EstateTimedPosition current,
        out EstateGateCrossing crossing,
        double minimumSpeedMetersPerSecond = 1.5,
        double sideDeadbandMeters = 0.01)
    {
        crossing = default;
        if (!gate.HasDirection || current.TimestampMilliseconds <= previous.TimestampMilliseconds ||
            Math.Max(previous.SpeedMetersPerSecond, current.SpeedMetersPerSecond) < minimumSpeedMetersPerSecond)
            return false;

        var forwardMagnitude = Math.Sqrt(gate.ForwardX * gate.ForwardX + gate.ForwardZ * gate.ForwardZ);
        var forwardX = gate.ForwardX / forwardMagnitude;
        var forwardZ = gate.ForwardZ / forwardMagnitude;
        var previousSide = (previous.X - gate.Left.X) * forwardX + (previous.Z - gate.Left.Z) * forwardZ;
        var currentSide = (current.X - gate.Left.X) * forwardX + (current.Z - gate.Left.Z) * forwardZ;
        // Do not require both samples to clear the deadband. At real game frame
        // rates a sample can land inside it; rejecting that segment loses the
        // crossing forever because the following sample is already past the line.
        if (currentSide - previousSide <= 1e-6 ||
            previousSide > sideDeadbandMeters ||
            currentSide < -sideDeadbandMeters)
            return false;

        var interpolation = -previousSide / (currentSide - previousSide);
        if (interpolation is < 0 or > 1) return false;
        var x = Lerp(previous.X, current.X, interpolation);
        var y = Lerp(previous.Y, current.Y, interpolation);
        var z = Lerp(previous.Z, current.Z, interpolation);
        var tangent = GateTangent(gate);
        var width = GateWidth(gate);
        var along = (x - gate.Left.X) * tangent.X + (z - gate.Left.Z) * tangent.Z;
        if (along < -gate.EndpointMarginMeters || along > width + gate.EndpointMarginMeters) return false;
        var expectedY = Lerp(gate.Left.Y, gate.Right.Y, Math.Clamp(along / width, 0, 1));
        if (Math.Abs(y - expectedY) > gate.HeightToleranceMeters) return false;

        crossing = new EstateGateCrossing(
            (long)Math.Round(Lerp(previous.TimestampMilliseconds, current.TimestampMilliseconds, interpolation)),
            interpolation,
            x,
            y,
            z,
            along);
        return true;
    }

    public static bool TryCreatePitStartFinishGate(
        EstateTimingGate referenceGate,
        IReadOnlyList<EstateGatePoint> pitCenterLine,
        double laneHalfWidthMeters,
        out EstateTimingGate pitGate)
    {
        pitGate = null!;
        if (!referenceGate.HasDirection || pitCenterLine.Count < 2) return false;

        var referenceMagnitude = Math.Sqrt(
            referenceGate.ForwardX * referenceGate.ForwardX +
            referenceGate.ForwardZ * referenceGate.ForwardZ);
        var referenceForwardX = referenceGate.ForwardX / referenceMagnitude;
        var referenceForwardZ = referenceGate.ForwardZ / referenceMagnitude;
        var halfWidth = Math.Clamp(laneHalfWidthMeters, 1, 20);

        for (var index = 1; index < pitCenterLine.Count; index++)
        {
            var previous = pitCenterLine[index - 1];
            var current = pitCenterLine[index];
            var previousSide = SignedDistanceToGate(referenceGate, previous);
            var currentSide = SignedDistanceToGate(referenceGate, current);
            var sideDelta = currentSide - previousSide;
            if (sideDelta <= 0.05 || previousSide > 0.25 || currentSide < -0.25) continue;

            var segmentX = current.X - previous.X;
            var segmentZ = current.Z - previous.Z;
            var segmentLength = Math.Sqrt(segmentX * segmentX + segmentZ * segmentZ);
            if (segmentLength < 0.25) continue;
            var forwardX = segmentX / segmentLength;
            var forwardZ = segmentZ / segmentLength;
            if (forwardX * referenceForwardX + forwardZ * referenceForwardZ < 0.2) continue;

            var interpolation = Math.Clamp(-previousSide / sideDelta, 0, 1);
            var center = new EstateGatePoint(
                Lerp(previous.X, current.X, interpolation),
                Lerp(previous.Y, current.Y, interpolation),
                Lerp(previous.Z, current.Z, interpolation));
            var perpendicularX = -forwardZ * halfWidth;
            var perpendicularZ = forwardX * halfWidth;
            pitGate = new EstateTimingGate(
                new EstateGatePoint(center.X + perpendicularX, center.Y, center.Z + perpendicularZ),
                new EstateGatePoint(center.X - perpendicularX, center.Y, center.Z - perpendicularZ),
                forwardX,
                forwardZ,
                0,
                0,
                0,
                referenceGate.HeightToleranceMeters,
                Math.Max(0.75, referenceGate.EndpointMarginMeters));
            return true;
        }

        return false;
    }

    private static double SignedDistanceToGate(EstateTimingGate gate, EstateGatePoint point)
    {
        var magnitude = Math.Sqrt(gate.ForwardX * gate.ForwardX + gate.ForwardZ * gate.ForwardZ);
        if (magnitude <= 1e-6) return 0;
        var forwardX = gate.ForwardX / magnitude;
        var forwardZ = gate.ForwardZ / magnitude;
        return (point.X - gate.Left.X) * forwardX + (point.Z - gate.Left.Z) * forwardZ;
    }

    public static IReadOnlyList<EstateCheckpoint> CreateCheckpoints(
        TrackTemplate track,
        int? requestedCount = null)
    {
        if (track.Points.Count < 4 || track.LengthMeters <= 0) return [];
        var count = requestedCount ?? Math.Clamp((int)Math.Round(track.LengthMeters / 250), 6, 20);
        var checkpoints = new List<EstateCheckpoint>(count);
        for (var index = 0; index < count; index++)
        {
            var progress = track.LengthMeters * (index + 1d) / (count + 1d);
            var point = PointAt(track.Points, progress);
            var tangentMagnitude = Math.Sqrt(point.TangentX * point.TangentX + point.TangentZ * point.TangentZ);
            var forwardX = tangentMagnitude > 1e-6 ? point.TangentX / tangentMagnitude : 1;
            var forwardZ = tangentMagnitude > 1e-6 ? point.TangentZ / tangentMagnitude : 0;
            var lateralX = -forwardZ;
            var lateralZ = forwardX;
            var halfWidth = Math.Max(12, track.MatchingToleranceMeters);
            var gate = new EstateTimingGate(
                new EstateGatePoint(point.X - lateralX * halfWidth, point.Y, point.Z - lateralZ * halfWidth),
                new EstateGatePoint(point.X + lateralX * halfWidth, point.Y, point.Z + lateralZ * halfWidth),
                forwardX,
                forwardZ,
                0,
                0,
                0,
                HeightToleranceMeters: Math.Max(3, track.MatchingToleranceMeters * 0.4),
                EndpointMarginMeters: 1);
            checkpoints.Add(new EstateCheckpoint(index, gate, progress));
        }
        return checkpoints;
    }

    public static double GateWidth(EstateTimingGate gate)
    {
        var dx = gate.Right.X - gate.Left.X;
        var dz = gate.Right.Z - gate.Left.Z;
        return Math.Sqrt(dx * dx + dz * dz);
    }

    private static (double X, double Z) GateTangent(EstateTimingGate gate)
    {
        var width = GateWidth(gate);
        return width <= 1e-6
            ? (1, 0)
            : ((gate.Right.X - gate.Left.X) / width, (gate.Right.Z - gate.Left.Z) / width);
    }

    private static TrackPoint PointAt(IReadOnlyList<TrackPoint> points, double progress)
    {
        var upper = 1;
        while (upper < points.Count - 1 && points[upper].S < progress) upper++;
        var lower = upper - 1;
        var distance = points[upper].S - points[lower].S;
        var amount = distance <= 1e-6 ? 0 : (progress - points[lower].S) / distance;
        return new TrackPoint(
            Lerp(points[lower].X, points[upper].X, amount),
            Lerp(points[lower].Y, points[upper].Y, amount),
            Lerp(points[lower].Z, points[upper].Z, amount),
            progress,
            Lerp(points[lower].TangentX, points[upper].TangentX, amount),
            Lerp(points[lower].TangentZ, points[upper].TangentZ, amount));
    }

    private static LineFit FitLine(IReadOnlyList<EstateGatePoint> points)
    {
        var centerX = points.Average(point => point.X);
        var centerZ = points.Average(point => point.Z);
        var xx = 0d;
        var xz = 0d;
        var zz = 0d;
        foreach (var point in points)
        {
            var x = point.X - centerX;
            var z = point.Z - centerZ;
            xx += x * x;
            xz += x * z;
            zz += z * z;
        }
        var angle = 0.5 * Math.Atan2(2 * xz, xx - zz);
        var tangentX = Math.Cos(angle);
        var tangentZ = Math.Sin(angle);
        var normalX = -tangentZ;
        var normalZ = tangentX;
        var squaredResidual = points.Sum(point =>
        {
            var residual = (point.X - centerX) * normalX + (point.Z - centerZ) * normalZ;
            return residual * residual;
        });
        var projections = points.Select(point =>
            (point.X - centerX) * tangentX + (point.Z - centerZ) * tangentZ).ToArray();
        return new LineFit(
            centerX,
            centerZ,
            tangentX,
            tangentZ,
            Math.Sqrt(squaredResidual / points.Count),
            projections.Max() - projections.Min());
    }

    private static double Percentile(IReadOnlyList<double> sorted, double percentile)
    {
        if (sorted.Count == 1) return sorted[0];
        var position = Math.Clamp(percentile, 0, 1) * (sorted.Count - 1);
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        return Lerp(sorted[lower], sorted[upper], position - lower);
    }

    private static EstateLineFitResult Rejected(int samples, string explanation) =>
        new(false, null, samples, double.NaN, double.NaN, double.NaN, explanation);

    private static double Lerp(double left, double right, double amount) => left + (right - left) * amount;

    private sealed record LineFit(
        double CenterX,
        double CenterZ,
        double TangentX,
        double TangentZ,
        double RmsMeters,
        double SpanMeters);
}

public sealed class EstateTimestampUnwrapper
{
    private uint? previous;
    private long epoch;

    public long Unwrap(uint value)
    {
        if (previous is uint last && value < last && last - value > int.MaxValue)
            epoch += 1L << 32;
        previous = value;
        return epoch + value;
    }

    public void Reset()
    {
        previous = null;
        epoch = 0;
    }
}
