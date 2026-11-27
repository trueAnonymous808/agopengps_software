using AgLibrary.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;

namespace AgOpenGPS
{
    // Fully automatic steering tuning: what the tuners suggest is applied by itself - at most one small change a
    // minute and never further than the limits from the values at switch-on (CSteerAutoGuard). It goes on while the
    // steer settings are open (their bars are moved with it, so closing them keeps the change) and pauses only for
    // the Steer Wizard, which calibrates step by step. Every change is shown, logged with its time and reason, and the
    // values at switch-on can be restored.
    public partial class FormGPS
    {
        private double steerAutoLastApplyS = -1e9, steerAutoLastCheckS = -1e9;

        public bool SteerAutoOn => Properties.Settings.Default.setSteerAuto_Enabled;

        /// <summary>What fully automatic tuning is doing now, for the auto-tune window; null while it is off.</summary>
        public string SteerAutoStatus { get; private set; }

        /// <summary>The loaded vehicle's values from when fully automatic was switched on: the limits' centre and
        /// what Restore puts back. Each vehicle profile keeps its own.</summary>
        public SteerBaseline SteerAutoBaseline
        {
            get
            {
                MoveOldSteerBaseline();
                return SteerBaseline.Load(Properties.VehicleSettings.Default.setSteerAuto_Baseline);
            }
        }

        private void SaveSteerBaseline(SteerBaseline b)
        {
            Properties.VehicleSettings.Default.setSteerAuto_Baseline = b.Save();
            if (!isEasyDriveMode) Properties.VehicleSettings.Default.Save();
        }

        // Older versions kept one baseline for the whole program: it goes to the vehicle loaded now (the one it was
        // most likely taken on), once.
        private void MoveOldSteerBaseline()
        {
            string old = Properties.Settings.Default.setSteerAuto_Baseline;
            if (string.IsNullOrEmpty(old)) return;
            if (string.IsNullOrEmpty(Properties.VehicleSettings.Default.setSteerAuto_Baseline))
            {
                Properties.VehicleSettings.Default.setSteerAuto_Baseline = old;
                Properties.VehicleSettings.Default.Save();
            }
            Properties.Settings.Default.setSteerAuto_Baseline = "";
            Properties.Settings.Default.Save();
            Log.EventWriter("Steering auto-tune: the starting values moved to vehicle profile '" + RegistrySettings.vehicleProfileName + "'");
        }

        // The tuners learn one vehicle: another profile loaded, they start again (its own baseline is taken when
        // fully automatic needs one).
        private string steerAutoProfile;

        private void NoteVehicleProfile()
        {
            string profile = RegistrySettings.vehicleProfileName ?? "";
            if (steerAutoProfile == null)
            {
                steerAutoProfile = profile;
                return;
            }
            if (profile == steerAutoProfile) return;
            steerAutoProfile = profile;
            ResetAllTuners();
            steerDynamics.Reset();   // another tractor, another steering gear
            estimator.Reset();       // and another steer angle sensor and IMU to learn
            rollTune.Reset();        // and another antenna and IMU
            steerAutoLastApplyS = secondsSinceStart;   // a minute of the new vehicle's data before any change
            SteerAutoLog.Insert(0, DateTime.Now.ToString("HH:mm") + "  " + TrA("Vehicle") + ": " + profile);
            Log.EventWriter("Steering auto-tune: vehicle profile '" + profile + "' loaded, the tuners start again");
        }

        /// <summary>The automatic changes of this session, newest first: time, what changed and why.</summary>
        public readonly List<string> SteerAutoLog = new List<string>();

        /// <summary>Seconds until the next automatic change may come (one a minute at most).</summary>
        public double SteerAutoWaitS => Math.Max(0, CSteerAutoGuard.CooldownS - (secondsSinceStart - steerAutoLastApplyS));

        public bool SteerWizardOpen => Application.OpenForms.OfType<FormSteerWiz>().Any();

        private static string TrA(string english) => AgLibrary.Localization.UiTranslator.TrText(english);

