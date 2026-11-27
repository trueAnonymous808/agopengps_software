using System.Drawing;
using System.Windows.Forms;

namespace AgOpenGPS
{
    // The Directions page: the driving direction at the start - remembered from the last time (automatic) or told by
    // hand with a click on the tractor, as originally. Saved with the vehicle profile.
    public partial class FormSmartFeatures
    {
        private Button forwardAuto, forwardHand;
        private Label movedState;

        private void BuildDirectionsPage(Panel page, int colW)
        {
            Panel fwd = Card(page, 0, 0, colW, 156, SmartIcons.Directions(), Tr("Driving direction at the start"));
            int half = (colW - 30) / 2;
            forwardAuto = Tile(Tr("Remembered (automatic)"), 10, 38, half, 48, 10f);
            forwardHand = Tile(Tr("By hand (click the tractor)"), 20 + half, 38, half, 48, 10f);
            forwardAuto.Click += (s, e) => { mf.SetRememberForward(true); ShowDirections(); };
            forwardHand.Click += (s, e) => { mf.SetRememberForward(false); ShowDirections(); };
            var note = Lbl(10, 94, colW - 22, 56, 9f, false);
            note.Text = Tr("Single antenna: where the front is, is saved when the tractor stops and when the program closes. Started again in the same place, it is known at once.");
            note.ForeColor = Color.Gray;
            fwd.Controls.AddRange(new Control[] { forwardAuto, forwardHand, note });
            forwardAuto.BringToFront();
            forwardHand.BringToFront();

            // moved elsewhere with the tablet off: the IMU carries the front, or the jolt at the first move tells
            Panel moved = Card(page, 0, 162, colW, 150, SmartIcons.Gps(), Tr("Moved with the tablet off"));
            var movedNote = Lbl(10, 40, colW - 22, 52, 9f, false);
            movedNote.Text = Tr("If the module stays on while the tablet is off, its IMU counts every turn: the front is known wherever the tractor stands. If the module was off too, the jolt at the first move tells forwards from backwards.");
            movedNote.ForeColor = Color.Gray;
            movedState = Lbl(10, 96, colW - 22, 48, 9f, false);
            moved.Controls.AddRange(new Control[] { movedNote, movedState });
        }

        private void ShowDirections()
        {
            if (forwardAuto == null) return;

            // the new module firmware sends its time and the acceleration; the jolt is learnt while driving forwards
            bool newFirmware = mf.imuSummary.Seen && mf.imuSummary.ModuleUptimeMs >= 0 && mf.imuSummary.HasAccel;
            string firmware = newFirmware ? Tr("Module firmware: new (it sends its time and the acceleration)") : Tr("Needs the new module firmware");
            string jolt = mf.startDirection.Learnt ? Tr("Forwards or backwards at the first move: learnt")
                : Tr("Forwards or backwards at the first move: learning while driving forwards") + " (" + (mf.startDirection.LearntShare * 100).ToString("0") + " %)";
            string text = firmware + "\n" + jolt;
            if (movedState.Text != text) movedState.Text = text;
            // the chosen way in green, as the switches elsewhere
            bool remember = mf.RememberForward;
            Color chosenBack = Color.FromArgb(104, 159, 56), otherBack = Color.FromArgb(246, 248, 251);
            foreach (Button b in new[] { forwardAuto, forwardHand })
            {
                bool chosen = (b == forwardAuto) == remember;
                Color back = chosen ? chosenBack : otherBack;
                if (b.BackColor.ToArgb() != back.ToArgb()) b.BackColor = back;
                Color fore = chosen ? Color.White : Color.FromArgb(40, 44, 52);
                if (b.ForeColor.ToArgb() != fore.ToArgb()) b.ForeColor = fore;
            }
        }
    }
}
