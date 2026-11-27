using System;
using System.Collections.Generic;

namespace AgOpenGPS
{
    /// <summary>
    /// RTK bridge: when the RTK fix is lost, the antenna position is carried on from the last RTK position by dead
    /// reckoning - the GPS speed (Doppler, accurate without RTK too) along a heading from the IMU, corrected by the
    /// IMU's own drift learnt while RTK was good (or, without an IMU, from the steer angle and the wheelbase). The
    /// degraded GPS position, less the offset it shows at the loss, is blended in slowly so the dead reckoning cannot
    /// wander off for ever. The bridge ends when its estimated error or its time runs out, or when RTK is back.
    /// Pure logic: positions in local metres (east, north), heading 0 = north, clockwise.
    /// </summary>
    public class CRtkBridge
    {
        public const double OffsetWindowS = 5;      // the degraded position's offset is measured over the first seconds
        public const double FloatBlendS = 60, OtherBlendS = 180;
        public const double LearnSpanS = 120, MinLearnSpanS = 30, MaxDriftDegPerS = 0.02;

        public enum Phase { Idle, Bridging, Expired }

        public double MaxSeconds = 120, MaxErrorCm = 10;

        /// <summary>Blend the degraded GPS position in (off for a virtual outage: there is no degraded position then).</summary>
        public bool BlendGnss = true;

        /// <summary>The error growth (cm/s) the exams measured for this method; NaN: the built-in guess is used.</summary>
        public double LearnedRateCmPerS = double.NaN;

        /// <summary>
        /// Use a measured movement (TDCP) instead of dead reckoning where it is given; a step without it falls back
        /// to dead reckoning.
        /// </summary>
        public bool UseExternal;
        private double extE, extN;
        private bool extValid;

        /// <summary>The movement (east, north metres) since the previous fix from TDCP, before Step.</summary>
        public void SetExternalStep(double east, double north, bool valid)
        {
            bool ok = valid && !double.IsNaN(east) && !double.IsNaN(north) && Math.Abs(east) < 50 && Math.Abs(north) < 50;
            extE = ok ? east : 0;
            extN = ok ? north : 0;
            extValid = ok;
        }

        public Phase Now { get; private set; } = Phase.Idle;
        public double Seconds { get; private set; }
        public double ErrorCm { get; private set; }
        public double LastBridgeS { get; private set; }
        public double LastErrorAtEndCm { get; private set; } = double.NaN;   // measured when RTK came back
        public int Bridges { get; private set; }
        public bool Active => Now == Phase.Bridging;
        public bool UsesImu { get; private set; }

        // learning while RTK is good: the IMU heading's offset from the GPS heading and how fast it drifts
        private readonly List<double> learnT = new List<double>(), learnDiff = new List<double>();
        private bool hadGood;
        private double lastGoodT, lastE, lastN, lastHeading;

        // bridging
        private double startT, prevT, drE, drN, heading, offE, offN, offSum, imuOffset, imuDrift;
        private int offCount, startQuality;

        /// <summary>Takes over what another bridge has learnt about the heading sensor (for a virtual outage).</summary>
        public void CopyLearningFrom(CRtkBridge other)
        {
            learnT.Clear();
            learnDiff.Clear();
            learnT.AddRange(other.learnT);
            learnDiff.AddRange(other.learnDiff);
        }

        /// <summary>Forget what was learnt (the heading sensor changed: IMU, dual antenna or none).</summary>
        public void ResetLearning()
        {
            learnT.Clear();
            learnDiff.Clear();
        }

