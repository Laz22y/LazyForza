using LazyForza.Analysis;
using LazyForza.Domain;

namespace LazyForza.Analysis.Tests;

[TestClass]
public sealed class TrackAnalysisTests
{
    [TestMethod]
    public void ChartProgressHitFindsTheNearestSavedSample()
    {
        var samples = new[]
        {
            Sample(0),
            Sample(25),
            Sample(70),
            Sample(120)
        };

        Assert.AreEqual(0, ChartInteractionAlgorithms.FindNearestProgressSample(samples, -10));
        Assert.AreEqual(1, ChartInteractionAlgorithms.FindNearestProgressSample(samples, 32));
        Assert.AreEqual(2, ChartInteractionAlgorithms.FindNearestProgressSample(samples, 68));
        Assert.AreEqual(3, ChartInteractionAlgorithms.FindNearestProgressSample(samples, 500));
    }

    [TestMethod]
    public void SpeedEnvelopeDownsamplingPreservesEndpointsAndShortLivedExtremes()
    {
        var samples = Enumerable.Range(0, 1_000)
            .Select(index => new LapSample(
                index,
                index * 0.1,
                index switch
                {
                    421 => 120,
                    733 => 2,
                    _ => 40 + Math.Sin(index * 0.03) * 5
                },
                5_000,
                4,
                1,
                0,
                0,
                index,
                0,
                index))
            .ToArray();

        var reduced = ChartInteractionAlgorithms.DownsampleSpeedEnvelope(samples, 64);

        Assert.IsTrue(reduced.Count <= 64);
        Assert.AreSame(samples[0], reduced[0]);
        Assert.AreSame(samples[^1], reduced[^1]);
        Assert.IsTrue(reduced.Any(sample => sample.SpeedMps == 120), "A short speed peak must remain visible.");
        Assert.IsTrue(reduced.Any(sample => sample.SpeedMps == 2), "A short braking minimum must remain visible.");
        Assert.IsTrue(reduced.Zip(reduced.Skip(1)).All(pair => pair.First.S <= pair.Second.S));
    }

    [TestMethod]
    public void PointToPointVisualsPreserveOpenRouteEndpointsAndProgress()
    {
        var first = new[]
        {
            SampleAt(0, 10, 20),
            SampleAt(500, 250, 80),
            SampleAt(1_000, 510, 140)
        };
        var second = new[]
        {
            SampleAt(0, 14, 18),
            SampleAt(500, 252, 82),
            SampleAt(1_000, 506, 144)
        };

        var endpoints = ChartInteractionAlgorithms.SummarizeTrackEndpoints([first, second]);

        Assert.IsNotNull(endpoints);
        Assert.AreEqual(12, endpoints.Value.StartX, 0.000_001);
        Assert.AreEqual(19, endpoints.Value.StartZ, 0.000_001);
        Assert.AreEqual(508, endpoints.Value.FinishX, 0.000_001);
        Assert.AreEqual(142, endpoints.Value.FinishZ, 0.000_001);
        Assert.AreEqual(0, ChartInteractionAlgorithms.FindNearestProgressSample(first, 0));
        Assert.AreEqual(2, ChartInteractionAlgorithms.FindNearestProgressSample(first, 1_000));
        Assert.AreEqual(
            1_200,
            ChartInteractionAlgorithms.ResolveProgressExtent([first, second], 1_200),
            0.000_001,
            "The speed chart must preserve the complete open-route extent when the final UDP sample is early.");

        var templateEndpoints = ChartInteractionAlgorithms.SummarizeTrackEndpoints(
            new[]
            {
                new TrackPoint(8, 0, 16, 0, 0, 0),
                new TrackPoint(520, 0, 150, 1_200, 0, 0)
            });
        Assert.IsNotNull(templateEndpoints);
        Assert.AreEqual(8, templateEndpoints.Value.StartX, 0.000_001);
        Assert.AreEqual(16, templateEndpoints.Value.StartZ, 0.000_001);
        Assert.AreEqual(520, templateEndpoints.Value.FinishX, 0.000_001);
        Assert.AreEqual(150, templateEndpoints.Value.FinishZ, 0.000_001);
    }

    [TestMethod]
    public void TrackEndpointSummaryIgnoresEmptyVisualSeries()
    {
        Assert.IsNull(ChartInteractionAlgorithms.SummarizeTrackEndpoints(
            [Array.Empty<LapSample>(), [SampleAt(0, 1, 2)]]));
    }

