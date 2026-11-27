using System;
using System.Collections.Generic;

namespace AgOpenGPS
{
    /// <summary>
    /// GPS quality guard: once an RTK fix has been seen, losing it (float, DGPS, no fix) or correction data getting old
    /// makes the position jump 10-50 cm - and nothing on the screen shows it. The guard tells the state, how long it
    /// has lasted, and when autosteer should be switched off. Without RTK in this session it stays quiet (a receiver
    /// that never had RTK is not "losing" anything).
    /// </summary>
    public class CGpsGuard
    {
        public const int RtkFix = 4;
        public const double OldCorrectionS = 3;

        public enum State { Off, Good, Degraded }

        private bool seenRtk;
        private double degradedSince = double.NaN;

        public State Now { get; private set; } = State.Off;
        public double DegradedForS { get; private set; }
        public int Events { get; private set; }

        public void Reset()
        {
            seenRtk = false;
            degradedSince = double.NaN;
            Now = State.Off;
            DegradedForS = 0;
            Events = 0;
        }

        /// <summary>One fix. Returns true at the moment the signal turns bad (for the warning).</summary>
        public bool Update(double timeS, int fixQuality, double correctionAgeS)
        {
            if (fixQuality == RtkFix && correctionAgeS <= OldCorrectionS)
            {
                seenRtk = true;
                Now = State.Good;
                degradedSince = double.NaN;
                DegradedForS = 0;
                return false;
            }
            if (!seenRtk) { Now = State.Off; return false; }

            bool turned = Now != State.Degraded;
            if (turned)
            {
                degradedSince = timeS;
                Events++;
            }
            Now = State.Degraded;
            DegradedForS = timeS - degradedSince;
            return turned;
        }

        /// <summary>Whether autosteer should be switched off: bad for longer than the given seconds (0: never).</summary>
        public bool ShouldDisengage(double afterS) => afterS > 0 && Now == State.Degraded && DegradedForS >= afterS;
    }

    /// <summary>
    /// Steer angle sensor linearity: the sensor's linkage often reads right in the middle and drifts at bigger angles.
    /// The geometry calibration's ratios (real / sensor) per angle band, relative to the small band, give a factor for
    /// the middle and the big band; the angle sent to the module is divided by it, so the module reaches the wanted
    /// real angle although its own reading is off.
    /// </summary>
    public static class CWasLinearity
    {
        public const double SmallEnd = 12, MiddleEnd = 20, MiddleCentre = 16, BigCentre = 27.5, SmallCentre = 8.5;
        public const double MinFactor = 0.85, MaxFactor = 1.18;

        /// <summary>The band of an angle: 0 small (5-12°), 1 middle (12-20°), 2 big (20° and more).</summary>
        public static int Band(double angleDeg)
        {
            double a = Math.Abs(angleDeg);
            return a < SmallEnd ? 0 : a < MiddleEnd ? 1 : 2;
        }

        /// <summary>The factor at an angle: 1 up to the small band's centre, then straight lines through the band factors.</summary>
        public static double Factor(double angleDeg, double middle, double big)
        {
            double a = Math.Abs(angleDeg);
            if (a <= SmallCentre) return 1;
            if (a <= MiddleCentre) return 1 + (middle - 1) * (a - SmallCentre) / (MiddleCentre - SmallCentre);
            if (a <= BigCentre) return middle + (big - middle) * (a - MiddleCentre) / (BigCentre - MiddleCentre);
            return big;
        }

        /// <summary>The angle to send for a wanted real angle.</summary>
        public static double Apply(double wantedDeg, double middle, double big)
        {
            if (middle == 1 && big == 1) return wantedDeg;
            return wantedDeg / Factor(wantedDeg, middle, big);
        }

        public static double Clamp(double factor) => Math.Max(MinFactor, Math.Min(MaxFactor, factor));
    }

