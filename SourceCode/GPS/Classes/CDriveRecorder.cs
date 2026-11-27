using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace AgOpenGPS
{
    /// <summary>One fix of a recording: what the sensors said and what was asked of the steering.</summary>
    public sealed class DriveRecord
    {
        public double T, East, North, AgeS, SpeedKmh, GpsHeadingDeg, ImuHeadingDeg, ImuRollDeg, DualHeadingDeg;
        public double WheelDeg, AimDeg, SentDeg, OffLineCm, WheelbaseM;
        public double ImuYawRateDegS = double.NaN;   // from the module's IMU summary (newer firmwares; older recordings: none)
        public int FixQuality;
        public bool Reverse, Steering;
        public string Method = "";

        public const string Header = "t;east;north;fix;age;kmh;reverse;gpsHeading;imuHeading;imuRoll;dualHeading;wheel;aim;sent;offLineCm;steering;method;wheelbase;imuYawRate";

        public string ToLine()
        {
            var c = CultureInfo.InvariantCulture;
            string F(double v, string f) => double.IsNaN(v) ? "" : v.ToString(f, c);
            return string.Join(";", F(T, "0.000"), F(East, "0.000"), F(North, "0.000"), FixQuality.ToString(c), F(AgeS, "0.0"), F(SpeedKmh, "0.00"),
                Reverse ? "1" : "0", F(GpsHeadingDeg, "0.000"), F(ImuHeadingDeg, "0.00"), F(ImuRollDeg, "0.00"), F(DualHeadingDeg, "0.000"),
                F(WheelDeg, "0.00"), F(AimDeg, "0.00"), F(SentDeg, "0.00"), F(OffLineCm, "0.0"), Steering ? "1" : "0", Method, F(WheelbaseM, "0.00"), F(ImuYawRateDegS, "0.00"));
        }

        public static DriveRecord Parse(string line)
        {
            string[] p = line.Split(';');
            if (p.Length < 18) return null;
            var c = CultureInfo.InvariantCulture;
            double D(string s) => s.Length == 0 ? double.NaN : double.Parse(s, c);
            return new DriveRecord
            {
                T = D(p[0]),
                East = D(p[1]),
                North = D(p[2]),
                FixQuality = int.Parse(p[3], c),
                AgeS = D(p[4]),
                SpeedKmh = D(p[5]),
                Reverse = p[6] == "1",
                GpsHeadingDeg = D(p[7]),
                ImuHeadingDeg = D(p[8]),
                ImuRollDeg = D(p[9]),
                DualHeadingDeg = D(p[10]),
                WheelDeg = D(p[11]),
                AimDeg = D(p[12]),
                SentDeg = D(p[13]),
                OffLineCm = D(p[14]),
                Steering = p[15] == "1",
                Method = p[16],
                WheelbaseM = D(p[17]),
                ImuYawRateDegS = p.Length > 18 ? D(p[18]) : double.NaN
            };
        }
    }

    /// <summary>
    /// The drive recorder: every fix while moving, into a file per session (Documents\AgOpenGPS\Recordings). Played
    /// back later (CDriveReplay) the steering gear and the state estimator can be worked out from the real tractor
    /// instead of a simulated one. Old files go when the folder holds more than the limit.
    /// </summary>
    public sealed class CDriveRecorder
    {
        public const long MaxFolderBytes = 200L * 1024 * 1024;

        private StreamWriter writer;
        private double lastFlushT = double.NaN;

        public string FilePath { get; private set; }
        public int Lines { get; private set; }
        public bool Recording => writer != null;

        public void Start(string folder)
        {
            Stop();
            Directory.CreateDirectory(folder);
            TrimFolder(folder);
            FilePath = Path.Combine(folder, DateTime.Now.ToString("yyyy-MM-dd_HHmmss", CultureInfo.InvariantCulture) + ".csv");
            writer = new StreamWriter(FilePath, false);
            writer.WriteLine(DriveRecord.Header);
            Lines = 0;
            lastFlushT = double.NaN;
        }

        public void Write(DriveRecord r)
        {
            if (writer == null) return;
            writer.WriteLine(r.ToLine());
            Lines++;
            // to the disc every few seconds (a switched off tablet loses only those)
            if (double.IsNaN(lastFlushT) || r.T - lastFlushT > 5)
            {
                writer.Flush();
                lastFlushT = r.T;
            }
        }

        public void Stop()
        {
            if (writer == null) return;
            try { writer.Flush(); writer.Dispose(); }
            catch (IOException) { }
            writer = null;
        }

        // the oldest recordings go while the folder is over its limit
        private static void TrimFolder(string folder)
        {
            var files = new DirectoryInfo(folder).GetFiles("*.csv").OrderBy(f => f.LastWriteTimeUtc).ToList();
            long total = files.Sum(f => f.Length);
            foreach (FileInfo f in files)
            {
                if (total <= MaxFolderBytes) break;
                total -= f.Length;
                try { f.Delete(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        public static List<DriveRecord> Read(string path)
        {
            var list = new List<DriveRecord>();
            foreach (string line in File.ReadLines(path).Skip(1))
            {
                DriveRecord r = DriveRecord.Parse(line);
                if (r != null) list.Add(r);
            }
            return list;
        }
    }

    /// <summary>
    /// Plays a recording back through the steering gear measurement and the state estimator - what they find on the
    /// real tractor's data (and so any change to them can be tried on it at the desk).
    /// </summary>
    public static class CDriveReplay
    {
        public sealed class Findings
        {
            public CSteerDynamics Gear;
            public CStateEstimator Estimator;
            public int Fixes;
        }

        public static Findings Run(IEnumerable<DriveRecord> records, double wheelbaseM = double.NaN)
        {
            var gear = new CSteerDynamics();
            var est = new CStateEstimator();
            int n = 0;
            foreach (DriveRecord r in records)
            {
                n++;
                double L = double.IsNaN(wheelbaseM) ? r.WheelbaseM : wheelbaseM;
                gear.AddSample(r.T, r.SentDeg, r.WheelDeg, r.Steering && r.SpeedKmh > 1 && !r.Reverse);
                double sigma = r.FixQuality == 4 ? 0.02 : r.FixQuality == 5 ? 0.25 : r.FixQuality == 2 ? 0.6 : r.FixQuality == 1 ? 1.5 : double.NaN;
                est.Step(new CStateEstimator.Inputs
                {
                    T = r.T,
                    East = r.East,
                    North = r.North,
                    PositionSigmaM = sigma,
                    SpeedMs = r.SpeedKmh / 3.6 * (r.Reverse ? -1 : 1),
                    CourseRad = double.IsNaN(r.DualHeadingDeg) && !r.Reverse ? r.GpsHeadingDeg * Math.PI / 180 : double.NaN,
                    ImuHeadingRad = r.ImuHeadingDeg * Math.PI / 180,
                    DualHeadingRad = r.DualHeadingDeg * Math.PI / 180,
                    SteerRad = r.WheelDeg * Math.PI / 180,
                    WheelbaseM = L,
                    HasYawRate = !double.IsNaN(r.ImuYawRateDegS),
                    YawRateRadS = r.ImuYawRateDegS * Math.PI / 180
                });
            }
            gear.Analyze();
            return new Findings { Gear = gear, Estimator = est, Fixes = n };
        }
    }
}
