using System;
using System.Collections.Generic;
using System.Linq;

namespace AgOpenGPS
{
    /// <summary>
    /// How the steering itself answers, measured while autosteer works: the dead time from the angle asked to the
    /// wheels following it, how fast the wheels can turn, and the play (backlash) when the direction changes. Large
    /// play or a slow answer is a matter for the workshop, not for tuning: the window says so.
    /// </summary>
    public sealed class CSteerDynamics
    {
        public const double StepS = 0.1;             // samples at 10 Hz
        public const int Capacity = 1800;            // three minutes
        public const int MinMoving = 300;            // samples with the asked angle moving, before anything is told

        private readonly List<double[]> s = new List<double[]>();   // t, asked, actual
        private double lastT = -1e9;

        public double DelayS { get; private set; } = double.NaN;
        public double MaxRateDegS { get; private set; } = double.NaN;
        /// <summary>The same turning right and left apart: a steering cylinder is often quicker one way.</summary>
        public double MaxRateRightDegS { get; private set; } = double.NaN;
        public double MaxRateLeftDegS { get; private set; } = double.NaN;
        public double BacklashDeg { get; private set; } = double.NaN;
        public int Moving { get; private set; }
        public bool Ready => Moving >= MinMoving && !double.IsNaN(DelayS);

        public void Reset()
        {
            s.Clear();
            lastT = -1e9;
            DelayS = MaxRateDegS = BacklashDeg = MaxRateRightDegS = MaxRateLeftDegS = double.NaN;
            Moving = 0;
        }

        public void AddSample(double t, double askedDeg, double actualDeg, bool steering)
        {
            if (!steering || double.IsNaN(askedDeg) || double.IsNaN(actualDeg)) return;
            if (t - lastT < StepS * 0.9) return;
            lastT = t;
            s.Add(new[] { t, askedDeg, actualDeg });
            if (s.Count > Capacity) s.RemoveAt(0);
        }

        // two samples k steps apart with no gap between them
        private bool Joined(int i, int k) => i - k >= 0 && Math.Abs(s[i][0] - s[i - k][0] - k * StepS) < 0.5 * StepS + 0.02 * k;

        /// <summary>Works the figures out from what was collected (a few milliseconds; once in a while is enough).</summary>
        public void Analyze()
        {
            int n = s.Count;
            Moving = 0;
            for (int i = 2; i < n; i++)
                if (Joined(i, 2) && Math.Abs(s[i][1] - s[i - 2][1]) / (2 * StepS) > 0.5) Moving++;
            if (Moving < MinMoving) return;

            // the dead time: the shift at which the wheels' movement matches the asked angle's movement best (the
            // movements, not the angles: play keeps the wheels a little behind, but while they move they move alike)
            int best = 0;
            double bestErr = double.MaxValue;
            var errs = new double[13];
            for (int k = 0; k <= 12; k++)
            {
                double sum = 0;
                int cnt = 0;
                for (int i = k + 1; i < n; i++)
                {
                    if (!Joined(i, k + 1)) continue;
                    double wheels = s[i][2] - s[i - 1][2];
                    if (Math.Abs(wheels) / StepS < 0.5) continue;   // standing still in the play
                    sum += Math.Abs(wheels - (s[i - k][1] - s[i - k - 1][1]));
                    cnt++;
                }
                errs[k] = cnt > 50 ? sum / cnt : double.MaxValue;
                if (errs[k] < bestErr) { bestErr = errs[k]; best = k; }
            }
            double frac = 0;
            if (best > 0 && best < 12 && errs[best - 1] < double.MaxValue && errs[best + 1] < double.MaxValue)
            {
                double a = errs[best - 1], b = errs[best], c = errs[best + 1];
                double den = a - 2 * b + c;
                if (den > 1e-9) frac = Math.Max(-0.5, Math.Min(0.5, 0.5 * (a - c) / den));
            }
            DelayS = (best + frac) * StepS;

            // the fastest the wheels turn: only known where they were asked to go faster than they went (the motor at
            // its limit); otherwise it is only how fast they were asked to move, which tells nothing
            var rates = new List<double>();
            var right = new List<double>();
            var left = new List<double>();
            for (int i = best + 1; i < n; i++)
            {
                if (!Joined(i, best + 1)) continue;
                double move = (s[i][2] - s[i - 1][2]) / StepS;
                double wheels = Math.Abs(move);
                double asked = Math.Abs(s[i - best][1] - s[i - best - 1][1]) / StepS;
                if (asked > wheels + 3)
                {
                    rates.Add(wheels);
                    (move > 0 ? right : left).Add(wheels);
                }
            }
            rates.Sort();
            right.Sort();
            left.Sort();
            MaxRateDegS = rates.Count >= 20 ? rates[rates.Count / 2] : double.NaN;
            MaxRateRightDegS = right.Count >= 10 ? right[right.Count / 2] : MaxRateDegS;
            MaxRateLeftDegS = left.Count >= 10 ? left[left.Count / 2] : MaxRateDegS;

            // the play: the wheels stay behind the asked angle by the same amount in whichever direction it moves;
            // only slow movements, where the following lag is small
            // (and only where the wheels themselves move the same way: right after a turn of direction they stand
            // still while the play is crossed, which may take seconds when the angle changes slowly)
            double up = 0, down = 0;
            int nUp = 0, nDown = 0;
            for (int i = best + 2; i < n; i++)
            {
                if (!Joined(i, best + 2)) continue;
                double askedThen = s[i - best][1];
                double rate = (s[i - best][1] - s[i - best - 2][1]) / (2 * StepS);
                double ar = Math.Abs(rate);
                if (ar < 0.5 || ar > 4) continue;
                double wheelsRate = (s[i][2] - s[i - 1][2]) / StepS;
                if (Math.Sign(wheelsRate) != Math.Sign(rate) || Math.Abs(wheelsRate) < 0.2) continue;
                double lag = askedThen - s[i][2];
                if (rate > 0) { up += lag; nUp++; }
                else { down += lag; nDown++; }
            }
            BacklashDeg = nUp >= 30 && nDown >= 30 ? Math.Max(0, up / nUp - down / nDown) : double.NaN;
        }

