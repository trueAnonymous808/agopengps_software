using System;

namespace AgOpenGPS
{
    /// <summary>
    /// The steering's dead zone by the speed. Within the dead zone the angle is no longer sent and the wheels keep the
    /// one they have - fine at working speed, where a tenth of a degree moves the tractor sideways only slowly. How far
    /// a held angle error carries it sideways grows with the square of the speed (the heading turns with the speed,
    /// the position with the speed again): at 40 km/h sixteen times as far as at 10. So above the speed the dead zone is
    /// set for it shrinks with the square of the speed: the same drift at any speed.
    /// </summary>
    public static class CVehicleDeadZone
    {
        public const double ReferenceKmh = 10;   // the dead zone as set holds up to this speed

        /// <summary>The share of the dead zone that holds at this speed (1 up to the reference speed).</summary>
        public static double SpeedFactor(double speedKmh)
        {
            double v = Math.Abs(speedKmh);
            if (v <= ReferenceKmh) return 1;
            double r = ReferenceKmh / v;
            return r * r;
        }
    }
}
