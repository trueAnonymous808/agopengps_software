using AgLibrary.Logging;
using System;
using System.Collections.Generic;

namespace AgOpenGPS
{
    // Rough ground: the roll compensation on time and with the height that really cancels the antenna's swing, and pit
    // mode - for a moment after a rock the compensation did not take out of the position, the steering does not chase
    // the jump. All of it measured on the tractor; a good IMU set up right just gets "nothing to change".
    public partial class FormGPS
    {
        public readonly CRollTune rollTune = new CRollTune();
        public readonly CPitGuard pitGuard = new CPitGuard();
        private readonly List<double[]> rollHistory = new List<double[]>();   // t, roll as the IMU told it
        private double rollAnalyzedS = -1e9;

        /// <summary>The height factor of the roll compensation (1: the antenna height set).</summary>
        public double RollGain => Math.Max(0.5, Math.Min(1.5, Properties.VehicleSettings.Default.setAS_RollGain));

        /// <summary>The roll compensation's timing (s): + the roll reading arrives late.</summary>
        public double RollShiftS => Math.Max(-0.3, Math.Min(0.3, Properties.VehicleSettings.Default.setAS_RollShiftS));

        public bool PitGuardOn => Properties.Settings.Default.setPitGuard_Enabled;

        public double PitRateDegS => Math.Max(1, Math.Min(15, Properties.VehicleSettings.Default.setAS_PitRateDegS));

        public void SetPitGuard(bool on)
        {
            Properties.Settings.Default.setPitGuard_Enabled = on;
            Properties.Settings.Default.Save();
        }

        // the roll at a time not long ago, between the readings around it
        private bool RollAt(double t, out double roll)
        {
            roll = 0;
            for (int i = rollHistory.Count - 1; i > 0; i--)
            {
                double[] a = rollHistory[i - 1], b = rollHistory[i];
                if (a[0] <= t && t <= b[0])
                {
                    roll = b[0] > a[0] ? a[1] + (b[1] - a[1]) * (t - a[0]) / (b[0] - a[0]) : b[1];
                    return true;
                }
            }
            return false;
        }

        private double summaryRollDeg = double.NaN;
        private long summaryRollTaken = -1;

        /// <summary>
        /// The roll the compensation starts from: with a newer module firmware the average of all the IMU's readings
        /// since the last fix (50-100 of them a second, far less noise than the single one in the PANDA), through the
        /// same roll filter as the PANDA's; otherwise the PANDA's.
        /// </summary>
        public double ImuRollNowDeg()
        {
            if (ahrs.imuRoll == 88888 || !imuSummary.Fresh(secondsSinceStart))
            {
                summaryRollDeg = double.NaN;
                return ahrs.imuRoll;
            }
            if (imuSummary.Received != summaryRollTaken)
            {
                // once per summary: the filter set for the roll, as on the PANDA's
                summaryRollTaken = imuSummary.Received;
                double roll = CImuSummary.AsRoll(imuSummary.RollAvgDeg, ahrs.isRollInvert, ahrs.rollZero);
                double f = Math.Max(0, Math.Min(0.98, ahrs.rollFilter));
                summaryRollDeg = double.IsNaN(summaryRollDeg) ? roll : summaryRollDeg * f + roll * (1 - f);
            }
            // a summary far from the PANDA's (another IMU axis, a firmware mix-up): the PANDA's - both filtered the
            // same way, they differ by the noise only
            return Math.Abs(summaryRollDeg - ahrs.imuRoll) < 3 ? summaryRollDeg : ahrs.imuRoll;
        }

        /// <summary>The fastest roll change since the last fix (deg/s), from the IMU summary; NaN without one.</summary>
        public double ImuRollPeakRateDegS() => imuSummary.Fresh(secondsSinceStart) ? imuSummary.RollRatePeakDegS : double.NaN;

