using System;
using System.Collections.Generic;

namespace AgOpenGPS.Tdcp
{
    /// <summary>The receiver's movement between two epochs, east / north / up (metres).</summary>
    public sealed class TdcpResult
    {
        public bool Ok;
        public string Reason = "";
        public double TowS, Dt;
        public double East, North, Up;
        public int Sats, Slips;
        public double RmsM;
    }

    /// <summary>
    /// Time-differenced carrier phase: the change of each satellite's carrier phase from one epoch to the next,
    /// less the change the satellites' own movement and clocks explain, is the receiver's movement along the line of
    /// sight plus its clock change. With five or more satellites that gives the movement to millimetres, without any
    /// correction service. Satellites whose tracking broke (cycle slip) are left out; bad measurements are found by
    /// their residuals and dropped.
    /// </summary>
    public sealed class TdcpSolver
    {
        public double ElevationMaskDeg = 10;
        public double MaxResidualM = 0.03, MaxRmsM = 0.02, MaxGapS = 1.5;
        public int MinSats = 5;

        private readonly Dictionary<long, Ephemeris> ephemerides = new Dictionary<long, Ephemeris>();
        private Dictionary<long, PhaseObs> prev;
        private double prevTow = double.NaN;
        private Ecef rx;
        private double lat, lon;
        private bool hasPosition;

        public int EphemerisCount => ephemerides.Count;

        private static long Key(GnssSystem s, int prn) => ((long)s << 8) | (uint)prn;

        public void SetEphemeris(Ephemeris e)
        {
            if (e == null) return;
            ephemerides[Key(e.System, e.Prn)] = e;
        }

        /// <summary>Where the receiver is, to a few metres (for the directions to the satellites).</summary>
        public void SetPosition(double latDeg, double lonDeg, double heightM)
        {
            if (double.IsNaN(latDeg) || double.IsNaN(lonDeg) || Math.Abs(latDeg) > 90) return;
            lat = latDeg;
            lon = lonDeg;
            rx = GnssOrbit.FromLatLon(latDeg, lonDeg, double.IsNaN(heightM) ? 0 : heightM);
            hasPosition = true;
        }

