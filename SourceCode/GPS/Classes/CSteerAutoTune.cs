using System;
using System.Collections.Generic;

namespace AgOpenGPS
{
    /// <summary>What a tuner found: nothing yet, fine as it is, or a change with its reason.</summary>
    public enum TuneVerdict { Collecting, Good, Oscillates, DeadBand, Slow, Weaves, ReturnsSlowly, Recommend, ZoneBelowNoise, ZoneAboveNoise, ZoneFlickers, MotorHunts, RollZeroOff, AcquireOvershoots, AcquireSlow, StaysToOneSide, IntegralWaves, TurnOvershoots, TurnEndsWide, FusionDrifts, FusionNoisy, SensorNotLinear }

    /// <summary>
    /// Steer motor tuning (Kp and minimum PWM) from the wanted and the reported wheel angle while autosteer works:
    /// - swinging round the wanted angle while it is held: the gain is too high;
    /// - stopping short of a held angle: the motor does not start moving for small errors (minimum PWM);
    /// - following changes late: the gain is too low.
    /// Pure logic, fed fix by fix.
    /// </summary>
    public class CMotorTune
    {
        public const double HeldDeg = 0.3;             // the wanted angle changed less than this over the last second: held
        public const int MinHeldSamples = 200;
        public const int MinAllSamples = 300;
        public const double SwingPerS = 1.0, SwingRmsDeg = 0.3;
        public const double ShortDeg = 0.6;
        public const double SlowS = 0.6;
        private const double WindowS = 60;

        private struct Sample { public double T, Set, Act; }
        private readonly List<Sample> samples = new List<Sample>();
        private readonly object sync = new object();

        public int Count { get { lock (sync) return samples.Count; } }

        public void Reset() { lock (sync) samples.Clear(); }

        public void AddSample(double timeS, double wantedDeg, double reportedDeg, double speedKmh, bool steering)
        {
            lock (sync)
            {
                if (!steering || speedKmh < 2) return;
                samples.Add(new Sample { T = timeS, Set = wantedDeg, Act = reportedDeg });
                while (samples.Count > 0 && timeS - samples[0].T > WindowS) samples.RemoveAt(0);
            }
        }

        /// <summary>The findings: delay of the wheel behind the wanted angle, swing and shortfall while held.</summary>
        public void Measure(out double delayS, out double swingPerS, out double swingRmsDeg, out double shortDeg, out int heldCount)
        {
            lock (sync)
            {
                delayS = Delay(samples);
                swingPerS = 0; swingRmsDeg = 0; shortDeg = 0; heldCount = 0;
                var errors = new List<double>();
                double sumSq = 0, heldTime = 0;
                int crossings = 0;
                double lastError = double.NaN, lastT = double.NaN;
                int back = 0;
                for (int i = 0; i < samples.Count; i++)
                {
                    while (back < i && samples[i].T - samples[back + 1].T >= 1) back++;
                    bool held = samples[i].T - samples[back].T >= 0.9 && Math.Abs(samples[i].Set - samples[back].Set) < HeldDeg;
                    if (!held) { lastError = double.NaN; continue; }
                    double e = samples[i].Set - samples[i].Act;
                    errors.Add(Math.Abs(e));
                    sumSq += e * e;
                    if (!double.IsNaN(lastError) && samples[i].T - lastT < 0.5)
                    {
                        heldTime += samples[i].T - lastT;
                        if (Math.Sign(e) != Math.Sign(lastError) && Math.Abs(e - lastError) > 0.1) crossings++;
                    }
                    lastError = e;
                    lastT = samples[i].T;
                }
                heldCount = errors.Count;
                if (heldCount == 0) return;
                errors.Sort();
                shortDeg = errors[errors.Count / 2];
                swingRmsDeg = Math.Sqrt(sumSq / heldCount);
                swingPerS = heldTime > 0 ? crossings / heldTime : 0;
            }
        }

