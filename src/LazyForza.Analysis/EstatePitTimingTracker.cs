using LazyForza.Domain;

namespace LazyForza.Analysis;

/// <summary>Tracks one legal pit passage, independently of lap resets and service completion.</summary>
public sealed class EstatePitTimingTracker
{
    private EstatePitDefinition? pit;
    private IReadOnlyList<EstateTimingGate> serviceGates = [];
    private RouteProjection? previousRoute;
    private double entryProgress, exitProgress;
    private bool finishConsumed;

    public bool IsActive { get; private set; }
    public bool WasActive { get; private set; }
    public bool Entered { get; private set; }
    public bool Exited { get; private set; }
    public bool FinishConsumed => WasActive && finishConsumed;

    public void Configure(EstatePitDefinition? definition)
    {
        pit = definition;
        serviceGates = definition?.StartFinishGate is { } gate
            ? ServiceTimingGates(definition, gate) : [];
        entryProgress = definition is null ? 0 : GateProgress(definition.EntryGate);
        exitProgress = definition is null ? 0 : GateProgress(definition.ExitGate);
        Reset();
    }

    public void Reset()
    {
        IsActive = WasActive = Entered = Exited = finishConsumed = false;
        BreakContinuity();
    }

    // A pause invalidates the connecting segment, not the active visit or its consumed finish.
    public void BreakContinuity() => previousRoute = null;

    public void AcceptFinishCrossing()
    {
        if (WasActive) finishConsumed = true;
    }

    public bool Observe(EstateTimedPosition previous, EstateTimedPosition current, out EstateGateCrossing crossing)
    {
        crossing = default;
        Entered = Exited = false;
        if (pit is null) { Reset(); return false; }
        var prior = previousRoute ?? Project(previous.X, previous.Y, previous.Z);
        var next = Project(current.X, current.Y, current.Z);
        previousRoute = next;
        var corridor = Math.Clamp(pit.LaneHalfWidthMeters, 1, 20) * 1.35 + .75;
        var inService = IsInService(pit, current);
        var enteredByProgress = prior.Distance <= corridor && next.Distance <= corridor &&
            prior.Progress < entryProgress - .25 && next.Progress >= entryProgress - .25;
        var recoveredInside = next.Distance <= corridor && next.Progress > entryProgress + .75 &&
            next.Progress < Math.Min(exitProgress, next.Length) - .75;
        if (!IsActive && (enteredByProgress || recoveredInside || inService ||
            EstateTrackAlgorithms.TryDetectForwardCrossing(pit.EntryGate, previous, current, out _)))
        {
            IsActive = Entered = true;
            finishConsumed = false;
        }
        WasActive = IsActive;
        var found = false;
        if (WasActive && !finishConsumed)
        {
            if (pit.StartFinishGate is { } gate)
                found = EstateTrackAlgorithms.TryDetectForwardCrossing(gate, previous, current, out crossing,
                    minimumSpeedMetersPerSecond: 0);
            foreach (var serviceGate in serviceGates)
            {
                if (EstateTrackAlgorithms.TryDetectForwardCrossing(serviceGate, previous, current, out var candidate,
                    minimumSpeedMetersPerSecond: 0) && (!found || candidate.TimestampMilliseconds < crossing.TimestampMilliseconds))
                { crossing = candidate; found = true; }
            }
        }
        var endProgress = Math.Max(exitProgress, next.Length - .75);
        if (IsActive && !inService && next.Distance <= corridor && next.Progress >= endProgress)
        { IsActive = false; Exited = true; }
        return found;
    }

    private double GateProgress(EstateTimingGate gate) => Project(
        (gate.Left.X + gate.Right.X) / 2, (gate.Left.Y + gate.Right.Y) / 2,
        (gate.Left.Z + gate.Right.Z) / 2).Progress;

