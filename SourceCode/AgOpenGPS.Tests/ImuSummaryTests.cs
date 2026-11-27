using NUnit.Framework;

namespace AgOpenGPS.Tests
{
    public class ImuSummaryTests
    {
        [Test]
        public void ItReadsWhatTheModuleSends()
        {
            byte[] pgn = CImuSummary.Build(-12.34, 3.21, 2.5, 4.1, 3.9, -27.5, 271.3, 0.098, 10, 1);
            Assert.That(pgn.Length, Is.EqualTo(24));
            // the crc as AgOpenGPS checks every PGN: the sum from the third byte
            byte crc = 0;
            for (int j = 2; j < pgn.Length - 1; j++) crc += pgn[j];
            Assert.That(pgn[pgn.Length - 1], Is.EqualTo(crc));

            var s = new CImuSummary();
            Assert.That(s.Parse(pgn, 100), Is.True);
            Assert.That(s.YawRateDegS, Is.EqualTo(-12.34).Within(0.005));
            Assert.That(s.RollAvgDeg, Is.EqualTo(3.21).Within(0.005));
            Assert.That(s.RollMinDeg, Is.EqualTo(2.5).Within(0.005));
            Assert.That(s.RollMaxDeg, Is.EqualTo(4.1).Within(0.005));
            Assert.That(s.RollLastDeg, Is.EqualTo(3.9).Within(0.005));
            Assert.That(s.RollRatePeakDegS, Is.EqualTo(-27.5).Within(0.05));
            Assert.That(s.HeadingDeg, Is.EqualTo(271.3).Within(0.05));
            Assert.That(s.SpanS, Is.EqualTo(0.098).Within(0.0005));
            Assert.That(s.Samples, Is.EqualTo(10));
            Assert.That(s.ImuType, Is.EqualTo(1));
            Assert.That(s.RateHz, Is.EqualTo(102).Within(1));

            // fresh for the fix it came with, not for one after a gap
            Assert.That(s.Fresh(100.1), Is.True);
            Assert.That(s.Fresh(100.5), Is.False);
            Assert.That(s.Fresh(99.9), Is.False);
        }

        [Test]
        public void ItLeavesOtherPgnsAlone()
        {
            var s = new CImuSummary();
            byte[] pgn = CImuSummary.Build(1, 0, 0, 0, 0, 0, 10, 0.1, 10, 2);
            pgn[3] = 0xD3;
            Assert.That(s.Parse(pgn, 1), Is.False);
            Assert.That(s.Parse(new byte[10], 1), Is.False);
            Assert.That(s.Seen, Is.False);
        }

        [Test]
        public void TheRollIsTurnedAndZeroedAsTheSettingsSay()
        {
            Assert.That(CImuSummary.AsRoll(3, false, 0.5), Is.EqualTo(2.5));
            Assert.That(CImuSummary.AsRoll(3, true, 0.5), Is.EqualTo(-3.5));
        }

        [Test]
        public void PitModeSeesAJoltBetweenTwoFixes()
        {
            // a short jolt: at 10 Hz the roll is back almost where it was (0.3 degree), the summary saw it at 20 deg/s
            var only = new CPitGuard();
            only.Update(0, 0, 0, true, 4);
            only.Update(0.1, 0.3, 0.05, true, 4);
            Assert.That(only.Holding(0.1), Is.False);

            var summed = new CPitGuard();
            summed.Update(0, 0, 0, true, 4, double.NaN);
            summed.Update(0.1, 0.3, 0.05, true, 4, 20);
            Assert.That(summed.Holding(0.1), Is.True);

            // a jolt the compensation took out of the position: still nothing
            var compensated = new CPitGuard();
            compensated.Update(0, 0, 0, true, 4, double.NaN);
            compensated.Update(0.1, 0.3, 0.01, true, 4, 20);
            Assert.That(compensated.Holding(0.1), Is.False);
        }
    }
}
