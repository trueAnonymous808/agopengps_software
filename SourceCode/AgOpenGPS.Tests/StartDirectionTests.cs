using NUnit.Framework;
using System;

namespace AgOpenGPS.Tests
{
    public class StartDirectionTests
    {
        // ---- carried by the IMU while the tablet was off ----

        [Test]
        public void TheImuCarriesTheHeadingWhenTheModuleRanOn()
        {
            // left facing 90, IMU 200 then; an hour later the module has run an hour, the IMU says 20 (turned -180)
            double h = CForwardCarry.HeadingDeg(90, 200, 1000000, 1000, 20, 1000000 + 3600000, 1000 + 3600);
            Assert.That(h, Is.EqualTo(270).Within(1e-9));
            // round the circle
            Assert.That(CForwardCarry.HeadingDeg(350, 10, 5000, 1000, 40, 65000, 1060), Is.EqualTo(20).Within(1e-9));
        }

        [Test]
        public void NotWhenTheModuleWasSwitchedOff()
        {
            // the module started again (its time is less than then)
            Assert.That(CForwardCarry.HeadingDeg(90, 200, 1000000, 1000, 20, 30000, 1000 + 3600), Is.NaN);
            // switched off and on again and has now run longer than then: its time does not match the clock's
            Assert.That(CForwardCarry.HeadingDeg(90, 200, 60000, 1000, 20, 1800000, 1000 + 7200), Is.NaN);
            // too long ago for the IMU's drift
            Assert.That(CForwardCarry.HeadingDeg(90, 200, 1000, 1000, 20, 1000 + 5 * 3600000.0, 1000 + 5 * 3600), Is.NaN);
            // nothing saved, or no IMU now
            Assert.That(CForwardCarry.HeadingDeg(90, -1, 1000, 1000, 20, 61000, 1060), Is.NaN);
            Assert.That(CForwardCarry.HeadingDeg(90, 200, 1000, 1000, double.NaN, 61000, 1060), Is.NaN);
            Assert.That(CForwardCarry.HeadingDeg(90, 200, -1, 1000, 20, 61000, 1060), Is.NaN);
        }

        [Test]
        public void TheSummaryCarriesTheModulesTimeAndTheAcceleration()
        {
            byte[] pgn = CImuSummary.Build(1, 2, 1, 3, 2, 0, 90, 0.1, 10, 1, 123456789, new[] { 0.25, -9.81, 0.5 });
            Assert.That(pgn.Length, Is.EqualTo(34));
            var s = new CImuSummary();
            Assert.That(s.Parse(pgn, 5), Is.True);
            Assert.That(s.ModuleUptimeMs, Is.EqualTo(123456789));
            Assert.That(s.AccX, Is.EqualTo(0.25).Within(0.005));
            Assert.That(s.AccY, Is.EqualTo(-9.81).Within(0.005));
            Assert.That(s.AccZ, Is.EqualTo(0.5).Within(0.005));

            // the first version (no time, no acceleration) still reads
            var old = new CImuSummary();
            Assert.That(old.Parse(CImuSummary.Build(1, 2, 1, 3, 2, 0, 90, 0.1, 10, 1), 5), Is.True);
            Assert.That(old.ModuleUptimeMs, Is.EqualTo(-1));
            Assert.That(old.HasAccel, Is.False);
        }

        // ---- forwards or backwards at the first move ----

        // the IMU mounted turned round: the tractor's forward is the IMU's -y; gravity mostly on z, a little on y
        // (mounted tilted); the accelerometer's noise 0.05 m/s2
        private sealed class Tractor
        {
            private readonly Random rnd = new Random(11);
            public readonly CStartDirection Dir = new CStartDirection();
            public double T, V;   // m/s, + forwards

            private double Noise() => (rnd.NextDouble() - 0.5) * 0.17;

            public void Drive(double seconds, double accel, bool headingKnown, bool programThinksReverse = false)
            {
                for (double s = 0; s < seconds; s += 0.1)
                {
                    V += accel * 0.1;
                    T += 0.1;
                    double ax = 0.02 + Noise(), ay = -accel - 0.6 + Noise(), az = 9.78 + Noise();
                    bool reverse = headingKnown ? (V < 0) != programThinksReverse : false;
                    Dir.Feed(T, Math.Abs(V) * 3.6, ax, ay, az, headingKnown, reverse);
                }
            }

            public void Learn()
            {
                // a working day's few minutes of speeding up and slowing down between 5 and 12 km/h
                V = 5 / 3.6;
                for (int i = 0; i < 40; i++)
                {
                    Drive(4, 0.5, true);
                    Drive(4, -0.5, true);
                }
            }
        }

        [Test]
        public void ItLearnsHowTheImuSitsAndTellsTheFirstMove()
        {
            var t = new Tractor();
            t.Learn();
            Assert.That(t.Dir.Learnt, Is.True);

            // standing (the program restarted: heading not known), then off backwards
            t.V = 0;
            t.Drive(3, 0, false);
            t.Drive(1.5, -0.4, false);
            Assert.That(t.Dir.FirstMove(t.T), Is.EqualTo(-1));

            // standing again, then off forwards
            t.V = 0;
            t.Drive(3, 0, false);
            t.Drive(1.5, 0.4, false);
            Assert.That(t.Dir.FirstMove(t.T), Is.EqualTo(1));
        }

        [Test]
        public void NotLearntItSaysNothing()
        {
            var t = new Tractor();
            t.Drive(3, 0, false);
            t.Drive(1.5, -0.4, false);
            Assert.That(t.Dir.Learnt, Is.False);
            Assert.That(t.Dir.FirstMove(t.T), Is.EqualTo(0));
        }

        [Test]
        public void AHeadingTheWrongWayRoundTeachesNothing()
        {
            // the program thinks it reverses while the tractor drives forwards: not learnt from
            var t = new Tractor();
            t.V = 5 / 3.6;
            for (int i = 0; i < 40; i++)
            {
                t.Drive(4, 0.5, true, programThinksReverse: true);
                t.Drive(4, -0.5, true, programThinksReverse: true);
            }
            Assert.That(t.Dir.Weight, Is.EqualTo(0));
        }
    }
}