        /// <summary>The steering is worth a look in the workshop: much play, or a slow answer.</summary>
        public bool MuchPlay => !double.IsNaN(BacklashDeg) && BacklashDeg > 1.0;
        public bool SlowAnswer => !double.IsNaN(DelayS) && DelayS > 0.4;
        public bool SlowMotor => !double.IsNaN(MaxRateDegS) && MaxRateDegS < 10;
    }

    /// <summary>
    /// Dead-time compensation: the angle sent to the module runs a little ahead of the one asked for - by how fast it
    /// changes times the steering's dead time - so the wheels are where the line wants them, not where it wanted them
    /// a moment ago. The guidance itself (and the distance off the line every tuner reads) is not touched.
    /// </summary>
    public sealed class CLeadComp
    {
        public const double MaxAddDeg = 5;
        private double prevDeg = double.NaN, prevT, rate;

        public double Apply(double t, double deg, double leadS, bool active)
        {
            if (!active || leadS <= 0 || double.IsNaN(deg) || double.IsNaN(prevDeg))
            {
                prevDeg = deg;
                prevT = t;
                rate = 0;
                return deg;
            }
            double dt = t - prevT;
            if (dt <= 1e-6) return deg + Math.Max(-MaxAddDeg, Math.Min(MaxAddDeg, rate * leadS));
            if (dt > 0.5)
            {
                prevDeg = deg;
                prevT = t;
                rate = 0;
                return deg;
            }
            double r = (deg - prevDeg) / dt;
            rate += (r - rate) * Math.Min(1, dt / 0.25);   // smoothed over a quarter second
            prevDeg = deg;
            prevT = t;
            return deg + Math.Max(-MaxAddDeg, Math.Min(MaxAddDeg, rate * leadS));
        }
    }

    public enum ChangeVerdict { None, Pending, Better, Same, Worse, Unclear }

    /// <summary>
    /// Whether an automatic change made the steering better: the distance off the line before and after, compared
    /// only where the conditions match (the same speed band, straight or on a curve). Clearly worse: it is undone.
    /// </summary>
    public sealed class CChangeVerifier
    {
        public const double WindowS = 600;       // the ten minutes before the change
        public const double MinCommonS = 180;    // three minutes after it, in conditions also seen before
        public const double TimeoutS = 1800;     // not enough comparable driving in half an hour: kept as it is
        public const double StepS = 0.1;

