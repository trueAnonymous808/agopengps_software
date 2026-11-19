using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace AgOpenGPS
{
    // The on / off switch of the navigation map (the live map while no field is open) on the display page, made like
    // the other display switches next to it.
    public partial class FormConfig
    {
        private CheckBox chkNavMap;

        private void AddNavMapSwitch()
        {
            chkNavMap = new CheckBox
            {
                Name = "chkNavMap",
                Appearance = Appearance.Button,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
                BackColor = Color.WhiteSmoke,
                FlatStyle = FlatStyle.Flat,
                Image = NavMapIcon(),
                ImageAlign = ContentAlignment.MiddleCenter,
                Location = new Point(691, 160),
                Size = new Size(100, 69),
                UseVisualStyleBackColor = false
            };
            chkNavMap.FlatAppearance.BorderColor = Color.Black;
            chkNavMap.FlatAppearance.BorderSize = 2;
            chkNavMap.FlatAppearance.CheckedBackColor = Color.LightGreen;

            var caption = new Label
            {
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
                AutoSize = false,
                BackColor = Color.Transparent,
                Font = new Font("Tahoma", 9.75F, FontStyle.Bold),
                Location = new Point(651, 232),
                Size = new Size(180, 16),
                Text = AgLibrary.Localization.UiTranslator.TrText("Navigation map"),
                TextAlign = ContentAlignment.MiddleCenter
            };

            // satellite picture instead of OpenStreetMap
            chkNavMapSatellite = new CheckBox
            {
                Name = "chkNavMapSatellite",
                Appearance = Appearance.Button,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
                BackColor = Color.WhiteSmoke,
                FlatStyle = FlatStyle.Flat,
                Image = SatelliteIcon(),
                ImageAlign = ContentAlignment.MiddleCenter,
                Location = new Point(691, 282),
                Size = new Size(100, 69),
                UseVisualStyleBackColor = false
            };
            chkNavMapSatellite.FlatAppearance.BorderColor = Color.Black;
            chkNavMapSatellite.FlatAppearance.BorderSize = 2;
            chkNavMapSatellite.FlatAppearance.CheckedBackColor = Color.LightGreen;
            var satelliteCaption = new Label
            {
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
                AutoSize = false,
                BackColor = Color.Transparent,
                Font = new Font("Tahoma", 9.75F, FontStyle.Bold),
                Location = new Point(651, 354),
                Size = new Size(180, 16),
                Text = AgLibrary.Localization.UiTranslator.TrText("Satellite"),
                TextAlign = ContentAlignment.MiddleCenter
            };

            tabDisplay.Controls.Add(chkNavMap);
            tabDisplay.Controls.Add(caption);
            tabDisplay.Controls.Add(chkNavMapSatellite);
            tabDisplay.Controls.Add(satelliteCaption);
            tabDisplay.Enter += (s, e) =>
            {
                chkNavMap.Checked = Properties.Settings.Default.setNavMap_Enabled;
                chkNavMapSatellite.Checked = NavMapRules.NormalizeSource(Properties.Settings.Default.setNavMap_Source) == NavMapRules.BingSatellite;
            };
        }

        private CheckBox chkNavMapSatellite;

        private void SaveNavMapSwitch()
        {
            if (chkNavMap == null) return;
            Properties.Settings.Default.setNavMap_Enabled = chkNavMap.Checked;
            Properties.Settings.Default.setNavMap_Source = chkNavMapSatellite.Checked ? NavMapRules.BingSatellite : NavMapRules.OpenStreetMap;
            mf.UpdateNavigationMapVisibility();
        }

        // a small satellite: a body with two solar panels
        private static Bitmap SatelliteIcon()
        {
            var bmp = new Bitmap(48, 42);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TranslateTransform(24, 21);
                g.RotateTransform(-35);
                using (var panel = new SolidBrush(Color.FromArgb(30, 136, 229)))
                using (var frame = new Pen(Color.FromArgb(13, 71, 161), 1.5f))
                {
                    g.FillRectangle(panel, -23, -6, 14, 12);
                    g.DrawRectangle(frame, -23, -6, 14, 12);
                    g.FillRectangle(panel, 9, -6, 14, 12);
                    g.DrawRectangle(frame, 9, -6, 14, 12);
                }
                using (var body = new SolidBrush(Color.FromArgb(158, 158, 158)))
                using (var edge = new Pen(Color.FromArgb(66, 66, 66), 1.5f))
                {
                    g.FillRectangle(body, -7, -8, 14, 16);
                    g.DrawRectangle(edge, -7, -8, 14, 16);
                    g.DrawLine(edge, -9, 0, -7, 0);
                    g.DrawLine(edge, 7, 0, 9, 0);
                }
            }
            return bmp;
        }

        // a folded map with a road and a location pin, drawn so no picture file is needed
        private static Bitmap NavMapIcon()
        {
            var bmp = new Bitmap(48, 42);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                PointF[][] panels =
                {
                    new[] { new PointF(2, 9), new PointF(16, 5), new PointF(16, 38), new PointF(2, 41) },
                    new[] { new PointF(16, 5), new PointF(31, 9), new PointF(31, 41), new PointF(16, 38) },
                    new[] { new PointF(31, 9), new PointF(45, 5), new PointF(45, 38), new PointF(31, 41) }
                };
                Color[] fills = { Color.FromArgb(129, 199, 132), Color.FromArgb(102, 175, 106), Color.FromArgb(129, 199, 132) };
                for (int i = 0; i < 3; i++)
                    using (var brush = new SolidBrush(fills[i])) g.FillPolygon(brush, panels[i]);
                using (var road = new Pen(Color.FromArgb(255, 241, 200), 3.5f)) g.DrawBezier(road, 3, 32, 15, 23, 25, 36, 44, 17);
                using (var line = new Pen(Color.FromArgb(46, 94, 49), 1.5f))
                    foreach (PointF[] panel in panels) g.DrawPolygon(line, panel);
                using (var pin = new GraphicsPath())
                {
                    pin.AddArc(26, 0, 14, 14, 135, 270);
                    pin.AddLine(38, 11, 33, 21);
                    pin.CloseFigure();
                    using (var red = new SolidBrush(Color.FromArgb(229, 57, 53))) g.FillPath(red, pin);
                    using (var edge = new Pen(Color.FromArgb(150, 30, 28), 1f)) g.DrawPath(edge, pin);
                }
                using (var dot = new SolidBrush(Color.White)) g.FillEllipse(dot, 30.5f, 4.5f, 5, 5);
            }
            return bmp;
        }
    }
}