        // how long the reported angle follows the wanted one: the shift (0 to 1.5 s) that matches them best
        private static double Delay(List<Sample> s)
        {
            if (s.Count < 50) return double.NaN;
            double mean = 0;
            foreach (Sample x in s) mean += x.Set;
            mean /= s.Count;
            double var = 0;
            foreach (Sample x in s) var += (x.Set - mean) * (x.Set - mean);
            if (Math.Sqrt(var / s.Count) < 0.5) return double.NaN;   // the wanted angle hardly moved: nothing to time

            double best = double.MaxValue, bestShift = 0;
            for (double shift = 0; shift <= 1.5001; shift += 0.1)
            {
                double sum = 0;
                int n = 0, j = 0;
                foreach (Sample x in s)
                {
                    double at = x.T + shift;
                    while (j + 1 < s.Count && s[j + 1].T <= at) j++;
                    if (s[j].T > at || j + 1 >= s.Count) continue;
                    double d = s[j].Act - x.Set;
                    sum += d * d;
                    n++;
                }
                if (n < 30) continue;
                double rms = sum / n;
                if (rms < best) { best = rms; bestShift = shift; }
            }
            return bestShift;
        }

        /// <summary>What to change: the new Kp and minimum PWM (the same values when nothing should change).</summary>
        public TuneVerdict Recommend(int kp, int minPwm, out int newKp, out int newMinPwm)
        {
            newKp = kp;
            newMinPwm = minPwm;
            double delay, swingPerS, swingRms, shortfall;
            int held;
            Measure(out delay, out swingPerS, out swingRms, out shortfall, out held);
            if (held < MinHeldSamples || Count < MinAllSamples) return TuneVerdict.Collecting;

            if (swingPerS > SwingPerS && swingRms > SwingRmsDeg)
            {
                newKp = Math.Max(1, (int)Math.Round(kp * 0.85));
                return TuneVerdict.Oscillates;
            }
            if (shortfall > ShortDeg)
            {
                newMinPwm = Math.Min(100, minPwm + 5);
                return TuneVerdict.DeadBand;
            }
            if (!double.IsNaN(delay) && delay > SlowS)
            {
                newKp = Math.Min(200, Math.Max(kp + 1, (int)Math.Round(kp * 1.15)));
                return TuneVerdict.Slow;
            }
            return TuneVerdict.Good;
        }
    }

    /// <summary>
    /// Look-ahead tuning from the distance off a straight or curved line while autosteer holds it:
    /// crossing the line often (a weave) wants a longer look-ahead, staying off it for long a shorter one.
    /// </summary>
    public class CLookAheadTune
    {
        public const double SampleEveryM = 0.5;
        public const int MinSamples = 200;              // 100 m
        public const double WeavePer10m = 0.8, WeaveRmsM = 0.025;
        public const double SlowPer10m = 0.15, SlowRmsM = 0.04;
        public const double CrossBandM = 0.01;           // a crossing counts only from one side of this band to the other
        private const int MaxSamples = 600;              // the last 300 m

        private readonly List<double> offsets = new List<double>();
        private double lastE = double.NaN, lastN = double.NaN, run;
        private readonly object sync = new object();

        public int Count { get { lock (sync) return offsets.Count; } }

        public void Reset()
        {
            lock (sync) { offsets.Clear(); lastE = lastN = double.NaN; run = 0; }
        }

        /// <summary>One fix while autosteer holds a line (onLine false breaks the run).</summary>
        public void AddSample(double east, double north, double offLineM, bool onLine)
        {
            lock (sync)
            {
                if (!onLine) { lastE = double.NaN; return; }
                if (double.IsNaN(lastE)) { lastE = east; lastN = north; run = 0; return; }
                run += Math.Sqrt((east - lastE) * (east - lastE) + (north - lastN) * (north - lastN));
                lastE = east;
                lastN = north;
                if (run < SampleEveryM) return;
                run = 0;
                offsets.Add(offLineM);
                if (offsets.Count > MaxSamples) offsets.RemoveAt(0);
            }
        }

