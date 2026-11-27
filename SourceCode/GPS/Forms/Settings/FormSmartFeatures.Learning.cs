using System;
using System.Drawing;
using System.Windows.Forms;

namespace AgOpenGPS
{
    // The learning page: the drive recorder, the steering gear and the IMU as measured, learning on the field and the
    // trailed implement kept on the line.
    public partial class FormSmartFeatures
    {
        private CheckBox recorderSwitch, fieldLearnSwitch, implementSwitch;
        private Label recorderState, gearModel, imuState, fieldLearnState, implementState;

        private void BuildLearningPage(Panel page, int colW)
        {
            page.AutoScroll = false;
            const int BtnW = 108, G = 6;
            int textW = colW - BtnW - 28;
            const int Top = 40;   // the text below the card's icon and title (they reach to 34)
            const int RecH = 108, GearH = 98, LearnH = 112;

            Panel rec = Card(page, 0, 0, colW, RecH, SmartIcons.Gps(), Tr("Drive recorder"));
            recorderState = Lbl(10, Top, textW, RecH - Top - 4, 8.5f, false);
            recorderSwitch = HelperSwitch(colW - BtnW - 10, Top, mf.RecorderOn, on => { mf.SetRecorder(on); ShowState(); });
            recorderSwitch.Height = 28;
            Button open = Tile(Tr("Folder"), colW - BtnW - 10, Top + 32, BtnW, 30, 9f);
            open.Click += (s, e) =>
            {
                try
                {
                    System.IO.Directory.CreateDirectory(FormGPS.RecordingsFolder);
                    System.Diagnostics.Process.Start(FormGPS.RecordingsFolder);
                }
                catch (Exception) { }
            };
            rec.Controls.AddRange(new Control[] { recorderState, recorderSwitch, open });

            int y = RecH + G;
            Panel gear = Card(page, 0, y, colW, GearH, SmartIcons.Motor(), Tr("Steering gear and IMU as measured"));
            gearModel = Lbl(10, Top, colW - 20, 34, 8.5f, false);
            imuState = Lbl(10, Top + 34, colW - 20, 20, 8.5f, false);
            gear.Controls.AddRange(new Control[] { gearModel, imuState });

            y += GearH + G;
            Panel learn = Card(page, 0, y, colW, LearnH, SmartIcons.Integral(), Tr("Learning on the field"));
            fieldLearnState = Lbl(10, Top, textW, LearnH - Top - 4, 8.5f, false);
            fieldLearnSwitch = HelperSwitch(colW - BtnW - 10, Top, mf.FieldLearningOn, on => { mf.SetFieldLearning(on); ShowState(); });
            fieldLearnSwitch.Height = 28;
            Button forget = Tile(Tr("Forget"), colW - BtnW - 10, Top + 32, BtnW, 30, 9f);
            forget.Click += (s, e) =>
            {
                if (AgOpenGPS.Forms.FormDialog.ShowQuestion(Tr("Learning on the field"), Tr("Forget what was learnt on this field?"),
                    AgOpenGPS.Forms.DialogSeverity.Warning) != DialogResult.OK) return;
                mf.ForgetFieldLearning();
                ShowState();
            };
            learn.Controls.AddRange(new Control[] { fieldLearnState, fieldLearnSwitch, forget });

            y += LearnH + G;
            Panel impl = Card(page, 0, y, colW, PageH - y - 2, SmartIcons.SideHill(), Tr("Trailed implement on the line"));
            implementState = Lbl(10, Top, textW, PageH - y - Top - 8, 8.5f, false);
            implementSwitch = HelperSwitch(colW - BtnW - 10, Top, mf.ImplementKeeperOn, on => { mf.SetImplementKeeper(on); ShowState(); });
            implementSwitch.Height = 28;
            impl.Controls.AddRange(new Control[] { implementState, implementSwitch });
        }

        private void ShowLearning()
        {
            if (recorderSwitch == null) return;

            recorderSwitch.Text = mf.RecorderOn ? Tr("On") : Tr("Off");
            recorderState.Text = !mf.RecorderOn ? Tr("Off: nothing is recorded")
                : mf.recorder.Recording ? Tr("Recording") + ": " + System.IO.Path.GetFileName(mf.recorder.FilePath) + ", " + mf.recorder.Lines + " " + Tr("fixes")
                : Tr("Records as soon as the tractor moves");
            // a write error: shown as the system tells it (a new file is tried a minute later)
            if (mf.RecorderOn && mf.RecorderError.Length > 0) recorderState.Text += "\n! " + mf.RecorderError;

            CSteerDynamics d = mf.steerDynamics;
            string R(double v) => double.IsNaN(v) ? "-" : v.ToString("0");
            gearModel.Text = !d.Ready ? Tr("Measured while autosteer steers") + ": " + Math.Min(d.Moving, CSteerDynamics.MinMoving) + " / " + CSteerDynamics.MinMoving
                : Tr("Dead time") + " " + d.DelayS.ToString("0.00") + " s, " + Tr("turning right") + " " + R(d.MaxRateRightDegS) + " °/s, " +
                  Tr("left") + " " + R(d.MaxRateLeftDegS) + " °/s, " + Tr("Play") + " " + (double.IsNaN(d.BacklashDeg) ? "-" : d.BacklashDeg.ToString("0.0")) + "°";
            CStateEstimator k = mf.estimator;
            imuState.Text = !k.Started ? Tr("IMU noise") + ": -"
                : Tr("IMU noise") + ": " + k.ImuNoiseDeg.ToString("0.00") + "° (" + (k.ImuNoiseDeg < 0.2 ? Tr("quiet") : Tr("noisier")) + ")";
            // a newer module firmware sums up all the IMU's readings between two fixes: how many a second
            if (mf.imuSummary.Fresh(mf.secondsSinceStart)) imuState.Text += "   " + mf.imuSummary.RateHz.ToString("0") + " Hz";

            fieldLearnSwitch.Text = mf.FieldLearningOn ? Tr("On") : Tr("Off");
            fieldLearnState.Text = Tr("Where it ran off the line before, it steers against in advance next time (each direction apart).") + "\n" +
                (mf.isJobStarted ? Tr("Places learnt on this field") + ": " + mf.fieldLearning.Count : Tr("Open a field to learn on it")) +
                (mf.FieldLearningOn && Math.Abs(mf.GuidanceShiftM) > 0.001 ? "   " + Tr("now") + ": " + (mf.GuidanceShiftM * 100).ToString("+0.0;-0.0") + " cm" : "");

            implementSwitch.Text = mf.ImplementKeeperOn ? Tr("On") : Tr("Off");
            implementState.Text = Tr("Keeps a trailed implement on the line in curves, where it cuts inside: the tractor runs wider as much as needed. An implement sliding down a slope it cannot see - that needs a GPS on the implement.") + "\n" +
                (!mf.tool.isToolTrailing ? Tr("The implement set up is not trailed: nothing to do")
                : Tr("Tractor beside the line") + ": " + (mf.implementKeeper.Shift(true) * -100).ToString("+0.0;-0.0;0.0") + " / " +
                  (mf.implementKeeper.Shift(false) * -100).ToString("+0.0;-0.0;0.0") + " cm (" + Tr("each way") + ")");
        }
    }
}
