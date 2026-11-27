using AgLibrary.Logging;
using System;

namespace AgOpenGPS
{
    // The Kalman state estimator in shadow mode: it runs beside the usual position and heading and changes neither -
    // it learns the steer angle sensor's offset, the IMU's offset and (dual antenna) the sideways slip, shown in the
    // smart features. MPC may take its offset and slip in place of its own simpler learning (a switch).
    public partial class FormGPS
    {
        public readonly CStateEstimator estimator = new CStateEstimator();
        public readonly CImuSummary imuSummary = new CImuSummary();   // PGN 0xB2 from the modified module firmwares
        private double estimatorLoggedS = -1e9, estimatorWheelbase = double.NaN;

        /// <summary>MPC takes the estimator's steer offset and slip once it is ready.</summary>
        public bool KalmanForMpc => Properties.Settings.Default.setKalman_Mpc;

        public void SetKalmanForMpc(bool on)
        {
            Properties.Settings.Default.setKalman_Mpc = on;
            Properties.Settings.Default.Save();
            Log.EventWriter("State estimator for MPC " + (on ? "on" : "off"));
        }

        /// <summary>Whether MPC uses the estimator now (switched on and learnt enough).</summary>
        public bool MpcUsesKalman => KalmanForMpc && estimator.Ready;

        /// <summary>The guidance steers from the estimator's position and heading (the switch; used once ready).</summary>
        public bool KalmanPositionOn => Properties.Settings.Default.setKalman_Position;

        public void SetKalmanPosition(bool on)
        {
            Properties.Settings.Default.setKalman_Position = on;
            Properties.Settings.Default.Save();
            Log.EventWriter("Steering from the state estimator's position " + (on ? "on" : "off"));
        }

        /// <summary>The guidance takes the estimator's position on this fix.</summary>
        public bool KalmanPositionInUse { get; private set; }

        private double kalmanFarLoggedS = -1e9;

        // every fix, before the guidance: the estimator steps on the raw position; switched on, ready and close to the
        // raw position (else the raw one stays - never a jump), the pivot, steer axle and heading become its own
        private void ApplyStateEstimator()
        {
            KalmanPositionInUse = false;
            try
            {
                StepEstimator();
                if (!KalmanPositionOn || !estimator.Ready) return;
                double e = estimator[CStateEstimator.E], n = estimator[CStateEstimator.N], h = estimator[CStateEstimator.H];
                double de = e - pivotAxlePos.easting, dn = n - pivotAxlePos.northing;
                double dh = Math.Abs(CStateEstimator.Wrap(h - fixHeading));
                if (de * de + dn * dn > 0.3 * 0.3 || dh > glm.toRadians(3) || estimator.PositionInnovationCm > 10)
                {
                    if (secondsSinceStart - kalmanFarLoggedS > 60)
                    {
                        kalmanFarLoggedS = secondsSinceStart;
                        Log.EventWriter("State estimator too far from the raw position (" + (Math.Sqrt(de * de + dn * dn) * 100).ToString("0") +
                            " cm, " + glm.toDegrees(dh).ToString("0.0") + " deg): the raw position is used");
                    }
                    return;
                }

                if (h < 0) h += glm.twoPI;
                fixHeading = h;
                pivotAxlePos.easting = e;
                pivotAxlePos.northing = n;
                pivotAxlePos.heading = h;
                double L = vehicle.VehicleConfig.Wheelbase;
                steerAxlePos.easting = e + Math.Sin(h) * L;
                steerAxlePos.northing = n + Math.Cos(h) * L;
                steerAxlePos.heading = h;
                KalmanPositionInUse = true;
            }
            catch (Exception ex)
            {
                // the raw position stays; the estimator starts again
                estimator.Reset();
                Log.EventWriter("State estimator error, the raw position is used: " + ex.Message);
            }
        }

        // the GNSS position's trust by its fix quality (m); NaN: no position to take
        private double FixSigmaM()
        {
            if (timerSim.Enabled) return 0.01;
            if (rtkBridge.Active) return 0.1;   // carried on by dead reckoning: not a measurement as good as RTK
            switch (pn.fixQuality)
            {
                case 4: return pn.age <= CGpsGuard.OldCorrectionS ? 0.02 : 0.1;
                case 5: return 0.25;
                case 2: return 0.6;
                case 1: return 1.5;
                default: return double.NaN;
            }
        }

        // every fix (from the auto-tune's feed: an error there never stops the position)
        private void StepEstimator()
        {
            // nothing learnt in the simulator: the estimator's offsets would be the simulated tractor's
            if (!isFirstHeadingSet || timerSim.Enabled) return;
            double wheelbase = ModelWheelbaseM;
            if (wheelbase != estimatorWheelbase)
            {
                // another vehicle: what was learnt belongs to the other one
                estimatorWheelbase = wheelbase;
                estimator.Reset();
            }

            var vs = Properties.VehicleSettings.Default;
            bool module = timerSim.Enabled || steerModuleConnectedCounter <= 30;
            double reading = mc.actualSteerAngleDegrees;
            double steerDeg = reading * CWasLinearity.Factor(reading, vs.setAS_WasLinMiddle, vs.setAS_WasLinBig);
            bool dual = headingFromSource == "Dual";
            bool imu = ahrs.imuHeading != 99999 && !ahrs.isDualAsIMU;

            estimator.Step(new CStateEstimator.Inputs
            {
                T = secondsSinceStart,
                East = pivotAxlePos.easting,
                North = pivotAxlePos.northing,
                PositionSigmaM = FixSigmaM(),
                SpeedMs = avgSpeed / 3.6 * (isReverse ? -1 : 1),
                // (the single antenna's heading is its course; reversing it is turned round - not taken then)
                CourseRad = !dual && !isReverse ? gpsHeading : double.NaN,
                ImuHeadingRad = imu ? glm.toRadians(ahrs.imuHeading) : double.NaN,
                DualHeadingRad = dual ? glm.toRadians(pn.headingTrueDual) : double.NaN,
                SteerRad = module ? glm.toRadians(steerDeg) : double.NaN,
                WheelbaseM = wheelbase,
                // the module's IMU summary: how fast it turned over the last interval (newer firmwares only)
                HasYawRate = imu && imuSummary.Fresh(secondsSinceStart),
                YawRateRadS = glm.toRadians(imuSummary.YawRateDegS)
            });

            // a line in the log now and then: what it found, for a look after a day in the field
            if (estimator.Started && secondsSinceStart - estimatorLoggedS > 60)
            {
                estimatorLoggedS = secondsSinceStart;
                Log.EventWriter("State estimator: steer offset " + estimator.SteerOffsetDeg.ToString("0.00") + " deg (+-" +
                    (estimator.Sigma(CStateEstimator.SteerOff) * 180 / Math.PI).ToString("0.00") + "), IMU offset " +
                    estimator.ImuOffsetDeg.ToString("0.0") + " deg, slip " + (estimator.SlipMs * 100).ToString("0.0") + " cm/s, off the GNSS " +
                    estimator.PositionInnovationCm.ToString("0.0") + " cm" + (estimator.Ready ? "" : " (learning)"));
            }
        }
    }
}