        public void Measure(out double rmsM, out double crossingsPer10m)
        {
            lock (sync)
            {
                rmsM = 0;
                crossingsPer10m = 0;
                if (offsets.Count == 0) return;
                double sum = 0;
                int side = 0, crossings = 0;
                foreach (double x in offsets)
                {
                    sum += x * x;
                    int s = x > CrossBandM ? 1 : x < -CrossBandM ? -1 : 0;
                    if (s != 0)
                    {
                        if (side != 0 && s != side) crossings++;
                        side = s;
                    }
                }
                rmsM = Math.Sqrt(sum / offsets.Count);
                crossingsPer10m = crossings / (offsets.Count * SampleEveryM / 10);
            }
        }

        /// <summary>The look-ahead hold distance to use (the same when nothing should change), within min..max.</summary>
        public TuneVerdict Recommend(double hold, double min, double max, out double newHold)
        {
            newHold = hold;
            if (Count < MinSamples) return TuneVerdict.Collecting;
            double rms, crossings;
            Measure(out rms, out crossings);
            if (crossings > WeavePer10m && rms > WeaveRmsM)
            {
                newHold = Math.Min(max, Math.Round(hold * 1.1, 1));
                return TuneVerdict.Weaves;
            }
            if (crossings < SlowPer10m && rms > SlowRmsM)
            {
                newHold = Math.Max(min, Math.Round(hold * 0.9, 1));
                return TuneVerdict.ReturnsSlowly;
            }
            return TuneVerdict.Good;
        }
    }

    /// <summary>
    /// Side-hill compensation from the wheel angle needed to hold a straight line at each roll: holding the line, the
    /// wheels stand at what the slope needs, whatever the compensation gave and the guidance added. The slope of
    /// wheel angle over roll is the compensation (the guidance adds roll * -factor, so factor = -slope).
    /// </summary>
    public class CSideHillTune
    {
        public const double SampleEveryM = 0.5;
        public const int MinSamples = 300;
        public const double MinRollSpreadDeg = 3;     // the roll seen must span this much (10th to 90th percentile)
        public const double MinFit = 0.3;              // R²
        public const double OnLineM = 0.05, StraightDeg = 5;
        private const int MaxSamples = 3000;

        private readonly List<double> rolls = new List<double>(), angles = new List<double>();
        private double lastE = double.NaN, lastN = double.NaN, run;
        private readonly object sync = new object();

        public int Count { get { lock (sync) return rolls.Count; } }

        public void Reset()
        {
            lock (sync) { rolls.Clear(); angles.Clear(); lastE = lastN = double.NaN; run = 0; }
        }

        public void AddSample(double east, double north, double rollDeg, double reportedDeg, double offLineM, bool onLine)
        {
            lock (sync)
            {
                if (!onLine || Math.Abs(offLineM) > OnLineM || Math.Abs(reportedDeg) > StraightDeg) { lastE = double.NaN; return; }
                if (double.IsNaN(lastE)) { lastE = east; lastN = north; run = 0; return; }
                run += Math.Sqrt((east - lastE) * (east - lastE) + (north - lastN) * (north - lastN));
                lastE = east;
                lastN = north;
                if (run < SampleEveryM) return;
                run = 0;
                rolls.Add(rollDeg);
                angles.Add(reportedDeg);
                if (rolls.Count > MaxSamples) { rolls.RemoveAt(0); angles.RemoveAt(0); }
            }
        }

        /// <summary>Wheel degrees per degree of roll, how well a line fits (R²) and the roll spread seen.</summary>
        public void Measure(out double slope, out double fit, out double rollSpread)
        {
            lock (sync)
            {
                slope = 0; fit = 0; rollSpread = 0;
                int n = rolls.Count;
                if (n < 3) return;
                double mx = 0, my = 0;
                for (int i = 0; i < n; i++) { mx += rolls[i]; my += angles[i]; }
                mx /= n; my /= n;
                double sxx = 0, sxy = 0, syy = 0;
                for (int i = 0; i < n; i++)
                {
                    double dx = rolls[i] - mx, dy = angles[i] - my;
                    sxx += dx * dx; sxy += dx * dy; syy += dy * dy;
                }
                if (sxx <= 0) return;
                slope = sxy / sxx;
                fit = syy > 0 ? sxy * sxy / (sxx * syy) : 0;
                var sorted = new List<double>(rolls);
                sorted.Sort();
                rollSpread = sorted[(int)(0.9 * (n - 1))] - sorted[(int)(0.1 * (n - 1))];
            }
        }

