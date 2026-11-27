using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace IconForge
{
    // Writes the new pictures over the old ones: same file, same size. It finds each picture's file through the GPS
    // project's Properties/Resources.resx; resources without a new design are left alone.
    //   IconForge <project folder> [preview.png] [--dry] [--only=prefix]
    // (GPS for the main screen; --only limits it to the resources whose names start with the prefix)
    // (a file of "resource name|png path" lines works in place of the folder too)
    internal static class Program
    {
        private static int Main(string[] args)
        {
            if (args.Length < 1)
            {
                Console.WriteLine("IconForge <GPS project folder> [preview.png] [--dry]");
                return 1;
            }
            bool dry = Array.IndexOf(args, "--dry") >= 0;
            string only = null;
            foreach (string a in args) if (a.StartsWith("--only=")) only = a.Substring(7);
            string preview = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : null;

            var made = new List<KeyValuePair<string, Bitmap>>();
            foreach (string line in Directory.Exists(args[0]) ? FromResx(args[0]) : File.ReadAllLines(args[0]))
            {
                string[] parts = line.Split('|');
                if (parts.Length < 2 || !Icons.All.TryGetValue(parts[0], out Design design)) continue;
                if (only != null && !parts[0].StartsWith(only, StringComparison.Ordinal)) continue;
                string path = parts[1].Trim();
                Size size;
                using (var old = Image.FromFile(path)) size = old.Size;

                Bitmap bmp = Render(design, size.Width, size.Height);
                if (!dry)
                {
                    using (var ms = new MemoryStream())
                    {
                        bmp.Save(ms, ImageFormat.Png);
                        File.WriteAllBytes(path, ms.ToArray());
                    }
                }
                made.Add(new KeyValuePair<string, Bitmap>(parts[0], bmp));
                Console.WriteLine((dry ? "would write " : "wrote ") + parts[0] + " " + size.Width + "x" + size.Height);
            }
            if (preview != null) Preview(made, preview);
            Console.WriteLine(made.Count + " pictures");
            return 0;
        }

        // "name|path" for every picture file the resources point to
        private static IEnumerable<string> FromResx(string gpsFolder)
        {
            string resx = Path.Combine(gpsFolder, "Properties", "Resources.resx");
            string text = File.ReadAllText(resx);
            var data = new System.Text.RegularExpressions.Regex(
                "<data name=\"([^\"]+)\" type=\"System.Resources.ResXFileRef[^\"]*\"[^>]*>\\s*<value>([^<;]+);");
            foreach (System.Text.RegularExpressions.Match m in data.Matches(text))
            {
                string path = Path.GetFullPath(Path.Combine(gpsFolder, "Properties", m.Groups[2].Value));
                if (path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
                    yield return m.Groups[1].Value + "|" + path;
            }
        }

        // the design on its own grid, scaled to fit the picture's size and centred in it
        private static Bitmap Render(Design d, int w, int h)
        {
            var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                float s = Math.Min(w / d.W, h / d.H);
                g.TranslateTransform((w - d.W * s) / 2, (h - d.H * s) / 2);
                g.ScaleTransform(s, s);
                d.Draw(g);
            }
            return bmp;
        }

        // every new picture on a dark and on a light bar, with its name
        private static void Preview(List<KeyValuePair<string, Bitmap>> made, string path)
        {
            const int cell = 110, cols = 10;
            int rows = (made.Count + cols - 1) / cols;
            using (var sheet = new Bitmap(cols * cell, rows * cell * 2))
            using (Graphics g = Graphics.FromImage(sheet))
            using (var font = new Font("Segoe UI", 7f))
            using (var dark = new SolidBrush(Color.FromArgb(43, 46, 52)))
            using (var light = new SolidBrush(Color.FromArgb(236, 239, 243)))
            {
                for (int r = 0; r < rows; r++)
                {
                    g.FillRectangle(dark, 0, r * cell * 2, cols * cell, cell);
                    g.FillRectangle(light, 0, r * cell * 2 + cell, cols * cell, cell);
                }
                for (int i = 0; i < made.Count; i++)
                {
                    int x = (i % cols) * cell, y = (i / cols) * cell * 2;
                    Bitmap b = made[i].Value;
                    float s = Math.Min(1f, Math.Min(90f / b.Width, 90f / b.Height));
                    float bw = b.Width * s, bh = b.Height * s;
                    g.DrawImage(b, x + (cell - bw) / 2, y + 4 + (90 - bh) / 2, bw, bh);
                    g.DrawImage(b, x + (cell - bw) / 2, y + cell + 4 + (90 - bh) / 2, bw, bh);
                    g.DrawString(made[i].Key, font, Brushes.White, x + 2, y + 96);
                    g.DrawString(made[i].Key, font, Brushes.Black, x + 2, y + cell + 96);
                }
                sheet.Save(path, ImageFormat.Png);
            }
        }
    }
}
