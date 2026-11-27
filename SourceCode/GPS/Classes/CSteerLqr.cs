using System;
using System.Collections.Generic;

namespace AgOpenGPS
{
    /// <summary>
    /// LQR steering: a linear state feedback on the distance off the line, the heading off the line and the angles
    /// already sent but not yet answered (the steering's dead time in the state), with gains from the discrete Riccati
    /// equation for the tractor's wheelbase, speed and dead time - worked out once for each and kept. The path's
    /// curvature where the wheels will answer is fed forward. Signs: off to the right +, heading turned right +, angle + right.
    /// </summary>
    public sealed class CSteerLqr
    {
        public double StepS = 0.1;
        public double WeightError = 1.0;      // per m² off the line
        public double WeightHeading = 0.5;    // per rad² of heading off the line
        public double WeightSteer = 10;       // per rad² of angle asked (more: calmer; below ~5 the turning speed limit makes it swing)

        private readonly Dictionary<string, double[]> gains = new Dictionary<string, double[]>();

        /// <summary>How many steps of dead time the state carries (at least one: the step itself).</summary>
        public int DelaySteps(double deadTimeS) => Math.Max(1, Math.Min(15, (int)Math.Round(deadTimeS / StepS)));

        /// <summary>
        /// The gains [e, psi, d1 .. dn] for this wheelbase, speed and dead time (d1 the angle sent last, dn the one
        /// the wheels answer now). Speed in 0.25 m/s steps so the table stays small.
        /// </summary>
        public double[] Gains(double wheelbaseM, double speedMs, int delaySteps)
        {
            double v = Math.Max(0.5, Math.Round(Math.Abs(speedMs) * 4) / 4);
            string key = wheelbaseM.ToString("0.00") + "|" + v.ToString("0.00") + "|" + delaySteps + "|" +
                WeightError.ToString("R") + "|" + WeightHeading.ToString("R") + "|" + WeightSteer.ToString("R") + "|" + StepS.ToString("R");
            if (gains.TryGetValue(key, out double[] k)) return k;
            k = Solve(wheelbaseM, v, delaySteps);
            if (gains.Count > 400) gains.Clear();
            gains[key] = k;
            return k;
        }

        /// <summary>
        /// The angle to ask for (rad): feedback on the deviations plus the curvature fed forward.
        /// sentRad: the angles sent in the last steps, newest first (their effective angles, offset included).
        /// </summary>
        public double Steer(double offLineM, double headingOffRad, IList<double> sentRad, double speedMs, double wheelbaseM,
            double feedForwardRad, double maxRad) => Steer(offLineM, headingOffRad, sentRad, speedMs, wheelbaseM, i => feedForwardRad, maxRad);

        /// <summary>
        /// The same with the path's angle where each one acts: feedForwardAt(i) is the steer angle the path wants where
        /// the angle sent i steps ago will act (i = 0: the one asked now, after the whole dead time).
        /// </summary>
        public double Steer(double offLineM, double headingOffRad, IList<double> sentRad, double speedMs, double wheelbaseM,
            Func<int, double> feedForwardAt, double maxRad)
        {
            int n = sentRad.Count;
            double[] k = Gains(wheelbaseM, speedMs, n);
            double u = -k[0] * offLineM - k[1] * headingOffRad;
            for (int i = 0; i < n; i++) u -= k[2 + i] * (sentRad[i] - feedForwardAt(i + 1));
            u += feedForwardAt(0);
            // within the sideways grip at this speed (fast, that is tenths of a degree)
            double grip = CSteerMpc.LateralLimitDeg(speedMs, wheelbaseM) * Math.PI / 180;
            double limit = Math.Min(maxRad, grip + Math.Abs(feedForwardAt(0)));
            return Math.Max(-limit, Math.Min(limit, u));
        }

        /// <summary>The path's steer angle (rad) at a distance ahead (over 3 m, from 1 m on).</summary>
        public static double PathAngleAt(IList<vec2> path, double aheadM, double wheelbaseM)
        {
            double at = Math.Max(1, aheadM - 1.5);
            return Math.Atan(wheelbaseM * CPathAhead.MeanCurvature(path, at, at + 3));
        }

        // the discrete Riccati equation by iteration (one input: no matrix inverse)
        private double[] Solve(double L, double v, int n)
        {
            int m = 2 + n;
            double dt = StepS;
            var A = new double[m, m];
            A[0, 0] = 1;
            A[0, 1] = v * dt;                 // e' = e + v psi dt
            A[1, 1] = 1;
            A[1, 1 + n] = v / L * dt;         // psi' = psi + v/L delta dt, delta = the oldest angle sent (dn)
            A[0, 1 + n] = v * v / L * dt * dt / 2;   // and within the step the turn already moves it sideways (fast: metres a step)
            for (int i = 1; i < n; i++) A[2 + i, 1 + i] = 1;   // the angles move on: d(i+1)' = d(i)
            var B = new double[m];
            B[2] = 1;                          // d1' = u
            var Q = new double[m];
            Q[0] = WeightError;
            Q[1] = WeightHeading;

            var P = new double[m, m];
            for (int i = 0; i < m; i++) P[i, i] = Q[i];
            var K = new double[m];
            var AP = new double[m, m];
            var PB = new double[m];
            for (int iter = 0; iter < 3000; iter++)
            {
                // PB = P B, s = R + B'PB, K = (B'PA) / s
                for (int i = 0; i < m; i++)
                {
                    double sum = 0;
                    for (int j = 0; j < m; j++) sum += P[i, j] * B[j];
                    PB[i] = sum;
                }
                // fast, an angle turns the tractor by speed² as much: the weight grows with it above work speed (4 m/s),
                // else it would chase tenths of a degree from side to side
                double s = WeightSteer * Math.Max(1, Math.Pow(v / 4, 4));
                for (int i = 0; i < m; i++) s += B[i] * PB[i];
                var Knew = new double[m];
                for (int j = 0; j < m; j++)
                {
                    double sum = 0;
                    for (int i = 0; i < m; i++) sum += PB[i] * A[i, j];   // (B'P A)_j
                    Knew[j] = sum / s;
                }
                // P = A'P A - A'P B K + Q  =  A'P (A - B K) + Q
                var ABK = new double[m, m];
                for (int i = 0; i < m; i++)
                    for (int j = 0; j < m; j++)
                        ABK[i, j] = A[i, j] - B[i] * Knew[j];
                for (int i = 0; i < m; i++)
                    for (int j = 0; j < m; j++)
                    {
                        double sum = 0;
                        for (int r = 0; r < m; r++) sum += P[i, r] * ABK[r, j];
                        AP[i, j] = sum;   // P (A - BK)
                    }
                double change = 0;
                var Pn = new double[m, m];
                for (int i = 0; i < m; i++)
                    for (int j = 0; j < m; j++)
                    {
                        double sum = 0;
                        for (int r = 0; r < m; r++) sum += A[r, i] * AP[r, j];
                        Pn[i, j] = sum + (i == j ? Q[i] : 0);
                        change = Math.Max(change, Math.Abs(Pn[i, j] - P[i, j]));
                    }
                P = Pn;
                K = Knew;
                if (change < 1e-10 * (1 + Math.Abs(P[0, 0]))) break;
            }
            return K;
        }
    }
}
