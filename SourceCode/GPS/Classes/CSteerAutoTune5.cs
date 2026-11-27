using System;
using System.Collections.Generic;
using System.Linq;

namespace AgOpenGPS
{
    /// <summary>
    /// The roll compensation measured on the tractor itself. When the tractor rocks, the high antenna swings to the
    /// side; the compensation takes that away with the antenna height times the roll. Two things decide how well:
    /// whether the roll reading is on time with the position (the IMU, its filter and the module's message cycle all
    /// add delay), and whether the height it uses is the one that really cancels the swing (an IMU on a sprung cab,
    /// a height measured to the wrong point). Both are found from rocking driving: the antenna's quick side swing (the
    /// position without compensation, less its slow course) against the roll's quick changes, at every time shift.
    /// Whatever the IMU: a good one on time with the right height just gets "nothing to change".
    /// </summary>
    public sealed class CRollTune
    {
        public const double SmoothS = 2.0;        // what is slower than this is the course, not the swing
        public const double StepS = 0.1;
        public const int Capacity = 6000;         // ten minutes of straight driving
        public const int MinRocking = 600;        // a minute of rocking (quick roll over 0.3 degrees)
        public const double MinFit = 0.25;        // how much of the swing the roll must explain

        private readonly List<double[]> s = new List<double[]>();   // t, side swing (m), -sin(quick roll)
        private double sE, sN, sRoll, sCorr, lastT = -1e9;
        private bool started;

        public double ShiftS { get; private set; } = double.NaN;   // + : the roll arrives late, take it from later
        public double HeightM { get; private set; } = double.NaN;  // the height that really cancels the swing
        public double Fit { get; private set; } = double.NaN;      // the share of the swing it explains (0..1)
        public int Rocking { get; private set; }

        /// <summary>The compensated position's quick side swing now (m): what the compensation left of a rock.</summary>
        public double LeftSwingM { get; private set; }

        public bool Ready => Rocking >= MinRocking && !double.IsNaN(Fit) && Fit >= MinFit && HeightM > 0.3;

        public void Reset()
        {
            s.Clear();
            started = false;
            ShiftS = HeightM = Fit = double.NaN;
            Rocking = 0;
            LeftSwingM = 0;
        }

        /// <param name="east">The antenna's place before the roll compensation (after its side offset).</param>
        /// <param name="corrM">The compensation applied to it now, to the right of the heading (m).</param>
        public void AddSample(double t, double east, double north, double headingRad, double rollDeg, double corrM, double speedKmh, bool straight)
        {
            if (double.IsNaN(rollDeg) || Math.Abs(rollDeg) > 30 || double.IsNaN(east))
            {
                started = false;
                return;
            }
            double dt = t - lastT;
            if (!started || dt > 1 || dt <= 0)
            {
                sE = east;
                sN = north;
                sRoll = rollDeg;
                sCorr = corrM;
                lastT = t;
                started = true;
                return;
            }
            if (dt < StepS * 0.9) return;
            lastT = t;
            double a = Math.Min(1, dt / SmoothS);
            sE += (east - sE) * a;
            sN += (north - sN) * a;
            sRoll += (rollDeg - sRoll) * a;
            sCorr += (corrM - sCorr) * a;

            // to the right of the heading, as the compensation is applied
            double rx = Math.Cos(-headingRad), ry = Math.Sin(-headingRad);
            double swing = (east - sE) * rx + (north - sN) * ry;
            LeftSwingM = swing + (corrM - sCorr);

            if (speedKmh < 2 || !straight) return;
            double quickRoll = rollDeg - sRoll;
            s.Add(new[] { t, swing, -Math.Sin(quickRoll * Math.PI / 180), quickRoll });
            if (s.Count > Capacity) s.RemoveAt(0);
        }

        // the roll term at a time, between the samples around it (none across a gap)
        private bool At(double t, out double c)
        {
            c = 0;
            int lo = 0, hi = s.Count - 1;
            if (hi < 1 || t < s[0][0] || t > s[hi][0]) return false;
            while (hi - lo > 1)
            {
                int mid = (lo + hi) / 2;
                if (s[mid][0] <= t) lo = mid;
                else hi = mid;
            }
            double t0 = s[lo][0], t1 = s[hi][0];
            if (t1 - t0 > 2.5 * StepS) return false;
            double f = t1 > t0 ? (t - t0) / (t1 - t0) : 0;
            c = s[lo][2] + (s[hi][2] - s[lo][2]) * f;
            return true;
        }

