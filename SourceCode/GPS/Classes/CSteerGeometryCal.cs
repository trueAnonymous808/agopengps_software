using System;
using System.Collections.Generic;

namespace AgOpenGPS
{
    /// <summary>
    /// Steering geometry calibration: compares the wheel angle the steer angle sensor reports with the angle the
    /// tractor really turns with. The real angle comes from GPS: how much the heading changes per metre driven,
    /// and the wheelbase (angle = atan(wheelbase * heading change per metre)).
    /// Right turns show the counts-per-degree error; left turns show the same times the Ackermann error (the module
    /// multiplies left angles by Ackermann / 100). Steady circles both ways give both.
    /// Pure logic (no form): fed fix by fix, read for recommendations.
    /// </summary>
    public class CSteerGeometryCal
    {
        public const double MinSpeedKmh = 2;
        public const double MaxSpeedKmh = 10;
        public const double MinAngleDeg = 5;
        public const double MaxAngleDeg = 35;
        public const double SteadyDeg = 1;          // the reported angle stays within ± this over SteadyS
        public const double SteadyS = 2;
        public const double ArcM = 2;               // the heading change is measured over at least this much driving
        public const double SampleEveryM = 1;       // one sample per metre: fixes closer together say the same
        public const int MinSamples = 150;          // per side
        public const double MaxSpread = 0.08;       // relative spread (median absolute deviation / median) to trust a side
        public const double MaxCpdChange = 0.30;    // one apply changes CPD by at most 30 %
        public const int MinAckermann = 50, MaxAckermann = 150;
        private const int MaxSamples = 2000;

        private struct Fix
        {
            public double Time, East, North, Heading, Angle, RunM;
        }

        private readonly List<Fix> history = new List<Fix>();
        private readonly List<double> right = new List<double>();   // real / reported, right turns
        private readonly List<double> left = new List<double>();
        // the same ratios by side (0 right, 1 left) and angle band (CWasLinearity.Band): the sensor's linearity
        private readonly List<double>[,] bands = { { new List<double>(), new List<double>(), new List<double>() }, { new List<double>(), new List<double>(), new List<double>() } };
        public const int MinBandSamples = 40;
        private double run, lastSampleRun = double.MinValue;
        private readonly object sync = new object();

        public int RightCount { get { lock (sync) return right.Count; } }
        public int LeftCount { get { lock (sync) return left.Count; } }

        public void Reset()
        {
            lock (sync)
            {
                history.Clear();
                right.Clear();
                left.Clear();
                foreach (List<double> b in bands) b.Clear();
                run = 0;
                lastSampleRun = double.MinValue;
            }
        }

        /// <summary>
        /// One GPS fix. Heading in radians (0 = north, clockwise), reported angle in degrees (positive = right).
        /// goodFix: RTK fix (or the simulator); a fix that is not good, reversing or out of range breaks the run.
        /// </summary>
        public void AddFix(double timeS, double east, double north, double heading, double reportedAngleDeg,
            double speedKmh, bool reversing, bool goodFix, double wheelbaseM)
        {
            lock (sync)
            {
                bool usable = goodFix && !reversing && speedKmh >= MinSpeedKmh && speedKmh <= MaxSpeedKmh
                    && Math.Abs(reportedAngleDeg) >= MinAngleDeg && Math.Abs(reportedAngleDeg) <= MaxAngleDeg
                    && wheelbaseM > 0.5;
                if (!usable)
                {
                    history.Clear();
                    return;
                }

                if (history.Count > 0)
                {
                    Fix last = history[history.Count - 1];
                    double de = east - last.East, dn = north - last.North;
                    run += Math.Sqrt(de * de + dn * dn);
                }
                history.Add(new Fix { Time = timeS, East = east, North = north, Heading = heading, Angle = reportedAngleDeg, RunM = run });

                // keep only what the steadiness window and the arc need
                while (history.Count > 2 && timeS - history[1].Time > SteadyS && run - history[1].RunM > ArcM)
                    history.RemoveAt(0);

                Fix first = history[0];
                if (timeS - first.Time < SteadyS || run - first.RunM < ArcM) return;
                if (run - lastSampleRun < SampleEveryM) return;

                // steady wheel over the window, all on one side
                double lo = double.MaxValue, hi = double.MinValue;
                foreach (Fix f in history)
                {
                    lo = Math.Min(lo, f.Angle);
                    hi = Math.Max(hi, f.Angle);
                }
                if (hi - lo > 2 * SteadyDeg || Math.Sign(lo) != Math.Sign(hi)) return;

                double turned = Wrap(heading - first.Heading);
                double perMetre = turned / (run - first.RunM);
                double realDeg = Math.Atan(wheelbaseM * perMetre) * 180 / Math.PI;
                double meanAngle = (lo + hi) / 2;
                if (Math.Sign(realDeg) != Math.Sign(meanAngle)) return;   // turning the other way: a heading jump

                double ratio = realDeg / meanAngle;
                if (ratio < 0.3 || ratio > 3) return;                     // nonsense, not a calibration error
                List<double> side = meanAngle > 0 ? right : left;
                side.Add(ratio);
                if (side.Count > MaxSamples) side.RemoveAt(0);
                List<double> band = bands[meanAngle > 0 ? 0 : 1, CWasLinearity.Band(meanAngle)];
                band.Add(ratio);
                if (band.Count > MaxSamples) band.RemoveAt(0);
                lastSampleRun = run;
            }
        }

