using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace AgOpenGPS
{
    /// <summary>
    /// What the RTK bridge has shown it can do, per method (dead reckoning with a dual antenna, an IMU or the steer
    /// angle; later TDCP): the error after 30, 60, 120 and 300 s - from virtual outages run in the background while
    /// RTK was good (the answer is known all along) and from real outages (the error when RTK came back). From these
    /// the bridge takes its error growth instead of a guess, and how long it can be trusted.
    /// </summary>
    public class CBridgeExams
    {
        public static readonly int[] Checkpoints = { 30, 60, 120, 300 };
        public const int MinExams = 20, MinPerCheckpoint = 10, MaxPerCheckpoint = 300;

        private readonly Dictionary<string, List<double>[]> results = new Dictionary<string, List<double>[]>();
        private readonly object sync = new object();

        private List<double>[] Of(string method)
        {
            List<double>[] r;
            if (!results.TryGetValue(method, out r))
            {
                r = new List<double>[Checkpoints.Length];
                for (int i = 0; i < r.Length; i++) r[i] = new List<double>();
                results[method] = r;
            }
            return r;
        }

        /// <summary>One checkpoint of a virtual outage: the error (cm) after Checkpoints[index] seconds.</summary>
        public void Add(string method, int index, double errorCm)
        {
            if (index < 0 || index >= Checkpoints.Length || double.IsNaN(errorCm) || double.IsInfinity(errorCm) || errorCm < 0) return;
            lock (sync)
            {
                List<double> list = Of(method)[index];
                list.Add(errorCm);
                if (list.Count > MaxPerCheckpoint) list.RemoveAt(0);
            }
        }

        /// <summary>A real outage: the error when RTK came back, filed under the last checkpoint it reached.</summary>
        public void AddReal(string method, double seconds, double errorCm)
        {
            int index = -1;
            for (int i = 0; i < Checkpoints.Length; i++) if (seconds >= Checkpoints[i]) index = i;
            Add(method, index, errorCm);
        }

        /// <summary>How many exams: the results at the first checkpoint.</summary>
        public int Count(string method)
        {
            lock (sync) return results.TryGetValue(method, out var r) ? r[0].Count : 0;
        }

        /// <summary>The error (cm) 95 % of the exams stayed under at a checkpoint; NaN with too few.</summary>
        public double P95(string method, int index)
        {
            lock (sync)
            {
                if (!results.TryGetValue(method, out var r) || r[index].Count < MinPerCheckpoint) return double.NaN;
                var sorted = new List<double>(r[index]);
                sorted.Sort();
                return sorted[(int)Math.Ceiling(0.95 * sorted.Count) - 1];
            }
        }

        /// <summary>
        /// The error growth (cm/s) the exams show: the steepest of the checkpoints' 95 % errors over their time, so the
        /// estimate never runs below what was seen. NaN until there are enough exams.
        /// </summary>
        public double RateCmPerS(string method)
        {
            if (Count(method) < MinExams) return double.NaN;
            double rate = double.NaN;
            for (int i = 0; i < Checkpoints.Length; i++)
            {
                double p = P95(method, i);
                if (double.IsNaN(p)) continue;
                double r = Math.Max(0, p - 1) / Checkpoints[i];
                rate = double.IsNaN(rate) ? r : Math.Max(rate, r);
            }
            return rate;
        }

        /// <summary>The longest checkpoint (s) whose 95 % error stays within the limit; 0 when none does or too few exams.</summary>
        public int TrustedSeconds(string method, double maxCm)
        {
            if (Count(method) < MinExams) return 0;
            int trusted = 0;
            for (int i = 0; i < Checkpoints.Length; i++)
            {
                double p = P95(method, i);
                if (double.IsNaN(p) || p > maxCm) break;
                trusted = Checkpoints[i];
            }
            return trusted;
        }

        /// <summary>The methods with results, for the window.</summary>
        public List<string> Methods()
        {
            lock (sync) return new List<string>(results.Keys);
        }

        public void Save(string path)
        {
            lock (sync)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                using (var w = new StreamWriter(path))
                {
                    foreach (var kv in results)
                        for (int i = 0; i < Checkpoints.Length; i++)
                            foreach (double e in kv.Value[i])
                                w.WriteLine(kv.Key + ";" + i + ";" + e.ToString("0.###", CultureInfo.InvariantCulture));
                }
            }
        }

        /// <summary>Reads a saved file; a missing or broken file leaves the exams empty.</summary>
        public void Load(string path)
        {
            lock (sync)
            {
                results.Clear();
                if (!File.Exists(path)) return;
                try
                {
                    foreach (string line in File.ReadAllLines(path))
                    {
                        string[] p = line.Split(';');
                        if (p.Length != 3) continue;
                        if (!int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int i)) continue;
                        if (!double.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double e)) continue;
                        if (i < 0 || i >= Checkpoints.Length || p[0].Length == 0) continue;
                        Of(p[0])[i].Add(e);
                    }
                }
                catch (IOException) { results.Clear(); }
                catch (UnauthorizedAccessException) { results.Clear(); }
            }
        }
    }
}