        // the same as At, for times that only grow: the place found last time is where the search goes on from
        // (the analysis walks the samples in order - one pass instead of a search for each of them)
        private bool AtFrom(ref int lo, double t, out double c)
        {
            c = 0;
            int last = s.Count - 1;
            if (last < 1 || t < s[0][0] || t > s[last][0]) return false;
            if (lo < 0) lo = 0;
            while (lo < last - 1 && s[lo + 1][0] <= t) lo++;
            int hi = lo + 1;
            double t0 = s[lo][0], t1 = s[hi][0];
            if (t1 - t0 > 2.5 * StepS) return false;
            double f = t1 > t0 ? (t - t0) / (t1 - t0) : 0;
            c = s[lo][2] + (s[hi][2] - s[lo][2]) * f;
            return true;
        }

        public void Analyze()
        {
            Rocking = s.Count(x => Math.Abs(x[3]) > 0.3);
            if (Rocking < MinRocking) return;

            double bestFit = -1, bestShift = 0, bestH = double.NaN;
            for (int k = -15; k <= 15; k++)
            {
                double shift = k * 0.02;
                double n = 0, sr = 0, sc = 0, src = 0, scc = 0, srr = 0;
                int from = 0;
                foreach (double[] x in s)
                {
                    if (!AtFrom(ref from, x[0] + shift, out double c)) continue;
                    double r = x[1];
                    n++;
                    sr += r;
                    sc += c;
                    src += r * c;
                    scc += c * c;
                    srr += r * r;
                }
                if (n < 300) continue;
                double mr = sr / n, mc = sc / n;
                double cov = src / n - mr * mc, vc = scc / n - mc * mc, vr = srr / n - mr * mr;
                if (vc <= 1e-12 || vr <= 1e-12) continue;
                double fit = cov * cov / (vc * vr);
                if (fit > bestFit)
                {
                    bestFit = fit;
                    bestShift = shift;
                    bestH = -cov / vc;   // the height that makes swing + height * term the smallest
                }
            }
            if (bestFit < 0) return;
            Fit = bestFit;
            ShiftS = bestShift;
            HeightM = bestH;
        }

        /// <summary>
        /// The timing and the height factor to use, for the ones in use now and the height set in the vehicle.
        /// Nothing worth changing (or not sure yet): Good / Collecting.
        /// </summary>
        public TuneVerdict Recommend(double shiftNow, double gainNow, double heightSet, out double shift, out double gain)
        {
            shift = shiftNow;
            gain = gainNow;
            if (!Ready || heightSet <= 0.1) return TuneVerdict.Collecting;
            shift = Math.Round(Math.Max(-0.3, Math.Min(0.3, ShiftS)), 2);
            gain = Math.Round(Math.Max(0.5, Math.Min(1.5, HeightM / heightSet)), 2);
            if (Math.Abs(shift - shiftNow) < 0.03 && Math.Abs(gain - gainNow) < 0.05) return TuneVerdict.Good;
            return TuneVerdict.Recommend;
        }
    }

    /// <summary>
    /// Pit mode: a sudden rock (a pothole, a rut) that the roll compensation did not take out of the position makes
    /// the line seem to jump; steering after it only weaves. For a moment after such a rock the angle sent may change
    /// only so fast. With a good IMU and the compensation on time the position does not jump and this never acts.
    /// How fast it may change is tried out: rock by rock, the rate in use and another one in turn, and the one after
    /// which the tractor stayed closer to the line is suggested.
    /// </summary>
    public sealed class CPitGuard
    {
        public const double TriggerDegS = 5;     // a rock: the roll changing this fast
        public const double JumpM = 0.03;        // and the compensated position still swung this far
        public const double HoldS = 0.8;
        public const double WatchS = 2.5;        // the distance off the line is judged over this after a rock
        public const int MinEvents = 10;         // rocks with each rate before anything is said

        private double prevRoll = double.NaN, prevT, holdUntil = -1e9, outDeg = double.NaN, outT;
        private bool eventUsesOther;
        private int eventIndex;
        private double eventStart = -1e9, sumSq;
        private int sumN;
        private readonly List<double> scoresNow = new List<double>(), scoresOther = new List<double>();
        private double factor = 0.6;             // the other rate: slower first, faster when that did not help
        private double rateTried = double.NaN;

