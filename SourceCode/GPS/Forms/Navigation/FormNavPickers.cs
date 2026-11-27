using AgOpenGPS.Controls;
using AgOpenGPS.Navigation;
using GMap.NET;
using GMap.NET.MapProviders;
using GMap.NET.WindowsForms;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Net;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace AgOpenGPS.Forms.Navigation
{
    // The three ways to choose a destination: a saved field from a list, an address by search, a point by tapping the
    // map. Each returns the destination, or null when the driver cancels.
    internal static class FormNavPickers
    {
        // ---- a saved field ----

        public static Destination PickField(Form owner, FormGPS mf)
        {
            List<Destination> fields = Destinations.Fields(RegistrySettings.fieldsDirectory,
                mf.AppModel.CurrentLatLon.Latitude, mf.AppModel.CurrentLatLon.Longitude);
            Destination chosen = null;
            using (Form f = NavUi.Window(NavUi.Tr("Saved field"), 560, 560))
            {
                var list = new FlowLayoutPanel
                {
                    Location = new Point(16, 16),
                    Size = new Size(528, 460),
                    AutoScroll = true,
                    FlowDirection = FlowDirection.TopDown,
                    WrapContents = false
                };
                foreach (Destination d in fields)
                {
                    Button b = NavUi.Tile(d.Name + "   " + d.DistanceKm.ToString("0.0") + " km", 0, 0, 500, 56, 13f);
                    b.TextAlign = ContentAlignment.MiddleLeft;
                    b.Margin = new Padding(0, 0, 0, 6);
                    Destination target = d;
                    b.Click += (s, e) => { chosen = target; f.DialogResult = DialogResult.OK; };
                    list.Controls.Add(b);
                }
                if (fields.Count == 0) list.Controls.Add(new Label { Text = NavUi.Tr("No fields"), AutoSize = true });
                f.Controls.Add(list);
                f.Controls.Add(CancelTile(f, 560, 560));
                f.ShowDialog(owner);
            }
            return chosen;
        }

        // ---- an address ----

        private static DateTime lastSearch = DateTime.MinValue;

        public static Destination PickAddress(Form owner, FormGPS mf)
        {
            Destination chosen = null;
            using (Form f = NavUi.Window(NavUi.Tr("Address"), 560, 600))
            {
                var box = new TextBox { Location = new Point(16, 16), Size = new Size(380, 40), Font = new Font("Segoe UI", 16f) };
                Button search = NavUi.Tile(NavUi.Tr("Search"), 406, 14, 138, 44, 13f);
                var status = new Label { Location = new Point(16, 64), Size = new Size(528, 46), Font = new Font("Segoe UI", 11f) };
                var list = new FlowLayoutPanel
                {
                    Location = new Point(16, 112),
                    Size = new Size(528, 404),
                    AutoScroll = true,
                    FlowDirection = FlowDirection.TopDown,
                    WrapContents = false
                };
                box.Click += (s, e) => { if (mf.isKeyboardOn) box.ShowKeyboard(f); };
                f.AcceptButton = search;

                search.Click += (s, e) =>
                {
                    list.Controls.Clear();
                    string query = box.Text;
                    Search(f, query, search, status, found =>
                    {
                        // a house number asked for, but the map knows only the street: say so, the map will help
                        bool askedHouse = System.Text.RegularExpressions.Regex.IsMatch(query, @"\d");
                        bool anyExact = found.Exists(d => !d.Approximate);
                        if (askedHouse && !anyExact)
                            status.Text = NavUi.Tr("The house number is not on the map: choose the street and mark the house on the map");

                        foreach (Destination d in found)
                        {
                            Button b = NavUi.Tile((d.Approximate ? "≈ " : "") + d.Name, 0, 0, 500, 64, 11f);
                            b.TextAlign = ContentAlignment.MiddleLeft;
                            b.Margin = new Padding(0, 0, 0, 6);
                            Destination target = d;
                            b.Click += (s2, e2) =>
                            {
                                if (!target.Approximate)
                                {
                                    chosen = target;
                                    f.DialogResult = DialogResult.OK;
                                    return;
                                }
                                // only the street: the map opens there to pin the exact place
                                Destination pinned = PickPoint(f, mf, target);
                                if (pinned == null) return;
                                chosen = pinned;
                                f.DialogResult = DialogResult.OK;
                            };
                            list.Controls.Add(b);
                        }
                    });
                };

                f.Controls.AddRange(new Control[] { box, search, status, list, CancelTile(f, 560, 600) });
                f.ShowDialog(owner);
            }
            return chosen;
        }

        /// <summary>
        /// An address search (Nominatim, at most one a second) or, typed as "lat, lon", a point without the internet.
        /// The results come on the window's thread; the status line says what is going on.
        /// </summary>
        private static void Search(Form f, string text, Button search, Label status, Action<List<Destination>> show)
        {
            string query = (text ?? "").Trim();
            if (query.Length == 0) return;

            Destination point = Destinations.ParseCoordinates(query);
            if (point != null)
            {
                status.Text = "";
                show(new List<Destination> { point });
                return;
            }

            if ((DateTime.Now - lastSearch).TotalSeconds < 1.1) return;
            lastSearch = DateTime.Now;
            search.Enabled = false;
            status.Text = NavUi.Tr("Search") + "...";
            string url = Destinations.NominatimUrl(query, RegistrySettings.culture);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                List<Destination> found = null;
                string error = null;
                try
                {
                    AgOpenGPS.Navigation.NetSetup.EnsureModernTls();
                    using (var web = new WebClient())
                    {
                        web.Encoding = Encoding.UTF8;
                        web.Headers[HttpRequestHeader.UserAgent] = AgOpenGPS.Navigation.TileSetup.Identity;
                        found = Destinations.ParseNominatim(web.DownloadString(url));
                    }
                }
                catch (WebException) { error = NavUi.Tr("No internet connection"); }
                catch (Exception ex) { error = ex.Message; }
                try
                {
                    f.BeginInvoke(new Action(() =>
                    {
                        search.Enabled = true;
                        status.Text = error ?? (found.Count == 0 ? NavUi.Tr("Not found") : "");
                        if (found != null && found.Count > 0) show(found);
                    }));
                }
                catch (Exception)
                {
                    // the window was closed meanwhile
                }
            });
        }

        // ---- a point on the map ----

        /// <param name="start">Where to open (an address found only to the street): marked, close up, its name kept
        /// for a point tapped near it. Null: the tractor's place.</param>
        public static Destination PickPoint(Form owner, FormGPS mf, Destination start = null)
        {
            Destination chosen = null;
            // the program's own name towards OpenStreetMap (else "Access blocked" tiles), the shared tile cache
            TileSetup.Prepare(System.IO.Path.Combine(RegistrySettings.baseDirectory, "MapCache"));
            const int top = 62;   // the search bar over the map
            using (Form f = NavUi.Window(NavUi.Tr("Point on the map"), 900, 640 + top))
            {
                // search: an address or coordinates; the map goes there and marks it
                var box = new TextBox { Location = new Point(12, 12), Size = new Size(620, 40), Font = new Font("Segoe UI", 16f) };
                Button search = NavUi.Tile(NavUi.Tr("Search"), 642, 9, 140, 44, 13f);
                var status = new Label { Location = new Point(792, 12), Size = new Size(100, 40), Font = new Font("Segoe UI", 10f), TextAlign = ContentAlignment.MiddleLeft };
                var results = new ListBox
                {
                    Location = new Point(12, top - 6),
                    Size = new Size(770, 10),
                    Font = new Font("Segoe UI", 13f),
                    IntegralHeight = true,
                    Visible = false
                };
                box.Click += (s, e) => { if (mf.isKeyboardOn) box.ShowKeyboard(f); };
                f.AcceptButton = search;

                var map = new GMapControl
                {
                    Location = new Point(0, top),
                    Size = new Size(900, 560),
                    MapProvider = NavMapRules.NormalizeSource(Properties.Settings.Default.setNavMap_Source) == NavMapRules.BingSatellite
                        ? (GMapProvider)GMapProviders.BingSatelliteMap : GMapProviders.OpenStreetMap,
                    MinZoom = 5,
                    MaxZoom = 19,
                    Zoom = 14,
                    ShowCenter = false,
                    DragButton = MouseButtons.Left,
                    Position = new PointLatLng(mf.AppModel.CurrentLatLon.Latitude, mf.AppModel.CurrentLatLon.Longitude)
                };
                var overlay = new GMapOverlay();
                map.Overlays.Add(overlay);
                PointLatLng? picked = null;
                string pickedName = null;   // the found place's name; a tapped point is named by its coordinates
                int bottom = top + 572;
                Button ok = NavUi.Accent(NavUi.Tile(NavUi.Tr("Navigation"), 16, bottom, 300, 56),
                    Color.FromArgb(232, 245, 234), Color.FromArgb(76, 175, 80));
                ok.Enabled = false;
                Button zoomIn = NavUi.Tile("+", 340, bottom, 56, 56, 20f);
                Button zoomOut = NavUi.Tile("−", 404, bottom, 56, 56, 20f);
                zoomIn.Click += (s, e) => map.Zoom = Math.Min(map.MaxZoom, map.Zoom + 1);
                zoomOut.Click += (s, e) => map.Zoom = Math.Max(map.MinZoom, map.Zoom - 1);

                // the chosen point: a red marker, and Navigation can start
                Action<PointLatLng, string> mark = (p, name) =>
                {
                    picked = p;
                    pickedName = name;
                    overlay.Markers.Clear();
                    overlay.Markers.Add(new GMap.NET.WindowsForms.Markers.GMarkerGoogle(p, GMap.NET.WindowsForms.Markers.GMarkerGoogleType.red));
                    ok.Enabled = true;
                };

                // a found place: the map goes there, close enough to see the surroundings
                Action<Destination> goTo = d =>
                {
                    results.Visible = false;
                    var p = new PointLatLng(d.Lat, d.Lon);
                    map.Position = p;
                    map.Zoom = Math.Max(map.Zoom, 15);
                    mark(p, d.Name);
                };
                var found = new List<Destination>();
                results.Click += (s, e) => { if (results.SelectedIndex >= 0 && results.SelectedIndex < found.Count) goTo(found[results.SelectedIndex]); };
                search.Click += (s, e) =>
                {
                    results.Visible = false;
                    Search(f, box.Text, search, status, list =>
                    {
                        found.Clear();
                        found.AddRange(list);
                        if (found.Count == 1)
                        {
                            goTo(found[0]);
                            return;
                        }
                        // several: the driver picks one from the list over the map
                        results.Items.Clear();
                        foreach (Destination d in found) results.Items.Add(d.Name);
                        results.Height = Math.Min(6, found.Count) * results.ItemHeight + 6;
                        results.Visible = true;
                        results.BringToFront();
                    });
                };

                // a tap (not a drag) chooses the point
                Point down = Point.Empty;
                map.MouseDown += (s, e) => down = e.Location;
                map.MouseUp += (s, e) =>
                {
                    if (Math.Abs(e.X - down.X) > 8 || Math.Abs(e.Y - down.Y) > 8) return;
                    results.Visible = false;
                    mark(map.FromLocalToLatLng(e.X, e.Y), start?.Name);
                };
                ok.Click += (s, e) =>
                {
                    if (picked == null) return;
                    chosen = new Destination
                    {
                        Name = pickedName ?? picked.Value.Lat.ToString("0.00000") + ", " + picked.Value.Lng.ToString("0.00000"),
                        Lat = picked.Value.Lat,
                        Lon = picked.Value.Lng
                    };
                    f.DialogResult = DialogResult.OK;
                };
                f.Controls.AddRange(new Control[] { box, search, status, results, map, ok, zoomIn, zoomOut, CancelTile(f, 900, 640 + top) });

                // opened from an address known only to the street: there, close up, marked; a tap puts it on the house
                if (start != null)
                {
                    var p = new PointLatLng(start.Lat, start.Lon);
                    map.Position = p;
                    map.Zoom = 17;
                    mark(p, start.Name);
                    status.Text = "";
                    box.Text = "";
                }
                results.BringToFront();
                f.ShowDialog(owner);
            }
            return chosen;
        }

        private static Button CancelTile(Form f, int width, int height)
        {
            Button close = NavUi.Tile("", width - 16 - 96, height - 8 - 60, 96, 60);
            close.Image = Properties.Resources.Cancel64;
            close.FlatAppearance.BorderSize = 0;
            close.BackColor = NavUi.WindowBack;
            close.DialogResult = DialogResult.Cancel;
            f.CancelButton = close;
            return close;
        }
    }
}
