using AgOpenGPS.Navigation;
using System;
using System.Drawing;
using System.Windows.Forms;

namespace AgOpenGPS.Forms.Navigation
{
    // Route autosteer: how far right or left of the middle of the road to drive, for roads and for field tracks.
    // Every press is saved and used at once, also while it steers.
    internal static class FormSteerOffset
    {
        public static void Show(IWin32Window owner, FormGPS mf)
        {
            using (Form f = NavUi.Window(NavUi.Tr("Offset from the middle of the road"), 520, 290))
            {
                const int edge = 16;
                var hint = new Label
                {
                    Text = "◀ " + NavUi.Tr("left") + "        " + NavUi.Tr("right") + " ▶",
                    Location = new Point(edge, 12),
                    Size = new Size(520 - 2 * edge, 30),
                    Font = new Font("Segoe UI", 12f),
                    TextAlign = ContentAlignment.MiddleCenter
                };
                f.Controls.Add(hint);
                AddRow(f, NavUi.Tr("Road"), 52, () => mf.RouteSteerRoadOffset, v => mf.RouteSteerRoadOffset = v);
                AddRow(f, NavUi.Tr("Field track"), 132, () => mf.RouteSteerTrackOffset, v => mf.RouteSteerTrackOffset = v);

                Button close = NavUi.Tile("", 520 - edge - 96, 220, 96, 54);
                close.Image = Properties.Resources.OK64;
                close.FlatAppearance.BorderSize = 0;
                close.BackColor = NavUi.WindowBack;
                close.DialogResult = DialogResult.OK;
                f.AcceptButton = close;
                f.CancelButton = close;
                f.Controls.Add(close);
                f.ShowDialog(owner);
            }
        }

        // a row: name, [◀] value [▶]
        private static void AddRow(Form f, string name, int y, Func<double> get, Action<double> set)
        {
            const int edge = 16, button = 76, height = 64;
            var title = new Label
            {
                Text = name,
                Location = new Point(edge, y),
                Size = new Size(130, height),
                TextAlign = ContentAlignment.MiddleLeft
            };
            Button left = NavUi.Tile("◀", edge + 136, y, button, height, 18f);
            var value = new Label
            {
                Location = new Point(edge + 136 + button + 6, y),
                Size = new Size(520 - 2 * edge - 136 - 2 * button - 12, height),
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI Semibold", 15f),
                BackColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle
            };
            Button right = NavUi.Tile("▶", 520 - edge - button, y, button, height, 18f);

            Action show = () => value.Text = Describe(get());
            left.Click += (s, e) => { set(get() - RouteSteer.OffsetStepM); show(); };
            right.Click += (s, e) => { set(get() + RouteSteer.OffsetStepM); show(); };
            show();
            f.Controls.AddRange(new Control[] { title, left, value, right });
        }

        public static string Describe(double metres)
        {
            if (Math.Abs(metres) < 0.001) return NavUi.Tr("in the middle");
            return Math.Abs(metres).ToString("0.00") + " m " + NavUi.Tr(metres > 0 ? "right" : "left");
        }
    }
}
