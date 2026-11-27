using System;

namespace AgOpenGPS
{
    /// <summary>
    /// The heading at the start carried by the IMU: the tablet was off but the module (with its IMU) went on running
    /// on the tractor's power - driven home to the field, turned on the yard. The IMU counted every turn meanwhile, so
    /// the heading now is the one saved plus the IMU's turn since, wherever the tractor stands. Only when the module
    /// was not switched off in between (its time since switched on went on as the clock did): after a restart the
    /// IMU starts its heading again from nothing.
    /// </summary>
    public static class CForwardCarry
    {
        public const double ClockToleranceS = 120;   // the tablet's clock and the module's may differ this much
        public const double MaxHoursAway = 4;        // the IMU's drift: longer than this, not trusted

        /// <summary>The heading now (degrees, 0..360) or NaN when the IMU cannot be trusted to have carried it.</summary>
        public static double HeadingDeg(double savedHeadingDeg, double savedImuDeg, double savedModuleMs, double savedUtcS,
            double imuNowDeg, double moduleNowMs, double utcNowS)
        {
            if (savedHeadingDeg < 0 || savedImuDeg < 0 || savedModuleMs < 0 || moduleNowMs < 0 || savedUtcS <= 0) return double.NaN;
            if (double.IsNaN(imuNowDeg) || imuNowDeg < 0 || imuNowDeg > 360) return double.NaN;
            // the module restarted (its time began again), or a different module
            if (moduleNowMs <= savedModuleMs) return double.NaN;
            double moduleS = (moduleNowMs - savedModuleMs) / 1000;
            double clockS = utcNowS - savedUtcS;
            if (clockS < -ClockToleranceS || Math.Abs(moduleS - clockS) > ClockToleranceS) return double.NaN;
            if (moduleS > MaxHoursAway * 3600) return double.NaN;

            double h = savedHeadingDeg + (imuNowDeg - savedImuDeg);
            h %= 360;
            if (h < 0) h += 360;
            return h;
        }

        /// <summary>Seconds since 1970 (UTC), as saved.</summary>
        public static double UtcNowS() => (DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
    }
}