        private readonly List<double[]> samples = new List<double[]>();   // t, bucket, xte^2
        private double lastT = -1e9, trialStart = double.NaN;

        public string Name { get; private set; }
        public bool Active => !double.IsNaN(trialStart);
        public double TrialSeconds(double t) => Active ? t - trialStart : 0;

        public static int Bucket(double speedKmh, bool curve) => (speedKmh < 6 ? 0 : speedKmh < 10 ? 1 : 2) + (curve ? 3 : 0);

        public void Feed(double t, double xteM, double speedKmh, bool curve, bool onLine)
        {
            if (!onLine || double.IsNaN(xteM) || Math.Abs(xteM) > 2) return;
            if (t - lastT < StepS * 0.9) return;
            lastT = t;
            samples.Add(new[] { t, Bucket(speedKmh, curve), xteM * xteM });
            double keepFrom = Active ? trialStart - WindowS : t - WindowS;
            int drop = 0;
            while (drop < samples.Count && samples[drop][0] < keepFrom) drop++;
            if (drop > 0) samples.RemoveRange(0, drop);
        }

        public void Begin(double t, string name)
        {
            trialStart = t;
            Name = name;
        }

        /// <summary>Everything measured so far belongs to old settings (a revert, the tuners started again).</summary>
        public void Forget()
        {
            samples.Clear();
            trialStart = double.NaN;
        }

        public ChangeVerdict Evaluate(double t, out double beforeCm, out double afterCm)
        {
            beforeCm = afterCm = double.NaN;
            if (!Active) return ChangeVerdict.None;
            var nb = new int[6];
            var na = new int[6];
            var sb = new double[6];
            var sa = new double[6];
            foreach (double[] x in samples)
            {
                int b = (int)x[1];
                if (x[0] < trialStart) { nb[b]++; sb[b] += x[2]; }
                else { na[b]++; sa[b] += x[2]; }
            }
            double w = 0, mb = 0, ma = 0;
            for (int b = 0; b < 6; b++)
            {
                if (nb[b] < 100 || na[b] < 100) continue;   // ten seconds each, at least
                w += na[b];
                mb += na[b] * sb[b] / nb[b];
                ma += na[b] * sa[b] / na[b];
            }
            if (w * StepS < MinCommonS)
            {
                if (t - trialStart < TimeoutS) return ChangeVerdict.Pending;
                Finish();
                return ChangeVerdict.Unclear;
            }
            double rb = Math.Sqrt(mb / w), ra = Math.Sqrt(ma / w);
            beforeCm = rb * 100;
            afterCm = ra * 100;
            Finish();
            if (ra > rb * 1.15 && ra - rb > 0.005) return ChangeVerdict.Worse;
            if (ra < rb * 0.95) return ChangeVerdict.Better;
            return ChangeVerdict.Same;
        }

        // the driving after the change is the history of the settings now in use
        private void Finish()
        {
            double from = trialStart;
            samples.RemoveAll(x => x[0] < from);
            trialStart = double.NaN;
        }
    }

    /// <summary>
    /// A small simulation of the tractor on a straight line: the steering geometry, the steering's dead time, speed
    /// and play as measured, pure pursuit with AgOpenGPS's look-ahead, and the ground's small pushes. A look-ahead
    /// change is tried here first and goes to the real tractor only if it is not worse.
    /// </summary>
    public static class CSteerSim
    {
        public sealed class Look
        {
            public double Hold, Mult, Acquire;
        }

        public static double LookAhead(Look l, double xteAbs, double speedKmh)
        {
            double basic = speedKmh * 0.05 * l.Mult;
            double hold = l.Hold, acquire = l.Hold * l.Acquire;
            double d;
            if (xteAbs <= 0.1) d = basic * hold + hold;
            else if (xteAbs < 0.4)
            {
                double h = (1 - (xteAbs - 0.1) / 0.3) * (hold - acquire) + acquire;
                d = basic * h + h;
            }
            else d = basic * acquire + acquire;
            return Math.Max(2, d);
        }

