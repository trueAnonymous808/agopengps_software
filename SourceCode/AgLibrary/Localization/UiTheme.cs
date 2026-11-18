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
        }

        private sealed class GridState
        {
            public Color Background;
            public Color Line;
            public Color CellBack;
            public Color CellFore;
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
                    if (last.Right >= tabs.Width) return;
                    using (Graphics g = Graphics.FromHwnd(tabs.Handle))
                    using (var brush = new SolidBrush(PageBack))
                        g.FillRectangle(brush, last.Right, 0, tabs.Width - last.Right, bottom);
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
            if (b.Image != null)
            {
                int x = b.ImageAlign.ToString().EndsWith("Right") ? r.Width - b.Image.Width - 4 : (b.ImageAlign.ToString().StartsWith("Top") || b.ImageAlign.ToString().StartsWith("Bottom")) && b.ImageAlign.ToString().Contains("Center") ? (r.Width - b.Image.Width) / 2 : 4;
                int y = b.ImageAlign.ToString().StartsWith("Top") ? 4 : b.ImageAlign.ToString().StartsWith("Bottom") ? r.Height - b.Image.Height - 4 : (r.Height - b.Image.Height) / 2;
                e.Graphics.DrawImage(b.Image, x, y, b.Image.Width, b.Image.Height);
            }
            if (!string.IsNullOrEmpty(b.Text))
            {
                TextFormatFlags tf = TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis;
                string al = b.TextAlign.ToString();
                tf |= al.EndsWith("Right") ? TextFormatFlags.Right : al.EndsWith("Left") ? TextFormatFlags.Left : TextFormatFlags.HorizontalCenter;
                Rectangle tr = Rectangle.Inflate(r, -10, 0);
                TextRenderer.DrawText(e.Graphics, b.Text.Trim(), b.Font, tr, Color.FromArgb(150, 154, 162), tf);
            }
        }

        private sealed class StripState
        {
            public ToolStripRenderer Renderer;
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
            else SetWindowTheme(c.Handle, dark ? "DarkMode_Explorer" : null, null);
        }

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        /// <summary>Menu button colour for the selected entry (day: system light blue).</summary>
        public static Color MenuActive { get { return IsNight() ? Color.FromArgb(255, 58, 64, 76) : SystemColors.GradientActiveCaption; } }

        /// <summary>Menu button colour for the other entries.</summary>
        public static Color MenuInactive { get { return IsNight() ? PageBack : SystemColors.GradientInactiveCaption; } }

        /// <summary>True when AgOpenGPS is in night mode.</summary>
        public static bool IsNight()
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"SOFTWARE\AgOpenGPS"))
                {
                    object v = key?.GetValue("DayMode");
                    return v != null && v.ToString() == "0";
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>Applies the current mode to every open window (called when the registry key changes).</summary>
        public static void Refresh()
        {
            try
            {
                SetMenuRenderer(IsNight());
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
            SetMenuRenderer(IsNight());
            // the main window of AgOpenGPS paints its own day / night look: only its menus follow
            if (form.GetType().Name == "FormGPS")
            {
                ThemeMenus(form, IsNight());
                ThemeMainPanel(form, IsNight());
                return;
            }
            try
            {
                bool night = IsNight();
                Capture(form);
                Walk(form, night);
                TitleBar(form, night);
                form.Invalidate(true); // controls that paint themselves read the mode while painting
            }
            catch (Exception)
            {
                // never let the theme disturb the program
            }
        }

        // the light button panel on the left of the main window of AgOpenGPS (2D / 3D, day / night, brightness ...)
        private static void ThemeMainPanel(Form form, bool night)
        {
            try
            {
                Control[] found = form.Controls.Find("panelNavigation", true);
                foreach (Control panel in found)
                {
                    Capture(panel);
                    Walk(panel, night);
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
                    if (night && !(strip.Renderer is DarkRenderer)) strip.Renderer = new DarkRenderer();
                    else if (!night && strip.Renderer is DarkRenderer) strip.RenderMode = ToolStripRenderMode.ManagerRenderMode;
                }
                foreach (Control child in root.Controls) ThemeMenus(child, night);
            }
            catch (Exception)
            {
            }
        }

        private static bool menuNight;
        private static bool menuSet;

        // menus and drop down lists of every window (also of the main window of AgOpenGPS) follow the same look
        private static void SetMenuRenderer(bool night)
        {
            try
            {
                if (menuSet && menuNight == night) return;
                menuSet = true;
                menuNight = night;
                ToolStripManager.Renderer = night ? (ToolStripRenderer)new DarkRenderer() : new ToolStripProfessionalRenderer();
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
            State st;
            if (!States.TryGetValue(c, out st))
            {
                st = new State { Back = c.BackColor, Fore = c.ForeColor };
                var button = c as ButtonBase;
                if (button != null) st.UseVisual = button.UseVisualStyleBackColor;
                States.Add(c, st);
            }
            foreach (Control child in c.Controls) Capture(child);
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
            foreach (Control child in c.Controls) Walk(child, night);
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

        // a light background of a colour (pale green, light yellow ...) becomes a dark shade of the same colour
        private static Color DarkBack(Control c, Color original)
        {
            if (Spread(original) > 24)
            {
                // strong colours (pale green, light yellow ...) keep their hue, pastel blues and greys become the neutral dark
                if (original.GetSaturation() < 0.6f) return Color.FromArgb(255, 34, 37, 42);
                return FromHsl(original.GetHue(), Math.Min(0.45f, original.GetSaturation()), 0.24f);
            }
            if (c is TextBoxBase || c is ComboBox || c is ListBox || c is UpDownBase || c is ListView || c is TreeView) return InputBack;
            if (c is Button) return ButtonBack;
            return PageBack;
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

            bool imageSwapped = false;
            if (c.BackgroundImage != null && !(c is PictureBox) && !(c is ButtonBase))
            {
                Image orig = c.BackgroundImage;
                Image dark = DarkImage(orig);
                if (dark != null)
                {
                    st.OrigImage = orig;
                    c.BackgroundImage = dark;
                    imageSwapped = true;
                }
                else if (IsMostlyTransparent(orig))
                {
                    imageSwapped = true;
                }
            }

            // pictures (icons on buttons, drawings) were made for a light background: those keep it
            if ((imageSwapped || !HasPicture(c)) && st.Back.A == 255 && Luminance(st.Back) >= 0.62)
            {
                bool followsLightParent = c.Parent != null
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

            if (c is ScrollBar || c is ListBox || c is TreeView || c is ComboBox) ScrollBarTheme(c, true);

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
                    flat.FlatAppearance.CheckedBackColor = Spread(fl.Checked) > 24 ? DarkBack(c, fl.Checked) : Color.FromArgb(88, 94, 106);
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
                var ss = new StripState { Renderer = strip.Renderer };
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
            if (c is ScrollBar || c is ListBox || c is TreeView || c is ComboBox) ScrollBarTheme(c, false);

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
                strip.Renderer = ss.Renderer ?? new ToolStripProfessionalRenderer();
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
