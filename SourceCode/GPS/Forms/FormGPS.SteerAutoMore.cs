using AgLibrary.Logging;
using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace AgOpenGPS
{
    // More for the steering's accuracy: what the steering gear itself does (dead time, speed, play), dead-time
    // compensation of the angle sent, checking every automatic change against the distance off the line (and undoing
    // a worse one), a simulated try-out of look-ahead changes, the track gap measured on the ground for the antenna's
    // side offset, and steering values kept for each implement.
    public partial class FormGPS
    {
        public readonly CSteerDynamics steerDynamics = new CSteerDynamics();
        private readonly CLeadComp leadComp = new CLeadComp();
        public readonly CChangeVerifier changeTrial = new CChangeVerifier();
        private SteerBaseline trialBefore;
        private string lastAllowedName;
        private readonly Dictionary<string, double> undoneUntil = new Dictionary<string, double>();
        private double dynamicsAnalyzedS = -1e9;

        /// <summary>The last verdicts on automatic changes, for the window.</summary>
        public string LastTrialResult { get; private set; }

        // every fix: the steering gear and the distance off the line
        private void FeedSteerMore(bool steering, double sentDeg, double offLineM, bool onLine)
        {
            bool module = steerModuleConnectedCounter <= 30;
            steerDynamics.AddSample(secondsSinceStart, SteerSentLastDeg(), mc.actualSteerAngleDegrees, steering && module && avgSpeed > 1);
            if (secondsSinceStart - dynamicsAnalyzedS > 10)
            {
                dynamicsAnalyzedS = secondsSinceStart;
                steerDynamics.Analyze();
            }
            changeTrial.Feed(secondsSinceStart, offLineM, avgSpeed, Math.Abs(mc.actualSteerAngleDegrees) >= 5, onLine);
            pitGuard.FeedXte(secondsSinceStart, offLineM, onLine);
            FeedPasses(offLineM, onLine);
            LearnFromFix(offLineM, onLine);
            NoteForward();
        }

        // ---- dead-time compensation ----

        /// <summary>The steering's dead time to make up for (s); 0: off.</summary>
        public double SteerLeadS => Math.Max(0, Math.Min(0.6, Properties.VehicleSettings.Default.setAS_LeadS));

        /// <summary>The angle for the module, a little ahead while it changes (only steering forwards, autosteer on).</summary>
        private double SteerLead(double askedDeg, bool steeringNow)
        {
            return leadComp.Apply(secondsSinceStart, askedDeg, SteerLeadS, steeringNow);
        }

        public void SetSteerLead(double seconds)
        {
            Properties.VehicleSettings.Default.setAS_LeadS = Math.Round(Math.Max(0, Math.Min(0.6, seconds)), 2);
            if (!isEasyDriveMode) Properties.VehicleSettings.Default.Save();
            Log.EventWriter("Steering: dead-time compensation " + Properties.VehicleSettings.Default.setAS_LeadS.ToString("0.00") + " s");
        }

        // ---- checking automatic changes ----

        /// <summary>
        /// While a change is on trial nothing else changes. Its verdict: worse - the values before it come back and
        /// that tuner waits half an hour; better or the same - kept.
        /// </summary>
        private bool CheckChangeOnTrial()
        {
            if (!changeTrial.Active) return false;
            ChangeVerdict v = changeTrial.Evaluate(secondsSinceStart, out double beforeCm, out double afterCm);
            if (v == ChangeVerdict.Pending)
            {
                SteerAutoStatus = TrA("Checking the last change") + " (" + changeTrial.Name + ")...";
                return true;
            }
            string name = changeTrial.Name;
            MethodSettingVerdict(v, name);   // a steering method's own setting on trial: put back or rested
            string figures = double.IsNaN(beforeCm) ? "" : " (" + beforeCm.ToString("0.0") + " → " + afterCm.ToString("0.0") + " cm)";
            string text;
            if (v == ChangeVerdict.Worse && trialBefore != null)
            {
                WriteSteerValues(trialBefore);
                ResetAllTuners();
                changeTrial.Forget();
                undoneUntil[name] = secondsSinceStart + 1800;
                text = TrA("Undone, it steered worse") + ": " + name + figures;
            }
            else if (v == ChangeVerdict.Unclear) text = TrA("Kept, too little comparable driving to check") + ": " + name;
            else text = TrA("Checked, kept") + ": " + name + figures;
            trialBefore = null;
            LastTrialResult = text;
            steerAutoLastApplyS = secondsSinceStart;   // a fresh minute before the next one
            SteerAutoLog.Insert(0, DateTime.Now.ToString("HH:mm") + "  " + text);
            if (SteerAutoLog.Count > 30) SteerAutoLog.RemoveAt(SteerAutoLog.Count - 1);
            Log.EventWriter("Steering auto-tune (automatic): " + text);
            SteerAutoStatus = text;
            return true;
        }

        private bool RecentlyUndone(string name) => undoneUntil.TryGetValue(name, out double until) && until > secondsSinceStart;

        // ---- a simulated try-out of look-ahead changes ----

        /// <summary>
        /// Whether a look-ahead change is worth trying on the tractor: once the steering gear is measured, it is
        /// tried in the simulation first. Not measured yet: no opinion (the real check after it still comes).
        /// </summary>
        private bool SimulationAccepts(string name, SteerBaseline now, double hold, double mult, double acquire)
        {
            if (!steerDynamics.Ready) return true;
            var current = new CSteerSim.Look { Hold = now.Hold, Mult = now.SpeedFactor, Acquire = now.AcquireFactor };
            var candidate = new CSteerSim.Look { Hold = hold, Mult = mult, Acquire = acquire };
            double speed = avgSpeed > 3 ? avgSpeed : 8;
            bool ok = CSteerSim.NotWorse(current, candidate, vehicle.VehicleConfig.Wheelbase, speed, steerDynamics.DelayS - SteerLeadS * 0.8,
                steerDynamics.MaxRateDegS, steerDynamics.BacklashDeg, vehicle.maxSteerAngle, out double nowCm, out double candCm);
            if (!ok)
                steerAutoHeld[name] = name + ": " + TrA("worse in the simulation") + " (" + nowCm.ToString("0.0") + " → " + candCm.ToString("0.0") + " cm)";
            return ok;
        }

        // ---- the track gap: the antenna's side offset from a mark on the ground ----

        /// <summary>The track gap wizard: one at a time, beside the main window (the driver drives meanwhile).</summary>
        public void OpenTrackGapWizard()
        {
            foreach (Form f in Application.OpenForms)
                if (f is FormTrackGapWizard open)
                {
                    open.Activate();
                    return;
                }
            new FormTrackGapWizard(this).Show(this);
        }

        /// <summary>
        /// The gap measured between a mark left on the first pass and the same point of the tractor on the pass back
        /// along the same line: half of it is how far the tractor runs beside where the program thinks. The side is the
        /// mark's side, seen from the seat on the pass back. Returns the new antenna offset (m).
        /// </summary>
        public double ApplyTrackGap(double gapCm, bool markOnLeft)
        {
            double change = TrackGap.Correction(gapCm, markOnLeft);
            var vs = Properties.VehicleSettings.Default;
            double old = vs.setVehicle_antennaOffset;
            vs.setVehicle_antennaOffset = Math.Round(old + change, 3);
            vehicle.VehicleConfig.AntennaOffset = vs.setVehicle_antennaOffset;
            if (!isEasyDriveMode) vs.Save();
            string text = TrA("Antenna offset") + " " + (old * 100).ToString("0.0") + " → " + (vs.setVehicle_antennaOffset * 100).ToString("0.0") + " cm"
                + " (" + TrA("track gap") + " " + gapCm.ToString("0.0") + " cm)";
            SteerAutoLog.Insert(0, DateTime.Now.ToString("HH:mm") + "  " + text);
            Log.EventWriter("Steering: " + text);
            return vs.setVehicle_antennaOffset;
        }

        // ---- steering values for each implement ----

        private string steerToolProfile;

        public bool PerToolTuning => Properties.Settings.Default.setSteerAuto_PerTool;

        public int ToolTuneCount => ToolTunes.Read(Properties.VehicleSettings.Default.setSteerAuto_ToolTunes).Count;

        public void SetPerToolTuning(bool on)
        {
            Properties.Settings.Default.setSteerAuto_PerTool = on;
            Properties.Settings.Default.Save();
            steerToolProfile = null;   // the implement now in use starts the list
        }

        /// <summary>
        /// Another implement loaded: the steering values used with the one before are kept for it, and the ones kept for
        /// the new one (if any) come back. The tuners start again: they learnt the other implement.
        /// </summary>
        private void NoteToolProfile()
        {
            if (!PerToolTuning) return;
            string tool = RegistrySettings.toolProfileName ?? "";
            if (steerToolProfile == null)
            {
                steerToolProfile = tool;
                return;
            }
            if (tool == steerToolProfile) return;

            var vs = Properties.VehicleSettings.Default;
            Dictionary<string, string> map = ToolTunes.Read(vs.setSteerAuto_ToolTunes);
            if (steerToolProfile.Length > 0) map[steerToolProfile] = CurrentSteerValues().Save();
            string before = steerToolProfile;
            steerToolProfile = tool;
            string text;
            if (map.TryGetValue(tool, out string kept) && SteerBaseline.Load(kept) is SteerBaseline values)
            {
                WriteSteerValues(values);
                if (SteerAutoBaseline != null) SaveSteerBaseline(values);   // the limits go with the implement's values
                text = TrA("Implement") + ": " + tool + " - " + TrA("its steering values are back");
            }
            else text = TrA("Implement") + ": " + tool + " - " + TrA("no steering values kept for it yet");
            vs.setSteerAuto_ToolTunes = ToolTunes.Write(map);
            if (!isEasyDriveMode) vs.Save();
            ResetAllTuners();
            changeTrial.Forget();
            trialBefore = null;
            steerAutoLastApplyS = secondsSinceStart;
            SteerAutoLog.Insert(0, DateTime.Now.ToString("HH:mm") + "  " + text);
            if (SteerAutoLog.Count > 30) SteerAutoLog.RemoveAt(SteerAutoLog.Count - 1);
            Log.EventWriter("Steering: implement '" + before + "' -> '" + tool + "': " + text);
        }
    }
}