        /// <summary>One epoch: the movement since the previous one (Ok false when it cannot be told).</summary>
        public TdcpResult Process(ObsEpoch epoch)
        {
            var result = new TdcpResult { TowS = epoch.GpsTowS };
            var now = new Dictionary<long, PhaseObs>();
            foreach (PhaseObs o in epoch.Obs) now[Key(o.System, o.Prn)] = o;

            Dictionary<long, PhaseObs> before = prev;
            double tPrev = prevTow;
            prev = now;
            prevTow = epoch.GpsTowS;

            if (!hasPosition) { result.Reason = "no position"; return result; }
            if (before == null) { result.Reason = "first epoch"; return result; }
            double dt = GnssOrbit.Wrap(epoch.GpsTowS - tPrev);
            result.Dt = dt;
            if (dt <= 0 || dt > MaxGapS) { result.Reason = "gap"; return result; }

            var rows = new List<double[]>();
            var ys = new List<double>();
            var ws = new List<double>();
            foreach (var kv in now)
            {
                if (!before.TryGetValue(kv.Key, out PhaseObs p)) continue;
                PhaseObs o = kv.Value;
                // a break in tracking: the phase may have jumped whole wavelengths
                if (o.LockMs <= 0 || o.LockMs < p.LockMs || o.HalfCycle != p.HalfCycle || o.HalfState != p.HalfState || o.Signal != p.Signal) { result.Slips++; continue; }
                if (!ephemerides.TryGetValue(kv.Key, out Ephemeris e) || !e.Healthy || !GnssOrbit.Usable(e, epoch.GpsTowS)) continue;

                double rPrev = GnssOrbit.Range(e, tPrev, rx, out double clkPrev, out _);
                double rNow = GnssOrbit.Range(e, epoch.GpsTowS, rx, out double clkNow, out Ecef u);
                double el = GnssOrbit.Elevation(u, lat, lon);
                if (el < ElevationMaskDeg * Math.PI / 180) continue;

                double y = (o.PhaseRangeM - p.PhaseRangeM) - (rNow - rPrev) + GnssOrbit.C * (clkNow - clkPrev);
                rows.Add(new[] { -u.X, -u.Y, -u.Z, 1.0 });
                ys.Add(y);
                double s = Math.Sin(el);
                ws.Add(s * s);
            }

            double[] x = null;
            double rms = 0;
            while (rows.Count >= MinSats)
            {
                x = Solve(rows, ys, ws);
                if (x == null) break;
                double worstAbs = Residuals(rows, ys, x, -1, out rms);
                if (worstAbs <= MaxResidualM) break;
                // a slip the lock time missed, multipath: one bad satellite pulls the whole solution, so its own
                // residual need not be the biggest. The one whose removal lets the others agree best goes out.
                x = null;
                if (rows.Count - 1 < MinSats) break;
                int drop = -1;
                double best = double.MaxValue;
                for (int k = 0; k < rows.Count; k++)
                {
                    double[] xk = Solve(rows, ys, ws, k);
                    if (xk == null) continue;
                    double w = Residuals(rows, ys, xk, k, out _);
                    if (w < best) { best = w; drop = k; }
                }
                if (drop < 0) break;
                rows.RemoveAt(drop);
                ys.RemoveAt(drop);
                ws.RemoveAt(drop);
            }

            result.Sats = rows.Count;
            result.RmsM = rms;
            if (x == null) { result.Reason = rows.Count < MinSats ? "too few satellites" : "no solution"; return result; }
            if (rms > MaxRmsM) { result.Reason = "residuals too big"; return result; }

            var d = new Ecef(x[0], x[1], x[2]);
            rx = rx + d;
            GnssOrbit.ToEnu(d, lat, lon, out result.East, out result.North, out result.Up);
            result.Ok = true;
            return result;
        }

        // the largest absolute residual and the RMS, leaving out row 'skip' (-1: none)
        private static double Residuals(List<double[]> h, List<double> y, double[] x, int skip, out double rms)
        {
            double worst = 0, sum = 0;
            int count = 0;
            for (int i = 0; i < h.Count; i++)
            {
                if (i == skip) continue;
                double v = y[i] - (h[i][0] * x[0] + h[i][1] * x[1] + h[i][2] * x[2] + h[i][3] * x[3]);
                sum += v * v;
                count++;
                worst = Math.Max(worst, Math.Abs(v));
            }
            rms = count > 0 ? Math.Sqrt(sum / count) : 0;
            return worst;
        }

        // weighted least squares: (H'WH) x = H'Wy, 4 unknowns (row 'skip' left out, -1: none)
        private static double[] Solve(List<double[]> h, List<double> y, List<double> w, int skip = -1)
        {
            var n = new double[4, 5];
            for (int i = 0; i < h.Count; i++)
            {
                if (i == skip) continue;
                for (int r = 0; r < 4; r++)
                {
                    for (int c = 0; c < 4; c++) n[r, c] += w[i] * h[i][r] * h[i][c];
                    n[r, 4] += w[i] * h[i][r] * y[i];
                }
            }
            for (int col = 0; col < 4; col++)
            {
                int pivot = col;
                for (int r = col + 1; r < 4; r++) if (Math.Abs(n[r, col]) > Math.Abs(n[pivot, col])) pivot = r;
                if (Math.Abs(n[pivot, col]) < 1e-12) return null;
                if (pivot != col)
                    for (int c = 0; c < 5; c++) { double t = n[col, c]; n[col, c] = n[pivot, c]; n[pivot, c] = t; }
                for (int r = 0; r < 4; r++)
                {
                    if (r == col) continue;
                    double f = n[r, col] / n[col, col];
                    for (int c = col; c < 5; c++) n[r, c] -= f * n[col, c];
                }
            }
            var x = new double[4];
            for (int r = 0; r < 4; r++) x[r] = n[r, 4] / n[r, r];
            return x;
        }
    }
}