        /// <summary>The steering values now.</summary>
        public SteerBaseline CurrentSteerValues()
        {
            var vs = Properties.VehicleSettings.Default;
            return new SteerBaseline
            {
                Kp = vs.setAS_Kp,
                MinPwm = vs.setAS_minSteerPWM,
                Cpd = vs.setAS_countsPerDegree,
                Ackermann = vs.setAS_ackerman,
                WasOffset = vs.setAS_wasOffset,
                DeadZoneHeading = vehicle.deadZoneHeading,
                DeadZoneDelay = vehicle.deadZoneDelay,
                Hold = vehicle.goalPointLookAheadHold,
                SpeedFactor = vehicle.goalPointLookAheadMult,
                SideHill = gyd.sideHillCompFactor,
                RollZero = ahrs.rollZero,
                AcquireFactor = vehicle.goalPointAcquireFactor,
                Integral = vehicle.purePursuitIntegralGain,
                UTurnComp = vehicle.uturnCompensation,
                FusionWeight = ahrs.fusionWeight,
                LinMiddle = vs.setAS_WasLinMiddle,
                LinBig = vs.setAS_WasLinBig,
                KpFast = vs.setAS_KpFast,
                RollShift = vs.setAS_RollShiftS,
                RollGain = vs.setAS_RollGain,
                PitRate = vs.setAS_PitRateDegS
            };
        }

        /// <summary>Switches fully automatic tuning on (taking today's values as its baseline) or off.</summary>
        public void SetSteerAuto(bool on)
        {
            Properties.Settings.Default.setSteerAuto_Enabled = on;
            steerAutoSeen = null;   // today's values are the start: nothing changed by hand yet
            if (on)
            {
                SaveSteerBaseline(CurrentSteerValues());
                smartWAS.Start();   // Smart WAS learns in the background too
                steerAutoLastApplyS = secondsSinceStart;   // a minute of fresh data before the first change
            }
            else if (!SteerSettingsOpen())
            {
                smartWAS.Stop();
            }
            Properties.Settings.Default.Save();
            SteerAutoStatus = on ? TrA("Learning...") : null;
            Log.EventWriter("Steering auto-tune: fully automatic " + (on ? "on" : "off"));
        }

        /// <summary>Puts back the values from when fully automatic tuning was switched on.</summary>
        public bool RestoreSteerBaseline()
        {
            SteerBaseline b = SteerAutoBaseline;
            if (b == null) return false;
            WriteSteerValues(b);
            ResetAllTuners();
            Log.EventWriter("Steering auto-tune: starting values restored");
            return true;
        }

        private void ResetAllTuners()
        {
            // a change on trial belongs to the values just replaced
            changeTrial.Forget();
            trialBefore = null;
            trialUndo = null;
            motorTune.Reset();
            deadZoneTune.Reset();
            lookAheadTune.Reset();
            lookAheadSpeedTune.Reset();
            sideHillTune.Reset();
            rollZeroTune.Reset();
            acquireTune.Reset();
            integralTune.Reset();
            uTurnTune.Reset();
            fusionTune.Reset();
            motorTuneFast.Reset();
            steerGeometryCal.Reset();
            smartWAS.Reset();
        }

        private static bool SteerSettingsOpen()
        {
            return Application.OpenForms.OfType<FormSteer>().Any() || Application.OpenForms.OfType<FormSteerWiz>().Any();
        }

