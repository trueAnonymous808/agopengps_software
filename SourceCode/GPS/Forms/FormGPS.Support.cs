using AgLibrary.Logging;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace AgOpenGPS
{
    /// <summary>One line of the setup status: what it is, how it stands and the figure behind it.</summary>
    public sealed class SetupItem
    {
        public const int Learning = 0, Ok = 1, Attention = 2, Problem = 3;
        public int State;
        public string Name, Detail;
    }

    // The setup status (what is learnt, what is missing), the support bundle (everything needed to help from afar, in
    // one zip) and the accuracy report (how accurately the passes were really driven).
    public partial class FormGPS
    {
        private static string TrS(string english) => AgLibrary.Localization.UiTranslator.TrText(english);

        public static string SupportFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "AgOpenGPS", "Support");
        public static string ReportsFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "AgOpenGPS", "Reports");

        /// <summary>Everything a kit needs, each green (ready), yellow (look at it), red (wrong) or grey (still learning).</summary>
        public List<SetupItem> SetupStatus()
        {
            var list = new List<SetupItem>();
            void Add(int state, string name, string detail) => list.Add(new SetupItem { State = state, Name = TrS(name), Detail = detail });
            bool sim = timerSim.Enabled;
            bool module = steerModuleConnectedCounter <= 30;

            if (sim) Add(SetupItem.Ok, "Steer module", TrS("simulator"));
            else Add(module ? SetupItem.Ok : SetupItem.Problem, "Steer module", TrS(module ? "answers" : "not answering"));

            if (sim) Add(SetupItem.Learning, "Module firmware", TrS("simulator"));
            else if (imuSummary.Seen && imuSummary.ModuleUptimeMs >= 0 && imuSummary.HasAccel) Add(SetupItem.Ok, "Module firmware", TrS("new"));
            else Add(module ? SetupItem.Attention : SetupItem.Learning, "Module firmware", TrS("older, or no IMU on the module"));

            if (sim) Add(SetupItem.Ok, "RTK", TrS("simulator"));
            else if (pn.fixQuality == 4 && pn.age <= 5) Add(SetupItem.Ok, "RTK", TrS("fix") + ", " + pn.age.ToString("0.0") + " s");
            else if (pn.fixQuality == 4) Add(SetupItem.Attention, "RTK", TrS("corrections old") + ": " + pn.age.ToString("0") + " s");
            else if (pn.fixQuality == 5) Add(SetupItem.Attention, "RTK", TrS("float"));
            else Add(SetupItem.Problem, "RTK", TrS("no RTK"));

            bool imu = ahrs.imuHeading != 99999;
            if (headingFromSource == "Dual") Add(SetupItem.Ok, "IMU", TrS("dual antenna"));
            else if (!imu) Add(SetupItem.Learning, "IMU", TrS("none (optional)"));
            else if (estimator.Started) Add(SetupItem.Ok, "IMU", TrS("noise") + " " + estimator.ImuNoiseDeg.ToString("0.00") + "°");
            else Add(SetupItem.Learning, "IMU", TrS("learning"));

            if (estimator.Ready)
            {
                double off = estimator.SteerOffsetDeg;
                Add(Math.Abs(off) < 0.3 ? SetupItem.Ok : SetupItem.Attention, "Steer angle sensor zero", TrS("off by") + " " + off.ToString("+0.00;-0.00") + "°");
            }
            else Add(SetupItem.Learning, "Steer angle sensor zero", TrS("learning"));

            if (steerDynamics.Ready) Add(SetupItem.Ok, "Steering gear", TrS("dead time") + " " + steerDynamics.DelayS.ToString("0.00") + " s");
            else Add(SetupItem.Learning, "Steering gear", TrS("learning") + " " + Math.Min(steerDynamics.Moving, CSteerDynamics.MinMoving) + " / " + CSteerDynamics.MinMoving);

            if (!imu && headingFromSource != "Dual") Add(SetupItem.Learning, "Roll compensation", TrS("needs an IMU"));
            else
            {
                TuneVerdict v = rollTune.Recommend(RollShiftS, RollGain, vehicle.VehicleConfig.AntennaHeight, out _, out _);
                Add(v == TuneVerdict.Good ? SetupItem.Ok : v == TuneVerdict.Collecting ? SetupItem.Learning : SetupItem.Attention, "Roll compensation",
                    TrS(v == TuneVerdict.Good ? "ready" : v == TuneVerdict.Collecting ? "learning" : "change suggested"));
                TuneVerdict z = rollZeroTune.Recommend(ahrs.rollZero, out _);
                Add(z == TuneVerdict.Good ? SetupItem.Ok : z == TuneVerdict.Collecting ? SetupItem.Learning : SetupItem.Attention, "Roll zero",
                    TrS(z == TuneVerdict.Good ? "ready" : z == TuneVerdict.Collecting ? "learning" : "change suggested"));
            }

            if (headingFromSource == "Dual") Add(SetupItem.Ok, "Direction at start", TrS("dual antenna"));
            else if (!RememberForward) Add(SetupItem.Attention, "Direction at start", TrS("by hand"));
            else if (startDirection.Learnt) Add(SetupItem.Ok, "Direction at start", TrS("learnt"));
            else Add(SetupItem.Learning, "Direction at start", TrS("learning") + " " + (startDirection.LearntShare * 100).ToString("0") + " %");

            if (SteerAutoOn) Add(SetupItem.Ok, "Fully automatic tuning", SteerAutoStatus ?? "");
            else Add(SetupItem.Attention, "Fully automatic tuning", TrS("off"));
            return list;
        }

        // ---- the support bundle ----

        /// <summary>
        /// One zip with what is needed to help from afar: the logs, the vehicle profile and the settings, the last drive
        /// recordings and a summary of what the program has found. Returns its path.
        /// </summary>
        public Task<string> MakeSupportBundleAsync()
        {
            // what the program knows now, here (it reads the running state); the packing, which can take seconds with
            // the recordings, in the background - the position and the steering must not wait for it
            string summary = SupportSummary();
            var files = new List<(string File, string Folder)>();
            // the logs of the last week (AgOpenGPS and AgIO write theirs in the same folder)
            if (!string.IsNullOrEmpty(RegistrySettings.logsDirectory) && Directory.Exists(RegistrySettings.logsDirectory))
                foreach (FileInfo f in new DirectoryInfo(RegistrySettings.logsDirectory).GetFiles("*.txt").Where(f => f.LastWriteTime > DateTime.Now.AddDays(-7)))
                    files.Add((f.FullName, "logs"));
            // the vehicle profile and the environment
            files.Add((Path.Combine(RegistrySettings.vehiclesDirectory ?? "", (RegistrySettings.vehicleProfileName ?? "") + ".xml"), "settings"));
            files.Add((Path.Combine(RegistrySettings.environmentDirectory ?? "", "environment.xml"), "settings"));
            // the two newest drive recordings (the steering gear and the estimator can be replayed from them)
            if (Directory.Exists(RecordingsFolder))
            {
                recorder.Stop();   // its file closed (a new one starts on the next fix)
                foreach (FileInfo f in new DirectoryInfo(RecordingsFolder).GetFiles("*.csv").OrderByDescending(f => f.LastWriteTimeUtc).Take(2))
                    if (f.Length < 60L * 1024 * 1024) files.Add((f.FullName, "recordings"));
            }
            Directory.CreateDirectory(SupportFolder);
            string path = Path.Combine(SupportFolder, "AgOpenGPS-support_" + DateTime.Now.ToString("yyyy-MM-dd_HHmmss", CultureInfo.InvariantCulture) + ".zip");
            return Task.Run(() =>
            {
                using (ZipArchive zip = ZipFile.Open(path, ZipArchiveMode.Create))
                {
                    zip.CreateEntry("status.txt").Open().Using(s => { var b = Encoding.UTF8.GetBytes(summary); s.Write(b, 0, b.Length); });
                    foreach (var f in files) AddFile(zip, f.File, f.Folder);
                }
                Log.EventWriter("Support bundle written: " + path);
                return path;
            });
        }

        private static void AddFile(ZipArchive zip, string file, string folder)
        {
            try
            {
                if (string.IsNullOrEmpty(file) || !File.Exists(file)) return;
                // shared reading: a log or a recording may still be open
                using (var src = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (Stream dst = zip.CreateEntry(folder + "/" + Path.GetFileName(file), CompressionLevel.Optimal).Open())
                    src.CopyTo(dst);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private string SupportSummary()
        {
            var c = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.AppendLine("AgOpenGPS support bundle, " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", c));
            sb.AppendLine("Program " + System.Windows.Forms.Application.ProductVersion + ", " + Environment.OSVersion + ", " + (Environment.Is64BitProcess ? "64" : "32") + " bit");
            sb.AppendLine("Vehicle profile: " + RegistrySettings.vehicleProfileName + ", wheelbase " + vehicle.VehicleConfig.Wheelbase.ToString("0.00", c) +
                " m, antenna height " + vehicle.VehicleConfig.AntennaHeight.ToString("0.00", c) + " m, working width " + tool.SwathWidth.ToString("0.00", c) + " m");
            sb.AppendLine("Steering: " + SteerModeName + (AdaptivePpOn ? " (adaptive)" : "") + ", fully automatic " + (SteerAutoOn ? "on" : "off") +
                ", simulator " + (timerSim.Enabled ? "on" : "off") + ", frame time " + frameTime.ToString("0.0", c) + " ms");
            sb.AppendLine();
            sb.AppendLine("Setup status:");
            foreach (SetupItem s in SetupStatus())
                sb.AppendLine("  [" + (s.State == SetupItem.Ok ? "ok" : s.State == SetupItem.Attention ? "!" : s.State == SetupItem.Problem ? "X" : "..") + "] " + s.Name + ": " + s.Detail);
            sb.AppendLine();
            sb.AppendLine("State estimator: " + (estimator.Started ? "steer offset " + estimator.SteerOffsetDeg.ToString("0.00", c) + " deg, IMU offset " +
                estimator.ImuOffsetDeg.ToString("0.0", c) + " deg, IMU noise " + estimator.ImuNoiseDeg.ToString("0.00", c) + " deg, slip " +
                (estimator.SlipMs * 100).ToString("0.0", c) + " cm/s, off the GNSS " + estimator.PositionInnovationCm.ToString("0.0", c) + " cm" +
                (estimator.Ready ? "" : " (learning)") : "not started"));
            if (steerDynamics.Ready)
                sb.AppendLine("Steering gear: dead time " + steerDynamics.DelayS.ToString("0.00", c) + " s, right " + steerDynamics.MaxRateRightDegS.ToString("0", c) +
                    " deg/s, left " + steerDynamics.MaxRateLeftDegS.ToString("0", c) + " deg/s");
            if (imuSummary.Seen)
                sb.AppendLine("IMU summary: " + imuSummary.RateHz.ToString("0", c) + " Hz, type " + imuSummary.ImuType + ", module up " +
                    (imuSummary.ModuleUptimeMs / 60000).ToString("0", c) + " min, acceleration " + (imuSummary.HasAccel ? "yes" : "no"));
            sb.AppendLine("Start direction: weight " + startDirection.Weight.ToString("0.0", c) + ", gain " + startDirection.Gain.ToString("0.00", c));
            sb.AppendLine();
            sb.AppendLine("Passes of this session (newest first):");
            foreach (PassResult p in PassLog.Take(40))
                sb.AppendLine("  " + p.When.ToString("HH:mm", c) + "  " + p.Method + "  " + p.RmsCm.ToString("0.0", c) + " cm RMS, " + p.MaxCm.ToString("0", c) + " cm max, " + p.LengthM.ToString("0", c) + " m");
            sb.AppendLine();
            sb.AppendLine("Automatic changes:");
            foreach (string line in SteerAutoLog.Take(40)) sb.AppendLine("  " + line);
            return sb.ToString();
        }

        // ---- the accuracy report ----

        /// <summary>The accuracy of this session's passes as a page (HTML) in Documents\AgOpenGPS\Reports. Returns its path.</summary>
        public async Task<string> WriteAccuracyReportAsync()
        {
            var c = CultureInfo.CurrentCulture;
            List<PassResult> passes = PassLog.AsEnumerable().Reverse().ToList();   // oldest first (finished passes: not changed any more)
            double swath = tool.SwathWidth;
            // comparing every pass with its neighbours can take a while: in the background, the steering goes on
            CAccuracyReport.Result r = await Task.Run(() => CAccuracyReport.Analyze(passes, swath));
            string Cm(double v) => double.IsNaN(v) ? "-" : v.ToString("0.0", c) + " cm";
            string H(string s) => WebUtility.HtmlEncode(s);

            var sb = new StringBuilder();
            sb.Append("<!doctype html><html><head><meta charset=\"utf-8\"><title>").Append(H(TrS("Accuracy report"))).Append("</title><style>")
              .Append("body{font-family:Segoe UI,Arial,sans-serif;margin:24px;color:#222;background:#fff}h1{margin:0 0 4px}h2{margin:28px 0 8px}")
              .Append(".sub{color:#666}.big{display:flex;gap:16px;flex-wrap:wrap}.box{border:1px solid #ccd;border-radius:8px;padding:12px 16px;min-width:200px}")
              .Append(".box b{font-size:28px;display:block}table{border-collapse:collapse;margin-top:6px}td,th{border:1px solid #ccd;padding:4px 10px;text-align:right}")
              .Append("th{background:#f2f4f8}td:first-child,th:first-child{text-align:left}</style></head><body>");
            sb.Append("<h1>").Append(H(TrS("Accuracy report"))).Append("</h1><div class=\"sub\">")
              .Append(H(DateTime.Now.ToString("g", c))).Append(" &middot; ").Append(H(TrS("Vehicle"))).Append(": ").Append(H(RegistrySettings.vehicleProfileName ?? ""))
              .Append(" &middot; ").Append(H(TrS("Working width"))).Append(": ").Append(swath.ToString("0.00", c)).Append(" m &middot; ")
              .Append(H(TrS("Passes"))).Append(": ").Append(r.Passes).Append(", ").Append(r.LengthM.ToString("0", c)).Append(" m</div>");

            sb.Append("<h2>").Append(H(TrS("Pass to pass"))).Append("</h2><div class=\"sub\">")
              .Append(H(TrS("How far each pass really ran from its neighbour, against the working width: an overlap or a gap between two passes."))).Append("</div>");
            if (r.P2PPoints == 0)
                sb.Append("<p>").Append(H(TrS("No neighbouring passes found yet - drive at least two passes side by side with autosteer and RTK fix."))).Append("</p>");
            else
                sb.Append("<div class=\"big\"><div class=\"box\">").Append(H(TrS("95 % within"))).Append("<b>&plusmn; ").Append(Cm(r.P2P95Cm)).Append("</b></div>")
                  .Append("<div class=\"box\">").Append(H(TrS("average"))).Append("<b>").Append(Cm(r.P2PMeanCm)).Append("</b></div>")
                  .Append("<div class=\"box\">").Append(H(TrS("worst"))).Append("<b>").Append(Cm(r.P2PMaxCm)).Append("</b></div></div>");

            sb.Append("<h2>").Append(H(TrS("On the line"))).Append("</h2><div class=\"sub\">")
              .Append(H(TrS("The distance off the line while autosteer held it (RTK fix, at work)."))).Append("</div>")
              .Append("<div class=\"big\"><div class=\"box\">").Append(H(TrS("95 % within"))).Append("<b>&plusmn; ").Append(Cm(r.Line95Cm)).Append("</b></div>")
              .Append("<div class=\"box\">RMS<b>").Append(Cm(r.LineRmsCm)).Append("</b></div>")
              .Append("<div class=\"box\">").Append(H(TrS("worst"))).Append("<b>").Append(Cm(r.LineMaxCm)).Append("</b></div></div>");

            sb.Append("<h2>").Append(H(TrS("Steering method"))).Append("</h2><table><tr><th>").Append(H(TrS("Steering method"))).Append("</th><th>")
              .Append(H(TrS("Passes"))).Append("</th><th>m</th><th>RMS</th><th>").Append(H(TrS("95 % within"))).Append("</th></tr>");
            foreach (var kv in r.ByMethod.OrderBy(k => k.Value.RmsCm))
                sb.Append("<tr><td>").Append(H(TrS(kv.Key))).Append("</td><td>").Append(kv.Value.Passes).Append("</td><td>").Append(kv.Value.LengthM.ToString("0", c))
                  .Append("</td><td>").Append(Cm(kv.Value.RmsCm)).Append("</td><td>&plusmn; ").Append(Cm(kv.Value.Abs95Cm)).Append("</td></tr>");
            sb.Append("</table>");

            if (r.Pairs.Count > 0)
            {
                sb.Append("<h2>").Append(H(TrS("Neighbouring passes"))).Append("</h2><table><tr><th>").Append(H(TrS("Pass"))).Append("</th><th>")
                  .Append(H(TrS("average"))).Append("</th><th>").Append(H(TrS("95 % within"))).Append("</th><th>").Append(H(TrS("worst"))).Append("</th></tr>");
                foreach (CAccuracyReport.Pair p in r.Pairs)
                    sb.Append("<tr><td>").Append(H(p.A.When.ToString("t", c) + " / " + p.B.When.ToString("t", c) + "  " + TrS(p.B.Method))).Append("</td><td>")
                      .Append(Cm(p.MeanCm)).Append("</td><td>&plusmn; ").Append(Cm(p.Abs95Cm)).Append("</td><td>").Append(Cm(p.MaxCm)).Append("</td></tr>");
                sb.Append("</table>");
            }
            sb.Append("<p class=\"sub\">AgOpenGPS ").Append(H(System.Windows.Forms.Application.ProductVersion)).Append("</p></body></html>");

            Directory.CreateDirectory(ReportsFolder);
            string path = Path.Combine(ReportsFolder, "Accuracy_" + DateTime.Now.ToString("yyyy-MM-dd_HHmm", CultureInfo.InvariantCulture) + ".html");
            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
            Log.EventWriter("Accuracy report written: " + path);
            return path;
        }
    }

    internal static class StreamUse
    {
        public static void Using(this Stream s, Action<Stream> a)
        {
            using (s) a(s);
        }
    }
}
