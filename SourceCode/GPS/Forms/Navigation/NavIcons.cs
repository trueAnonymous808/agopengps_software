using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace AgOpenGPS.Forms.Navigation
{
    // The icons of the navigation menu, drawn in code so they follow the day / night theme (light lines at night,
    // dark lines by day) with the same orange and green accents as the smart features' icons. Drawn on a 32 grid.
    internal static class NavIcons
    {
        private static readonly Color Orange = Color.FromArgb(255, 140, 0);
        private static readonly Color Green = Color.FromArgb(76, 175, 80);
        private static readonly Color Blue = Color.FromArgb(30, 136, 229);
        private static readonly Color Red = Color.FromArgb(229, 57, 53);

        private static bool Night => AgLibrary.Localization.UiTheme.IsNight();
        private static Color Ink => Night ? Color.FromArgb(225, 230, 237) : Color.FromArgb(55, 60, 70);
        private static Color Soft => Night ? Color.FromArgb(140, 148, 160) : Color.FromArgb(150, 158, 170);

        private static Bitmap Canvas(int size, out Graphics g)
        {
            var bmp = new Bitmap(size, size);
            g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            float s = size / 32f;
            g.ScaleTransform(s, s);
            return bmp;
        }

        private static Pen P(Color c, float w, DashStyle dash = DashStyle.Solid)
        {
            return new Pen(c, w) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round, DashStyle = dash };
        }

        // a map pin: a round head and a point below it, with a light dot
        private static void Pin(Graphics g, float cx, float cy, float r, Color color)
        {
            using (var path = new GraphicsPath())
            using (var fill = new SolidBrush(color))
            using (var dot = new SolidBrush(Color.White))
            {
                path.AddArc(cx - r, cy - r, 2 * r, 2 * r, 150, 240);
                path.AddLine(cx + r * (float)Math.Cos(Math.PI / 6), cy + r * 0.5f, cx, cy + 2.1f * r);
                path.CloseFigure();
                g.FillPath(fill, path);
                g.FillEllipse(dot, cx - r * 0.38f, cy - r * 0.38f, r * 0.76f, r * 0.76f);
            }
        }

        /// <summary>A saved field: a striped field with a pin on it.</summary>
        public static Bitmap SavedField(int size = 32)
        {
            Bitmap bmp = Canvas(size, out Graphics g);
            using (g)
            using (var fill = new SolidBrush(Color.FromArgb(90, Green)))
            using (var edge = P(Green, 2f))
            using (var rows = P(Color.FromArgb(Night ? 200 : 160, Green), 1.2f))
            {
                var field = new[] { new PointF(2, 29), new PointF(8, 13), new PointF(30, 13), new PointF(25, 29) };
                g.FillPolygon(fill, field);
                for (int i = 1; i < 5; i++)
                {
                    float k = i / 5f;   // from the near edge (y 29) to the far one (y 13)
                    g.DrawLine(rows, 2 + 6 * k, 29 - 16 * k, 25 + 5 * k, 29 - 16 * k);
                }
                g.DrawPolygon(edge, field);
                Pin(g, 17, 6.5f, 5.5f, Orange);
            }
            return bmp;
        }

        /// <summary>An address: a house with an orange door.</summary>
        public static Bitmap Address(int size = 32)
        {
            Bitmap bmp = Canvas(size, out Graphics g);
            using (g)
            using (var line = P(Ink, 2.4f))
            using (var door = new SolidBrush(Orange))
            using (var window = new SolidBrush(Blue))
            {
                g.DrawLines(line, new[] { new PointF(3, 16), new PointF(16, 4), new PointF(29, 16) });
                g.DrawLines(line, new[] { new PointF(7, 13), new PointF(7, 29), new PointF(25, 29), new PointF(25, 13) });
                g.FillRectangle(door, 13.5f, 19, 6, 10);
                g.FillRectangle(window, 9.5f, 16, 3.2f, 3.2f);
                g.FillRectangle(window, 20.5f, 16, 3.2f, 3.2f);
            }
            return bmp;
        }

        /// <summary>A point on the map: a folded map with a pin.</summary>
        public static Bitmap PointOnMap(int size = 32)
        {
            Bitmap bmp = Canvas(size, out Graphics g);
            using (g)
            using (var line = P(Ink, 2f))
            using (var fold = P(Soft, 1.4f))
            using (var paper = new SolidBrush(Color.FromArgb(Night ? 50 : 35, Blue)))
            {
                var map = new[]
                {
                    new PointF(2, 9), new PointF(11, 6), new PointF(21, 9), new PointF(30, 6),
                    new PointF(30, 27), new PointF(21, 30), new PointF(11, 27), new PointF(2, 30)
                };
                g.FillPolygon(paper, map);
                g.DrawLine(fold, 11, 6, 11, 27);
                g.DrawLine(fold, 21, 9, 21, 30);
                g.DrawPolygon(line, map);
                Pin(g, 16, 13, 5.5f, Orange);
            }
            return bmp;
        }

        /// <summary>The street map: a block of streets and a main road.</summary>
        public static Bitmap StreetMap(int size = 32)
        {
            Bitmap bmp = Canvas(size, out Graphics g);
            using (g)
            using (var paper = new SolidBrush(Color.FromArgb(Night ? 45 : 30, Ink)))
            using (var street = P(Ink, 1.8f))
            using (var main = P(Orange, 3.4f))
            using (var park = new SolidBrush(Color.FromArgb(150, Green)))
            {
                g.FillRectangle(paper, 2, 2, 28, 28);
                g.FillEllipse(park, 19, 4, 9, 8);
                g.DrawLine(street, 2, 15, 30, 12);
                g.DrawLine(street, 11, 2, 13, 30);
                g.DrawLine(street, 22, 13, 24, 30);
                g.DrawBezier(main, 2, 26, 12, 20, 18, 28, 30, 20);
            }
            return bmp;
        }

        /// <summary>The satellite picture: a satellite with its panels and a signal.</summary>
        public static Bitmap Satellite(int size = 32)
        {
            Bitmap bmp = Canvas(size, out Graphics g);
            using (g)
            using (var body = new SolidBrush(Ink))
            using (var panel = new SolidBrush(Blue))
            using (var cell = P(Color.FromArgb(160, 255, 255, 255), 0.8f))
            using (var signal = P(Orange, 2f))
            {
                GraphicsState st = g.Save();
                g.TranslateTransform(20, 11);
                g.RotateTransform(-45);
                using (var arm = P(Ink, 1.4f)) g.DrawLine(arm, -6, 0, 6, 0);
                g.FillRectangle(body, -3, -3.5f, 6, 7);
                g.FillRectangle(panel, -13, -6, 7, 12);
                g.FillRectangle(panel, 6, -6, 7, 12);
                g.DrawLine(cell, -13, 0, -6, 0);
                g.DrawLine(cell, 6, 0, 13, 0);
                g.DrawLine(cell, -9.5f, -6, -9.5f, 6);
                g.DrawLine(cell, 9.5f, -6, 9.5f, 6);
                g.Restore(st);
                g.DrawArc(signal, -1, 23, 10, 10, 270, 90);
                g.DrawArc(signal, -5, 19, 18, 18, 270, 90);
            }
            return bmp;
        }

        /// <summary>Stop: a red octagon with a white square.</summary>
        public static Bitmap Stop(int size = 32)
        {
            Bitmap bmp = Canvas(size, out Graphics g);
            using (g)
            using (var fill = new SolidBrush(Red))
            using (var mark = new SolidBrush(Color.White))
            {
                var pts = new PointF[8];
                for (int i = 0; i < 8; i++)
                {
                    double a = Math.PI / 8 + i * Math.PI / 4;
                    pts[i] = new PointF(16 + 14.5f * (float)Math.Cos(a), 16 + 14.5f * (float)Math.Sin(a));
                }
                g.FillPolygon(fill, pts);
                g.FillRectangle(mark, 11, 11, 10, 10);
            }
            return bmp;
        }

        /// <summary>Download the road map: an arrow down onto a road.</summary>
        public static Bitmap Download(int size = 32)
        {
            Bitmap bmp = Canvas(size, out Graphics g);
            using (g)
            using (var arrow = P(Green, 3f))
            using (var tray = P(Ink, 2.4f))
            {
                g.DrawLine(arrow, 16, 3, 16, 19);
                g.DrawLines(arrow, new[] { new PointF(9, 13), new PointF(16, 20), new PointF(23, 13) });
                g.DrawLines(tray, new[] { new PointF(4, 22), new PointF(4, 29), new PointF(28, 29), new PointF(28, 22) });
            }
            return bmp;
        }

        /// <summary>The road map: a road running into the distance.</summary>
        public static Bitmap Road(int size = 32)
        {
            Bitmap bmp = Canvas(size, out Graphics g);
            using (g)
            using (var asphalt = new SolidBrush(Color.FromArgb(Night ? 70 : 50, Ink)))
            using (var side = P(Ink, 2.2f))
            using (var middle = P(Orange, 2f))
            {
                var road = new[] { new PointF(4, 30), new PointF(13, 3), new PointF(19, 3), new PointF(28, 30) };
                g.FillPolygon(asphalt, road);
                g.DrawLine(side, 4, 30, 13, 3);
                g.DrawLine(side, 28, 30, 19, 3);
                g.DrawLine(middle, 16, 5, 16, 9);
                g.DrawLine(middle, 16, 14, 16, 19);
                g.DrawLine(middle, 16, 24, 16, 30);
            }
            return bmp;
        }

        /// <summary>Autosteer: a steering wheel with an orange hub.</summary>
        public static Bitmap SteeringWheel(int size = 32)
        {
            Bitmap bmp = Canvas(size, out Graphics g);
            using (g)
            using (var rim = P(Ink, 2.8f))
            using (var spoke = P(Ink, 2.4f))
            using (var hub = new SolidBrush(Orange))
            {
                g.DrawEllipse(rim, 3, 3, 26, 26);
                g.DrawLine(spoke, 12.5f, 16, 4, 14.5f);
                g.DrawLine(spoke, 19.5f, 16, 28, 14.5f);
                g.DrawLine(spoke, 16, 19.5f, 16, 28);
                g.FillEllipse(hub, 11.5f, 11.5f, 9, 9);
            }
            return bmp;
        }

        /// <summary>Offset from the middle of the road: a dashed middle line and arrows to both sides.</summary>
        public static Bitmap Offset(int size = 32)
        {
            Bitmap bmp = Canvas(size, out Graphics g);
            using (g)
            using (var middle = P(Soft, 2f, DashStyle.Dash))
            using (var arrow = P(Orange, 2.6f))
            using (var side = P(Ink, 2.2f))
            {
                g.DrawLine(side, 2, 3, 2, 29);
                g.DrawLine(side, 30, 3, 30, 29);
                g.DrawLine(middle, 16, 3, 16, 29);
                g.DrawLine(arrow, 13, 16, 6, 16);
                g.DrawLines(arrow, new[] { new PointF(9.5f, 12), new PointF(5.5f, 16), new PointF(9.5f, 20) });
                g.DrawLine(arrow, 19, 16, 26, 16);
                g.DrawLines(arrow, new[] { new PointF(22.5f, 12), new PointF(26.5f, 16), new PointF(22.5f, 20) });
            }
            return bmp;
        }
    }
}
