using AgLibrary.Logging;
using AgOpenGPS.Core.Models;
using OpenTK.Graphics.OpenGL;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace AgOpenGPS
{
    // Steering extras: the GPS quality guard (warning, marking the work done without RTK, autosteer off after a while),
    // the steer angle sensor's linearity correction on the angle sent to the module, the IMU fusion tuner, and the
    // speed dependent Kp (slow and fast, sent to the module as the speed changes).
    public partial class FormGPS
    {
        public readonly CGpsGuard gpsGuard = new CGpsGuard();
        public readonly CFusionTune fusionTune = new CFusionTune();
        public readonly CMotorTune motorTuneFast = new CMotorTune();   // the Kp above 11 km/h
        public readonly CRtkBridge rtkBridge = new CRtkBridge();
        private readonly System.Diagnostics.Stopwatch bridgeClock = System.Diagnostics.Stopwatch.StartNew();
        private double dualHeadingSeenS = -1e9;
        private string bridgeHeadingSource = "";
        private bool bridgeFailed, extrasFailed;

        /// <summary>Called when a dual antenna heading arrives (the bridge prefers it: it needs no corrections).</summary>
        public void NoteDualHeading() => dualHeadingSeenS = bridgeClock.Elapsed.TotalSeconds;

        /// <summary>The heading sensor the bridge uses now: "Dual", "IMU" or "" (steer angle, or nothing).</summary>
        public string BridgeHeadingSource => bridgeHeadingSource;

        // ---- TDCP: the receiver's raw carrier phase (RTCM MSM) via AgIO, movement to millimetres ----
        public const string TdcpMethod = "TDCP";
        public readonly AgOpenGPS.Tdcp.TdcpEngine tdcp = new AgOpenGPS.Tdcp.TdcpEngine();
        private System.Net.Sockets.Socket rawSocket;
        private readonly byte[] rawBuffer = new byte[16384];   // a u-blox RAWX with every signal is over 2 kB
        private System.Net.EndPoint rawFrom = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0);
        private bool rawListenFailed, bridgeUsesTdcp;
        private CRtkBridge tdcpExam;
        private double tdcpExamStartS, lastTdcpExamEndS = -1e9;
        private int tdcpExamNextCheckpoint;

        /// <summary>0 off, 1 learn only, 2 automatic.</summary>
        public int TdcpMode => Math.Max(0, Math.Min(2, Properties.Settings.Default.setTdcp_Mode));

        /// <summary>The real bridge uses TDCP right now (automatic mode, proven by its exams, data coming in).</summary>
        public bool BridgeUsesTdcp => rtkBridge.Active && bridgeUsesTdcp;

        /// <summary>Whether automatic mode would use TDCP now: data alive, enough exams, and better than dead reckoning.</summary>
        public bool TdcpTrusted(string drMethod)
        {
            if (!tdcp.Alive || bridgeExams.Count(TdcpMethod) < CBridgeExams.MinExams) return false;
            double t = bridgeExams.RateCmPerS(TdcpMethod);
            double d = drMethod.Length > 0 ? bridgeExams.RateCmPerS(drMethod) : double.NaN;
            return !double.IsNaN(t) && (double.IsNaN(d) || t < d);
        }

        private void ListenForRawGnss()
        {
            if (rawSocket != null || rawListenFailed) return;
            try
            {
                rawSocket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork, System.Net.Sockets.SocketType.Dgram, System.Net.Sockets.ProtocolType.Udp);
                rawSocket.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, AgLibrary.Gnss.Rtcm3Frames.LocalPort));
                rawSocket.BeginReceiveFrom(rawBuffer, 0, rawBuffer.Length, System.Net.Sockets.SocketFlags.None, ref rawFrom, RawReceived, null);
                Log.EventWriter("TDCP: listening for raw GNSS on port " + AgLibrary.Gnss.Rtcm3Frames.LocalPort);
            }
            catch (Exception ex)
            {
                rawListenFailed = true;
                rawSocket = null;
                Log.EventWriter("TDCP: raw GNSS port not available: " + ex.Message);
            }
        }

        private void RawReceived(IAsyncResult ar)
        {
            try
            {
                int n = rawSocket.EndReceiveFrom(ar, ref rawFrom);
                if (n > 0)
                {
                    var frame = new byte[n];
                    Array.Copy(rawBuffer, frame, n);
                    tdcp.Feed(frame);
                }
                rawSocket.BeginReceiveFrom(rawBuffer, 0, rawBuffer.Length, System.Net.Sockets.SocketFlags.None, ref rawFrom, RawReceived, null);
            }
            catch (ObjectDisposedException) { }
            catch (Exception)
            {
                try { rawSocket?.BeginReceiveFrom(rawBuffer, 0, rawBuffer.Length, System.Net.Sockets.SocketFlags.None, ref rawFrom, RawReceived, null); }
                catch (Exception) { }
            }
        }

        // the TDCP exam: like the dead reckoning one, with the measured movement
        private void RunTdcpExam(double t, bool rtkGood, double rawE, double rawN, double speedMs, double sensorDeg, double steerDeg,
            double tdcpE, double tdcpN, bool tdcpValid)
        {
            if (!Properties.Settings.Default.setRtkBridge_Exams || TdcpMode == 0 || !tdcp.Alive)
            {
                tdcpExam = null;
                return;
            }
            if (tdcpExam != null && (!rtkGood || rtkBridge.Active || isReverse))
            {
                tdcpExam = null;
                lastTdcpExamEndS = t;
                return;
            }
            if (tdcpExam == null)
            {
                if (!rtkGood || rtkBridge.Active || avgSpeed < 2 || isReverse || t - lastTdcpExamEndS < ExamIntervalS) return;
                tdcpExam = new CRtkBridge { BlendGnss = false, MaxSeconds = 1e9, MaxErrorCm = 1e9, UseExternal = true };
                tdcpExam.CopyLearningFrom(rtkBridge);
                tdcpExam.Step(t, true, CGpsGuard.RtkFix, rawE, rawN, speedMs, fixHeading, sensorDeg, steerDeg, vehicle.VehicleConfig.Wheelbase, out _, out _);
                tdcpExamStartS = t;
                tdcpExamNextCheckpoint = 0;
                return;
            }
            tdcpExam.SetExternalStep(tdcpE, tdcpN, tdcpValid);
            tdcpExam.Step(t, false, 5, rawE, rawN, speedMs, fixHeading, sensorDeg, steerDeg, vehicle.VehicleConfig.Wheelbase, out double se, out double sn);
            if (!tdcpExam.Active)
            {
                tdcpExam = null;
                lastTdcpExamEndS = t;
                return;
            }
            if (t - tdcpExamStartS >= CBridgeExams.Checkpoints[tdcpExamNextCheckpoint])
            {
                double errorCm = Math.Sqrt((se - rawE) * (se - rawE) + (sn - rawN) * (sn - rawN)) * 100;
                bridgeExams.Add(TdcpMethod, tdcpExamNextCheckpoint, errorCm);
                tdcpExamNextCheckpoint++;
                if (tdcpExamNextCheckpoint >= CBridgeExams.Checkpoints.Length)
                {
                    tdcpExam = null;
                    lastTdcpExamEndS = t;
                    SaveExams();
                }
            }
        }

        // ---- exams: virtual outages in the background, and what real outages showed ----
        public const double ExamIntervalS = 300;
        public readonly CBridgeExams bridgeExams = new CBridgeExams();
        private bool examsLoaded;
        private CRtkBridge exam;            // the shadow bridge of a virtual outage (never touches the real position)
        private string examMethod;
        private double examStartS, lastExamEndS = -1e9;
        private int examNextCheckpoint;

        /// <summary>The bridge method in use: dead reckoning with a dual antenna, an IMU or the steer angle.</summary>
        public string BridgeMethod { get; private set; } = "";

        /// <summary>A virtual outage is running in the background (nothing of it reaches the position or the steering).</summary>
        public bool ExamRunning => exam != null;

        private static string ExamFile()
        {
            string name = RegistrySettings.vehicleProfileName;
            foreach (char c in System.IO.Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            if (string.IsNullOrEmpty(name)) name = "default";
            return System.IO.Path.Combine(RegistrySettings.baseDirectory, "SmartFeatures", "BridgeExams_" + name + ".txt");
        }

        private void SaveExams()
        {
            try { bridgeExams.Save(ExamFile()); }
            catch (Exception ex) { Log.EventWriter("RTK bridge exams not saved: " + ex.Message); }
        }

        // a virtual outage: a shadow bridge started from the RTK position, compared with RTK at each checkpoint
        private void RunExam(double t, bool rtkGood, double rawE, double rawN, double speedMs, double sensorDeg, double steerDeg, string method)
        {
            if (!Properties.Settings.Default.setRtkBridge_Exams || method.Length == 0)
            {
                exam = null;
                return;
            }
            if (exam != null && (!rtkGood || rtkBridge.Active || method != examMethod || isReverse))
            {
                exam = null;   // a real outage or a change of sensor: this exam no longer says anything
                lastExamEndS = t;
                return;
            }
            if (exam == null)
            {
                if (!rtkGood || rtkBridge.Active || avgSpeed < 2 || isReverse || t - lastExamEndS < ExamIntervalS) return;
                exam = new CRtkBridge { BlendGnss = false, MaxSeconds = 1e9, MaxErrorCm = 1e9 };
                exam.CopyLearningFrom(rtkBridge);
                exam.Step(t, true, CGpsGuard.RtkFix, rawE, rawN, speedMs, fixHeading, sensorDeg, steerDeg, vehicle.VehicleConfig.Wheelbase, out _, out _);
                examMethod = method;
                examStartS = t;
                examNextCheckpoint = 0;
                return;
            }

            // the shadow bridge goes on as if RTK were lost; the real RTK position is the answer
            exam.Step(t, false, 5, rawE, rawN, speedMs, fixHeading, sensorDeg, steerDeg, vehicle.VehicleConfig.Wheelbase, out double se, out double sn);
            if (!exam.Active)
            {
                exam = null;   // the shadow could not follow the heading
                lastExamEndS = t;
                return;
            }
            double elapsed = t - examStartS;
            if (elapsed >= CBridgeExams.Checkpoints[examNextCheckpoint])
            {
                double errorCm = Math.Sqrt((se - rawE) * (se - rawE) + (sn - rawN) * (sn - rawN)) * 100;
                bridgeExams.Add(method, examNextCheckpoint, errorCm);
                examNextCheckpoint++;
                if (examNextCheckpoint >= CBridgeExams.Checkpoints.Length)
                {
                    exam = null;
                    lastExamEndS = t;
                    SaveExams();
                }
            }
        }

        /// <summary>RTK losses because the corrections stopped arriving, and because the receiver lost the fix itself.</summary>
        public int GpsLinkDrops { get; private set; }
        public int GpsSkyDrops { get; private set; }

        private readonly List<List<vec2>> gpsGuardMarks = new List<List<vec2>>();
        private bool gpsGuardDisengaged;
        private int lastSentKp = -1;
        private double lastKpSendS = -1e9;
        private Core.DrawLib.Texture2D gpsBannerTexture;
        private string gpsBannerKey;

        private static string TrX(string english) => AgLibrary.Localization.UiTranslator.TrText(english);

        /// <summary>The angle to send for a wanted real angle (degrees): the sensor's linearity taken into account.</summary>
        public double SteerSentDeg(double wantedDeg)
        {
            var vs = Properties.VehicleSettings.Default;
            return CWasLinearity.Apply(wantedDeg, vs.setAS_WasLinMiddle, vs.setAS_WasLinBig);
        }

        /// <summary>Whether the fusion tuner applies: a single antenna (heading from the fix) with an IMU.</summary>
        public bool FusionTunable => headingFromSource == "Fix" && ahrs.imuHeading != 99999;

        /// <summary>Called on every fix (from the auto-tune feed).</summary>
        private void FeedSteerExtras(bool steering, bool straightOnLine)
        {
            if (extrasFailed) return;
            try
            {
                FeedSteerExtrasNow(steering, straightOnLine);
            }
            catch (Exception ex)
            {
                // the guard, the fusion and the fast Kp must never stop the position: off for this session
                extrasFailed = true;
                Log.EventWriter("Steering extras switched off after an error: " + ex);
            }
        }

        private void FeedSteerExtrasNow(bool steering, bool straightOnLine)
        {
            UpdateGpsGuard();

            if (FusionTunable) fusionTune.AddSample(gpsHeading, fixHeading, straightOnLine && avgSpeed > 5);
            else fusionTune.AddSample(0, 0, false);

            motorTuneFast.AddSample(secondsSinceStart, SteerAimDeg(), mc.actualSteerAngleDegrees,
                avgSpeed, steering && avgSpeed >= 11);
            SendSpeedKp();
        }

        // ---- RTK bridge ----

        private void ApplyRtkBridge()
        {
            if (timerSim.Enabled || !Properties.Settings.Default.setRtkBridge_Enabled || bridgeFailed)
            {
                if (rtkBridge.Now != CRtkBridge.Phase.Idle) rtkBridge.Reset();
                return;
            }
            try
            {
                StepRtkBridge();
            }
            catch (Exception ex)
            {
                // the bridge must never stop the position: off for this session, the raw position goes on
                bridgeFailed = true;
                rtkBridge.Reset();
                Log.EventWriter("RTK bridge switched off after an error: " + ex);
            }
        }

        private void StepRtkBridge()
        {
            double t = bridgeClock.Elapsed.TotalSeconds;
            bool rtkGood = pn.fixQuality == CGpsGuard.RtkFix && pn.age <= CGpsGuard.OldCorrectionS;

            // the heading sensor: a dual antenna (needs no corrections) first, then an IMU, else none
            bool dualFresh = t - dualHeadingSeenS < 1.5 && (headingFromSource == "Dual" || ahrs.isDualAsIMU);
            bool imu = ahrs.imuHeading != 99999 && !ahrs.isDualAsIMU;
            string source = dualFresh ? "Dual" : imu ? "IMU" : "";
            double sensorDeg = dualFresh ? pn.headingTrueDual : imu ? ahrs.imuHeading : double.NaN;
            if (source != bridgeHeadingSource && !rtkBridge.Active)
            {
                rtkBridge.ResetLearning();   // what was learnt belongs to the other sensor
                bridgeHeadingSource = source;
            }

            // learn the sensor's offset and drift from the GPS heading while RTK is good and the tractor drives forward
            if (rtkGood && !double.IsNaN(sensorDeg) && avgSpeed > 2 && !isReverse) rtkBridge.Learn(t, gpsHeading, sensorDeg);

            // the steer angle only when a steer module answers
            bool steerModule = steerModuleConnectedCounter <= 30;
            double steerDeg = steerModule ? mc.actualSteerAngleDegrees : double.NaN;

            // the method, for what the exams have measured of it
            string method = source.Length > 0 ? "DR-" + source : steerModule ? "DR-Steer" : "";
            if (!rtkBridge.Active) BridgeMethod = method;
            if (!examsLoaded)
            {
                examsLoaded = true;
                bridgeExams.Load(ExamFile());
            }
            // TDCP: raw data in, the receiver's place for the satellite directions, the movement since the last fix
            if (TdcpMode > 0) ListenForRawGnss();
            tdcp.SetPosition(AppModel.CurrentLatLon.Latitude, AppModel.CurrentLatLon.Longitude, pn.altitude);
            tdcp.Take(out double tdcpE, out double tdcpN, out bool tdcpValid);

            // which method the real bridge takes: decided when it starts, kept while it runs
            if (!rtkBridge.Active) bridgeUsesTdcp = TdcpMode == 2 && TdcpTrusted(method);
            string usedMethod = bridgeUsesTdcp ? TdcpMethod : BridgeMethod;
            rtkBridge.UseExternal = bridgeUsesTdcp;
            rtkBridge.SetExternalStep(tdcpE, tdcpN, tdcpValid);
            rtkBridge.LearnedRateCmPerS = usedMethod.Length > 0 ? bridgeExams.RateCmPerS(usedMethod) : double.NaN;

            rtkBridge.MaxSeconds = Properties.Settings.Default.setRtkBridge_MaxS;
            rtkBridge.MaxErrorCm = Properties.Settings.Default.setRtkBridge_MaxCm;
            double kmh = pn.vtgSpeed != float.MaxValue && pn.vtgSpeed < 100 ? pn.vtgSpeed : avgSpeed;
            double speedMs = kmh / 3.6 * (isReverse ? -1 : 1);
            // virtual outages in the background (only while RTK is good; they never touch the position)
            RunExam(t, rtkGood, pn.fix.easting, pn.fix.northing, speedMs, sensorDeg, steerDeg, method);
            RunTdcpExam(t, rtkGood, pn.fix.easting, pn.fix.northing, speedMs, sensorDeg, steerDeg, tdcpE, tdcpN, tdcpValid);

            bool wasActive = rtkBridge.Active;
            rtkBridge.Step(t, rtkGood, pn.fixQuality, pn.fix.easting, pn.fix.northing, speedMs, fixHeading,
                sensorDeg, steerDeg, vehicle.VehicleConfig.Wheelbase, out double e, out double n);
            if (!double.IsNaN(e) && !double.IsNaN(n) && !double.IsInfinity(e) && !double.IsInfinity(n))
            {
                pn.fix.easting = e;
                pn.fix.northing = n;
            }

            if (wasActive && !rtkBridge.Active)
            {
                if (rtkGood)
                {
                    Log.EventWriter("RTK bridge: RTK back after " + rtkBridge.LastBridgeS.ToString("0") + " s, bridge was off by " +
                        rtkBridge.LastErrorAtEndCm.ToString("0.0") + " cm");
                    // a real outage is the best exam of all: it counts too
                    string realMethod = bridgeUsesTdcp ? TdcpMethod : BridgeMethod;
                    if (realMethod.Length > 0 && rtkBridge.LastBridgeS >= CBridgeExams.Checkpoints[0])
                    {
                        bridgeExams.AddReal(realMethod, rtkBridge.LastBridgeS, rtkBridge.LastErrorAtEndCm);
                        SaveExams();
                    }
                }
                else
                {
                    Log.EventWriter("RTK bridge: ended after " + rtkBridge.LastBridgeS.ToString("0") + " s (estimated error " + rtkBridge.ErrorCm.ToString("0") + " cm)");
                    TimedMessageBox(4000, TrX("RTK bridge ended"), TrX("The position is no longer bridged: without RTK it may be off by 10-50 cm."));
                }
            }
        }

        // ---- GPS quality guard ----

        private void UpdateGpsGuard()
        {
            if (!Properties.Settings.Default.setGpsGuard_Enabled || timerSim.Enabled)
            {
                if (gpsGuard.Now != CGpsGuard.State.Off) gpsGuard.Reset();   // the simulator cycles its fix quality on purpose
                return;
            }
            bool turned = gpsGuard.Update(secondsSinceStart, pn.fixQuality, pn.age);
            if (turned)
            {
                gpsGuardDisengaged = false;
                // why: corrections that stopped arriving (the link) or a receiver that lost the fix (sky, trees)
                bool link = pn.age > CGpsGuard.OldCorrectionS;
                if (link) GpsLinkDrops++;
                else GpsSkyDrops++;
                Log.EventWriter("GPS guard: RTK lost (fix " + pn.fixQuality + ", correction age " + pn.age.ToString("0.0") + " s, " + (link ? "corrections stopped" : "receiver lost the fix") + ")");
                string why = link
                    ? TrX("The corrections stopped arriving. A fallback correction source can be set up in AgIO.")
                    : TrX("Corrections arrive, the receiver lost the fix (trees, buildings, sky view).");
                string what = rtkBridge.Active ? TrX("The RTK bridge carries the position on.") : TrX("The work from here is marked red.");
                TimedMessageBox(5000, TrX("RTK fix lost"), why + "\r\n" + what);
                try { sounds.sndAutoSteerOff.Play(); } catch (Exception) { }
            }

            double offAfter = Properties.Settings.Default.setGpsGuard_OffAfterS;
            // while the bridge holds the position, steering goes on; when it ends, the usual time applies
            if (!gpsGuardDisengaged && !rtkBridge.Active && gpsGuard.ShouldDisengage(offAfter) && (isBtnAutoSteerOn || RouteSteerArmed))
            {
                gpsGuardDisengaged = true;
                if (isBtnAutoSteerOn) btnAutoSteer.PerformClick();
                if (RouteSteerArmed) SetRouteSteer(false);
                Log.EventWriter("GPS guard: autosteer off after " + offAfter.ToString("0") + " s without RTK");
                TimedMessageBox(5000, TrX("Autosteer off"), TrX("No RTK fix for too long."));
            }

            // the work done without RTK: red marks over it (kept while the field is open)
            if (!isJobStarted)
            {
                if (gpsGuardMarks.Count > 0) gpsGuardMarks.Clear();
                return;
            }
            if (gpsGuard.Now == CGpsGuard.State.Degraded && !rtkBridge.Active)
            {
                List<vec2> mark = gpsGuardMarks.Count > 0 ? gpsGuardMarks[gpsGuardMarks.Count - 1] : null;
                if (turned || mark == null)
                {
                    mark = new List<vec2>();
                    gpsGuardMarks.Add(mark);
                }
                var here = new vec2(pivotAxlePos.easting, pivotAxlePos.northing);
                if (mark.Count == 0 || Math.Abs(mark[mark.Count - 1].easting - here.easting) + Math.Abs(mark[mark.Count - 1].northing - here.northing) > 1)
                    mark.Add(here);
            }
            else if (gpsGuardMarks.Count > 0 && gpsGuardMarks[gpsGuardMarks.Count - 1].Count > 0)
            {
                gpsGuardMarks.Add(new List<vec2>());   // the next bad stretch starts a new mark
            }
        }

        /// <summary>Metres of work marked as done without RTK in this field.</summary>
        public double GpsGuardMarkedM
        {
            get
            {
                double m = 0;
                foreach (List<vec2> mark in gpsGuardMarks)
                    for (int i = 1; i < mark.Count; i++)
                        m += Math.Sqrt(Math.Pow(mark[i].easting - mark[i - 1].easting, 2) + Math.Pow(mark[i].northing - mark[i - 1].northing, 2));
                return m;
            }
        }

        /// <summary>The red marks, in the 3D view over the worked area.</summary>
        private void DrawGpsGuardMarks()
        {
            if (gpsGuardMarks.Count == 0) return;
            GL.LineWidth(6f);
            GL.Color4(0.9f, 0.15f, 0.15f, 0.85f);
            foreach (List<vec2> mark in gpsGuardMarks)
            {
                if (mark.Count < 2) continue;
                GL.Begin(PrimitiveType.LineStrip);
                foreach (vec2 p in mark) GL.Vertex3(p.easting, p.northing, 0.02);
                GL.End();
            }
            GL.LineWidth(1f);
        }

        /// <summary>A red bar at the top while the RTK fix is lost (2D overlay, under the navigation bar if shown).</summary>
        private void DrawGpsGuardBanner()
        {
            if (gpsGuard.Now != CGpsGuard.State.Degraded) return;
            bool bridged = rtkBridge.Active;
            string quality = pn.fixQuality == 5 ? "FLOAT" : pn.fixQuality == 2 ? "DGPS" : pn.fixQuality == 1 ? "GPS" : pn.fixQuality == 4 ? TrX("old correction") : TrX("no fix");
            string text = bridged
                ? TrX("RTK bridge") + " - " + quality + " - " + ((int)rtkBridge.Seconds / 60) + ":" + ((int)rtkBridge.Seconds % 60).ToString("00") + " - ±" + rtkBridge.ErrorCm.ToString("0") + " cm"
                : TrX("RTK fix lost") + " - " + quality + " - " + ((int)gpsGuard.DegradedForS) + " s";
            int width = Math.Min(oglMain.Width - 40, 520), height = 44;
            string key = width + "|" + text + "|" + bridged;
            if (key != gpsBannerKey || gpsBannerTexture == null)
            {
                gpsBannerTexture?.Dispose();
                gpsBannerTexture = new Core.DrawLib.Texture2D(RenderBanner(width, height, text, bridged));
                gpsBannerKey = key;
            }
            int top = 8 + (IsNavigating && !isJobStarted ? 110 : 0);
            GL.Color4(1f, 1f, 1f, 1f);
            gpsBannerTexture.Draw(new XyCoord(-width / 2, top), new XyCoord(width / 2, top + height));
        }

        private static Bitmap RenderBanner(int width, int height, string text, bool bridged)
        {
            var bmp = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            using (var back = new SolidBrush(bridged ? Color.FromArgb(230, 230, 160, 20) : Color.FromArgb(230, 198, 40, 40)))
            using (var path = new GraphicsPath())
            using (var font = new Font("Segoe UI Semibold", 15f))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                const int r = 10;
                path.AddArc(0, 0, 2 * r, 2 * r, 180, 90);
                path.AddArc(width - 2 * r - 1, 0, 2 * r, 2 * r, 270, 90);
                path.AddArc(width - 2 * r - 1, height - 2 * r - 1, 2 * r, 2 * r, 0, 90);
                path.AddArc(0, height - 2 * r - 1, 2 * r, 2 * r, 90, 90);
                path.CloseFigure();
                g.FillPath(back, path);
                TextRenderer.DrawText(g, "⚠ " + text, font, new Rectangle(0, 0, width, height), Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
            }
            return bmp;
        }

        // ---- recommendations shared by the window and fully automatic ----

        /// <summary>The linearity factors the geometry calibration has measured (the current ones where a band has too few).</summary>
        public TuneVerdict LinearityRecommend(out double middle, out double big)
        {
            var vs = Properties.VehicleSettings.Default;
            steerGeometryCal.Linearity(out double m, out double b);
            middle = double.IsNaN(m) ? vs.setAS_WasLinMiddle : Math.Round(CWasLinearity.Clamp(m), 3);
            big = double.IsNaN(b) ? vs.setAS_WasLinBig : Math.Round(CWasLinearity.Clamp(b), 3);
            if (double.IsNaN(m) && double.IsNaN(b)) return TuneVerdict.Collecting;
            if (Math.Abs(middle - vs.setAS_WasLinMiddle) < 0.02 && Math.Abs(big - vs.setAS_WasLinBig) < 0.02) return TuneVerdict.Good;
            return TuneVerdict.SensorNotLinear;
        }

        /// <summary>The fast Kp (from 12 km/h) the fast-side motor measurements ask for.</summary>
        public TuneVerdict FastKpRecommend(out int kpFast)
        {
            var vs = Properties.VehicleSettings.Default;
            int now = vs.setAS_KpFast > 0 ? vs.setAS_KpFast : vs.setAS_Kp;
            TuneVerdict v = motorTuneFast.Recommend(now, vs.setAS_minSteerPWM, out kpFast, out _);
            if (v == TuneVerdict.DeadBand) { kpFast = now; return TuneVerdict.Good; }   // the minimum PWM is the slow card's
            if (v == TuneVerdict.Good && vs.setAS_KpFast <= 0) kpFast = now;
            return v;
        }

        public void SetLinearity(double middle, double big)
        {
            var vs = Properties.VehicleSettings.Default;
            vs.setAS_WasLinMiddle = CWasLinearity.Clamp(middle);
            vs.setAS_WasLinBig = CWasLinearity.Clamp(big);
            if (!isEasyDriveMode) vs.Save();
        }

        public void SetFusionWeight(double weight)
        {
            var vs = Properties.VehicleSettings.Default;
            vs.setIMU_fusionWeight2 = ahrs.fusionWeight = Math.Max(0.002, Math.Min(0.4, weight));
            if (!isEasyDriveMode) vs.Save();
        }

        public void SetKpFast(int kpFast)
        {
            var vs = Properties.VehicleSettings.Default;
            vs.setAS_KpFast = (byte)Math.Max(0, Math.Min(255, kpFast));
            if (!isEasyDriveMode) vs.Save();
            lastSentKp = -2;   // send the right Kp at once
            lastKpSendS = -1e9;
        }

        // ---- speed dependent Kp ----

        /// <summary>Something sent the plain Kp (the steer settings): the speed dependent one goes again soon.</summary>
        public void SendSpeedKpAgain()
        {
            if (Properties.VehicleSettings.Default.setAS_KpFast <= 0) return;
            lastSentKp = -2;
            lastKpSendS = -1e9;
        }

        private void SendSpeedKp()
        {
            var vs = Properties.VehicleSettings.Default;
            if (vs.setAS_KpFast <= 0)
            {
                // off: if a speed Kp was sent before, the plain one goes back once
                if (lastSentKp >= 0 && lastSentKp != vs.setAS_Kp)
                {
                    p_252.pgn[p_252.gainProportional] = vs.setAS_Kp;
                    SendPgnToLoop(p_252.pgn);
                }
                lastSentKp = -1;
                return;
            }
            int kp = Math.Max(1, Math.Min(255, CSpeedGain.Kp(vs.setAS_Kp, vs.setAS_KpFast, avgSpeed)));
            if (kp == lastSentKp || secondsSinceStart - lastKpSendS < 1) return;
            p_252.pgn[p_252.gainProportional] = (byte)kp;
            SendPgnToLoop(p_252.pgn);
            lastSentKp = kp;
            lastKpSendS = secondsSinceStart;
        }
    }
}
