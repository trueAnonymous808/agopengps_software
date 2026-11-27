using System;

namespace AgOpenGPS
{
    /// <summary>
    /// A Kalman filter (extended) for the tractor's state, put together from everything it knows: the GNSS position
    /// (trusted by its fix quality), the GNSS speed and course, the IMU's heading, a dual antenna's heading, and the
    /// steer angle with the wheelbase (which way the tractor must turn). Besides position, heading and speed it learns:
    /// the IMU's heading offset, the steer angle sensor's offset (the angle the tractor really drives straight with: a
    /// slope, a zero a little off) and - with a dual antenna only - the sideways slip on a slope.
    /// The heading is a bearing (rad, 0 north, clockwise), angles + right.
    /// </summary>
    public sealed class CStateEstimator
    {
        public const int E = 0, N = 1, H = 2, V = 3, ImuOff = 4, SteerOff = 5, Slip = 6, Count = 7;

        private readonly double[] x = new double[Count];
        private readonly double[,] P = new double[Count, Count];
        private double lastT = double.NaN;
        private double innovationMs = double.NaN;
        private int updates;
        private bool hadImu;

        public bool Started { get; private set; }

        /// <summary>The state: east, north (m), heading (rad), speed (m/s), IMU offset (rad), steer offset (rad), slip (m/s).</summary>
        public double this[int i] => x[i];
        public double Sigma(int i) => Math.Sqrt(Math.Max(0, P[i, i]));

        public double SteerOffsetDeg => x[SteerOff] * 180 / Math.PI;

        // the IMU's heading noise, measured from what it says against what the filter expects: a TM171 is several
        // times quieter than a BNO085, and each is trusted as much as it deserves
        private double imuNoiseVar = Rad(0.5) * Rad(0.5);

        /// <summary>How noisy the IMU's heading is (degrees, measured).</summary>
        public double ImuNoiseDeg => Math.Sqrt(imuNoiseVar) * 180 / Math.PI;
        public double ImuOffsetDeg => x[ImuOff] * 180 / Math.PI;
        public double SlipMs => x[Slip];

        /// <summary>The GNSS positions' distance from where the filter expected them (RMS, cm, recent).</summary>
        public double PositionInnovationCm => Math.Sqrt(innovationMs) * 100;

        /// <summary>Learnt enough to be used: a while running, and the steer offset known to a third of a degree.</summary>
        public bool Ready => Started && updates > 300 && Sigma(SteerOff) < 0.33 * Math.PI / 180;

        public void Reset()
        {
            Started = false;
            lastT = double.NaN;
            innovationMs = double.NaN;
            updates = 0;
        }

        public struct Inputs
        {
            public double T;                // time (s)
            public double East, North;      // GNSS position of the pivot (m)
            public double PositionSigmaM;   // how far it may be off (by fix quality); NaN: no position now
            public double SpeedMs;          // GNSS speed (- reversing); NaN: none
            public double CourseRad;        // GNSS course over ground (single antenna); NaN: none
            public double ImuHeadingRad;    // IMU heading; NaN: no IMU
            public double DualHeadingRad;   // dual antenna heading; NaN: none
            public double SteerRad;         // steer angle sensor (real angle); NaN: no steer module
            public double WheelbaseM;
            public bool HasYawRate;         // the IMU's turning rate over the interval (the module's IMU summary)
            public double YawRateRadS;      //  heading increasing = +
        }