        public int Events { get; private set; }
        public int EventsNow => scoresNow.Count;
        public int EventsOther => scoresOther.Count;
        public double OtherRate(double rateNow) => Math.Max(1, Math.Min(15, rateNow * factor));

        public bool Holding(double t) => t < holdUntil;

        /// <summary>
        /// Every fix: the roll and what the compensation left of the swing. peakRateDegS: the fastest roll change since
        /// the last fix from the module's IMU summary (NaN without one) - a short jolt between two fixes the 10 Hz roll
        /// barely shows.
        /// </summary>
        public void Update(double t, double rollDeg, double leftSwingM, bool enabled, double rateNow, double peakRateDegS = double.NaN)
        {
            if (rateNow != rateTried)
            {
                // another rate in use: what was tried belongs to the old one
                rateTried = rateNow;
                scoresNow.Clear();
                scoresOther.Clear();
            }
            if (double.IsNaN(rollDeg) || Math.Abs(rollDeg) > 30)
            {
                prevRoll = double.NaN;
                return;
            }
            if (!double.IsNaN(prevRoll))
            {
                double dt = t - prevT;
                if (dt > 0.02 && dt < 0.5)
                {
                    double rate = (rollDeg - prevRoll) / dt;
                    if (!double.IsNaN(peakRateDegS) && Math.Abs(peakRateDegS) > Math.Abs(rate)) rate = peakRateDegS;
                    if (enabled && Math.Abs(rate) > TriggerDegS && Math.Abs(leftSwingM) > JumpM && t >= holdUntil)
                    {
                        CloseEvent();
                        eventUsesOther = eventIndex++ % 2 == 1;
                        eventStart = t;
                        sumSq = 0;
                        sumN = 0;
                        Events++;
                        holdUntil = t + HoldS;
                    }
                }
            }
            prevRoll = rollDeg;
            prevT = t;
        }

        /// <summary>The angle to send: as asked, or for a moment after a rock no faster than the rate.</summary>
        public double Smooth(double t, double askedDeg, double rateNow)
        {
            double dt = t - outT;
            outT = t;
            bool limit = t < holdUntil || (t < holdUntil + 0.5 && !double.IsNaN(outDeg) && Math.Abs(askedDeg - outDeg) > 0.3);
            if (!limit || double.IsNaN(outDeg) || dt <= 0 || dt > 0.5)
            {
                outDeg = askedDeg;
                return askedDeg;
            }
            double rate = eventUsesOther ? OtherRate(rateNow) : rateNow;
            if (t >= holdUntil) rate *= 2;   // letting go: twice as fast
            double step = rate * dt;
            outDeg += Math.Max(-step, Math.Min(step, askedDeg - outDeg));
            return outDeg;
        }

        /// <summary>The distance off the line after a rock, for judging the rate it was steered with.</summary>
        public void FeedXte(double t, double xteM, bool onLine)
        {
            if (t - eventStart <= WatchS)
            {
                if (onLine && !double.IsNaN(xteM)) { sumSq += xteM * xteM; sumN++; }
            }
            else CloseEvent();
        }

        private void CloseEvent()
        {
            if (sumN < 5) { sumN = 0; return; }
            (eventUsesOther ? scoresOther : scoresNow).Add(sumSq / sumN);
            sumN = 0;
        }

        /// <summary>The other rate, once it kept the tractor clearly closer to the line after enough rocks.</summary>
        public TuneVerdict Recommend(double rateNow, out double rate, out double nowCm, out double otherCm)
        {
            rate = rateNow;
            nowCm = otherCm = double.NaN;
            if (scoresNow.Count < MinEvents || scoresOther.Count < MinEvents) return TuneVerdict.Collecting;
            nowCm = Math.Sqrt(scoresNow.Average()) * 100;
            otherCm = Math.Sqrt(scoresOther.Average()) * 100;
            if (otherCm < nowCm * 0.9)
            {
                rate = Math.Round(OtherRate(rateNow), 1);
                return TuneVerdict.Recommend;
            }
            // not better: the other way next time
            factor = factor < 1 ? 1.6 : 0.6;
            scoresNow.Clear();
            scoresOther.Clear();
            return TuneVerdict.Good;
        }
    }
}
