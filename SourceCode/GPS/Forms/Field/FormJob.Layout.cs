using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace AgOpenGPS
{
    // The modern layout of the job menu, built over the designer's grid when the window opens.
    // The buttons, their texts and their click handlers are the designer's; only their place and look change:
    //
    //   [ Resume  <last field>                                  ]   the usual first step, wide and on top
    //   [ Drive In ]   [ Open ]   [ Easy Drive ]                     start work in a field that exists
    //   ---------------------------------------------------------
    //   [ New ] [ From Existing ] [ From KML ] [ ISO-XML ]           make a new field
    //   ---------------------------------------------------------
    //   [ AgShare Download ]   [ AgShare Upload ]                     only when AgShare is on
    //   [ Close field ]                                [ Cancel ]
    public partial class FormJob
    {
        private const int Edge = 16;   // the space round the window's content
        private const int Gap = 10;
        private const int ContentWidth = 620;

        // the y of the thin lines between the groups, painted by the window
        private readonly List<int> separators = new List<int>();

        private static readonly Font tileFont = new Font("Segoe UI Semibold", 14f, FontStyle.Regular, GraphicsUnit.Point);

        private static readonly Color TileBack = Color.FromArgb(246, 248, 251);
        private static readonly Color TileLine = Color.FromArgb(205, 211, 220);
        private static readonly Color TileOver = Color.FromArgb(232, 239, 248);
        private static readonly Color TileDown = Color.FromArgb(214, 226, 242);

        // (Visible cannot tell whether the AgShare buttons are meant to show: it is false for every control while the
        // window itself is not shown yet, so the caller says it)
        private void ApplyModernLayout(bool agShare, bool cloudNote)
        {
            SuspendLayout();
            try
            {
                // the buttons leave the designer's grid: the grid and its spacer labels go
                var buttons = new Button[] { btnJobResume, btnInField, btnJobOpen, btnEasyDrive, btnJobNew, btnFromExisting,
                    btnFromKML, btnFromISOXML, btnJobAgShare, btnAgShareBulkUpload, btnJobClose };
                foreach (Button b in buttons)
                {
                    tableLayoutPanel1.Controls.Remove(b);
                    b.Dock = DockStyle.None;
                    b.Anchor = AnchorStyles.Top | AnchorStyles.Left;
                    b.Margin = Padding.Empty;
                    Controls.Add(b);
                }
                Controls.Remove(tableLayoutPanel1);
                tableLayoutPanel1.Dispose();

                BackColor = Color.FromArgb(236, 239, 243);
                separators.Clear();
                int y = Edge;

                // resume: the field it opens is part of the button, not a line at the bottom of the window
                if (btnJobResume.Enabled || !string.IsNullOrEmpty(lblResumeField.Text))
                {
                    Wide(btnJobResume, y, 84, Color.FromArgb(232, 245, 234), Color.FromArgb(76, 175, 80), 15f);
                    if (!string.IsNullOrEmpty(lblResumeField.Text)) btnJobResume.Text = lblResumeField.Text;
                    y += 84 + Gap + 4;
                }
                else
                {
                    btnJobResume.Visible = false;
                }
                lblResumeField.Visible = false;

                y = TileRow(new[] { btnInField, btnJobOpen, btnEasyDrive }, y, 132);
                Tint(btnEasyDrive, Color.FromArgb(255, 248, 225), Color.FromArgb(255, 183, 77));
                y = Separator(y);
                y = TileRow(new[] { btnJobNew, btnFromExisting, btnFromKML, btnFromISOXML }, y, 132);

                if (agShare)
                {
                    y = Separator(y);
                    int w = (ContentWidth - Gap) / 2;
                    Side(btnJobAgShare, Edge, y, w, 64);
                    Side(btnAgShareBulkUpload, Edge + w + Gap, y, w, 64);
                    y += 64 + Gap;
                }

                if (cloudNote)
                {
                    lblAgShareCloudLoad.Anchor = AnchorStyles.Top | AnchorStyles.Left;
                    lblAgShareCloudLoad.SetBounds(Edge, y, ContentWidth, 22);
                    y += 22 + 4;
                }

                // the bottom bar: close the open field on the left, leave the menu on the right
                y += 6;
                Side(btnJobClose, Edge, y, 220, 64);
                btnDeleteAB.Anchor = AnchorStyles.Top | AnchorStyles.Left;
                btnDeleteAB.SetBounds(Edge + ContentWidth - 96, y, 96, 64);
                btnDeleteAB.FlatAppearance.BorderSize = 0;
                y += 64 + Edge;

                ClientSize = new Size(ContentWidth + 2 * Edge, y);
                Paint -= PaintSeparators;
                Paint += PaintSeparators;
            }
            finally
            {
                ResumeLayout(true);
            }
        }

        // a row of equal tiles across the window: icon above, caption below
        private int TileRow(Button[] row, int y, int height)
        {
            int w = (ContentWidth - (row.Length - 1) * Gap) / row.Length;
            for (int i = 0; i < row.Length; i++)
            {
                Button b = row[i];
                Style(b, 14f);
                b.SetBounds(Edge + i * (w + Gap), y, w, height);
                b.ImageAlign = ContentAlignment.TopCenter;
                b.TextAlign = ContentAlignment.BottomCenter;
                // Windows' own button layout gives an icon-above-caption tile only a narrow strip for the caption
                // (longer translations lost their last letter or ran into the icon): the tile paints itself
                b.Paint -= PaintTile;
                b.Paint += PaintTile;
                b.MouseEnter += TileStateChanged;
                b.MouseLeave += TileStateChanged;
                b.MouseDown += TileStateChanged;
                b.MouseUp += TileStateChanged;
            }
            return y + height + Gap;
        }

        private void TileStateChanged(object sender, System.EventArgs e)
        {
            ((Control)sender).Invalidate();
        }

        // background in the colour of its state, frame, icon at the top, caption below it: wrapped between words and
        // written smaller when it still does not fit. Every colour is the button's own, so the night look applies.
        private void PaintTile(object sender, PaintEventArgs e)
        {
            var b = (Button)sender;
            Rectangle r = b.ClientRectangle;
            Point mouse = b.PointToClient(Cursor.Position);
            bool over = b.Enabled && r.Contains(mouse);
            bool down = over && (MouseButtons & MouseButtons.Left) != 0;
            Color back = b.BackColor;
            if (down && !b.FlatAppearance.MouseDownBackColor.IsEmpty) back = b.FlatAppearance.MouseDownBackColor;
            else if (over && !b.FlatAppearance.MouseOverBackColor.IsEmpty) back = b.FlatAppearance.MouseOverBackColor;

            Graphics g = e.Graphics;
            using (var brush = new SolidBrush(back)) g.FillRectangle(brush, r);
            int border = b.FlatAppearance.BorderSize;
            if (border > 0)
            {
                using (var pen = new Pen(b.FlatAppearance.BorderColor, border))
                {
                    pen.Alignment = System.Drawing.Drawing2D.PenAlignment.Inset;
                    g.DrawRectangle(pen, 0, 0, r.Width - 1, r.Height - 1);
                }
            }

            int top = 10;
            if (b.Image != null)
            {
                var place = new Rectangle((r.Width - b.Image.Width) / 2, top, b.Image.Width, b.Image.Height);
                if (b.Enabled) g.DrawImage(b.Image, place);
                else
                {
                    using (var faded = new System.Drawing.Imaging.ImageAttributes())
                    {
                        faded.SetColorMatrix(new System.Drawing.Imaging.ColorMatrix { Matrix33 = 0.35f });
                        g.DrawImage(b.Image, place, 0, 0, b.Image.Width, b.Image.Height, GraphicsUnit.Pixel, faded);
                    }
                }
                top += b.Image.Height + 4;
            }

            string text = (b.Text ?? "").Trim();
            if (text.Length == 0) return;
            var area = new Rectangle(6, top, r.Width - 12, r.Height - top - 6);
            const TextFormatFlags wrap = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.HorizontalCenter;
            // every tile starts from the same size (the translator may have shrunk a long caption's own font already)
            Font font = tileFont;
            Font smaller = null;
            try
            {
                for (float size = font.Size; size >= font.Size * 0.6f; size -= 0.5f)
                {
                    Font f = size == font.Size ? font : new Font(font.FontFamily, size, font.Style, font.Unit);
                    if (!ReferenceEquals(f, font))
                    {
                        if (smaller != null) smaller.Dispose();
                        smaller = f;
                    }
                    bool fits = TextRenderer.MeasureText(g, text, f, new Size(area.Width, int.MaxValue), wrap).Height <= area.Height;
                    foreach (string word in text.Split(' '))
                        if (fits && TextRenderer.MeasureText(g, word, f, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPrefix).Width > area.Width) fits = false;
                    if (fits) break;
                }
                Font use = smaller ?? font;
                // the caption sits in the middle of the space under the icon
                int h = TextRenderer.MeasureText(g, text, use, new Size(area.Width, int.MaxValue), wrap).Height;
                var place = new Rectangle(area.X, area.Y + System.Math.Max(0, (area.Height - h) / 2), area.Width, System.Math.Min(h, area.Height));
                Color fore = b.Enabled ? b.ForeColor : Color.FromArgb(150, 154, 162);
                TextRenderer.DrawText(g, text, use, place, fore, wrap | TextFormatFlags.EndEllipsis);
            }
            finally
            {
                if (smaller != null) smaller.Dispose();
            }
        }

        // a wide button: icon on the left, caption beside it
        private void Wide(Button b, int y, int height, Color back, Color line, float size)
        {
            Side(b, Edge, y, ContentWidth, height);
            Style(b, size);
            Tint(b, back, line);
            b.Padding = new Padding(18, 0, 12, 0);
        }

        private void Side(Button b, int x, int y, int width, int height)
        {
            Style(b, 13f);
            b.SetBounds(x, y, width, height);
            b.ImageAlign = ContentAlignment.MiddleLeft;
            b.TextAlign = ContentAlignment.MiddleLeft;
            b.TextImageRelation = TextImageRelation.ImageBeforeText;
            b.Padding = new Padding(10, 0, 8, 0);
        }

        private static void Style(Button b, float size)
        {
            b.FlatStyle = FlatStyle.Flat;
            b.Font = new Font("Segoe UI Semibold", size, FontStyle.Regular, GraphicsUnit.Point);
            b.BackColor = TileBack;
            b.UseVisualStyleBackColor = false;
            b.FlatAppearance.BorderSize = 1;
            b.FlatAppearance.BorderColor = TileLine;
            b.FlatAppearance.MouseOverBackColor = TileOver;
            b.FlatAppearance.MouseDownBackColor = TileDown;
        }

        // a button that stands out: a light shade of its colour with a frame in the full colour
        private static void Tint(Button b, Color back, Color line)
        {
            b.BackColor = back;
            b.FlatAppearance.BorderColor = line;
            b.FlatAppearance.BorderSize = 2;
        }

        private int Separator(int y)
        {
            int line = y + 2;
            separators.Add(line);
            return line + 1 + Gap + 2;
        }

        private void PaintSeparators(object sender, PaintEventArgs e)
        {
            Color c = AgLibrary.Localization.UiTheme.IsNight() ? Color.FromArgb(70, 74, 82) : Color.FromArgb(205, 211, 220);
            using (var pen = new Pen(c))
                foreach (int line in separators)
                    e.Graphics.DrawLine(pen, Edge, line, Edge + ContentWidth, line);
        }
    }
}