        /// <summary>
        /// The roll for the compensation now, put on time: a reading that arrives late is carried forward by how fast
        /// it changes; one that arrives early is taken from a moment ago.
        /// </summary>
        public double CompensationRollDeg()
        {
            double roll = ImuRollNowDeg(), t = secondsSinceStart;
            if (rollHistory.Count == 0 || t - rollHistory[rollHistory.Count - 1][0] > 0.02) rollHistory.Add(new[] { t, roll });
            while (rollHistory.Count > 0 && t - rollHistory[0][0] > 2) rollHistory.RemoveAt(0);

            double shift = RollShiftS;
            if (Math.Abs(shift) < 0.005) return roll;
            if (shift < 0) return RollAt(t + shift, out double earlier) ? earlier : roll;
            if (!RollAt(t - 0.2, out double before)) return roll;
            double rate = (roll - before) / 0.2;
            return roll + Math.Max(-3, Math.Min(3, rate * shift));
        }

        /// <summary>The antenna height the compensation uses (the one set, times the learnt factor).</summary>
        public double CompensationHeightM => vehicle.VehicleConfig.AntennaHeight * RollGain;

        /// <summary>
        /// The roll compensation for the fix now: how far to move it to the right of the heading (m). The position
        /// before it and the roll as told go to the roll tuner and to pit mode.
        /// </summary>
        public double RollCorrection(double headingRad)
        {
            double corr = Math.Sin(glm.toRadians(CompensationRollDeg())) * -CompensationHeightM;
            if (timerSim.Enabled) return corr;   // nothing learnt in the simulator
            try
            {
                bool straight = Math.Abs(mc.actualSteerAngleDegrees) < 5;
                rollTune.AddSample(secondsSinceStart, pn.fix.easting, pn.fix.northing, headingRad, ahrs.imuRoll, corr, avgSpeed, straight && !isReverse);
                pitGuard.Update(secondsSinceStart, ahrs.imuRoll, rollTune.LeftSwingM, PitGuardOn, PitRateDegS, ImuRollPeakRateDegS());
                if (secondsSinceStart - rollAnalyzedS > 15)
                {
                    rollAnalyzedS = secondsSinceStart;
                    rollTune.Analyze();
                }
            }
            catch (Exception ex)
            {
                // the learning must never stop the position
                Log.EventWriter("Roll tune error: " + ex.Message);
            }
            return corr;
        }

        /// <summary>The angle to send, held back for a moment after an unexplained rock (pit mode).</summary>
        private double PitSmooth(double askedDeg, bool steeringNow)
        {
            if (!steeringNow || !PitGuardOn) return pitGuard.Smooth(secondsSinceStart, askedDeg, 1e9);
            return pitGuard.Smooth(secondsSinceStart, askedDeg, PitRateDegS);
        }

        /// <summary>The roll compensation's timing and height factor the measurements suggest.</summary>
        public TuneVerdict RollTuneRecommend(SteerBaseline now, out double shift, out double gain)
        {
            return rollTune.Recommend(now.RollShift, now.RollGain, vehicle.VehicleConfig.AntennaHeight, out shift, out gain);
        }

        /// <summary>The roll compensation's suggestion, put in by hand (the window's Apply).</summary>
        public void ApplyRollTune(double shift, double gain)
        {
            var vs = Properties.VehicleSettings.Default;
            string text = TrA("Roll compensation") + " " + vs.setAS_RollShiftS.ToString("0.00") + " s / " + vs.setAS_RollGain.ToString("0.00") + " → " +
                shift.ToString("0.00") + " s / " + gain.ToString("0.00");
            vs.setAS_RollShiftS = Math.Max(-0.3, Math.Min(0.3, shift));
            vs.setAS_RollGain = Math.Max(0.5, Math.Min(1.5, gain));
            if (!isEasyDriveMode) vs.Save();
            rollTune.Reset();
            ResetLineTuners();
            Log.EventWriter("Steering: " + text);
        }

        /// <summary>Pit mode's suggested rate, put in by hand.</summary>
        public void ApplyPitRate(double rate)
        {
            var vs = Properties.VehicleSettings.Default;
            Log.EventWriter("Steering: pit mode " + vs.setAS_PitRateDegS.ToString("0.0") + " -> " + rate.ToString("0.0") + " deg/s");
            vs.setAS_PitRateDegS = Math.Max(1, Math.Min(15, rate));
            if (!isEasyDriveMode) vs.Save();
        }
    }
}