    /// <summary>
    /// IMU fusion (single antenna): how fast the IMU heading is pulled to the GPS heading. Too fast, the GPS heading's
    /// noise comes through; too slow, the IMU's drift stays in. Measured straight on a line above 5 km/h: the noise of
    /// the GPS heading from fix to fix, and how far the fused heading stays off the GPS heading on average.
    /// </summary>
    public class CFusionTune
    {
        public const int MinSamples = 300, MaxSamples = 1200;
        public const double DriftDeg = 0.3, NoiseInDeg = 0.15;

        private readonly List<double> steps = new List<double>(), offsets = new List<double>();
        private double lastGps = double.NaN;
        private readonly object sync = new object();

        public int Count { get { lock (sync) return offsets.Count; } }

        public void Reset() { lock (sync) { steps.Clear(); offsets.Clear(); lastGps = double.NaN; } }

        /// <summary>One fix: GPS heading and fused heading in radians.</summary>
        public void AddSample(double gpsHeading, double fusedHeading, bool straightOnLine)
        {
            lock (sync)
            {
                if (!straightOnLine) { lastGps = double.NaN; return; }
                if (!double.IsNaN(lastGps))
                {
                    steps.Add(Wrap(gpsHeading - lastGps) * 180 / Math.PI);
                    if (steps.Count > MaxSamples) steps.RemoveAt(0);
                }
                lastGps = gpsHeading;
                offsets.Add(Wrap(fusedHeading - gpsHeading) * 180 / Math.PI);
                if (offsets.Count > MaxSamples) offsets.RemoveAt(0);
            }
        }

        /// <summary>GPS heading noise (degrees, one fix), and the drift: the mean fused-minus-GPS offset over 10 s blocks.</summary>
        public void Measure(out double noiseDeg, out double driftDeg)
        {
            lock (sync)
            {
                noiseDeg = driftDeg = 0;
                if (steps.Count > 10)
                {
                    double sq = 0;
                    foreach (double s in steps) sq += s * s;
                    noiseDeg = Math.Sqrt(sq / steps.Count) / Math.Sqrt(2);
                }
                const int block = 100;
                for (int i = 0; i + block <= offsets.Count; i += block)
                {
                    double sum = 0;
                    for (int j = i; j < i + block; j++) sum += offsets[j];
                    driftDeg = Math.Max(driftDeg, Math.Abs(sum / block));
                }
            }
        }

        public TuneVerdict Recommend(double weight, double min, double max, out double newWeight)
        {
            newWeight = weight;
            if (Count < MinSamples) return TuneVerdict.Collecting;
            Measure(out double noise, out double drift);
            if (drift > DriftDeg && weight < max)
            {
                newWeight = Math.Min(max, Math.Round(weight * 1.25, 3));
                return TuneVerdict.FusionDrifts;
            }
            // what of the GPS noise gets through the filter: about the noise times the square root of half the weight
            if (noise * Math.Sqrt(weight / 2) > NoiseInDeg && weight > min)
            {
                newWeight = Math.Max(min, Math.Round(weight * 0.8, 3));
                return TuneVerdict.FusionNoisy;
            }
            return TuneVerdict.Good;
        }

        private static double Wrap(double r) => Math.IEEERemainder(r, 2 * Math.PI);
    }

    /// <summary>The Kp for a speed: the slow one up to 8 km/h, the fast one from 12 km/h, a straight line between.</summary>
    public static class CSpeedGain
    {
        public const double SlowKmh = 8, FastKmh = 12;

        public static int Kp(int slowKp, int fastKp, double speedKmh)
        {
            if (fastKp <= 0) return slowKp;   // off: one Kp for all speeds
            double v = Math.Abs(speedKmh);
            if (v <= SlowKmh) return slowKp;
            if (v >= FastKmh) return fastKp;
            return (int)Math.Round(slowKp + (fastKp - slowKp) * (v - SlowKmh) / (FastKmh - SlowKmh));
        }
    }
}
