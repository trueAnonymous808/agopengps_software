using System.Drawing;
using System.Windows.Forms;

namespace AgOpenGPS.Forms.Navigation
{
    // The look of the navigation windows: the same tiles as the redesigned job menu, large enough for a finger.
    internal static class NavUi
    {
        public static readonly Color WindowBack = Color.FromArgb(236, 239, 243);

        public static Form Window(string title, int width, int height)
        {
            return new Form
            {
                Text = title,
                FormBorderStyle = FormBorderStyle.FixedToolWindow,
                StartPosition = FormStartPosition.CenterParent,
                ShowInTaskbar = false,
                MinimizeBox = false,
                MaximizeBox = false,
                ClientSize = new Size(width, height),
                BackColor = WindowBack,
                Font = new Font("Segoe UI Semibold", 13f)
            };
        }

        public static Button Tile(string text, int x, int y, int width, int height, float size = 15f)
        {
            var b = new Button
            {
                Text = text,
                Location = new Point(x, y),
                Size = new Size(width, height),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(246, 248, 251),
                UseVisualStyleBackColor = false,
                Font = new Font("Segoe UI Semibold", size),
                TextAlign = ContentAlignment.MiddleCenter
            };
            b.FlatAppearance.BorderColor = Color.FromArgb(205, 211, 220);
            b.FlatAppearance.MouseOverBackColor = Color.FromArgb(232, 239, 248);
            b.FlatAppearance.MouseDownBackColor = Color.FromArgb(214, 226, 242);
            return b;
        }

        /// <summary>A tile that stands out: a light shade of its colour with a frame in the full colour.</summary>
        public static Button Accent(Button b, Color back, Color line)
        {
            b.BackColor = back;
            b.FlatAppearance.BorderColor = line;
            b.FlatAppearance.BorderSize = 2;
            return b;
        }

        public static string Tr(string english)
        {
            return AgLibrary.Localization.UiTranslator.TrText(english);
        }
    }
}