        // all values to the settings, the vehicle and the steer module
        private void WriteSteerValues(SteerBaseline v)
        {
            MpcValuesChanging(CurrentSteerValues(), v);
            var vs = Properties.VehicleSettings.Default;
            var ts = Properties.ToolSettings.Default;
            vs.setAS_Kp = (byte)Math.Max(1, Math.Min(200, v.Kp));
            vs.setAS_minSteerPWM = (byte)Math.Max(0, Math.Min(200, v.MinPwm));
            vs.setAS_countsPerDegree = (byte)Math.Max(1, Math.Min(255, v.Cpd));
            vs.setAS_ackerman = (byte)Math.Max(1, Math.Min(200, v.Ackermann));
            vs.setAS_wasOffset = v.WasOffset;
            // each on its bar's step in the steer settings (an open window puts back what its bars show)
            vs.setAS_sideHillComp = gyd.sideHillCompFactor = Math.Max(0, Math.Min(1, Math.Round(v.SideHill, 2)));
            ts.setAS_deadZoneHeading = vehicle.deadZoneHeading = Math.Max(10, Math.Min(500, v.DeadZoneHeading));
            ts.setAS_deadZoneDelay = vehicle.deadZoneDelay = Math.Max(1, Math.Min(10, v.DeadZoneDelay));
            ts.setVehicle_goalPointLookAheadHold = vehicle.goalPointLookAheadHold = Math.Max(1, Math.Min(7, Math.Round(v.Hold, 1)));
            ts.setVehicle_goalPointLookAheadMult = vehicle.goalPointLookAheadMult = Math.Max(0.5, Math.Min(6, Math.Round(v.SpeedFactor, 1)));
            if (v.HasExtras)
            {
                vs.setIMU_rollZero = ahrs.rollZero = Math.Max(-20, Math.Min(20, v.RollZero));
                ts.setVehicle_goalPointAcquireFactor = vehicle.goalPointAcquireFactor = Math.Max(0.2, Math.Min(3, Math.Round(v.AcquireFactor, 2)));
                ts.purePursuitIntegralGainAB = vehicle.purePursuitIntegralGain = Math.Max(0, Math.Min(1, Math.Round(v.Integral, 2)));
                ts.setAS_uTurnCompensation = vehicle.uturnCompensation = Math.Max(0.2, Math.Min(2, Math.Round(v.UTurnComp, 1)));
            }
            if (v.HasExtras2)
            {
                vs.setIMU_fusionWeight2 = ahrs.fusionWeight = Math.Max(0.002, Math.Min(0.4, v.FusionWeight));
                vs.setAS_WasLinMiddle = CWasLinearity.Clamp(v.LinMiddle);
                vs.setAS_WasLinBig = CWasLinearity.Clamp(v.LinBig);
                vs.setAS_KpFast = (byte)Math.Max(0, Math.Min(255, v.KpFast));
                lastSentKp = -2;
                lastKpSendS = -1e9;
            }
            if (v.HasExtras3)
            {
                vs.setAS_RollShiftS = Math.Max(-0.3, Math.Min(0.3, v.RollShift));
                vs.setAS_RollGain = Math.Max(0.5, Math.Min(1.5, v.RollGain));
                vs.setAS_PitRateDegS = Math.Max(1, Math.Min(15, v.PitRate));
            }
            if (!isEasyDriveMode)
            {
                vs.Save();
                ts.Save();
            }
            SendSteerSettings();

            // open steer settings show the new values too: closing them writes their bars back
            FormSteer steer = Application.OpenForms.OfType<FormSteer>().FirstOrDefault();
            steer?.ShowSteerValues(v);
            steerAutoSeen = SteerAutoSeenValues();
            steerAutoSeenContext = SteerAutoContext();
        }

        // the values as fully automatic last wrote or saw them: anything else is a change by hand (the steer
        // settings, the Steer Wizard, the smart features' buttons, the IMU zero)
        private string steerAutoSeen;

        // the profiles and Easy Drive the values were seen with: these loading others is no change by hand
        private string SteerAutoContext() =>
            (RegistrySettings.vehicleProfileName ?? "") + "|" + (RegistrySettings.toolProfileName ?? "") + "|" + isEasyDriveMode;

        private string steerAutoSeenContext;

        // the tuned values and what their data rests on: the vehicle's geometry, the antenna and the dead-time lead
        private string SteerAutoSeenValues()
        {
            var c = System.Globalization.CultureInfo.InvariantCulture;
            var g = vehicle.VehicleConfig;
            return CurrentSteerValues().Save() + "|" + string.Join(";", g.Wheelbase.ToString("R", c), g.AntennaHeight.ToString("R", c),
                g.AntennaPivot.ToString("R", c), g.AntennaOffset.ToString("R", c), vehicle.maxSteerAngle.ToString("R", c), SteerLeadS.ToString("R", c),
                SteerModeName, AdaptivePpOn, SteerSmoothOn, PlayCompOn, KalmanPositionOn, FieldLearningOn, ImplementKeeperOn,
                Properties.ToolSettings.Default.setMpc_Calm, Properties.ToolSettings.Default.setMpc_HorizonS.ToString("R", c),
                Properties.ToolSettings.Default.setApp_Curve, Properties.ToolSettings.Default.setApp_Floor, Properties.ToolSettings.Default.setApp_Weave);
        }

        /// <summary>
        /// A change by hand since the last look: the tuners' data belongs to the old values, and a change on trial
        /// is no longer judged (undoing it would put back the old values over the hand-made change).
        /// </summary>
        private bool ChangedByHand()
        {
            string now = SteerAutoSeenValues();
            string context = SteerAutoContext();
            bool changed = steerAutoSeen != null && now != steerAutoSeen;
            bool otherProfile = steerAutoSeenContext != null && context != steerAutoSeenContext;
            steerAutoSeen = now;
            steerAutoSeenContext = context;
            if (!changed) return false;
            MpcLearnQuickly();
            ResetAllTuners();
            steerAutoHeld.Clear();
            steerAutoLastApplyS = secondsSinceStart;   // a fresh minute with the new values before any change
            if (otherProfile)
            {
                // another vehicle or implement, or Easy Drive on or off: its values, not a change by hand
                Log.EventWriter("Steering auto-tune (automatic): other values loaded with the profile, the tuners start again");
                return true;
            }
            string text = TrA("Changed by hand, the tuners start again");
            SteerAutoStatus = text;
            SteerAutoLog.Insert(0, DateTime.Now.ToString("HH:mm") + "  " + text);
            if (SteerAutoLog.Count > 30) SteerAutoLog.RemoveAt(SteerAutoLog.Count - 1);
            Log.EventWriter("Steering auto-tune (automatic): values changed by hand, the tuners start again");
            return true;
        }