    [TestMethod]
    public void ChartZoomKeepsTheWorldPointUnderTheCursor()
    {
        var current = new ChartViewport(2, 35, -18);
        const double centerX = 300;
        const double centerY = 180;
        const double cursorX = 420;
        const double cursorY = 95;
        var worldX = (cursorX - centerX - current.OffsetX) / current.Zoom;
        var worldY = (cursorY - centerY - current.OffsetY) / current.Zoom;

        var zoomed = ChartInteractionAlgorithms.ZoomAroundCursor(
            current,
            cursorX,
            cursorY,
            centerX,
            centerY,
            120);

        Assert.IsTrue(zoomed.Zoom > current.Zoom);
        Assert.AreEqual(cursorX, centerX + worldX * zoomed.Zoom + zoomed.OffsetX, 0.000_001);
        Assert.AreEqual(cursorY, centerY + worldY * zoomed.Zoom + zoomed.OffsetY, 0.000_001);

        var reset = ChartInteractionAlgorithms.ZoomAroundCursor(
            zoomed,
            cursorX,
            cursorY,
            centerX,
            centerY,
            -1_200);
        Assert.AreEqual(1, reset.Zoom, 0.000_001);
        Assert.AreEqual(0, reset.OffsetX, 0.000_001);
        Assert.AreEqual(0, reset.OffsetY, 0.000_001);
    }

    [TestMethod]
    public void ScreenHitGridFindsTheNearestPointOnALongRoute()
    {
        var points = Enumerable.Range(0, 20_000)
            .Select(index => new ScreenHitPoint(
                index % 4,
                index,
                index * 0.25,
                240 + Math.Sin(index * 0.01) * 90))
            .ToArray();
        var grid = new ScreenHitGrid(points);
        var target = points[13_579];

        var found = grid.TryFindNearest(
            target.X + 0.35,
            target.Y - 0.25,
            11,
            out var nearest);

        Assert.IsTrue(found);
        Assert.AreEqual(target.SampleIndex, nearest.SampleIndex);
        Assert.IsFalse(grid.TryFindNearest(-1_000, -1_000, 11, out _));
    }

    [TestMethod]
    public void ScreenHitGridHandlesNegativeCoordinatesAndCellEdges()
    {
        var points = new[]
        {
            new ScreenHitPoint(0, 0, -24.1, -0.1),
            new ScreenHitPoint(0, 1, -0.2, -23.9),
            new ScreenHitPoint(1, 2, 24.1, 24.1)
        };
        var grid = new ScreenHitGrid(points, 24);

        Assert.IsTrue(grid.TryFindNearest(-0.1, -23.7, 2, out var nearest));
        Assert.AreEqual(1, nearest.SampleIndex);
        Assert.IsTrue(grid.TryFindNearest(24, 24, 2, out nearest));
        Assert.AreEqual(2, nearest.SampleIndex);
    }

    private static LapSample Sample(double progress) =>
        new(progress, progress / 10, 20, 5_000, 3, 0.5, 0, 0, progress, 0, progress);

    private static LapSample SampleAt(double progress, double x, double z) =>
        new(progress, progress / 10, 20, 5_000, 3, 0.5, 0, 0, x, 0, z);

    [TestMethod]
    public void ResamplingIsDeterministicAndDistanceMonotonic()
    {
        var input = Enumerable.Range(0, 101).Select(index =>
        {
            var angle = index / 100d * Math.PI * 2;
            return new TrackPoint(100 * Math.Cos(angle), 2 * Math.Sin(angle * 2), 70 * Math.Sin(angle), 0, 0, 0);
        }).ToArray();
        var first = TrackAlgorithms.Resample(input, 5);
        var second = TrackAlgorithms.Resample(input, 5);
        Assert.AreEqual(first.Count, second.Count);
        Assert.IsTrue(first.Zip(first.Skip(1)).All(pair => pair.First.S < pair.Second.S));
        CollectionAssert.AreEqual(first.ToArray(), second.ToArray());
    }

    [TestMethod]
    public void ConstrainedProjectionUsesProgressAndElevationAtCrossing()
    {
        var route = new[]
        {
            new TrackPoint(-20, 0, 0, 0, 1, 0), new TrackPoint(0, 0, 0, 20, 1, 0), new TrackPoint(20, 0, 0, 40, 1, 0),
            new TrackPoint(20, 8, 20, 60, -1, 0), new TrackPoint(0, 8, 0, 80, -1, 0), new TrackPoint(-20, 8, 0, 100, -1, 0)
        };
        var lower = TrackAlgorithms.ProjectConstrained(route, 1, 0.2, 0, 0, 0, 2);
        var upper = TrackAlgorithms.ProjectConstrained(route, 1, 7.9, 0, 4, 1, 1);
        Assert.IsTrue(lower.S < 50);
        Assert.IsTrue(upper.S > 60);
        Assert.IsTrue(upper.ElevationErrorMeters < 1);
    }

