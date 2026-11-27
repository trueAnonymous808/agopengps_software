using System;
using System.Collections.Generic;

namespace AgOpenGPS
{
    /// <summary>
    /// The path ahead of the tractor as a polyline (easting, northing), from the point of the line closest to the
    /// pivot onwards in the driving direction: points, headings and curvature at a distance along it.
    /// </summary>
    public static class CPathAhead
    {
        /// <summary>The point at distance s along the path (before its start and past its end: straight on).</summary>
        public static vec2 PointAt(IList<vec2> path, double s)
        {
            if (path.Count == 1) return path[0];
            if (s <= 0) return Extend(path[0], path[1], s);
            double gone = 0;
            for (int i = 0; i < path.Count - 1; i++)
            {
                double len = Dist(path[i], path[i + 1]);
                if (gone + len >= s && len > 1e-9)
                {
                    double f = (s - gone) / len;
                    return new vec2(path[i].easting + (path[i + 1].easting - path[i].easting) * f,
                        path[i].northing + (path[i + 1].northing - path[i].northing) * f);
                }
                gone += len;
            }
            return Extend(path[path.Count - 2], path[path.Count - 1], s - gone + Dist(path[path.Count - 2], path[path.Count - 1]));
        }

        /// <summary>The path's heading (bearing, rad: 0 north, clockwise) at distance s, over a chord of 2 m.</summary>
        public static double HeadingAt(IList<vec2> path, double s)
        {
            vec2 a = PointAt(path, s - 1), b = PointAt(path, s + 1);
            return Math.Atan2(b.easting - a.easting, b.northing - a.northing);
        }

        /// <summary>The mean curvature between s0 and s1 (1/m, + turning right): the heading change over the length.</summary>
        public static double MeanCurvature(IList<vec2> path, double s0, double s1)
        {
            if (path.Count < 2 || s1 - s0 < 0.5) return 0;
            return Wrap(HeadingAt(path, s1) - HeadingAt(path, s0)) / (s1 - s0);
        }

        /// <summary>
        /// For pure pursuit on curves: it steers with the path's mean curvature out to its goal point, so it turns in
        /// before a curve as early as the look-ahead and cuts it. The correction (degrees) swaps that for the curvature
        /// where the wheels will be when they have answered: after the dead time and the time they need to turn half
        /// way to the curve's angle (a slow steering must start earlier). On a straight or an even arc it is 0.
        /// </summary>
        public static double CurvePreviewDeg(IList<vec2> path, double goalDistanceM, double speedMs, double deadTimeS, double wheelbaseM,
            double turnRateDegS = 25, double limitDeg = 5)
        {
            if (path == null || path.Count < 2 || goalDistanceM < 1 || wheelbaseM < 0.5) return 0;
            // the wheels' answer: the dead time, and half the swing to the tightest angle ahead at their turning speed
            double tightest = 0;
            for (double s = 1; s + 3 <= goalDistanceM + 1e-9; s += 1) tightest = Math.Max(tightest, Math.Abs(MeanCurvature(path, s, s + 3)));
            double swingS = Math.Atan(wheelbaseM * tightest) * 180 / Math.PI / 2 / Math.Max(1, turnRateDegS);
            // (headings from 1 m on: at the very start the 2 m chord would reach behind the path)
            double at = Math.Max(0, speedMs * (deadTimeS + swingS));
            if (at >= goalDistanceM) return 0;   // pure pursuit's own turn-in is not even early enough
            double from = Math.Max(1, at - 1.5);
            double ahead = MeanCurvature(path, from, from + 3);
            double ppSees = MeanCurvature(path, 1, Math.Max(2, goalDistanceM));
            double deg = (Math.Atan(wheelbaseM * ahead) - Math.Atan(wheelbaseM * ppSees)) * 180 / Math.PI;
            return Math.Max(-limitDeg, Math.Min(limitDeg, deg));
        }

        public static double Wrap(double a)
        {
            while (a > Math.PI) a -= 2 * Math.PI;
            while (a < -Math.PI) a += 2 * Math.PI;
            return a;
        }

        private static double Dist(vec2 a, vec2 b) => Math.Sqrt((b.easting - a.easting) * (b.easting - a.easting) + (b.northing - a.northing) * (b.northing - a.northing));

