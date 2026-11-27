using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace AgOpenGPS
{
    // The track gap wizard: the tractor drives an AB line one way, a mark goes on the ground under the hitch pin, it
    // comes back on the same line, and the gap between the hitch pin and the mark is measured. The tractor runs beside
    // the line by half of it - whatever the cause (roll zero, a dual antenna's heading, a hitch not in the middle) - and
    // the antenna's side offset takes it out. Not modal: the driver drives meanwhile; it stays beside the main window
    // and shows whether autosteer is on and how far off the line the tractor is.
    public class FormTrackGapWizard : Form
    {
        private const int W = 560, Pad = 14;

        private readonly FormGPS mf;
        private readonly Label title, text, live, preview;
        private readonly PictureBox picture;
        private readonly Button back, next, gapButton, sideLeft, sideRight;
        private readonly Timer refresh = new Timer { Interval = 400 };
        private int step;
        private double gapCm;
        private bool? markLeft;

        private static string Tr(string english) => AgLibrary.Localization.UiTranslator.TrText(english);

        private static bool Night => AgLibrary.Localization.UiTheme.IsNight();

        public FormTrackGapWizard(FormGPS mf)
        {
            this.mf = mf;
            Text = Tr("Track gap wizard");
            FormBorderStyle = FormBorderStyle.FixedToolWindow;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            Font = new Font("Segoe UI", 10f);
            ClientSize = new Size(W, 470);
            Rectangle area = Screen.FromControl(mf).WorkingArea;
            Location = new Point(Math.Max(area.Left, area.Right - Width - 20), Math.Max(area.Top, area.Top + 80));

            title = new Label { Location = new Point(Pad, 10), Size = new Size(W - 2 * Pad, 30), Font = new Font("Segoe UI Semibold", 14f) };
            picture = new PictureBox { Location = new Point(Pad, 44), Size = new Size(W - 2 * Pad, 190), SizeMode = PictureBoxSizeMode.Zoom };
            text = new Label { Location = new Point(Pad, 240), Size = new Size(W - 2 * Pad, 70), Font = new Font("Segoe UI", 11f) };
            live = new Label { Location = new Point(Pad, 312), Size = new Size(W - 2 * Pad, 26), Font = new Font("Segoe UI Semibold", 10.5f) };

            // the measuring step: the gap, the mark's side, what it will change
            gapButton = Big("", Pad, 246, 170, 64, 15f);
            gapButton.Click += (s, e) => AskGap();
            int sideW = (W - 2 * Pad - 180 - 8) / 2;
            sideLeft = Big(Tr("Mark on my left"), Pad + 180, 246, sideW, 64, 10.5f);
            sideRight = Big(Tr("Mark on my right"), Pad + 180 + sideW + 8, 246, sideW, 64, 10.5f);
            sideLeft.Image = SideDrawing(true);
            sideRight.Image = SideDrawing(false);
            foreach (Button b in new[] { sideLeft, sideRight })
            {
                b.ImageAlign = ContentAlignment.MiddleLeft;
                b.TextAlign = ContentAlignment.MiddleRight;
                b.TextImageRelation = TextImageRelation.ImageBeforeText;
            }
            sideLeft.Click += (s, e) => { markLeft = true; ShowStep(step); };
            sideRight.Click += (s, e) => { markLeft = false; ShowStep(step); };
            preview = new Label { Location = new Point(Pad, 318), Size = new Size(W - 2 * Pad, 60), Font = new Font("Segoe UI Semibold", 11f) };

            back = Big(Tr("Back"), Pad, 400, 130, 56, 12f);
            next = Big(Tr("Next"), W - Pad - 200, 400, 200, 56, 12f);
            back.Click += (s, e) => ShowStep(step - 1);
            next.Click += (s, e) => Next();

            Controls.AddRange(new Control[] { title, picture, text, live, gapButton, sideLeft, sideRight, preview, back, next });
            refresh.Tick += (s, e) => ShowLive();
            refresh.Start();
            FormClosed += (s, e) => refresh.Dispose();
            ShowStep(0);
        }

        // strong colours with their own text colour: readable by day and at night alike
        private static Button Big(string caption, int x, int y, int w, int h, float size)
        {
            var b = new Button
            {
                Text = caption,
                Location = new Point(x, y),
                Size = new Size(w, h),
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI Semibold", size),
                BackColor = Color.FromArgb(84, 110, 122),
                ForeColor = Color.White,
                UseVisualStyleBackColor = false
            };
            b.FlatAppearance.BorderColor = Color.FromArgb(55, 71, 79);
            return b;
        }

        private static void Colour(Button b, bool chosen)
        {
            Color back = chosen ? Color.FromArgb(67, 140, 45) : Color.FromArgb(84, 110, 122);
            if (b.BackColor.ToArgb() != back.ToArgb()) b.BackColor = back;
            if (b.ForeColor.ToArgb() != Color.White.ToArgb()) b.ForeColor = Color.White;
        }

        private void ShowStep(int s)
        {
            step = Math.Max(0, Math.Min(3, s));
            string[] titles = { Tr("Preparation"), Tr("First pass"), Tr("Back on the same line"), Tr("Measure") };
            string[] texts =
            {
                Tr("Flat, even ground. Choose an AB line and switch autosteer on; let the tractor settle on the line at working speed."),
                Tr("Drive at least 30 m on the line with autosteer, then stop. Put a mark on the ground right under the hitch pin."),
                Tr("Turn round and come back on the same AB line with autosteer. Stop with the hitch pin level with the mark."),
                ""
            };
            title.Text = (step + 1) + " / 4   " + titles[step];
            text.Text = texts[step];
            picture.Image?.Dispose();
            picture.Image = StepDrawing(step);

            bool measuring = step == 3;
            text.Visible = live.Visible = !measuring;
            gapButton.Visible = sideLeft.Visible = sideRight.Visible = preview.Visible = measuring;
            back.Enabled = step > 0;
            next.Text = measuring ? Tr("Apply") : Tr("Next");
            next.Enabled = !measuring || (gapCm > 0 && markLeft.HasValue);

            gapButton.Text = Tr("Gap") + "\n" + gapCm.ToString("0.0") + " cm";
            Colour(gapButton, gapCm > 0);
            Colour(sideLeft, markLeft == true);
            Colour(sideRight, markLeft == false);
            Colour(back, false);
            Colour(next, measuring && next.Enabled);

            double now = Properties.VehicleSettings.Default.setVehicle_antennaOffset * 100;
            if (gapCm > 0 && markLeft.HasValue)
            {
                double change = TrackGap.Correction(gapCm, markLeft.Value) * 100;
                preview.Text = Tr("Antenna offset") + ": " + now.ToString("0.0") + " → " + (now + change).ToString("0.0") + " cm  (" +
                    change.ToString("+0.0;-0.0") + " cm)";
            }
            else preview.Text = Tr("Antenna offset") + ": " + now.ToString("0.0") + " cm";
            ShowLive();
        }

        // what the driver needs while driving: autosteer on, and on the line
        private void ShowLive()
        {
            if (step == 3 || IsDisposed) return;
            bool on = mf.isBtnAutoSteerOn;
            short off = mf.guidanceLineDistanceOff;
            bool hasLine = off != 32000 && off != 32020;
            string distance = hasLine ? "   " + Tr("off the line") + ": " + (Math.Abs(off) / 10.0).ToString("0.0") + " cm" : "";
            live.Text = (on ? Tr("Autosteer on") : Tr("Autosteer off")) + distance;
            live.ForeColor = on && hasLine && Math.Abs(off) <= 30 ? Color.ForestGreen : Color.DarkOrange;
        }

        private void AskGap()
        {
            using (var f = new FormNumeric(0, 100, gapCm))
            {
                if (f.ShowDialog(this) == DialogResult.OK) gapCm = Math.Max(0, Math.Min(100, f.ReturnValue));
            }
            ShowStep(step);
        }

        private void Next()
        {
            if (step < 3)
            {
                ShowStep(step + 1);
                return;
            }
            if (gapCm <= 0 || !markLeft.HasValue) return;
            mf.ApplyTrackGap(gapCm, markLeft.Value);
            mf.TimedMessageBox(3000, Tr("Track gap wizard"), Tr("Antenna offset") + ": " +
                (Properties.VehicleSettings.Default.setVehicle_antennaOffset * 100).ToString("0.0") + " cm");
            Close();
        }

        // ---- drawings: seen from above, north up ----

        private static Color Ink => Night ? Color.FromArgb(225, 230, 237) : Color.FromArgb(55, 60, 70);

        /// <summary>The wizard's icon for the menu: two wheel tracks and the gap between them.</summary>
        public static Bitmap MenuIcon()
        {
            var bmp = new Bitmap(64, 64);
            using (Graphics g = Graphics.FromImage(bmp))
            using (var track1 = new Pen(Color.FromArgb(76, 175, 80), 5) { DashStyle = DashStyle.Dash })
            using (var track2 = new Pen(Color.FromArgb(255, 160, 0), 5) { DashStyle = DashStyle.Dash })
            using (var arrow = new Pen(Color.FromArgb(30, 136, 229), 3) { StartCap = LineCap.ArrowAnchor, EndCap = LineCap.ArrowAnchor })
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.DrawLine(track1, 20, 4, 20, 60);
                g.DrawLine(track2, 44, 4, 44, 60);
                g.DrawLine(arrow, 24, 32, 40, 32);
            }
            return bmp;
        }

        private static void Tractor(Graphics g, float cx, float cy, bool north, float s = 1)
        {
            GraphicsState st = g.Save();
            g.TranslateTransform(cx, cy);
            if (!north) g.RotateTransform(180);
            g.ScaleTransform(s, s);
            using (var tyre = new SolidBrush(Color.FromArgb(40, 44, 52)))
            using (var body = new SolidBrush(Color.FromArgb(198, 40, 40)))
            using (var cab = new SolidBrush(Color.FromArgb(128, 203, 196)))
            using (var hitch = new SolidBrush(Color.FromArgb(255, 160, 0)))
            using (var edge = new Pen(Color.FromArgb(60, 0, 0, 0), 1))
            {
                g.FillRectangle(tyre, -22, 4, 10, 22);
                g.FillRectangle(tyre, 12, 4, 10, 22);
                g.FillRectangle(tyre, -18, -26, 7, 13);
                g.FillRectangle(tyre, 11, -26, 7, 13);
                g.FillRectangle(body, -10, -30, 20, 34);
                g.FillRectangle(cab, -12, 2, 24, 22);
                g.DrawRectangle(edge, -12, 2, 24, 22);
                g.FillRectangle(body, -2, 24, 4, 10);   // the drawbar
                g.FillEllipse(hitch, -4, 32, 8, 8);     // the hitch pin
            }
            g.Restore(st);
        }

        private static void Mark(Graphics g, float x, float y)
        {
            using (var pen = new Pen(Color.FromArgb(229, 57, 53), 3))
            {
                g.DrawLine(pen, x - 7, y - 7, x + 7, y + 7);
                g.DrawLine(pen, x - 7, y + 7, x + 7, y - 7);
            }
        }

        private static Bitmap StepDrawing(int step)
        {
            var bmp = new Bitmap(532, 190);
            using (Graphics g = Graphics.FromImage(bmp))
            using (var field = new SolidBrush(Color.FromArgb(Night ? 60 : 90, 76, 175, 80)))
            using (var line = new Pen(Color.FromArgb(30, 136, 229), 2.5f) { DashStyle = DashStyle.Dash })
            using (var ink = new SolidBrush(Ink))
            using (var font = new Font("Segoe UI Semibold", 11f))
            using (var cap = new AdjustableArrowCap(5, 6))
            using (var arrow = new Pen(Ink, 2.5f) { CustomEndCap = cap })
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.FillRectangle(field, 0, 0, 532, 190);
                g.DrawLine(line, 266, 6, 266, 184);
                g.DrawString("A", font, ink, 274, 160);
                g.DrawString("B", font, ink, 274, 8);
                switch (step)
                {
                    case 0:
                        Tractor(g, 266, 120, true);
                        g.DrawLine(arrow, 330, 150, 330, 60);
                        break;
                    case 1:
                        Tractor(g, 266, 70, true);
                        Mark(g, 266, 106);
                        g.DrawLine(arrow, 330, 170, 330, 80);
                        g.DrawString("30 m+", font, ink, 340, 120);
                        break;
                    case 2:
                        Mark(g, 266, 106);
                        Tractor(g, 254, 142, false);   // back the other way, the hitch pin level with the mark
                        g.DrawLine(arrow, 330, 20, 330, 110);
                        break;
                    default:
                        Mark(g, 284, 100);
                        Tractor(g, 250, 136, false);
                        using (var dimCap = new AdjustableArrowCap(4, 5))
                        using (var dim = new Pen(Color.FromArgb(255, 160, 0), 2.5f) { CustomStartCap = dimCap, CustomEndCap = dimCap })
                            g.DrawLine(dim, 256, 100, 278, 100);
                        g.DrawString("cm", font, ink, 300, 90);
                        break;
                }
            }
            return bmp;
        }

        // seen from the seat (forward is up): the hitch pin behind, the mark on the left or the right of it
        private static Bitmap SideDrawing(bool left)
        {
            var bmp = new Bitmap(56, 56);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                Tractor(g, 28, 18, true, 0.55f);
                using (var pen = new Pen(Color.FromArgb(255, 82, 82), 3))
                {
                    float x = left ? 10 : 46, y = 40;
                    g.DrawLine(pen, x - 5, y - 5, x + 5, y + 5);
                    g.DrawLine(pen, x - 5, y + 5, x + 5, y - 5);
                }
            }
            return bmp;
        }
    }
}
