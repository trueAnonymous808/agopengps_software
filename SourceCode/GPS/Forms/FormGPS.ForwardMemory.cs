using AgLibrary.Logging;
using AgOpenGPS.Core.Models;
using System;

namespace AgOpenGPS
{
    // Where the tractor's front is, remembered (single antenna): the heading and the place are saved whenever the
    // tractor stops and when the program closes, per vehicle profile. At the next start in the same place the heading
    // is known at once - no question mark, and the first move backwards is seen as reversing. Moved elsewhere while
    // the program was off, or switched to "by hand" in Smart features: the original way (the first move is forward,
    // a click on the tractor resets it).
    public partial class FormGPS
    {
        // how far the tractor may seem from where it was left: at the start the position may be metres off until
        // RTK is fixed
        public const double ForwardMemoryRadiusM = 10;

        private bool forwardResetByHand, forwardDirty, forwardFarTold;
        private double forwardSavedS = -1e9, forwardFirstTryS = double.NaN;

        // forwards or backwards at the first move, from the IMU's jolt (per vehicle profile)
        public readonly CStartDirection startDirection = new CStartDirection();
        private string startDirectionProfile;

        /// <summary>The module's time since it was switched on, now (ms); -1: not known (older firmware, no module).</summary>
        private double ModuleUptimeNowMs()
        {
            if (!imuSummary.Seen || imuSummary.ModuleUptimeMs < 0) return -1;
            double age = secondsSinceStart - imuSummary.ReceivedT;
            if (age < 0 || age > 2) return -1;
            return imuSummary.ModuleUptimeMs + age * 1000;
        }

        // every fix (before the heading): the jolt at starting off, and how the IMU sits learnt while driving
        private void FeedStartDirection()
        {
            if (timerSim.Enabled) return;   // nothing learnt in the simulator
            try
            {
                string profile = RegistrySettings.vehicleProfileName ?? "";
                if (profile != startDirectionProfile)
                {
                    startDirectionProfile = profile;
                    var vs = Properties.VehicleSettings.Default;
                    startDirection.Load(vs.setDir_AccAxisX, vs.setDir_AccAxisY, vs.setDir_AccAxisZ, vs.setDir_AccWeight);
                }
                bool acc = imuSummary.Fresh(secondsSinceStart) && imuSummary.HasAccel;
                startDirection.Feed(secondsSinceStart, avgSpeed, acc ? imuSummary.AccX : double.NaN, acc ? imuSummary.AccY : double.NaN,
                    acc ? imuSummary.AccZ : double.NaN, isFirstHeadingSet, isReverse);
            }
            catch (Exception ex)
            {
                Log.EventWriter("Start direction error: " + ex.Message);
            }
        }

        // at the first heading from the movement: was that move backwards?
        private bool FirstMoveBackwards()
        {
            if (timerSim.Enabled || headingFromSource != "Fix") return false;
            int move = startDirection.FirstMove(secondsSinceStart);
            if (move != 0)
                Log.EventWriter("First move " + (move > 0 ? "forwards" : "backwards") + " (the IMU's jolt at starting off)");
            return move < 0;
        }

        public bool RememberForward => Properties.VehicleSettings.Default.setDir_RememberForward;

        public void SetRememberForward(bool on)
        {
            var vs = Properties.VehicleSettings.Default;
            vs.setDir_RememberForward = on;
            if (!on) vs.setDir_LastHeadingDeg = -1;   // by hand: nothing remembered to come back later
            if (!isEasyDriveMode) vs.Save();
            if (on) forwardDirty = true;
            Log.EventWriter("Driving direction at start: " + (on ? "remembered" : "by hand"));
        }

        /// <summary>A click on the tractor: the driver tells the direction again, nothing is put back meanwhile.</summary>
        private void ForwardResetByHand() => forwardResetByHand = true;

        // every fix: saved when the tractor has moved and now stands
        private void NoteForward()
        {
            if (!isFirstHeadingSet) return;
            forwardResetByHand = false;
            if (!RememberForward || headingFromSource != "Fix") return;
            if (Math.Abs(avgSpeed) > 0.5)
            {
                forwardDirty = true;
                return;
            }
            // Easy Drive saves nothing: a save here would write its temporary values into the profile too
            if (forwardDirty && secondsSinceStart - forwardSavedS > 5 && !isEasyDriveMode)
            {
                SaveForward();
                if (timerSim.Enabled) sim.RememberPlace();
            }
        }

        /// <summary>Also when the program closes.</summary>
        private void SaveForward()
        {
            if (!RememberForward || headingFromSource != "Fix" || !isFirstHeadingSet || isEasyDriveMode) return;
            Wgs84 here = AppModel.CurrentLatLon;
            if (!here.IsValid) return;
            var vs = Properties.VehicleSettings.Default;
            vs.setDir_LastHeadingDeg = Math.Round(glm.toDegrees(fixHeading), 2);
            vs.setDir_LastLat = here.Latitude;
            vs.setDir_LastLon = here.Longitude;
            // the IMU's heading and the module's time now: if the module runs on while the tablet is off, the turns
            // the IMU counts meanwhile carry the heading to wherever the tractor is next
            vs.setDir_LastImuDeg = ahrs.imuHeading != 99999 && !ahrs.isDualAsIMU ? ahrs.imuHeading : -1;
            vs.setDir_LastModuleMs = ModuleUptimeNowMs();
            vs.setDir_LastSavedUtcS = CForwardCarry.UtcNowS();
            if (startDirectionProfile == (RegistrySettings.vehicleProfileName ?? ""))
            {
                vs.setDir_AccAxisX = startDirection.SumX;
                vs.setDir_AccAxisY = startDirection.SumY;
                vs.setDir_AccAxisZ = startDirection.SumZ;
                vs.setDir_AccWeight = startDirection.Weight;
            }
            vs.Save();
            forwardDirty = false;
            forwardSavedS = secondsSinceStart;
        }

