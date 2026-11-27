using NUnit.Framework;
using System;

namespace AgOpenGPS.Tests
{
    public class DeadZoneAndSpeedTuneTests
    {
        // a wanted angle held at 3°, the sensor reading it with noise (sigma 0.2°), plus an optional extra movement
        private static CDeadZoneTune Held(Func<double, double> extra, Func<double, bool> inZone, double sigma = 0.2)
        {
            var rnd = new Random(7);
            var tune = new CDeadZoneTune();
            for (double t = 0; t < 60; t += 0.1)
            {
                double gauss = Math.Sqrt(-2 * Math.Log(1 - rnd.NextDouble())) * Math.Cos(2 * Math.PI * rnd.NextDouble());
                tune.AddSample(t, 3, 3 + sigma * gauss + extra(t), inZone(t), 6, true);
            }
            return tune;
        }

        [Test]
        public void DeadZone_TheNoiseIsMeasured()
        {
            CDeadZoneTune tune = Held(t => 0, t => false);
            tune.Measure(out double noise, out _, out _, out _);
            Assert.That(noise, Is.EqualTo(0.2).Within(0.04));
        }

        [Test]
        public void DeadZone_SmallerThanTheNoiseGrowsToTwiceTheNoise()
        {
            CDeadZoneTune tune = Held(t => 0, t => false);
            Assert.That(tune.Recommend(0.1, 5, 0.1, 5, 1, 10, out double zone, out int delay), Is.EqualTo(TuneVerdict.ZoneBelowNoise));
            Assert.That(zone, Is.EqualTo(0.4).Within(0.11));
            Assert.That(delay, Is.EqualTo(5));
        }

        [Test]
        public void DeadZone_MuchLargerThanTheNoiseShrinks()
        {
            CDeadZoneTune tune = Held(t => 0, t => false);
            Assert.That(tune.Recommend(2.0, 5, 0.1, 5, 1, 10, out double zone, out _), Is.EqualTo(TuneVerdict.ZoneAboveNoise));
            Assert.That(zone, Is.EqualTo(0.4).Within(0.11));
        }

        [Test]
        public void DeadZone_FlickeringWantsALongerDelay()
        {
            CDeadZoneTune tune = Held(t => 0, t => ((int)t) % 2 == 0);   // on and off every second
            Assert.That(tune.Recommend(0.6, 5, 0.1, 5, 1, 10, out double zone, out int delay), Is.EqualTo(TuneVerdict.ZoneFlickers));
            Assert.That(delay, Is.EqualTo(7));
            Assert.That(zone, Is.EqualTo(0.6));
        }

        [Test]
        public void DeadZone_HuntingWithAQuietZoneWantsAShorterDelay()
        {
            // the motor swings half a degree each way every 0.3 s and the zone never comes on
            CDeadZoneTune tune = Held(t => ((int)(t / 0.3)) % 2 == 0 ? 0.5 : -0.5, t => false);
            Assert.That(tune.Recommend(0.6, 5, 0.1, 5, 1, 10, out _, out int delay), Is.EqualTo(TuneVerdict.MotorHunts));
            Assert.That(delay, Is.EqualTo(4));
        }

        [Test]
        public void DeadZone_NothingWithoutSteering()
        {
            var tune = new CDeadZoneTune();
            for (double t = 0; t < 60; t += 0.1) tune.AddSample(t, 3, 3, false, 6, false);
            Assert.That(tune.Recommend(0.1, 5, 0.1, 5, 1, 10, out _, out _), Is.EqualTo(TuneVerdict.Collecting));
        }

        // 300 m slowly, then 300 m fast, each with its own way of holding the line
        private static CLookAheadSpeedTune Drive(Func<double, double> slowOff, Func<double, double> fastOff, bool fast = true)
        {
            var tune = new CLookAheadSpeedTune();
            for (double s = 0; s < 300; s += 0.15) tune.AddSample(0, s, slowOff(s), true, 6);
            if (fast) for (double s = 300; s < 600; s += 0.15) tune.AddSample(0, s, fastOff(s), true, 12);
            return tune;
        }

        private static double Tight(double s) => 0.008 * Math.Sin(2 * Math.PI * s / 10);
        private static double Weave(double s) => 0.05 * Math.Sin(2 * Math.PI * s / 10);

        [Test]
        public void SpeedFactor_WeavingOnlyWhenFastRaisesIt()
        {
            CLookAheadSpeedTune tune = Drive(Tight, Weave);
            Assert.That(tune.Recommend(1.9, 0.5, 6, out double factor), Is.EqualTo(TuneVerdict.Weaves));
            Assert.That(factor, Is.EqualTo(2.1).Within(1e-9));
        }

        [Test]
        public void SpeedFactor_StayingOffOnlyWhenFastLowersIt()
        {
            CLookAheadSpeedTune tune = Drive(Tight, s => 0.06 + 0.005 * Math.Sin(s));
            Assert.That(tune.Recommend(2.0, 0.5, 6, out double factor), Is.EqualTo(TuneVerdict.ReturnsSlowly));
            Assert.That(factor, Is.EqualTo(1.8).Within(1e-9));
        }

        [Test]
        public void SpeedFactor_AlikeAtBothSpeedsIsLeftAlone()
        {
            CLookAheadSpeedTune tune = Drive(Weave, Weave);
            Assert.That(tune.Recommend(1.9, 0.5, 6, out double factor), Is.EqualTo(TuneVerdict.Good));
            Assert.That(factor, Is.EqualTo(1.9));
        }

        [Test]
        public void SpeedFactor_NeedsBothSpeeds()
        {
            CLookAheadSpeedTune tune = Drive(Tight, Weave, fast: false);
            Assert.That(tune.Recommend(1.9, 0.5, 6, out _), Is.EqualTo(TuneVerdict.Collecting));
        }
    }
}
