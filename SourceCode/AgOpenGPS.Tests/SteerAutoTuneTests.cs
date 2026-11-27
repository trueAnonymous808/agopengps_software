using NUnit.Framework;
using System;

namespace AgOpenGPS.Tests
{
    public class SteerAutoTuneTests
    {
        // the wanted angle: held steps of 5 s, alternating +3° / -3°
        private static double Wanted(double t) => ((int)(t / 5)) % 2 == 0 ? 3 : -3;

        private static CMotorTune Motor(Func<double, double> reported)
        {
            var tune = new CMotorTune();
            for (double t = 0; t < 60; t += 0.1) tune.AddSample(t, Wanted(t), reported(t), 6, true);
            return tune;
        }

        [Test]
        public void Motor_AWellTunedMotorIsLeftAlone()
        {
            // follows within a fifth of a second
            CMotorTune tune = Motor(t => Wanted(t - 0.2));
            Assert.That(tune.Recommend(15, 25, out int kp, out int pwm), Is.EqualTo(TuneVerdict.Good));
            Assert.That(kp, Is.EqualTo(15));
            Assert.That(pwm, Is.EqualTo(25));
        }

        [Test]
        public void Motor_SwingingRoundTheTargetLowersTheGain()
        {
            CMotorTune tune = Motor(t => Wanted(t) + 0.8 * Math.Sin(2 * Math.PI * 1.5 * t));
            Assert.That(tune.Recommend(40, 25, out int kp, out int pwm), Is.EqualTo(TuneVerdict.Oscillates));
            Assert.That(kp, Is.EqualTo(34));
            Assert.That(pwm, Is.EqualTo(25));
        }

        [Test]
        public void Motor_StoppingShortRaisesTheMinimumPwm()
        {
            CMotorTune tune = Motor(t => Wanted(t - 0.2) * (2.0 / 3));   // stops a degree short
            Assert.That(tune.Recommend(15, 25, out int kp, out int pwm), Is.EqualTo(TuneVerdict.DeadBand));
            Assert.That(pwm, Is.EqualTo(30));
            Assert.That(kp, Is.EqualTo(15));
        }

        [Test]
        public void Motor_FollowingLateRaisesTheGain()
        {
            CMotorTune tune = Motor(t => Wanted(t - 1.0));
            Assert.That(tune.Recommend(20, 25, out int kp, out int pwm), Is.EqualTo(TuneVerdict.Slow));
            Assert.That(kp, Is.EqualTo(23));
        }

        [Test]
        public void Motor_NothingWithoutSteering()
        {
            var tune = new CMotorTune();
            for (double t = 0; t < 60; t += 0.1) tune.AddSample(t, Wanted(t), Wanted(t), 6, false);
            Assert.That(tune.Recommend(15, 25, out _, out _), Is.EqualTo(TuneVerdict.Collecting));
        }

        private static CLookAheadTune Line(Func<double, double> offAt, double metres = 300)
        {
            var tune = new CLookAheadTune();
            for (double s = 0; s < metres; s += 0.15) tune.AddSample(0, s, offAt(s), true);
            return tune;
        }

        [Test]
        public void LookAhead_AWeaveWantsALongerLookAhead()
        {
            CLookAheadTune tune = Line(s => 0.05 * Math.Sin(2 * Math.PI * s / 10));
            Assert.That(tune.Recommend(4.0, 1, 7, out double hold), Is.EqualTo(TuneVerdict.Weaves));
            Assert.That(hold, Is.EqualTo(4.4).Within(1e-9));
        }

        [Test]
        public void LookAhead_StayingOffTheLineWantsAShorterOne()
        {
            CLookAheadTune tune = Line(s => 0.06 + 0.005 * Math.Sin(s));
            Assert.That(tune.Recommend(4.0, 1, 7, out double hold), Is.EqualTo(TuneVerdict.ReturnsSlowly));
            Assert.That(hold, Is.EqualTo(3.6).Within(1e-9));
        }

        [Test]
        public void LookAhead_ATightLineIsLeftAlone()
        {
            CLookAheadTune tune = Line(s => 0.008 * Math.Sin(2 * Math.PI * s / 10));
            Assert.That(tune.Recommend(4.0, 1, 7, out double hold), Is.EqualTo(TuneVerdict.Good));
            Assert.That(hold, Is.EqualTo(4.0));
        }

        [Test]
        public void LookAhead_StaysWithinTheLimits()
        {
            CLookAheadTune tune = Line(s => 0.05 * Math.Sin(2 * Math.PI * s / 10));
            tune.Recommend(6.9, 1, 7, out double hold);
            Assert.That(hold, Is.EqualTo(7));
        }

        [Test]
        public void LookAhead_NeedsAHundredMetres()
        {
            CLookAheadTune tune = Line(s => 0.05 * Math.Sin(2 * Math.PI * s / 10), 60);
            Assert.That(tune.Recommend(4.0, 1, 7, out _), Is.EqualTo(TuneVerdict.Collecting));
        }

        private static CSideHillTune Slope(double needPerRoll, double rollAmplitude, double metres = 600)
        {
            var tune = new CSideHillTune();
            for (double s = 0; s < metres; s += 0.15)
            {
                double roll = rollAmplitude * Math.Sin(s / 20);
                tune.AddSample(0, s, roll, needPerRoll * roll + 0.2, 0.01, true);
            }
            return tune;
        }

        [Test]
        public void SideHill_TheWheelAnglePerRollGivesTheCompensation()
        {
            // holding the line needs -0.3° of wheel per degree of roll: compensation 0.30
            CSideHillTune tune = Slope(-0.3, 4);
            Assert.That(tune.Recommend(0, 0, 1, out double factor), Is.EqualTo(TuneVerdict.Recommend));
            Assert.That(factor, Is.EqualTo(0.3).Within(1e-9));
        }

        [Test]
        public void SideHill_AlreadyRightIsLeftAlone()
        {
            CSideHillTune tune = Slope(-0.3, 4);
            Assert.That(tune.Recommend(0.3, 0, 1, out double factor), Is.EqualTo(TuneVerdict.Good));
        }

        [Test]
        public void SideHill_FlatGroundTellsNothing()
        {
            CSideHillTune tune = Slope(-0.3, 0.5);   // hardly any roll: no slope to learn from
            Assert.That(tune.Recommend(0, 0, 1, out _), Is.EqualTo(TuneVerdict.Collecting));
        }

        [Test]
        public void SideHill_OnlyWhileHoldingTheLineStraight()
        {
            var tune = new CSideHillTune();
            for (double s = 0; s < 600; s += 0.15)
                tune.AddSample(0, s, 4 * Math.Sin(s / 20), 1, 0.2, true);   // 20 cm off the line
            Assert.That(tune.Count, Is.EqualTo(0));
        }
    }
}
