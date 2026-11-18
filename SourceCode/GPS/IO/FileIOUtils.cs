using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using AgOpenGPS.Core.Models;

namespace AgOpenGPS.IO
{
    public static class FileIoUtils
    {
        // ---- Formatting helper ----
        public static string FormatDouble(double value, int decimals)
        {
            return Math.Round(value, decimals).ToString(CultureInfo.InvariantCulture);
        }

        // ---- Parsing helpers ----

        public static int ParseIntSafe(string line)
        {
            int v;
            if (!string.IsNullOrWhiteSpace(line) &&
                int.TryParse(line.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v))
            {
                return v;
            }
            return 0;
        }

        public static List<vec3> ReadVec3Block(StreamReader r, int count)
        {
            var list = new List<vec3>(count > 0 ? count : 0);
            for (int i = 0; i < count && !r.EndOfStream; i++)
            {
                var words = (r.ReadLine() ?? string.Empty).Split(',');
                if (words.Length < 3) continue;

                double e, n, h;
                if (double.TryParse(words[0], NumberStyles.Float, CultureInfo.InvariantCulture, out e) &&
                    double.TryParse(words[1], NumberStyles.Float, CultureInfo.InvariantCulture, out n) &&
                    double.TryParse(words[2], NumberStyles.Float, CultureInfo.InvariantCulture, out h))
                {
                    list.Add(new vec3(e, n, h));
                }
            }
            return list;
        }
    }

    /// <summary>
    /// A StreamWriter for files that are rewritten completely (boundary, tracks, flags...). It writes to a
    /// temporary file next to the target and swaps it in when disposed, so a power cut or crash in the middle of
    /// a save leaves the previous file intact instead of a truncated one.
    /// </summary>
    public sealed class AtomicStreamWriter : StreamWriter
    {
        private readonly string target;
        private readonly string temp;
        private bool swapped;

        private AtomicStreamWriter(string target, string temp) : base(temp, false)
        {
            this.target = target;
            this.temp = temp;
        }

        public static AtomicStreamWriter Create(string target)
        {
            return new AtomicStreamWriter(target, target + ".tmp");
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (!disposing || swapped) return;
            swapped = true;

            try
            {
                if (File.Exists(target)) File.Replace(temp, target, null);
                else File.Move(temp, target);
            }
            catch (IOException)
            {
                // e.g. a virus scanner holding the file: fall back to a plain overwrite
                File.Copy(temp, target, true);
                File.Delete(temp);
            }
        }
    }
}
