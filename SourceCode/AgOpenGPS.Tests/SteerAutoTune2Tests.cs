using NUnit.Framework;
using System;

namespace AgOpenGPS.Tests
{
    public class SteerAutoTune2Tests
    {
        // ---- roll zero ----

        // passes 6 m apart, driven north and south in turn over gently rolling ground; the sensor's zero is 0.6° off
        private static CRollZeroTune Field(double zeroError)
        {
            var tune = new CRollZeroTune();
            for (int pass = 0; pass < 20; pass++)
            {
                double east = pass * 6;
                bool north = pass % 2 == 0;
                double heading = north ? 0 : Math.PI;
                tune.AddSample(0, 0, 0, 0, false);   // a turn between the passes
                for (double s = 0; s <= 200; s += 0.5)
                {
                    double y = north ? s : 200 - s;
                    double slope = 3 * Math.Sin(east / 200 + y / 300);    // the ground's roll for a tractor heading north
                    double roll = (north ? slope : -slope) + zeroError;
                    tune.AddSample(east, y, heading, roll, true);
                }
            }
            return tune;
        }

        [Test]
        public void RollZero_TheErrorComesOutOfPassesBothWays()
        {
            CRollZeroTune tune = Field(0.6);
            Assert.That(tune.Recommend(0, out double zero), Is.EqualTo(TuneVerdict.RollZeroOff));
            Assert.That(zero, Is.EqualTo(0.6).Within(0.1));
        }

        [Test]
        public void RollZero_ARightZeroIsLeftAlone()
        {
            CRollZeroTune tune = Field(0);
            Assert.That(tune.Recommend(0.3, out double zero), Is.EqualTo(TuneVerdict.Good));
            Assert.That(zero, Is.EqualTo(0.3));
        }

        [Test]
        public void RollZero_OneWayOnlyTellsNothing()
        {
            var tune = new CRollZeroTune();
            for (double s = 0; s <= 400; s += 0.5) tune.AddSample(0, s, 0, 2.0, true);
            Assert.That(tune.Recommend(0, out _), Is.EqualTo(TuneVerdict.Collecting));
        }

        // ---- acquire ----

        // comes onto the line from 60 cm off, again and again: closing over 'approachM', then 'overshootM' past it
        private static CAcquireTune Approaches(double approachM, double overshootM)
        {
            var tune = new CAcquireTune();
            double s = 0;
            for (int i = 0; i < 6; i++)
            {
                for (double d = 0; d < 5; d += 0.5, s += 0.5) tune.AddSample(0, s, 0.6, true);
                for (double d = 0; d < approachM; d += 0.5, s += 0.5) tune.AddSample(0, s, 0.4 * (1 - d / approachM), true);
                for (double d = 0; d < 10; d += 0.5, s += 0.5) tune.AddSample(0, s, -overshootM * Math.Sin(Math.PI * d / 10), true);
                for (double d = 0; d < 20; d += 0.5, s += 0.5) tune.AddSample(0, s, 0, true);
            }
            return tune;
        }

        [Test]
        public void Acquire_ShootingPastWantsABiggerFactor()
        {
            CAcquireTune tune = Approaches(10, 0.15);
            Assert.That(tune.Recommend(0.75, 0.2, 3, out double factor), Is.EqualTo(TuneVerdict.AcquireOvershoots));
            Assert.That(factor, Is.EqualTo(0.8).Within(1e-9));
        }

        [Test]
        public void Acquire_CreepingOnWantsASmallerFactor()
        {
            CAcquireTune tune = Approaches(40, 0);
            Assert.That(tune.Recommend(0.75, 0.2, 3, out double factor), Is.EqualTo(TuneVerdict.AcquireSlow));
            Assert.That(factor, Is.EqualTo(0.7).Within(1e-9));
        }

        [Test]
        public void Acquire_ACleanApproachIsLeftAlone()
        {
            CAcquireTune tune = Approaches(12, 0.01);
            Assert.That(tune.Recommend(0.75, 0.2, 3, out _), Is.EqualTo(TuneVerdict.Good));
        }

        // ---- integral ----

        private static CIntegralTune Straight(Func<double, double> off)
        {
            var tune = new CIntegralTune();
            for (double s = 0; s < 250; s += 0.15) tune.AddSample(0, s, off(s), true);
            return tune;
        }

        [Test]
        public void Integral_StayingToOneSideWantsMore()
        {
            CIntegralTune tune = Straight(s => 0.04 + 0.005 * Math.Sin(s));
            Assert.That(tune.Recommend(0, 0, 1, out double gain), Is.EqualTo(TuneVerdict.StaysToOneSide));
            Assert.That(gain, Is.EqualTo(0.1).Within(1e-9));
            tune.Recommend(0.2, 0, 1, out gain);
            Assert.That(gain, Is.EqualTo(0.25).Within(1e-9));
        }

        [Test]
        public void Integral_ASlowWaveWantsLess()
        {
            CIntegralTune tune = Straight(s => 0.05 * Math.Sin(2 * Math.PI * s / 40));
            Assert.That(tune.Recommend(0.3, 0, 1, out double gain), Is.EqualTo(TuneVerdict.IntegralWaves));
            Assert.That(gain, Is.EqualTo(0.24).Within(1e-9));
        }

        [Test]
        public void Integral_ATightLineIsLeftAlone()
        {
            CIntegralTune tune = Straight(s => 0.005 * Math.Sin(s));
            Assert.That(tune.Recommend(0.3, 0, 1, out _), Is.EqualTo(TuneVerdict.Good));
        }

        // ---- U-turn ----

        private static CUTurnTune Turns(Func<double, double> after, int count = 5)
        {
            var tune = new CUTurnTune();
            double s = 0;
            for (int i = 0; i < count; i++)
            {
                for (int k = 0; k < 10; k++) tune.AddSample(0, s, 0, true, false);
                for (double d = 0; d <= 25; d += 0.5) tune.AddSample(0, s += 0.5, after(d), false, true);
            }
            return tune;
        }

        [Test]
        public void UTurn_ShootingAcrossWantsLessCompensation()
        {
            CUTurnTune tune = Turns(d => 0.1 - 0.25 * Math.Sin(Math.PI * Math.Min(d, 20) / 20));
            Assert.That(tune.Recommend(1.0, 0.2, 2, out double comp), Is.EqualTo(TuneVerdict.TurnOvershoots));
            Assert.That(comp, Is.EqualTo(0.9).Within(1e-9));
        }

        [Test]
        public void UTurn_EndingWideWantsMore()
        {
            CUTurnTune tune = Turns(d => 0.3 * Math.Exp(-d / 15));
            Assert.That(tune.Recommend(1.0, 0.2, 2, out double comp), Is.EqualTo(TuneVerdict.TurnEndsWide));
            Assert.That(comp, Is.EqualTo(1.1).Within(1e-9));
        }

        [Test]
        public void UTurn_NeedsSeveralTurns()
        {
            CUTurnTune tune = Turns(d => 0.3 * Math.Exp(-d / 15), 3);
            Assert.That(tune.Recommend(1.0, 0.2, 2, out _), Is.EqualTo(TuneVerdict.Collecting));
        }
    }
}
