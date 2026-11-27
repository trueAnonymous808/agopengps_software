using NUnit.Framework;

namespace AgOpenGPS.Tests
{
    public class SteerAutoGuardTests
    {
        private static SteerBaseline Start() => new SteerBaseline
        {
            Kp = 20,
            MinPwm = 25,
            Cpd = 100,
            Ackermann = 100,
            WasOffset = 0,
            DeadZoneHeading = 10,
            DeadZoneDelay = 5,
            Hold = 4,
            SpeedFactor = 2,
            SideHill = 0.2
        };

        [Test]
        public void Baseline_SurvivesSavingAndLoading()
        {
            SteerBaseline b = SteerBaseline.Load(Start().Save());
            Assert.That(b, Is.Not.Null);
            Assert.That(b.Kp, Is.EqualTo(20));
            Assert.That(b.Cpd, Is.EqualTo(100));
            Assert.That(b.Hold, Is.EqualTo(4));
            Assert.That(b.SideHill, Is.EqualTo(0.2));
            Assert.That(b.DeadZoneDelay, Is.EqualTo(5));
        }

        [Test]
        public void Baseline_UnreadableTextGivesNone()
        {
            Assert.That(SteerBaseline.Load(""), Is.Null);
            Assert.That(SteerBaseline.Load("1;2;3"), Is.Null);
            Assert.That(SteerBaseline.Load("a;b;c;d;e;f;g;h;i;j"), Is.Null);
        }

        [Test]
        public void Guard_KeepsEveryValueNearItsStart()
        {
            SteerBaseline b = Start();
            Assert.That(CSteerAutoGuard.KpOk(b, 30), Is.True);
            Assert.That(CSteerAutoGuard.KpOk(b, 45), Is.False);    // more than double
            Assert.That(CSteerAutoGuard.KpOk(b, 9), Is.False);     // less than half
            Assert.That(CSteerAutoGuard.CpdOk(b, 125), Is.True);
            Assert.That(CSteerAutoGuard.CpdOk(b, 140), Is.False);
            Assert.That(CSteerAutoGuard.AckermannOk(b, 75), Is.True);
            Assert.That(CSteerAutoGuard.AckermannOk(b, 65), Is.False);
            Assert.That(CSteerAutoGuard.WasOffsetOk(b, 250, 100), Is.True);   // 2.5°
            Assert.That(CSteerAutoGuard.WasOffsetOk(b, 350, 100), Is.False);  // 3.5°
            Assert.That(CSteerAutoGuard.HoldOk(b, 6), Is.True);
            Assert.That(CSteerAutoGuard.HoldOk(b, 6.5), Is.False);
            Assert.That(CSteerAutoGuard.SpeedFactorOk(b, 1.0), Is.True);
            Assert.That(CSteerAutoGuard.SpeedFactorOk(b, 0.9), Is.False);
            Assert.That(CSteerAutoGuard.SideHillOk(b, 0.7), Is.True);
            Assert.That(CSteerAutoGuard.SideHillOk(b, 0.8), Is.False);
            Assert.That(CSteerAutoGuard.DeadZoneOk(b, 100), Is.True);
            Assert.That(CSteerAutoGuard.DeadZoneOk(b, 120), Is.False);
            Assert.That(CSteerAutoGuard.DelayOk(b, 9), Is.True);
            Assert.That(CSteerAutoGuard.DelayOk(b, 10), Is.False);
            Assert.That(CSteerAutoGuard.MinPwmOk(b, 45), Is.True);
            Assert.That(CSteerAutoGuard.MinPwmOk(b, 46), Is.False);
        }
    }
}