        /// <summary>The root mean square distance off the line (m) over a run with the given settings.</summary>
        public static double RmsXte(Look l, double wheelbase, double speedKmh, double delayS, double rateDegS, double playDeg,
            double maxSteerDeg, int seed, double seconds = 90)
        {
            const double dt = 0.05;
            var rnd = new Random(seed);
            double v = Math.Max(0.5, speedKmh / 3.6), L = Math.Max(1, wheelbase);
            double y = 0.4, psi = 0, steer = 0;
            int delaySteps = Math.Max(0, (int)Math.Round(delayS / dt));
            var queue = new Queue<double>();
            for (int i = 0; i < delaySteps; i++) queue.Enqueue(0);
            double rate = double.IsNaN(rateDegS) || rateDegS <= 0 ? 30 : rateDegS;
            double play = double.IsNaN(playDeg) ? 0 : Math.Max(0, playDeg);
            double sum = 0;
            int cnt = 0;
            for (double t = 0; t < seconds; t += dt)
            {
                double ld = LookAhead(l, Math.Abs(y), speedKmh);
                double alpha = Math.Atan2(-y, ld) - psi;
                double dist = Math.Sqrt(ld * ld + y * y);
                double asked = Math.Atan(2 * L * Math.Sin(alpha) / dist) * 180 / Math.PI;
                asked = Math.Max(-maxSteerDeg, Math.Min(maxSteerDeg, asked));
                queue.Enqueue(asked);
                double target = queue.Dequeue();

                // the wheels follow the angle asked, at most so fast, and only once the play is taken up
                double gap = target - steer;
                if (Math.Abs(gap) > play / 2)
                {
                    double want = target - Math.Sign(gap) * play / 2;
                    double step = Math.Max(-rate * dt, Math.Min(rate * dt, want - steer));
                    steer += step;
                }

                psi += v / L * Math.Tan(steer * Math.PI / 180) * dt;
                psi += (rnd.NextDouble() - 0.5) * 0.004;   // the ground pushes the heading a little
                y += v * Math.Sin(psi) * dt;
                if (t > 10)
                {
                    sum += y * y;
                    cnt++;
                }
            }
            return Math.Sqrt(sum / Math.Max(1, cnt));
        }

        /// <summary>Whether the new look-ahead does not steer worse than the one now, over a few runs and speeds.</summary>
        public static bool NotWorse(Look now, Look candidate, double wheelbase, double speedKmh, double delayS, double rateDegS,
            double playDeg, double maxSteerDeg, out double nowCm, out double candidateCm)
        {
            double a = 0, b = 0;
            foreach (double sp in new[] { speedKmh, speedKmh * 0.7 })
                for (int seed = 1; seed <= 3; seed++)
                {
                    a += RmsXte(now, wheelbase, sp, delayS, rateDegS, playDeg, maxSteerDeg, seed);
                    b += RmsXte(candidate, wheelbase, sp, delayS, rateDegS, playDeg, maxSteerDeg, seed);
                }
            nowCm = a / 6 * 100;
            candidateCm = b / 6 * 100;
            return b <= a * 1.05;
        }
    }

    /// <summary>The track gap measured on the ground, turned into a change of the antenna's side offset.</summary>
    public static class TrackGap
    {
        /// <summary>
        /// The change of the antenna offset (m) for a measured gap: the tractor runs beside the line by half the gap,
        /// to the right when the mark is on the left on the pass back. A larger offset moves the position the program
        /// uses to the right (Position: fix + cos(-heading) * offset east when heading north).
        /// </summary>
        public static double Correction(double gapCm, bool markOnLeft)
        {
            double half = Math.Abs(gapCm) / 200.0;
            return markOnLeft ? half : -half;
        }
    }

    /// <summary>The steering values kept for each implement, in the vehicle profile: name and values, one after another.</summary>
    public static class ToolTunes
    {
        private const char Entry = '¦', Pair = '¤';

        public static Dictionary<string, string> Read(string text)
        {
            var map = new Dictionary<string, string>();
            if (string.IsNullOrEmpty(text)) return map;
            foreach (string e in text.Split(Entry))
            {
                int i = e.IndexOf(Pair);
                if (i > 0) map[e.Substring(0, i)] = e.Substring(i + 1);
            }
            return map;
        }

        public static string Write(Dictionary<string, string> map) =>
            string.Join(Entry.ToString(), map.Where(kv => kv.Key.Length > 0 && kv.Key.IndexOf(Entry) < 0 && kv.Key.IndexOf(Pair) < 0)
                .Select(kv => kv.Key + Pair + kv.Value));
    }
}
