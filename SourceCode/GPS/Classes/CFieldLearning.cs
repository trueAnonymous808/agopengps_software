using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace AgOpenGPS
{
    /// <summary>
    /// Learning on the field (iterative learning control): where the tractor ran off the line before - a slope, a rut,
    /// a wet patch - is remembered in 3 m cells for each driving direction, and next time the steering is moved to the
    /// other side in advance by as much. Each pass learns a part of what is left, so a repeating error fades away over
    /// a few passes, while a one-off one hardly moves it.
    /// </summary>
    public sealed class CFieldLearning
    {
        public const double CellM = 3;
        public const int Directions = 8;
        public double Gain = 0.03;         // a fix's share (about a third of the error a pass at work speed)
        public double MaxShiftM = 0.3;

        private readonly Dictionary<long, double[]> cells = new Dictionary<long, double[]>();   // shift (m), fixes learnt

        public int Count => cells.Count;
        public bool Dirty { get; private set; }

        public void Clear()
        {
            cells.Clear();
            Dirty = true;
        }

        private static long Key(double east, double north, double headingRad)
        {
            long x = (long)Math.Floor(east / CellM) & 0xFFFFFF, y = (long)Math.Floor(north / CellM) & 0xFFFFFF;
            double h = headingRad % (2 * Math.PI);
            if (h < 0) h += 2 * Math.PI;
            long d = (long)Math.Round(h / (2 * Math.PI / Directions)) % Directions;
            return (x << 28) | (y << 4) | d;
        }

        /// <summary>The shift (m, + right) learnt for this place and direction; 0 where nothing was learnt.</summary>
        public double ShiftAt(double east, double north, double headingRad)
        {
            return cells.TryGetValue(Key(east, north, headingRad), out double[] c) ? c[0] : 0;
        }

        /// <summary>
        /// One fix on the line: how far the tractor really ran off it (m, + right) with the shift it had. The place's
        /// shift moves towards the error, so next time the steering keeps it on the line.
        /// </summary>
        public void Learn(double east, double north, double headingRad, double offLineM)
        {
            if (double.IsNaN(offLineM) || Math.Abs(offLineM) > 1) return;
            long k = Key(east, north, headingRad);
            if (!cells.TryGetValue(k, out double[] c)) cells[k] = c = new double[2];
            c[0] = Math.Max(-MaxShiftM, Math.Min(MaxShiftM, c[0] + Gain * offLineM));
            c[1]++;
            Dirty = true;
        }

        public void Save(string path)
        {
            var c = CultureInfo.InvariantCulture;
            using (var w = new StreamWriter(path, false))
            {
                w.WriteLine("key;shift;fixes");
                foreach (var kv in cells) w.WriteLine(kv.Key.ToString(c) + ";" + kv.Value[0].ToString("0.0000", c) + ";" + kv.Value[1].ToString("0", c));
            }
            Dirty = false;
        }

        public void Load(string path)
        {
            cells.Clear();
            Dirty = false;
            if (!File.Exists(path)) return;
            var c = CultureInfo.InvariantCulture;
            bool first = true;
            foreach (string line in File.ReadLines(path))
            {
                if (first) { first = false; continue; }
                string[] p = line.Split(';');
                if (p.Length < 3) continue;
                if (long.TryParse(p[0], NumberStyles.Integer, c, out long k) && double.TryParse(p[1], NumberStyles.Float, c, out double s)
                    && double.TryParse(p[2], NumberStyles.Float, c, out double n))
                    cells[k] = new[] { s, n };
            }
        }
    }

    /// <summary>
    /// A trailed implement kept on the line: where the implement runs beside it (on a slope it slides downhill, in a
    /// curve it cuts inside) the tractor is moved the other way, slowly, until the implement is on the line - each
    /// way along the line on its own (downhill is the other side coming back).
    /// </summary>
    public sealed class CImplementKeeper
    {
        public double GainPerS = 0.15;    // of the implement's offset a second
        public double MaxShiftM = 1.0;

        private readonly double[] shift = new double[2];
        private double lastT = double.NaN;

        public double Shift(bool sameWay) => shift[sameWay ? 0 : 1];

        public void Reset()
        {
            shift[0] = shift[1] = 0;
            lastT = double.NaN;
        }

        /// <summary>One fix on the line: the implement's distance off it (m, + right).</summary>
        public void Update(double t, bool sameWay, double implementOffM)
        {
            double dt = double.IsNaN(lastT) ? 0 : Math.Max(0, Math.Min(0.5, t - lastT));
            lastT = t;
            if (double.IsNaN(implementOffM) || Math.Abs(implementOffM) > 3) return;
            int i = sameWay ? 0 : 1;
            shift[i] = Math.Max(-MaxShiftM, Math.Min(MaxShiftM, shift[i] + GainPerS * implementOffM * dt));
        }

        /// <summary>Off the line (a turn, autosteer off): the time base starts again, the shifts are kept.</summary>
        public void Pause() => lastT = double.NaN;
    }
}