        /// <summary>Real angle / reported angle on one side (median), with its relative spread; NaN without samples.</summary>
        public double Ratio(bool rightSide, out double spread)
        {
            lock (sync) return Median(rightSide ? right : left, out spread);
        }

        public bool RightReady { get { double s; double m = Ratio(true, out s); return RightCount >= MinSamples && s <= MaxSpread && !double.IsNaN(m); } }
        public bool LeftReady { get { double s; double m = Ratio(false, out s); return LeftCount >= MinSamples && s <= MaxSpread && !double.IsNaN(m); } }

        /// <summary>
        /// The counts per degree to use: the reported angle is counts / CPD, so CPD / ratio makes it the real one.
        /// Null until the right side is ready. Limited to MaxCpdChange per apply and to 1..255.
        /// </summary>
        public int? RecommendedCpd(int currentCpd)
        {
            if (!RightReady || currentCpd <= 0) return null;
            double spread;
            double k = Ratio(true, out spread);
            double target = currentCpd / k;
            target = Math.Max(currentCpd * (1 - MaxCpdChange), Math.Min(currentCpd * (1 + MaxCpdChange), target));
            return (int)Math.Max(1, Math.Min(255, Math.Round(target)));
        }

        /// <summary>
        /// The Ackermann to use: left angles are scaled by Ackermann / 100 on top of the CPD, so the left ratio over the
        /// right ratio is the Ackermann error alone. Null until both sides are ready. Limited to 50..150.
        /// </summary>
        public int? RecommendedAckermann(int currentAckermann)
        {
            if (!RightReady || !LeftReady || currentAckermann <= 0) return null;
            double s;
            double kRight = Ratio(true, out s), kLeft = Ratio(false, out s);
            double target = currentAckermann * kLeft / kRight;
            return (int)Math.Max(MinAckermann, Math.Min(MaxAckermann, Math.Round(target)));
        }

        /// <summary>Samples per band (small, middle, big), both sides together.</summary>
        public int[] BandCounts()
        {
            lock (sync)
                return new[] { bands[0, 0].Count + bands[1, 0].Count, bands[0, 1].Count + bands[1, 1].Count, bands[0, 2].Count + bands[1, 2].Count };
        }

        /// <summary>
        /// The sensor's linearity: the real / sensor ratio of the middle and the big band relative to the small band
        /// (1 = linear), averaged over the sides that have enough samples in both; NaN for a band without enough.
        /// </summary>
        public void Linearity(out double middle, out double big)
        {
            lock (sync)
            {
                middle = Relative(1);
                big = Relative(2);
            }
        }

        private double Relative(int band)
        {
            double sum = 0;
            int n = 0;
            for (int side = 0; side < 2; side++)
            {
                if (bands[side, 0].Count < MinBandSamples || bands[side, band].Count < MinBandSamples) continue;
                double s;
                double small = Median(bands[side, 0], out s), here = Median(bands[side, band], out s);
                if (small <= 0) continue;
                sum += here / small;
                n++;
            }
            return n > 0 ? sum / n : double.NaN;
        }

        /// <summary>How sure the result is, 0..100: enough samples on both sides and little spread.</summary>
        public double Confidence
        {
            get
            {
                double sr, sl;
                double r = Ratio(true, out sr), l = Ratio(false, out sl);
                double c = 0;
                if (!double.IsNaN(r)) c += 50 * Math.Min(1, RightCount / (double)MinSamples) * Math.Max(0, 1 - sr / (2 * MaxSpread));
                if (!double.IsNaN(l)) c += 50 * Math.Min(1, LeftCount / (double)MinSamples) * Math.Max(0, 1 - sl / (2 * MaxSpread));
                return Math.Round(c);
            }
        }

        private static double Median(List<double> values, out double spread)
        {
            spread = double.NaN;
            if (values.Count == 0) return double.NaN;
            var sorted = new List<double>(values);
            sorted.Sort();
            double median = Middle(sorted);
            var dev = new List<double>(sorted.Count);
            foreach (double v in sorted) dev.Add(Math.Abs(v - median));
            dev.Sort();
            spread = median != 0 ? Middle(dev) / Math.Abs(median) : double.NaN;
            return median;
        }

        private static double Middle(List<double> sorted)
        {
            int n = sorted.Count;
            return n % 2 == 1 ? sorted[n / 2] : (sorted[n / 2 - 1] + sorted[n / 2]) / 2;
        }

        private static double Wrap(double radians)
        {
            while (radians > Math.PI) radians -= 2 * Math.PI;
            while (radians < -Math.PI) radians += 2 * Math.PI;
            return radians;
        }
    }
}
