using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace IconForge
{
    // The icons of the main window's left bar, drawn in code: coloured shapes with soft gradients, a light top edge
    // and a faint shadow, so they read on the light bar by day and the dark one at night alike. Each is drawn on a
    // 64 grid (the menu one on 47 x 36), the size of the pictures they replace.
    internal static class LeftBar
    {
        /// <summary>The left bar's pictures, by resource name.</summary>
        public static void AddAll(System.Action<string, float, float, System.Action<Graphics>> add)
        {
            add("fileMenu", 47, 36, g => g.DrawImage(Menu(), 0, 0, 47, 36));
            add("NavigationSettings", 64, 64, g => g.DrawImage(Display(), 0, 0, 64, 64));
            add("SpecialFunctions", 64, 64, g => g.DrawImage(Tools(), 0, 0, 64, 64));
            add("Settings48", 64, 64, g => g.DrawImage(Settings(), 0, 0, 64, 64));
            add("JobActive", 64, 64, g => g.DrawImage(Job(), 0, 0, 64, 64));
            add("FieldTools", 64, 64, g => g.DrawImage(FieldTools(), 0, 0, 64, 64));
            add("AutoSteerConf", 64, 64, g => g.DrawImage(SteerPanel(), 0, 0, 64, 64));
            add("AgIO", 64, 64, g => g.DrawImage(AgIO(), 0, 0, 64, 64));
        }

        private static Bitmap Canvas(int w, int h, out Graphics g)
        {
            var bmp = new Bitmap(w, h);
            g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            return bmp;
        }

        private static LinearGradientBrush Grad(RectangleF r, Color top, Color bottom, float angle = 90)
        {
            if (r.Width < 1) r.Width = 1;
            if (r.Height < 1) r.Height = 1;
            return new LinearGradientBrush(r, top, bottom, angle);
        }

        private static GraphicsPath Rounded(float x, float y, float w, float h, float r)
        {
            var p = new GraphicsPath();
            float d = 2 * r;
            p.AddArc(x, y, d, d, 180, 90);
            p.AddArc(x + w - d, y, d, d, 270, 90);
            p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
            p.AddArc(x, y + h - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        // the path, a soft shadow under it, a gradient fill and a thin outline
        private static void Shape(Graphics g, GraphicsPath path, Color top, Color bottom, Color edge, float edgeWidth = 1.2f)
        {
            using (var shadowPath = (GraphicsPath)path.Clone())
            using (var shadow = new SolidBrush(Color.FromArgb(55, 0, 0, 0)))
            using (var m = new Matrix())
            {
                m.Translate(1.2f, 1.8f);
                shadowPath.Transform(m);
                g.FillPath(shadow, shadowPath);
            }
            using (var fill = Grad(path.GetBounds(), top, bottom))
            using (var pen = new Pen(edge, edgeWidth) { LineJoin = LineJoin.Round })
            {
                g.FillPath(fill, path);
                g.DrawPath(pen, path);
            }
        }

        // a gear: teeth around a rim, a hole in the middle
        private static GraphicsPath GearPath(float cx, float cy, float rOuter, float rInner, int teeth, float rHole)
        {
            var pts = new PointF[teeth * 4];
            double step = 2 * Math.PI / teeth;
            for (int i = 0; i < teeth; i++)
            {
                double a = i * step;
                pts[4 * i] = Polar(cx, cy, rInner, a - step * 0.30);
                pts[4 * i + 1] = Polar(cx, cy, rOuter, a - step * 0.17);
                pts[4 * i + 2] = Polar(cx, cy, rOuter, a + step * 0.17);
                pts[4 * i + 3] = Polar(cx, cy, rInner, a + step * 0.30);
            }
            var p = new GraphicsPath(FillMode.Alternate);
            p.AddPolygon(pts);
            p.AddEllipse(cx - rHole, cy - rHole, 2 * rHole, 2 * rHole);
            return p;
        }

        private static PointF Polar(float cx, float cy, float r, double a) =>
            new PointF(cx + r * (float)Math.Cos(a), cy + r * (float)Math.Sin(a));

        /// <summary>The menu: three rounded bars, green, amber and orange.</summary>
        public static Bitmap Menu()
        {
            Bitmap bmp = Canvas(47, 36, out Graphics g);
            using (g)
            {
                Color[][] colours =
                {
                    new[] { Color.FromArgb(129, 199, 132), Color.FromArgb(56, 142, 60) },
                    new[] { Color.FromArgb(255, 213, 79), Color.FromArgb(255, 160, 0) },
                    new[] { Color.FromArgb(255, 138, 101), Color.FromArgb(230, 74, 25) },
                };
                for (int i = 0; i < 3; i++)
                    using (GraphicsPath bar = Rounded(4, 5 + 10 * i, 38, 7, 3.5f))
                        Shape(g, bar, colours[i][0], colours[i][1], Color.FromArgb(120, 0, 0, 0), 0.8f);
            }
            return bmp;
        }

        /// <summary>Display settings: a tablet showing a field with guidance lines, and a small gear.</summary>
        public static Bitmap Display()
        {
            Bitmap bmp = Canvas(64, 64, out Graphics g);
            using (g)
            {
                // stand
                using (GraphicsPath foot = Rounded(20, 52, 24, 5, 2.5f))
                    Shape(g, foot, Color.FromArgb(120, 130, 145), Color.FromArgb(70, 78, 90), Color.FromArgb(50, 55, 65));
                using (var neck = new SolidBrush(Color.FromArgb(80, 88, 100))) g.FillRectangle(neck, 29, 46, 6, 7);

                // body and screen
                using (GraphicsPath body = Rounded(4, 8, 56, 40, 6))
                    Shape(g, body, Color.FromArgb(84, 92, 106), Color.FromArgb(38, 42, 50), Color.FromArgb(25, 28, 34));
                var screen = new RectangleF(9, 13, 46, 30);
                using (GraphicsPath sp = Rounded(screen.X, screen.Y, screen.Width, screen.Height, 2.5f))
                {
                    GraphicsState st = g.Save();
                    g.SetClip(sp);
                    using (var sky = Grad(screen, Color.FromArgb(129, 212, 250), Color.FromArgb(3, 155, 229)))
                        g.FillRectangle(sky, screen);
                    var ground = new RectangleF(9, 26, 46, 17);
                    using (var field = Grad(ground, Color.FromArgb(156, 204, 101), Color.FromArgb(56, 142, 60)))
                        g.FillRectangle(field, ground);
                    // guidance lines running into the distance
                    using (var line = new Pen(Color.FromArgb(230, 255, 255, 255), 1.4f))
                        for (int i = -3; i <= 3; i++) g.DrawLine(line, 32 + i * 2.2f, 26, 32 + i * 11, 43);
                    using (var path = new Pen(Color.FromArgb(255, 160, 0), 2.2f)) g.DrawLine(path, 32, 26, 32, 43);
                    g.Restore(st);
                }
                // a glint on the glass
                using (var glint = new SolidBrush(Color.FromArgb(50, 255, 255, 255)))
                    g.FillPolygon(glint, new[] { new PointF(9, 13), new PointF(30, 13), new PointF(9, 30) });

                // settings: a small gear at the corner
                using (GraphicsPath gear = GearPath(52, 46, 10, 7.5f, 8, 3.2f))
                    Shape(g, gear, Color.FromArgb(255, 183, 77), Color.FromArgb(239, 108, 0), Color.FromArgb(150, 70, 0), 1f);
            }
            return bmp;
        }

        /// <summary>Special functions: a wrench and a screwdriver crossed.</summary>
        public static Bitmap Tools()
        {
            Bitmap bmp = Canvas(64, 64, out Graphics g);
            using (g)
            {
                Color steelTop = Color.FromArgb(236, 239, 241), steelBottom = Color.FromArgb(120, 134, 145), steelEdge = Color.FromArgb(70, 80, 90);
                // a little bigger: the thin tools fill less of the square than the other icons
                g.TranslateTransform(32, 32);
                g.ScaleTransform(1.14f, 1.14f);
                g.TranslateTransform(-32, -32);

                // wrench, from the lower left to the upper right
                GraphicsState st = g.Save();
                g.TranslateTransform(32, 32);
                g.RotateTransform(-45);
                using (GraphicsPath shaft = Rounded(-27, -4.5f, 40, 9, 4.5f))
                    Shape(g, shaft, steelTop, steelBottom, steelEdge);
                using (var head = new GraphicsPath())
                {
                    head.AddEllipse(10, -11, 22, 22);
                    using (var region = new Region(head))
                    {
                        region.Exclude(new RectangleF(21, -4.5f, 14, 9));   // the jaw
                        using (var shadow = new SolidBrush(Color.FromArgb(55, 0, 0, 0)))
                        {
                            Region sh = region.Clone();
                            sh.Translate(1.2f, 1.8f);
                            g.FillRegion(shadow, sh);
                            sh.Dispose();
                        }
                        using (var fill = Grad(new RectangleF(10, -11, 22, 22), steelTop, steelBottom))
                            g.FillRegion(fill, region);
                    }
                    using (var pen = new Pen(steelEdge, 1.2f)) g.DrawArc(pen, 10, -11, 22, 22, 25, 310);
                }
                g.Restore(st);

                // screwdriver, from the lower right to the upper left
                st = g.Save();
                g.TranslateTransform(32, 32);
                g.RotateTransform(45);
                using (GraphicsPath blade = Rounded(-28, -2.2f, 30, 4.4f, 1.5f))
                    Shape(g, blade, steelTop, steelBottom, steelEdge, 1f);
                using (GraphicsPath tip = Rounded(-30, -3.2f, 5, 6.4f, 1))
                    Shape(g, tip, steelTop, steelBottom, steelEdge, 1f);
                using (GraphicsPath handle = Rounded(1, -6.5f, 27, 13, 6.5f))
                    Shape(g, handle, Color.FromArgb(255, 183, 77), Color.FromArgb(230, 81, 0), Color.FromArgb(140, 50, 0));
                using (var grip = new Pen(Color.FromArgb(110, 140, 50, 0), 1.4f))
                    for (int i = 0; i < 3; i++) g.DrawLine(grip, 9 + 5 * i, -4, 9 + 5 * i, 4);
                g.Restore(st);
            }
            return bmp;
        }

        /// <summary>Settings: a big slate gear with a small orange and a small blue one.</summary>
        public static Bitmap Settings()
        {
            Bitmap bmp = Canvas(64, 64, out Graphics g);
            using (g)
            {
                using (GraphicsPath big = GearPath(39, 24, 21, 16, 10, 6.5f))
                    Shape(g, big, Color.FromArgb(176, 190, 197), Color.FromArgb(84, 110, 122), Color.FromArgb(55, 71, 79));
                using (GraphicsPath orange = GearPath(19, 44, 15, 11, 8, 4.5f))
                    Shape(g, orange, Color.FromArgb(255, 183, 77), Color.FromArgb(239, 108, 0), Color.FromArgb(150, 70, 0));
                using (GraphicsPath blue = GearPath(46, 51, 10, 7.5f, 7, 3f))
                    Shape(g, blue, Color.FromArgb(100, 181, 246), Color.FromArgb(25, 118, 210), Color.FromArgb(13, 71, 161), 1f);
            }
            return bmp;
        }

        /// <summary>Field / job: a folder holding a field map, with a sprout on its front.</summary>
        public static Bitmap Job()
        {
            Bitmap bmp = Canvas(64, 64, out Graphics g);
            using (g)
            {
                Color amberTop = Color.FromArgb(255, 213, 79), amberBottom = Color.FromArgb(245, 124, 0), amberEdge = Color.FromArgb(150, 80, 0);
                // the back of the folder with its tab
                using (var back = new GraphicsPath())
                {
                    back.AddLines(new[] { new PointF(5, 16), new PointF(5, 10), new PointF(22, 10), new PointF(26, 15), new PointF(59, 15), new PointF(59, 56), new PointF(5, 56) });
                    back.CloseFigure();
                    Shape(g, back, Color.FromArgb(255, 193, 7), Color.FromArgb(200, 110, 0), amberEdge);
                }
                // the field map sticking out
                using (GraphicsPath sheet = Rounded(11, 6, 42, 30, 2))
                    Shape(g, sheet, Color.FromArgb(250, 250, 250), Color.FromArgb(220, 225, 230), Color.FromArgb(150, 155, 160), 1f);
                using (var field = new SolidBrush(Color.FromArgb(102, 187, 106)))
                    g.FillPolygon(field, new[] { new PointF(15, 30), new PointF(19, 11), new PointF(49, 11), new PointF(45, 30) });
                using (var rows = new Pen(Color.FromArgb(46, 125, 50), 1.2f))
                    for (int i = 1; i < 6; i++)
                    {
                        float k = i / 6f;   // from the near edge (y 30) to the far one (y 11)
                        g.DrawLine(rows, 15 + 4 * k, 30 - 19 * k, 45 + 4 * k, 30 - 19 * k);
                    }
                // the front of the folder
                using (var front = new GraphicsPath())
                {
                    front.AddLines(new[] { new PointF(3, 27), new PointF(61, 27), new PointF(58, 57), new PointF(6, 57) });
                    front.CloseFigure();
                    Shape(g, front, amberTop, amberBottom, amberEdge);
                }
                // a sprout on the front
                using (var stem = new Pen(Color.FromArgb(46, 125, 50), 2.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawLine(stem, 32, 52, 32, 40);
                using (var leftLeaf = new GraphicsPath())
                using (var rightLeaf = new GraphicsPath())
                {
                    leftLeaf.AddBezier(32, 43, 26, 44, 20, 40, 19, 33);
                    leftLeaf.AddBezier(19, 33, 26, 32, 31, 36, 32, 43);
                    rightLeaf.AddBezier(32, 41, 37, 41, 44, 37, 45, 30);
                    rightLeaf.AddBezier(45, 30, 38, 29, 33, 34, 32, 41);
                    Shape(g, leftLeaf, Color.FromArgb(174, 213, 129), Color.FromArgb(67, 160, 71), Color.FromArgb(27, 94, 32), 1f);
                    Shape(g, rightLeaf, Color.FromArgb(174, 213, 129), Color.FromArgb(67, 160, 71), Color.FromArgb(27, 94, 32), 1f);
                }
            }
            return bmp;
        }

        /// <summary>Field tools: a field with its boundary and headland, and a set square.</summary>
        public static Bitmap FieldTools()
        {
            Bitmap bmp = Canvas(64, 64, out Graphics g);
            using (g)
            {
                var outer = new[] { new PointF(14, 6), new PointF(56, 4), new PointF(60, 40), new PointF(40, 50), new PointF(12, 44) };
                using (var field = new GraphicsPath())
                {
                    field.AddPolygon(outer);
                    Shape(g, field, Color.FromArgb(174, 213, 129), Color.FromArgb(56, 142, 60), Color.FromArgb(27, 94, 32), 1.6f);
                }
                // the headland inside the boundary
                var inner = new PointF[outer.Length];
                for (int i = 0; i < outer.Length; i++) inner[i] = new PointF(36 + (outer[i].X - 36) * 0.72f, 27 + (outer[i].Y - 27) * 0.72f);
                using (var headland = new Pen(Color.FromArgb(240, 255, 255, 255), 1.6f) { DashStyle = DashStyle.Dash })
                    g.DrawPolygon(headland, inner);

                // set square in the lower left corner
                using (var square = new GraphicsPath(FillMode.Alternate))
                {
                    square.AddPolygon(new[] { new PointF(4, 60), new PointF(4, 24), new PointF(40, 60) });
                    square.AddPolygon(new[] { new PointF(10, 54), new PointF(10, 39), new PointF(25, 54) });
                    Shape(g, square, Color.FromArgb(255, 236, 130), Color.FromArgb(255, 179, 0), Color.FromArgb(160, 100, 0));
                }
                using (var tick = new Pen(Color.FromArgb(150, 110, 70, 0), 1f))
                    for (int i = 0; i < 6; i++) g.DrawLine(tick, 4, 30 + 5 * i, 7, 30 + 5 * i);
            }
            return bmp;
        }

        /// <summary>The steer angle display: a light rounded panel the set and actual angle are written on.</summary>
        public static Bitmap SteerPanel()
        {
            Bitmap bmp = Canvas(64, 64, out Graphics g);
            using (g)
            {
                using (GraphicsPath panel = Rounded(3, 5, 57, 53, 8))
                    Shape(g, panel, Color.FromArgb(250, 252, 253), Color.FromArgb(200, 212, 224), Color.FromArgb(96, 110, 128), 1.6f);
                using (GraphicsPath shine = Rounded(6, 8, 51, 12, 5))
                using (var light = new SolidBrush(Color.FromArgb(110, 255, 255, 255)))
                    g.FillPath(light, shine);
                // a faint steering wheel behind the numbers
                using (var wheel = new Pen(Color.FromArgb(40, 30, 60, 90), 3f))
                {
                    g.DrawEllipse(wheel, 17, 16, 30, 30);
                    g.DrawLine(wheel, 17, 31, 47, 31);
                    g.DrawLine(wheel, 32, 31, 32, 46);
                }
            }
            return bmp;
        }

        /// <summary>AgIO: a green hub linked to three coloured ports.</summary>
        public static Bitmap AgIO()
        {
            Bitmap bmp = Canvas(64, 64, out Graphics g);
            using (g)
            {
                var hub = new PointF(25, 38);
                var ports = new[] { new PointF(48, 10), new PointF(56, 33), new PointF(44, 56) };
                Color[] colours = { Color.FromArgb(30, 136, 229), Color.FromArgb(251, 140, 0), Color.FromArgb(67, 160, 71) };
                using (var link = new Pen(Color.FromArgb(56, 142, 60), 3.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    foreach (PointF p in ports) g.DrawLine(link, hub, p);
                for (int i = 0; i < ports.Length; i++)
                    using (var port = new GraphicsPath())
                    {
                        port.AddEllipse(ports[i].X - 6.5f, ports[i].Y - 6.5f, 13, 13);
                        Shape(g, port, Color.White, Color.FromArgb(225, 230, 235), colours[i], 3f);
                    }
                using (var ball = new GraphicsPath())
                {
                    ball.AddEllipse(hub.X - 17, hub.Y - 17, 34, 34);
                    Shape(g, ball, Color.FromArgb(129, 199, 132), Color.FromArgb(27, 94, 32), Color.FromArgb(20, 70, 25));
                }
                using (var glint = new SolidBrush(Color.FromArgb(120, 255, 255, 255)))
                    g.FillEllipse(glint, hub.X - 11, hub.Y - 13, 13, 9);
            }
            return bmp;
        }
    }
}
