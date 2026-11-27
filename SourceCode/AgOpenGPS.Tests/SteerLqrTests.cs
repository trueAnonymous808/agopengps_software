using NUnit.Framework;
using System;
using System.Collections.Generic;

namespace AgOpenGPS.Tests
{
    public class SteerLqrTests
    {
        // a straight line north; the tractor starts beside it; dead time and a turning speed limit; LQR at 10 Hz
        private static double Drive(double startOffM, double deadTimeS, double rateDegS, double speedMs, out double furthestOver)
        {
            const double L = 3, plantDt = 0.01, controlDt = 0.1;
            var lqr = new CSteerLqr();
            var sent = new List<double[]>();
            double AskedAt(double t)
            {
                for (int i = sent.Count - 1; i >= 0; i--) if (sent[i][0] <= t) return sent[i][1];
                return 0;
            }
            double e = startOffM, n = 0, h = 0, wheel = 0, next = 0;
            furthestOver = 0;
            for (double t = 0; t < 30; t += plantDt)
            {
                if (t >= next - 1e-9)
                {
                    next += controlDt;
                    int steps = lqr.DelaySteps(deadTimeS);
                    var chain = new double[steps];
                    for (int i = 0; i < steps; i++) chain[i] = AskedAt(t - (i + 1) * controlDt + 1e-6) * Math.PI / 180;
                    double u = lqr.Steer(e, h, chain, speedMs, L, 0, 35 * Math.PI / 180) * 180 / Math.PI;
                    sent.Add(new[] { t, u });
                }
                double move = AskedAt(t - deadTimeS) - wheel, max = rateDegS * plantDt;
                wheel += Math.Max(-max, Math.Min(max, move));
                h += speedMs * Math.Tan(wheel * Math.PI / 180) / L * plantDt;
                e += speedMs * Math.Sin(h) * plantDt;
                n += speedMs * Math.Cos(h) * plantDt;
                furthestOver = Math.Min(furthestOver, e);
            }
            return e;
        }

        [TestCase(0.15, 30, 2.5)]
        [TestCase(0.6, 12, 2.5)]
        [TestCase(0.6, 12, 4.0)]
        [TestCase(0.15, 30, 16.7)]   // 60 km/h
        [TestCase(0.15, 30, 33.3)]   // 120 km/h: it once swung from side to side
        public void ItComesOntoTheLineQuickAndSlowSteeringAlike(double deadTimeS, double rateDegS, double speedMs)
        {
            double end = Drive(0.5, deadTimeS, rateDegS, speedMs, out double over);
            Assert.That(Math.Abs(end), Is.LessThan(0.02), "still off the line");
            Assert.That(over, Is.GreaterThan(-0.2), "swung far over");
        }

        [Test, Explicit("timing, run by name")]
        public void HowLongWorkingOutTheGainsTakes()
        {
            var lqr = new CSteerLqr();
            var w = System.Diagnostics.Stopwatch.StartNew();
            lqr.Gains(3, 2.5, 6);
            double six = w.Elapsed.TotalMilliseconds;
            w.Restart();
            lqr.Gains(3, 2.5, 15);
            System.IO.File.WriteAllText(System.IO.Path.Combine(TestContext.CurrentContext.WorkDirectory, "lqr-timing.txt"),
                "6 steps: " + six.ToString("0.0") + " ms, 15 steps: " + w.Elapsed.TotalMilliseconds.ToString("0.0") + " ms");
        }

        [Test]
        public void TheGainsAreKeptForTheSameTractorAndSpeed()
        {
            var lqr = new CSteerLqr();
            Assert.That(lqr.Gains(3, 2.5, 3), Is.SameAs(lqr.Gains(3, 2.55, 3)));
            Assert.That(lqr.Gains(3, 2.5, 3)[0], Is.GreaterThan(0), "off to the right: steer left");
        }
    }
}
