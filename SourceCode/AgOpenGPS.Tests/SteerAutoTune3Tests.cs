using NUnit.Framework;
using System;

namespace AgOpenGPS.Tests
{
    public class SteerAutoTune3Tests
    {
        // ---- GPS guard ----

        [Test]
        public void GpsGuard_QuietUntilAnRtkFixWasSeen()
        {
            var guard = new CGpsGuard();
            Assert.That(guard.Update(0, 5, 1), Is.False);   // float from the start: nothing lost
            Assert.That(guard.Now, Is.EqualTo(CGpsGuard.State.Off));
        }

        [Test]
        public void GpsGuard_LosingTheFixWarnsOnceAndCountsTheTime()
        {
            var guard = new CGpsGuard();
            guard.Update(0, 4, 1);
            Assert.That(guard.Now, Is.EqualTo(CGpsGuard.State.Good));
            Assert.That(guard.Update(1, 5, 1), Is.True);    // RTK fix -> float: the warning
            Assert.That(guard.Update(5, 5, 1), Is.False);   // still float: no second warning
            Assert.That(guard.DegradedForS, Is.EqualTo(4).Within(1e-9));
            Assert.That(guard.ShouldDisengage(10), Is.False);
            guard.Update(12, 5, 1);
            Assert.That(guard.ShouldDisengage(10), Is.True);
            Assert.That(guard.ShouldDisengage(0), Is.False);   // 0: never switch off
            guard.Update(13, 4, 1);
            Assert.That(guard.Now, Is.EqualTo(CGpsGuard.State.Good));
            Assert.That(guard.Events, Is.EqualTo(1));
        }

        [Test]
        public void GpsGuard_OldCorrectionsCountAsLost()
        {
            var guard = new CGpsGuard();
            guard.Update(0, 4, 1);
            Assert.That(guard.Update(1, 4, 6), Is.True);
            Assert.That(guard.Now, Is.EqualTo(CGpsGuard.State.Degraded));
        }

        // ---- linearity ----

        [Test]
        public void Linearity_LinearSendsTheAngleAsItIs()
        {
            Assert.That(CWasLinearity.Apply(25, 1, 1), Is.EqualTo(25));
            Assert.That(CWasLinearity.Apply(-5, 1.1, 1.2), Is.EqualTo(-5));   // small angles are never touched
        }

        [Test]
        public void Linearity_BigAnglesAreDividedByTheirFactor()
        {
            Assert.That(CWasLinearity.Factor(16, 1.05, 1.1), Is.EqualTo(1.05).Within(1e-9));
            Assert.That(CWasLinearity.Factor(30, 1.05, 1.1), Is.EqualTo(1.1).Within(1e-9));
            Assert.That(CWasLinearity.Apply(-30, 1.05, 1.1), Is.EqualTo(-30 / 1.1).Within(1e-9));
            Assert.That(CWasLinearity.Factor(12.25, 1.1, 1.1), Is.EqualTo(1.05).Within(1e-9));   // half way to the middle band
        }

        [Test]
        public void Linearity_FactorsStayInRange()
        {
            Assert.That(CWasLinearity.Clamp(2), Is.EqualTo(CWasLinearity.MaxFactor));
            Assert.That(CWasLinearity.Clamp(0.5), Is.EqualTo(CWasLinearity.MinFactor));
        }

        [Test]
        public void Linearity_TheGeometryCirclesShowIt()
        {
            // a sensor that reads 10 % short at big angles (real / sensor 1.1 there, 1.0 at small ones)
            var cal = new CSteerGeometryCal();
            double t = 0;
            foreach (double real in new[] { 8.0, -8.0, 26.0, -26.0 })
            {
                double reported = Math.Abs(real) > 20 ? real / 1.1 : real;
                t = Drive(cal, real, reported, 150, t);
            }
            cal.Linearity(out double middle, out double big);
            Assert.That(double.IsNaN(middle), Is.True);          // no circles in the middle band
            Assert.That(big, Is.EqualTo(1.1).Within(0.02));
            int[] n = cal.BandCounts();
            Assert.That(n[0], Is.GreaterThan(CSteerGeometryCal.MinBandSamples));
            Assert.That(n[2], Is.GreaterThan(CSteerGeometryCal.MinBandSamples));
        }

        private static double Drive(CSteerGeometryCal cal, double realDeg, double reportedDeg, double seconds, double t)
        {
            const double wheelbase = 3, speed = 6, dt = 0.1;
            double perMetre = Math.Tan(realDeg * Math.PI / 180) / wheelbase;
            double e = 0, n = 0, h = 0;
            for (int i = 0; i < seconds / dt; i++)
            {
                cal.AddFix(t, e, n, h, reportedDeg, speed, false, true, wheelbase);
                double step = speed / 3.6 * dt;
                e += step * Math.Sin(h);
                n += step * Math.Cos(h);
                h += step * perMetre;
                t += dt;
            }
            cal.AddFix(t, e, n, h, 0, speed, false, true, wheelbase);   // break the run between circles
            return t + 1;
        }

        // ---- IMU fusion ----

        [Test]
        public void Fusion_ADriftingImuWantsFasterFusion()
        {
            var tune = new CFusionTune();
            for (int i = 0; i < 400; i++) tune.AddSample(0, 0.5 * Math.PI / 180, true);   // fused heading half a degree off
            Assert.That(tune.Recommend(0.06, 0.002, 0.4, out double w), Is.EqualTo(TuneVerdict.FusionDrifts));
            Assert.That(w, Is.EqualTo(0.075).Within(1e-9));
        }

        [Test]
        public void Fusion_NoisyGpsHeadingWantsSlowerFusion()
        {
            var rnd = new Random(3);
            var tune = new CFusionTune();
            for (int i = 0; i < 400; i++) tune.AddSample((rnd.NextDouble() - 0.5) * 4 * Math.PI / 180, 0, true);   // +-2 degrees of noise
            Assert.That(tune.Recommend(0.2, 0.002, 0.4, out double w), Is.EqualTo(TuneVerdict.FusionNoisy));
            Assert.That(w, Is.EqualTo(0.16).Within(1e-9));
        }

        [Test]
        public void Fusion_ACalmHeadingIsLeftAlone()
        {
            var tune = new CFusionTune();
            for (int i = 0; i < 400; i++) tune.AddSample(0.0005 * Math.Sin(i), 0, true);
            Assert.That(tune.Recommend(0.06, 0.002, 0.4, out _), Is.EqualTo(TuneVerdict.Good));
        }

        // ---- speed dependent Kp ----

        [Test]
        public void SpeedGain_BlendsBetweenSlowAndFast()
        {
            Assert.That(CSpeedGain.Kp(20, 0, 15), Is.EqualTo(20));    // off: one Kp
            Assert.That(CSpeedGain.Kp(20, 12, 5), Is.EqualTo(20));
            Assert.That(CSpeedGain.Kp(20, 12, 10), Is.EqualTo(16));
            Assert.That(CSpeedGain.Kp(20, 12, 18), Is.EqualTo(12));
        }
    }
}
