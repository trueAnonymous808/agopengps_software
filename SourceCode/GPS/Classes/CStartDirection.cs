using System;
using System.Collections.Generic;

namespace AgOpenGPS
{
    /// <summary>
    /// Forwards or backwards at the first move (single antenna, heading not known yet): the jolt the IMU feels when the
    /// tractor starts off. Standing, the accelerometer reads gravity only; starting off, it reads the push too, forwards
    /// or backwards along the tractor. Which of the IMU's axes is "forwards" (and which way round it is mounted) is
    /// learnt while driving with the heading known: the IMU's acceleration against the GNSS speed changing.
    /// Learnt only going forwards above walking pace - a heading the wrong way round (that thinks it reverses) never
    /// teaches it the wrong way.
    /// </summary>
    public sealed class CStartDirection
    {
        public const double StandKmh = 0.15;       // standing: below this
        public const double OnsetKmh = 0.4;        // started off: above this
        public const double OnsetWindowS = 1.0;    // the jolt is averaged over this after starting off
        public const double DecideMs2 = 0.08;      // the jolt along the forward axis at least this, to decide
        public const double LearnKmh = 4;          // learnt from driving forwards above this only
        public const double MinWeight = 30;        // (m/s2)^2 fixes of speed change learnt from before it decides
        public const double MinGain = 0.25;        // and the IMU follows the speed change at least this well

        // the forward axis as learnt: sum of (acceleration high-passed) x (speed change), and of (speed change)^2
        public double SumX, SumY, SumZ, Weight;

        private readonly List<double[]> standing = new List<double[]>();   // t, x, y, z while standing
        private double[] baseline, onsetSum, slow;
        private int onsetN;
        private bool moving;
        private double onsetT = double.NaN, prevT = double.NaN;
        private readonly List<double[]> recent = new List<double[]>();   // t, v, high-passed x, y, z
        private const double WindowS = 0.5;

        /// <summary>The jolt at the last start (m/s2, IMU axes, gravity taken off) and when it began; null: none.</summary>
        public double[] LastJolt { get; private set; }
        public double LastJoltT { get; private set; } = double.NaN;

        /// <summary>How well the IMU's forward axis is known (0..1 of what it needs).</summary>
        public double LearntShare => Math.Min(1, Weight / MinWeight) * Math.Min(1, Gain / MinGain);

        /// <summary>The IMU's acceleration along the learnt axis per m/s2 of speed change (1: exactly).</summary>
        public double Gain => Weight > 1e-6 ? Math.Sqrt(SumX * SumX + SumY * SumY + SumZ * SumZ) / Weight : 0;

        public bool Learnt => Weight >= MinWeight && Gain >= MinGain;

        /// <summary>What was learnt (another vehicle profile: its own); everything else starts again.</summary>
        public void Load(double sumX, double sumY, double sumZ, double weight)
        {
            SumX = sumX;
            SumY = sumY;
            SumZ = sumZ;
            Weight = Math.Max(0, weight);
            standing.Clear();
            recent.Clear();
            baseline = null;
            slow = null;
            moving = false;
            prevT = double.NaN;
            LastJolt = null;
            LastJoltT = double.NaN;
        }

        /// <summary>
        /// Every fix with an IMU summary. speedKmh: the GNSS speed (no sign); headingKnown, reverse: what the program
        /// knows of the direction now.
        /// </summary>
        public void Feed(double t, double speedKmh, double ax, double ay, double az, bool headingKnown, bool reverse)
        {
            if (double.IsNaN(ax) || double.IsNaN(ay) || double.IsNaN(az))
            {
                prevT = double.NaN;
                return;
            }
            var a = new[] { ax, ay, az };
            double speed = Math.Abs(speedKmh);

            // ---- the jolt at starting off ----
            if (speed < StandKmh)
            {
                standing.Add(new[] { t, ax, ay, az });
                while (standing.Count > 0 && t - standing[0][0] > 1.5) standing.RemoveAt(0);
                moving = false;
            }
            else if (!moving && speed > OnsetKmh)
            {
                moving = true;
                onsetT = t;
                baseline = standing.Count >= 3 ? Mean(standing) : null;
                onsetSum = new double[3];
                onsetN = 0;
                standing.Clear();
            }
            if (moving && baseline != null && t - onsetT <= OnsetWindowS)
            {
                for (int k = 0; k < 3; k++) onsetSum[k] += a[k];
                onsetN++;
                LastJolt = new[] { onsetSum[0] / onsetN - baseline[0], onsetSum[1] / onsetN - baseline[1], onsetSum[2] / onsetN - baseline[2] };
                LastJoltT = onsetT;
            }

            // ---- learning the forward axis: the IMU against the speed changing ----
            // (over half a second: from one fix to the next the GNSS speed's noise would drown the change)
            double dt = t - prevT;
            if (slow == null || double.IsNaN(prevT) || dt <= 0 || dt > 0.5)
            {
                slow = (double[])a.Clone();
                recent.Clear();
            }
            double v = speedKmh / 3.6 * (reverse ? -1 : 1);
            recent.Add(new[] { t, v, a[0] - slow[0], a[1] - slow[1], a[2] - slow[2] });
            while (recent.Count > 2 && t - recent[1][0] >= WindowS) recent.RemoveAt(0);
            double span = t - recent[0][0];
            bool learn = headingKnown && !reverse && speed > LearnKmh && span >= WindowS * 0.9 && dt > 0 && dt < 0.5;
            if (learn)
            {
                double dvdt = Math.Max(-3, Math.Min(3, (v - recent[0][1]) / span));
                var m = new double[3];
                for (int i = 1; i < recent.Count; i++)
                    for (int k = 0; k < 3; k++) m[k] += recent[i][k + 2];
                for (int k = 0; k < 3; k++) m[k] /= recent.Count - 1;
                SumX += m[0] * dvdt * dt;
                SumY += m[1] * dvdt * dt;
                SumZ += m[2] * dvdt * dt;
                Weight += dvdt * dvdt * dt;
            }
            // gravity and the IMU's offsets: followed slowly (a few seconds)
            double f = dt > 0 && dt < 0.5 ? Math.Min(1, dt / 3) : 1;
            for (int k = 0; k < 3; k++) slow[k] += (a[k] - slow[k]) * f;
            prevT = t;
        }

        /// <summary>
        /// The first move just now: +1 forwards, -1 backwards, 0 not known (not learnt yet, no jolt seen, or too soft
        /// to tell).
        /// </summary>
        public int FirstMove(double t)
        {
            if (!Learnt || LastJolt == null || double.IsNaN(LastJoltT) || t - LastJoltT > 15 || t < LastJoltT) return 0;
            double n = Math.Sqrt(SumX * SumX + SumY * SumY + SumZ * SumZ);
            double along = (LastJolt[0] * SumX + LastJolt[1] * SumY + LastJolt[2] * SumZ) / n;
            if (along > DecideMs2) return 1;
            if (along < -DecideMs2) return -1;
            return 0;
        }

        private static double[] Mean(List<double[]> list)
        {
            var m = new double[3];
            foreach (double[] s in list)
                for (int k = 0; k < 3; k++) m[k] += s[k + 1];
            for (int k = 0; k < 3; k++) m[k] /= list.Count;
            return m;
        }
    }
}