        /// <summary>One fix: predicts to its time, then takes in what was measured.</summary>
        public void Step(Inputs m)
        {
            if (!Started)
            {
                if (double.IsNaN(m.PositionSigmaM) || (double.IsNaN(m.CourseRad) && double.IsNaN(m.DualHeadingRad))) return;
                Start(m);
                return;
            }
            double dt = m.T - lastT;
            lastT = m.T;
            if (dt <= 0) return;
            if (dt > 2) { Reset(); return; }   // a long gap: start again

            Predict(dt, m);

            if (!double.IsNaN(m.PositionSigmaM))
            {
                double de = m.East - x[E], dn = m.North - x[N];
                // a jump far beyond what is expected (a new field origin, a fix gone wrong): start again
                if (de * de + dn * dn > 25 + 100 * m.PositionSigmaM * m.PositionSigmaM) { Reset(); return; }
                double d2 = (de * de + dn * dn) * 0.5;
                innovationMs = double.IsNaN(innovationMs) ? d2 : innovationMs * 0.98 + d2 * 0.02;
                // (the position is the anchor: never left out as an outlier)
                double r = m.PositionSigmaM * m.PositionSigmaM;
                Update(E, m.East - x[E], r, false);
                Update(N, m.North - x[N], r, false);
                updates++;
            }
            if (!double.IsNaN(m.SpeedMs)) Update(V, m.SpeedMs - x[V], 0.15 * 0.15);

            if (!double.IsNaN(m.DualHeadingRad))
                Update(H, Wrap(m.DualHeadingRad - x[H]), Rad(0.3) * Rad(0.3));
            else
            {
                // no dual antenna: the slip cannot be told apart from the IMU's offset or the course - held at none
                Update(Slip, -x[Slip], 0.02 * 0.02);
            }

            // the course: the heading plus the slip's angle (only moving, else it is the GNSS noise)
            if (!double.IsNaN(m.CourseRad) && Math.Abs(x[V]) > 1)
            {
                double v = x[V], s = x[Slip];
                double d2 = v * v + s * s;
                double predicted = x[H] + Math.Atan2(s, Math.Abs(v)) * Math.Sign(v);
                double[] h = bufH;
                Array.Clear(h, 0, h.Length);
                h[H] = 1;
                h[V] = -s / d2 * Math.Sign(v);
                h[Slip] = Math.Abs(v) / d2 * Math.Sign(v);
                double sigma = Rad(Math.Abs(v) > 2 ? 1.5 : 3);
                UpdateVector(h, Wrap(m.CourseRad - predicted), sigma * sigma);
            }

            if (!double.IsNaN(m.ImuHeadingRad))
            {
                if (!hadImu) { x[ImuOff] = Wrap(m.ImuHeadingRad - x[H]); P[ImuOff, ImuOff] = Rad(10) * Rad(10); hadImu = true; }
                double[] h = bufH;
                Array.Clear(h, 0, h.Length);
                h[H] = 1;
                h[ImuOff] = 1;
                double residual = Wrap(m.ImuHeadingRad - x[H] - x[ImuOff]);
                // the residual's spread beyond what the filter's own uncertainty explains is the IMU's noise
                double expected = P[H, H] + 2 * P[H, ImuOff] + P[ImuOff, ImuOff];
                double sample = residual * residual - expected;
                if (residual * residual < 25 * (expected + imuNoiseVar))
                    imuNoiseVar = Math.Max(Rad(0.05) * Rad(0.05), Math.Min(Rad(3) * Rad(3), imuNoiseVar + (sample - imuNoiseVar) * 0.01));
                UpdateVector(h, residual, imuNoiseVar);
            }

            // the turning rate: what the steer angle (with its offset) says it should be, at speed - the gyro tells
            // the steer offset far sooner than the heading drifting away does
            if (m.HasYawRate && !double.IsNaN(m.SteerRad) && m.WheelbaseM > 0.5 && Math.Abs(x[V]) > 1 && Math.Abs(m.YawRateRadS) < Rad(40))
            {
                double L = Math.Max(0.5, m.WheelbaseM);
                double delta = Math.Max(-1.2, Math.Min(1.2, m.SteerRad + x[SteerOff]));
                double tanD = Math.Tan(delta), c = Math.Cos(delta), v = x[V];
                double[] h = bufH;
                Array.Clear(h, 0, h.Length);
                h[V] = tanD / L;
                h[SteerOff] = v / (L * c * c);
                // the gyro's own noise and the steering moving within the interval
                double sigma = Rad(0.4) + Math.Abs(m.YawRateRadS) * 0.05;
                UpdateVector(h, m.YawRateRadS - v * tanD / L, sigma * sigma);
            }
        }

        private void Start(Inputs m)
        {
            Array.Clear(x, 0, Count);
            Array.Clear(P, 0, P.Length);
            x[E] = m.East;
            x[N] = m.North;
            x[H] = !double.IsNaN(m.DualHeadingRad) ? m.DualHeadingRad : m.CourseRad;
            x[V] = double.IsNaN(m.SpeedMs) ? 0 : m.SpeedMs;
            hadImu = !double.IsNaN(m.ImuHeadingRad);
            x[ImuOff] = hadImu ? Wrap(m.ImuHeadingRad - x[H]) : 0;
            P[E, E] = P[N, N] = 0.1 * 0.1;
            P[H, H] = Rad(5) * Rad(5);
            P[V, V] = 1;
            P[ImuOff, ImuOff] = Rad(10) * Rad(10);
            P[SteerOff, SteerOff] = Rad(3) * Rad(3);
            P[Slip, Slip] = 0.05 * 0.05;
            lastT = m.T;
            Started = true;
        }