        private void SendSteerSettings()
        {
            var vs = Properties.VehicleSettings.Default;
            p_252.pgn[p_252.gainProportional] = vs.setAS_Kp;
            p_252.pgn[p_252.minPWM] = vs.setAS_minSteerPWM;
            p_252.pgn[p_252.countsPerDegree] = vs.setAS_countsPerDegree;
            p_252.pgn[p_252.ackerman] = vs.setAS_ackerman;
            p_252.pgn[p_252.wasOffsetHi] = unchecked((byte)(vs.setAS_wasOffset >> 8));
            p_252.pgn[p_252.wasOffsetLo] = unchecked((byte)vs.setAS_wasOffset);
            p_252.pgn[p_252.highPWM] = vs.setAS_highSteerPWM;
            p_252.pgn[p_252.lowPWM] = vs.setAS_lowSteerPWM;
            SendPgnToLoop(p_252.pgn);
        }

        /// <summary>Every few seconds while fully automatic: applies the first suggestion that is ready and within limits.</summary>
        private void SteerAutoTick()
        {
            NoteVehicleProfile();
            NoteToolProfile();
            if (!SteerAutoOn) { SteerAutoStatus = null; return; }
            if (secondsSinceStart - steerAutoLastCheckS < 5) return;
            steerAutoLastCheckS = secondsSinceStart;

            if (SteerWizardOpen) { SteerAutoStatus = TrA("Paused while the Steer Wizard is open"); return; }
            // the A/B comparison changes the method from pass to pass: no other change may mix in
            if (AbActive) { SteerAutoStatus = TrA("Paused during the A/B comparison"); return; }
            // the open steer settings' bars are what the tractor has now
            Application.OpenForms.OfType<FormSteer>().FirstOrDefault()?.PutBarsInSettings();
            if (ChangedByHand()) return;
            if (timerSim.Enabled) { SteerAutoStatus = TrA("Paused in the simulator"); return; }
            // on the road (route autosteer) nothing changes: the data still comes, the changes wait for the field
            if (routeSteerActive) { SteerAutoStatus = TrA("Paused while route autosteer steers"); return; }
            // a change on trial is decided before any other comes
            if (CheckChangeOnTrial()) return;
            if (secondsSinceStart - steerAutoLastApplyS < CSteerAutoGuard.CooldownS) { SteerAutoStatus = TrA("Learning..."); return; }
            SteerBaseline b = SteerAutoBaseline;
            if (b == null) { SetSteerAuto(true); return; }
            if (!b.HasExtras2)
            {
                // a baseline from before fusion, linearity and the fast Kp: today's values are their starting point
                SteerBaseline now2 = CurrentSteerValues();
                b.FusionWeight = now2.FusionWeight;
                b.LinMiddle = now2.LinMiddle;
                b.LinBig = now2.LinBig;
                b.KpFast = now2.KpFast;
                SaveSteerBaseline(b);
            }
            if (!b.HasExtras3)
            {
                // a baseline from before the roll timing, the roll height and pit mode: today's values are their start
                SteerBaseline now3 = CurrentSteerValues();
                b.RollShift = now3.RollShift;
                b.RollGain = now3.RollGain;
                b.PitRate = now3.PitRate;
                SaveSteerBaseline(b);
            }
            if (!b.HasExtras)
            {
                // a baseline from before the newer tuners: today's values become their limits' centre
                SteerBaseline now = CurrentSteerValues();
                b.RollZero = now.RollZero;
                b.AcquireFactor = now.AcquireFactor;
                b.Integral = now.Integral;
                b.UTurnComp = now.UTurnComp;
                SaveSteerBaseline(b);
            }

            try
            {
                SteerBaseline before = CurrentSteerValues();
                trialUndo = null;
                string done = TryAutoApply(b);
                if (done == null) { SteerAutoStatus = TrA("Learning..."); return; }
                steerAutoLastApplyS = secondsSinceStart;
                // on trial: the distance off the line before and after decides whether it stays
                trialBefore = before;
                changeTrial.Begin(secondsSinceStart, lastAllowedName);
                SteerAutoStatus = TrA("Last change") + ": " + done;
                SteerAutoLog.Insert(0, DateTime.Now.ToString("HH:mm") + "  " + done);
                if (SteerAutoLog.Count > 30) SteerAutoLog.RemoveAt(SteerAutoLog.Count - 1);
                // no pop-up while driving: the change is shown in the smart features window (status and log) and logged
                Log.EventWriter("Steering auto-tune (automatic): " + done);
            }
            catch (Exception ex)
            {
                Log.EventWriter("Steering auto-tune (automatic) error: " + ex.Message);
            }
        }