        /// <summary>The side-hill compensation to use (the same when nothing should change), within min..max.</summary>
        public TuneVerdict Recommend(double factor, double min, double max, out double newFactor)
        {
            newFactor = factor;
            if (Count < MinSamples) return TuneVerdict.Collecting;
            double slope, fit, spread;
            Measure(out slope, out fit, out spread);
            if (spread < MinRollSpreadDeg || fit < MinFit) return TuneVerdict.Collecting;
            double target = Math.Max(min, Math.Min(max, Math.Round(-slope, 2)));
            if (Math.Abs(target - factor) < 0.02) return TuneVerdict.Good;
            newFactor = target;
            return TuneVerdict.Recommend;
        }
    }

    /// <summary>
    /// Dead zone tuning from the steer angle sensor while autosteer holds an angle:
    /// - the zone should be about twice the sensor's own noise: smaller, the motor chases noise; much larger, it
    ///   reacts late;
    /// - the delay: a zone that switches on and off all the time wants a longer delay, a motor that keeps hunting
    ///   back and forth while the zone is hardly ever on wants a shorter one.
    /// </summary>
    public class CDeadZoneTune
    {
        public const double HeldDeg = 0.3;
        public const int MinHeldSamples = 200;
        public const double FlickerPerMin = 30, HuntPerS = 1.5, QuietPerMin = 5;
        private const double WindowS = 60;

        private struct Sample { public double T, Set, Act; public bool Zone; }
        private readonly List<Sample> samples = new List<Sample>();
        private readonly object sync = new object();

        public int Count { get { lock (sync) return samples.Count; } }

        public void Reset() { lock (sync) samples.Clear(); }

        public void AddSample(double timeS, double wantedDeg, double reportedDeg, bool inDeadZone, double speedKmh, bool steering)
        {
            lock (sync)
            {
                if (!steering || speedKmh < 2) return;
                samples.Add(new Sample { T = timeS, Set = wantedDeg, Act = reportedDeg, Zone = inDeadZone });
                while (samples.Count > 0 && timeS - samples[0].T > WindowS) samples.RemoveAt(0);
            }
        }

        /// <summary>The sensor noise (degrees), dead zone switches per minute, motor direction changes per second while held.</summary>
        public void Measure(out double noiseDeg, out double switchesPerMin, out double huntsPerS, out int heldCount)
        {
            lock (sync)
            {
                noiseDeg = 0; switchesPerMin = 0; huntsPerS = 0; heldCount = 0;
                if (samples.Count < 2) return;

                int switches = 0;
                for (int i = 1; i < samples.Count; i++) if (samples[i].Zone != samples[i - 1].Zone) switches++;
                double span = samples[samples.Count - 1].T - samples[0].T;
                switchesPerMin = span > 0 ? switches * 60 / span : 0;

                // steps of the reported angle while the wanted one is held: their spread is the noise
                var steps = new List<double>();
                var times = new List<double>();
                int back = 0;
                for (int i = 1; i < samples.Count; i++)
                {
                    while (back < i && samples[i].T - samples[back + 1].T >= 1) back++;
                    bool held = samples[i].T - samples[back].T >= 0.9 && Math.Abs(samples[i].Set - samples[back].Set) < HeldDeg;
                    if (!held || samples[i].T - samples[i - 1].T > 0.3) continue;
                    steps.Add(samples[i].Act - samples[i - 1].Act);
                    times.Add(samples[i].T - samples[i - 1].T);
                }
                heldCount = steps.Count;
                if (steps.Count < 10) return;

                var abs = new List<double>();
                foreach (double d in steps) abs.Add(Math.Abs(d));
                abs.Sort();
                // the median step of white noise is 0.674 * sigma * sqrt(2): back to the noise of one reading
                noiseDeg = abs[abs.Count / 2] / (0.674 * Math.Sqrt(2));

                // the motor reverses: a real step (beyond the noise) the other way than the last real step
                int reversals = 0, lastSign = 0;
                double heldTime = 0;
                for (int i = 0; i < steps.Count; i++)
                {
                    heldTime += times[i];
                    if (Math.Abs(steps[i]) <= 2 * noiseDeg) continue;
                    int sign = Math.Sign(steps[i]);
                    if (lastSign != 0 && sign != lastSign) reversals++;
                    lastSign = sign;
                }
                huntsPerS = heldTime > 0 ? reversals / heldTime : 0;
            }
        }

