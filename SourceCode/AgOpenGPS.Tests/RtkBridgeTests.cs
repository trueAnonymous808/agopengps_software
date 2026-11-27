using NUnit.Framework;
using System;

namespace AgOpenGPS.Tests
{
    public class RtkBridgeTests
    {
        private const double Speed = 3;   // m/s, north
        private const double Dt = 0.1;

        // drives north: RTK for 'goodS', then a loss for 'lostS' where the receiver's position is 0.5 m east and
        // 0.3 m south of the truth (a float solution's offset); the IMU reads 5 degrees less than the true heading
        // and drifts 'imuDriftDegPerS'. Returns the bridge and the largest error (m) while bridging.
        private static double endT, endN;   // where the last run stopped

        private static CRtkBridge Run(double goodS, double lostS, double imuDriftDegPerS, bool withImu, out double worst,
            double maxS = 120, int quality = 5)
        {
            var bridge = new CRtkBridge { MaxSeconds = maxS, MaxErrorCm = 50 };
            double t = 0, n = 0;
            worst = 0;
            for (; t < goodS; t += Dt, n += Speed * Dt)
            {
                double imu = 360 - 5 + imuDriftDegPerS * t;
                bridge.Learn(t, 0, imu);
                bridge.Step(t, true, 4, 0, n, Speed, 0, withImu ? imu : double.NaN, 0, 3, out _, out _);
            }
            for (double end = t + lostS; t < end; t += Dt, n += Speed * Dt)
            {
                double imu = 360 - 5 + imuDriftDegPerS * t;
                bridge.Step(t, false, quality, 0.5, n - 0.3, Speed, 0, withImu ? imu : double.NaN, 0, 3, out double e, out double nn);
                if (bridge.Active) worst = Math.Max(worst, Math.Sqrt(e * e + (nn - n) * (nn - n)));
            }
            endT = t;
            endN = n;
            return bridge;
        }

        [Test]
        public void Bridge_HoldsTheLineThroughAShortLoss()
        {
            CRtkBridge bridge = Run(90, 30, 0, true, out double worst);
            Assert.That(bridge.Active, Is.True);
            Assert.That(bridge.UsesImu, Is.True);
            Assert.That(worst, Is.LessThan(0.05));   // the float's half metre offset does not come through
        }

        [Test]
        public void Bridge_TheLearntImuDriftIsTakenOff()
        {
            CRtkBridge bridge = Run(120, 30, 0.01, true, out double worst);
            bridge.Learnt(out _, out double drift, out bool ready);
            Assert.That(ready, Is.True);
            Assert.That(drift, Is.EqualTo(-0.01).Within(0.002));   // GPS minus IMU falls as the IMU drifts up
            Assert.That(worst, Is.LessThan(0.05));
        }

        [Test]
        public void Bridge_WithoutAnImuTheSteerAngleCarriesTheHeading()
        {
            CRtkBridge bridge = Run(90, 20, 0, false, out double worst);
            Assert.That(bridge.UsesImu, Is.False);
            Assert.That(worst, Is.LessThan(0.05));
        }

        [Test]
        public void Bridge_EndsAfterItsTime()
        {
            CRtkBridge bridge = Run(90, 30, 0, true, out _, maxS: 10);
            Assert.That(bridge.Now, Is.EqualTo(CRtkBridge.Phase.Expired));
            Assert.That(bridge.LastBridgeS, Is.EqualTo(10).Within(0.2));
        }

        [Test]
        public void Bridge_EndsWhenTheErrorEstimateIsTooBig()
        {
            var bridge = new CRtkBridge { MaxSeconds = 600, MaxErrorCm = 5 };
            double t = 0;
            for (; t < 60; t += Dt) bridge.Step(t, true, 4, 0, t * Speed, Speed, 0, double.NaN, 0, 3, out _, out _);
            for (; t < 200; t += Dt) bridge.Step(t, false, 2, 0, t * Speed, Speed, 0, double.NaN, 0, 3, out _, out _);
            Assert.That(bridge.Now, Is.EqualTo(CRtkBridge.Phase.Expired));
            // DGPS without an IMU: 1 cm + 0.3 cm/s reaches 5 cm after about 13 s
            Assert.That(bridge.LastBridgeS, Is.EqualTo(13.3).Within(0.5));
        }

