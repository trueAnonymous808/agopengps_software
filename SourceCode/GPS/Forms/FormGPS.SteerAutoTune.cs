namespace AgOpenGPS
{
    // Steering auto-tune (motor, look-ahead, side-hill): the tuners learn in the background from every GPS fix while
    // autosteer works, so the window only shows what they have found so far. Each keeps a bounded window of data.
    public partial class FormGPS
    {
        public readonly CMotorTune motorTune = new CMotorTune();
        public readonly CLookAheadTune lookAheadTune = new CLookAheadTune();
        public readonly CSideHillTune sideHillTune = new CSideHillTune();
        public readonly CDeadZoneTune deadZoneTune = new CDeadZoneTune();
        public readonly CLookAheadSpeedTune lookAheadSpeedTune = new CLookAheadSpeedTune();
        public readonly CRollZeroTune rollZeroTune = new CRollZeroTune();
        public readonly CAcquireTune acquireTune = new CAcquireTune();
        public readonly CIntegralTune integralTune = new CIntegralTune();
        public readonly CUTurnTune uTurnTune = new CUTurnTune();

        /// <summary>
        /// After the WAS zero, the roll zero, the side-hill compensation or the steering geometry changed: how the
        /// line was held so far belongs to the old values (a pull to one side the change has just removed), so the
        /// integral and look-ahead tuners start measuring again.
        /// </summary>
        public void ResetLineTuners()
        {
            fusionTune.Reset();
            integralTune.Reset();
            lookAheadTune.Reset();
            lookAheadSpeedTune.Reset();
        }

        private bool autoTuneFailed;

        // the angle last sent to the module (degrees); NaN until the first one
        private double steerAimDeg = double.NaN;

        /// <summary>The angle the module aims at now: the last one sent, or the guidance's before any was sent.</summary>
        public double SteerAimDeg() => double.IsNaN(steerAimDeg) ? SteerSentDeg(guidanceLineSteerAngle * 0.01) : steerAimDeg;

        // the tuners run in the position path: an error in them must never stop the position
        private void FeedSteerAutoTune()
        {
            if (autoTuneFailed) return;
            // the simulator teaches nothing: its tractor is not the real one (no tuner, no gear, no passes, no
            // learning on the field); only where the front is goes on being remembered, and the auto-tune says it waits
            if (timerSim.Enabled)
            {
                try
                {
                    NoteForward();
                    SteerAutoTick();
                }
                catch (System.Exception ex)
                {
                    AgLibrary.Logging.Log.EventWriter("Forward memory error: " + ex.Message);
                }
                return;
            }
            try
            {
                FeedSteerAutoTuneNow();
            }
            catch (System.Exception ex)
            {
                autoTuneFailed = true;
                AgLibrary.Logging.Log.EventWriter("Steering auto-tune switched off after an error: " + ex);
            }
        }

        private void FeedSteerAutoTuneNow()
        {
            // a steer module that answers (or the simulator): without one the wheel angle is frozen, not the steering's
            bool moduleAnswers = timerSim.Enabled || steerModuleConnectedCounter <= 30;
            bool steering = (isBtnAutoSteerOn || routeSteerActive) && !vehicle.isInFreeDriveMode && !isReverse && moduleAnswers;
            // the module aims at the angle sent to it (the sensor's linearity, the dead-time lead and pit mode taken
            // in): that is what its reading follows. The one sent on the last fix: the tuners run before this fix's
            // angle is made, and the wheels answer what was sent already.
            double sentDeg = SteerAimDeg();
            // with a separate fast Kp, the plain Kp is tuned only on the slow side
            bool slowSide = Properties.VehicleSettings.Default.setAS_KpFast <= 0 || avgSpeed <= 10;
            motorTune.AddSample(secondsSinceStart, sentDeg, mc.actualSteerAngleDegrees, avgSpeed, steering && slowSide);
            deadZoneTune.AddSample(secondsSinceStart, sentDeg, mc.actualSteerAngleDegrees, vehicle.isInDeadZone, avgSpeed, steering);

            // holding an AB line or curve: not on a contour, not in a U-turn, at working speed
            bool hasOffset = guidanceLineDistanceOff != 32000 && guidanceLineDistanceOff != 32020;
            bool rtkFix = timerSim.Enabled || (pn.fixQuality == CGpsGuard.RtkFix && pn.age <= CGpsGuard.OldCorrectionS);
            bool onLine = steering && isBtnAutoSteerOn && trk.idx > -1 && !ct.isContourBtnOn && !yt.isYouTurnTriggered
                && avgSpeed > 3 && hasOffset && rtkFix;
            double offLineM = hasOffset ? guidanceLineDistanceOff * 0.001 : 0;
            // the look-ahead only matters to pure pursuit
            lookAheadTune.AddSample(pivotAxlePos.easting, pivotAxlePos.northing, offLineM, onLine && PurePursuitSteers);
            lookAheadSpeedTune.AddSample(pivotAxlePos.easting, pivotAxlePos.northing, offLineM, onLine && PurePursuitSteers, avgSpeed);
            sideHillTune.AddSample(pivotAxlePos.easting, pivotAxlePos.northing, ahrs.imuRoll, mc.actualSteerAngleDegrees, offLineM, onLine);

            // straight on the line: the roll zero (passes both ways) and the integral (staying to one side)
            bool straightOnLine = onLine && System.Math.Abs(mc.actualSteerAngleDegrees) < 5;
            rollZeroTune.AddSample(pivotAxlePos.easting, pivotAxlePos.northing, fixHeading, ahrs.imuRoll, straightOnLine && System.Math.Abs(offLineM) < 0.1);
            integralTune.AddSample(pivotAxlePos.easting, pivotAxlePos.northing, offLineM, straightOnLine && PurePursuitSteers);
            acquireTune.AddSample(pivotAxlePos.easting, pivotAxlePos.northing, offLineM, onLine && PurePursuitSteers);
            uTurnTune.AddSample(pivotAxlePos.easting, pivotAxlePos.northing, offLineM, yt.isYouTurnTriggered, onLine);

            // GPS guard, IMU fusion, fast Kp
            FeedSteerExtras(steering, straightOnLine);

            // the steering gear, and the distance off the line for checking automatic changes
            FeedSteerMore(steering, sentDeg, offLineM, onLine);

            // fully automatic: apply what is ready (it checks itself only every few seconds)
            SteerAutoTick();
        }
    }
}
