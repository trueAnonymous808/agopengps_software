using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace AgOpenGPS
{
    // The Status page: what the program has learnt of the kit and what is still missing, at a glance; and the support
    // bundle and the accuracy report.
    public partial class FormSmartFeatures
    {
        private const int StatusRows = 12;
        private Label statusSummary, statusMessage;
        private readonly Label[] statusDot = new Label[StatusRows], statusName = new Label[StatusRows], statusDetail = new Label[StatusRows];

        private void BuildStatusPage(Panel page, int colW)
        {
            page.AutoScroll = false;
            const int Top = 40, RowH = 25;
            int cardH = Top + 26 + StatusRows * RowH + 6;
            Panel card = Card(page, 0, 0, colW, cardH, SmartIcons.Gps(), Tr("Setup status"));
            statusSummary = Lbl(10, Top, colW - 20, 22, 9.5f, true);
            card.Controls.Add(statusSummary);
            for (int i = 0; i < StatusRows; i++)
            {
                int y = Top + 26 + i * RowH;
                statusDot[i] = new Label { Location = new Point(10, y), Size = new Size(20, RowH), Text = "●", Font = new Font("Segoe UI", 12f), TextAlign = ContentAlignment.MiddleLeft };
                // (the names are long in some languages - German, French, Russian: room for them, and "..." beyond)
                statusName[i] = Lbl(32, y + 3, 228, RowH - 3, 8.5f, true);
                statusName[i].AutoEllipsis = true;
                statusDetail[i] = Lbl(264, y + 3, colW - 272, RowH - 3, 8.5f, false);
                statusDetail[i].AutoEllipsis = true;
                card.Controls.AddRange(new Control[] { statusDot[i], statusName[i], statusDetail[i] });
            }

            int by = cardH + 8, half = (colW - 10) / 2;
            Button bundle = Tile(Tr("Support bundle"), 0, by, half, 44, 10f);
            Button report = Tile(Tr("Accuracy report"), half + 10, by, half, 44, 10f);
            statusMessage = Lbl(2, by + 50, colW - 4, PageH - by - 52, 8.5f, false);
            statusMessage.ForeColor = Color.Gray;
            statusMessage.Text = Tr("The support bundle holds the logs, the settings and the last drive recordings (with positions on the field): send it to whoever helps you.");
            // (both worked out in the background: the steering never waits for them)
            bundle.Click += async (s, e) =>
            {
                bundle.Enabled = false;
                statusMessage.Text = Tr("Please wait...");
                try
                {
                    string path = await mf.MakeSupportBundleAsync();
                    if (IsDisposed) return;
                    statusMessage.Text = Tr("Saved") + ": " + path;
                    System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + path + "\"");
                }
                catch (Exception ex)
                {
                    if (!IsDisposed) statusMessage.Text = Tr("Not saved") + ": " + ex.Message;
                }
                finally
                {
                    if (!IsDisposed) bundle.Enabled = true;
                }
            };
            report.Click += async (s, e) =>
            {
                report.Enabled = false;
                statusMessage.Text = Tr("Please wait...");
                try
                {
                    string path = await mf.WriteAccuracyReportAsync();
                    if (IsDisposed) return;
                    statusMessage.Text = Tr("Saved") + ": " + path;
                    System.Diagnostics.Process.Start(path);
                }
                catch (Exception ex)
                {
                    if (!IsDisposed) statusMessage.Text = Tr("Not saved") + ": " + ex.Message;
                }
                finally
                {
                    if (!IsDisposed) report.Enabled = true;
                }
            };
            page.Controls.AddRange(new Control[] { bundle, report, statusMessage });
        }

        private void ShowStatus()
        {
            if (statusSummary == null) return;
            List<SetupItem> items = mf.SetupStatus();
            int ready = 0;
            for (int i = 0; i < StatusRows; i++)
            {
                bool shown = i < items.Count;
                statusDot[i].Visible = statusName[i].Visible = statusDetail[i].Visible = shown;
                if (!shown) continue;
                SetupItem it = items[i];
                if (it.State == SetupItem.Ok) ready++;
                Color c = it.State == SetupItem.Ok ? Color.ForestGreen : it.State == SetupItem.Attention ? Color.DarkOrange
                    : it.State == SetupItem.Problem ? Color.Firebrick : Color.Silver;
                if (statusDot[i].ForeColor.ToArgb() != c.ToArgb()) statusDot[i].ForeColor = c;
                if (statusName[i].Text != it.Name) statusName[i].Text = it.Name;
                if (statusDetail[i].Text != it.Detail) statusDetail[i].Text = it.Detail;
            }
            string sum = ready + " / " + items.Count + " " + Tr("ready");
            if (statusSummary.Text != sum) statusSummary.Text = sum;
        }
    }
}
