using System;
using System.Collections.Generic;

namespace AgOpenGPS.Navigation
{
    /// <summary>Why the route autosteer does not steer now (None: it may steer).</summary>
    public enum SteerBlock { None, NoRoute, TooFast, OffRoute, Reversing, NearArrival, NoRtk }

    public struct SteerResult
    {
        public double AngleDeg;      // steer angle for the module, positive to the right
        public double CrossTrackM;   // signed distance of the pivot from the route
        public double GoalE, GoalN;  // the look-ahead point on the route
    }

    // EXPERIMENTAL: steers the tractor along the navigation route. Only steering - speed and brakes stay with the
    // driver. The route is the centre line of the mapped road, a few metres off at best: on roads (not on field
    // tracks) the line is moved into the right-hand lane. It steers slowly and stops well before the destination.
    public static class RouteSteer
    {
        public const double MaxSpeedKmh = 15;
        public const double MaxOffM = 4;
        public const double MaxCorrectionAgeS = 5;   // RTK corrections older than this: not steered on
        public const double StopBeforeEndM = 40;
        public const double LaneOffsetM = 1.75;  // default on roads: from the mapped centre line to the middle of the right lane
        public const double MaxOffsetM = 5;      // the offset the driver may set, either way
        public const double OffsetStepM = 0.25;
        public const double MinLookAheadM = 4;   // mapped tracks are jagged: never aim closer than this

        /// <summary>How far right (+) or left (-) of the mapped line to drive: field tracks and roads each have their own offset.</summary>
        public static double LaneOffset(RoadClass roadClass, double roadOffsetM, double trackOffsetM)
        {
            if (roadClass == RoadClass.OffRoad) return 0;   // across the field: straight on the line
            return ClampOffset(roadClass == RoadClass.Track ? trackOffsetM : roadOffsetM);
        }

        /// <summary>An offset within the allowed range, on the step grid.</summary>
        public static double ClampOffset(double metres)
        {
            if (double.IsNaN(metres) || double.IsInfinity(metres)) return 0;
            double stepped = Math.Round(metres / OffsetStepM) * OffsetStepM;
            return Math.Max(-MaxOffsetM, Math.Min(MaxOffsetM, stepped));
        }

        /// <summary>
        /// The path moved to the right of its direction, leg k by offsets[k] metres (local east/north). A point between
        /// two legs moves along the mean of both directions, so bends stay smooth.
        /// </summary>
        public static void OffsetRight(IList<double> east, IList<double> north, IList<double> offsets, out double[] outE, out double[] outN)
        {
            int count = Math.Min(east.Count, north.Count);
            outE = new double[count];
            outN = new double[count];
            for (int i = 0; i < count; i++)
            {
                double nx = 0, ny = 0, off = 0;
                int legs = 0;
                for (int k = i - 1; k <= i; k++)
                {
                    if (k < 0 || k + 1 >= count || k >= offsets.Count) continue;
                    double dx = east[k + 1] - east[k], dz = north[k + 1] - north[k];
                    double len = Math.Sqrt(dx * dx + dz * dz);
                    if (len < 1e-6) continue;
                    nx += dz / len;    // right of (dx, dz) is (dz, -dx)
                    ny += -dx / len;
                    off += offsets[k];
                    legs++;
                }
                double nlen = Math.Sqrt(nx * nx + ny * ny);
                if (legs == 0 || nlen < 1e-6) { outE[i] = east[i]; outN[i] = north[i]; continue; }
                off /= legs;
                outE[i] = east[i] + nx / nlen * off;
                outN[i] = north[i] + ny / nlen * off;
            }
        }

        public const double CornerRadiusM = 10;   // the least radius a corner of the route is driven with

        /// <summary>The corner radius for a vehicle: at least CornerRadiusM and half again its tightest turn.</summary>
        public static double CornerRadius(double wheelbase, double maxSteerAngleDeg)
        {
            double tan = Math.Tan(Math.Abs(maxSteerAngleDeg) * Math.PI / 180);
            double tightest = tan > 1e-3 ? wheelbase / tan : 0;
            return Math.Max(CornerRadiusM, 1.5 * tightest);
        }

