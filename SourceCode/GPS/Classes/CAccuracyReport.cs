using System;
using System.Collections.Generic;
using System.Linq;

namespace AgOpenGPS
{
    /// <summary>
    /// How accurate the driving really was, from the passes' tracks: on the line (the distance off it while autosteer
    /// held it) and pass to pass (how far a pass really ran from the neighbouring one, against the working width - the
    /// figure the makers of the big systems give: an overlap or a gap between two passes is what the farmer sees).
    /// </summary>
    public static class CAccuracyReport
    {
        public const int MinPairPoints = 15;       // a pair of neighbouring passes: at least this many points side by side
        public const double MinPairShare = 0.4;    // and this share of the later pass's points

        public sealed class Pair
        {
            public PassResult A, B;
            public double MeanCm, Abs95Cm, MaxCm;  // the distance between the two against the working width
            public int Points;
        }

        public sealed class Result
        {
            public int Passes;
            public double LengthM;
            public double LineRmsCm, Line95Cm, LineMaxCm;          // on the line
            public double P2PMeanCm, P2PStdCm, P2P95Cm, P2PMaxCm;  // pass to pass (NaN: no neighbours found)
            public int P2PPoints;
            public List<Pair> Pairs = new List<Pair>();
            public Dictionary<string, (double RmsCm, double Abs95Cm, int Passes, double LengthM)> ByMethod =
                new Dictionary<string, (double, double, int, double)>();
        }

        public static Result Analyze(IList<PassResult> passes, double swathM)
        {
            var r = new Result();
            var withTrack = passes.Where(p => p != null && p.Track != null && p.Track.Count >= 2).ToList();
            r.Passes = withTrack.Count;
            r.LengthM = withTrack.Sum(p => p.LengthM);

            // ---- on the line ----
            var offs = withTrack.SelectMany(p => p.Track.Select(x => Math.Abs(x[2]) * 100)).ToList();
            if (offs.Count > 0)
            {
                r.LineRmsCm = Math.Sqrt(offs.Average(x => x * x));
                r.Line95Cm = Percentile(offs, 0.95);
                r.LineMaxCm = offs.Max();
            }
            foreach (var g in withTrack.GroupBy(p => p.Method))
            {
                var o = g.SelectMany(p => p.Track.Select(x => Math.Abs(x[2]) * 100)).ToList();
                r.ByMethod[g.Key] = (Math.Sqrt(o.Average(x => x * x)), Percentile(o, 0.95), g.Count(), g.Sum(p => p.LengthM));
            }

            // ---- pass to pass: each pass against the earlier ones that ran beside it ----
            var errs = new List<double>();
            for (int j = 1; j < withTrack.Count; j++)
            {
                PassResult b = withTrack[j];
                for (int i = Math.Max(0, j - 30); i < j; i++)
                {
                    PassResult a = withTrack[i];
                    if (!BoxesNear(a.Track, b.Track, swathM * 1.6)) continue;
                    var e = new List<double>();
                    foreach (double[] q in b.Track)
                    {
                        double d = Distance(a.Track, q[0], q[1], out bool inside);
                        if (inside && d > swathM * 0.5 && d < swathM * 1.5) e.Add((d - swathM) * 100);
                    }
                    if (e.Count < MinPairPoints || e.Count < MinPairShare * b.Track.Count) continue;
                    var abs = e.Select(Math.Abs).ToList();
                    r.Pairs.Add(new Pair { A = a, B = b, MeanCm = e.Average(), Abs95Cm = Percentile(abs, 0.95), MaxCm = abs.Max(), Points = e.Count });
                    errs.AddRange(e);
                }
            }
            r.P2PPoints = errs.Count;
            if (errs.Count > 0)
            {
                r.P2PMeanCm = errs.Average();
                r.P2PStdCm = Math.Sqrt(errs.Average(x => (x - r.P2PMeanCm) * (x - r.P2PMeanCm)));
                var abs = errs.Select(Math.Abs).ToList();
                r.P2P95Cm = Percentile(abs, 0.95);
                r.P2PMaxCm = abs.Max();
            }
            else r.P2PMeanCm = r.P2PStdCm = r.P2P95Cm = r.P2PMaxCm = double.NaN;
            return r;
        }

        // the distance of a point from a track; inside: it falls beside the track, not beyond its ends
        private static double Distance(List<double[]> track, double e, double n, out bool inside)
        {
            double best = double.MaxValue;
            int bestI = -1;
            double bestU = 0;
            for (int i = 0; i + 1 < track.Count; i++)
            {
                double ax = track[i][0], ay = track[i][1], dx = track[i + 1][0] - ax, dy = track[i + 1][1] - ay;
                double len2 = dx * dx + dy * dy;
                double u = len2 > 1e-9 ? ((e - ax) * dx + (n - ay) * dy) / len2 : 0;
                double uc = Math.Max(0, Math.Min(1, u));
                double px = ax + uc * dx - e, py = ay + uc * dy - n;
                double d2 = px * px + py * py;
                if (d2 < best) { best = d2; bestI = i; bestU = u; }
            }
            inside = bestI >= 0 && !(bestI == 0 && bestU < 0) && !(bestI == track.Count - 2 && bestU > 1);
            return Math.Sqrt(best);
        }

        private static bool BoxesNear(List<double[]> a, List<double[]> b, double margin)
        {
            double aMinE = a.Min(x => x[0]) - margin, aMaxE = a.Max(x => x[0]) + margin, aMinN = a.Min(x => x[1]) - margin, aMaxN = a.Max(x => x[1]) + margin;
            double bMinE = b.Min(x => x[0]), bMaxE = b.Max(x => x[0]), bMinN = b.Min(x => x[1]), bMaxN = b.Max(x => x[1]);
            return bMaxE >= aMinE && bMinE <= aMaxE && bMaxN >= aMinN && bMinN <= aMaxN;
        }

        public static double Percentile(List<double> values, double q)
        {
            if (values.Count == 0) return double.NaN;
            var s = values.OrderBy(x => x).ToList();
            int i = (int)Math.Min(s.Count - 1, Math.Max(0, Math.Ceiling(q * s.Count) - 1));
            return s[i];
        }
    }
}
