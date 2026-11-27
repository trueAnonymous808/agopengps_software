using NUnit.Framework;
using System;
using System.Collections.Generic;

namespace AgOpenGPS.Tests
{
    public class SteerAutoTune4Tests
    {
        // a steering with a known dead time, speed limit and play, asked to follow a wandering angle
        private static CSteerDynamics Measure(double delayS, double rateDegS, double playDeg, double seconds = 170)
        {
            var d = new CSteerDynamics();
            const double dt = 0.1;
            var queue = new Queue<double>();
            int delaySteps = (int)Math.Round(delayS / dt);
            for (int i = 0; i < delaySteps; i++) queue.Enqueue(0);
            double wheels = 0;
            for (double t = 0; t < seconds; t += dt)
            {
                double asked = 6 * Math.Sin(0.35 * t) + 2.5 * Math.Sin(1.1 * t + 1);
                queue.Enqueue(asked);
                double target = queue.Dequeue();
                double gap = target - wheels;
                if (Math.Abs(gap) > playDeg / 2)
                {
                    double want = target - Math.Sign(gap) * playDeg / 2;
                    wheels += Math.Max(-rateDegS * dt, Math.Min(rateDegS * dt, want - wheels));
                }
                d.AddSample(t, asked, wheels, true);
            }
            d.Analyze();
            return d;
        }

        [Test]
        public void Dynamics_FindsTheDeadTimeAndThePlay()
        {
            CSteerDynamics d = Measure(0.3, 40, 1.2);
            Assert.That(d.Ready, Is.True);
            Assert.That(d.DelayS, Is.EqualTo(0.3).Within(0.08));
            Assert.That(d.BacklashDeg, Is.EqualTo(1.2).Within(0.4));
            Assert.That(d.MuchPlay, Is.True);
            Assert.That(d.SlowAnswer, Is.False);
        }

        [Test]
        public void Dynamics_AGoodSteeringHasNothingToReport()
        {
            CSteerDynamics d = Measure(0.1, 60, 0.1);
            Assert.That(d.DelayS, Is.EqualTo(0.1).Within(0.08));
            Assert.That(d.BacklashDeg, Is.LessThan(0.5));
            Assert.That(d.MuchPlay || d.SlowAnswer || d.SlowMotor, Is.False);
        }

        [Test]
        public void Dynamics_TellsNothingBeforeEnoughMovement()
        {
            var d = new CSteerDynamics();
            for (double t = 0; t < 20; t += 0.1) d.AddSample(t, 1, 1, true);
            d.Analyze();
            Assert.That(d.Ready, Is.False);
        }

        [Test]
        public void Lead_RunsAheadOfAChangingAngleOnly()
        {
            var lead = new CLeadComp();
            double outDeg = 0;
            for (double t = 0; t < 2; t += 0.1) outDeg = lead.Apply(t, 5 * t, 0.3, true);   // 5 deg/s
            Assert.That(outDeg, Is.EqualTo(5 * 1.9 + 5 * 0.3).Within(0.3));
            for (double t = 2; t < 4; t += 0.1) outDeg = lead.Apply(t, 10, 0.3, true);       // steady
            Assert.That(outDeg, Is.EqualTo(10).Within(0.1));
            Assert.That(lead.Apply(4, 10, 0.3, false), Is.EqualTo(10));                         // off: as asked
        }

        // ten minutes before with one spread, then the trial with another
        private static ChangeVerdict Trial(double beforeCm, double afterCm, double afterSeconds, out double b, out double a)
        {
            var v = new CChangeVerifier();
            var rnd = new Random(3);
            double t = 0;
            for (; t < 600; t += 0.1) v.Feed(t, (rnd.NextDouble() - 0.5) * 2 * beforeCm / 100 * Math.Sqrt(3), 8, false, true);
            v.Begin(t, "test");
            ChangeVerdict last = ChangeVerdict.Pending;
            b = a = double.NaN;
            for (double end = t + afterSeconds; t < end; t += 0.1)
            {
                v.Feed(t, (rnd.NextDouble() - 0.5) * 2 * afterCm / 100 * Math.Sqrt(3), 8, false, true);
                if (Math.Abs(t % 10) < 0.05) { last = v.Evaluate(t, out b, out a); if (last != ChangeVerdict.Pending) return last; }
            }
            return v.Evaluate(t, out b, out a);
        }