        /// <summary>
        /// At the start (heading not known yet, single antenna): the remembered heading, if the tractor is where it was
        /// left. Sets everything the first move would have set. False: the original way.
        /// </summary>
        private bool TryRestoreForward()
        {
            var vs = Properties.VehicleSettings.Default;
            if (!RememberForward || forwardResetByHand || vs.setDir_LastHeadingDeg < 0) return false;
            Wgs84 here = AppModel.CurrentLatLon;
            if (!here.IsValid) return false;

            // a moment for the module's IMU summary (its time tells whether the IMU ran on meanwhile): it comes just
            // before each PANDA, so normally with the first fix already - a fix or two at most, never in the simulator
            // (longer, the tractor would stand drawn in the old heading and then turn round)
            if (double.IsNaN(forwardFirstTryS)) forwardFirstTryS = secondsSinceStart;
            if (!timerSim.Enabled && !imuSummary.Seen && steerModuleConnectedCounter <= 30 && secondsSinceStart - forwardFirstTryS < 0.3) return false;

            // carried by the IMU: the module ran on while the tablet was off - anywhere, turned on the spot too
            double imuNow = ahrs.imuHeading != 99999 && !ahrs.isDualAsIMU ? ahrs.imuHeading : double.NaN;
            double headingDeg = CForwardCarry.HeadingDeg(vs.setDir_LastHeadingDeg, vs.setDir_LastImuDeg, vs.setDir_LastModuleMs,
                vs.setDir_LastSavedUtcS, imuNow, ModuleUptimeNowMs(), CForwardCarry.UtcNowS());
            double away = here.DistanceInMeters(new Wgs84(vs.setDir_LastLat, vs.setDir_LastLon));
            string how;
            if (!double.IsNaN(headingDeg))
            {
                how = "carried by the IMU (the module ran on)";
            }
            else
            {
                // the module was switched off meanwhile (or an older firmware): only where it was left
                if (double.IsNaN(away) || away > ForwardMemoryRadiusM)
                {
                    if (!forwardFarTold)
                    {
                        forwardFarTold = true;
                        Log.EventWriter("Forward not remembered: " + away.ToString("0.0") + " m from where it was left, and the module did not run on meanwhile");
                    }
                    return false;
                }
                headingDeg = vs.setDir_LastHeadingDeg;
                how = "where it was left";
            }

            gpsHeading = glm.toRadians(headingDeg);
            if (gpsHeading < 0) gpsHeading += glm.twoPI;
            else if (gpsHeading >= glm.twoPI) gpsHeading -= glm.twoPI;
            fixHeading = gpsHeading;

            // the IMU lined up with it, as the first move does
            if (ahrs.imuHeading != 99999)
            {
                imuGPS_Offset = gpsHeading - glm.toRadians(ahrs.imuHeading);
                if (imuGPS_Offset < 0) imuGPS_Offset += glm.twoPI;
                else if (imuGPS_Offset >= glm.twoPI) imuGPS_Offset -= glm.twoPI;
                imuCorrected = gpsHeading;
            }
            camHeading = glm.toDegrees(gpsHeading);
            smoothCamHeading = gpsHeading;

            // the steps the heading is measured from: here, with the antenna's offset and the roll as on every fix
            double e = pn.fix.easting, n = pn.fix.northing;
            if (vehicle.VehicleConfig.AntennaOffset != 0)
            {
                e += Math.Cos(-gpsHeading) * vehicle.VehicleConfig.AntennaOffset;
                n += Math.Sin(-gpsHeading) * vehicle.VehicleConfig.AntennaOffset;
            }
            if (ahrs.imuRoll != 88888)
            {
                rollCorrectionDistance = Math.Tan(glm.toRadians(CompensationRollDeg())) * -CompensationHeightM;
                e += Math.Cos(-gpsHeading) * rollCorrectionDistance;
                n += Math.Sin(-gpsHeading) * rollCorrectionDistance;
            }
            for (int i = 0; i < totalFixSteps; i++)
            {
                stepFixPts[i].easting = e;
                stepFixPts[i].northing = n;
                stepFixPts[i].distance = 0;
                stepFixPts[i].isSet = i < 3 ? 1 : 0;
            }
            pn.fix.easting = e;
            pn.fix.northing = n;
            prevFix = pn.fix;
            lastGPS = pn.fix;
            isReverse = false;
            isFirstHeadingSet = true;
            hasBeenFirstHeadingSet = true;
            Log.EventWriter("Forward remembered: heading " + headingDeg.ToString("0.0") + ", " + how + ", " + away.ToString("0.0") + " m from where it was left");
            return true;
        }
    }
}
