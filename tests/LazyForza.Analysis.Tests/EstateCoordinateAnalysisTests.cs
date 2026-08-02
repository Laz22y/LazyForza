using LazyForza.Analysis;

namespace LazyForza.Analysis.Tests;

[TestClass]
public sealed class EstateCoordinateAnalysisTests
{
    private static readonly EstateCoordinateMarker[] Reference =
    [
        new("起点", 0, 10, 0),
        new("一号弯", 100, 12, 0),
        new("终点侧", 40, 15, 120),
        new("维修区", -30, 9, 70)
    ];

    [TestMethod]
    public void IdentifiesDirectlySharedCoordinates()
    {
        var candidate = Reference.Select(marker => marker with
        {
            X = marker.X + 0.08,
            Y = marker.Y - 0.03,
            Z = marker.Z + 0.05
        }).ToArray();

        var result = EstateCoordinateAnalyzer.Compare(Reference, candidate);

        Assert.AreEqual(EstateCoordinateCompatibility.DirectMatch, result.Compatibility);
        Assert.AreEqual(4, result.MatchedMarkerCount);
        Assert.IsTrue(result.DirectRmsMeters < 0.2);
    }

    [TestMethod]
    public void RecoversYawRotationAndTranslation()
    {
        const double angleDegrees = 37;
        var angle = angleDegrees * Math.PI / 180;
        var cos = Math.Cos(angle);
        var sin = Math.Sin(angle);
        var candidate = Reference.Select(marker => new EstateCoordinateMarker(
            marker.Name,
            cos * marker.X - sin * marker.Z + 600,
            marker.Y - 25,
            sin * marker.X + cos * marker.Z - 300)).ToArray();

        var result = EstateCoordinateAnalyzer.Compare(Reference, candidate);

        Assert.AreEqual(EstateCoordinateCompatibility.RigidTransform, result.Compatibility);
        Assert.AreEqual(-angleDegrees, result.RotationDegrees, 0.001);
        Assert.IsTrue(result.FittedRmsMeters < 0.001);
        Assert.AreEqual(1, result.EstimatedScaleRatio, 0.0001);
    }

    [TestMethod]
    public void RejectsAStretchedCoordinateSpace()
    {
        var candidate = Reference.Select(marker => marker with
        {
            X = marker.X * 1.08 + 500,
            Z = marker.Z * 1.08 - 200
        }).ToArray();

        var result = EstateCoordinateAnalyzer.Compare(Reference, candidate);

        Assert.AreEqual(EstateCoordinateCompatibility.Incompatible, result.Compatibility);
        Assert.IsTrue(Math.Abs(result.EstimatedScaleRatio - 1) > 0.05);
    }

    [TestMethod]
    public void RequiresAtLeastTwoNamedMarkers()
    {
        var result = EstateCoordinateAnalyzer.Compare(
            [new EstateCoordinateMarker("起点", 0, 0, 0)],
            [new EstateCoordinateMarker("起点", 1, 0, 0)]);

        Assert.AreEqual(EstateCoordinateCompatibility.InsufficientEvidence, result.Compatibility);
    }
}
