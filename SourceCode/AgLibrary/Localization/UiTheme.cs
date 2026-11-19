using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.CompilerServices;
using System.Windows.Forms;

namespace AgLibrary.Localization
{
    /// <summary>
    /// Dark mode for the helper programs. AgOpenGPS decides: it writes its day / night mode to the registry
    /// (HKCU\SOFTWARE\AgOpenGPS, value "DayMode": "1" = day, "0" = night). The helper programs follow it and
    /// change at once when it changes (see UiTranslator, which watches the registry key).
    /// </summary>
    public static class UiTheme
    {
        private static readonly Color PageBack = Color.FromArgb(32, 34, 38);
        private static readonly Color InputBack = Color.FromArgb(52, 56, 62);
        private static readonly Color ButtonBack = Color.FromArgb(60, 64, 72);
        private static readonly Color TextColor = Color.FromArgb(230, 230, 230);
        private static readonly Color GridLine = Color.FromArgb(80, 84, 92);

        private sealed class State
        {
            public Color Back;
            public Color Fore;
            public bool Dark;
            public bool BackTouched;
            public bool ForeTouched;
            public bool UseVisual;
            public object Extra;
            public Image OrigImage;
            public Image OrigIcon;
            public Image CleanIcon;
            public bool GridLinesOff;
        }

        // ---- colour samples ----

        // In the colour windows (section colours, day / night colours) the small buttons without a picture show a
        // colour the user chose: the theme must not change it.
        private static bool IsSwatch(Control c)
        {
            var button = c as ButtonBase;
            if (button == null || button.Image != null || button.BackgroundImage != null) return false;
            Form form = c.FindForm();
            if (form == null || form.GetType().Name.IndexOf("Color", StringComparison.OrdinalIgnoreCase) < 0) return false;
            return (c.Text ?? "").Trim().Length <= 3;
        }

        // black text on a light colour, light text on a dark one
        private static void SwatchText(Control c, State st)
        {
            Color fore = Luminance(c.BackColor) >= 0.5 ? Color.FromArgb(255, 20, 20, 20) : Color.FromArgb(255, 240, 240, 240);
            if (c.ForeColor.ToArgb() != fore.ToArgb()) c.ForeColor = fore;
            st.ForeTouched = true;
        }

        private static readonly ConditionalWeakTable<Control, object> WatchedSwatches = new ConditionalWeakTable<Control, object>();

        // the user picks a new colour: the text follows it
        private static void WatchSwatch(Control c)
        {
            object known;
            if (WatchedSwatches.TryGetValue(c, out known)) return;
            WatchedSwatches.Add(c, new object());
            c.BackColorChanged += (sender, e) =>
            {
                try
                {
                    var control = (Control)sender;
                    State st;
                    if (States.TryGetValue(control, out st) && st.Dark) SwatchText(control, st);
                }
                catch (Exception)
                {
                }
            };
        }

        // ---- disabled labels ----

        private static readonly ConditionalWeakTable<Control, object> WatchedLabels = new ConditionalWeakTable<Control, object>();

        private static void WatchDisabledLabel(Label label)
        {
            object known;
            if (WatchedLabels.TryGetValue(label, out known)) return;
            WatchedLabels.Add(label, new object());
            label.Paint += PaintDisabledLabel;
            label.EnabledChanged += (s, e) => ((Control)s).Invalidate();
        }

        // over what Windows drew: the background again and the text in a calm grey that can be read
        private static void PaintDisabledLabel(object sender, PaintEventArgs e)
        {
            try
            {
                var label = (Label)sender;
                State st;
                if (label.Enabled || !States.TryGetValue(label, out st) || !st.Dark || string.IsNullOrEmpty(label.Text)) return;
                Color back = label.BackColor.A == 255 ? label.BackColor : EffectiveBack(label);
                using (var b = new SolidBrush(back)) e.Graphics.FillRectangle(b, label.ClientRectangle);
                TextFormatFlags flags = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix;
                ContentAlignment a = label.TextAlign;
                if (a == ContentAlignment.TopCenter || a == ContentAlignment.MiddleCenter || a == ContentAlignment.BottomCenter) flags |= TextFormatFlags.HorizontalCenter;
                if (a == ContentAlignment.TopRight || a == ContentAlignment.MiddleRight || a == ContentAlignment.BottomRight) flags |= TextFormatFlags.Right;
                if (a == ContentAlignment.MiddleLeft || a == ContentAlignment.MiddleCenter || a == ContentAlignment.MiddleRight) flags |= TextFormatFlags.VerticalCenter;
                if (a == ContentAlignment.BottomLeft || a == ContentAlignment.BottomCenter || a == ContentAlignment.BottomRight) flags |= TextFormatFlags.Bottom;
                Rectangle r = label.ClientRectangle;
                r = new Rectangle(r.Left + label.Padding.Left, r.Top + label.Padding.Top, r.Width - label.Padding.Horizontal, r.Height - label.Padding.Vertical);
                TextRenderer.DrawText(e.Graphics, label.Text, label.Font, r, Color.FromArgb(255, 140, 146, 156), flags);
            }
            catch (Exception)
            {
            }
        }

        // ---- dark line drawings ----

        // A drawing of black / dark grey lines on a transparent ground (the Shift and Backspace keys, plain glyphs)
        // disappears on a dark button: the same drawing in light grey. Null when the picture is anything else.
        private static readonly ConditionalWeakTable<Image, object> LightGlyphs = new ConditionalWeakTable<Image, object>();

