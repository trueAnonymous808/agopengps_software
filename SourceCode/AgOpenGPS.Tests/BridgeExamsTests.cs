using NUnit.Framework;
using System;
using System.IO;

namespace AgOpenGPS.Tests
{
    public class BridgeExamsTests
    {
        // exams whose error grows 5 cm a minute: about 2.5 / 5 / 10 / 25 cm at the checkpoints
        private static CBridgeExams Grown(int count, string method = "DR-IMU")
        {
            var exams = new CBridgeExams();
            for (int n = 0; n < count; n++)
                for (int i = 0; i < CBridgeExams.Checkpoints.Length; i++)
                    exams.Add(method, i, 1 + CBridgeExams.Checkpoints[i] / 60.0 * 5 * (0.8 + 0.2 * (n % 3)));
            return exams;
        }

        [Test]
        public void Exams_NothingIsTrustedTooEarly()
        {
            CBridgeExams exams = Grown(5);
            Assert.That(exams.Count("DR-IMU"), Is.EqualTo(5));
            Assert.That(double.IsNaN(exams.RateCmPerS("DR-IMU")), Is.True);
            Assert.That(exams.TrustedSeconds("DR-IMU", 10), Is.EqualTo(0));
        }

        [Test]
        public void Exams_GiveTheGrowthAndTheTrustedTime()
        {
            CBridgeExams exams = Grown(30);
            // the 95 % value is the worst third (1.2 times 5 cm/min): 6 cm/min -> 0.1 cm/s
            Assert.That(exams.RateCmPerS("DR-IMU"), Is.EqualTo(0.1).Within(0.002));
            Assert.That(exams.P95("DR-IMU", 1), Is.EqualTo(7).Within(0.01));          // 60 s: 1 + 6
            Assert.That(exams.TrustedSeconds("DR-IMU", 10), Is.EqualTo(60));          // 120 s reaches 13 cm
            Assert.That(exams.TrustedSeconds("DR-IMU", 14), Is.EqualTo(120));
        }

        [Test]
        public void Exams_EachMethodHasItsOwn()
        {
            CBridgeExams exams = Grown(30, "DR-Dual");
            Assert.That(exams.Count("DR-IMU"), Is.EqualTo(0));
            Assert.That(exams.Count("DR-Dual"), Is.EqualTo(30));
        }

        [Test]
        public void Exams_ARealOutageGoesUnderItsLastCheckpoint()
        {
            var exams = new CBridgeExams();
            exams.AddReal("DR-IMU", 95, 4);     // 95 s: under 60 s
            exams.AddReal("DR-IMU", 20, 1);     // shorter than the first checkpoint: not counted
            Assert.That(exams.Count("DR-IMU"), Is.EqualTo(0));   // the count is the 30 s checkpoint
            for (int i = 0; i < 9; i++) exams.AddReal("DR-IMU", 70, 5);
            Assert.That(exams.P95("DR-IMU", 1), Is.EqualTo(5));
        }

        [Test]
        public void Exams_SurviveSavingAndLoading()
        {
            string path = Path.Combine(Path.GetTempPath(), "aog_exams_" + Guid.NewGuid().ToString("N"), "e.txt");
            try
            {
                Grown(25).Save(path);
                var loaded = new CBridgeExams();
                loaded.Load(path);
                Assert.That(loaded.Count("DR-IMU"), Is.EqualTo(25));
                Assert.That(loaded.RateCmPerS("DR-IMU"), Is.EqualTo(Grown(25).RateCmPerS("DR-IMU")).Within(1e-9));
                var none = new CBridgeExams();
                none.Load(path + ".missing");   // a missing file: just empty
                Assert.That(none.Methods().Count, Is.EqualTo(0));
            }
            finally
            {
                try { Directory.Delete(Path.GetDirectoryName(path), true); } catch (IOException) { }
            }
        }

        // ---- the bridge with what the exams measured ----

        [Test]
        public void Bridge_UsesTheMeasuredGrowth()
        {
            var bridge = new CRtkBridge { MaxSeconds = 600, MaxErrorCm = 5, LearnedRateCmPerS = 0.04 };
            double t = 0;
            for (; t < 60; t += 0.1) bridge.Step(t, true, 4, 0, t * 3, 3, 0, double.NaN, 0, 3, out _, out _);
            for (; t < 300 && bridge.Now != CRtkBridge.Phase.Expired; t += 0.1) bridge.Step(t, false, 2, 0, t * 3, 3, 0, double.NaN, 0, 3, out _, out _);
            // 1 + 0.04 cm/s reaches 5 cm at 100 s (the built-in guess for DGPS without an IMU would stop at 13 s)
            Assert.That(bridge.LastBridgeS, Is.EqualTo(100).Within(0.5));
        }

        [Test]
        public void Bridge_WithoutBlendingFollowsOnlyItsOwnReckoning()
        {
            // a virtual outage: the 'degraded' position is the true one, but it must not be blended in
            var bridge = new CRtkBridge { BlendGnss = false, MaxSeconds = 1e9, MaxErrorCm = 1e9 };
            double t = 0;
            for (; t < 30; t += 0.1) bridge.Step(t, true, 4, 0, t * 3, 3, 0, double.NaN, 0, 3, out _, out _);
            double e = 0, n = 0;
            // the heading is off by a steer angle of 1 degree: the reckoning drifts east, the position input does not
            for (double end = t + 60; t < end; t += 0.1) bridge.Step(t, false, 5, 0, t * 3, 3, 0, double.NaN, 1, 3, out e, out n);
            Assert.That(e, Is.GreaterThan(1));   // drifted with its own heading, not pulled back to east = 0
        }
    }
}
