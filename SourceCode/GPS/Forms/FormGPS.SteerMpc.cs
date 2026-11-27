using AgLibrary.Logging;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace AgOpenGPS
{
    // The steering method (pure pursuit, Stanley or model predictive) and the helpers on the angle sent: the curve
    // preview for pure pursuit, calmer steering and play compensation. MPC steers AB lines and curves going forwards;
    // U-turns, contours and reversing keep pure pursuit.
    public partial class FormGPS
    {
        /// <summary>Model predictive steering chosen (kept with the implement, as Stanley is).</summary>
        public bool isMpcUsed;

        public bool MpcActive => isMpcUsed && !isStanleyUsed;

        /// <summary>Pure pursuit steers: its look-ahead, acquire and integral are in use.</summary>
        public bool PurePursuitSteers => !isStanleyUsed && !isMpcUsed && !isLqrUsed && !isHybridUsed;

        public string SteerModeName => isStanleyUsed ? "Stanley" : isMpcUsed ? "MPC" : isLqrUsed ? "LQR" : isHybridUsed ? "Hybrid" : "Pure pursuit";

        /// <summary>Adaptive pure pursuit: the look-ahead set on every fix, and the curve preview.</summary>
        public bool AdaptivePpOn => Properties.Settings.Default.setSteer_AdaptivePP;
        public bool SteerSmoothOn => Properties.Settings.Default.setSteer_Smooth;
        public bool PlayCompOn => Properties.Settings.Default.setSteer_PlayComp;

        public void SetAdaptivePp(bool on)
        {
            Properties.Settings.Default.setSteer_AdaptivePP = on;
            Properties.Settings.Default.Save();
            adaptiveLook.Reset();
            ShowSteerModeInSettings();
            Log.EventWriter("Steering: adaptive pure pursuit " + (on ? "on" : "off"));
        }

        // an open steer settings window shows the method's tabs and switches
        private static void ShowSteerModeInSettings()
        {
            Application.OpenForms.OfType<FormSteer>().FirstOrDefault()?.RefreshSteerMode();
        }
        public void SetSteerSmooth(bool on) { Properties.Settings.Default.setSteer_Smooth = on; Properties.Settings.Default.Save(); Log.EventWriter("Steering: calm steering " + (on ? "on" : "off")); }
        public void SetPlayComp(bool on) { Properties.Settings.Default.setSteer_PlayComp = on; Properties.Settings.Default.Save(); Log.EventWriter("Steering: play compensation " + (on ? "on" : "off")); }

        /// <summary>Pure pursuit, Stanley, MPC and round again.</summary>
        public void NextSteerMode() => SetSteerMode((SteerMode + 1) % 5);

        /// <summary>The steering method: 0 pure pursuit, 1 Stanley, 2 MPC, 3 LQR, 4 hybrid.</summary>
        public int SteerMode => isStanleyUsed ? 1 : isMpcUsed ? 2 : isLqrUsed ? 3 : isHybridUsed ? 4 : 0;

        public void SetSteerMode(int mode)
        {
            isStanleyUsed = mode == 1;
            isMpcUsed = mode == 2;
            isLqrUsed = mode == 3;
            isHybridUsed = mode == 4;
            Properties.ToolSettings.Default.setVehicle_isLqrUsed = isLqrUsed;
            Properties.ToolSettings.Default.setVehicle_isHybridUsed = isHybridUsed;
            hybridBlend.Reset();
            Properties.ToolSettings.Default.setVehicle_isStanleyUsed = isStanleyUsed;
            Properties.ToolSettings.Default.setVehicle_isMpcUsed = isMpcUsed;
            if (!isEasyDriveMode) Properties.ToolSettings.Default.Save();
            mpcBiasDeg = 0;
            btnFlag.Text = SteerModeLetter;
            ShowSteerModeInSettings();
            Log.EventWriter("Steer mode: " + SteerModeName);
        }

        /// <summary>The letter on the main screen's mode button.</summary>
        public string SteerModeLetter => isStanleyUsed ? "S" : isMpcUsed ? "M" : isLqrUsed ? "L" : isHybridUsed ? "H" : "P";

        /// <summary>The steer mode's picture (0 pure pursuit, 1 Stanley, 2 MPC, 3 LQR, 4 hybrid).</summary>
        public static Image SteerModeImage(int mode)
        {
            if (mode == 1) return Properties.Resources.ModeStanley;
            Image pp = Properties.Resources.ModePurePursuit;
            if (mode == 2) return SteerTabIcons.MpcMode(pp.Width, pp.Height, "MPC");
            if (mode == 3) return SteerTabIcons.MpcMode(pp.Width, pp.Height, "LQR");
            if (mode == 4) return SteerTabIcons.MpcMode(pp.Width, pp.Height, "HYB");
            return pp;
        }

        // ---- the path ahead, filled by the AB line and curve guidance when something uses it ----

        public readonly List<vec2> pathAhead = new List<vec2>();

        /// <summary>Whether the guidance should fill the path ahead (MPC, or pure pursuit's curve preview).</summary>
        public bool PathAheadWanted => ModelSteers || (PurePursuitSteers && AdaptivePpOn);

        /// <summary>How far ahead the path is wanted (m).</summary>
        public double PathAheadLengthM => Math.Max(25, Math.Abs(avgSpeed) / 3.6 * (mpc.HorizonS + 1) + 10);

        /// <summary>
        /// The wheelbase the steering models (MPC, LQR, the state estimator) take: the vehicle's - or in the simulator its
        /// own, as it turns: tan(0.947 x angle) / 2 m, about 2.11 m for the angles that matter. With the vehicle's there
        /// the models would learn offsets and slips the simulator does not have.
        /// </summary>
        public double ModelWheelbaseM => timerSim.Enabled ? SimWheelbaseM : vehicle.VehicleConfig.Wheelbase;

        public const double SimWheelbaseM = 2.11;

        /// <summary>
        /// The share of the angle asked that the wheels really take: the simulator turns them to 90 % of it (see
        /// timerSim_Tick); a real steering takes all of it. The models count the angles asked with it (the wheelbase
        /// above is for the wheels' real angle).
        /// </summary>
        public double ModelWheelShare => timerSim.Enabled ? 0.9 : 1.0;

        // the steering's dead time and turning speed, as measured (and safe guesses until they are)
        public double SteerDeadTimeS => steerDynamics.Ready ? Math.Max(0, Math.Min(1, steerDynamics.DelayS)) : 0.2;
        public double SteerTurnRateDegS => !double.IsNaN(steerDynamics.MaxRateDegS) ? Math.Max(5, Math.Min(60, steerDynamics.MaxRateDegS)) : 25;

        /// <summary>Pure pursuit on a curve: the correction from the curve preview (degrees; 0 when off).</summary>
        public double CurvePreviewDeg(double goalDistanceM)
        {
            if (!PurePursuitSteers || !AdaptivePpOn || isReverse || avgSpeed < 1 || pathAhead.Count < 2) return 0;
            return CPathAhead.CurvePreviewDeg(pathAhead, goalDistanceM, avgSpeed / 3.6, SteerDeadTimeS, ModelWheelbaseM, SteerTurnRateDegS);
        }

        // ---- adaptive pure pursuit ----

        private readonly CAdaptiveLookAhead adaptiveLook = new CAdaptiveLookAhead();

        /// <summary>The look-ahead adaptive pure pursuit uses now (m; NaN while it is not on).</summary>
        public double AdaptiveLookAheadM => AdaptivePpOn && PurePursuitSteers ? adaptiveLook.LastLookAheadM : double.NaN;

        /// <summary>
        /// The AB line's and curve's look-ahead: as the settings give it, or adapted to the curve ahead, the dead time
        /// and weaving (the path ahead is the one from the fix before: a tenth of a second does not matter).
        /// </summary>
        public double AdaptLookAhead(double baseM) => AdaptLookAhead(baseM, double.NaN);

        /// <summary>
        /// The same where the curvature is known another way (a U-turn's radius; a contour: 0). uTurn: the U-turns of
        /// MPC, LQR and the hybrid (pure pursuit drives them) take the adaptive look-ahead too - in the simulated U-turns
        /// it ran closer than the plain one in 15 of 18 cases, often by half.
        /// </summary>
        public double AdaptLookAhead(double baseM, double curvatureKnown, bool uTurn = false)
        {
            bool adapt = PurePursuitSteers ? AdaptivePpOn : uTurn && ModelSteers;
            if (!adapt || isReverse || avgSpeed < 1)
            {
                adaptiveLook.Reset();
                return baseM;
            }
            // the driver's choice from the steer settings' adaptive tab
            var ts = Properties.ToolSettings.Default;
            adaptiveLook.CurveGain = Math.Max(0, Math.Min(10, ts.setApp_Curve)) * 0.4;
            adaptiveLook.FloorGain = Math.Max(10, Math.Min(40, ts.setApp_Floor)) * 0.1;
            adaptiveLook.MaxWeaveFactor = 1 + Math.Max(0, Math.Min(60, ts.setApp_Weave)) * 0.01;
            double v = avgSpeed / 3.6;
            double curvature = !double.IsNaN(curvatureKnown) ? curvatureKnown
                : CAdaptiveLookAhead.TightestCurvature(pathAhead, baseM + v * SteerDeadTimeS);
            bool onLine = isBtnAutoSteerOn && !yt.isYouTurnTriggered && !ct.isContourBtnOn;
            double m = adaptiveLook.Adapt(secondsSinceStart, baseM, v, SteerDeadTimeS, curvature, vehicle.modeActualXTE, onLine);
            vehicle.goalDistance = m;   // the look-ahead drawn on the screen
            return m;
        }

        // ---- model predictive steering ----

        private readonly CSteerMpc mpc = new CSteerMpc();
        private readonly List<double[]> steerAskedHistory = new List<double[]>();   // t, angle asked (degrees)
        private double mpcBiasDeg, mpcLastDeg, mpcLastS = -1e9, mpcLastHeading = double.NaN;

        /// <summary>The offset MPC has learnt: the angle the tractor drives straight with (slope, sensor zero).</summary>
        public double MpcBiasDeg => mpcBiasDeg;

        /// <summary>The offset MPC, LQR and the hybrid use now: the state estimator's once ready, else their own learnt one.</summary>
        public double ModelOffsetDeg => MpcUsesKalman ? estimator.SteerOffsetDeg : mpcBiasDeg;

        private double mpcSolveMs, mpcFastLearnUntil = -1e9;

        /// <summary>How long one MPC step takes on this computer (ms), and whether the quicker search is used.</summary>
        public double MpcSolveMs => mpcSolveMs;
        public bool MpcQuick => mpc.Quick;

        /// <summary>
        /// Steering values changing (fully automatic, or by hand): the side-hill compensation's part of the offset moves
        /// with it at once; a new sensor zero, scale or roll zero is learnt again quickly for ten seconds.
        /// </summary>
        private void MpcValuesChanging(SteerBaseline before, SteerBaseline after)
        {
            if (Math.Abs(after.SideHill - before.SideHill) > 1e-9 && ahrs.imuRoll != 88888)
                mpcBiasDeg = Math.Max(-4, Math.Min(4, mpcBiasDeg + ahrs.imuRoll * -(after.SideHill - before.SideHill)));
            bool sensor = after.WasOffset != before.WasOffset || after.Cpd != before.Cpd
                || (after.HasExtras && Math.Abs(after.RollZero - before.RollZero) > 1e-9)
                || (after.HasExtras2 && (Math.Abs(after.LinMiddle - before.LinMiddle) > 1e-9 || Math.Abs(after.LinBig - before.LinBig) > 1e-9));
            if (sensor) MpcLearnQuickly();
        }

        /// <summary>The offset learnt again quickly for ten seconds (something it rests on changed).</summary>
        public void MpcLearnQuickly() => mpcFastLearnUntil = secondsSinceStart + 10;

        /// <summary>
        /// The angle MPC steers with on the path ahead (degrees, without the side-hill compensation the guidance adds);
        /// NaN when it does not steer now (then pure pursuit's angle stays).
        /// </summary>
        public double MpcSteerDeg()
        {
            if (!(MpcActive || HybridActive) || isReverse || pathAhead.Count < 2 || avgSpeed < 1)
            {
                mpcLastS = -1e9;
                mpcLastHeading = double.NaN;
                return double.NaN;
            }
            try
            {
                double t = secondsSinceStart;
                double v = avgSpeed / 3.6;
                double wheelbase = ModelWheelbaseM;
                // the part of the angle that is the side-hill compensation: added again by the guidance
                double sideHill = ahrs.imuRoll != 88888 ? ahrs.imuRoll * -gyd.sideHillCompFactor : 0;
                // the real wheel angle: the sensor's reading with its linearity
                var vs = Properties.VehicleSettings.Default;
                double reading = mc.actualSteerAngleDegrees;
                double wheels = reading * CWasLinearity.Factor(reading, vs.setAS_WasLinMiddle, vs.setAS_WasLinBig) - sideHill;

                LearnMpcBias(t, v, wheelbase, wheels);

                // the angles sent in the last dead time reach the wheels before anything asked now
                double dead = SteerDeadTimeS;
                int pendingSteps = (int)Math.Round(dead / mpc.StepS);
                var pending = new double[pendingSteps];
                for (int k = 0; k < pendingSteps; k++)
                    pending[k] = AskedAt(t - dead + k * mpc.StepS, mpcLastDeg + sideHill) * ModelWheelShare - sideHill;

                // the driver's choice: steering style (sharp ... calm) and how far ahead it plans
                var ts = Properties.ToolSettings.Default;
                mpc.WeightMove = 3e-5 * Math.Pow(2, (Math.Max(1, Math.Min(10, ts.setMpc_Calm)) - 5) * 0.6);
                mpc.HorizonS = Math.Max(2, Math.Min(5, ts.setMpc_HorizonS));

                bool fresh = t - mpcLastS > 1;
                if (fresh) mpc.ForgetWarmStart();
                var st = new CSteerMpc.State
                {
                    East = pivotAxlePos.easting,
                    North = pivotAxlePos.northing,
                    HeadingRad = fixHeading,
                    SpeedMs = v,
                    WheelbaseM = wheelbase,
                    WheelDeg = wheels,
                    MaxDeg = vehicle.maxSteerAngle,
                    RateDegS = SteerTurnRateDegS,
                    RateRightDegS = double.IsNaN(steerDynamics.MaxRateRightDegS) ? 0 : Math.Max(5, Math.Min(60, steerDynamics.MaxRateRightDegS)),
                    RateLeftDegS = double.IsNaN(steerDynamics.MaxRateLeftDegS) ? 0 : Math.Max(5, Math.Min(60, steerDynamics.MaxRateLeftDegS)),
                    // the state estimator's offset (its steer offset plus the side-hill part the guidance adds) and
                    // slip once it is ready; its own simpler learning until then
                    BiasDeg = MpcUsesKalman ? estimator.SteerOffsetDeg + sideHill : mpcBiasDeg,
                    SlipMs = MpcUsesKalman ? estimator.SlipMs : 0,
                    LastDeg = fresh ? wheels : mpcLastDeg * ModelWheelShare,
                    Pending = pending
                };
                var watch = System.Diagnostics.Stopwatch.StartNew();
                // (the angle for the wheels; asked so much more that they take it)
                double u = mpc.Solve(st, pathAhead) / ModelWheelShare;
                // a slow tablet: the coarser search above 20 ms, back to the fine one below 6 ms
                mpcSolveMs += (watch.Elapsed.TotalMilliseconds - mpcSolveMs) * 0.1;
                if (!mpc.Quick && mpcSolveMs > 20) { mpc.Quick = true; Log.EventWriter("MPC: " + mpcSolveMs.ToString("0") + " ms a step, the quicker search from now"); }
                else if (mpc.Quick && mpcSolveMs < 6) mpc.Quick = false;
                mpcLastDeg = u;
                mpcLastS = t;
                return u;
            }
            catch (Exception ex)
            {
                // never leave the tractor without an angle: pure pursuit's stays
                Log.EventWriter("MPC steering error, pure pursuit used: " + ex.Message);
                return double.NaN;
            }
        }

        // the angle that was asked for at time t (the one before it; the fallback when nothing is kept)
        private double AskedAt(double t, double fallback)
        {
            for (int i = steerAskedHistory.Count - 1; i >= 0; i--)
                if (steerAskedHistory[i][0] <= t) return steerAskedHistory[i][1];
            return steerAskedHistory.Count > 0 ? steerAskedHistory[0][1] : fallback;
        }

        // every fix: the angle asked for, for the dead time MPC looks back over
        private void NoteSteerAsked(double deg)
        {
            double t = secondsSinceStart;
            steerAskedHistory.Add(new[] { t, deg });
            while (steerAskedHistory.Count > 0 && t - steerAskedHistory[0][0] > 2) steerAskedHistory.RemoveAt(0);
        }

        // driving straight on: the angle the wheels needed for the heading change that really came is the offset
        // (a slope, a sensor zero a little off); learnt slowly, only while the steering is quiet
        private void LearnMpcBias(double t, double v, double wheelbase, double wheels)
        {
            double dt = t - mpcLastS;
            if (!double.IsNaN(mpcLastHeading) && dt > 0.04 && dt < 0.5 && v > 1 && Math.Abs(wheels) < 4
                && steerModuleConnectedCounter <= 30)
            {
                double turn = CPathAhead.Wrap(fixHeading - mpcLastHeading) / dt;
                if (Math.Abs(turn) < 0.03)
                {
                    double needed = Math.Atan(turn * wheelbase / v) * 180 / Math.PI;
                    double offset = needed - wheels;
                    double tau = t < mpcFastLearnUntil ? 1.0 : 5.0;
                    mpcBiasDeg += (offset - mpcBiasDeg) * Math.Min(1, dt / tau);
                    mpcBiasDeg = Math.Max(-4, Math.Min(4, mpcBiasDeg));
                }
            }
            mpcLastHeading = fixHeading;
        }

        // ---- on the angle sent ----

        private readonly CSteerSmoother steerSmoother = new CSteerSmoother();
        private readonly CPlayComp playComp = new CPlayComp();
        private double steerSentLastDeg = double.NaN;

        // calm steering: small wobbles smoothed, never faster than the wheels turn
        private double SteerSmooth(double deg, bool steering)
        {
            if (!SteerSmoothOn || !steering)
            {
                steerSmoother.Reset();
                return deg;
            }
            return steerSmoother.Apply(secondsSinceStart, deg, steerDynamics.MaxRateDegS);
        }

        // play compensation: half the measured play ahead in the direction the angle moves
        private double SteerPlay(double deg, bool steering)
        {
            if (!PlayCompOn || !steering) { playComp.Reset(); return deg; }
            return playComp.Apply(deg, steerDynamics.BacklashDeg);
        }

        /// <summary>The angle really sent on the last fix (play compensation in it), for measuring the steering gear.</summary>
        public double SteerSentLastDeg() => double.IsNaN(steerSentLastDeg) ? SteerAimDeg() : steerSentLastDeg;
    }
}
