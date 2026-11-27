using AgLibrary.Logging;
using System;
using System.Collections.Generic;
using System.Linq;

namespace AgOpenGPS
{
    // Measured in the field: every pass's distance off the line, told at its end, and an A/B comparison that changes
    // the steering method from pass to pass (pure pursuit, adaptive pure pursuit, MPC) and tells which did best here.
    // Also: fully automatic tuning of the adaptive pure pursuit's and MPC's own settings, a step at a time, each kept
    // only if the distance off the line did not get worse.
    public partial class FormGPS
    {
        // ---- passes ----

        private readonly CPassTracker passTracker = new CPassTracker();

        /// <summary>The passes of this session, newest first.</summary>
        public readonly List<PassResult> PassLog = new List<PassResult>();

        /// <summary>The steering method as passes are told apart: adaptive pure pursuit is a method of its own.</summary>
        public string PassMethodName => isStanleyUsed ? "Stanley" : isMpcUsed ? "MPC" : isLqrUsed ? "LQR" : isHybridUsed ? "Hybrid" : AdaptivePpOn ? "Adaptive PP" : "Pure pursuit";

        // every fix (with the auto-tune's data): the pass goes on, or has ended
        private void FeedPasses(double offLineM, bool counts)
        {
            bool holding = isBtnAutoSteerOn && trk.idx > -1 && !ct.isContourBtnOn && !yt.isYouTurnTriggered && !isReverse && avgSpeed > 1;
            string line = "";
            if (holding)
            {
                bool same = trk.gArr[trk.idx].mode == TrackMode.AB ? ABLine.isHeadingSameWay : curve.isHeadingSameWay;
                line = trk.idx + (same ? "+" : "-");
            }
            string method = PassMethodName + (KalmanPositionInUse ? KalmanSuffix : "");
            // (where the tractor really is: the guidance's position may be moved sideways by learning or the implement;
            // and the distance off the line without that shift)
            PassResult done = passTracker.Feed(secondsSinceStart, holding, line, method, rawPivotE, rawPivotN, offLineM - guidanceShiftM, counts);
            if (done == null) return;

            // no message while driving: the passes are shown in the smart features' comparison page only
            PassLog.Insert(0, done);
            if (PassLog.Count > 100) PassLog.RemoveAt(PassLog.Count - 1);
            Log.EventWriter("Pass: " + done.Method + " " + done.RmsCm.ToString("0.0") + " cm RMS, " + done.MaxCm.ToString("0") + " cm max, " + done.LengthM.ToString("0") + " m");
            if (AbActive) AbNextMethod();
        }

        private static string TrP(string english) => AgLibrary.Localization.UiTranslator.TrText(english);

        // ---- A/B comparison ----

        private static readonly string[] AbMethods = { "Pure pursuit", "Adaptive PP", "MPC", "LQR", "Hybrid" };
        private int abIndex;
        private int abStartMode;
        private bool abStartAdaptive;
        private DateTime abStarted;

        /// <summary>The A/B comparison runs: the method changes after every pass (this session only).</summary>
        public bool AbActive { get; private set; }

        /// <summary>The passes driven since the comparison began.</summary>
        public IEnumerable<PassResult> AbPasses => PassLog.Where(p => p.When >= abStarted);

        public void StartAb()
        {
            // the passes are not counted in the simulator: a comparison would never move on
            if (AbActive || timerSim.Enabled) return;
            abStartMode = SteerMode;
            abStartAdaptive = AdaptivePpOn;
            abStarted = DateTime.Now;
            abIndex = 0;
            // a change on trial would be judged on driving with other methods: forgotten
            changeTrial.Forget();
            trialBefore = null;
            trialUndo = null;
            AbActive = true;
            SetAbMethod(AbMethods[0]);
            Log.EventWriter("A/B comparison started");
        }

        /// <summary>Ends the comparison: with the best method, or back to the one in use before.</summary>
        public void StopAb(bool takeBest)
        {
            if (!AbActive) return;
            AbActive = false;
            string best = takeBest ? AbBest() : null;
            if (best != null) SetAbMethod(best);
            else
            {
                SetSteerMode(abStartMode);
                if (AdaptivePpOn != abStartAdaptive) SetAdaptivePp(abStartAdaptive);
            }
            // the tuners' data comes from all methods: they start again, quietly (no change by hand)
            ResetAllTuners();
            steerAutoSeen = SteerAutoSeenValues();
            steerAutoSeenContext = SteerAutoContext();
            Log.EventWriter("A/B comparison ended" + (best != null ? ", " + best + " kept" : ""));
        }

        /// <summary>The method that ran closest to the line, once each has at least two passes; null before.</summary>
        /// <summary>A pass driven from the state estimator's position: its method's name ends so.</summary>
        public const string KalmanSuffix = " + Kalman";

        /// <summary>The method without the Kalman ending.</summary>
        public static string BaseMethod(string method) => method.EndsWith(KalmanSuffix) ? method.Substring(0, method.Length - KalmanSuffix.Length) : method;

        public string AbBest()
        {
            var sum = CPassTracker.Summary(AbPasses.Select(p => new PassResult { Method = BaseMethod(p.Method), RmsCm = p.RmsCm, MaxCm = p.MaxCm, LengthM = p.LengthM, When = p.When }));
            if (AbMethods.Any(m => !sum.ContainsKey(m) || sum[m].Passes < 2)) return null;
            return AbMethods.OrderBy(m => sum[m].RmsCm).First();
        }