        /// <summary>The dead zone (degrees, one decimal) and delay to use; the same values when nothing should change.</summary>
        public TuneVerdict Recommend(double zoneDeg, int delay, double zoneMin, double zoneMax, int delayMin, int delayMax,
            out double newZoneDeg, out int newDelay)
        {
            newZoneDeg = zoneDeg;
            newDelay = delay;
            double noise, switches, hunts;
            int held;
            Measure(out noise, out switches, out hunts, out held);
            if (held < MinHeldSamples) return TuneVerdict.Collecting;

            double target = Math.Max(zoneMin, Math.Min(zoneMax, Math.Round(2 * noise, 1)));
            if (target - zoneDeg >= 0.1 - 1e-9)
            {
                newZoneDeg = target;
                return TuneVerdict.ZoneBelowNoise;
            }
            if (zoneDeg - target >= 0.1 - 1e-9 && zoneDeg > 2 * target)
            {
                newZoneDeg = target;
                return TuneVerdict.ZoneAboveNoise;
            }
            if (switches > FlickerPerMin && delay < delayMax)
            {
                newDelay = Math.Min(delayMax, delay + 2);
                return TuneVerdict.ZoneFlickers;
            }
            if (hunts > HuntPerS && switches < QuietPerMin && delay > delayMin)
            {
                newDelay = Math.Max(delayMin, delay - 1);
                return TuneVerdict.MotorHunts;
            }
            return TuneVerdict.Good;
        }
    }

    /// <summary>
    /// The speed factor of the look-ahead, from how the line is held slowly and fast: weaving only when fast wants a
    /// bigger factor (it looks further ahead the faster it goes), staying off the line only when fast a smaller one.
    /// Both alike: the factor is right, the base look-ahead is the one to tune.
    /// </summary>
    public class CLookAheadSpeedTune
    {
        public const double SlowMaxKmh = 8, FastMinKmh = 10;
        private readonly CLookAheadTune slow = new CLookAheadTune(), fast = new CLookAheadTune();

        public int SlowCount => slow.Count;
        public int FastCount => fast.Count;

        public void Reset() { slow.Reset(); fast.Reset(); }

        public void AddSample(double east, double north, double offLineM, bool onLine, double speedKmh)
        {
            slow.AddSample(east, north, offLineM, onLine && speedKmh <= SlowMaxKmh);
            fast.AddSample(east, north, offLineM, onLine && speedKmh >= FastMinKmh);
        }

        public void Measure(bool fastSide, out double rmsM, out double crossingsPer10m)
        {
            (fastSide ? fast : slow).Measure(out rmsM, out crossingsPer10m);
        }

        /// <summary>The look-ahead speed factor to use (the same when nothing should change), one decimal, within min..max.</summary>
        public TuneVerdict Recommend(double factor, double min, double max, out double newFactor)
        {
            newFactor = factor;
            TuneVerdict s = slow.Recommend(1, 0, 10, out _), f = fast.Recommend(1, 0, 10, out _);
            if (s == TuneVerdict.Collecting || f == TuneVerdict.Collecting) return TuneVerdict.Collecting;
            if (f == TuneVerdict.Weaves && s != TuneVerdict.Weaves)
            {
                newFactor = Math.Min(max, Math.Round(factor * 1.1, 1));
                return TuneVerdict.Weaves;
            }
            if (f == TuneVerdict.ReturnsSlowly && s != TuneVerdict.ReturnsSlowly)
            {
                newFactor = Math.Max(min, Math.Round(factor * 0.9, 1));
                return TuneVerdict.ReturnsSlowly;
            }
            return TuneVerdict.Good;
        }
    }
}
