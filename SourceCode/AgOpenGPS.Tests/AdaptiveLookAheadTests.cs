using NUnit.Framework;

namespace AgOpenGPS.Tests
{
    public class AdaptiveLookAheadTests
    {
        [Test]
        public void OnAStraightWithAQuickSteeringItKeepsTheSettings()
        {
            var a = new CAdaptiveLookAhead();
            Assert.That(a.Adapt(0, 5, 2.5, 0.15, 0, 0, true), Is.EqualTo(5).Within(1e-9));
        }

        [Test]
        public void ATightCurveShortensItButNotBelowTheDeadTimeFloor()
        {
            var a = new CAdaptiveLookAhead();
            double m = a.Adapt(0, 5, 2.5, 0.15, 1 / 12.0, 0, true);
            Assert.That(m, Is.LessThan(5));
            Assert.That(m, Is.GreaterThanOrEqualTo(CAdaptiveLookAhead.DeadTimeFloorM(2.5, 0.15) - 1e-9));
        }

        [Test]
        public void ASlowSteeringLengthensAShortSetting()
        {
            var a = new CAdaptiveLookAhead();
            double m = a.Adapt(0, 3, 4, 0.6, 0, 0, true);
            Assert.That(m, Is.GreaterThan(3));
        }

        [Test]
        public void TheDeadTimeFloorHoldsAboveTheWeavingCap()
        {
            // a short setting, fast, on a slow steering: the floor is far above 1.35 × the setting
            var a = new CAdaptiveLookAhead();
            double m = a.Adapt(0, 3, 3.9, 0.6, 0, 0, true);
            Assert.That(m, Is.EqualTo(CAdaptiveLookAhead.DeadTimeFloorM(3.9, 0.6)).Within(1e-9));
            Assert.That(m, Is.GreaterThan(3 * a.MaxWeaveFactor));
        }

        [Test]
        public void WeavingAcrossTheLineLengthensItAndCalmItComesBack()
        {
            var a = new CAdaptiveLookAhead();
            double t = 0, m = 0;
            for (int i = 0; i < 100; i++, t += 0.1) m = a.Adapt(t, 5, 2.5, 0.15, 0, (i / 20) % 2 == 0 ? 0.05 : -0.05, true);
            Assert.That(m, Is.GreaterThan(5.5));
            for (int i = 0; i < 1200; i++, t += 0.1) m = a.Adapt(t, 5, 2.5, 0.15, 0, 0, true);
            Assert.That(m, Is.EqualTo(5).Within(0.05));
        }

        [Test]
        public void ItChangesSmoothly()
        {
            var a = new CAdaptiveLookAhead();
            a.Adapt(0, 5, 2.5, 0.15, 0, 0, true);
            double m = a.Adapt(0.1, 5, 2.5, 0.15, 1 / 8.0, 0, true);
            Assert.That(5 - m, Is.LessThanOrEqualTo(CAdaptiveLookAhead.ChangePerS * 0.1 + 1e-9));
        }
    }
}
