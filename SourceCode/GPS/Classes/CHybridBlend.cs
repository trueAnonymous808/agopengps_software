using System;

namespace AgOpenGPS
{
    /// <summary>
    /// Hybrid steering: the calm LQR holds the line on straights and gentle curves, MPC takes over for what it does
    /// better - a tight curve coming (it plans into it), far off the line or turned away from it (coming onto the line).
    /// The share of MPC moves smoothly between 0 and 1: quickly up (before the curve), slowly down (after it settled),
    /// so the two never swap with a jump.
    /// </summary>
    public sealed class CHybridBlend
    {
        public double CurveToMpc = 1 / 45.0;       // a curve tighter than 45 m radius ahead: MPC
        public double CurveToLqr = 1 / 120.0;      // only gentler than 120 m: LQR may take it back
        public double OffToMpcM = 0.20, OffToLqrM = 0.06;
        public double HeadingToMpcDeg = 6, HeadingToLqrDeg = 2;
        public double UpPerS = 2.0, DownPerS = 0.4;

        private double share, lastT = double.NaN;
        private bool wantMpc;

        /// <summary>MPC's share of the angle now (0: LQR alone, 1: MPC alone).</summary>
        public double MpcShare => share;

        public void Reset()
        {
            share = 0;
            wantMpc = false;
            lastT = double.NaN;
        }

        /// <summary>The share for this fix, from the tightest curvature ahead, the distance and heading off the line.</summary>
        public double Update(double t, double curvatureAhead, double offLineM, double headingOffDeg)
        {
            double k = Math.Abs(curvatureAhead), off = Math.Abs(offLineM), hd = Math.Abs(headingOffDeg);
            // with a band between: what it wants changes only past the far edge of the band
            if (k > CurveToMpc || off > OffToMpcM || hd > HeadingToMpcDeg) wantMpc = true;
            else if (k < CurveToLqr && off < OffToLqrM && hd < HeadingToLqrDeg) wantMpc = false;

            double dt = double.IsNaN(lastT) ? 0 : Math.Max(0, Math.Min(0.5, t - lastT));
            lastT = t;
            double target = wantMpc ? 1 : 0;
            double step = (target > share ? UpPerS : DownPerS) * dt;
            share += Math.Max(-step, Math.Min(step, target - share));
            return share;
        }

        /// <summary>The angle from the two: blended by MPC's share.</summary>
        public static double Blend(double lqrDeg, double mpcDeg, double mpcShare)
        {
            if (double.IsNaN(mpcDeg)) return lqrDeg;
            if (double.IsNaN(lqrDeg)) return mpcDeg;
            return lqrDeg + (mpcDeg - lqrDeg) * mpcShare;
        }
    }
}
