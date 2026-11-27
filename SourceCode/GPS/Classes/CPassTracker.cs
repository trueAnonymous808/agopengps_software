using System;
using System.Collections.Generic;

namespace AgOpenGPS
{
    /// <summary>One pass along a line: how far off it the tractor ran, with which steering method.</summary>
    public sealed class PassResult
    {
        public DateTime When;
        public string Method;
        public double RmsCm, MaxCm, LengthM;

        /// <summary>Where the tractor really went (east, north, distance off the line), a point every metre or so -
        /// for the accuracy report: how far the neighbouring passes really were from each other.</summary>
        public List<double[]> Track = new List<double[]>();
    }

    /// <summary>
    /// Cuts the driving into passes - one line, one direction, one steering method, autosteer holding it - and tells
    /// how each went: the distance off the line (only where it counts: RTK fix, at work) and the length.
    /// </summary>
    public sealed class CPassTracker
    {
        public const double MinLengthM = 30;     // shorter: not a pass worth telling
        public const int MinSamples = 50;
        public const double PauseS = 3;          // off the line longer than this: the pass has ended

        private string key;
        private double sumSq, max, length, lastE, lastN, offSince = double.NaN;
        private int samples;
        private string method;
        private List<double[]> track = new List<double[]>();
        public const double TrackStepM = 1.0;
        public const int MaxTrackPoints = 5000;

        public bool Active => key != null;
        public double LengthM => length;

        public void Reset() => key = null;

        /// <summary>
        /// Every fix. holding: autosteer holds a line now (any fix quality); counts: the distance off it counts now.
        /// The result of a pass that has just ended, or null.
        /// </summary>
        public PassResult Feed(double t, bool holding, string line, string steerMethod, double east, double north, double offLineM, bool counts)
        {
            string now = holding ? line + "|" + steerMethod : null;
            PassResult done = null;
            if (key != null && now != key)
            {
                // another line, direction or method: the pass ends at once; a short break (a gap in the fix) is waited out
                if (now != null)
                {
                    done = Finish();
                    key = null;
                }
                else
                {
                    if (double.IsNaN(offSince)) offSince = t;
                    if (t - offSince > PauseS)
                    {
                        done = Finish();
                        key = null;
                    }
                }
            }
            if (now == null) return done;
            offSince = double.NaN;
            if (key == null)
            {
                key = now;
                method = steerMethod;
                sumSq = max = length = 0;
                samples = 0;
                lastE = east;
                lastN = north;
                track = new List<double[]>();
            }
            double step = Math.Sqrt((east - lastE) * (east - lastE) + (north - lastN) * (north - lastN));
            if (step < 5) length += step;   // (a jump of the position is no driving)
            lastE = east;
            lastN = north;
            if (counts && !double.IsNaN(offLineM) && Math.Abs(offLineM) < 2)
            {
                sumSq += offLineM * offLineM;
                max = Math.Max(max, Math.Abs(offLineM));
                samples++;
                // the way driven, a point every metre (only where it counts: RTK fix, at work)
                double[] last = track.Count > 0 ? track[track.Count - 1] : null;
                if (track.Count < MaxTrackPoints && (last == null || (east - last[0]) * (east - last[0]) + (north - last[1]) * (north - last[1]) >= TrackStepM * TrackStepM))
                    track.Add(new[] { east, north, offLineM });
            }
            return done;
        }

        private PassResult Finish()
        {
            if (length < MinLengthM || samples < MinSamples) return null;
            return new PassResult
            {
                When = DateTime.Now,
                Method = method,
                RmsCm = Math.Sqrt(sumSq / samples) * 100,
                MaxCm = max * 100,
                LengthM = length,
                Track = track
            };
        }

        /// <summary>Each method's passes summed up: the mean distance off the line weighted by length, and how many.</summary>
        public static Dictionary<string, (double RmsCm, int Passes, double LengthM)> Summary(IEnumerable<PassResult> passes)
        {
            var sum = new Dictionary<string, (double SumSq, int N, double L)>();
            foreach (PassResult p in passes)
            {
                sum.TryGetValue(p.Method, out var s);
                sum[p.Method] = (s.SumSq + p.RmsCm * p.RmsCm * p.LengthM, s.N + 1, s.L + p.LengthM);
            }
            var result = new Dictionary<string, (double, int, double)>();
            foreach (var kv in sum) result[kv.Key] = (Math.Sqrt(kv.Value.SumSq / Math.Max(1e-9, kv.Value.L)), kv.Value.N, kv.Value.L);
            return result;
        }
    }
}