        [Test]
        public void Verifier_UndoesAClearlyWorseChange()
        {
            Assert.That(Trial(3, 5, 400, out double b, out double a), Is.EqualTo(ChangeVerdict.Worse));
            Assert.That(b, Is.EqualTo(3).Within(0.3));
            Assert.That(a, Is.EqualTo(5).Within(0.4));
        }

        [Test]
        public void Verifier_KeepsABetterOrEqualChange()
        {
            Assert.That(Trial(4, 2.5, 400, out _, out _), Is.EqualTo(ChangeVerdict.Better));
            Assert.That(Trial(3, 3.05, 400, out _, out _), Is.EqualTo(ChangeVerdict.Same));
        }

        [Test]
        public void Verifier_WaitsForComparableDriving()
        {
            var v = new CChangeVerifier();
            double t = 0;
            for (; t < 600; t += 0.1) v.Feed(t, 0.03, 8, false, true);    // before: 8 km/h straight
            v.Begin(t, "test");
            for (double end = t + 300; t < end; t += 0.1) v.Feed(t, 0.08, 14, false, true);   // after: only fast
            Assert.That(v.Evaluate(t, out _, out _), Is.EqualTo(ChangeVerdict.Pending));
            Assert.That(v.Evaluate(t + 1800, out _, out _), Is.EqualTo(ChangeVerdict.Unclear));
        }

        [Test]
        public void Sim_ATooShortLookAheadWithDeadTimeIsWorse()
        {
            var good = new CSteerSim.Look { Hold = 4, Mult = 1.4, Acquire = 1.5 };
            var tooShort = new CSteerSim.Look { Hold = 1.2, Mult = 0.5, Acquire = 1.5 };
            // a slow steering with a long dead time: a short look-ahead weaves
            bool ok = CSteerSim.NotWorse(good, tooShort, 3, 10, 0.5, 15, 1, 35, out double goodCm, out double shortCm);
            Assert.That(ok, Is.False);
            Assert.That(shortCm, Is.GreaterThan(goodCm));
            Assert.That(CSteerSim.NotWorse(good, good, 3, 10, 0.5, 15, 1, 35, out _, out _), Is.True);
        }

        [Test]
        public void TrackGap_CorrectsTowardsWhereTheTractorReallyRuns()
        {
            // the tractor really runs d to the right of where the program puts it (its own frame)
            const double d = 0.10;
            // pass one, heading north: the program holds x = 0, the tractor is at x = +d, the mark is left there
            double markX = +d;
            // pass back, heading south on the same line: the tractor's right is west, so it is at x = -d
            double tractorX = -d;
            // seen from the seat facing south (0, -1): the mark's side by the cross product (positive: left)
            double cross = 0 * 0 - (-1) * (markX - tractorX);
            bool markOnLeft = cross > 0;
            double gapCm = Math.Abs(markX - tractorX) * 100;

            Assert.That(markOnLeft, Is.True);
            Assert.That(gapCm, Is.EqualTo(20).Within(1e-9));
            // a larger antenna offset moves the program's position to the right: it must grow by d
            Assert.That(TrackGap.Correction(gapCm, markOnLeft), Is.EqualTo(d).Within(1e-9));
            Assert.That(TrackGap.Correction(gapCm, false), Is.EqualTo(-d).Within(1e-9));
        }

        [Test]
        public void ToolTunes_ReadBackWhatWasWritten()
        {
            var map = new Dictionary<string, string> { ["Plough 5"] = "50;25;110", ["Seeder"] = "60;20;105" };
            Dictionary<string, string> back = ToolTunes.Read(ToolTunes.Write(map));
            Assert.That(back.Count, Is.EqualTo(2));
            Assert.That(back["Plough 5"], Is.EqualTo("50;25;110"));
            Assert.That(ToolTunes.Read("").Count, Is.EqualTo(0));
        }
    }
}