        private static Image LightGlyph(Image src)
        {
            object cached;
            if (src == null) return null;
            if (LightGlyphs.TryGetValue(src, out cached)) return cached as Image;
            Bitmap result = null;
            try
            {
                if (src.Width * src.Height <= 1000000)
                {
                    var bmp = new Bitmap(src.Width, src.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                    using (Graphics g = Graphics.FromImage(bmp)) g.DrawImage(src, 0, 0, src.Width, src.Height);
                    var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
                    var data = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadWrite, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                    int bytes = Math.Abs(data.Stride) * data.Height;
                    byte[] px = new byte[bytes];
                    System.Runtime.InteropServices.Marshal.Copy(data.Scan0, px, 0, bytes);

                    long all = (long)data.Width * data.Height, visible = 0, darkNeutral = 0;
                    for (int y = 0; y < data.Height; y++)
                        for (int x = 0; x < data.Width; x++)
                        {
                            int i = y * data.Stride + x * 4;
                            if (px[i + 3] < 40) continue;
                            visible++;
                            int mx = Math.Max(px[i], Math.Max(px[i + 1], px[i + 2]));
                            int mn = Math.Min(px[i], Math.Min(px[i + 1], px[i + 2]));
                            if (mx - mn <= 40 && mx <= 110) darkNeutral++;
                        }

                    // mostly see-through, and what can be seen is nearly all dark grey / black
                    if (visible > 0 && visible * 100 / all <= 60 && darkNeutral * 100 / visible >= 85)
                    {
                        for (int y = 0; y < data.Height; y++)
                            for (int x = 0; x < data.Width; x++)
                            {
                                int i = y * data.Stride + x * 4;
                                if (px[i + 3] == 0) continue;
                                int mx = Math.Max(px[i], Math.Max(px[i + 1], px[i + 2]));
                                int mn = Math.Min(px[i], Math.Min(px[i + 1], px[i + 2]));
                                if (mx - mn > 40) continue;   // coloured parts stay
                                byte v = (byte)Math.Min(255, 225 - (px[i] + px[i + 1] + px[i + 2]) / 12);
                                px[i] = px[i + 1] = px[i + 2] = v;
                            }
                        System.Runtime.InteropServices.Marshal.Copy(px, 0, data.Scan0, bytes);
                        bmp.UnlockBits(data);
                        result = bmp;
                    }
                    else
                    {
                        bmp.UnlockBits(data);
                        bmp.Dispose();
                    }
                }
            }
            catch (Exception)
            {
                result = null;
            }
            LightGlyphs.Add(src, result);
            return result;
        }

        // Whether a background picture fills the whole control (tiled, stretched or zoomed, or simply as large).
        private static bool PictureCovers(Control c)
        {
            if (c is PictureBox || c.BackgroundImage == null) return true;
            switch (c.BackgroundImageLayout)
            {
                case ImageLayout.Tile:
                case ImageLayout.Stretch:
                case ImageLayout.Zoom:
                    return true;
                default:
                    return c.BackgroundImage.Width >= c.ClientSize.Width && c.BackgroundImage.Height >= c.ClientSize.Height;
            }
        }

        private sealed class GridState
        {
            public Color Background;
            public Color Line;
            public Color CellBack;
            public Color CellFore;
        }

        // Windows draws a scroll bar control (the sliders of the settings windows) light whatever theme it is given.
        // In night mode a dark slider is laid exactly over it: it is drawn here and moves the real scroll bar, whose
        // Value, ValueChanged and Scroll the program goes on using as before.
        private sealed class DarkScrollBar : Control
        {
            private readonly ScrollBar bar;
            private readonly bool horizontal;
            private readonly Timer repeat = new Timer();
            private int repeatStep;
            private ScrollEventType repeatType;
            private bool dragging;
            private int dragOffset;
            private bool hoverThumb;

            private static readonly Color Track = Color.FromArgb(43, 47, 54);
            private static readonly Color ArrowBack = Color.FromArgb(60, 64, 72);
            private static readonly Color ArrowMark = Color.FromArgb(205, 208, 214);
            private static readonly Color Thumb = Color.FromArgb(112, 120, 134);
            private static readonly Color ThumbHot = Color.FromArgb(138, 146, 160);
            private static readonly Color Frame = Color.FromArgb(96, 100, 110);

            public DarkScrollBar(ScrollBar bar)
            {
                this.bar = bar;
                horizontal = bar is HScrollBar;
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                         ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
                TabStop = false;
                Follow();
                bar.Parent.Controls.Add(this);
                BringToFront();
                bar.ValueChanged += Changed;
                bar.LocationChanged += Moved;
                bar.SizeChanged += Moved;
                bar.VisibleChanged += Moved;
                bar.EnabledChanged += Changed;
                bar.ParentChanged += Reparent;
                repeat.Tick += (s, e) => { repeat.Interval = 60; Step(repeatStep, repeatType); };
            }

            public void Detach()
            {
                bar.ValueChanged -= Changed;
                bar.LocationChanged -= Moved;
                bar.SizeChanged -= Moved;
                bar.VisibleChanged -= Moved;
                bar.EnabledChanged -= Changed;
                bar.ParentChanged -= Reparent;
                repeat.Stop();
                repeat.Dispose();
                if (Parent != null) Parent.Controls.Remove(this);
                Dispose();
            }

            private void Follow()
            {
                Bounds = bar.Bounds;
                Anchor = bar.Anchor;
                Visible = bar.Visible;
            }

            private void Changed(object sender, EventArgs e) { Invalidate(); }

            private void Moved(object sender, EventArgs e) { Follow(); if (Visible) BringToFront(); }

            private void Reparent(object sender, EventArgs e)
            {
                if (Parent != null) Parent.Controls.Remove(this);
                if (bar.Parent != null) { bar.Parent.Controls.Add(this); Follow(); BringToFront(); }
            }

            private int Length { get { return horizontal ? Width : Height; } }
            private int Thick { get { return horizontal ? Height : Width; } }
            private int Arrow { get { return Math.Min(Thick, Length / 3); } }
            private int Range { get { return Math.Max(0, bar.Maximum - bar.LargeChange + 1 - bar.Minimum); } }

            // where the thumb is, along the bar
            private void ThumbSpan(out int start, out int size)
            {
                int track = Length - 2 * Arrow;
                int all = Math.Max(1, bar.Maximum - bar.Minimum + 1);
                size = Math.Max(Math.Min(track, 18), track * Math.Max(1, bar.LargeChange) / all);
                size = Math.Min(size, track);
                int range = Range;
                start = Arrow + (range == 0 ? 0 : (track - size) * (bar.Value - bar.Minimum) / range);
            }

            private Rectangle Along(int start, int size)
            {
                return horizontal ? new Rectangle(start, 0, size, Height) : new Rectangle(0, start, Width, size);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (var b = new SolidBrush(Track)) g.FillRectangle(b, ClientRectangle);

                int a = Arrow;
                DrawArrow(g, Along(0, a), true);
                DrawArrow(g, Along(Length - a, a), false);

                if (bar.Enabled && Range > 0)
                {
                    int start, size;
                    ThumbSpan(out start, out size);
                    Rectangle t = Along(start, size);
                    t.Inflate(horizontal ? -1 : -4, horizontal ? -4 : -1);
                    using (var path = Rounded(t, Math.Min(6, Math.Min(t.Width, t.Height) / 2)))
                    using (var b = new SolidBrush(dragging || hoverThumb ? ThumbHot : Thumb))
                        g.FillPath(b, path);
                }
                using (var p = new Pen(Frame)) g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
            }

            private void DrawArrow(Graphics g, Rectangle r, bool first)
            {
                using (var b = new SolidBrush(ArrowBack)) g.FillRectangle(b, r);
                int cx = r.Left + r.Width / 2, cy = r.Top + r.Height / 2;
                int s = Math.Max(3, Math.Min(r.Width, r.Height) / 5);
                Point[] tri;
                if (horizontal)
                    tri = first ? new[] { new Point(cx - s / 2 - 1, cy), new Point(cx + s / 2 + 1, cy - s), new Point(cx + s / 2 + 1, cy + s) }
                                : new[] { new Point(cx + s / 2 + 1, cy), new Point(cx - s / 2 - 1, cy - s), new Point(cx - s / 2 - 1, cy + s) };
                else
                    tri = first ? new[] { new Point(cx, cy - s / 2 - 1), new Point(cx - s, cy + s / 2 + 1), new Point(cx + s, cy + s / 2 + 1) }
                                : new[] { new Point(cx, cy + s / 2 + 1), new Point(cx - s, cy - s / 2 - 1), new Point(cx + s, cy - s / 2 - 1) };
                using (var b = new SolidBrush(bar.Enabled ? ArrowMark : Color.FromArgb(110, 114, 122))) g.FillPolygon(b, tri);
            }

            private static System.Drawing.Drawing2D.GraphicsPath Rounded(Rectangle r, int radius)
            {
                var path = new System.Drawing.Drawing2D.GraphicsPath();
                int d = Math.Max(1, radius * 2);
                path.AddArc(r.Left, r.Top, d, d, 180, 90);
                path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
                path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
                path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
                path.CloseFigure();
                return path;
            }

            private int Along(MouseEventArgs e) { return horizontal ? e.X : e.Y; }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e);
                if (!bar.Enabled || e.Button != MouseButtons.Left) return;
                int p = Along(e), a = Arrow, start, size;
                ThumbSpan(out start, out size);
                if (p < a) Press(-bar.SmallChange, ScrollEventType.SmallDecrement);
                else if (p >= Length - a) Press(bar.SmallChange, ScrollEventType.SmallIncrement);
                else if (p < start) Press(-bar.LargeChange, ScrollEventType.LargeDecrement);
                else if (p >= start + size) Press(bar.LargeChange, ScrollEventType.LargeIncrement);
                else { dragging = true; dragOffset = p - start; Capture = true; Invalidate(); }
            }

            private void Press(int step, ScrollEventType type)
            {
                repeatStep = step;
                repeatType = type;
                Step(step, type);
                repeat.Interval = 400;
                repeat.Start();
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e);
                int start, size;
                ThumbSpan(out start, out size);
                bool hot = Along(e) >= start && Along(e) < start + size;
                if (hot != hoverThumb) { hoverThumb = hot; Invalidate(); }
                if (!dragging) return;
                int track = Length - 2 * Arrow - size;
                if (track <= 0) return;
                int pos = Math.Max(0, Math.Min(track, Along(e) - dragOffset - Arrow));
                SetValue(bar.Minimum + (int)Math.Round((double)pos * Range / track), ScrollEventType.ThumbTrack);
            }

            protected override void OnMouseUp(MouseEventArgs e)
            {
                base.OnMouseUp(e);
                repeat.Stop();
                if (dragging)
                {
                    dragging = false;
                    Capture = false;
                    RaiseScroll(ScrollEventType.ThumbPosition, bar.Value, bar.Value);
                    Invalidate();
                }
                RaiseScroll(ScrollEventType.EndScroll, bar.Value, bar.Value);
            }

            protected override void OnMouseLeave(EventArgs e)
            {
                base.OnMouseLeave(e);
                if (hoverThumb) { hoverThumb = false; Invalidate(); }
            }

            protected override void OnMouseWheel(MouseEventArgs e)
            {
                base.OnMouseWheel(e);
                if (!bar.Enabled) return;
                int notches = e.Delta / 120;
                if (notches != 0) Step(-notches * bar.SmallChange, notches > 0 ? ScrollEventType.SmallDecrement : ScrollEventType.SmallIncrement);
            }

            private void Step(int step, ScrollEventType type)
            {
                SetValue(bar.Value + step, type);
            }

            private void SetValue(int value, ScrollEventType type)
            {
                int max = bar.Minimum + Range;
                value = Math.Max(bar.Minimum, Math.Min(max, value));
                int old = bar.Value;
                if (value == old) return;
                bar.Value = value;                  // raises ValueChanged
                RaiseScroll(type, old, value);      // and Scroll, as a real move of the bar would
            }

            private static readonly System.Reflection.MethodInfo OnScrollMethod = typeof(ScrollBar).GetMethod("OnScroll",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, null, new[] { typeof(ScrollEventArgs) }, null);

