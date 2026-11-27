using NUnit.Framework;
using System;

namespace AgOpenGPS.Tests
{
    public class SteerAutoTune5Tests
    {
        // the tractor rocks; the antenna, heightReal metres up, swings with it; the IMU tells the roll lateS late
        private static CRollTune Drive(double heightReal, double lateS, double seconds = 400, double rockDeg = 1.5)
        {
            var tune = new CRollTune();
            const double dt = 0.1;
            Func<double, double> roll = t => rockDeg * (Math.Sin(2 * Math.PI * 0.6 * t) * 0.6 + Math.Sin(2 * Math.PI * 1.3 * t + 1) * 0.3
                + Math.Sin(2 * Math.PI * 2.1 * t + 2) * 0.2);
            for (double t = 0; t < seconds; t += dt)
            {
                double phi = roll(t);
                // heading north: the right is east; a slow wander of the course besides the swing
                double east = 0.05 * Math.Sin(0.05 * t) + heightReal * Math.Sin(phi * Math.PI / 180);
                double north = 3 * t;
                double imu = roll(t - lateS);
                double corr = -Math.Sin(imu * Math.PI / 180) * 3.6;   // what the compensation set to 3.6 m applies
                tune.AddSample(t, east, north, 0, imu, corr, 10, true);
            }
            tune.Analyze();
            return tune;
        }

        [Test]
        public void RollTune_FindsTheDelayAndTheRealHeight()
        {
            CRollTune tune = Drive(3.0, 0.15);
            Assert.That(tune.Ready, Is.True);
            Assert.That(tune.ShiftS, Is.EqualTo(0.15).Within(0.03));
            Assert.That(tune.HeightM, Is.EqualTo(3.0).Within(0.2));
            TuneVerdict v = tune.Recommend(0, 1, 3.6, out double shift, out double gain);
            Assert.That(v, Is.EqualTo(TuneVerdict.Recommend));
            Assert.That(shift, Is.EqualTo(0.15).Within(0.03));
            Assert.That(gain, Is.EqualTo(3.0 / 3.6).Within(0.06));
        }

        [Test]
        public void RollTune_AGoodSetUpHasNothingToChange()
        {
            CRollTune tune = Drive(3.6, 0);
            TuneVerdict v = tune.Recommend(0, 1, 3.6, out _, out _);
            Assert.That(v, Is.EqualTo(TuneVerdict.Good));
        }

        [Test]
        public void RollTune_SaysNothingOnSmoothGround()
        {
            CRollTune tune = Drive(3.0, 0.15, rockDeg: 0.05);
            Assert.That(tune.Ready, Is.False);
            Assert.That(tune.Recommend(0, 1, 3.6, out _, out _), Is.EqualTo(TuneVerdict.Collecting));
        }

        [Test]
        public void Pit_HoldsTheSteeringOnlyAfterAnUnexplainedRock()
        {
            var pit = new CPitGuard();
            pit.Update(0, 0, 0, true, 4);
            pit.Smooth(0, 0, 4);
            // a rock the compensation took out of the position: nothing
            pit.Update(0.1, 2, 0.01, true, 4);
            Assert.That(pit.Holding(0.1), Is.False);
            Assert.That(pit.Smooth(0.1, 6, 4), Is.EqualTo(6));

            var pit2 = new CPitGuard();
            pit2.Update(0, 0, 0, true, 4);
            pit2.Smooth(0, 0, 4);
            // a rock that still swung the position 5 cm: the angle may change only 4 degrees a second
            pit2.Update(0.1, 2, 0.05, true, 4);
            Assert.That(pit2.Holding(0.1), Is.True);
            Assert.That(pit2.Smooth(0.1, 6, 4), Is.EqualTo(0.4).Within(1e-9));
            Assert.That(pit2.Holding(1.0), Is.False);

            // switched off: never
            var off = new CPitGuard();
            off.Update(0, 0, 0, false, 4);
            off.Update(0.1, 2, 0.05, false, 4);
            Assert.That(off.Holding(0.1), Is.False);
        }

        [Test]
        public void Pit_SuggestsTheRateThatKeptCloserToTheLine()
        {
            var pit = new CPitGuard();
            double t = 0;
            for (int e = 0; e < 24; e++)
            {
                pit.Update(t, 0, 0, true, 4);
                pit.Update(t + 0.1, 2, 0.05, true, 4);   // a rock: every other one uses the other rate
                bool other = e % 2 == 1;
                for (double u = 0.1; u < 2.6; u += 0.1) pit.FeedXte(t + u, other ? 0.02 : 0.05, true);
                t += 10;
                pit.FeedXte(t, 0, true);   // the watch is over
            }
            TuneVerdict v = pit.Recommend(4, out double rate, out double nowCm, out double otherCm);
            Assert.That(v, Is.EqualTo(TuneVerdict.Recommend));
            Assert.That(rate, Is.EqualTo(2.4).Within(1e-9));   // 4 * 0.6
            Assert.That(otherCm, Is.LessThan(nowCm));
        }

        [Test]
        public void Baseline_KeepsTheRollAndPitValues()
        {
            var b = new SteerBaseline
            {
                Kp = 50,
                MinPwm = 25,
                Cpd = 110,
                Ackermann = 100,
                WasOffset = 3,
                DeadZoneHeading = 10,
                DeadZoneDelay = 5,
                Hold = 3,
                SpeedFactor = 1,
                SideHill = 0,
                RollZero = 0,
                AcquireFactor = 1.5,
                Integral = 0,
                UTurnComp = 1,
                FusionWeight = 0.06,
                LinMiddle = 1,
                LinBig = 1,
                KpFast = 0,
                RollShift = 0.12,
                RollGain = 0.85,
                PitRate = 3.5
            };
            SteerBaseline back = SteerBaseline.Load(b.Save());
            Assert.That(back.HasExtras3, Is.True);
            Assert.That(back.RollShift, Is.EqualTo(0.12));
            Assert.That(back.RollGain, Is.EqualTo(0.85));
            Assert.That(back.PitRate, Is.EqualTo(3.5));
            // one stored before them: read, and known to lack them
            string old = string.Join(";", b.Save().Split(';'), 0, 18);
            Assert.That(SteerBaseline.Load(old).HasExtras3, Is.False);
        }
    }
}
