using NUnit.Framework;
using System;
using System.Collections.Generic;

namespace AgOpenGPS.Tests
{
    public class AccuracyReportTests
    {
        // a pass along north at east = x, 200 m, the tractor weaving by the noise (m)
        private static PassResult Pass(double x, double noise, Random rnd, string method = "Pure pursuit")
        {
            var tracker = new CPassTracker();
            PassResult done = null;
            double t = 0;
            for (double n = 0; n <= 200; n += 0.25, t += 0.1)
            {
                double off = (rnd.NextDouble() - 0.5) * 2 * noise;
                done = tracker.Feed(t, true, "1+", method, x + off, n, off, true) ?? done;
            }
            // off the line long enough: the pass ends
            for (int k = 0; k < 40; k++, t += 0.1) done = tracker.Feed(t, false, "", method, 0, 0, 0, false) ?? done;
            return done;
        }

        [Test]
        public void ThePassesKeepTheirTrack()
        {
            PassResult p = Pass(0, 0.02, new Random(1));
            Assert.That(p, Is.Not.Null);
            Assert.That(p.Track.Count, Is.InRange(150, 210));   // a point every metre
        }

        [Test]
        public void ItFindsTheNeighboursAndTheirSpacing()
        {
            var rnd = new Random(2);
            // three passes 6 m apart, the middle one 10 cm too far: +10 cm to the first, -10 cm to the third
            var passes = new List<PassResult> { Pass(0, 0.02, rnd), Pass(6.1, 0.02, rnd), Pass(12, 0.02, rnd) };
            CAccuracyReport.Result r = CAccuracyReport.Analyze(passes, 6);
            Assert.That(r.Pairs.Count, Is.EqualTo(2));
            Assert.That(r.Pairs[0].MeanCm, Is.EqualTo(10).Within(2));
            Assert.That(r.Pairs[1].MeanCm, Is.EqualTo(-10).Within(2));
            Assert.That(r.P2P95Cm, Is.InRange(9, 15));
            Assert.That(r.Line95Cm, Is.LessThan(2.1));
            Assert.That(r.Passes, Is.EqualTo(3));
        }

        [Test]
        public void PassesFarApartAreNoNeighbours()
        {
            var rnd = new Random(3);
            var passes = new List<PassResult> { Pass(0, 0.02, rnd), Pass(20, 0.02, rnd) };
            CAccuracyReport.Result r = CAccuracyReport.Analyze(passes, 6);
            Assert.That(r.Pairs.Count, Is.EqualTo(0));
            Assert.That(r.P2P95Cm, Is.NaN);
        }
    }
}