        /// <summary>While RTK is good and the tractor moves: GPS heading (rad) and the heading sensor's heading (deg).</summary>
        public void Learn(double t, double gpsHeading, double imuHeadingDeg)
        {
            if (double.IsNaN(gpsHeading) || double.IsNaN(imuHeadingDeg) || double.IsInfinity(imuHeadingDeg)) return;
            double diff = Wrap(gpsHeading * 180 / Math.PI - imuHeadingDeg);
            if (learnDiff.Count > 0)
            {
                // keep it continuous across the 0/360 seam
                double last = learnDiff[learnDiff.Count - 1];
                while (diff - last > 180) diff -= 360;
                while (diff - last < -180) diff += 360;
            }
            learnT.Add(t);
            learnDiff.Add(diff);
            while (learnT.Count > 2 && t - learnT[0] > LearnSpanS)
            {
                learnT.RemoveAt(0);
                learnDiff.RemoveAt(0);
            }
        }

        /// <summary>The learnt IMU offset (deg) now and its drift (deg/s); drift 0 until enough has been seen.</summary>
        public void Learnt(out double offsetDeg, out double driftDegPerS, out bool ready)
        {
            offsetDeg = driftDegPerS = 0;
            ready = false;
            int n = learnT.Count;
            if (n < 10) return;
            double span = learnT[n - 1] - learnT[0];
            double mt = 0, md = 0;
            for (int i = 0; i < n; i++) { mt += learnT[i]; md += learnDiff[i]; }
            mt /= n;
            md /= n;
            double stt = 0, std = 0;
            for (int i = 0; i < n; i++)
            {
                stt += (learnT[i] - mt) * (learnT[i] - mt);
                std += (learnT[i] - mt) * (learnDiff[i] - md);
            }
            double slope = stt > 0 && span >= MinLearnSpanS ? std / stt : 0;
            driftDegPerS = Math.Max(-MaxDriftDegPerS, Math.Min(MaxDriftDegPerS, slope));
            offsetDeg = md + driftDegPerS * (learnT[n - 1] - mt);   // at the newest sample
            ready = span >= MinLearnSpanS;
        }