        /// <summary>
        /// The path with every corner replaced by a circular arc of the given radius (smaller where the legs are too
        /// short for it), points about a metre apart along the arcs. A mapped junction is a sharp point: followed as it
        /// is, the wheels go to full lock at once; a real driver swings round it.
        /// </summary>
        public static void RoundCorners(IList<double> east, IList<double> north, double radius, out List<double> outE, out List<double> outN)
        {
            int count = Math.Min(east.Count, north.Count);
            outE = new List<double>();
            outN = new List<double>();
            if (count == 0) return;
            outE.Add(east[0]);
            outN.Add(north[0]);
            for (int i = 1; i + 1 < count; i++)
            {
                double ie = east[i] - east[i - 1], inn = north[i] - north[i - 1];
                double oe = east[i + 1] - east[i], on = north[i + 1] - north[i];
                double lin = Math.Sqrt(ie * ie + inn * inn), lout = Math.Sqrt(oe * oe + on * on);
                if (lin < 1e-6 || lout < 1e-6) continue;   // a doubled point
                ie /= lin; inn /= lin; oe /= lout; on /= lout;
                double cross = ie * on - inn * oe;            // > 0: a left turn
                double turn = Math.Acos(Math.Max(-1, Math.Min(1, ie * oe + inn * on)));
                if (turn < 2 * Math.PI / 180 || turn > 179 * Math.PI / 180)
                {
                    outE.Add(east[i]);
                    outN.Add(north[i]);
                    continue;
                }

                // the arc touches both legs; it may use at most half of each (the next corner needs the rest)
                double half = Math.Tan(turn / 2);
                double cut = Math.Min(radius * half, Math.Min(lin, lout) / 2);
                double r = cut / half;
                double se = east[i] - ie * cut, sn = north[i] - inn * cut;
                double side = cross > 0 ? 1 : -1;
                double ce = se - inn * r * side, cn = sn + ie * r * side;   // left of the way in for a left turn
                double a0 = Math.Atan2(sn - cn, se - ce);
                int steps = Math.Max(2, (int)Math.Ceiling(r * turn));
                for (int j = 0; j <= steps; j++)
                {
                    double a = a0 + side * turn * j / steps;
                    outE.Add(ce + r * Math.Cos(a));
                    outN.Add(cn + r * Math.Sin(a));
                }
            }
            if (count > 1)
            {
                outE.Add(east[count - 1]);
                outN.Add(north[count - 1]);
            }
        }

        /// <summary>None when the route may be steered now, otherwise the first reason it may not.</summary>
        public static SteerBlock Check(Route route, int segment, double offM, double speedKmh, bool reversing, double remainingM)
        {
            if (route == null || route.Points.Count < 2 || route.Legs.Count != route.Points.Count - 1) return SteerBlock.NoRoute;
            if (reversing) return SteerBlock.Reversing;
            if (Math.Abs(speedKmh) > MaxSpeedKmh) return SteerBlock.TooFast;
            if (remainingM < StopBeforeEndM) return SteerBlock.NearArrival;
            if (Math.Abs(offM) > MaxOffM) return SteerBlock.OffRoute;
            return SteerBlock.None;
        }

        /// <summary>
        /// Pure pursuit on a path in local metres (east, north), the same way the recorded path is driven:
        /// heading in radians, 0 = north, clockwise.
        /// </summary>
        public static SteerResult PurePursuit(IList<double> east, IList<double> north, double pivE, double pivN, double heading,
            double lookAheadM, double wheelbase, double maxAngleDeg)
        {
            var result = new SteerResult();
            int count = Math.Min(east.Count, north.Count);
            if (count < 2) return result;

            // the nearest leg
            int leg = 0;
            double best = double.MaxValue, bestT = 0;
            for (int i = 0; i + 1 < count; i++)
            {
                double dx = east[i + 1] - east[i], dz = north[i + 1] - north[i];
                double len2 = dx * dx + dz * dz;
                double t = len2 > 0 ? Math.Max(0, Math.Min(1, ((pivE - east[i]) * dx + (pivN - north[i]) * dz) / len2)) : 0;
                double pe = east[i] + t * dx - pivE, pn = north[i] + t * dz - pivN;
                double d2 = pe * pe + pn * pn;
                if (d2 < best) { best = d2; leg = i; bestT = t; }
            }

            double ax = east[leg], az = north[leg], bx = east[leg + 1], bz = north[leg + 1];
            double ldx = bx - ax, ldz = bz - az;
            double llen = Math.Sqrt(ldx * ldx + ldz * ldz);
            if (llen > 1e-6)
                result.CrossTrackM = ((ldz * pivE) - (ldx * pivN) + (bx * az) - (bz * ax)) / llen;

            // walk the look-ahead distance along the path from the point nearest the pivot
            double lookAhead = Math.Max(MinLookAheadM, lookAheadM);
            double se = ax + bestT * ldx, sn = az + bestT * ldz;
            double goalE = east[count - 1], goalN = north[count - 1];
            double sofar = 0;
            for (int i = leg + 1; i < count; i++)
            {
                double step = Math.Sqrt((east[i] - se) * (east[i] - se) + (north[i] - sn) * (north[i] - sn));
                if (sofar + step >= lookAhead && step > 0)
                {
                    double j = (lookAhead - sofar) / step;
                    goalE = se + j * (east[i] - se);
                    goalN = sn + j * (north[i] - sn);
                    break;
                }
                sofar += step;
                se = east[i];
                sn = north[i];
            }
            result.GoalE = goalE;
            result.GoalN = goalN;

            double d2Goal = (goalE - pivE) * (goalE - pivE) + (goalN - pivN) * (goalN - pivN);
            if (d2Goal < 0.01) return result;
            double localHeading = 2 * Math.PI - heading;
            double side = ((goalE - pivE) * Math.Cos(localHeading)) + ((goalN - pivN) * Math.Sin(localHeading));
            double angle = Math.Atan(2 * side * wheelbase / d2Goal) * 180 / Math.PI;
            result.AngleDeg = Math.Max(-maxAngleDeg, Math.Min(maxAngleDeg, angle));
            return result;
        }
    }
}