        // a pass ended: the next method (in the turn, before the next pass)
        private string AbNextMethod()
        {
            abIndex = (abIndex + 1) % AbMethods.Length;
            SetAbMethod(AbMethods[abIndex]);
            return AbMethods[abIndex];
        }

        private void SetAbMethod(string method)
        {
            if (method == "MPC") SetSteerMode(2);
            else if (method == "LQR") SetSteerMode(3);
            else if (method == "Hybrid") SetSteerMode(4);
            else
            {
                SetSteerMode(0);
                bool adaptive = method == "Adaptive PP";
                if (AdaptivePpOn != adaptive) SetAdaptivePp(adaptive);
            }
        }

        // ---- fully automatic: the adaptive pure pursuit's and MPC's own settings ----

        private sealed class MethodSetting
        {
            public string Name;
            public Func<double> Get;
            public Action<double> Set;
            public double Min, Max, Step;
        }

        private int methodTuneNext;
        private readonly Dictionary<string, int> methodTuneDir = new Dictionary<string, int>();
        private Action trialUndo;          // a method setting on trial: how to put it back
        private string trialSettingName;   // and its name (without the direction)

        private MethodSetting[] MethodSettings()
        {
            var ts = Properties.ToolSettings.Default;
            if (LqrActive)
                return new[]
                {
                    new MethodSetting { Name = "LQR steering style", Get = () => ts.setMpc_Calm, Set = v => ts.setMpc_Calm = (int)Math.Round(v), Min = 1, Max = 10, Step = 1 },
                };
            if (MpcActive || HybridActive)
                return new[]
                {
                    new MethodSetting { Name = "MPC steering style", Get = () => ts.setMpc_Calm, Set = v => ts.setMpc_Calm = (int)Math.Round(v), Min = 1, Max = 10, Step = 1 },
                    new MethodSetting { Name = "MPC looks ahead", Get = () => ts.setMpc_HorizonS, Set = v => ts.setMpc_HorizonS = v, Min = 2, Max = 5, Step = 0.5 },
                };
            if (PurePursuitSteers && AdaptivePpOn)
                return new[]
                {
                    new MethodSetting { Name = "Adaptive: shorter in curves", Get = () => ts.setApp_Curve, Set = v => ts.setApp_Curve = (int)Math.Round(v), Min = 0, Max = 10, Step = 1 },
                    new MethodSetting { Name = "Adaptive: room for the dead time", Get = () => ts.setApp_Floor, Set = v => ts.setApp_Floor = (int)Math.Round(v), Min = 10, Max = 40, Step = 3 },
                    new MethodSetting { Name = "Adaptive: longer when weaving", Get = () => ts.setApp_Weave, Set = v => ts.setApp_Weave = (int)Math.Round(v), Min = 0, Max = 60, Step = 10 },
                };
            return new MethodSetting[0];
        }

        /// <summary>
        /// The lowest step of fully automatic tuning, when nothing else wants a change: one of the steering method's own
        /// settings a step on, the way it went last (the other way after an undo). The change goes on trial as the
        /// others: worse, it is put back and the setting rests half an hour; no better either, it rests too.
        /// </summary>
        private string TryTuneMethodSettings()
        {
            MethodSetting[] all = MethodSettings();
            for (int tries = 0; tries < all.Length; tries++)
            {
                MethodSetting s = all[methodTuneNext++ % all.Length];
                if (!methodTuneDir.TryGetValue(s.Name, out int dir)) dir = 1;
                for (int turn = 0; turn < 2; turn++, dir = -dir)
                {
                    string name = s.Name + (dir > 0 ? " +" : " -");
                    if (RecentlyUndone(name)) continue;
                    double old = s.Get();
                    double now = Math.Max(s.Min, Math.Min(s.Max, old + dir * s.Step));
                    if (Math.Abs(now - old) < 1e-9) continue;
                    s.Set(now);
                    SaveMethodSettings();
                    methodTuneDir[s.Name] = dir;
                    trialUndo = () => { s.Set(old); SaveMethodSettings(); };
                    trialSettingName = s.Name;
                    lastAllowedName = name;
                    return TrP(s.Name) + " " + old.ToString("0.#") + " → " + now.ToString("0.#");
                }
            }
            return null;
        }

        private void SaveMethodSettings()
        {
            if (!isEasyDriveMode) Properties.ToolSettings.Default.Save();
            steerAutoSeen = SteerAutoSeenValues();   // its own change, not one by hand
        }

        // the verdict on a method setting's trial (called with the others' verdict)
        private void MethodSettingVerdict(ChangeVerdict v, string name)
        {
            if (trialUndo == null) return;
            if (v == ChangeVerdict.Worse)
            {
                trialUndo();
                // the other way next time
                if (methodTuneDir.TryGetValue(trialSettingName, out int dir)) methodTuneDir[trialSettingName] = -dir;
            }
            else if (v != ChangeVerdict.Better)
                undoneUntil[name] = secondsSinceStart + 1800;   // no better: this way rests a while
            trialUndo = null;
            trialSettingName = null;
        }
    }
}