            private void RaiseScroll(ScrollEventType type, int old, int value)
            {
                try
                {
                    var args = new ScrollEventArgs(type, old, value, horizontal ? ScrollOrientation.HorizontalScroll : ScrollOrientation.VerticalScroll);
                    OnScrollMethod?.Invoke(bar, new object[] { args });
                    // a handler may change the value it was given
                    if (args.NewValue != value && args.NewValue >= bar.Minimum && args.NewValue <= bar.Maximum) bar.Value = args.NewValue;
                }
                catch (Exception)
                {
                }
            }
        }

        private static readonly ConditionalWeakTable<ScrollBar, DarkScrollBar> DarkBars = new ConditionalWeakTable<ScrollBar, DarkScrollBar>();

        private static void DarkSlider(ScrollBar bar, bool night)
        {
            try
            {
                DarkScrollBar overlay;
                bool has = DarkBars.TryGetValue(bar, out overlay);
                if (night && !has && bar.Parent != null) DarkBars.Add(bar, new DarkScrollBar(bar));
                else if (!night && has)
                {
                    DarkBars.Remove(bar);
                    overlay.Detach();
                }
            }
            catch (Exception)
            {
                // without the dark slider the light one stays: still usable
            }
        }

        // Controls the program adds to a window after it was themed (a tab page put back, a panel filled later)
        // get the same look: every container of a themed window is watched for new children.
        private static readonly ConditionalWeakTable<Control, object> Watched = new ConditionalWeakTable<Control, object>();

        private static void WatchChildren(Control c)
        {
            object known;
            if (c is DarkScrollBar || Watched.TryGetValue(c, out known)) return;
            Watched.Add(c, new object());
            c.ControlAdded += (sender, e) =>
            {
                try
                {
                    var parent = (Control)sender;
                    State ps;
                    if (e.Control is DarkScrollBar || !States.TryGetValue(parent, out ps) || !ps.Dark) return;
                    Capture(e.Control);
                    Walk(e.Control, true);
                }
                catch (Exception)
                {
                }
            };
        }

        // paints the empty part of the tab row (right of the last tab) dark after Windows has drawn the control
        private sealed class StripPainter : NativeWindow
        {
            private readonly TabControl tabs;

            public StripPainter(TabControl tabs)
            {
                this.tabs = tabs;
            }

            protected override void WndProc(ref Message m)
            {
                base.WndProc(ref m);
                if (m.Msg != 0x000F || tabs.TabCount == 0) return; // WM_PAINT
                try
                {
                    Rectangle first = tabs.GetTabRect(0);
                    Rectangle last = tabs.GetTabRect(tabs.TabCount - 1);
                    int top = first.Y;
                    int bottom = first.Bottom + 2;
                    for (int i = 0; i < tabs.TabCount; i++) bottom = Math.Max(bottom, tabs.GetTabRect(i).Bottom + 2);
                    using (Graphics g = Graphics.FromHwnd(tabs.Handle))
                    using (var brush = new SolidBrush(PageBack))
                    using (var line = new Pen(GridLine))
                    {
                        if (last.Right < tabs.Width) g.FillRectangle(brush, last.Right, 0, tabs.Width - last.Right, bottom);
                        if (tabs.Appearance != TabAppearance.Normal) return;

                        // Windows draws light 3D edges round every tab and round the page: cover them with the page
                        // colour, then a thin dark frame takes their place
                        using (var edge = new Pen(PageBack, 3))
                        {
                            for (int i = 0; i < tabs.TabCount; i++)
                            {
                                Rectangle t = tabs.GetTabRect(i);
                                g.DrawRectangle(edge, t.X - 1, t.Y - 1, t.Width + 1, t.Height + 1);
                            }
                            Rectangle d = tabs.DisplayRectangle;
                            Rectangle frame = Rectangle.Inflate(d, 3, 3);
                            frame.Intersect(new Rectangle(0, 0, tabs.Width, tabs.Height));
                            g.DrawRectangle(edge, frame.X + 1, frame.Y + 1, frame.Width - 3, frame.Height - 3);
                            g.DrawRectangle(line, frame.X, frame.Y, frame.Width - 1, frame.Height - 1);
                        }
                        for (int i = 0; i < tabs.TabCount; i++)
                        {
                            Rectangle t = tabs.GetTabRect(i);
                            g.DrawRectangle(line, t.X, t.Y, t.Width - 1, t.Height - 1);
                        }
                    }
                }
                catch (Exception)
                {
                }
            }
        }

        // tab icons come from an old 8 bit image list: light fringes round the icons are removed
        private static readonly ConditionalWeakTable<ImageList, Dictionary<int, Bitmap>> TabIcons = new ConditionalWeakTable<ImageList, Dictionary<int, Bitmap>>();

        private static Bitmap CleanIcon(ImageList list, int index)
        {
            Dictionary<int, Bitmap> cache = TabIcons.GetOrCreateValue(list);
            Bitmap done;
            if (cache.TryGetValue(index, out done)) return done;

            Bitmap result = null;
            try
            {
                using (Image src = list.Images[index])
                {
                    var bmp = new Bitmap(src.Width, src.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                    using (Graphics g = Graphics.FromImage(bmp)) g.DrawImage(src, 0, 0, src.Width, src.Height);
                    int w = bmp.Width, h = bmp.Height;
                    var data = bmp.LockBits(new Rectangle(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.ReadWrite, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                    int stride = data.Stride;
                    byte[] px = new byte[Math.Abs(stride) * h];
                    System.Runtime.InteropServices.Marshal.Copy(data.Scan0, px, 0, px.Length);

                    for (int pass = 0; pass < 2; pass++)
                    {
                        var kill = new List<int>();
                        for (int y = 0; y < h; y++)
                        {
                            for (int x = 0; x < w; x++)
                            {
                                int i = y * stride + x * 4;
                                if (px[i + 3] < 128) continue;
                                int mx = Math.Max(px[i], Math.Max(px[i + 1], px[i + 2]));
                                int mn = Math.Min(px[i], Math.Min(px[i + 1], px[i + 2]));
                                if (mx - mn > 40 || (px[i] + px[i + 1] + px[i + 2]) / 3 < 170) continue; // only light, neutral pixels
                                bool edge = x == 0 || y == 0 || x == w - 1 || y == h - 1
                                    || px[i - 4 + 3] < 128 || px[i + 4 + 3] < 128 || px[i - stride + 3] < 128 || px[i + stride + 3] < 128;
                                if (edge) kill.Add(i);
                            }
                        }
                        foreach (int i in kill) px[i + 3] = 0;
                    }

                    // the icons are drawn for a light tab (dark teal, brown, black lines): on the dark tab they
                    // disappear. Every colour is lifted to a lighter shade of itself, light parts stay as they are.
                    for (int y = 0; y < h; y++)
                    {
                        for (int x = 0; x < w; x++)
                        {
                            int i = y * stride + x * 4;
                            if (px[i + 3] == 0) continue;
                            Color c = Color.FromArgb(px[i + 2], px[i + 1], px[i]);
                            float l = c.GetBrightness();
                            float lifted = 0.30f + 0.70f * (float)Math.Pow(l, 0.8);
                            if (lifted <= l) continue;
                            // colours a little stronger too, so the lifted shade does not look washed out
                            Color n = FromHsl(c.GetHue(), Math.Min(1f, c.GetSaturation() * 1.15f), lifted);
                            px[i] = n.B;
                            px[i + 1] = n.G;
                            px[i + 2] = n.R;
                        }
                    }

                    System.Runtime.InteropServices.Marshal.Copy(px, 0, data.Scan0, px.Length);
                    bmp.UnlockBits(data);
                    result = bmp;
                }
            }
            catch (Exception)
            {
                result = null;
            }

            cache[index] = result;
            return result;
        }

        private static System.Drawing.Drawing2D.GraphicsPath RoundedRect(Rectangle r, int radius)
        {
            int d = radius * 2;
            var p = new System.Drawing.Drawing2D.GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        private sealed class TabState
        {
            public TabDrawMode Mode;
            public DrawItemEventHandler Draw;
            public StripPainter Painter;
            public EventHandler Created;
            public EventHandler Destroyed;
        }

        private sealed class GroupState
        {
            public FlatStyle Style;
            public PaintEventHandler Paint;
        }

        private sealed class FlatState
        {
            public Color Checked;
            public Color Over;
            public Color Down;
            public bool CheckedTouched;
            public bool HoverTouched;
            public bool BorderTouched;
            public FlatStyle? Style;
            public Color Border;
            public PaintEventHandler Paint;
        }

        private static readonly ConditionalWeakTable<Image, object> HazeCache = new ConditionalWeakTable<Image, object>();
        private sealed class NoHaze { }

        // very faint, light, neutral pixels (the soft grey halo some icons carry) are removed; null when there is nothing to clean
        private static Image HazeFree(Image source)
        {
            try
            {
                object cached;
                if (HazeCache.TryGetValue(source, out cached)) return cached as Image;
                Image result = null;
                if (source.Width * source.Height <= 65536 && source.Width > 0 && source.Height > 0)
                {
                    var bmp = new Bitmap(source.Width, source.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                    using (var g = Graphics.FromImage(bmp)) g.DrawImage(source, 0, 0, source.Width, source.Height);
                    var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), System.Drawing.Imaging.ImageLockMode.ReadWrite, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                    int bytes = Math.Abs(data.Stride) * bmp.Height;
                    var px = new byte[bytes];
                    System.Runtime.InteropServices.Marshal.Copy(data.Scan0, px, 0, bytes);
                    int changed = 0;
                    for (int y = 0; y < bmp.Height; y++)
                    {
                        for (int x = 0; x < bmp.Width; x++)
                        {
                            int i = y * data.Stride + x * 4;
                            int a = px[i + 3];
                            if (a == 0 || a > 40) continue;
                            int b = px[i], gr = px[i + 1], r = px[i + 2];
                            int min = Math.Min(b, Math.Min(gr, r)), max = Math.Max(b, Math.Max(gr, r));
                            if (min >= 150 && max - min <= 30)
                            {
                                px[i] = px[i + 1] = px[i + 2] = px[i + 3] = 0;
                                changed++;
                            }
                        }
                    }
                    System.Runtime.InteropServices.Marshal.Copy(px, 0, data.Scan0, bytes);
                    bmp.UnlockBits(data);
                    if (changed > 0) result = bmp; else bmp.Dispose();
                }
                HazeCache.Add(source, result != null ? (object)result : new NoHaze());
                return result;
            }
            catch (Exception)
            {
                return null;
            }
        }

        // Windows draws the text of a disabled flat button in a dark shade that disappears on the dark colours:
        // such a button is painted again completely (colour, border, icon, light grey text)
        private static void PaintDisabledFlat(object sender, PaintEventArgs e)
        {
            var b = sender as ButtonBase;
            if (b == null || b.Enabled || !IsNight()) return;
            var check = b as CheckBox;
            Color back = b.BackColor;
            if (check != null && check.Checked && b.FlatAppearance.CheckedBackColor.A == 255) back = b.FlatAppearance.CheckedBackColor;
            Rectangle r = b.ClientRectangle;
            using (var br = new SolidBrush(back)) e.Graphics.FillRectangle(br, r);
            if (b.FlatAppearance.BorderSize > 0)
            {
                Color bc = b.FlatAppearance.BorderColor.IsEmpty ? Color.FromArgb(110, 114, 122) : b.FlatAppearance.BorderColor;
                using (var pen = new Pen(bc)) e.Graphics.DrawRectangle(pen, 0, 0, r.Width - 1, r.Height - 1);
            }
            int padL = b.Padding.Left, padR = b.Padding.Right;
            int imageRight = -1, imageLeft = -1;
            string textAlign = b.TextAlign.ToString();
            if (b.Image != null)
            {
                string ia = b.ImageAlign.ToString();
                int x = ia.EndsWith("Right") ? r.Width - b.Image.Width - padR - 3 : ia.EndsWith("Center") ? (r.Width - b.Image.Width) / 2 : padL + 3;
                int y = ia.StartsWith("Top") ? 3 : ia.StartsWith("Bottom") ? r.Height - b.Image.Height - 3 : (r.Height - b.Image.Height) / 2;
                // a disabled button shows its icon faded, as Windows does: a bright icon must not look clickable
                using (var faded = new System.Drawing.Imaging.ImageAttributes())
                {
                    faded.SetColorMatrix(new System.Drawing.Imaging.ColorMatrix { Matrix33 = 0.35f });
                    e.Graphics.DrawImage(b.Image, new Rectangle(x, y, b.Image.Width, b.Image.Height), 0, 0, b.Image.Width, b.Image.Height, GraphicsUnit.Pixel, faded);
                }
                // the caption stays beside an icon on the side, never across it: either because the button says so
                // (image before text) or because icon and text are aligned to opposite sides in the same row
                // (an icon on the left, a caption on the right: a longer translation would run over the icon)
                bool sameRow = ia.StartsWith("Middle") || textAlign.StartsWith("Middle") ||
                    (ia.StartsWith("Top") && textAlign.StartsWith("Top")) || (ia.StartsWith("Bottom") && textAlign.StartsWith("Bottom"));
                if (ia.EndsWith("Left") && (b.TextImageRelation == TextImageRelation.ImageBeforeText || (sameRow && !textAlign.EndsWith("Left"))))
                    imageRight = x + b.Image.Width + 3;
                if (ia.EndsWith("Right") && (b.TextImageRelation == TextImageRelation.TextBeforeImage || (sameRow && !textAlign.EndsWith("Right"))))
                    imageLeft = x - 3;
            }
            if (!string.IsNullOrEmpty(b.Text))
            {
                TextFormatFlags tf = TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis;
                string al = textAlign;
                tf |= al.EndsWith("Right") ? TextFormatFlags.Right : al.EndsWith("Left") ? TextFormatFlags.Left : TextFormatFlags.HorizontalCenter;
                // the caption keeps its place (a "Load" under its icon stays under it, not across the middle of it)
                tf |= al.StartsWith("Top") ? TextFormatFlags.Top : al.StartsWith("Bottom") ? TextFormatFlags.Bottom : TextFormatFlags.VerticalCenter;
                int left = imageRight >= 0 ? imageRight : padL + 3;
                int right = imageLeft >= 0 ? imageLeft : r.Width - padR - 3;
                var tr = new Rectangle(left, 3, Math.Max(10, right - left), Math.Max(10, r.Height - 6));
                // a word wider than the button is written smaller rather than cut off
                string text = b.Text.Trim();
                Font font = b.Font;
                Font smaller = null;
                try
                {
                    float min = font.Size * 0.6f;
                    for (float size = font.Size; size >= min; size -= 0.5f)
                    {
                        Font f = size == font.Size ? font : new Font(font.FontFamily, size, font.Style, font.Unit);
                        bool fits = true;
                        foreach (string word in text.Split(new[] { ' ', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                            if (TextRenderer.MeasureText(e.Graphics, word, f, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPrefix).Width > tr.Width) fits = false;
                        // and all its lines must fit in the height too
                        if (fits && TextRenderer.MeasureText(e.Graphics, text, f, new Size(tr.Width, int.MaxValue), TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak).Height > tr.Height) fits = false;
                        if (!ReferenceEquals(f, font))
                        {
                            if (smaller != null) smaller.Dispose();
                            smaller = f;
                        }
                        if (fits) break;
                    }
                    TextRenderer.DrawText(e.Graphics, text, smaller ?? font, tr, Color.FromArgb(150, 154, 162), tf);
                }
                finally
                {
                    if (smaller != null) smaller.Dispose();
                }
            }
        }

        private sealed class StripState
        {
            public ToolStripRenderer Renderer;
            public ToolStripRenderMode Mode;
            public List<KeyValuePair<ToolStripItem, Color>> Items = new List<KeyValuePair<ToolStripItem, Color>>();
        }

        private static readonly ConditionalWeakTable<Control, State> States = new ConditionalWeakTable<Control, State>();

        [System.Runtime.InteropServices.DllImport("uxtheme.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern int SetWindowTheme(IntPtr hwnd, string subAppName, string subIdList);

        // dark scroll bars (the sliders of the settings windows are scroll bars)
        private static void ScrollBarTheme(Control c, bool dark)
        {
            try
            {
                if (c.IsHandleCreated) ApplyScrollTheme(c, dark);
                else if (dark)
                {
                    EventHandler handler = null;
                    handler = (sender, e) =>
                    {
                        c.HandleCreated -= handler;
                        State state;
                        if (States.TryGetValue(c, out state) && state.Dark) ApplyScrollTheme(c, true);
                    };
                    c.HandleCreated += handler;
                }
            }
            catch (Exception)
            {
            }
        }

        private static void ApplyScrollTheme(Control c, bool dark)
        {
            if (c is ComboBox) SetWindowTheme(c.Handle, dark ? "DarkMode_CFD" : null, dark ? "Combobox" : null);
            else if (c is ProgressBar) SetWindowTheme(c.Handle, dark ? "" : null, dark ? "" : null);
            else SetWindowTheme(c.Handle, dark ? "DarkMode_Explorer" : null, null);
        }

        // ---- the column header of a details list ----

        private sealed class HeaderState
        {
            public bool OwnerDraw;
            public DrawListViewColumnHeaderEventHandler Header;
            public DrawListViewItemEventHandler Item;
            public DrawListViewSubItemEventHandler SubItem;
            public HeaderPainter Painter;
            public EventHandler Created;
        }

        private static readonly ConditionalWeakTable<ListView, HeaderState> DarkHeaders = new ConditionalWeakTable<ListView, HeaderState>();

        // The header of a details list is white with black text in the system look, and the system's own dark list
        // theme only works for programs that opt into an undocumented dark mode. At night the list draws its header
        // itself (dark, light text, thin lines) and lets Windows draw the rows as before; the part of the header right
        // of the last column is painted dark too.
        private static void DarkListHeader(ListView list, bool dark)
        {
            try
            {
                HeaderState hs;
                bool has = DarkHeaders.TryGetValue(list, out hs);
                // a list that draws itself already is left as it is: its own drawing must not be replaced
                if (dark && !has && !list.OwnerDraw && list.View == View.Details && list.HeaderStyle != ColumnHeaderStyle.None)
                {
                    hs = new HeaderState { OwnerDraw = list.OwnerDraw };
                    hs.Header = (s, e) =>
                    {
                        var lv = (ListView)s;
                        using (var back = new SolidBrush(Color.FromArgb(44, 48, 56))) e.Graphics.FillRectangle(back, e.Bounds);
                        using (var line = new Pen(GridLine))
                        {
                            e.Graphics.DrawLine(line, e.Bounds.Right - 1, e.Bounds.Top + 3, e.Bounds.Right - 1, e.Bounds.Bottom - 4);
                            e.Graphics.DrawLine(line, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
                        }
                        TextFormatFlags align = e.Header.TextAlign == HorizontalAlignment.Right ? TextFormatFlags.Right
                            : e.Header.TextAlign == HorizontalAlignment.Center ? TextFormatFlags.HorizontalCenter : TextFormatFlags.Left;
                        var r = Rectangle.Inflate(e.Bounds, -6, 0);
                        TextRenderer.DrawText(e.Graphics, e.Header.Text, e.Font ?? lv.Font, r, TextColor,
                            align | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
                    };
                    hs.Item = (s, e) => e.DrawDefault = true;
                    hs.SubItem = (s, e) => e.DrawDefault = true;
                    hs.Painter = new HeaderPainter(list);
                    hs.Created = (s, e) => hs.Painter.Attach();
                    list.DrawColumnHeader += hs.Header;
                    list.DrawItem += hs.Item;
                    list.DrawSubItem += hs.SubItem;
                    list.HandleCreated += hs.Created;
                    list.OwnerDraw = true;
                    hs.Painter.Attach();
                    DarkHeaders.Add(list, hs);
                    list.Invalidate();
                }
                else if (!dark && has)
                {
                    list.DrawColumnHeader -= hs.Header;
                    list.DrawItem -= hs.Item;
                    list.DrawSubItem -= hs.SubItem;
                    list.HandleCreated -= hs.Created;
                    hs.Painter.Detach();
                    list.OwnerDraw = hs.OwnerDraw;
                    DarkHeaders.Remove(list);
                    list.Invalidate();
                }
            }
            catch (Exception)
            {
            }
        }

        // paints the header strip right of the last column, which the header control fills with its light colour
        private sealed class HeaderPainter : NativeWindow
        {
            private readonly ListView list;

            public HeaderPainter(ListView list)
            {
                this.list = list;
            }

            public void Attach()
            {
                if (!list.IsHandleCreated) return;
                IntPtr header = SendMessage(list.Handle, 0x101F, IntPtr.Zero, IntPtr.Zero);   // LVM_GETHEADER
                if (header == IntPtr.Zero || header == Handle) return;
                if (Handle != IntPtr.Zero) ReleaseHandle();
                AssignHandle(header);
            }

            public void Detach()
            {
                if (Handle != IntPtr.Zero) ReleaseHandle();
            }

            protected override void WndProc(ref Message m)
            {
                base.WndProc(ref m);
                if (m.Msg != 0x000F) return;   // WM_PAINT
                try
                {
                    int right = 0;
                    foreach (ColumnHeader column in list.Columns) right += column.Width;
                    RECT r;
                    if (!GetClientRect(Handle, out r)) return;
                    if (right >= r.Right) return;
                    using (Graphics g = Graphics.FromHwnd(Handle))
                    using (var back = new SolidBrush(Color.FromArgb(44, 48, 56)))
                    using (var line = new Pen(GridLine))
                    {
                        g.FillRectangle(back, right, 0, r.Right - right, r.Bottom);
                        g.DrawLine(line, right, r.Bottom - 1, r.Right, r.Bottom - 1);
                    }
                }
                catch (Exception)
                {
                }
            }
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr hWnd, out RECT rect);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        /// <summary>Menu button colour for the selected entry (day: system light blue).</summary>
        public static Color MenuActive { get { return IsNight() ? Color.FromArgb(255, 58, 64, 76) : SystemColors.GradientActiveCaption; } }

        /// <summary>Menu button colour for the other entries.</summary>
        public static Color MenuInactive { get { return IsNight() ? PageBack : SystemColors.GradientInactiveCaption; } }

        /// <summary>True when AgOpenGPS is in night mode.</summary>
        /// <remarks>Called while painting, so the registry is read at most once a second; the watcher of the key
        /// (UiTranslator) reads it again at once whenever it changes.</remarks>
        public static bool IsNight()
        {
            int now = Environment.TickCount;
            if (nightKnown && unchecked(now - nightReadAt) < 1000) return night;
            return ReadNight();
        }

        private static volatile bool night;
        private static volatile bool nightKnown;
        private static int nightReadAt;

        /// <summary>Reads the mode from the registry now (and keeps it for IsNight).</summary>
        internal static bool ReadNight()
        {
            bool value = false;
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"SOFTWARE\AgOpenGPS"))
                {
                    object v = key?.GetValue("DayMode");
                    value = v != null && v.ToString() == "0";
                }
            }
            catch (Exception)
            {
                value = false;
            }
            night = value;
            nightReadAt = Environment.TickCount;
            nightKnown = true;
            return value;
        }

        // which modes windows were themed with since the last refresh of all windows (1 = day, 2 = night)
        private static int themedModes;

        private static void NoteThemed(bool isNight)
        {
            int bit = isNight ? 2 : 1;
            int seen;
            do { seen = themedModes; }
            while ((seen & bit) == 0 && System.Threading.Interlocked.CompareExchange(ref themedModes, seen | bit, seen) != seen);
        }

        /// <summary>True when a window has been themed with the mode other than the given one since the last refresh.</summary>
        internal static bool ThemedWithOtherMode(bool isNight)
        {
            return (System.Threading.Volatile.Read(ref themedModes) & (isNight ? 1 : 2)) != 0;
        }

        /// <summary>Applies the current mode to every open window (called when the registry key changes).</summary>
        public static void Refresh()
        {
            try
            {
                // every window gets the mode of now: what was themed before no longer counts
                System.Threading.Interlocked.Exchange(ref themedModes, 0);
                SetMenuRenderer(ReadNight());
                foreach (Form form in Application.OpenForms) Apply(form);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>Applies the current mode to one window (called before a window is shown).</summary>
        public static void Apply(Form form)
        {
            if (form == null || form.IsDisposed) return;
            // not the value kept for painting: a window is themed with the mode of this very moment
            bool nightNow = ReadNight();
            NoteThemed(nightNow);
            SetMenuRenderer(nightNow);
            // the main window of AgOpenGPS paints its own day / night look: only its menus follow
            if (form.GetType().Name == "FormGPS")
            {
                Modernize(form);
                ThemeMenus(form, nightNow);
                ThemeMainPanel(form, nightNow);
                return;
            }
            try
            {
                Capture(form);
                Walk(form, nightNow);
                TitleBar(form, nightNow);
                form.Invalidate(true); // controls that paint themselves read the mode while painting
            }
            catch (Exception)
            {
                // never let the theme disturb the program
            }
        }

        // the light button panel on the left of the main window of AgOpenGPS (2D / 3D, day / night, brightness ...)
        private static readonly ConditionalWeakTable<Control, object> ShownPanels = new ConditionalWeakTable<Control, object>();
        private static void ThemeMainPanel(Form form, bool night)
        {
            try
            {
                Control[] found = form.Controls.Find("panelNavigation", true);
                foreach (Control panel in found)
                {
                    Capture(panel);
                    Walk(panel, night);
                    // the panel is hidden until its button is pressed: when it appears it takes the mode of that moment
                    object known;
                    if (!ShownPanels.TryGetValue(panel, out known))
                    {
                        ShownPanels.Add(panel, new object());
                        panel.VisibleChanged += (sender, e) =>
                        {
                            try
                            {
                                var p = (Control)sender;
                                if (!p.Visible) return;
                                Capture(p);
                                Walk(p, IsNight());
                            }
                            catch (Exception)
                            {
                            }
                        };
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        private static void ThemeMenus(Control root, bool night)
        {
            try
            {
                var strip = root as ToolStrip;
                if (strip != null)
                {
                    if (night && !(strip.Renderer is DarkRenderer))
                    {
                        // remember how the strip drew itself, so day brings back exactly that look
                        // (the status strips of the left bar draw with the system renderer, not the manager's)
                        OrigStrip known;
                        if (!OrigStrips.TryGetValue(strip, out known))
                            OrigStrips.Add(strip, new OrigStrip { Mode = strip.RenderMode, Renderer = strip.Renderer });
                        strip.Renderer = new DarkRenderer();
                    }
                    else if (!night && strip.Renderer is DarkRenderer)
                    {
                        OrigStrip orig;
                        if (!OrigStrips.TryGetValue(strip, out orig)) strip.RenderMode = ToolStripRenderMode.ManagerRenderMode;
                        else if (orig.Mode != ToolStripRenderMode.Custom) strip.RenderMode = orig.Mode;
                        else strip.Renderer = orig.Renderer;
                        strip.Invalidate(true);
                    }
                }
                foreach (Control child in root.Controls) ThemeMenus(child, night);
            }
            catch (Exception)
            {
            }
        }

        private sealed class OrigStrip
        {
            public ToolStripRenderMode Mode;
            public ToolStripRenderer Renderer;
        }
        private static readonly ConditionalWeakTable<ToolStrip, OrigStrip> OrigStrips = new ConditionalWeakTable<ToolStrip, OrigStrip>();

        private static bool menuNight;
        private static bool menuSet;
        private static ToolStripRenderer dayMenuRenderer;

        // menus and drop down lists of every window (also of the main window of AgOpenGPS) follow the same look
        private static void SetMenuRenderer(bool night)
        {
            try
            {
                if (menuSet && menuNight == night) return;
                menuSet = true;
                menuNight = night;
                // the renderer the program had before the first night: day brings back that one
                if (dayMenuRenderer == null && !(ToolStripManager.Renderer is DarkRenderer)) dayMenuRenderer = ToolStripManager.Renderer;
                ToolStripManager.Renderer = night ? new DarkRenderer() : (dayMenuRenderer ?? new ToolStripProfessionalRenderer());
            }
            catch (Exception)
            {
            }
        }

        // ---- modern finish, the same in day and night ----

        private static readonly ConditionalWeakTable<Control, object> Modernized = new ConditionalWeakTable<Control, object>();

        // the thin line that replaces the old raised / sunken 3D frames and the hard black single frames
        private static Color SoftLine { get { return IsNight() ? GridLine : Color.FromArgb(176, 181, 189); } }

        // Done once per control: windows get rounded corners (Windows 11), value boxes and panels a thin soft frame
        // instead of the 3D bevel of old Windows versions. Colours are read while painting, so day / night just repaints.
        private static void Modernize(Control c)
        {
            object done;
            if (Modernized.TryGetValue(c, out done)) return;
            Modernized.Add(c, new object());
            try
            {
                var form = c as Form;
                if (form != null)
                {
                    if (form.IsHandleCreated) RoundCorners(form);
                    else form.HandleCreated += (s, e) => RoundCorners((Form)s);
                    return;
                }

                var label = c as Label;
                if (label != null && label.BorderStyle != BorderStyle.None && !(label is LinkLabel))
                {
                    label.BorderStyle = BorderStyle.None;
                    label.Paint += SoftFrame;
                    label.Invalidate();
                    return;
                }

                // only a plain panel: a split container, a flow list or a table keeps the frame it has
                var panel = c as Panel;
                if (panel != null && panel.GetType() == typeof(Panel) && panel.BorderStyle != BorderStyle.None)
                {
                    panel.BorderStyle = BorderStyle.None;
                    // docked children keep clear of the painted frame, as they kept clear of the old border
                    if (panel.Padding == Padding.Empty) panel.Padding = new Padding(1);
                    panel.Paint += SoftFrame;
                    panel.Resize += (s, e) => ((Control)s).Invalidate();
                    panel.Invalidate();
                }
            }
            catch (Exception)
            {
            }
        }

        private static void SoftFrame(object sender, PaintEventArgs e)
        {
            var c = (Control)sender;
            using (var pen = new Pen(SoftLine))
                e.Graphics.DrawRectangle(pen, 0, 0, c.ClientSize.Width - 1, c.ClientSize.Height - 1);
        }

        // DWMWA_WINDOW_CORNER_PREFERENCE = DWMWCP_ROUND; older Windows versions do not know it and ignore it.
        // Only windows with a frame: a borderless one is the full-screen main window or a window that draws its own
        // edge, and rounding it cuts its corners off (the desktop shows through at the edges of the full screen)
        private static void RoundCorners(Form form)
        {
            try
            {
                if (form.FormBorderStyle == FormBorderStyle.None || form.GetType().Name == "FormGPS") return;
                int round = 2;
                DwmSetWindowAttribute(form.Handle, 33, ref round, sizeof(int));
            }
            catch (Exception)
            {
            }
        }

        private static void TitleBar(Form form, bool night)
        {
            try
            {
                if (!form.IsHandleCreated) return;
                int value = night ? 1 : 0;
                DwmSetWindowAttribute(form.Handle, 20, ref value, sizeof(int));
                DwmSetWindowAttribute(form.Handle, 19, ref value, sizeof(int));
            }
            catch (Exception)
            {
            }
        }

        // The original colours are noted for every control before anything is changed (the colours of a control
        // without its own colour follow its parent, which would otherwise be dark already).
        private static void Capture(Control c)
        {
            if (c is DarkScrollBar) return;
            WatchChildren(c);
            Modernize(c);
            State st;
            if (!States.TryGetValue(c, out st))
            {
                st = new State { Back = c.BackColor, Fore = c.ForeColor };
                var button = c as ButtonBase;
                if (button != null) st.UseVisual = button.UseVisualStyleBackColor;
                States.Add(c, st);
            }
            foreach (Control child in Children(c)) Capture(child);
        }

        // a copy of the child list: theming may add controls (the dark sliders) while the children are walked, and a
        // live list would then skip the next child
        private static Control[] Children(Control c)
        {
            var list = new Control[c.Controls.Count];
            c.Controls.CopyTo(list, 0);
            return list;
        }

        private static void Walk(Control c, bool night)
        {
            State st;
            if (States.TryGetValue(c, out st))
            {
                if (night && !st.Dark)
                {
                    Darken(c, st);
                    st.Dark = true;
                }
                else if (!night && st.Dark)
                {
                    Restore(c, st);
                    st.Dark = false;
                }
            }
            foreach (Control child in Children(c)) Walk(child, night);
        }

        private static double Luminance(Color c)
        {
            return (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
        }

        private static int Spread(Color c)
        {
            return Math.Max(c.R, Math.Max(c.G, c.B)) - Math.Min(c.R, Math.Min(c.G, c.B));
        }

        private static bool IsNeutralLight(Color c)
        {
            return c.A == 255 && Spread(c) <= 24 && Luminance(c) >= 0.62;
        }

        private static Color Lighten(Color c, double amount)
        {
            return Color.FromArgb(c.A,
                (int)(c.R + (255 - c.R) * amount),
                (int)(c.G + (255 - c.G) * amount),
                (int)(c.B + (255 - c.B) * amount));
        }

        private static Color FromHsl(float h, float sat, float l)
        {
            double c = (1 - Math.Abs(2 * l - 1)) * sat;
            double hh = h / 60.0;
            double x = c * (1 - Math.Abs(hh % 2 - 1));
            double r = 0, g = 0, b = 0;
            if (hh < 1) { r = c; g = x; }
            else if (hh < 2) { r = x; g = c; }
            else if (hh < 3) { g = c; b = x; }
            else if (hh < 4) { g = x; b = c; }
            else if (hh < 5) { r = x; b = c; }
            else { r = c; b = x; }
            double m = l - c / 2;
            return Color.FromArgb(255, (int)Math.Round((r + m) * 255), (int)Math.Round((g + m) * 255), (int)Math.Round((b + m) * 255));
        }

        // text on a dark background: grey / black text becomes light, coloured text keeps its colour but gets lighter
        private static Color DarkFore(Color f)
        {
            if (f.A == 0) return f;
            if (Spread(f) <= 40) return Luminance(f) < 0.6 ? TextColor : f;
            float l = f.GetBrightness();
            if (l >= 0.6f) return f;
            return FromHsl(f.GetHue(), Math.Min(1f, f.GetSaturation()), 0.65f);
        }

        private static bool SameAsParentOriginal(Control c, Color original)
        {
            State parentState;
            return c.Parent != null && States.TryGetValue(c.Parent, out parentState) && parentState.Back.ToArgb() == original.ToArgb();
        }

        // a light background of a colour (pale green, light yellow ...) becomes a dark shade of the same colour
        private static Color DarkBack(Control c, Color original)
        {
            // lists and input fields are always the neutral input colour (a pale green list must not turn dark green)
            if (c is TextBoxBase || c is ComboBox || c is ListBox || c is UpDownBase || c is ListView || c is TreeView || c is DataGridView) return InputBack;
            // a pale coloured flow panel holds a list of entries (the fields to upload)
            if (c is FlowLayoutPanel) return InputBack;
            if (Spread(original) > 24)
            {
                // strong colours (pale green, light yellow ...) keep their hue, pastel blues and greys become the neutral dark
                if (original.GetSaturation() < 0.6f) return Color.FromArgb(255, 34, 37, 42);
                // a large coloured area (a panel, a page, a whole window) in a dark shade of its colour looks muddy
                // (orange -> brown): it becomes the neutral dark, only buttons and labels keep their colour as a signal
                // (a control that only shows the colour of its parent, like a progress bar on the panel, goes with it)
                if (!(c is ButtonBase || c is Label) || SameAsParentOriginal(c, original)) return Color.FromArgb(255, 34, 37, 42);
                return FromHsl(original.GetHue(), Math.Min(0.45f, original.GetSaturation()), 0.24f);
            }
            if (c is TextBoxBase || c is ComboBox || c is ListBox || c is UpDownBase || c is ListView || c is TreeView) return InputBack;
            if (c is Button) return ButtonBack;
            return PageBack;
        }

        // a disabled dark scroll bar has no thumb and its track blends into the page: the parent draws a thin frame around it
        private static readonly ConditionalWeakTable<Control, object> OutlinedParents = new ConditionalWeakTable<Control, object>();

        private static readonly ConditionalWeakTable<Control, object> OutlinedBars = new ConditionalWeakTable<Control, object>();

        private static void OutlineScrollBars(Control bar)
        {
            Control parent = bar.Parent;
            if (parent == null) return;
            object known;
            if (!OutlinedBars.TryGetValue(bar, out known))
            {
                OutlinedBars.Add(bar, new object());
                // the frame lies outside the bar: when the bar hides or moves, the old frame must be wiped from the parent
                EventHandler changed = (sender, e) =>
                {
                    Control host = ((Control)sender).Parent;
                    if (host != null) host.Invalidate();
                };
                bar.VisibleChanged += changed;
                bar.LocationChanged += changed;
                bar.SizeChanged += changed;
                bar.EnabledChanged += changed;
                bar.ParentChanged += changed;
            }
            if (!OutlinedParents.TryGetValue(parent, out known))
            {
                OutlinedParents.Add(parent, new object());
                parent.Paint += (sender, e) =>
                {
                    if (!IsNight()) return;
                    var host = (Control)sender;
                    using (var pen = new Pen(Color.FromArgb(96, 100, 110)))
                    {
                        foreach (Control child in host.Controls)
                        {
                            if ((child is ScrollBar || child is ProgressBar) && child.Visible) e.Graphics.DrawRectangle(pen, child.Left - 1, child.Top - 1, child.Width + 1, child.Height + 1);
                        }
                    }
                };
            }
            parent.Invalidate();
        }

        // pressed (checked) state of a toggle button: the colour keeps its hue and stays clearly lighter than the idle button
        private static Color CheckedDark(Color original)
        {
            if (Spread(original) > 24) return FromHsl(original.GetHue(), Math.Min(0.5f, Math.Max(0.35f, original.GetSaturation())), 0.32f);
            return Color.FromArgb(255, 88, 94, 106);
        }

        private static Color EffectiveBack(Control c)
        {
            for (Control p = c; p != null; p = p.Parent)
            {
                Color b = p.BackColor;
                if (b.A == 255) return b;
            }
            return PageBack;
        }

        private static bool HasPicture(Control c)
        {
            // drawings and photos made for a light background keep it (icons on buttons are coloured and work on both)
            return c.BackgroundImage != null || c is PictureBox;
        }

        private static bool SkipControl(Control c)
        {
            // charts and custom drawn surfaces keep their own colours
            string name = c.GetType().Name;
            return name == "Chart" || name == "GLControl" || name == "OpenGLControl";
        }

        // Pages with a drawing as background (grey page with coloured icons): the grey and the black lines are
        // inverted, the coloured parts stay. Photos and other colourful images are left alone (null).
        private static readonly ConditionalWeakTable<Image, object> DarkImages = new ConditionalWeakTable<Image, object>();

        // images that are mostly transparent (icons on a coloured page) keep working on a dark colour behind them
        private static readonly ConditionalWeakTable<Image, object> TransparentImages = new ConditionalWeakTable<Image, object>();

        private static bool IsMostlyTransparent(Image img)
        {
            object v;
            return TransparentImages.TryGetValue(img, out v) && v is bool && (bool)v;
        }

        private static Image DarkImage(Image src)
        {
            object cached;
            if (DarkImages.TryGetValue(src, out cached)) return cached as Image;

            Bitmap result = null;
            try
            {
                if (src.Width * src.Height <= 4000000)
                {
                    var bmp = new Bitmap(src.Width, src.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                    using (Graphics g = Graphics.FromImage(bmp)) g.DrawImage(src, 0, 0, src.Width, src.Height);

                    var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
                    var data = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadWrite, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                    int bytes = Math.Abs(data.Stride) * data.Height;
                    byte[] px = new byte[bytes];
                    System.Runtime.InteropServices.Marshal.Copy(data.Scan0, px, 0, bytes);

                    long opaque = 0, lightNeutral = 0;
                    for (int y = 0; y < data.Height; y++)
                    {
                        int row = y * data.Stride;
                        for (int x = 0; x < data.Width; x++)
                        {
                            int i = row + x * 4;
                            if (px[i + 3] < 128) continue;
                            opaque++;
                            int mx = Math.Max(px[i], Math.Max(px[i + 1], px[i + 2]));
                            int mn = Math.Min(px[i], Math.Min(px[i + 1], px[i + 2]));
                            if (mx - mn <= 30 && (px[i] + px[i + 1] + px[i + 2]) / 3 >= 150) lightNeutral++;
                        }
                    }

                    TransparentImages.Add(src, opaque * 100 / Math.Max(1L, (long)data.Width * data.Height) < 85);
                    if (opaque > 0 && lightNeutral * 100 / opaque >= 50)
                    {
                        // the light background (connected to the border) becomes transparent; what is inside stays as it is
                        int W = data.Width, H = data.Height, stride = data.Stride;
                        var seen = new bool[W * H];
                        var stack = new Stack<int>();
                        Func<int, bool> isBack = p =>
                        {
                            int i = (p / W) * stride + (p % W) * 4;
                            if (px[i + 3] < 128) return false;
                            int mx2 = Math.Max(px[i], Math.Max(px[i + 1], px[i + 2]));
                            int mn2 = Math.Min(px[i], Math.Min(px[i + 1], px[i + 2]));
                            return mx2 - mn2 <= 30 && (px[i] + px[i + 1] + px[i + 2]) / 3 >= 157;
                        };
                        for (int x = 0; x < W; x++) { stack.Push(x); stack.Push((H - 1) * W + x); }
                        for (int y = 0; y < H; y++) { stack.Push(y * W); stack.Push(y * W + W - 1); }
                        while (stack.Count > 0)
                        {
                            int p = stack.Pop();
                            if (seen[p] || !isBack(p)) continue;
                            seen[p] = true;
                            int px0 = p % W, py0 = p / W;
                            if (px0 > 0) stack.Push(p - 1);
                            if (px0 < W - 1) stack.Push(p + 1);
                            if (py0 > 0) stack.Push(p - W);
                            if (py0 < H - 1) stack.Push(p + W);
                        }
                        // large white areas inside the picture (not only at its border) go too: small ones (letters) stay
                        Func<int, bool> isWhite = p =>
                        {
                            int i = (p / W) * stride + (p % W) * 4;
                            if (px[i + 3] < 128) return false;
                            int mx3 = Math.Max(px[i], Math.Max(px[i + 1], px[i + 2]));
                            int mn3 = Math.Min(px[i], Math.Min(px[i + 1], px[i + 2]));
                            return mx3 - mn3 <= 14 && mn3 >= 232;
                        };
                        for (int start = 0; start < seen.Length; start++)
                        {
                            if (seen[start] || !isWhite(start)) continue;
                            var comp = new List<int>();
                            var work = new Stack<int>();
                            work.Push(start);
                            seen[start] = true;
                            while (work.Count > 0)
                            {
                                int q = work.Pop();
                                comp.Add(q);
                                int qx = q % W, qy = q / W;
                                if (qx > 0 && !seen[q - 1] && isWhite(q - 1)) { seen[q - 1] = true; work.Push(q - 1); }
                                if (qx < W - 1 && !seen[q + 1] && isWhite(q + 1)) { seen[q + 1] = true; work.Push(q + 1); }
                                if (qy > 0 && !seen[q - W] && isWhite(q - W)) { seen[q - W] = true; work.Push(q - W); }
                                if (qy < H - 1 && !seen[q + W] && isWhite(q + W)) { seen[q + W] = true; work.Push(q + W); }
                            }
                            if (comp.Count < 150) foreach (int q in comp) seen[q] = false;
                        }
                        for (int p = 0; p < seen.Length; p++)
                        {
                            if (seen[p]) px[(p / W) * stride + (p % W) * 4 + 3] = 0;
                        }
                        System.Runtime.InteropServices.Marshal.Copy(px, 0, data.Scan0, bytes);
                        result = bmp;
                    }
                    bmp.UnlockBits(data);
                    if (result == null) bmp.Dispose();
                }
            }
            catch (Exception)
            {
                result = null;
            }

            DarkImages.Add(src, result);
            return result;
        }

        private static void Darken(Control c, State st)
        {
            if (SkipControl(c)) return;

            // a colour sample (the section colours, the day / night colours) shows a real colour: it keeps it, only
            // its text follows it so that it can be read
            if (IsSwatch(c))
            {
                SwatchText(c, st);
                WatchSwatch(c);
                return;
            }

            bool imageSwapped = false;
            if (c.BackgroundImage != null && !(c is PictureBox))
            {
                Image orig = c.BackgroundImage;
                Image dark = DarkImage(orig) ?? (c is ButtonBase ? LightGlyph(orig) : null);
                if (dark != null)
                {
                    // a drawing of black and grey lines (the Shift key, the background of a page) turns light
                    st.OrigImage = orig;
                    c.BackgroundImage = dark;
                    imageSwapped = true;
                }
                else if (!(c is ButtonBase) && IsMostlyTransparent(orig))
                {
                    imageSwapped = true;
                }
            }

            // a disabled label is drawn by Windows in an embossed dark grey that cannot be read on dark colours
            if (c is Label) WatchDisabledLabel((Label)c);

            // the white grid lines of a list are glaring on a dark list
            var list = c as ListView;
            if (list != null && list.GridLines)
            {
                list.GridLines = false;
                st.GridLinesOff = true;
            }
            if (list != null) DarkListHeader(list, true);

            // icons on buttons: the faint light haze around the drawing shows as a lighter square on dark colours
            var iconButton = c as ButtonBase;
            if (iconButton != null && iconButton.Image != null)
            {
                Image icon = iconButton.Image;
                Image clean = LightGlyph(icon) ?? HazeFree(icon);
                if (clean != null)
                {
                    st.OrigIcon = icon;
                    st.CleanIcon = clean;
                    iconButton.Image = clean;
                }
            }

            // pictures (icons on buttons, drawings) were made for a light background: those keep it, unless the
            // picture covers only part of the control (the rest of it is then dark); a strongly coloured label that is
            // only middle light (a light blue hint) gets a dark shade of its colour too
            // a window or panel in a middle grey (the colour picker) is a page as well
            bool container = c is Form || c is Panel || c is GroupBox || c is TabPage || c is UserControl;
            double lightFrom = (c is Label && Spread(st.Back) > 24) || (container && Spread(st.Back) <= 24) ? 0.45 : 0.62;
            if ((imageSwapped || !HasPicture(c) || !PictureCovers(c)) && st.Back.A == 255 && Luminance(st.Back) >= lightFrom)
            {
                // a control in the colour of its light parent goes dark together with that parent: only when the
                // theme handles the parent too (the main window of AgOpenGPS paints itself, so its navigation panel
                // must go dark on its own, whatever colour the window has at that moment)
                State parentState;
                bool followsLightParent = c.Parent != null && States.TryGetValue(c.Parent, out parentState)
                    && !(c is TextBoxBase) && !(c is ComboBox) && !(c is ListBox) && !(c is UpDownBase)
                    && Luminance(EffectiveBack(c.Parent)) >= 0.62
                    && st.Back.ToArgb() == c.Parent.BackColor.ToArgb();
                if (!followsLightParent)
                {
                    var button = c as ButtonBase;
                    if (button != null) button.UseVisualStyleBackColor = false;
                    var page = c as TabPage;
                    if (page != null) page.UseVisualStyleBackColor = false;
                    c.BackColor = DarkBack(c, st.Back);
                    // a flat icon button without border blends into the page (like the buttons with a background picture)
                    if (button != null && button.FlatStyle == FlatStyle.Flat && button.Image != null && button.FlatAppearance.BorderSize == 0 && c.Parent != null)
                        c.BackColor = EffectiveBack(c.Parent);
                    st.BackTouched = true;
                }
            }
            else if (c is Button && st.Back.A < 255 && ((Button)c).FlatStyle != FlatStyle.Flat)
            {
                // a raised button with a see-through colour is drawn by Windows with a white face, while its text
                // turns light for the dark page behind it: light text on white. It gets the dark button colour.
                ((Button)c).UseVisualStyleBackColor = false;
                c.BackColor = ButtonBack;
                st.BackTouched = true;
            }

            // text turns light only where the background is dark
            if (Luminance(EffectiveBack(c)) < 0.5)
            {
                Color fore = DarkFore(st.Fore);
                if (fore != st.Fore)
                {
                    c.ForeColor = fore;
                    st.ForeTouched = true;
                }
            }

            if (c is ScrollBar || c is ListBox || c is ListView || c is TreeView || c is ComboBox || c is ProgressBar) ScrollBarTheme(c, true);
            if (c is ProgressBar)
            {
                // the bar itself: a calm teal instead of the bright system blue
                c.ForeColor = Color.FromArgb(255, 48, 140, 120);
                st.ForeTouched = true;
            }
            if ((c is ScrollBar || c is ProgressBar) && c.Parent != null) OutlineScrollBars(c);
            if (c is ScrollBar) DarkSlider((ScrollBar)c, true);

            var grid = c as DataGridView;
            if (grid != null)
            {
                st.Extra = new GridState
                {
                    Background = grid.BackgroundColor,
                    Line = grid.GridColor,
                    CellBack = grid.DefaultCellStyle.BackColor,
                    CellFore = grid.DefaultCellStyle.ForeColor
                };
                grid.BackgroundColor = PageBack;
                grid.GridColor = GridLine;
                grid.DefaultCellStyle.BackColor = InputBack;
                grid.DefaultCellStyle.ForeColor = TextColor;
            }

            // tab headers are drawn by Windows (light): draw them ourselves
            var tabs = c as TabControl;
            if (tabs != null && tabs.DrawMode == TabDrawMode.Normal)
            {
                var ts = new TabState { Mode = tabs.DrawMode };
                ts.Draw = (s, e) =>
                {
                    var tc = (TabControl)s;
                    if (e.Index < 0 || e.Index >= tc.TabPages.Count) return;
                    TabPage page = tc.TabPages[e.Index];
                    bool selected = e.Index == tc.SelectedIndex;
                    Rectangle r = tc.GetTabRect(e.Index);
                    using (var back = new SolidBrush(selected ? Color.FromArgb(30, 32, 37) : Color.FromArgb(22, 24, 27)))
                        e.Graphics.FillRectangle(back, r);
                    using (var pen = new Pen(GridLine))
                        e.Graphics.DrawRectangle(pen, r.X, r.Y, r.Width - 1, r.Height - 1);

                    Image img = null;
                    try
                    {
                        if (tc.ImageList != null)
                        {
                            int idx = page.ImageIndex;
                            if (idx < 0 && !string.IsNullOrEmpty(page.ImageKey)) idx = tc.ImageList.Images.IndexOfKey(page.ImageKey);
                            if (idx >= 0 && idx < tc.ImageList.Images.Count) img = CleanIcon(tc.ImageList, idx);
                        }
                    }
                    catch (Exception)
                    {
                        img = null;
                    }

                    Rectangle textRect = r;
                    if (img != null)
                    {
                        // a soft plate behind the icon keeps dark icons visible
                        Rectangle plate = Rectangle.Inflate(r, -4, -4);
                        using (var path = RoundedRect(plate, 8))
                        using (var pb = new SolidBrush(selected ? Color.FromArgb(52, 56, 64) : Color.FromArgb(30, 32, 37)))
                        {
                            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                            e.Graphics.FillPath(pb, path);
                        }

                        bool hasText = !string.IsNullOrEmpty(page.Text);
                        int availW = hasText ? Math.Max(8, r.Width / 2) : r.Width - 12;
                        int availH = r.Height - 12;
                        double scale = Math.Min((double)availW / img.Width, (double)availH / img.Height);
                        int iw = Math.Max(1, (int)(img.Width * scale)), ih = Math.Max(1, (int)(img.Height * scale));
                        int ix = hasText ? r.X + 8 : r.X + (r.Width - iw) / 2;
                        e.Graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        e.Graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                        e.Graphics.DrawImage(img, new Rectangle(ix, r.Y + (r.Height - ih) / 2, iw, ih));
                        if (hasText) textRect = new Rectangle(ix + iw + 4, r.Y, r.Right - ix - iw - 8, r.Height);
                    }
                    if (!string.IsNullOrEmpty(page.Text))
                        TextRenderer.DrawText(e.Graphics, page.Text, tc.Font, textRect, TextColor,
                            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                };
                tabs.DrawItem += ts.Draw;
                tabs.DrawMode = TabDrawMode.OwnerDrawFixed;
                ts.Painter = new StripPainter(tabs);
                ts.Created = (s, e) =>
                {
                    if (ts.Painter.Handle != IntPtr.Zero) ts.Painter.ReleaseHandle();
                    ts.Painter.AssignHandle(tabs.Handle);
                };
                ts.Destroyed = (s, e) =>
                {
                    if (ts.Painter.Handle != IntPtr.Zero) ts.Painter.ReleaseHandle();
                };
                tabs.HandleCreated += ts.Created;
                tabs.HandleDestroyed += ts.Destroyed;
                if (tabs.IsHandleCreated) ts.Created(tabs, EventArgs.Empty);
                st.Extra = ts;
            }

            // group box titles stay black with the system theme: paint the title over it in the text colour
            var group = c as GroupBox;
            if (group != null)
            {
                var gs = new GroupState { Style = group.FlatStyle };
                if (group.FlatStyle == FlatStyle.System) group.FlatStyle = FlatStyle.Standard;
                gs.Paint = (s, e) =>
                {
                    var gb = (GroupBox)s;
                    if (string.IsNullOrEmpty(gb.Text)) return;
                    const TextFormatFlags tf = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
                    Size size = TextRenderer.MeasureText(e.Graphics, gb.Text, gb.Font, new Size(int.MaxValue, int.MaxValue), tf);
                    using (var back = new SolidBrush(EffectiveBack(gb)))
                        e.Graphics.FillRectangle(back, new Rectangle(5, 0, size.Width + 6, size.Height));
                    TextRenderer.DrawText(e.Graphics, gb.Text, gb.Font, new Point(8, 0), TextColor, tf);
                };
                group.Paint += gs.Paint;
                st.Extra = gs;
                group.Invalidate();
            }

            // flat buttons: the colours of the checked state and of the mouse over / down state are light by default
            var flat = c as ButtonBase;
            FlatStyle? systemStyle = null;
            if (flat is Button && flat.FlatStyle == FlatStyle.System)
            {
                // a system drawn button ignores every colour: it becomes a flat one
                systemStyle = FlatStyle.System;
                flat.FlatStyle = FlatStyle.Flat;
            }
            if (flat != null && flat.FlatStyle == FlatStyle.Flat)
            {
                var fl = new FlatState
                {
                    Style = systemStyle,
                    Checked = flat.FlatAppearance.CheckedBackColor,
                    Over = flat.FlatAppearance.MouseOverBackColor,
                    Down = flat.FlatAppearance.MouseDownBackColor,
                    Border = flat.FlatAppearance.BorderColor
                };
                // the default border of a flat button follows the text colour (white in dark mode)
                if (fl.Border.IsEmpty && flat.FlatAppearance.BorderSize > 0)
                {
                    flat.FlatAppearance.BorderColor = Color.FromArgb(110, 114, 122);
                    fl.BorderTouched = true;
                }
                fl.Paint = PaintDisabledFlat;
                flat.Paint += fl.Paint;
                st.Extra = fl;
                // text of a flat button: dark text must not stay on the dark colours
                if (Spread(c.ForeColor) <= 40 && Luminance(c.ForeColor) < 0.6 && c.ForeColor.ToArgb() != TextColor.ToArgb())
                {
                    c.ForeColor = TextColor;
                    st.ForeTouched = true;
                }
                if (fl.Checked.A == 255 && Luminance(fl.Checked) >= 0.62)
                {
                    flat.FlatAppearance.CheckedBackColor = CheckedDark(fl.Checked);
                    fl.CheckedTouched = true;
                }
                if (fl.Over.IsEmpty || (fl.Over.A == 255 && Luminance(fl.Over) >= 0.62))
                {
                    flat.FlatAppearance.MouseOverBackColor = Color.FromArgb(70, 76, 88);
                    flat.FlatAppearance.MouseDownBackColor = Color.FromArgb(50, 54, 62);
                    fl.HoverTouched = true;
                }
            }

            var strip = c as ToolStrip;
            if (strip != null)
            {
                var ss = new StripState { Renderer = strip.Renderer, Mode = strip.RenderMode };
                st.Extra = ss;
                strip.Renderer = new DarkRenderer();
                strip.BackColor = PageBack;
                st.BackTouched = true;
                DarkenItems(strip.Items, ss);
            }
        }

        private static void DarkenItems(ToolStripItemCollection items, StripState ss)
        {
            foreach (ToolStripItem item in items)
            {
                ss.Items.Add(new KeyValuePair<ToolStripItem, Color>(item, item.ForeColor));
                item.ForeColor = DarkFore(item.ForeColor);
                var drop = item as ToolStripDropDownItem;
                if (drop != null && drop.DropDownItems.Count > 0) DarkenItems(drop.DropDownItems, ss);
            }
        }

        private static void Restore(Control c, State st)
        {
            if (SkipControl(c)) return;

            if (st.OrigImage != null)
            {
                c.BackgroundImage = st.OrigImage;
                st.OrigImage = null;
            }
            var iconRestore = c as ButtonBase;
            if (st.OrigIcon != null)
            {
                if (iconRestore != null && iconRestore.Image == st.CleanIcon) iconRestore.Image = st.OrigIcon;
                st.OrigIcon = null;
                st.CleanIcon = null;
            }
            if (st.BackTouched)
            {
                c.BackColor = st.Back;
                var button = c as ButtonBase;
                if (button != null) button.UseVisualStyleBackColor = st.UseVisual;
                var page = c as TabPage;
                if (page != null) page.UseVisualStyleBackColor = true;
                st.BackTouched = false;
            }
            if (st.ForeTouched)
            {
                c.ForeColor = st.Fore;
                st.ForeTouched = false;
            }
            if (st.GridLinesOff)
            {
                var list = c as ListView;
                if (list != null) list.GridLines = true;
                st.GridLinesOff = false;
            }
            if (c is ListView) DarkListHeader((ListView)c, false);
            if (c is ScrollBar || c is ListBox || c is ListView || c is TreeView || c is ComboBox || c is ProgressBar) ScrollBarTheme(c, false);
            if (c is ScrollBar) DarkSlider((ScrollBar)c, false);

            var gs = st.Extra as GridState;
            var grid = c as DataGridView;
            if (gs != null && grid != null)
            {
                grid.BackgroundColor = gs.Background;
                grid.GridColor = gs.Line;
                grid.DefaultCellStyle.BackColor = gs.CellBack;
                grid.DefaultCellStyle.ForeColor = gs.CellFore;
            }

            var tstate = st.Extra as TabState;
            var tabControl = c as TabControl;
            if (tstate != null && tabControl != null)
            {
                tabControl.DrawItem -= tstate.Draw;
                tabControl.HandleCreated -= tstate.Created;
                tabControl.HandleDestroyed -= tstate.Destroyed;
                if (tstate.Painter.Handle != IntPtr.Zero) tstate.Painter.ReleaseHandle();
                tabControl.DrawMode = tstate.Mode;
            }

            var gstate = st.Extra as GroupState;
            var groupBox = c as GroupBox;
            if (gstate != null && groupBox != null)
            {
                groupBox.Paint -= gstate.Paint;
                groupBox.FlatStyle = gstate.Style;
                groupBox.Invalidate();
            }

            var fs = st.Extra as FlatState;
            var flatButton = c as ButtonBase;
            if (fs != null && flatButton != null)
            {
                if (fs.Paint != null) flatButton.Paint -= fs.Paint;
                if (fs.Style.HasValue) flatButton.FlatStyle = fs.Style.Value;
                if (fs.CheckedTouched) flatButton.FlatAppearance.CheckedBackColor = fs.Checked;
                if (fs.BorderTouched) flatButton.FlatAppearance.BorderColor = fs.Border;
                if (fs.HoverTouched)
                {
                    flatButton.FlatAppearance.MouseOverBackColor = fs.Over;
                    flatButton.FlatAppearance.MouseDownBackColor = fs.Down;
                }
            }

            var ss = st.Extra as StripState;
            var strip = c as ToolStrip;
            if (ss != null && strip != null)
            {
                if (ss.Mode != ToolStripRenderMode.Custom) strip.RenderMode = ss.Mode;
                else strip.Renderer = ss.Renderer ?? new ToolStripProfessionalRenderer();
                foreach (var kv in ss.Items)
                {
                    try { kv.Key.ForeColor = kv.Value; } catch (Exception) { }
                }
            }
            st.Extra = null;
        }

        private sealed class DarkRenderer : ToolStripProfessionalRenderer
        {
            public DarkRenderer() : base(new DarkColors())
            {
                RoundedEdges = false;
            }

            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                e.TextColor = e.Item.Enabled ? TextColor : Color.FromArgb(130, 134, 142);
                base.OnRenderItemText(e);
            }

            protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
            {
                e.ArrowColor = TextColor;
                base.OnRenderArrow(e);
            }

            protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
            {
                // the status strip draws a light line along its top edge: not in dark mode
                if (e.ToolStrip is StatusStrip) return;
                base.OnRenderToolStripBorder(e);
            }
        }

        private sealed class DarkColors : ProfessionalColorTable
        {
            private static readonly Color Back = Color.FromArgb(32, 34, 38);
            private static readonly Color Over = Color.FromArgb(64, 70, 80);
            private static readonly Color Line = Color.FromArgb(80, 84, 92);

            public override Color ToolStripDropDownBackground { get { return Back; } }
            public override Color ImageMarginGradientBegin { get { return Back; } }
            public override Color ImageMarginGradientMiddle { get { return Back; } }
            public override Color ImageMarginGradientEnd { get { return Back; } }
            public override Color MenuBorder { get { return Line; } }
            public override Color MenuItemBorder { get { return Line; } }
            public override Color MenuItemSelected { get { return Over; } }
            public override Color MenuItemSelectedGradientBegin { get { return Over; } }
            public override Color MenuItemSelectedGradientEnd { get { return Over; } }
            public override Color MenuItemPressedGradientBegin { get { return Over; } }
            public override Color MenuItemPressedGradientMiddle { get { return Over; } }
            public override Color MenuItemPressedGradientEnd { get { return Over; } }
            public override Color MenuStripGradientBegin { get { return Back; } }
            public override Color MenuStripGradientEnd { get { return Back; } }
            public override Color ToolStripGradientBegin { get { return Back; } }
            public override Color ToolStripGradientMiddle { get { return Back; } }
            public override Color ToolStripGradientEnd { get { return Back; } }
            public override Color ToolStripBorder { get { return Line; } }
            public override Color StatusStripGradientBegin { get { return Back; } }
            public override Color StatusStripGradientEnd { get { return Back; } }
            public override Color ButtonSelectedHighlight { get { return Over; } }
            public override Color ButtonSelectedGradientBegin { get { return Over; } }
            public override Color ButtonSelectedGradientMiddle { get { return Over; } }
            public override Color ButtonSelectedGradientEnd { get { return Over; } }
            public override Color ButtonPressedGradientBegin { get { return Over; } }
            public override Color ButtonPressedGradientMiddle { get { return Over; } }
            public override Color ButtonPressedGradientEnd { get { return Over; } }
            public override Color SeparatorDark { get { return Line; } }
            public override Color SeparatorLight { get { return Line; } }
            public override Color CheckBackground { get { return Over; } }
        }
    }
}