        private static vec2 Extend(vec2 a, vec2 b, double s)
        {
            double len = Dist(a, b);
            if (len < 1e-9) return a;
            return new vec2(a.easting + (b.easting - a.easting) / len * s, a.northing + (b.northing - a.northing) / len * s);
        }
    }

    /// <summary>
    /// Model predictive steering: the tractor is simulated a few seconds ahead - its geometry, the steering's dead time,
    /// how fast the wheels turn, the angles already sent and not yet answered, and a learnt offset (slope, sensor zero).
    /// Two angles are searched (the first half second, then the rest) for the one that keeps it closest to the path with
    /// the least steering; the first is sent, and it is worked out again on the next fix.
    /// </summary>
    public sealed class CSteerMpc
    {
        public double HorizonS = 3.0;
        public double StepS = 0.05;
        public double FirstMoveS = 0.5;
        public double WeightHeading = 0.5;     // m² per rad² of heading off the path
        public double WeightMove = 3e-5;       // per deg² of change in the angle asked for (1° ≈ 0.5 cm off the line)

        /// <summary>A coarser search (about a third of the work), for a slow computer.</summary>
        public bool Quick;

        // the last answer: the next search starts around it (the whole range every tenth time, or when it runs to the edge)
        private double warm1 = double.NaN, warm2;
        private int sinceFull;

        /// <summary>The next search covers the whole range (MPC was not steering a while: the last answer is stale).</summary>
        public void ForgetWarmStart() => warm1 = double.NaN;

        public struct State
        {
            public double East, North, HeadingRad, SpeedMs, WheelbaseM;
            public double WheelDeg;            // where the wheels are now (the model's angle, the offset not in it)
            public double MaxDeg, RateDegS;
            public double RateRightDegS, RateLeftDegS;   // the turning speed each way, when measured apart (0: RateDegS)
            public double BiasDeg;             // learnt: the angle the tractor drives straight with
            public double SlipMs;              // the tractor's slide sideways (+ right; a slope), from the state estimator
            public double LastDeg;             // the angle asked for on the fix before
            public double[] Pending;           // the angles already sent and still on their way (one per step)
        }

        // the path as segments, worked out once per solve
        private double[] segE, segN, segDe, segDn, segLen, segHead;
        private int segCount;

        /// <summary>The angle to ask for now (degrees, + right).</summary>
        public double Solve(State st, IList<vec2> path)
        {
            Prepare(path);
            if (segCount == 0 || st.SpeedMs <= 0.1 || st.WheelbaseM < 0.5) return st.LastDeg;
            double max = Math.Max(5, st.MaxDeg);

            // the angles worth trying: what the sideways grip allows at this speed (around the learnt offset). At work
            // speed that is the whole range; fast, only tenths of a degree - so the search's steps shrink with it, else
            // a 2 degree step would be far too coarse and it would swing from side to side
            double reach = LateralLimitDeg(st.SpeedMs, st.WheelbaseM);
            double centre = -st.BiasDeg;
            lo = Math.Max(-max, centre - reach);
            hi = Math.Min(max, centre + reach);
            if (hi - lo < 0.02) { lo = Math.Max(-max, centre - 0.01); hi = Math.Min(max, centre + 0.01); }
            double f = Math.Min(1, (hi - lo) / 70);   // 1 at the whole range of +-35 degrees

            double best1 = Math.Max(lo, Math.Min(hi, 0)), best2 = best1, bestCost = double.MaxValue;

            // coarse first - around the last answer, or over the whole range - then finer around the best
            double coarse = (Quick ? 5 : 3) * f;
            bool warm = !double.IsNaN(warm1) && sinceFull < 10;
            if (warm)
            {
                double window = 8 * f;
                double step = (Quick ? 4 : 2) * f;
                double w1 = Math.Max(lo, Math.Min(hi, warm1)), w2 = Math.Max(lo, Math.Min(hi, warm2));
                Search(st, w1 - window, w1 + window, step, w2 - window, w2 + window, step, ref best1, ref best2, ref bestCost);
                sinceFull++;
                // at the window's edge the best may lie outside it: the whole range after all
                if (Math.Abs(best1 - w1) >= window - 1e-9 || Math.Abs(best2 - w2) >= window - 1e-9) warm = false;
            }
            if (!warm)
            {
                Search(st, lo, hi, coarse, lo, hi, coarse, ref best1, ref best2, ref bestCost);
                sinceFull = 0;
            }
            if (Quick)
            {
                Search(st, best1 - 5 * f, best1 + 5 * f, f, best2 - 5 * f, best2 + 5 * f, f, ref best1, ref best2, ref bestCost);
                Search(st, best1 - f, best1 + f, 0.25 * f, best2 - f, best2 + f, 0.5 * f, ref best1, ref best2, ref bestCost);
            }
            else
            {
                Search(st, best1 - 3 * f, best1 + 3 * f, 0.5 * f, best2 - 3 * f, best2 + 3 * f, 0.5 * f, ref best1, ref best2, ref bestCost);
                Search(st, best1 - 0.5 * f, best1 + 0.5 * f, 0.1 * f, best2 - 0.5 * f, best2 + 0.5 * f, 0.1 * f, ref best1, ref best2, ref bestCost);
            }
            warm1 = best1;
            warm2 = best2;
            return Math.Max(lo, Math.Min(hi, best1));
        }

