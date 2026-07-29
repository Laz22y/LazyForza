using LazyForza.Analysis;
using LazyForza.Domain;

namespace LazyForza.Analysis.Tests;

[TestClass]
public sealed class DrivingDynamicsAnalyzerTests
{
    private static readonly VehicleProfileFingerprint RearWheelDrive =
        new(1, 5, 850, 1, 8, 8_000, "g", "c");

    [TestMethod]
    public void LegacySamplesKeepOriginalInputLayersAndRejectExtendedLayers()
    {
        var sample = Sample(dynamics: null) with { Accel = 0.72, Brake = 0.18 };

        var throttle = DrivingDynamicsAnalyzer.Evaluate(
            sample,
            RearWheelDrive,
            DrivingDynamicsLayer.Throttle);
        var brake = DrivingDynamicsAnalyzer.Evaluate(
            sample,
            RearWheelDrive,
            DrivingDynamicsLayer.Brake);
        var slip = DrivingDynamicsAnalyzer.Evaluate(
            sample,
            RearWheelDrive,
            DrivingDynamicsLayer.TireSlip);

        Assert.IsTrue(throttle.IsAvailable);
        Assert.AreEqual(0.72, throttle.Intensity, 0.0001);
        Assert.IsTrue(brake.IsAvailable);
        Assert.AreEqual(0.18, brake.Intensity, 0.0001);
        Assert.IsFalse(slip.IsAvailable);
    }

    [TestMethod]
    public void BalanceUsesFrontRearSlipAngleDifferenceAsQualifiedEvidence()
    {
        var understeer = Sample(new LapDynamics(
            0.35,
            default,
            new WheelValues(0.26f, 0.24f, 0.05f, 0.06f),
            default));
        var oversteer = Sample(new LapDynamics(
            -0.4,
            default,
            new WheelValues(0.05f, 0.06f, 0.27f, 0.25f),
            default));

        var understeerPoint = DrivingDynamicsAnalyzer.Evaluate(
            understeer,
            RearWheelDrive,
            DrivingDynamicsLayer.HandlingBalance);
        var oversteerPoint = DrivingDynamicsAnalyzer.Evaluate(
            oversteer,
            RearWheelDrive,
            DrivingDynamicsLayer.HandlingBalance);

        Assert.AreEqual(HandlingBalanceState.SuspectedUndersteer, understeerPoint.Balance);
        Assert.IsTrue(understeerPoint.Intensity > 0.5);
        Assert.AreEqual(HandlingBalanceState.SuspectedOversteer, oversteerPoint.Balance);
        Assert.IsTrue(oversteerPoint.Intensity > 0.5);
    }

    [TestMethod]
    public void WheelspinUsesDrivenAxleAndBrakingInstabilityNeedsBraking()
    {
        var dynamics = new LapDynamics(
            0.1,
            new WheelValues(0.03f, 0.04f, 0.55f, 0.48f),
            default,
            new WheelValues(0.08f, 0.09f, 0.52f, 0.47f));
        var accelerating = Sample(dynamics) with { Accel = 0.85, Brake = 0.02 };
        var braking = accelerating with { Accel = 0, Brake = 0.8 };

        var wheelspin = DrivingDynamicsAnalyzer.Evaluate(
            accelerating,
            RearWheelDrive,
            DrivingDynamicsLayer.ExitWheelspin);
        var noBrakeInstability = DrivingDynamicsAnalyzer.Evaluate(
            accelerating,
            RearWheelDrive,
            DrivingDynamicsLayer.BrakingInstability);
        var brakeInstability = DrivingDynamicsAnalyzer.Evaluate(
            braking,
            RearWheelDrive,
            DrivingDynamicsLayer.BrakingInstability);

        Assert.IsTrue(wheelspin.Intensity > 0.5);
        Assert.AreEqual(0, noBrakeInstability.Intensity);
        Assert.IsTrue(brakeInstability.Intensity > 0);
    }

    private static LapSample Sample(LapDynamics? dynamics) => new(
        100,
        5,
        30,
        5_000,
        3,
        0,
        0,
        0,
        10,
        0,
        20,
        dynamics);
}
