using System;
using System.Drawing;
using System.Windows.Forms;

namespace AgOpenGPS
{
    // Adaptive pure pursuit in the steer settings: a switch beside the steer mode button (shown with pure pursuit)
    // and, while it is on, its own tab: how much a curve shortens the look-ahead, how much room the dead time gets,
    // how much weaving lengthens it - and what it uses now.
    public partial class FormSteer
    {
        private CheckBox cboxAdaptive;
        private Label labelAdaptive;
        private TabPage tabAdaptive;
        private HScrollBar hsbarAppCurve, hsbarAppFloor, hsbarAppWeave;
        private Label lblAppCurve, lblAppFloor, lblAppWeave, lblAppNow;

        private void BuildAdaptive()
        {
            // the switch: left of the steer mode button, as the reverse steering switch looks
            labelAdaptive = new Label
            {
                Text = TrM("Adaptive"),
                Location = new Point(btnStanleyPure.Left - 148, label34.Top),
                Size = new Size(108, label34.Height),
                Font = new Font(label34.Font.FontFamily, 12f),
                ForeColor = label34.ForeColor,
                BackColor = Color.Transparent,
                TextAlign = ContentAlignment.MiddleCenter
            };
            cboxAdaptive = new CheckBox
            {
                Appearance = Appearance.Button,
                Location = new Point(btnStanleyPure.Left - 129, btnStanleyPure.Top),
                Size = btnStanleyPure.Size,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.White,
                UseVisualStyleBackColor = false,
                Image = SteerTabIcons.AdaptiveMode(64, 64),
                ImageAlign = ContentAlignment.MiddleCenter,
                Checked = mf.AdaptivePpOn
            };
            cboxAdaptive.FlatAppearance.BorderColor = Color.Black;
            cboxAdaptive.FlatAppearance.CheckedBackColor = Color.LightGreen;
            cboxAdaptive.CheckedChanged += (s, e) =>
            {
                if (cboxAdaptive.Checked == mf.AdaptivePpOn) return;
                mf.SetAdaptivePp(cboxAdaptive.Checked);
                ShowSteerMode();
            };
            tabSettings.Controls.Add(labelAdaptive);
            tabSettings.Controls.Add(cboxAdaptive);
            cboxAdaptive.BringToFront();

            // the tab
            tabAdaptive = new TabPage
            {
                BackColor = tabPP.BackColor,
                ForeColor = Color.Black,
                ImageIndex = SteerTabIcons.AdaptiveTabIndex,
                Size = tabPP.Size,
                Name = "tabAdaptive"
            };
            Label AddLabel(string text, int x, int y, int w, int h, float size, FontStyle style = FontStyle.Regular, ContentAlignment align = ContentAlignment.MiddleCenter)
            {
                var l = new Label
                {
                    Text = text,
                    Location = new Point(x, y),
                    Size = new Size(w, h),
                    Font = new Font("Tahoma", size, style),
                    ForeColor = Color.Black,
                    BackColor = Color.Transparent,
                    TextAlign = align
                };
                tabAdaptive.Controls.Add(l);
                return l;
            }
            HScrollBar AddBar(int y, int min, int max, int value, Action<int> set)
            {
                var b = new HScrollBar { Location = new Point(53, y), Size = new Size(264, 30), Minimum = min, Maximum = max, LargeChange = 1 };
                b.Value = Math.Max(min, Math.Min(max, value));
                b.ValueChanged += (s, e) => { set(b.Value); ShowAdaptiveValues(); };
                tabAdaptive.Controls.Add(b);
                return b;
            }

            var ts = Properties.ToolSettings.Default;
            AddLabel(TrM("Adaptive pure pursuit"), 6, 4, 353, 28, 14.25f, FontStyle.Bold).ForeColor = Color.SteelBlue;

            // how much a curve shortens the look-ahead (0: not at all)
            AddLabel(TrM("Shorter in curves"), 53, 34, 264, 22, 12f);
            lblAppCurve = AddLabel("", 3, 56, 47, 32, 16f, FontStyle.Regular, ContentAlignment.MiddleRight);
            hsbarAppCurve = AddBar(58, 0, 10, ts.setApp_Curve, v => ts.setApp_Curve = v);

            // the room the dead time gets: more is calmer on a slow steering
            AddLabel(TrM("Room for the dead time"), 53, 94, 264, 22, 12f);
            lblAppFloor = AddLabel("", 3, 116, 47, 32, 16f, FontStyle.Regular, ContentAlignment.MiddleRight);
            hsbarAppFloor = AddBar(118, 10, 40, ts.setApp_Floor, v => ts.setApp_Floor = v);

            // how much weaving lengthens it (0: not at all)
            AddLabel(TrM("Longer when weaving"), 53, 154, 264, 22, 12f);
            lblAppWeave = AddLabel("", 3, 176, 47, 32, 16f, FontStyle.Regular, ContentAlignment.MiddleRight);
            hsbarAppWeave = AddBar(178, 0, 60, ts.setApp_Weave, v => ts.setApp_Weave = v);
            AddLabel("%", 320, 178, 40, 30, 14.25f, FontStyle.Regular, ContentAlignment.MiddleLeft);

            // what it uses now
            lblAppNow = AddLabel("", 6, 220, 353, 64, 10.5f, FontStyle.Regular, ContentAlignment.TopLeft);
            AddLabel(TrM("The look-ahead from the Pure pursuit tab is the start; this adapts it on every fix."),
                6, 288, 353, 50, 9.75f, FontStyle.Regular, ContentAlignment.TopCenter);
            ShowAdaptiveValues();
        }

        /// <summary>The steering method changed elsewhere (A/B comparison, smart features): its tabs and switches.</summary>
        internal void RefreshSteerMode()
        {
            if (IsDisposed || tabAdaptive == null) return;
            ShowSteerMode();
        }

        // a bar to a value set elsewhere (its handler writes the same value back: no loop)
        private static void SetBar(ScrollBar bar, int value)
        {
            int v = Math.Max(bar.Minimum, Math.Min(bar.Maximum, value));
            if (bar.Value != v) bar.Value = v;
        }

        private void ShowAdaptiveValues()
        {
            if (tabAdaptive == null) return;
            // 5. the bars follow what fully automatic tuning set meanwhile
            var ts = Properties.ToolSettings.Default;
            SetBar(hsbarAppCurve, ts.setApp_Curve);
            SetBar(hsbarAppFloor, ts.setApp_Floor);
            SetBar(hsbarAppWeave, ts.setApp_Weave);
            lblAppCurve.Text = hsbarAppCurve.Value.ToString();
            lblAppFloor.Text = (hsbarAppFloor.Value * 0.1).ToString("0.0");
            lblAppWeave.Text = hsbarAppWeave.Value.ToString();
            CSteerDynamics d = mf.steerDynamics;
            double now = mf.AdaptiveLookAheadM;
            double drawn = mf.vehicle.goalDistance;
            double setting = mf.vehicle.UpdateGoalPointDistance();
            mf.vehicle.goalDistance = drawn;
            lblAppNow.Text = TrM("Look-ahead now") + ": " + (double.IsNaN(now) ? "-" : now.ToString("0.0") + " m") + "   (" +
                TrM("setting") + " " + setting.ToString("0.0") + " m)\n" +
                TrM("Dead time") + ": " + (d.Ready ? d.DelayS.ToString("0.00") + " s" : TrM("not measured yet") + " (0.20 s)");
        }
    }
}