        // ---- suggestions the limits hold back ----

        // what each tuner would change but may not, because it is further from the starting values than the limits
        // allow: shown in the smart features window, so the driver can take today's values as the new start
        private readonly Dictionary<string, string> steerAutoHeld = new Dictionary<string, string>();

        /// <summary>The suggestions the limits hold back now, one line each.</summary>
        public IList<string> SteerAutoHeldBack => steerAutoHeld.Values.ToList();

        /// <summary>
        /// Whether a tuner's suggestion may be applied: it wants a change and the change is within the limits. A
        /// change the limits stop is noted (and logged the first time); one no longer wanted is forgotten.
        /// </summary>
        private bool Allowed(string name, bool wants, bool withinLimits, string start, string suggested)
        {
            if (wants && RecentlyUndone(name))
            {
                steerAutoHeld[name] = name + ": " + TrA("undone recently, tried again later");
                return false;
            }
            if (!wants || withinLimits)
            {
                steerAutoHeld.Remove(name);
                if (wants) lastAllowedName = name;
                return wants;
            }
            string line = name + ": " + TrA("starting value") + " " + start + ", " + TrA("suggested") + " " + suggested;
            if (!steerAutoHeld.ContainsKey(name))
            {
                SteerAutoLog.Insert(0, DateTime.Now.ToString("HH:mm") + "  " + TrA("At the limit") + " - " + line);
                if (SteerAutoLog.Count > 30) SteerAutoLog.RemoveAt(SteerAutoLog.Count - 1);
                Log.EventWriter("Steering auto-tune (automatic): held back by the limits - " + line);
            }
            steerAutoHeld[name] = line;
            return false;
        }

        /// <summary>
        /// Today's values become the starting values: the limits move with them. For a tractor that was set far off:
        /// once it drives well and a tuner keeps pointing the same way, the driver lets it go on.
        /// </summary>
        public void TakeSteerBaselineNow()
        {
            SaveSteerBaseline(CurrentSteerValues());
            steerAutoHeld.Clear();
            changeTrial.Forget();
            trialBefore = null;
            SteerAutoLog.Insert(0, DateTime.Now.ToString("HH:mm") + "  " + TrA("New starting values"));
            if (SteerAutoLog.Count > 30) SteerAutoLog.RemoveAt(SteerAutoLog.Count - 1);
            Log.EventWriter("Steering auto-tune: today's values are the new starting values");
        }