    private RouteProjection Project(double x, double y, double z)
    {
        var bestDistance = double.PositiveInfinity;
        double bestProgress = 0, progress = 0;
        for (var i = 0; i < pit!.CenterLine.Count - 1; i++)
        {
            var a = pit.CenterLine[i]; var b = pit.CenterLine[i + 1];
            var dx = b.X - a.X; var dy = b.Y - a.Y; var dz = b.Z - a.Z;
            var squared = dx * dx + dy * dy + dz * dz;
            if (squared < .0001) continue;
            var length = Math.Sqrt(squared);
            var t = Math.Clamp(((x - a.X) * dx + (y - a.Y) * dy + (z - a.Z) * dz) / squared, 0, 1);
            var distance = Math.Sqrt(Math.Pow(x - a.X - dx * t, 2) +
                Math.Pow(y - a.Y - dy * t, 2) + Math.Pow(z - a.Z - dz * t, 2));
            if (distance < bestDistance) { bestDistance = distance; bestProgress = progress + length * t; }
            progress += length;
        }
        return new(bestDistance, bestProgress, progress);
    }

    private static bool IsInService(EstatePitDefinition definition, EstateTimedPosition position)
    {
        if (definition.ServiceZoneBoundary is { Count: >= 3 } boundary)
            return Math.Abs(position.Y - boundary.Average(p => p.Y)) <= 3 && Contains(boundary, position.X, position.Z);
        return Math.Abs(position.Y - definition.ServiceCenter.Y) <= 3 &&
            double.Hypot(position.X - definition.ServiceCenter.X, position.Z - definition.ServiceCenter.Z) <= definition.ServiceRadiusMeters;
    }

    private static bool Contains(IReadOnlyList<EstateGatePoint> polygon, double x, double z)
    {
        var inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            var a = polygon[i]; var b = polygon[j];
            if ((a.Z > z) != (b.Z > z) && x < (b.X - a.X) * (z - a.Z) / (b.Z - a.Z) + a.X)
                inside = !inside;
        }
        return inside;
    }

    // Intersect the timing plane with the actual service polygon. Separate spans
    // preserve concave gaps; widening the lane gate would count the space between routes.
    private static IReadOnlyList<EstateTimingGate> ServiceTimingGates(EstatePitDefinition definition, EstateTimingGate gate)
    {
        var magnitude = double.Hypot(gate.ForwardX, gate.ForwardZ);
        if (!gate.HasDirection || magnitude <= 0) return [];
        var nx = gate.ForwardX / magnitude; var nz = gate.ForwardZ / magnitude;
        double Side(EstateGatePoint p) => (p.X - gate.Left.X) * nx + (p.Z - gate.Left.Z) * nz;
        double Along(EstateGatePoint p) => -(p.X - gate.Left.X) * nz + (p.Z - gate.Left.Z) * nx;
        var intersections = new List<EstateGatePoint>();
        if (definition.ServiceZoneBoundary is { Count: >= 3 } polygon)
        {
            for (var i = 0; i < polygon.Count; i++)
            {
                var a = polygon[i]; var b = polygon[(i + 1) % polygon.Count];
                var sa = Side(a); var sb = Side(b);
                if (Math.Abs(sa) < 1e-8) intersections.Add(a);
                if (sa * sb < 0)
                {
                    var t = sa / (sa - sb);
                    intersections.Add(new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t));
                }
            }
            var ordered = intersections.OrderBy(Along).ToArray();
            var result = new List<EstateTimingGate>();
            for (var i = 1; i < ordered.Length; i++)
            {
                var a = ordered[i - 1]; var b = ordered[i];
                if (Along(b) - Along(a) > 1e-6 && Contains(polygon, (a.X + b.X) / 2, (a.Z + b.Z) / 2))
                    result.Add(gate with { Left = a, Right = b, EndpointMarginMeters = 0 });
            }
            return result;
        }
        var side = Side(definition.ServiceCenter);
        var radius = definition.ServiceRadiusMeters;
        if (radius <= 0 || Math.Abs(side) >= radius) return [];
        var half = Math.Sqrt(radius * radius - side * side);
        var center = definition.ServiceCenter;
        return [gate with
        {
            Left = new(center.X - nx * side + nz * half, center.Y, center.Z - nz * side - nx * half),
            Right = new(center.X - nx * side - nz * half, center.Y, center.Z - nz * side + nx * half),
            EndpointMarginMeters = 0
        }];
    }

    private readonly record struct RouteProjection(double Distance, double Progress, double Length);
}