        [Test]
        public void Bridge_MeasuresItselfWhenRtkComesBack()
        {
            var bridge = Run(90, 20, 0, true, out _);
            bridge.Step(endT, true, 4, 0, endN, Speed, 0, 355, 0, 3, out double e, out double nn);
            Assert.That(bridge.Now, Is.EqualTo(CRtkBridge.Phase.Idle));
            Assert.That(e, Is.EqualTo(0));                    // RTK again: the raw position
            Assert.That(bridge.LastErrorAtEndCm, Is.LessThan(5));
            Assert.That(bridge.Bridges, Is.EqualTo(1));
        }

        [Test]
        public void Bridge_NothingWithoutAnRtkFixFirst()
        {
            var bridge = new CRtkBridge();
            bridge.Step(0, false, 5, 1, 2, Speed, 0, double.NaN, 0, 3, out double e, out double n);
            Assert.That(bridge.Active, Is.False);
            Assert.That(e, Is.EqualTo(1));
            Assert.That(n, Is.EqualTo(2));
        }

        [Test]
        public void Setup_WithoutAnyHeadingSourceNoBridgeStarts()
        {
            // no IMU, no dual antenna and no steer module: the heading cannot be followed, so no bridge
            var bridge = new CRtkBridge();
            double t = 0;
            for (; t < 30; t += Dt) bridge.Step(t, true, 4, 0, t * Speed, Speed, 0, double.NaN, double.NaN, 3, out _, out _);
            bridge.Step(t, false, 5, 0.5, t * Speed, Speed, 0, double.NaN, double.NaN, 3, out double e, out double n);
            Assert.That(bridge.Active, Is.False);
            Assert.That(e, Is.EqualTo(0.5));   // the raw position goes through
        }

        [Test]
        public void Setup_LosingTheLastHeadingSourceEndsTheBridge()
        {
            var bridge = new CRtkBridge();
            double t = 0;
            for (; t < 30; t += Dt) bridge.Step(t, true, 4, 0, t * Speed, Speed, 0, double.NaN, 0, 3, out _, out _);
            for (double end = t + 5; t < end; t += Dt) bridge.Step(t, false, 5, 0, t * Speed, Speed, 0, double.NaN, 0, 3, out _, out _);
            Assert.That(bridge.Active, Is.True);
            bridge.Step(t, false, 5, 0, t * Speed, Speed, 0, double.NaN, double.NaN, 3, out _, out _);   // the steer module is gone
            Assert.That(bridge.Now, Is.EqualTo(CRtkBridge.Phase.Expired));
        }

        [Test]
        public void Setup_ADualAntennaHeadingIsUsedLikeAnImu()
        {
            // a dual antenna reads the true heading (no offset): learnt offset 0, bridge holds
            var bridge = new CRtkBridge();
            double t = 0, n = 0;
            for (; t < 60; t += Dt, n += Speed * Dt)
            {
                bridge.Learn(t, 0, 0);
                bridge.Step(t, true, 4, 0, n, Speed, 0, 0, double.NaN, 3, out _, out _);
            }
            double worst = 0;
            for (double end = t + 30; t < end; t += Dt, n += Speed * Dt)
            {
                bridge.Step(t, false, 2, 0.3, n + 0.2, Speed, 0, 0, double.NaN, 3, out double e, out double nn);
                worst = Math.Max(worst, Math.Sqrt(e * e + (nn - n) * (nn - n)));
            }
            Assert.That(bridge.UsesImu, Is.True);
            Assert.That(worst, Is.LessThan(0.05));
        }

        [Test]
        public void Setup_BadNumbersNeverComeOut()
        {
            var bridge = new CRtkBridge();
            double t = 0;
            for (; t < 30; t += Dt) bridge.Step(t, true, 4, 0, t * Speed, Speed, 0, double.NaN, 0, 3, out _, out _);
            bridge.Step(t, false, 5, 1, 2, double.NaN, 0, double.NaN, 0, 3, out double e, out double n);
            Assert.That(double.IsNaN(e) || double.IsNaN(n), Is.False);
            bridge.Learn(t, double.NaN, 10);   // ignored, no exception
        }
    }
}