        /// <summary>
        /// One fix. rtkGood: RTK fix with fresh corrections. Raw position (local metres), GPS speed (m/s), heading of
        /// the last good fix (rad), heading sensor (IMU or dual antenna, deg; NaN without), steer angle (deg; NaN
        /// without a steer module) and wheelbase (m) for the fallback without a heading sensor. With neither a heading
        /// sensor nor a steer angle the heading cannot be followed: no bridge then (it would take every curve for a
        /// straight line). Returns the position to use (the raw one unless bridging).
        /// </summary>
        public void Step(double t, bool rtkGood, int fixQuality, double rawE, double rawN, double speedMs, double gpsHeading,
            double imuHeadingDeg, double steerDeg, double wheelbase, out double outE, out double outN)
        {
            outE = rawE;
            outN = rawN;
            if (rtkGood)
            {
                if (Now == Phase.Bridging)
                {
                    // how far the bridge was off when RTK came back: carried on to this moment first
                    double dtEnd = Math.Max(0, Math.Min(1, t - prevT));
                    drE += speedMs * dtEnd * Math.Sin(heading);
                    drN += speedMs * dtEnd * Math.Cos(heading);
                    LastErrorAtEndCm = Math.Sqrt((rawE - drE) * (rawE - drE) + (rawN - drN) * (rawN - drN)) * 100;
                    LastBridgeS = Seconds;
                }
                Now = Phase.Idle;
                hadGood = true;
                lastGoodT = t;
                lastE = rawE;
                lastN = rawN;
                lastHeading = gpsHeading;
                return;
            }
            if (!hadGood || Now == Phase.Expired) return;
            bool canFollowHeading = !double.IsNaN(imuHeadingDeg) || (!double.IsNaN(steerDeg) && wheelbase > 0.5) || (UseExternal && extValid);
            if (double.IsNaN(rawE) || double.IsNaN(rawN) || double.IsNaN(speedMs) || double.IsInfinity(speedMs)) canFollowHeading = false;
            if (!canFollowHeading)
            {
                if (Now == Phase.Bridging) { Now = Phase.Expired; LastBridgeS = Seconds; }
                return;
            }

            if (Now == Phase.Idle)
            {
                // the loss: start from the last good position, carried to now
                Now = Phase.Bridging;
                Bridges++;
                startT = lastGoodT;
                prevT = lastGoodT;
                drE = lastE;
                drN = lastN;
                heading = lastHeading;
                offSum = offE = offN = 0;
                offCount = 0;
                startQuality = fixQuality;
                Learnt(out imuOffset, out imuDrift, out bool ready);
                UsesImu = !double.IsNaN(imuHeadingDeg) && ready;
                if (!UsesImu) imuDrift = 0;
            }

            double dt = Math.Max(0, Math.Min(1, t - prevT));
            prevT = t;
            Seconds = t - startT;

            // the heading: the IMU with its learnt offset and drift, or the steering geometry
            if (UsesImu && !double.IsNaN(imuHeadingDeg))
                heading = (imuHeadingDeg + imuOffset + imuDrift * Seconds) * Math.PI / 180;
            else if (wheelbase > 0.5 && !double.IsNaN(steerDeg))
                heading += speedMs * dt * Math.Tan(Math.Max(-60, Math.Min(60, steerDeg)) * Math.PI / 180) / wheelbase;

            if (UseExternal && extValid)
            {
                // the measured movement; its direction also keeps the heading for a step without it
                drE += extE;
                drN += extN;
                if (Math.Sqrt(extE * extE + extN * extN) > 0.05 && speedMs > 0) heading = Math.Atan2(extE, extN);
            }
            else
            {
                drE += speedMs * dt * Math.Sin(heading);
                drN += speedMs * dt * Math.Cos(heading);
            }
            extValid = false;

            // the degraded position's offset, measured early, then blended in slowly
            if (Seconds <= OffsetWindowS)
            {
                offE += rawE - drE;
                offN += rawN - drN;
                offCount++;
            }
            else if (offCount > 0 && BlendGnss)
            {
                double tau = startQuality == 5 ? FloatBlendS : OtherBlendS;
                double gE = rawE - offE / offCount, gN = rawN - offN / offCount;
                drE += (gE - drE) * Math.Min(1, dt / tau);
                drN += (gN - drN) * Math.Min(1, dt / tau);
            }

            ErrorCm = !double.IsNaN(LearnedRateCmPerS) ? 1 + LearnedRateCmPerS * Seconds
                : UseExternal ? 1 + 0.03 * Seconds
                : EstimateCm(Seconds, startQuality == 5, UsesImu);
            if (double.IsNaN(drE) || double.IsNaN(drN) || double.IsInfinity(drE) || double.IsInfinity(drN))
            {
                Now = Phase.Expired;   // something went wrong in the sums: the raw position again
                LastBridgeS = Seconds;
                return;
            }
            if (Seconds > MaxSeconds || ErrorCm > MaxErrorCm)
            {
                Now = Phase.Expired;
                LastBridgeS = Seconds;
                return;   // from here the raw position again (the guard takes over)
            }
            outE = drE;
            outN = drN;
        }

        /// <summary>
        /// The bridge's own error estimate (cm) after some seconds: dead reckoning grows about linearly (heading
        /// error times distance); with float the blend holds it better than with DGPS; without an IMU it doubles.
        /// </summary>
        public static double EstimateCm(double seconds, bool isFloat, bool imu)
        {
            double perS = isFloat ? 0.08 : 0.15;
            if (!imu) perS *= 2;
            return 1 + perS * seconds;
        }

        /// <summary>Back to waiting (a new field or the bridge switched off).</summary>
        public void Reset()
        {
            Now = Phase.Idle;
            hadGood = false;
            learnT.Clear();
            learnDiff.Clear();
        }

        private static double Wrap(double deg)
        {
            while (deg > 180) deg -= 360;
            while (deg < -180) deg += 360;
            return deg;
        }
    }
}
