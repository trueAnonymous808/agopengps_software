using AgOpenGPS.Navigation;
using System;
using System.Drawing;
using System.Windows.Forms;

namespace AgOpenGPS.Forms.Navigation
{
    // The navigation menu (top bar button, no field open): where to go - a saved field, an address or a point on the
    // map -, stopping the guidance, and the stored road map with its download.
    public class FormNavMenu : Form
    {
        private readonly FormGPS mf;
        private readonly Label lblRoads;
        private readonly Button btnDownload;
        private readonly Timer refresh = new Timer { Interval = 500 };

        public FormNavMenu(FormGPS mf)
        {
            this.mf = mf;
            Text = NavUi.Tr("Navigation");
            FormBorderStyle = FormBorderStyle.FixedToolWindow;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            ClientSize = new Size(560, 486);
            BackColor = NavUi.WindowBack;
            Font = new Font("Segoe UI Semibold", 13f);

            const int edge = 16, gap = 10;
            int w = (560 - 2 * edge - 2 * gap) / 3;
            Button field = NavUi.Tile(NavUi.Tr("Saved field"), edge, edge, w, 110);
            Button address = NavUi.Tile(NavUi.Tr("Address"), edge + w + gap, edge, w, 110);
            Button point = NavUi.Tile(NavUi.Tr("Point on the map"), edge + 2 * (w + gap), edge, w, 110);
            // the map under the route: the street map or the satellite picture (both sharp near the tractor)
            int half = (560 - 2 * edge - gap) / 2;
            string chosen = NavMapRules.NormalizeSource(Properties.Settings.Default.setNavMap_NavigationSource);
            RadioButton streetMap = MapChoice("OpenStreetMap", NavMapRules.OpenStreetMap, chosen, edge, 136, half);
            RadioButton satellite = MapChoice(NavUi.Tr("Satellite") + " (Bing)", NavMapRules.BingSatellite, chosen, edge + half + gap, 136, half);

            Button stop = NavUi.Accent(NavUi.Tile(NavUi.Tr("Stop navigation"), edge, 202, 560 - 2 * edge, 64),
                Color.FromArgb(255, 235, 238), Color.FromArgb(229, 57, 53));
            stop.Enabled = mf.IsNavigating;

            lblRoads = new Label
            {
                Location = new Point(edge, 288),
                Size = new Size(560 - 2 * edge, 60),
                Font = new Font("Segoe UI", 12f),
                TextAlign = ContentAlignment.MiddleLeft
            };
            btnDownload = NavUi.Tile(NavUi.Tr("Download road map"), edge, 356, 560 - 2 * edge, 64, 13f);
            Button close = NavUi.Tile("", 560 - edge - 96, 428, 96, 50);
            close.Image = Properties.Resources.Cancel64;
            close.FlatAppearance.BorderSize = 0;
            close.BackColor = NavUi.WindowBack;
            close.DialogResult = DialogResult.Cancel;
            CancelButton = close;

            field.Click += (s, e) => Choose(FormNavPickers.PickField(this, mf));
            address.Click += (s, e) => Choose(FormNavPickers.PickAddress(this, mf));
            point.Click += (s, e) => Choose(FormNavPickers.PickPoint(this, mf));
            stop.Click += (s, e) => { mf.StopNavigation(); Close(); };
            btnDownload.Click += (s, e) => Download();

            Controls.AddRange(new Control[] { field, address, point, streetMap, satellite, stop, lblRoads, btnDownload, close });
            refresh.Tick += (s, e) => ShowRoadStatus();
            refresh.Start();
            ShowRoadStatus();
        }

        // one of the two map switches: looks like a tile, green while chosen; the choice is saved and shown at once
        private RadioButton MapChoice(string text, string source, string chosen, int x, int y, int width)
        {
            var b = new RadioButton
            {
                Appearance = Appearance.Button,
                Text = text,
                Location = new Point(x, y),
                Size = new Size(width, 56),
                FlatStyle = FlatStyle.Flat,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI Semibold", 13f),
                BackColor = Color.FromArgb(246, 248, 251),
                UseVisualStyleBackColor = false,
                Checked = source == chosen
            };
            b.FlatAppearance.BorderColor = Color.FromArgb(205, 211, 220);
            b.FlatAppearance.CheckedBackColor = Color.FromArgb(197, 225, 165);
            b.CheckedChanged += (s, e) =>
            {
                if (((RadioButton)s).Checked) mf.SetNavigationMapSource(source);
            };
            return b;
        }

        private void Choose(Destination destination)
        {
            if (destination == null) return;
            Close();
            mf.StartNavigation(destination);
        }

        private void Download()
        {
            if (!mf.isFirstFixPositionSet) return;
            mf.DownloadRoadMap(mf.AppModel.CurrentLatLon.Latitude, mf.AppModel.CurrentLatLon.Longitude, FormGPS.RoadMapRadiusKm, (ok, message) =>
            {
                if (!ok && !IsDisposed) lblRoads.Text = message ?? "";
                else ShowRoadStatus();
            });
            ShowRoadStatus();
        }

        private void ShowRoadStatus()
        {
            if (IsDisposed) return;
            string status = mf.RoadDownloadStatus;
            btnDownload.Enabled = status == null && mf.isFirstFixPositionSet;
            if (status != null) { lblRoads.Text = status; return; }
            RoadNetwork net = mf.Roads;
            if (net == null) { lblRoads.Text = NavUi.Tr("No road map yet"); return; }
            bool here = mf.isFirstFixPositionSet && net.Covers(mf.AppModel.CurrentLatLon.Latitude, mf.AppModel.CurrentLatLon.Longitude);
            lblRoads.Text = NavUi.Tr("Road map") + ": " + net.RadiusKm.ToString("0") + " km" + (here ? " ✔" : " ✖");
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) refresh.Dispose();
            base.Dispose(disposing);
        }
    }
}
