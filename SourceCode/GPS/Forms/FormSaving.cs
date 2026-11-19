using AgOpenGPS.Core.Translations;
using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;

namespace AgOpenGPS
{
    public enum SavingStepState { Pending, Done, Failed }

    public partial class FormSaving : Form
    {
        public FormSaving()
        {
            InitializeComponent();

            labelBeer.Text = "✔ " + gStr.gsSaveBeerTime;

            // the night look is applied while the window appears: the list must melt into its panel again,
            // and nothing may hold the focus (a focused list draws a dotted frame around its first step)
            Shown += (sender, e) =>
            {
                listViewSteps.BackColor = panel1.BackColor;
                ActiveControl = null;
                foreach (ListViewItem item in listViewSteps.Items)
                {
                    if (item.ForeColor != Color.Gray) item.ForeColor = DoneColor();
                }
            };
        }

        // finished steps stand out from the waiting grey ones: dark on the light panel, light on the night panel
        private Color DoneColor()
        {
            return panel1.BackColor.GetBrightness() < 0.5f ? Color.FromArgb(235, 235, 235) : Color.Black;
        }

        public void AddStep(string key, string message)
        {
            listViewSteps.Items.Add(new ListViewItem()
            {
                Name = key,
                Text = GetStepText(message + "...", SavingStepState.Pending),
                ForeColor = GetStepColor(SavingStepState.Pending)
            });
        }

        public void UpdateStep(string key, string message, SavingStepState state)
        {
            listViewSteps.Items[key].Text = GetStepText(message, state);
            listViewSteps.Items[key].ForeColor = GetStepColor(state);
        }

        public void Finish()
        {
            progressBar.Visible = false;
            labelBeer.Visible = true;
        }

        private static string GetStepText(string message, SavingStepState state)
        {
            switch (state)
            {
                case SavingStepState.Pending:
                    return "• " + message;
                case SavingStepState.Done:
                    return "✓ " + message;
                case SavingStepState.Failed:
                    return "✗ " + message;
                default:
                    throw new InvalidEnumArgumentException(nameof(state), (int)state, typeof(SavingStepState));
            }
        }

        private Color GetStepColor(SavingStepState state)
        {
            return state == SavingStepState.Pending ? Color.Gray : DoneColor();
        }
    }
}
