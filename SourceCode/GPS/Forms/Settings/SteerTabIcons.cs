using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace AgOpenGPS
{
    // The steer settings' tab icons, redrawn in code at a high resolution: the old ones came from a small 8 bit image
    // list and looked blurred. Same subjects and colours as before; drawn on a grid 100 high (150 wide for the wide
    // tuning tabs) at four times the size, then scaled down smoothly, into 32 bit image lists.
    internal static class SteerTabIcons
    {
        private const int Grid = 100, Oversample = 4;

        private static readonly Color Orange = Color.FromArgb(255, 140, 0);
        private static readonly Color Dark = Color.FromArgb(55, 60, 68);
        private static readonly Color Grey = Color.FromArgb(150, 158, 170);
        private static readonly Color Teal = Color.FromArgb(18, 110, 110);
        private static readonly Color Blue = Color.FromArgb(30, 136, 229);
        private static readonly Color Green = Color.FromArgb(67, 160, 71);

        /// <summary>The tuning tabs (wide, 60 x 40): steer, gain, Stanley, pure pursuit, advanced - the old list's order.</summary>
        public static ImageList TuningList()
        {
            return List(60, 40, Square(SteerWheel), Square(Gain), Stanley, PurePursuit, Square(Advanced), MpcTab, AdaptiveTab);
        }

        /// <summary>The adaptive pure pursuit tab's place in the tuning list.</summary>
        public const int AdaptiveTabIndex = 6;

        // adaptive pure pursuit: pure pursuit's arrow, its length marked as changing (a short and a long tick)
        private static void AdaptiveTab(Graphics g)
        {
            ArrowTractor(g, Blue);
            using (var mark = P(Orange, 5f))
            {
                g.DrawLine(mark, 4, 84, 58, 84);
                g.DrawLine(mark, 4, 78, 4, 90);
                g.DrawLine(mark, 30, 78, 30, 90);
                g.DrawLine(mark, 58, 78, 58, 90);
                g.DrawLine(mark, 4, 16, 18, 16);
                g.DrawLine(mark, 8, 10, 4, 16);
                g.DrawLine(mark, 8, 22, 4, 16);
                g.DrawLine(mark, 58, 16, 44, 16);
                g.DrawLine(mark, 54, 10, 58, 16);
                g.DrawLine(mark, 54, 22, 58, 16);
            }
        }

        /// <summary>The adaptive switch's picture: the letter A over pure pursuit's arrow with its changing length.</summary>
        public static Bitmap AdaptiveMode(int w, int h)
        {
            return Render(g =>
            {
                float gridW = 100f * w / h;
                using (var font = new Font("Segoe UI", 34, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var text = new SolidBrush(Blue))
                using (var centre = new StringFormat { Alignment = StringAlignment.Center })
                    g.DrawString("A", font, text, new RectangleF(0, 0, gridW, 44), centre);
                var state = g.Save();
                float scale = Math.Min(gridW / 150f, 0.54f);
                g.TranslateTransform((gridW - 150 * scale) / 2, 44);
                g.ScaleTransform(scale, scale);
                AdaptiveTab(g);
                g.Restore(state);
            }, w, h);
        }

        /// <summary>The MPC tab's place in the tuning list.</summary>
        public const int MpcTabIndex = 5;

        // MPC: the tractor and the paths it tries ahead of it, the chosen one solid
        private static void MpcTab(Graphics g)
        {
            using (var tried = new Pen(Color.FromArgb(230, Orange), 5f) { DashStyle = DashStyle.Dash, StartCap = LineCap.Round, EndCap = LineCap.Round })
            using (var chosen = P(Blue, 9f))
            {
                g.DrawBezier(tried, 62, 50, 40, 50, 22, 40, 4, 16);
                g.DrawBezier(tried, 62, 50, 40, 50, 22, 60, 4, 84);
                g.DrawBezier(chosen, 62, 50, 40, 50, 22, 46, 6, 38);
            }
            using (var dot = new SolidBrush(Blue))
                g.FillEllipse(dot, 0, 32, 12, 12);
            TractorOnly(g);
        }

        private static void TractorOnly(Graphics g)
        {
            using (var body = new SolidBrush(Color.FromArgb(232, 235, 240)))
            using (var line = P(Dark, 3.5f))
            using (var tyre = new SolidBrush(Dark))
            using (var cab = new SolidBrush(Blue))
            {
                g.FillRectangle(tyre, 72, 20, 20, 12);
                g.FillRectangle(tyre, 72, 68, 20, 12);
                g.FillRectangle(tyre, 110, 12, 28, 18);
                g.FillRectangle(tyre, 110, 70, 28, 18);
                g.FillRectangle(body, 64, 34, 80, 32);
                g.DrawRectangle(line, 64, 34, 80, 32);
                g.FillRectangle(cab, 112, 40, 22, 20);
                g.DrawRectangle(line, 112, 40, 22, 20);
            }
        }

        /// <summary>The steer mode button's picture for MPC or LQR, as pure pursuit's: the letters over the tractor and its paths.</summary>
        public static Bitmap MpcMode(int w, int h, string letters)
        {
            return Render(g =>
            {
                float gridW = 100f * w / h;
                using (var font = new Font("Segoe UI", 30, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var text = new SolidBrush(Blue))
                using (var centre = new StringFormat { Alignment = StringAlignment.Center })
                    g.DrawString(letters, font, text, new RectangleF(0, 4, gridW, 40), centre);
                // the tab picture below, smaller and centred
                var state = g.Save();
                float scale = Math.Min(gridW / 150f, 0.54f);
                g.TranslateTransform((gridW - 150 * scale) / 2, 44);
                g.ScaleTransform(scale, scale);
                MpcTab(g);
                g.Restore(state);
            }, w, h);
        }

        /// <summary>The settings tabs (40 x 40): sensors, pins, settings, alarm, on the line - the old list's order.</summary>
        public static ImageList SettingsList()
        {
            return List(40, 40, Sensors, Pins, TealWheel, Alarm, Gear);
        }

        // a square (100 x 100) icon in the middle of a wide (150 x 100) one
        private static Action<Graphics> Square(Action<Graphics> draw)
        {
            return g => { g.TranslateTransform(25, 0); draw(g); };
        }

        private static ImageList List(int w, int h, params Action<Graphics>[] icons)
        {
            var list = new ImageList { ColorDepth = ColorDepth.Depth32Bit, ImageSize = new System.Drawing.Size(w, h) };
            foreach (Action<Graphics> icon in icons) list.Images.Add(Render(icon, w, h));
            return list;
        }

        public static Bitmap Render(Action<Graphics> draw, int w, int h)
        {
            using (var large = new Bitmap(w * Oversample, h * Oversample))
            {
                using (Graphics g = Graphics.FromImage(large))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    float scale = h * Oversample / (float)Grid;
                    g.ScaleTransform(scale, scale);
                    draw(g);
                }
                var small = new Bitmap(w, h);
                using (Graphics g = Graphics.FromImage(small))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.DrawImage(large, 0, 0, w, h);
                }
                return small;
            }
        }

        private static Pen P(Color c, float w)
        {
            return new Pen(c, w) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        }

        // a steering wheel: rim, grips at the sides, three spokes and a hub
        private static void Wheel(Graphics g, float cx, float cy, float r, Color rim, Color grip, Color hub)
        {
            using (var pRim = P(rim, r * 0.24f))
            using (var pGrip = P(grip, r * 0.26f))
            using (var spoke = P(rim, r * 0.18f))
            using (var bHub = new SolidBrush(hub))
            using (var bRim = new SolidBrush(rim))
            {
                g.DrawEllipse(pRim, cx - r, cy - r, 2 * r, 2 * r);
                if (grip != rim)
                {
                    g.DrawArc(pGrip, cx - r, cy - r, 2 * r, 2 * r, 145, 60);
                    g.DrawArc(pGrip, cx - r, cy - r, 2 * r, 2 * r, -25, 60);
                }
                g.DrawLine(spoke, cx - r, cy, cx + r, cy);
                g.DrawLine(spoke, cx, cy, cx, cy + r);
                g.FillEllipse(bRim, cx - r * 0.36f, cy - r * 0.36f, r * 0.72f, r * 0.72f);
                g.FillEllipse(bHub, cx - r * 0.22f, cy - r * 0.22f, r * 0.44f, r * 0.44f);
            }
        }

        private static void SteerWheel(Graphics g) => Wheel(g, 50, 50, 38, Dark, Orange, Orange);

        private static void TealWheel(Graphics g) => Wheel(g, 50, 50, 38, Teal, Teal, Color.FromArgb(120, 200, 200));

        // gain: a coloured dial, green to red, with a blue knob and its pointer
        private static void Gain(Graphics g)
        {
            Color[] bands = { Color.FromArgb(46, 125, 50), Color.FromArgb(124, 179, 66), Color.FromArgb(253, 216, 53), Color.FromArgb(251, 140, 0), Color.FromArgb(211, 47, 47) };
            float sweep = 180f / bands.Length;
            for (int i = 0; i < bands.Length; i++)
                using (var pen = new Pen(bands[i], 14f)) g.DrawArc(pen, 10, 18, 80, 80, 180 + i * sweep, sweep + 0.5f);
            using (var knob = new SolidBrush(Blue))
            using (var rim = P(Color.FromArgb(13, 71, 161), 3f))
            {
                g.FillEllipse(knob, 30, 38, 40, 40);
                g.DrawEllipse(rim, 30, 38, 40, 40);
            }
            using (var needle = P(Orange, 5f)) g.DrawLine(needle, 50, 58, 50, 42);
        }

        // Stanley and pure pursuit: an arrow to the left (orange, blue) and a tractor seen from above (150 x 100)
        private static void Stanley(Graphics g) => ArrowTractor(g, Orange);

        private static void PurePursuit(Graphics g) => ArrowTractor(g, Blue);

        private static void ArrowTractor(Graphics g, Color arrowColour)
        {
            using (var arrow = new SolidBrush(arrowColour))
                g.FillPolygon(arrow, new[] { new PointF(4, 50), new PointF(30, 26), new PointF(30, 41), new PointF(58, 41), new PointF(58, 59), new PointF(30, 59), new PointF(30, 74) });
            using (var body = new SolidBrush(Color.FromArgb(232, 235, 240)))
            using (var line = P(Dark, 3.5f))
            using (var tyre = new SolidBrush(Dark))
            using (var cab = new SolidBrush(arrowColour))
            {
                g.FillRectangle(tyre, 72, 20, 20, 12);    // front wheels
                g.FillRectangle(tyre, 72, 68, 20, 12);
                g.FillRectangle(tyre, 110, 12, 28, 18);   // rear wheels
                g.FillRectangle(tyre, 110, 70, 28, 18);
                g.FillRectangle(body, 64, 34, 80, 32);
                g.DrawRectangle(line, 64, 34, 80, 32);
                g.FillRectangle(cab, 112, 40, 22, 20);    // cab
                g.DrawRectangle(line, 112, 40, 22, 20);
            }
        }

        // advanced: a green wheel with a blue gear
        private static void Advanced(Graphics g)
        {
            Wheel(g, 44, 56, 32, Color.FromArgb(46, 125, 50), Color.FromArgb(102, 187, 106), Color.FromArgb(200, 230, 201));
            GearShape(g, 74, 26, 20, Blue, Color.White);
        }

        // sensors: a dial with a blue ring and a red needle
        private static void Sensors(Graphics g) => Dial(g);

        private static void Dial(Graphics g)
        {
            using (var face = new SolidBrush(Color.FromArgb(245, 248, 252)))
            using (var ring = P(Blue, 8f))
            using (var tick = P(Dark, 3f))
            using (var needle = P(Color.FromArgb(211, 47, 47), 5f))
            using (var hub = new SolidBrush(Dark))
            {
                g.FillEllipse(face, 10, 10, 80, 80);
                g.DrawEllipse(ring, 10, 10, 80, 80);
                for (int a = 135; a <= 405; a += 30)
                {
                    double r = a * Math.PI / 180;
                    g.DrawLine(tick, (float)(50 + 28 * Math.Cos(r)), (float)(50 + 28 * Math.Sin(r)), (float)(50 + 34 * Math.Cos(r)), (float)(50 + 34 * Math.Sin(r)));
                }
                g.DrawLine(needle, 50, 50, 66, 30);
                g.FillEllipse(hub, 44, 44, 12, 12);
            }
        }

        // pins: a module board with its connector lines
        private static void Pins(Graphics g)
        {
            using (var board = P(Teal, 6f))
            using (var head = new SolidBrush(Teal))
            using (var pin = P(Teal, 4f))
            {
                g.DrawRectangle(board, 16, 12, 68, 76);
                g.FillRectangle(head, 16, 12, 68, 14);
                g.DrawLine(pin, 38, 40, 38, 76);
                g.DrawLines(pin, new[] { new PointF(52, 40), new PointF(52, 50), new PointF(60, 56), new PointF(52, 62), new PointF(52, 76) });
                g.DrawLines(pin, new[] { new PointF(66, 40), new PointF(66, 50), new PointF(58, 56) });
                g.DrawLine(pin, 66, 62, 66, 76);
            }
        }

        // alarm: a bell with sound waves
        private static void Alarm(Graphics g)
        {
            using (var bell = new SolidBrush(Teal))
            using (var wave = P(Teal, 5f))
            {
                var path = new GraphicsPath();
                path.AddBezier(28, 70, 32, 62, 30, 30, 50, 24);
                path.AddBezier(50, 24, 70, 30, 68, 62, 72, 70);
                path.AddLine(72, 70, 28, 70);
                g.FillPath(bell, path);
                g.FillEllipse(bell, 44, 72, 12, 10);
                g.FillEllipse(bell, 46, 16, 8, 10);
                g.DrawArc(wave, 14, 22, 20, 40, 120, 120);
                g.DrawArc(wave, 4, 14, 26, 56, 125, 110);
                g.DrawArc(wave, 66, 22, 20, 40, -60, 120);
                g.DrawArc(wave, 70, 14, 26, 56, -55, 110);
            }
        }

        private static void Gear(Graphics g) => GearShape(g, 50, 50, 40, Teal, Color.FromArgb(0, 0, 0, 0));

        // a gear: eight teeth, a body and a hole (hole colour transparent leaves a real hole)
        private static void GearShape(Graphics g, float cx, float cy, float r, Color body, Color hole)
        {
            using (var b = new SolidBrush(body))
            {
                var path = new GraphicsPath();
                for (int i = 0; i < 8; i++)
                {
                    var state = g.Save();
                    g.TranslateTransform(cx, cy);
                    g.RotateTransform(i * 45);
                    g.FillRectangle(b, -r * 0.2f, -r, r * 0.4f, r * 0.4f);
                    g.Restore(state);
                }
                path.AddEllipse(cx - r * 0.75f, cy - r * 0.75f, r * 1.5f, r * 1.5f);
                path.AddEllipse(cx - r * 0.32f, cy - r * 0.32f, r * 0.64f, r * 0.64f);   // alternate fill: a hole
                g.FillPath(b, path);
                if (hole.A > 0)
                    using (var h = new SolidBrush(hole)) g.FillEllipse(h, cx - r * 0.32f, cy - r * 0.32f, r * 0.64f, r * 0.64f);
            }
        }
    }
}
