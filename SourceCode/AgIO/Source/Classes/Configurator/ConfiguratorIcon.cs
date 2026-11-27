using System.Drawing;
using System.Drawing.Drawing2D;

namespace AgIO.Configurator
{
    /// <summary>The module configurator's picture, drawn flat as AgIO's other menu icons: a circuit board with its
    /// processor, and the GNSS signal it takes coming in at its corner.</summary>
    public static class ConfiguratorIcon
    {
        public static Bitmap Draw(int size = 64)
        {
            var bmp = new Bitmap(size, size);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                float s = size / 64f;
                g.ScaleTransform(s, s);

                // the board: most of the picture, a little down and left to leave the corner for the signal
                var board = new RectangleF(3, 15, 46, 46);
                using (var fill = new SolidBrush(Color.FromArgb(56, 142, 60)))
                using (var edge = new Pen(Color.FromArgb(27, 94, 32), 2.5f))
                using (GraphicsPath p = Rounded(board, 6))
                {
                    g.FillPath(fill, p);
                    g.DrawPath(edge, p);
                }

                // the processor in the middle of the board, three pins on each side
                var chip = new RectangleF(15, 27, 22, 22);
                using (var pin = new SolidBrush(Color.FromArgb(255, 202, 40)))
                {
                    for (int i = 0; i < 3; i++)
                    {
                        float o = 19.5f + i * 6;
                        g.FillRectangle(pin, o, 22, 2.8f, 5);           // top
                        g.FillRectangle(pin, o, 49, 2.8f, 5);           // bottom
                        g.FillRectangle(pin, 10, o + 12, 5, 2.8f);      // left
                        g.FillRectangle(pin, 37, o + 12, 5, 2.8f);      // right
                    }
                }
                using (var body = new SolidBrush(Color.FromArgb(38, 50, 56)))
                using (GraphicsPath p = Rounded(chip, 2.5f))
                    g.FillPath(body, p);
                using (var mark = new SolidBrush(Color.FromArgb(120, 144, 156)))
                    g.FillEllipse(mark, 18, 30, 3.5f, 3.5f);

                // the GNSS signal coming in at the board's corner
                var corner = new PointF(46, 18);
                using (var wave = new Pen(Color.FromArgb(255, 140, 0), 4f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                {
                    g.DrawArc(wave, corner.X - 9, corner.Y - 9, 18, 18, 280, 80);
                    g.DrawArc(wave, corner.X - 17, corner.Y - 17, 34, 34, 280, 80);
                }
                using (var dot = new SolidBrush(Color.FromArgb(255, 140, 0)))
                    g.FillEllipse(dot, corner.X - 4, corner.Y - 4, 8, 8);
            }
            return bmp;
        }

        private static GraphicsPath Rounded(RectangleF r, float radius)
        {
            var p = new GraphicsPath();
            float d = radius * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }
}
