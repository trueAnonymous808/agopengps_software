using NUnit.Framework;
using System;

namespace AgOpenGPS.Tests
{
    public class SteerGeometryCalTests
    {
        private const double Wheelbase = 3;

        // drives a steady circle: the wheels really stand at realDeg, the sensor reports reportedDeg
        private static double Drive(CSteerGeometryCal cal, double realDeg, double reportedDeg, double seconds,
            double startTime = 0, double speedKmh = 6, bool goodFix = true)
        {
            double v = speedKmh / 3.6, dt = 0.1;
            double perMetre = Math.Tan(realDeg * Math.PI / 180) / Wheelbase;
            double e = 0, n = 0, h = 0, t = startTime;
            for (int i = 0; i < seconds / dt; i++)
            {
                cal.AddFix(t, e, n, h, reportedDeg, speedKmh, false, goodFix, Wheelbase);
                double step = v * dt;
                e += step * Math.Sin(h);
                n += step * Math.Cos(h);
                h += step * perMetre;
                t += dt;
            }
            return t;
        }

        [Test]
        public void ACorrectSensorNeedsNoChange()
        {
            var cal = new CSteerGeometryCal();
            double t = Drive(cal, 20, 20, 200);
            Drive(cal, -20, -20, 200, t);
            Assert.That(cal.RightReady && cal.LeftReady, Is.True);
            Assert.That(cal.RecommendedCpd(100), Is.EqualTo(100));
            Assert.That(cal.RecommendedAckermann(100), Is.EqualTo(100));
            Assert.That(cal.Confidence, Is.GreaterThan(90));
        }

        [Test]
        public void ASensorThatReadsTooLittleLowersTheCpd()
        {
            // the sensor says 16° where the wheels stand at 20°: counts / CPD is too small, so CPD must come down
            var cal = new CSteerGeometryCal();
            Drive(cal, 20, 16, 200);
            Assert.That(cal.RecommendedCpd(100), Is.EqualTo(80).Within(1));
        }

        [Test]
        public void TheLeftSideAloneGivesTheAckermann()
        {
            // right reads true, left reads 10 % too much: Ackermann 100 -> about 91
            var cal = new CSteerGeometryCal();
            double t = Drive(cal, 20, 20, 200);
            Drive(cal, -20, -22, 200, t);
            Assert.That(cal.RecommendedCpd(100), Is.EqualTo(100).Within(1));
            Assert.That(cal.RecommendedAckermann(100), Is.EqualTo(91).Within(1));
        }

        [Test]
        public void NothingIsRecommendedTooEarly()
        {
            var cal = new CSteerGeometryCal();
            Drive(cal, 20, 20, 20);   // about 30 m: far fewer than the samples needed
            Assert.That(cal.RightCount, Is.GreaterThan(0));
            Assert.That(cal.RecommendedCpd(100), Is.Null);
            Assert.That(cal.RecommendedAckermann(100), Is.Null);
        }

        [Test]
        public void AckermannNeedsBothSides()
        {
            var cal = new CSteerGeometryCal();
            Drive(cal, 20, 20, 200);
            Assert.That(cal.RecommendedCpd(100), Is.Not.Null);
            Assert.That(cal.RecommendedAckermann(100), Is.Null);
        }

        [Test]
        public void TheCpdChangeIsLimited()
        {
            // a sensor reading half the real angle would ask for CPD 50: one apply goes only to 70
            var cal = new CSteerGeometryCal();
            Drive(cal, 30, 15, 200);
            Assert.That(cal.RecommendedCpd(100), Is.EqualTo(70));
        }

        [Test]
        public void NoSamplesWithoutAGoodFixOrOutsideTheLimits()
        {
            var cal = new CSteerGeometryCal();
            Drive(cal, 20, 20, 100, goodFix: false);
            Drive(cal, 20, 20, 100, speedKmh: 15);
            Drive(cal, 3, 3, 100);    // too little steering to measure
            Assert.That(cal.RightCount + cal.LeftCount, Is.EqualTo(0));
        }

        [Test]
        public void ResetForgetsEverything()
        {
            var cal = new CSteerGeometryCal();
            Drive(cal, 20, 20, 200);
            cal.Reset();
            Assert.That(cal.RightCount, Is.EqualTo(0));
            Assert.That(cal.RecommendedCpd(100), Is.Null);
        }
    }
}
