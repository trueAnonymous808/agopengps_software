using NUnit.Framework;
using System.Collections.Generic;

namespace AgOpenGPS.Tests
{
    public class PassTrackerTests
    {
        // drives along a line northwards at 2.5 m/s, off it by the given distance
        private static PassResult Drive(CPassTracker p, ref double t, ref double n, double seconds, string line, string method, double off)
        {
            PassResult done = null;
            for (double end = t + seconds; t < end; t += 0.1, n += 0.25)
                done = p.Feed(t, true, line, method, 0, n, off, true) ?? done;
            return done;
        }

        [Test]
        public void APassEndsWhenTheLineOrMethodChangesAndTellsHowItWent()
        {
            var p = new CPassTracker();
            double t = 0, n = 0;
            Assert.That(Drive(p, ref t, ref n, 40, "3+", "MPC", 0.02), Is.Null);
            PassResult r = Drive(p, ref t, ref n, 1, "3-", "MPC", 0);   // turned onto the next pass
            Assert.That(r, Is.Not.Null);
            Assert.That(r.Method, Is.EqualTo("MPC"));
            Assert.That(r.RmsCm, Is.EqualTo(2).Within(0.01));
            Assert.That(r.LengthM, Is.EqualTo(100).Within(1));
        }

        [Test]
        public void AShortBreakDoesNotEndItAndAShortPassIsNotTold()
        {
            var p = new CPassTracker();
            double t = 0, n = 0;
            Drive(p, ref t, ref n, 10, "1+", "Pure pursuit", 0.01);
            for (double end = t + 1; t < end; t += 0.1) Assert.That(p.Feed(t, false, "", "", 0, n, 0, false), Is.Null);
            Drive(p, ref t, ref n, 1, "1+", "Pure pursuit", 0.01);
            PassResult r = null;
            for (double end = t + 5; t < end; t += 0.1) r = p.Feed(t, false, "", "", 0, n, 0, false) ?? r;
            Assert.That(r, Is.Null, "30 m is not reached: 11 s × 2.5 m/s");
        }

        [Test]
        public void TheSummaryWeighsThePassesByLength()
        {
            var sum = CPassTracker.Summary(new List<PassResult>
            {
                new PassResult { Method = "A", RmsCm = 1, LengthM = 300 },
                new PassResult { Method = "A", RmsCm = 3, LengthM = 100 },
                new PassResult { Method = "B", RmsCm = 2, LengthM = 100 },
            });
            Assert.That(sum["A"].Passes, Is.EqualTo(2));
            Assert.That(sum["A"].RmsCm, Is.EqualTo(System.Math.Sqrt((1 * 300 + 9 * 100) / 400.0)).Within(1e-9));
            Assert.That(sum["B"].RmsCm, Is.EqualTo(2).Within(1e-9));
        }
    }
}
