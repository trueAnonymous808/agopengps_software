using System;
using System.Drawing;
using System.Windows.Forms;

namespace AgOpenGPS
{
    // The MPC tab of the steer settings: in place of pure pursuit's look-ahead and integral when MPC steers - how calm
    // or sharp it steers, how far it looks ahead, and what it knows of the steering gear.
    public partial class FormSteer
    {
        private TabPage tabMpc;
        private HScrollBar hsbarMpcCalm, hsbarMpcHorizon;
        private Label lblMpcCalm, lblMpcHorizon, lblMpcModel, lblMpcTitle, lblMpcNote, lblMpcHorizonTitle, lblMpcHorizonUnit;

        private static string TrM(string english) => AgLibrary.Localization.UiTranslator.TrText(english);

        private void BuildMpcTab()
        {
            // the tab with the same look as pure pursuit's (the theme recolours it as the others)
            tabMpc = new TabPage
            {
                BackColor = tabPP.BackColor,
                ForeColor = Color.Black,
                ImageIndex = SteerTabIcons.MpcTabIndex,
                Size = tabPP.Size,
                Name = "tabMpc"
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
                tabMpc.Controls.Add(l);
                return l;
            }

            lblMpcTitle = AddLabel(TrM("Model predictive steering"), 6, 6, 353, 30, 14.25f, FontStyle.Bold);
            lblMpcTitle.ForeColor = Color.SteelBlue;

            // steering style: sharp (follows the line closely) or calm (fewer and smaller moves)
            AddLabel(TrM("Steering style"), 81, 44, 196, 24, 14.25f);
            AddLabel(TrM("Sharp"), 4, 48, 76, 19, 12f, FontStyle.Regular, ContentAlignment.MiddleRight);
            AddLabel(TrM("Calm"), 279, 48, 84, 19, 12f, FontStyle.Regular, ContentAlignment.MiddleLeft);
            lblMpcCalm = AddLabel("", 3, 67, 47, 35, 18f, FontStyle.Regular, ContentAlignment.MiddleRight);
            hsbarMpcCalm = new HScrollBar { Location = new Point(53, 70), Size = new Size(264, 30), Minimum = 1, Maximum = 10, LargeChange = 1 };
            hsbarMpcCalm.Value = Math.Max(1, Math.Min(10, Properties.ToolSettings.Default.setMpc_Calm));
            hsbarMpcCalm.ValueChanged += (s, e) =>
            {
                Properties.ToolSettings.Default.setMpc_Calm = hsbarMpcCalm.Value;
                ShowMpcValues();
            };
            tabMpc.Controls.Add(hsbarMpcCalm);

            // how far ahead it plans
            lblMpcHorizonTitle = AddLabel(TrM("Looks ahead"), 81, 116, 196, 24, 14.25f);
            lblMpcHorizon = AddLabel("", 3, 139, 47, 35, 18f, FontStyle.Regular, ContentAlignment.MiddleRight);
            hsbarMpcHorizon = new HScrollBar { Location = new Point(53, 142), Size = new Size(264, 30), Minimum = 20, Maximum = 50, LargeChange = 1 };
            hsbarMpcHorizon.Value = Math.Max(20, Math.Min(50, (int)Math.Round(Properties.ToolSettings.Default.setMpc_HorizonS * 10)));
            hsbarMpcHorizon.ValueChanged += (s, e) =>
            {
                Properties.ToolSettings.Default.setMpc_HorizonS = hsbarMpcHorizon.Value * 0.1;
                ShowMpcValues();
            };
            tabMpc.Controls.Add(hsbarMpcHorizon);
            lblMpcHorizonUnit = AddLabel("s", 320, 142, 40, 30, 14.25f, FontStyle.Regular, ContentAlignment.MiddleLeft);

            // what it knows of the steering gear (measured while driving)
            lblMpcModel = AddLabel("", 6, 190, 353, 84, 10.5f, FontStyle.Regular, ContentAlignment.TopLeft);
            lblMpcNote = AddLabel(TrM("MPC steers AB lines and curves going forwards; U-turns, contours and reversing use pure pursuit."),
                6, 280, 353, 56, 9.75f, FontStyle.Regular, ContentAlignment.TopCenter);
            ShowMpcValues();
        }

        private void ShowMpcValues()
        {
            if (tabMpc == null) return;
            // the same tab for LQR: its title and note, no horizon (it does not plan ahead)
            bool lqrTab = mf.isLqrUsed, hybridTab = mf.isHybridUsed;
            lblMpcTitle.Text = TrM(lqrTab ? "LQR steering" : hybridTab ? "Hybrid steering (LQR + MPC)" : "Model predictive steering");
            lblMpcNote.Text = TrM(lqrTab ? "LQR steers AB lines and curves going forwards; U-turns, contours and reversing use pure pursuit."
                : hybridTab ? "LQR holds the line, MPC takes the curves and coming onto the line. U-turns, contours and reversing use pure pursuit."
                : "MPC steers AB lines and curves going forwards; U-turns, contours and reversing use pure pursuit.");
            lblMpcHorizonTitle.Visible = lblMpcHorizon.Visible = lblMpcHorizonUnit.Visible = hsbarMpcHorizon.Visible = !lqrTab;
            // the bars follow what fully automatic tuning set meanwhile
            var ts = Properties.ToolSettings.Default;
            SetBar(hsbarMpcCalm, ts.setMpc_Calm);
            SetBar(hsbarMpcHorizon, (int)Math.Round(ts.setMpc_HorizonS * 10));
            lblMpcCalm.Text = hsbarMpcCalm.Value.ToString();
            lblMpcHorizon.Text = (hsbarMpcHorizon.Value * 0.1).ToString("0.0");
            CSteerDynamics d = mf.steerDynamics;
            string delay = d.Ready ? d.DelayS.ToString("0.00") + " s" : TrM("not measured yet") + " (0.20 s)";
            string rate = double.IsNaN(d.MaxRateDegS) ? TrM("not measured yet") + " (25 °/s)" : d.MaxRateDegS.ToString("0") + " °/s";
            lblMpcModel.Text = TrM("Dead time") + ": " + delay + "\n" +
                TrM("Turning speed") + ": " + rate + "\n" +
                TrM("Learnt offset") + ": " + mf.ModelOffsetDeg.ToString("+0.0;-0.0;0.0") + "°" + (mf.MpcUsesKalman ? " (Kalman)" : "") + "\n" +
                (hybridTab ? TrM("MPC share now") + ": " + (mf.HybridMpcShare * 100).ToString("0") + " %\n" : "") +
                TrM("One step") + ": " + mf.MpcSolveMs.ToString("0") + " ms" + (mf.MpcQuick ? " (" + TrM("quicker search") + ")" : "");
        }
    }
}