        // the first tuner with a suggestion within the limits wins: the zero first, then the geometry, the motor and
        // the rest - each later one builds on the earlier ones
        private string TryAutoApply(SteerBaseline b)
        {
            SteerBaseline now = CurrentSteerValues();

            {
                bool wasWants = smartWAS.HasValidCalibration && Math.Abs(smartWAS.RecommendedOffset) >= CSteerAutoGuard.MinWasStepDeg;
                double offsetDeg = wasWants ? smartWAS.RecommendedOffset : 0;
                int counts = (int)Math.Round(offsetDeg * now.Cpd);
                if (Allowed(TrA("Smart WAS"), wasWants, Math.Abs(counts) <= 50 && CSteerAutoGuard.WasOffsetOk(b, now.WasOffset + counts, now.Cpd),
                    b.WasOffset.ToString(), (now.WasOffset + counts).ToString()))
                {
                    int old = now.WasOffset;
                    now.WasOffset += counts;
                    WriteSteerValues(now);
                    smartWAS.ApplyOffsetCorrection(offsetDeg);
                    ResetLineTuners();
                    return TrA("Smart WAS") + ": " + offsetDeg.ToString("+0.0;-0.0") + "° (" + old + " → " + now.WasOffset + ")";
                }
            }

            // the roll zero right after the WAS zero: every position across a slope depends on it
            TuneVerdict rv = rollZeroTune.Recommend(now.RollZero, out double rollZero);
            if (Allowed(TrA("Roll zero"), rv == TuneVerdict.RollZeroOff, CSteerAutoGuard.RollZeroOk(b, rollZero),
                b.RollZero.ToString("0.00"), rollZero.ToString("0.00")))
            {
                string text = TrA("Roll zero") + " " + now.RollZero.ToString("0.00") + " → " + rollZero.ToString("0.00");
                now.RollZero = rollZero;
                WriteSteerValues(now);
                rollZeroTune.Reset();
                ResetLineTuners();
                return text + "  -  " + SteerVerdicts.Text(rv);
            }

            // the roll compensation's timing and height, right after its zero: every position on rough ground rests on them
            TuneVerdict rtv = RollTuneRecommend(now, out double rollShift, out double rollGain);
            if (Allowed(TrA("Roll compensation"), rtv == TuneVerdict.Recommend,
                CSteerAutoGuard.RollShiftOk(b, rollShift) && CSteerAutoGuard.RollGainOk(b, rollGain),
                b.RollShift.ToString("0.00") + " s / " + b.RollGain.ToString("0.00"), rollShift.ToString("0.00") + " s / " + rollGain.ToString("0.00")))
            {
                string text = TrA("Roll compensation") + " " + now.RollShift.ToString("0.00") + " s / " + now.RollGain.ToString("0.00") + " → " +
                    rollShift.ToString("0.00") + " s / " + rollGain.ToString("0.00");
                now.RollShift = rollShift;
                now.RollGain = rollGain;
                WriteSteerValues(now);
                rollTune.Reset();
                ResetLineTuners();
                return text;
            }

            TuneVerdict pv = pitGuard.Recommend(now.PitRate, out double pitRate, out double pitNowCm, out double pitOtherCm);
            if (Allowed(TrA("Pit mode"), pv == TuneVerdict.Recommend && Math.Abs(pitRate - now.PitRate) > 1e-9,
                CSteerAutoGuard.PitRateOk(b, pitRate), b.PitRate.ToString("0.0") + " °/s", pitRate.ToString("0.0") + " °/s"))
            {
                string text = TrA("Pit mode") + " " + now.PitRate.ToString("0.0") + " → " + pitRate.ToString("0.0") + " °/s (" +
                    pitNowCm.ToString("0.0") + " → " + pitOtherCm.ToString("0.0") + " cm)";
                now.PitRate = pitRate;
                WriteSteerValues(now);
                return text;
            }

            int? cpd = steerGeometryCal.RecommendedCpd(now.Cpd);
            int? ackermann = steerGeometryCal.RecommendedAckermann(now.Ackermann);
            bool cpdGo = Allowed(TrA("Counts per degree"), cpd.HasValue && cpd.Value != now.Cpd, cpd.HasValue && CSteerAutoGuard.CpdOk(b, cpd.Value),
                b.Cpd.ToString(), cpd.HasValue ? cpd.Value.ToString() : "");
            bool ackGo = Allowed(TrA("Ackermann"), ackermann.HasValue && ackermann.Value != now.Ackermann,
                ackermann.HasValue && CSteerAutoGuard.AckermannOk(b, ackermann.Value), b.Ackermann.ToString(), ackermann.HasValue ? ackermann.Value.ToString() : "");
            if (cpdGo || ackGo)
            {
                string text = "";
                if (cpdGo) { text += TrA("Counts per degree") + " " + now.Cpd + " → " + cpd.Value; now.Cpd = cpd.Value; }
                if (ackGo) { text += (text.Length > 0 ? ", " : "") + TrA("Ackermann") + " " + now.Ackermann + " → " + ackermann.Value; now.Ackermann = ackermann.Value; }
                WriteSteerValues(now);
                steerGeometryCal.Reset();
                ResetLineTuners();
                return text;
            }

            // the sensor's linearity right after its scale (counts per degree, Ackermann)
            TuneVerdict linv = LinearityRecommend(out double linMiddle, out double linBig);
            if (Allowed(TrA("Sensor linearity"), linv == TuneVerdict.SensorNotLinear,
                CSteerAutoGuard.LinearityOk(b, linMiddle, linBig),
                b.LinMiddle.ToString("0.00") + "/" + b.LinBig.ToString("0.00"), linMiddle.ToString("0.00") + "/" + linBig.ToString("0.00")))
            {
                string text = TrA("Sensor linearity") + " " + now.LinMiddle.ToString("0.00") + "/" + now.LinBig.ToString("0.00") + " → " +
                    linMiddle.ToString("0.00") + "/" + linBig.ToString("0.00");
                now.LinMiddle = linMiddle;
                now.LinBig = linBig;
                WriteSteerValues(now);
                return text + "  -  " + SteerVerdicts.Text(linv);
            }

            TuneVerdict mv = motorTune.Recommend(now.Kp, now.MinPwm, out int kp, out int pwm);
            if (Allowed("Kp / " + TrA("Minimum PWM"), mv != TuneVerdict.Collecting && mv != TuneVerdict.Good && (kp != now.Kp || pwm != now.MinPwm),
                CSteerAutoGuard.KpOk(b, kp) && CSteerAutoGuard.MinPwmOk(b, pwm), b.Kp + " / " + b.MinPwm, kp + " / " + pwm))
            {
                string text = "Kp " + now.Kp + " → " + kp + ", " + TrA("Minimum PWM") + " " + now.MinPwm + " → " + pwm;
                now.Kp = kp;
                now.MinPwm = pwm;
                WriteSteerValues(now);
                motorTune.Reset();
                return text + "  -  " + SteerVerdicts.Text(mv);
            }

            TuneVerdict kfv = FastKpRecommend(out int kpFast);
            if (Allowed(TrA("Kp above 12 km/h"), (kfv == TuneVerdict.Oscillates || kfv == TuneVerdict.Slow) && kpFast != (int)now.KpFast,
                CSteerAutoGuard.KpFastOk(b, kpFast), "Kp " + b.Kp, kpFast.ToString()))
            {
                string text = TrA("Kp above 12 km/h") + " " + (now.KpFast > 0 ? now.KpFast.ToString("0") : "-") + " → " + kpFast;
                now.KpFast = kpFast;
                WriteSteerValues(now);
                motorTuneFast.Reset();
                return text + "  -  " + SteerVerdicts.Text(kfv);
            }

            if (FusionTunable)
            {
                TuneVerdict fuv = fusionTune.Recommend(now.FusionWeight, 0.002, 0.4, out double weight);
                if (Allowed(TrA("IMU fusion"), fuv == TuneVerdict.FusionDrifts || fuv == TuneVerdict.FusionNoisy, CSteerAutoGuard.FusionOk(b, weight),
                    b.FusionWeight.ToString("0.000"), weight.ToString("0.000")))
                {
                    string text = TrA("IMU fusion") + " " + now.FusionWeight.ToString("0.000") + " → " + weight.ToString("0.000");
                    now.FusionWeight = weight;
                    WriteSteerValues(now);
                    fusionTune.Reset();
                    return text + "  -  " + SteerVerdicts.Text(fuv);
                }
            }

            TuneVerdict dv = deadZoneTune.Recommend(now.DeadZoneHeading * 0.01, now.DeadZoneDelay, 0.1, 5, 1, 10, out double zone, out int delay);
            int zoneCenti = (int)Math.Round(zone * 100);
            if (Allowed(TrA("Dead zone"), dv != TuneVerdict.Collecting && dv != TuneVerdict.Good && (zoneCenti != now.DeadZoneHeading || delay != now.DeadZoneDelay),
                CSteerAutoGuard.DeadZoneOk(b, zoneCenti) && CSteerAutoGuard.DelayOk(b, delay),
                (b.DeadZoneHeading * 0.01).ToString("0.0") + " / " + b.DeadZoneDelay, zone.ToString("0.0") + " / " + delay))
            {
                string text = TrA("Dead zone") + " " + (now.DeadZoneHeading * 0.01).ToString("0.0") + " → " + zone.ToString("0.0") + ", " +
                    TrA("Delay") + " " + now.DeadZoneDelay + " → " + delay;
                now.DeadZoneHeading = zoneCenti;
                now.DeadZoneDelay = delay;
                WriteSteerValues(now);
                deadZoneTune.Reset();
                return text + "  -  " + SteerVerdicts.Text(dv);
            }

            TuneVerdict lv = lookAheadTune.Recommend(now.Hold, 1, 7, out double hold);
            if (Allowed(TrA("Look-ahead"), lv != TuneVerdict.Collecting && lv != TuneVerdict.Good && Math.Abs(hold - now.Hold) > 1e-9,
                CSteerAutoGuard.HoldOk(b, hold), b.Hold.ToString("0.0"), hold.ToString("0.0"))
                && SimulationAccepts(TrA("Look-ahead"), now, hold, now.SpeedFactor, now.AcquireFactor))
            {
                string text = TrA("Look-ahead") + " " + now.Hold.ToString("0.0") + " → " + hold.ToString("0.0");
                now.Hold = hold;
                WriteSteerValues(now);
                lookAheadTune.Reset();
                lookAheadSpeedTune.Reset();
                return text + "  -  " + SteerVerdicts.Text(lv);
            }

            TuneVerdict fv = lookAheadSpeedTune.Recommend(now.SpeedFactor, 0.5, 6, out double factor);
            if (Allowed(TrA("Speed factor"), fv != TuneVerdict.Collecting && fv != TuneVerdict.Good && Math.Abs(factor - now.SpeedFactor) > 1e-9,
                CSteerAutoGuard.SpeedFactorOk(b, factor), b.SpeedFactor.ToString("0.0"), factor.ToString("0.0"))
                && SimulationAccepts(TrA("Speed factor"), now, now.Hold, factor, now.AcquireFactor))
            {
                string text = TrA("Speed factor") + " " + now.SpeedFactor.ToString("0.0") + " → " + factor.ToString("0.0");
                now.SpeedFactor = factor;
                WriteSteerValues(now);
                lookAheadSpeedTune.Reset();
                lookAheadTune.Reset();
                return text + "  -  " + SteerVerdicts.Text(fv, true);
            }

            TuneVerdict av = acquireTune.Recommend(now.AcquireFactor, 0.2, 3, out double acquire);
            if (Allowed(TrA("Acquire factor"), av != TuneVerdict.Collecting && av != TuneVerdict.Good && Math.Abs(acquire - now.AcquireFactor) > 1e-9,
                CSteerAutoGuard.AcquireOk(b, acquire), b.AcquireFactor.ToString("0.00"), acquire.ToString("0.00"))
                && SimulationAccepts(TrA("Acquire factor"), now, now.Hold, now.SpeedFactor, acquire))
            {
                string text = TrA("Acquire factor") + " " + now.AcquireFactor.ToString("0.00") + " → " + acquire.ToString("0.00");
                now.AcquireFactor = acquire;
                WriteSteerValues(now);
                acquireTune.Reset();
                return text + "  -  " + SteerVerdicts.Text(av);
            }

            TuneVerdict iv = integralTune.Recommend(now.Integral, 0, 1, out double integral);
            if (Allowed(TrA("Integral"), iv != TuneVerdict.Collecting && iv != TuneVerdict.Good && Math.Abs(integral - now.Integral) > 1e-9,
                CSteerAutoGuard.IntegralOk(b, integral), b.Integral.ToString("0.00"), integral.ToString("0.00")))
            {
                string text = TrA("Integral") + " " + now.Integral.ToString("0.00") + " → " + integral.ToString("0.00");
                now.Integral = integral;
                WriteSteerValues(now);
                integralTune.Reset();
                return text + "  -  " + SteerVerdicts.Text(iv);
            }

            TuneVerdict uv = uTurnTune.Recommend(now.UTurnComp, 0.2, 2, out double uturn);
            if (Allowed(TrA("U-turn compensation"), uv != TuneVerdict.Collecting && uv != TuneVerdict.Good && Math.Abs(uturn - now.UTurnComp) > 1e-9,
                CSteerAutoGuard.UTurnOk(b, uturn), b.UTurnComp.ToString("0.0"), uturn.ToString("0.0")))
            {
                string text = TrA("U-turn compensation") + " " + now.UTurnComp.ToString("0.0") + " → " + uturn.ToString("0.0");
                now.UTurnComp = uturn;
                WriteSteerValues(now);
                uTurnTune.Reset();
                return text + "  -  " + SteerVerdicts.Text(uv);
            }

            TuneVerdict sv = sideHillTune.Recommend(now.SideHill, 0, 1, out double comp);
            if (Allowed(TrA("Side-hill compensation"), sv == TuneVerdict.Recommend, CSteerAutoGuard.SideHillOk(b, comp),
                b.SideHill.ToString("0.00"), comp.ToString("0.00")))
            {
                string text = TrA("Side-hill compensation") + " " + now.SideHill.ToString("0.00") + " → " + comp.ToString("0.00");
                now.SideHill = comp;
                WriteSteerValues(now);
                sideHillTune.Reset();
                ResetLineTuners();
                return text + "  -  " + SteerVerdicts.Text(sv);
            }

            // nothing else wants a change: the steering method's own settings, a step at a time
            return TryTuneMethodSettings();
        }
    }
}