        // the tractor drives on: the kinematic bicycle with the steer angle (offset in it) and the slip sideways
        private void Predict(double dt, Inputs m)
        {
            bool steer = !double.IsNaN(m.SteerRad) && m.WheelbaseM > 0.5;
            double delta = (steer ? m.SteerRad : 0) + x[SteerOff];
            double L = Math.Max(0.5, m.WheelbaseM);
            double v = x[V], h = x[H], s = x[Slip];
            double tanD = Math.Tan(Math.Max(-1.2, Math.Min(1.2, delta)));
            double sinH = Math.Sin(h), cosH = Math.Cos(h);

            x[E] += (v * sinH + s * cosH) * dt;
            x[N] += (v * cosH - s * sinH) * dt;
            // the heading turns as the steering says; without a steer angle as the gyro says, if there is one
            bool gyro = !steer && m.HasYawRate && Math.Abs(m.YawRateRadS) < Rad(60);
            if (steer) x[H] = Wrap(h + v * tanD / L * dt);
            else if (gyro) x[H] = Wrap(h + m.YawRateRadS * dt);

            // (the matrices are kept between fixes: no garbage on every fix)
            double[,] F = bufF, FP = bufFP;
            double[] q = bufQ;
            Array.Clear(F, 0, F.Length);
            Array.Clear(q, 0, q.Length);
            for (int i = 0; i < Count; i++) F[i, i] = 1;
            F[E, H] = (v * cosH - s * sinH) * dt;
            F[E, V] = sinH * dt;
            F[E, Slip] = cosH * dt;
            F[N, H] = (-v * sinH - s * cosH) * dt;
            F[N, V] = cosH * dt;
            F[N, Slip] = -sinH * dt;
            if (steer)
            {
                double c = Math.Cos(Math.Max(-1.2, Math.Min(1.2, delta)));
                F[H, V] = tanD / L * dt;
                F[H, SteerOff] = v / (L * c * c) * dt;
            }

            // what may change meanwhile (without a steer angle the heading may turn freely)
            q[E] = q[N] = 0.01 * 0.01 * dt;
            q[H] = (steer ? Rad(0.5) * Rad(0.5) : gyro ? Rad(1) * Rad(1) : Rad(10) * Rad(10)) * dt;
            q[V] = 0.5 * 0.5 * dt;
            q[ImuOff] = Rad(0.05) * Rad(0.05) * dt;
            q[SteerOff] = Rad(0.02) * Rad(0.02) * dt;
            q[Slip] = 0.03 * 0.03 * dt;

            for (int i = 0; i < Count; i++)
                for (int j = 0; j < Count; j++)
                {
                    double sum = 0;
                    for (int k = 0; k < Count; k++) sum += F[i, k] * P[k, j];
                    FP[i, j] = sum;
                }
            for (int i = 0; i < Count; i++)
                for (int j = 0; j < Count; j++)
                {
                    double sum = 0;
                    for (int k = 0; k < Count; k++) sum += FP[i, k] * F[j, k];
                    P[i, j] = sum + (i == j ? q[i] : 0);
                }
        }

        private readonly double[,] bufF = new double[Count, Count], bufFP = new double[Count, Count];
        private readonly double[] bufQ = new double[Count], bufH = new double[Count], bufPh = new double[Count];

        private void Update(int index, double residual, double r, bool gate = true)
        {
            double[] h = bufH;
            Array.Clear(h, 0, h.Length);
            h[index] = 1;
            UpdateVector(h, residual, r, gate);
        }

        // one scalar measurement: residual = measured - predicted, h its sensitivity to the state
        private void UpdateVector(double[] h, double residual, double r, bool gate = true)
        {
            double[] Ph = bufPh;
            for (int i = 0; i < Count; i++)
            {
                double sum = 0;
                for (int k = 0; k < Count; k++) sum += P[i, k] * h[k];
                Ph[i] = sum;
            }
            double s = r;
            for (int i = 0; i < Count; i++) s += h[i] * Ph[i];
            if (s <= 0) return;
            // an outlier (a jump of the GNSS, a bad IMU reading) is not taken in
            if (gate && residual * residual > 25 * s) return;
            for (int i = 0; i < Count; i++) x[i] += Ph[i] / s * residual;
            x[H] = Wrap(x[H]);
            x[ImuOff] = Wrap(x[ImuOff]);
            for (int i = 0; i < Count; i++)
                for (int j = 0; j < Count; j++)
                    P[i, j] -= Ph[i] * Ph[j] / s;
            // kept symmetric and sane
            for (int i = 0; i < Count; i++)
            {
                if (P[i, i] < 1e-12) P[i, i] = 1e-12;
                for (int j = i + 1; j < Count; j++) P[i, j] = P[j, i] = 0.5 * (P[i, j] + P[j, i]);
            }
        }

        private static double Rad(double deg) => deg * Math.PI / 180;

        public static double Wrap(double a)
        {
            while (a > Math.PI) a -= 2 * Math.PI;
            while (a < -Math.PI) a += 2 * Math.PI;
            return a;
        }
    }
}
