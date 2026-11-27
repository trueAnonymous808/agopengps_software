using NUnit.Framework;

namespace AgOpenGPS.Tests
{
    public class DeadZoneSpeedTests
    {
        [Test]
        public void TheDeadZoneShrinksWithTheSquareOfTheSpeed()
        {
            // as set up to 10 km/h (working speed), then the same sideways drift at any speed
            Assert.That(CVehicleDeadZone.SpeedFactor(0), Is.EqualTo(1));
            Assert.That(CVehicleDeadZone.SpeedFactor(8), Is.EqualTo(1));
            Assert.That(CVehicleDeadZone.SpeedFactor(10), Is.EqualTo(1));
            Assert.That(CVehicleDeadZone.SpeedFactor(20), Is.EqualTo(0.25).Within(1e-12));
            Assert.That(CVehicleDeadZone.SpeedFactor(40), Is.EqualTo(1.0 / 16).Within(1e-12));
            Assert.That(CVehicleDeadZone.SpeedFactor(-20), Is.EqualTo(0.25).Within(1e-12));
        }
    }
}
