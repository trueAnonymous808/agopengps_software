using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace AgOpenGPS
{
    // The small icons of the smart features window, drawn in code so they follow the day / night theme:
    // light lines at night, dark lines by day, orange and green accents as on the steer settings' own icons.
    internal static class SmartIcons
    {
        private static readonly Color Orange = Color.FromArgb(255, 140, 0);
        private static readonly Color Green = Color.FromArgb(76, 175, 80);

        private static bool Night => AgLibrary.Localization.UiTheme.IsNight();
        private static Color Ink => Night ? Color.FromArgb(225, 230, 237) : Color.FromArgb(55, 60, 70);
        private static Color Soft => Night ? Color.FromArgb(140, 148, 160) : Color.FromArgb(150, 158, 170);

        private static Bitmap Canvas(int size, out Graphics g)
        {
            var bmp = new Bitmap(size, size);
            g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float s = size / 32f;
            g.ScaleTransform(s, s);
            return bmp;
        }

        private static Pen P(Color c, float w, DashStyle dash = DashStyle.Solid)
        {
            return new Pen(c, w) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round, DashStyle = dash };
        }

        /// <summary>The Smart WAS picture with its black parts turned light at night (it is drawn for a light button).</summary>
        public static Bitmap SmartWas()
        {
            var src = Properties.Resources.ST_SmartWAS;
            var bmp = new Bitmap(src.Width, src.Height);
            Color light = Color.FromArgb(235, 238, 242);
            for (int y = 0; y < src.Height; y++)
            {
                for (int x = 0; x < src.Width; x++)
                {
                    Color c = src.GetPixel(x, y);
                    bool dark = c.A > 0 && c.R < 90 && c.G < 90 && c.B < 90;
                    bmp.SetPixel(x, y, Night && dark ? Color.FromArgb(c.A, light) : c);
                }
            }
            return bmp;
        }

        /// <summary>Directions: arrows both ways, the right one ticked.</summary>
        public static Bitmap Directions(int size = 32)
        {
            Bitmap bmp = Canvas(size, out Graphics g);
            using (g)
            using (var ink = P(Ink, 2.4f))
            using (var tick = P(Green, 2.8f))
            using (var accent = P(Orange, 2.4f))
            {
                ink.CustomEndCap = new AdjustableArrowCap(3, 3);
                accent.CustomEndCap = new AdjustableArrowCap(3, 3);
                g.DrawLine(ink, 16, 9, 4, 9);
                g.DrawLine(accent, 14, 21, 28, 21);
                g.DrawLines(tick, new[] { new PointF(19, 8), new PointF(22, 11), new PointF(28, 4) });
            }
            return bmp;
        }

        /// <summary>Steering geometry: a wheel turned off the straight line, with the angle between them.</summary>
        public static Bitmap Geometry(int size = 32)
        {
            Bitmap bmp = Canvas(size, out Graphics g);
            using (g)
            using (var dash = P(Soft, 1.6f, DashStyle.Dash))
            using (var arc = P(Orange, 2.4f))
            using (var tyre = new SolidBrush(Ink))
            using (var tread = P(Night ? Color.FromArgb(70, 75, 85) : Color.FromArgb(210, 214, 220), 1.2f))
            {
                // straight ahead, and the angle the wheel stands at, drawn outside the tyre
                g.DrawLine(dash, 16, 31, 16, 1);
                g.DrawArc(arc, 3, 4, 26, 26, 270, 30);
                g.DrawLine(arc, 16, 4, 16, 9);
                var state = g.Save();
                g.TranslateTransform(16, 17);
                g.RotateTransform(30);
                using (var path = Rounded(-4, -9, 8, 18, 3)) g.FillPath(tyre, path);
                for (int i = -6; i <= 6; i += 4) g.DrawLine(tread, -2.5f, i, 2.5f, i);
                g.Restore(state);
            }
            return bmp;
        }

        /// <summary>Steer motor: a gear.</summary>
        public static Bitmap Motor(int size = 32)
        {
            Bitmap bmp = Canvas(size, out Graphics g);
            using (g)
            using (var teeth = new SolidBrush(Ink))
            using (var hub = new SolidBrush(Orange))
            {
                for (int i = 0; i < 8; i++)
                {
                    var state = g.Save();
                    g.TranslateTransform(16, 16);
                    g.RotateTransform(i * 45);
                    g.FillRectangle(teeth, -3, -14, 6, 7);
                    g.Restore(state);
                }
                g.FillEllipse(teeth, 5, 5, 22, 22);
                g.FillEllipse(hub, 10.5f, 10.5f, 11, 11);
                using (var hole = new SolidBrush(Ink)) g.FillEllipse(hole, 14, 14, 4, 4);
            }
            return bmp;
        }

        /// <summary>Dead zone: a band in the middle with arrows stopping at its edges.</summary>
        public static Bitmap DeadZone(int size = 32)
        {
            Bitmap bmp = Canvas(size, out Graphics g);
            using (g)
            using (var band = new SolidBrush(Color.FromArgb(Night ? 90 : 70, Orange)))
            using (var edge = P(Orange, 2f))
            using (var arrow = P(Ink, 2.4f))
            {
                g.FillRectangle(band, 12, 6, 8, 20);
                g.DrawLine(edge, 12, 5, 12, 27);
                g.DrawLine(edge, 20, 5, 20, 27);
                g.DrawLine(arrow, 2, 16, 9, 16);
                g.DrawLines(arrow, new[] { new PointF(6, 12.5f), new PointF(9.5f, 16), new PointF(6, 19.5f) });
                g.DrawLine(arrow, 30, 16, 23, 16);
                g.DrawLines(arrow, new[] { new PointF(26, 12.5f), new PointF(22.5f, 16), new PointF(26, 19.5f) });
            }
            return bmp;
        }

        /// <summary>Look-ahead: the line ahead and the point the tractor aims at.</summary>
        public static Bitmap LookAhead(int size = 32)
        {
            Bitmap bmp = Canvas(size, out Graphics g);
            using (g)
            using (var line = P(Green, 2.4f))
            using (var aim = P(Ink, 1.8f, DashStyle.Dot))
            using (var tractor = new SolidBrush(Ink))
            using (var target = new SolidBrush(Orange))
            {
                g.DrawBezier(line, 6, 31, 6, 18, 20, 14, 22, 2);
                g.DrawLine(aim, 9, 25, 19, 9);
                using (var path = Rounded(5, 23, 8, 8, 2)) g.FillPath(tractor, path);
                g.FillEllipse(target, 16, 4, 8, 8);
                using (var ring = P(Ink, 1.4f)) g.DrawEllipse(ring, 14.5f, 2.5f, 11, 11);
            }
            return bmp;
        }

        /// <summary>Speed factor: a speedometer.</summary>
        public static Bitmap Speed(int size = 32)
        {
            Bitmap bmp = Canvas(size, out Graphics g);
            using (g)
            using (var dial = P(Ink, 2.6f))
            using (var fast = P(Orange, 2.6f))
            using (var needle = P(Orange, 2.4f))
            using (var hub = new SolidBrush(Ink))
            {
                g.DrawArc(dial, 3, 5, 26, 26, 160, 150);
                g.DrawArc(fast, 3, 5, 26, 26, 310, 70);
                for (int a = 180; a <= 360; a += 45)
                {
                    double r = a * Math.PI / 180;
                    g.DrawLine(dial, (float)(16 + 9 * Math.Cos(r)), (float)(18 + 9 * Math.Sin(r)), (float)(16 + 11 * Math.Cos(r)), (float)(18 + 11 * Math.Sin(r)));
                }
                g.DrawLine(needle, 16, 18, 24, 10);
                g.FillEllipse(hub, 13, 15, 6, 6);
            }
            return bmp;
        }

        /// <summary>Side-hill: a slope with a tractor tilted on it.</summary>
        public static Bitmap SideHill(int size = 32)
        {
            Bitmap bmp = Canvas(size, out Graphics g);
            using (g)
            using (var ground = new SolidBrush(Color.FromArgb(Night ? 120 : 90, Green)))
            using (var slope = P(Green, 2.2f))
            using (var body = new SolidBrush(Ink))
            using (var cab = P(Ink, 1.8f))
            using (var wheel = new SolidBrush(Orange))
            using (var hub = new SolidBrush(Night ? Color.FromArgb(60, 64, 72) : Color.White))
            {
                g.FillPolygon(ground, new[] { new PointF(0, 31), new PointF(32, 15), new PointF(32, 32), new PointF(0, 32) });
                g.DrawLine(slope, 0, 31, 32, 15);
                // the tractor stands on the slope: everything drawn level, then turned with it
                var state = g.Save();
                g.TranslateTransform(15, 23.5f);
                g.RotateTransform(-26.6f);
                g.FillRectangle(body, -9, -9, 17, 5);          // bonnet and frame
                g.DrawRectangle(cab, -8, -17, 7, 8);           // cab
                g.FillEllipse(wheel, -10, -8, 9, 9);           // big rear wheel
                g.FillEllipse(hub, -7, -5, 3, 3);
                g.FillEllipse(wheel, 3, -5, 6, 6);             // small front wheel
                g.Restore(state);
            }
            return bmp;
        }

        /// <summary>Roll zero: a spirit level with its bubble in the middle.</summary>
        public static Bitmap RollZero(int size = 32)
        {
            Bitmap bmp = Canvas(size, out Graphics g);
            using (g)
            using (var body = P(Ink, 2.2f))
            using (var tick = P(Orange, 1.8f))
            using (var bubble = new SolidBrush(Green))
            using (var tilt = P(Soft, 1.6f, DashStyle.Dash))
            {
                g.DrawLine(tilt, 2, 24, 30, 10);
                using (var path = Rounded(3, 11, 26, 10, 5)) g.DrawPath(body, path);
                g.FillEllipse(bubble, 12, 13, 8, 6);
                g.DrawLine(tick, 11, 9, 11, 23);
                g.DrawLine(tick, 21, 9, 21, 23);
            }
            return bmp;
        }

        /// <summary>Coming onto the line: a path curving in to settle on the line.</summary>
        public static Bitmap Acquire(int size = 32)
        {
            Bitmap bmp = Canvas(size, out Graphics g);
            using (g)
            using (var line = P(Green, 2.4f))
            using (var path = P(Orange, 2.4f))
            using (var start = new SolidBrush(Ink))
            {
                g.DrawLine(line, 20, 31, 20, 1);
                g.DrawBezier(path, 4, 30, 4, 18, 20, 18, 20, 6);
                g.FillEllipse(start, 1, 26, 6, 6);
                g.DrawLines(path, new[] { new PointF(16.5f, 9), new PointF(20, 4), new PointF(23.5f, 9) });
            }
            return bmp;
        }

        /// <summary>Integral: the integral sign over a line it pulls back to.</summary>
        public static Bitmap Integral(int size = 32)
        {
            Bitmap bmp = Canvas(size, out Graphics g);
            using (g)
            using (var sign = P(Ink, 2.6f))
            using (var line = P(Green, 2f, DashStyle.Dash))
            using (var arrow = P(Orange, 2.2f))
            {
                g.DrawBezier(sign, 18, 4, 14, 1, 13, 6, 14, 16);
                g.DrawBezier(sign, 14, 16, 15, 26, 13, 31, 9, 28);
                g.DrawLine(line, 1, 30, 31, 30);
                g.DrawLine(arrow, 26, 10, 26, 24);
                g.DrawLines(arrow, new[] { new PointF(22.5f, 20.5f), new PointF(26, 24), new PointF(29.5f, 20.5f) });
            }
            return bmp;
        }

        /// <summary>U-turn compensation: a U-turn joining the next line.</summary>
        public static Bitmap UTurn(int size = 32)
        {
            Bitmap bmp = Canvas(size, out Graphics g);
            using (g)
            using (var lines = P(Soft, 1.6f, DashStyle.Dash))
            using (var turn = P(Orange, 2.6f))
            {
                g.DrawLine(lines, 8, 31, 8, 12);
                g.DrawLine(lines, 24, 31, 24, 12);
                g.DrawLine(turn, 8, 28, 8, 14);
                g.DrawArc(turn, 8, 6, 16, 16, 180, 180);
                g.DrawLine(turn, 24, 14, 24, 26);
                g.DrawLines(turn, new[] { new PointF(20.5f, 22.5f), new PointF(24, 26), new PointF(27.5f, 22.5f) });
            }
            return bmp;
        }

        /// <summary>GPS guard: a satellite with signal waves and a check shield.</summary>
        public static Bitmap Gps(int size = 32)
        {
            Bitmap bmp = Canvas(size, out Graphics g);
            using (g)
            using (var body = new SolidBrush(Ink))
            using (var panel = new SolidBrush(Color.FromArgb(30, 136, 229)))
            using (var wave = P(Orange, 2f))
            {
                var state = g.Save();
                g.TranslateTransform(11, 11);
                g.RotateTransform(45);
                g.FillRectangle(body, -3, -3, 6, 6);
                g.FillRectangle(panel, -11, -2.5f, 7, 5);
                g.FillRectangle(panel, 4, -2.5f, 7, 5);
                g.Restore(state);
                g.DrawArc(wave, 12, 12, 12, 12, 0, 90);
                g.DrawArc(wave, 8, 8, 20, 20, 5, 80);
                using (var shield = new SolidBrush(Green)) g.FillEllipse(shield, 19, 19, 12, 12);
                using (var tick = P(Color.White, 1.8f)) g.DrawLines(tick, new[] { new PointF(22, 25), new PointF(24.5f, 27.5f), new PointF(28, 22.5f) });
            }
            return bmp;
        }

        /// <summary>Sensor linearity: a straight dashed line and the bent curve of the sensor.</summary>
        public static Bitmap Linearity(int size = 32)
        {
            Bitmap bmp = Canvas(size, out Graphics g);
            using (g)
            using (var axis = P(Soft, 1.6f))
            using (var straight = P(Soft, 1.6f, DashStyle.Dash))
            using (var curve = P(Orange, 2.6f))
            {
                g.DrawLine(axis, 3, 29, 30, 29);
                g.DrawLine(axis, 3, 29, 3, 2);
                g.DrawLine(straight, 3, 29, 29, 3);
                g.DrawBezier(curve, 3, 29, 14, 18, 20, 14, 29, 8);
                using (var dot = new SolidBrush(Ink)) g.FillEllipse(dot, 25.5f, 4.5f, 6, 6);
            }
            return bmp;
        }

        /// <summary>IMU fusion: a chip and a compass needle joined by arrows.</summary>
        public static Bitmap Fusion(int size = 32)
        {
            Bitmap bmp = Canvas(size, out Graphics g);
            using (g)
            using (var chip = new SolidBrush(Ink))
            using (var legs = P(Ink, 1.6f))
            using (var needle = new SolidBrush(Orange))
            using (var arrow = P(Green, 2f))
            {
                g.FillRectangle(chip, 3, 9, 12, 12);
                for (int i = 0; i < 3; i++)
                {
                    g.DrawLine(legs, 5 + i * 4, 6, 5 + i * 4, 9);
                    g.DrawLine(legs, 5 + i * 4, 21, 5 + i * 4, 24);
                }
                g.FillPolygon(needle, new[] { new PointF(25, 3), new PointF(28.5f, 15), new PointF(25, 13), new PointF(21.5f, 15) });
                using (var ring = P(Ink, 1.6f)) g.DrawEllipse(ring, 18, 2, 14, 14);
                g.DrawLine(arrow, 16, 27, 28, 27);
                g.DrawLines(arrow, new[] { new PointF(24.5f, 24), new PointF(28, 27), new PointF(24.5f, 30) });
            }
            return bmp;
        }

        /// <summary>Speed dependent gain: a gear with speed lines.</summary>
        public static Bitmap FastGain(int size = 32)
        {
            Bitmap bmp = Canvas(size, out Graphics g);
            using (g)
            using (var teeth = new SolidBrush(Ink))
            using (var hub = new SolidBrush(Orange))
            using (var lines = P(Orange, 2.2f))
            {
                for (int i = 0; i < 8; i++)
                {
                    var state = g.Save();
                    g.TranslateTransform(20, 16);
                    g.RotateTransform(i * 45);
                    g.FillRectangle(teeth, -2.5f, -11, 5, 5);
                    g.Restore(state);
                }
                g.FillEllipse(teeth, 11, 7, 18, 18);
                g.FillEllipse(hub, 16, 12, 8, 8);
                g.DrawLine(lines, 1, 10, 8, 10);
                g.DrawLine(lines, 0, 16, 6, 16);
                g.DrawLine(lines, 1, 22, 8, 22);
            }
            return bmp;
        }

        /// <summary>RTK bridge: an arch over a gap.</summary>
        public static Bitmap Bridge(int size = 32)
        {
            Bitmap bmp = Canvas(size, out Graphics g);
            using (g)
            using (var deck = P(Ink, 2.6f))
            using (var arch = P(Orange, 2.4f))
            using (var cable = P(Soft, 1.4f))
            using (var bank = new SolidBrush(Green))
            {
                g.FillRectangle(bank, 0, 20, 6, 12);
                g.FillRectangle(bank, 26, 20, 6, 12);
                g.DrawLine(deck, 1, 19, 31, 19);
                g.DrawArc(arch, 4, 6, 24, 26, 180, 180);
                for (int x = 9; x <= 23; x += 4) g.DrawLine(cable, x, 10.5f + System.Math.Abs(16 - x) * 0.55f, x, 19);
            }
            return bmp;
        }

        /// <summary>TDCP: a satellite and the carrier wave down to the antenna.</summary>
        public static Bitmap Tdcp(int size = 32)
        {
            Bitmap bmp = Canvas(size, out Graphics g);
            using (g)
            using (var sat = new SolidBrush(Ink))
            using (var panel = new SolidBrush(Color.FromArgb(30, 136, 229)))
            using (var wave = P(Orange, 2f))
            using (var mast = P(Ink, 2.2f))
            {
                g.FillRectangle(sat, 21, 3, 6, 6);
                g.FillRectangle(panel, 14, 4.5f, 6, 3);
                g.FillRectangle(panel, 28, 4.5f, 3, 3);
                var pts = new PointF[12];
                for (int i = 0; i < pts.Length; i++)
                {
                    float k = i / (float)(pts.Length - 1);
                    pts[i] = new PointF(23 - 15 * k + 2.5f * (float)System.Math.Sin(k * 6 * System.Math.PI), 10 + 15 * k);
                }
                g.DrawCurve(wave, pts);
                g.DrawLine(mast, 6, 26, 6, 31);
                g.DrawArc(mast, 1, 21, 10, 8, 180, 180);
            }
            return bmp;
        }

        private static GraphicsPath Rounded(float x, float y, float w, float h, float r)
        {
            var p = new GraphicsPath();
            p.AddArc(x, y, 2 * r, 2 * r, 180, 90);
            p.AddArc(x + w - 2 * r, y, 2 * r, 2 * r, 270, 90);
            p.AddArc(x + w - 2 * r, y + h - 2 * r, 2 * r, 2 * r, 0, 90);
            p.AddArc(x, y + h - 2 * r, 2 * r, 2 * r, 90, 90);
            p.CloseFigure();
            return p;
        }
    }
}