        /// <summary>The sideways acceleration the angles are kept within (m/s²): a tractor's tyres on the field and the road.</summary>
        public const double LateralAccelMax = 4;

        /// <summary>The largest steer angle (degrees) that stays within the sideways grip at this speed.</summary>
        public static double LateralLimitDeg(double speedMs, double wheelbaseM)
        {
            double v2 = Math.Max(0.25, speedMs * speedMs);
            return Math.Atan(LateralAccelMax * wheelbaseM / v2) * 180 / Math.PI;
        }

        // the range this solve searches (within the steering's limits and the sideways grip)
        private double lo, hi;

        private void Search(State st, double from1, double to1, double step1, double from2, double to2, double step2,
            ref double best1, ref double best2, ref double bestCost)
        {
            step1 = Math.Max(1e-3, step1);
            step2 = Math.Max(1e-3, step2);
            for (double u1 = Math.Max(lo, from1); u1 <= Math.Min(hi, to1) + 1e-9; u1 += step1)
                for (double u2 = Math.Max(lo, from2); u2 <= Math.Min(hi, to2) + 1e-9; u2 += step2)
                {
                    double c = Cost(st, u1, u2, bestCost);
                    if (c < bestCost) { bestCost = c; best1 = u1; best2 = u2; }
                }
        }

        /// <summary>The cost of asking u1 now and u2 after the first move (stops early once over the best so far).</summary>
        public double Cost(State st, double u1, double u2, double stopAbove = double.MaxValue)
        {
            double dt = StepS;
            int steps = (int)Math.Round(HorizonS / dt);
            int pending = st.Pending?.Length ?? 0;
            double x = st.East, y = st.North, h = st.HeadingRad, d = st.WheelDeg;
            double v = st.SpeedMs, rate = Math.Max(1, st.RateDegS) * dt;
            double rateRight = st.RateRightDegS > 0 ? Math.Max(1, st.RateRightDegS) * dt : rate;
            double rateLeft = st.RateLeftDegS > 0 ? Math.Max(1, st.RateLeftDegS) * dt : rate;
            double cost = WeightMove * ((u1 - st.LastDeg) * (u1 - st.LastDeg) + (u2 - u1) * (u2 - u1));
            int seg = Nearest(x, y);
            for (int k = 0; k < steps; k++)
            {
                double target = k < pending ? st.Pending[k] : ((k - pending) * dt < FirstMoveS ? u1 : u2);
                double move = target - d;
                d += move > rateRight ? rateRight : move < -rateLeft ? -rateLeft : move;
                h += v * Math.Tan((d + st.BiasDeg) * Math.PI / 180) / st.WheelbaseM * dt;
                x += (v * Math.Sin(h) + st.SlipMs * Math.Cos(h)) * dt;
                y += (v * Math.Cos(h) - st.SlipMs * Math.Sin(h)) * dt;

                // off the path: the segment beside the point (onwards only), its side distance and heading
                while (seg < segCount - 1 && Along(seg, x, y) > segLen[seg]) seg++;
                double side = ((x - segE[seg]) * segDn[seg] - (y - segN[seg]) * segDe[seg]) / segLen[seg];
                double psi = CPathAhead.Wrap(h - segHead[seg]);
                cost += (side * side + WeightHeading * psi * psi) * dt;
                if (cost > stopAbove) return cost;
            }
            return cost;
        }

        private double Along(int i, double x, double y) => ((x - segE[i]) * segDe[i] + (y - segN[i]) * segDn[i]) / segLen[i];