    [TestMethod]
    public void SpatialIndexNarrowsLongRoutesAndKeepsStackedRoadsDistinct()
    {
        var longRoute = Enumerable.Range(0, 1_001)
            .Select(index => new TrackPoint(index * 5, 0, 0, index * 5, 1, 0))
            .ToArray();
        var longIndex = new TrackSpatialIndex(longRoute, 50);
        var nearbySegments = longIndex.QuerySegmentIndices(2_500, 0, 20);
        Assert.IsTrue(nearbySegments.Count < 30,
            $"A local grid query should not scan all {longIndex.IndexedSegmentCount} route segments.");
        Assert.IsTrue(nearbySegments.Contains(499) || nearbySegments.Contains(500));

        var stackedRoute = new[]
        {
            new TrackPoint(-20, 0, 0, 0, 1, 0),
            new TrackPoint(20, 0, 0, 40, 1, 0),
            new TrackPoint(20, 20, 20, 60, -1, 0),
            new TrackPoint(-20, 20, 0, 100, -1, 0)
        };
        var stackedIndex = new TrackSpatialIndex(stackedRoute, 40);
        var lower = stackedIndex.ProjectNearest(0, 0.2, 0, 10);
        var upper = stackedIndex.ProjectNearest(0, 19.8, 0, 10);

        Assert.AreEqual(0, lower.SegmentIndex);
        Assert.AreEqual(2, upper.SegmentIndex);
        Assert.IsTrue(lower.ElevationErrorMeters < 1);
        Assert.IsTrue(upper.ElevationErrorMeters < 1);
    }

    [TestMethod]
    public void SectorGenerationIsVersionedDeterministicAndBounded()
    {
        var points = Enumerable.Range(0, 281).Select(index => new TrackPoint(index * 5, 0, 0, index * 5, 1, 0)).ToArray();
        var track = new TrackTemplate(Guid.NewGuid(), "test", 1, "user_learned", null, points, 1400, 0, 0, 0, 1400, 0, 0, 15, 1, 1, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        var first = TrackAlgorithms.CreateSectors(track);
        var second = TrackAlgorithms.CreateSectors(track);
        Assert.AreEqual(4, first.Count);
        CollectionAssert.AreEqual(first.ToArray(), second.ToArray());
        Assert.IsTrue(first.All(sector => sector.SectorSchemaVersion == 2 && sector.EndS > sector.StartS));
        var userDefined = TrackAlgorithms.CreateSectors(track, requestedCount: 7);
        Assert.HasCount(7, userDefined);
        Assert.AreEqual(0, userDefined[0].StartS, 0.001);
        Assert.AreEqual(track.LengthMeters, userDefined[^1].EndS, 0.001);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            TrackAlgorithms.CreateSectors(track, requestedCount: TrackAlgorithms.MaximumSectorCount + 1));
    }

    [TestMethod]
    public void LayoutInferenceSeparatesClosedCircuitFromPointToPointRoute()
    {
        var circuit = new[]
        {
            new TrackPoint(0, 0, 0, 0, 0, 0),
            new TrackPoint(100, 0, 0, 0, 0, 0),
            new TrackPoint(100, 0, 100, 0, 0, 0),
            new TrackPoint(2, 0, 1, 0, 0, 0)
        };
        var pointToPoint = Enumerable.Range(0, 101)
            .Select(index => new TrackPoint(index * 5, 0, 0, 0, 0, 0))
            .ToArray();

        Assert.AreEqual(TrackLayoutKind.Circuit, TrackAlgorithms.InferLayout(circuit));
        Assert.AreEqual(TrackLayoutKind.PointToPoint, TrackAlgorithms.InferLayout(pointToPoint));
        Assert.AreEqual(
            TrackLayoutKind.PointToPoint,
            TrackAlgorithms.BuildTemplate("sprint", pointToPoint, layoutKind: TrackLayoutKind.PointToPoint).LayoutKind);
    }

    [TestMethod]
    public void SectorColorsSeparateCurrentCompetitionAndHistoricalBest()
    {
        Assert.AreEqual(SectorColorState.Gray, SectorColorClassifier.Classify(null, true, 10, 9));
        Assert.AreEqual(SectorColorState.Gray, SectorColorClassifier.Classify(10, false, 10, 9));
        Assert.AreEqual(SectorColorState.Yellow, SectorColorClassifier.Classify(10.05, true, 10, 9));
        Assert.AreEqual(SectorColorState.Green, SectorColorClassifier.Classify(9.5, true, 10, 9));
        Assert.AreEqual(SectorColorState.Purple, SectorColorClassifier.Classify(9, true, 9, 9));
        Assert.AreEqual(SectorColorState.Green, SectorColorClassifier.Classify(10, true, null, 9));
        Assert.AreEqual(SectorColorState.Purple, SectorColorClassifier.Classify(10, true, null, null),
            "Without any saved reference, the first valid sector is the dataset best and must be purple.");
        Assert.AreEqual(SectorColorState.Yellow, SectorColorClassifier.Classify(10, true, 10, 9, false),
            "Point-to-point events compare only with historical best and must not emit session-best green.");
        Assert.AreEqual(SectorColorState.Purple, SectorColorClassifier.Classify(8.9, true, 8.9, 9, false));
        StringAssert.Contains(SectorColorClassifier.DatasetBestExplanation, "不是在线世界纪录");
    }
}
