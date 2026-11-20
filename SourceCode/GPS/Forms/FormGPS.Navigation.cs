using AgOpenGPS.Core.Models;
using AgOpenGPS.Forms;
using AgOpenGPS.Forms.Navigation;
using AgOpenGPS.Navigation;
using OpenTK.Graphics.OpenGL;
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace AgOpenGPS
{
    // Route navigation on the navigation map (no field open): a button in the top bar opens the destination menu;
    // the route is planned on the stored road network (offline), drawn on the ground and followed fix by fix with a
    // guidance bar at the top of the 3D view. Nothing of it runs while a field is open.
    public partial class FormGPS
    {
        private Button btnNavigation;
        private RoadNetwork roadNetwork;
        private bool roadNetworkLoaded;
        private RouteGuide navGuide;
        private GuideState navState;
        private Destination navDestination;
        private int navPlanning;              // 1 while a route is being planned on a worker thread
        private int roadDownloading;          // 1 while the road map is being downloaded

        /// <summary>Progress text of the road map download, or null when none runs. Read by the menu.</summary>
        public string RoadDownloadStatus { get; private set; }

        public bool IsNavigating => navGuide != null || navDestination != null;

        private static string RoadFile => Path.Combine(RegistrySettings.baseDirectory, "Navigation", "roads.bin");

        /// <summary>The stored road network (read from disk once).</summary>
        public RoadNetwork Roads
        {
            get
            {
                if (!roadNetworkLoaded)
                {
                    roadNetworkLoaded = true;
                    try { roadNetwork = RoadNetwork.Load(RoadFile); } catch (Exception) { roadNetwork = null; }
                }
                return roadNetwork;
            }
        }

        // ---- the button ----

        /// <summary>Makes the navigation button (once) and shows it only while no field is open.</summary>
        private void UpdateNavigationButton()
        {
            try
            {
                if (btnNavigation == null)
                {
                    btnNavigation = new Button
                    {
                        Name = "btnNavigation",
                        Location = new Point(2, 2),
                        Size = new Size(75, 38),
                        FlatStyle = FlatStyle.Flat,
                        BackColor = Color.Transparent,
                        UseVisualStyleBackColor = false,
                        Image = NavigationIcon(),
                        ImageAlign = ContentAlignment.MiddleCenter
                    };
                    btnNavigation.FlatAppearance.BorderSize = 0;
                    btnNavigation.Click += (s, e) => OpenNavigationMenu();
                    panelControlBox.Controls.Add(btnNavigation);
                    btnNavigation.BringToFront();
                }
                btnNavigation.Visible = !isJobStarted;
            }
            catch (Exception)
            {
            }
        }

        private void OpenNavigationMenu()
        {
            if (isJobStarted) return;
            using (var menu = new FormNavMenu(this))
            {
                menu.ShowDialog(this);
            }
        }

        // ---- road map download ----

        /// <summary>Downloads the roads within radiusKm of a point on a worker thread, then stores them.</summary>
        public void DownloadRoadMap(double lat, double lon, double radiusKm, Action<bool, string> done)
        {
            if (Interlocked.CompareExchange(ref roadDownloading, 1, 0) != 0) return;
            RoadDownloadStatus = AgLibrary.Localization.UiTranslator.TrText("Downloading road map...");
            ThreadPool.QueueUserWorkItem(_ =>
            {
                bool ok = false;
                string message = null;
                try
                {
                    AgOpenGPS.Navigation.NetSetup.EnsureModernTls();
                    string json;
                    try
                    {
                        json = FetchRoads(lat, lon, radiusKm);
                    }
                    catch (WebException ex) when (ex.Status == WebExceptionStatus.ProtocolError || ex.Status == WebExceptionStatus.Timeout)
                    {
                        // the public Overpass server is often busy (502 / 504): one more try after a short pause
                        Thread.Sleep(5000);
                        json = FetchRoads(lat, lon, radiusKm);
                    }
                    RoadNetwork net = OverpassParser.Parse(json, lat, lon, radiusKm);
                    if (net.NodeCount == 0) throw new InvalidOperationException("no roads");
                    net.Save(RoadFile);
                    roadNetwork = net;
                    roadNetworkLoaded = true;
                    ok = true;
                }
                catch (WebException ex)
                {
                    // a server that answers but is busy is not a missing connection
                    message = ex.Status == WebExceptionStatus.ProtocolError || ex.Status == WebExceptionStatus.Timeout
                        ? AgLibrary.Localization.UiTranslator.TrText("The map server is busy, try again later")
                        : AgLibrary.Localization.UiTranslator.TrText("No internet connection");
                }
                catch (Exception ex)
                {
                    message = ex.Message;
                }
                finally
                {
                    RoadDownloadStatus = null;
                    Interlocked.Exchange(ref roadDownloading, 0);
                }
                try { BeginInvoke(new Action(() => done?.Invoke(ok, message))); } catch (Exception) { }
            });
        }

        /// <summary>The default radius of the road map: about 6 MB and a minute or two in the countryside.</summary>
        public const double RoadMapRadiusKm = 20;

        private static string FetchRoads(double lat, double lon, double radiusKm)
        {
            using (var web = new WebClient())
            {
                web.Encoding = Encoding.UTF8;
                web.Headers[HttpRequestHeader.UserAgent] = AgOpenGPS.Navigation.TileSetup.Identity;
                web.Headers[HttpRequestHeader.ContentType] = "application/x-www-form-urlencoded";
                return web.UploadString("https://overpass-api.de/api/interpreter",
                    "data=" + Uri.EscapeDataString(OverpassParser.Query(lat, lon, radiusKm)));
            }
        }

        // ---- navigation ----

        /// <summary>Starts guiding to a destination: downloads the road map first when it does not cover the trip.</summary>
        public void StartNavigation(Destination destination)
        {
            if (destination == null || isJobStarted || !isFirstFixPositionSet) return;
            Wgs84 here = AppModel.CurrentLatLon;
            RoadNetwork net = Roads;
            if (net == null || !net.Covers(here.Latitude, here.Longitude) || !net.Covers(destination.Lat, destination.Lon))
            {
                DialogResult answer = FormDialog.ShowQuestion(AgLibrary.Localization.UiTranslator.TrText("Navigation"),
                    AgLibrary.Localization.UiTranslator.TrText("The road map does not cover this trip. Download it now?"));
                if (answer != DialogResult.OK) return;
                // a circle round both ends, at least the usual radius
                double midLat = (here.Latitude + destination.Lat) / 2, midLon = (here.Longitude + destination.Lon) / 2;
                double half = RoadNetwork.DistanceM(here.Latitude, here.Longitude, destination.Lat, destination.Lon) / 2000;
                navDestination = destination;
                UpdateNavigationMapVisibility();   // the street map comes on at once
                DownloadRoadMap(midLat, midLon, Math.Max(RoadMapRadiusKm, half + 10), (ok, message) =>
                {
                    if (ok) PlanRoute();
                    else
                    {
                        navDestination = null;
                        TimedMessageBox(3000, AgLibrary.Localization.UiTranslator.TrText("Navigation"), message ?? "");
                    }
                });
                return;
            }
            navDestination = destination;
            UpdateNavigationMapVisibility();   // the street map comes on at once
            PlanRoute();
        }

        public void StopNavigation()
        {
            bool was = IsNavigating;
            navDestination = null;
            navGuide = null;
            navState = null;
            if (was) UpdateNavigationMapVisibility();   // the display settings decide about the map again
        }

        // plans from where the tractor is now to the destination on a worker thread (a big network takes a moment)
        private void PlanRoute()
        {
            Destination target = navDestination;
            RoadNetwork net = Roads;
            if (target == null || net == null) return;
            if (Interlocked.CompareExchange(ref navPlanning, 1, 0) != 0) return;
            Wgs84 here = AppModel.CurrentLatLon;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                Route route = null;
                try { route = RoutePlanner.Plan(net, here.Latitude, here.Longitude, target.Lat, target.Lon); }
                catch (Exception) { route = null; }
                finally { Interlocked.Exchange(ref navPlanning, 0); }
                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        if (navDestination != target) return;   // stopped or changed meanwhile
                        if (route == null)
                        {
                            StopNavigation();
                            TimedMessageBox(3000, AgLibrary.Localization.UiTranslator.TrText("Navigation"),
                                AgLibrary.Localization.UiTranslator.TrText("No route found"));
                            return;
                        }
                        navGuide = new RouteGuide(route);
                        navState = navGuide.Update(here.Latitude, here.Longitude);
                    }));
                }
                catch (Exception)
                {
                }
            });
        }

        /// <summary>Called on every GPS fix: follows the route, re-plans when it was left, ends on arrival.</summary>
        public void UpdateNavigationGuide()
        {
            if (!IsNavigating) return;
            if (isJobStarted) { StopNavigation(); return; }
            if (navGuide == null) return;
            try
            {
                Wgs84 here = AppModel.CurrentLatLon;
                navState = navGuide.Update(here.Latitude, here.Longitude);
                if (navState.Arrived)
                {
                    StopNavigation();
                    TimedMessageBox(3000, AgLibrary.Localization.UiTranslator.TrText("Navigation"),
                        AgLibrary.Localization.UiTranslator.TrText("You have arrived"));
                }
                else if (navState.NeedsReplan)
                {
                    PlanRoute();
                }
            }
            catch (Exception)
            {
            }
        }

        // ---- drawing ----

        /// <summary>The route as a wide line on the ground (3D, after the map, before the vehicle).</summary>
        private void DrawNavigationRoute()
        {
            RouteGuide guide = navGuide;
            if (guide == null || isJobStarted || AppModel.LocalPlane == null) return;
            try
            {
                GL.LineWidth(9f);
                GL.Color4(0.12f, 0.53f, 0.90f, 0.95f);
                // only the part still ahead: from the route point nearest the tractor to the end (the driven part goes)
                GuideState s = navState;
                var points = guide.Route.Points;
                int first = 0;
                GL.Begin(PrimitiveType.LineStrip);
                if (s != null && s.Segment + 1 < points.Count)
                {
                    GeoCoord here = AppModel.LocalPlane.ConvertWgs84ToGeoCoord(new Wgs84(s.OnRoute.Lat, s.OnRoute.Lon));
                    GL.Vertex3(here.Easting, here.Northing, -0.04);
                    first = s.Segment + 1;
                }
                for (int i = first; i < points.Count; i++)
                {
                    GeoCoord g = AppModel.LocalPlane.ConvertWgs84ToGeoCoord(new Wgs84(points[i].Lat, points[i].Lon));
                    GL.Vertex3(g.Easting, g.Northing, -0.04);
                }
                GL.End();
                GL.LineWidth(1f);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>The guidance bar at the top of the 3D view (2D overlay, x from the centre, y from the top).</summary>
        private void DrawNavigationBar()
        {
            GuideState s = navState;
            if (isJobStarted) return;
            string line1, line2;
            if (navGuide == null || s == null)
            {
                if (navDestination == null) return;
                line1 = RoadDownloadStatus ?? AgLibrary.Localization.UiTranslator.TrText("Planning route...");
                line2 = navDestination.Name;
            }
            else
            {
                line1 = FormatDistance(s.NextTurnDistanceM) + "  " + TurnText(s.NextTurn) +
                    (string.IsNullOrEmpty(s.NextRoadName) || s.NextTurn == Manoeuvre.Arrive ? "" : " - " + s.NextRoadName);
                line2 = FormatDistance(s.RemainingM) + "   " + FormatDuration(s.RemainingS) + "   " +
                    AgLibrary.Localization.UiTranslator.TrText("Arrival") + ": " + ArrivalClock(s.RemainingS);
            }

            // the 3D view's own font has only the 128 ASCII characters: the bar is drawn with Windows text into a
            // picture (any language's letters) and put on the screen as a texture, made again only when it changes
            int width = Math.Min(oglMain.Width - 40, 640), height = 74;
            Manoeuvre turn = s != null && navGuide != null ? s.NextTurn : Manoeuvre.Straight;
            bool arrow = s != null && navGuide != null;
            string key = width + "|" + line1 + "|" + line2 + "|" + turn + "|" + arrow;
            if (key != navBarKey || navBarTexture == null)
            {
                navBarTexture?.Dispose();
                navBarTexture = new Core.DrawLib.Texture2D(RenderNavigationBar(width, height, line1, line2, arrow ? (Manoeuvre?)turn : null));
                navBarKey = key;
            }
            int left = -width / 2, top = 8;
            GL.Color4(1f, 1f, 1f, 1f);
            navBarTexture.Draw(new XyCoord(left, top), new XyCoord(left + width, top + height));
        }

        private Core.DrawLib.Texture2D navBarTexture;
        private string navBarKey;

        private static Bitmap RenderNavigationBar(int width, int height, string line1, string line2, Manoeuvre? turn)
        {
            var bmp = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                using (var back = new SolidBrush(Color.FromArgb(215, 20, 26, 33)))
                using (var path = new GraphicsPath())
                {
                    const int r = 12;
                    path.AddArc(0, 0, 2 * r, 2 * r, 180, 90);
                    path.AddArc(width - 2 * r - 1, 0, 2 * r, 2 * r, 270, 90);
                    path.AddArc(width - 2 * r - 1, height - 2 * r - 1, 2 * r, 2 * r, 0, 90);
                    path.AddArc(0, height - 2 * r - 1, 2 * r, 2 * r, 90, 90);
                    path.CloseFigure();
                    g.FillPath(back, path);
                }
                int textLeft = 16;
                if (turn.HasValue)
                {
                    DrawTurnArrow(g, 42, height / 2, turn.Value);
                    textLeft = 84;
                }
                var flags = System.Windows.Forms.TextFormatFlags.EndEllipsis | System.Windows.Forms.TextFormatFlags.NoPrefix |
                    System.Windows.Forms.TextFormatFlags.SingleLine | System.Windows.Forms.TextFormatFlags.VerticalCenter;
                using (var big = new System.Drawing.Font("Segoe UI Semibold", 17f))
                using (var small = new System.Drawing.Font("Segoe UI", 12f))
                {
                    TextRenderer.DrawText(g, line1, big, new Rectangle(textLeft, 4, width - textLeft - 12, 38), Color.White, flags);
                    TextRenderer.DrawText(g, line2, small, new Rectangle(textLeft, 40, width - textLeft - 12, 28), Color.FromArgb(190, 215, 255), flags);
                }
            }
            return bmp;
        }

        // an arrow bent the way of the turn, drawn round (cx, cy)
        private static void DrawTurnArrow(Graphics g, int cx, int cy, Manoeuvre turn)
        {
            using (var pen = new Pen(Color.FromArgb(77, 191, 255), 7f) { StartCap = LineCap.Round, LineJoin = LineJoin.Round })
            using (var brush = new SolidBrush(Color.FromArgb(77, 191, 255)))
            {
                if (turn == Manoeuvre.Arrive)
                {
                    g.DrawEllipse(pen, cx - 16, cy - 16, 32, 32);
                    g.FillEllipse(brush, cx - 6, cy - 6, 12, 12);
                    return;
                }
                double angle;
                switch (turn)
                {
                    case Manoeuvre.SlightLeft: angle = -40; break;
                    case Manoeuvre.Left: angle = -90; break;
                    case Manoeuvre.SharpLeft: angle = -135; break;
                    case Manoeuvre.SlightRight: angle = 40; break;
                    case Manoeuvre.Right: angle = 90; break;
                    case Manoeuvre.SharpRight: angle = 135; break;
                    default: angle = 0; break;
                }
                // the stem comes from below and bends at the middle towards the turn
                double rad = angle * Math.PI / 180, hx = Math.Sin(rad), hy = -Math.Cos(rad);
                float ex = (float)(cx + 18 * hx), ey = (float)(cy + 18 * hy);
                g.DrawLines(pen, new[] { new PointF(cx, cy + 26), new PointF(cx, cy), new PointF(ex, ey) });
                g.FillPolygon(brush, new[]
                {
                    new PointF((float)(ex + hx * 14), (float)(ey + hy * 14)),
                    new PointF((float)(ex - hy * 11), (float)(ey + hx * 11)),
                    new PointF((float)(ex + hy * 11), (float)(ey - hx * 11))
                });
            }
        }

        // the time left in the chosen language: "27 perc", and with hours for a long trip: "1 óra 20 perc"
        private static string FormatDuration(double seconds)
        {
            int minutes = (int)Math.Ceiling(Math.Max(0, seconds) / 60);
            string min = AgLibrary.Localization.UiTranslator.TrText("min");
            if (minutes < 60) return minutes + " " + min;
            return (minutes / 60) + " " + AgLibrary.Localization.UiTranslator.TrText("h") + " " + (minutes % 60) + " " + min;
        }

        // the time of day of the arrival, in the computer's own clock format (24 h or AM/PM), to the minute
        private static string ArrivalClock(double secondsLeft)
        {
            DateTime arrival = DateTime.Now.AddSeconds(Math.Max(0, secondsLeft));
            return arrival.ToString(System.Globalization.CultureInfo.CurrentCulture.DateTimeFormat.ShortTimePattern);
        }

        private string FormatDistance(double metres)
        {
            if (!isMetric)
            {
                double miles = metres / 1609.344;
                return miles >= 0.2 ? miles.ToString("0.0") + " mi" : ((int)(metres * 3.28084 / 10) * 10).ToString() + " ft";
            }
            return metres >= 1000 ? (metres / 1000).ToString("0.0") + " km" : ((int)(metres / 10) * 10).ToString() + " m";
        }

        private static string TurnText(Manoeuvre turn)
        {
            switch (turn)
            {
                case Manoeuvre.SlightLeft: return AgLibrary.Localization.UiTranslator.TrText("Bear left");
                case Manoeuvre.Left: return AgLibrary.Localization.UiTranslator.TrText("Turn left");
                case Manoeuvre.SharpLeft: return AgLibrary.Localization.UiTranslator.TrText("Sharp left");
                case Manoeuvre.SlightRight: return AgLibrary.Localization.UiTranslator.TrText("Bear right");
                case Manoeuvre.Right: return AgLibrary.Localization.UiTranslator.TrText("Turn right");
                case Manoeuvre.SharpRight: return AgLibrary.Localization.UiTranslator.TrText("Sharp right");
                case Manoeuvre.Arrive: return AgLibrary.Localization.UiTranslator.TrText("Destination");
                default: return "";
            }
        }

        // a navigation arrow (a filled chevron pointing up and to the right), drawn so no picture file is needed
        private static Bitmap NavigationIcon()
        {
            var bmp = new Bitmap(34, 34);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TranslateTransform(17, 17);
                g.RotateTransform(45);
                using (var path = new GraphicsPath())
                {
                    path.AddPolygon(new[] { new PointF(0, -14), new PointF(11, 12), new PointF(0, 6), new PointF(-11, 12) });
                    using (var fill = new SolidBrush(Color.FromArgb(30, 136, 229))) g.FillPath(fill, path);
                    using (var edge = new Pen(Color.White, 2f) { LineJoin = LineJoin.Round }) g.DrawPath(edge, path);
                }
            }
            return bmp;
        }
    }
}