        // the segment the start is beside (the path starts at the closest point, so it is one of the first)
        private int Nearest(double x, double y)
        {
            int best = 0;
            double bestD = double.MaxValue;
            for (int i = 0; i < Math.Min(segCount, 20); i++)
            {
                double t = Math.Max(0, Math.Min(segLen[i], Along(i, x, y)));
                double ex = segE[i] + segDe[i] / segLen[i] * t - x, ey = segN[i] + segDn[i] / segLen[i] * t - y;
                double dd = ex * ex + ey * ey;
                if (dd < bestD) { bestD = dd; best = i; }
            }
            return best;
        }

        private void Prepare(IList<vec2> path)
        {
            int n = path?.Count ?? 0;
            if (segE == null || segE.Length < n)
            {
                int cap = Math.Max(16, n);
                segE = new double[cap]; segN = new double[cap]; segDe = new double[cap]; segDn = new double[cap];
                segLen = new double[cap]; segHead = new double[cap];
            }
            segCount = 0;
            for (int i = 0; i + 1 < n; i++)
            {
                double de = path[i + 1].easting - path[i].easting, dn = path[i + 1].northing - path[i].northing;
                double len = Math.Sqrt(de * de + dn * dn);
                if (len < 0.05) continue;   // points on top of each other
                segE[segCount] = path[i].easting;
                segN[segCount] = path[i].northing;
                segDe[segCount] = de;
                segDn[segCount] = dn;
                segLen[segCount] = len;
                segHead[segCount] = Math.Atan2(de, dn);
                segCount++;
            }
        }
    }

    /// <summary>
    /// Calmer steering: small, quick wobbles of the asked angle (GPS noise) are smoothed, real moves pass at once, and
    /// the angle never moves faster than the wheels can follow (asked faster, the motor only runs into its limit).
    /// </summary>
    public sealed class CSteerSmoother
    {
        public const double NoiseDeg = 0.15;   // a change smaller than this from one fix to the next is smoothed
        private double last = double.NaN, lastT;

        public void Reset() => last = double.NaN;

        public double Apply(double t, double deg, double maxRateDegS)
        {
            if (double.IsNaN(last) || double.IsNaN(deg))
            {
                last = deg;
                lastT = t;
                return deg;
            }
            double dt = Math.Max(0, Math.Min(0.5, t - lastT));
            lastT = t;
            double change = deg - last;
            if (Math.Abs(change) < NoiseDeg) change *= 0.5;
            if (!double.IsNaN(maxRateDegS) && maxRateDegS > 0 && dt > 0)
            {
                double limit = Math.Max(0.05, maxRateDegS * 1.2 * dt);
                change = Math.Max(-limit, Math.Min(limit, change));
            }
            last += change;
            return last;
        }
    }

    /// <summary>
    /// Play compensation: the wheels stay behind the asked angle by half the play in whichever direction it moves.
    /// The angle sent is put ahead by that half in the direction it moves (with a little hysteresis, so standing
    /// still it does not jump from side to side).
    /// </summary>
    public sealed class CPlayComp
    {
        public const double MinPlayDeg = 0.3, MaxPlayDeg = 3, TurnDeg = 0.2;
        private int dir;
        private double edge = double.NaN;

        public int Direction => dir;

        public void Reset()
        {
            dir = 0;
            edge = double.NaN;
        }

        public double Apply(double deg, double playDeg)
        {
            if (double.IsNaN(playDeg) || playDeg < MinPlayDeg || double.IsNaN(deg))
            {
                Reset();
                return deg;
            }
            if (double.IsNaN(edge)) edge = deg;
            // edge: the furthest the angle went in the direction it moves (where it started, before it moved)
            if (dir == 0)
            {
                if (deg > edge + TurnDeg) { dir = 1; edge = deg; }
                else if (deg < edge - TurnDeg) { dir = -1; edge = deg; }
            }
            else if (dir > 0)
            {
                if (deg > edge) edge = deg;
                else if (deg < edge - TurnDeg) { dir = -1; edge = deg; }
            }
            else
            {
                if (deg < edge) edge = deg;
                else if (deg > edge + TurnDeg) { dir = 1; edge = deg; }
            }
            return deg + dir * Math.Min(MaxPlayDeg, playDeg) / 2;
        }
    }
}
