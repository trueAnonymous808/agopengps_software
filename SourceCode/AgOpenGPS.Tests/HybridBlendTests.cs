using NUnit.Framework;

namespace AgOpenGPS.Tests
{
    public class HybridBlendTests
    {
        [Test]
        public void OnAStraightLineLqrSteersAlone()
        {
            var b = new CHybridBlend();
            double share = 0;
            for (double t = 0; t < 10; t += 0.1) share = b.Update(t, 0, 0.02, 0.5);
            Assert.That(share, Is.EqualTo(0));
        }

        [Test]
        public void ATightCurveAheadHandsOverToMpcQuicklyAndBackSlowly()
        {
            var b = new CHybridBlend();
            double t = 0, share = 0;
            for (; t < 0.6; t += 0.1) share = b.Update(t, 1 / 20.0, 0.02, 0.5);
            Assert.That(share, Is.EqualTo(1).Within(1e-9), "MPC within half a second");
            // on a gentle stretch, but not yet calm enough: MPC keeps it (the band between)
            for (double end = t + 2; t < end; t += 0.1) share = b.Update(t, 1 / 80.0, 0.02, 0.5);
            Assert.That(share, Is.EqualTo(1).Within(1e-9));
            // straight and on the line again: back to LQR over a couple of seconds, without a jump
            double before = share;
            share = b.Update(t, 0, 0.02, 0.5);
            Assert.That(before - share, Is.LessThan(0.05));
            for (double end = t + 3; t < end; t += 0.1) share = b.Update(t, 0, 0.02, 0.5);
            Assert.That(share, Is.EqualTo(0).Within(1e-9));
        }

        [Test]
        public void FarOffTheLineMpcBringsItOn()
        {
            var b = new CHybridBlend();
            double share = 0;
            for (double t = 0; t < 1; t += 0.1) share = b.Update(t, 0, 0.6, 0);
            Assert.That(share, Is.EqualTo(1).Within(1e-9));
            Assert.That(CHybridBlend.Blend(2, 6, 0.25), Is.EqualTo(3).Within(1e-9));
            Assert.That(CHybridBlend.Blend(2, double.NaN, 0.5), Is.EqualTo(2));
        }
    }
}
