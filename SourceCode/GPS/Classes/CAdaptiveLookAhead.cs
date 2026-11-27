using System;
using System.Collections.Generic;

namespace AgOpenGPS
{
    /// <summary>
    /// Adaptive pure pursuit: the look-ahead worked out again on every fix from the one the settings give - shorter in
    /// a tight curve (less cut off), never shorter than the steering's dead time allows (no weaving on slow
    /// hydraulics), a little longer while the tractor weaves across the line, and changed smoothly.
    /// </summary>
    public sealed class CAdaptiveLookAhead
    {
        public const double MinCurveFactor = 0.5;      // a curve shortens it to half at most
        public const double DefaultCurveGain = 2.0, DefaultFloorGain = 2.5, DefaultMaxWeave = 1.35;

        /// <summary>How much a curve shortens it: base / (1 + gain × base × curvature); 0: not at all.</summary>
        public double CurveGain = DefaultCurveGain;
        /// <summary>How many dead times of driving it must at least reach (more: calmer on a slow steering).</summary>
        public double FloorGain = DefaultFloorGain;
        /// <summary>Weaving lengthens it by this factor at most (1: not at all).</summary>
        public double MaxWeaveFactor = DefaultMaxWeave;
        public const double WeaveWindowS = 15;         // crossings of the line counted over this time
        public const int WeaveCrossings = 3;           // this many in the window: weaving
        public const double CrossBandM = 0.02;         // a crossing: from more than 2 cm on one side to the other
        public const double ChangePerS = 3;            // the look-ahead moves at most this many metres a second

        private readonly List<double> crossings = new List<double>();
        private int side;
        private double weave = 1, last = double.NaN, lastT;

        public double WeaveFactor => weave;
        public double LastLookAheadM => last;

        public void Reset()
        {
            crossings.Clear();
            side = 0;
            weave = 1;
            last = double.NaN;
        }

        /// <summary>The shortest look-ahead the steering's dead time allows at this speed.</summary>
        public static double DeadTimeFloorM(double speedMs, double deadTimeS, double floorGain = DefaultFloorGain) => 1.5 + speedMs * (floorGain * deadTimeS + 0.2);

        /// <summary>The look-ahead a curve wants: shorter the tighter it is.</summary>
        public static double CurveFactor(double baseM, double curvature, double curveGain = DefaultCurveGain)
        {
            return Math.Max(MinCurveFactor, 1 / (1 + curveGain * baseM * Math.Abs(curvature)));
        }

        /// <summary>The look-ahead to use now (m).</summary>
        /// <param name="t">Time (s).</param>
        /// <param name="baseM">The one the settings give (hold, speed factor, acquire).</param>
        /// <param name="curvature">The tightest curvature ahead within it (1/m).</param>
        /// <param name="offLineM">The distance off the line (m, signed), for weaving.</param>
        /// <param name="onLine">Holding a line now (autosteer on, at work): only then weaving is counted.</param>
        public double Adapt(double t, double baseM, double speedMs, double deadTimeS, double curvature, double offLineM, bool onLine)
        {
            NoteWeaving(t, offLineM, onLine);

            // (the cap first: the dead-time floor must hold even above it)
            double want = baseM * CurveFactor(baseM, curvature, CurveGain) * weave;
            want = Math.Min(want, baseM * Math.Max(1, MaxWeaveFactor));
            want = Math.Max(want, DeadTimeFloorM(speedMs, deadTimeS, FloorGain));
            want = Math.Max(2, want);

            if (double.IsNaN(last))
            {
                last = want;
                lastT = t;
                return want;
            }
            double dt = Math.Max(0, Math.Min(0.5, t - lastT));
            lastT = t;
            double step = ChangePerS * dt;
            last += Math.Max(-step, Math.Min(step, want - last));
            return last;
        }

        // crossing the line again and again: the look-ahead grows towards a third longer; calm again, it shrinks back
        private void NoteWeaving(double t, double offLineM, bool onLine)
        {
            if (onLine && !double.IsNaN(offLineM))
            {
                int now = offLineM > CrossBandM ? 1 : offLineM < -CrossBandM ? -1 : 0;
                if (now != 0)
                {
                    if (side != 0 && now != side) crossings.Add(t);
                    side = now;
                }
            }
            while (crossings.Count > 0 && t - crossings[0] > WeaveWindowS) crossings.RemoveAt(0);
            double maxWeave = Math.Max(1, MaxWeaveFactor);
            double target = crossings.Count >= WeaveCrossings ? maxWeave : 1;
            double dt = double.IsNaN(last) ? 0 : Math.Max(0, Math.Min(0.5, t - lastT));
            // up within a few seconds, back down slowly
            double tau = target > weave ? 3 : 20;
            weave += (target - weave) * Math.Min(1, dt / tau);
            weave = Math.Max(1, Math.Min(maxWeave, weave));
        }

        /// <summary>The tightest curvature along the path from 1 m out to the given distance (1/m, unsigned).</summary>
        public static double TightestCurvature(IList<vec2> path, double toM)
        {
            if (path == null || path.Count < 2) return 0;
            double worst = 0;
            for (double s = 1; s + 3 <= Math.Max(4, toM) + 1e-9; s += 1)
                worst = Math.Max(worst, Math.Abs(CPathAhead.MeanCurvature(path, s, s + 3)));
            return worst;
        }
    }
}
