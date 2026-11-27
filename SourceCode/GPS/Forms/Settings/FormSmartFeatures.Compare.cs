using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace AgOpenGPS
{
    // The comparison page: every pass's distance off the line, and the A/B comparison of the steering methods.
    public partial class FormSmartFeatures
    {
        private Button abButton, abBestButton;
        private Label abState, abMethods;
        private ListBox passList;
        private Label kalmanFigures, kalmanState;
        private CheckBox kalmanSwitch, kalmanPosSwitch;

        // a method's name in the window's language (the Kalman ending kept)
        private string MethodText(string method)
        {
            string b = FormGPS.BaseMethod(method);
            return Tr(b) + (b.Length < method.Length ? FormGPS.KalmanSuffix : "");
        }
        private int passListShown = -1;

        private void BuildComparePage(Panel page, int colW)
        {
            page.AutoScroll = false;

            // (the passes are only shown here: no message on the main screen while driving)
            Panel ab = Card(page, 0, 0, colW, 200, SmartIcons.Speed(), Tr("A/B comparison"));
            Label abText = Lbl(10, 34, colW - 20, 36, 8.5f, false);
            abText.Text = Tr("The steering method changes after every pass: pure pursuit, adaptive, MPC, LQR, hybrid. Fully automatic tuning waits meanwhile.");
            abButton = Tile("", 10, 74, 190, 36, 10f);
            abButton.Click += (s, e) =>
            {
                if (mf.AbActive) mf.StopAb(false);
                else mf.StartAb();
                ShowState();
            };
            abBestButton = Tile(Tr("Keep the best"), 210, 74, 190, 36, 10f);
            abBestButton.Click += (s, e) => { mf.StopAb(true); ShowState(); };
            abState = Lbl(10, 114, colW - 20, 20, 9f, true);
            abMethods = Lbl(10, 136, colW - 20, 60, 9f, false);
            ab.Controls.AddRange(new Control[] { abText, abButton, abBestButton, abState, abMethods });

            // the Kalman state estimator: in shadow mode, what it has found
            Panel kalman = Card(page, 0, 206, colW, 140, SmartIcons.Gps(), Tr("State estimator (Kalman)"));
            kalmanFigures = Lbl(10, 32, colW - 136, 70, 8.5f, false);
            kalmanState = Lbl(10, 106, colW - 136, 30, 8.5f, true);
            Label kalmanText = Lbl(colW - 118, 28, 108, 16, 8f, false);
            kalmanText.Text = Tr("Model steering");
            kalmanText.TextAlign = ContentAlignment.MiddleCenter;
            kalmanSwitch = HelperSwitch(colW - 118, 45, mf.KalmanForMpc, on => { mf.SetKalmanForMpc(on); ShowState(); });
            kalmanSwitch.Height = 28;
            Label kalmanPosText = Lbl(colW - 118, 78, 108, 16, 8f, false);
            kalmanPosText.Text = Tr("Steering uses it");
            kalmanPosText.TextAlign = ContentAlignment.MiddleCenter;
            kalmanPosSwitch = HelperSwitch(colW - 118, 95, mf.KalmanPositionOn, on => { mf.SetKalmanPosition(on); ShowState(); });
            kalmanPosSwitch.Height = 28;
            kalman.Controls.AddRange(new Control[] { kalmanFigures, kalmanState, kalmanText, kalmanSwitch, kalmanPosText, kalmanPosSwitch });

            Panel last = Card(page, 0, 352, colW, PageH - 354, SmartIcons.Integral(), Tr("Last passes"));
            passList = new ListBox
            {
                Location = new Point(10, 36),
                Size = new Size(colW - 20, PageH - 354 - 46),
                Font = new Font("Segoe UI", 9f),
                BorderStyle = BorderStyle.None,
                IntegralHeight = false
            };
            last.Controls.Add(passList);
        }

        private void ShowCompare()
        {
            if (abButton == null) return;

            CStateEstimator k = mf.estimator;
            kalmanSwitch.Text = mf.KalmanForMpc ? Tr("On") : Tr("Off");
            kalmanPosSwitch.Text = mf.KalmanPositionOn ? Tr("On") : Tr("Off");
            if (!k.Started)
            {
                kalmanFigures.Text = Tr("Not started (needs a fix and a heading)");
                kalmanState.Text = "";
            }
            else
            {
                kalmanFigures.Text = Tr("Steer angle offset") + ": " + k.SteerOffsetDeg.ToString("+0.00;-0.00;0.00") + "° (± " +
                    (k.Sigma(CStateEstimator.SteerOff) * 180 / Math.PI).ToString("0.00") + ")   " + Tr("IMU offset") + ": " + k.ImuOffsetDeg.ToString("0.0") + "°\n" +
                    Tr("Slip") + ": " + (mf.headingFromSource == "Dual" ? (k.SlipMs * 100).ToString("0.0") + " cm/s" : Tr("only with a dual antenna")) + "\n" +
                    Tr("Off the GNSS") + ": " + k.PositionInnovationCm.ToString("0.0") + " cm";
                kalmanState.Text = !k.Ready ? Tr("Learning...")
                    : (mf.KalmanPositionInUse ? Tr("Ready, steering uses the filtered position") : mf.MpcUsesKalman && mf.ModelSteers ? Tr("Ready, model steering (MPC, LQR, hybrid) uses it") : Tr("Ready"))
                      + (mf.KalmanPositionOn && !mf.KalmanPositionInUse ? "\n" + Tr("Too far from the raw position: the raw one is used") : "");
                kalmanState.ForeColor = k.Ready ? Color.ForestGreen : Color.DarkOrange;
            }

            abButton.Text = mf.AbActive ? Tr("Stop (back as before)") : Tr("Start");
            string best = mf.AbActive ? mf.AbBest() : null;
            abBestButton.Enabled = best != null;
            var sum = CPassTracker.Summary(mf.AbActive ? mf.AbPasses : mf.PassLog);
            abState.Text = mf.AbActive
                ? Tr("Running") + ": " + Tr(mf.PassMethodName) + (best != null ? "   " + Tr("Best so far") + ": " + Tr(best) : "   " + Tr("two passes each first"))
                : Tr("All passes of this session") + ":";
            // nothing is learnt or counted in the simulator
            bool sim = mf.timerSim.Enabled;
            abButton.Enabled = mf.AbActive || !sim;
            if (sim && !mf.AbActive) abState.Text = Tr("Paused in the simulator");
            abMethods.Text = sum.Count == 0 ? Tr("No pass yet (at least 30 m with autosteer on a line)")
                : string.Join("\n", sum.OrderBy(kv => kv.Value.RmsCm).Select(kv =>
                    MethodText(kv.Key) + ": " + kv.Value.RmsCm.ToString("0.0") + " cm, " + kv.Value.Passes + " " + Tr("passes") + ", " + kv.Value.LengthM.ToString("0") + " m"));

            // the list only when a pass was added
            if (passListShown != mf.PassLog.Count)
            {
                passListShown = mf.PassLog.Count;
                passList.Items.Clear();
                foreach (PassResult p in mf.PassLog)
                    passList.Items.Add(p.When.ToString("HH:mm") + "  " + MethodText(p.Method) + ": " + p.RmsCm.ToString("0.0") + " cm, max " + p.MaxCm.ToString("0") +
                        " cm, " + p.LengthM.ToString("0") + " m");
            }
        }
    }
}
