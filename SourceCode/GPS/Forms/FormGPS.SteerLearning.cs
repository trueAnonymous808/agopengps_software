using AgLibrary.Logging;
using System;
using System.IO;

namespace AgOpenGPS
{
    // The drive recorder, learning on the field and the trailed implement kept on the line. Learning and the
    // implement move the position the guidance sees sideways (every steering method then drives the tractor so much
    // the other way, and none fights it with its own integral); the recorder takes the raw data before that.
    public partial class FormGPS
    {
        // ---- the drive recorder ----

        public readonly CDriveRecorder recorder = new CDriveRecorder();

        public bool RecorderOn => Properties.Settings.Default.setRecorder_On;

        public void SetRecorder(bool on)
        {
            Properties.Settings.Default.setRecorder_On = on;
            Properties.Settings.Default.Save();
            if (!on) recorder.Stop();
            Log.EventWriter("Drive recorder " + (on ? "on" : "off"));
        }

        private double recorderRetryS = -1e9;

        /// <summary>The last write error (empty: none since recording started again).</summary>
        public string RecorderError { get; private set; } = "";

        public static string RecordingsFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "AgOpenGPS", "Recordings");

        // every fix, before the guidance: the raw sensors and the angles asked (while moving)
        private void RecordFix()
        {
            // (the simulator is not recorded: a recording is for learning from the real tractor)
            if (!RecorderOn || avgSpeed < 0.5 || secondsSinceStart < recorderRetryS || timerSim.Enabled)
                return;
            try
            {
                if (!recorder.Recording)
                {
                    recorder.Start(RecordingsFolder);
                    RecorderError = "";
                }
                bool dual = headingFromSource == "Dual";
                recorder.Write(new DriveRecord
                {
                    T = secondsSinceStart,
                    East = pivotAxlePos.easting,
                    North = pivotAxlePos.northing,
                    FixQuality = timerSim.Enabled ? 4 : pn.fixQuality,
                    AgeS = pn.age,
                    SpeedKmh = avgSpeed,
                    Reverse = isReverse,
                    GpsHeadingDeg = glm.toDegrees(gpsHeading),
                    ImuHeadingDeg = ahrs.imuHeading != 99999 ? ahrs.imuHeading : double.NaN,
                    ImuRollDeg = ahrs.imuRoll != 88888 ? ahrs.imuRoll : double.NaN,
                    DualHeadingDeg = dual ? pn.headingTrueDual : double.NaN,
                    WheelDeg = mc.actualSteerAngleDegrees,
                    AimDeg = SteerAimDeg(),
                    SentDeg = SteerSentLastDeg(),
                    // (the guidance measured the moved position of the last fix: without that shift, the real distance)
                    OffLineCm = guidanceLineDistanceOff != 32000 && guidanceLineDistanceOff != 32020 ? guidanceLineDistanceOff * 0.1 - guidanceShiftM * 100 : double.NaN,
                    Steering = isBtnAutoSteerOn,
                    Method = PassMethodName,
                    WheelbaseM = ModelWheelbaseM,
                    ImuYawRateDegS = imuSummary.Fresh(secondsSinceStart) ? imuSummary.YawRateDegS : double.NaN
                });
            }
            catch (Exception ex)
            {
                // a full disc or a locked file: this file ends, steering goes on; a new file is tried a minute later
                // (the switch stays as the user set it)
                recorder.Stop();
                recorderRetryS = secondsSinceStart + 60;
                RecorderError = ex.Message;
                Log.EventWriter("Drive recorder stopped: " + ex.Message);
            }
        }

        // ---- learning on the field, the implement kept on the line ----

        public readonly CFieldLearning fieldLearning = new CFieldLearning();
        public readonly CImplementKeeper implementKeeper = new CImplementKeeper();
        private string learningField = "";
        private double learningSavedS = -1e9, guidanceShiftM, rawPivotE, rawPivotN, rawHeading, shiftE, shiftN;
        private int offLineSign = 1;

        public bool FieldLearningOn => Properties.Settings.Default.setFieldLearn_On;
        public bool ImplementKeeperOn => Properties.Settings.Default.setImplementKeep_On;

        public void SetFieldLearning(bool on)
        {
            Properties.Settings.Default.setFieldLearn_On = on;
            Properties.Settings.Default.Save();
            Log.EventWriter("Learning on the field " + (on ? "on" : "off"));
        }

        public void SetImplementKeeper(bool on)
        {
            Properties.Settings.Default.setImplementKeep_On = on;
            Properties.Settings.Default.Save();
            if (!on) implementKeeper.Reset();
            Log.EventWriter("Trailed implement kept on the line " + (on ? "on" : "off"));
        }

        /// <summary>How far the position the guidance sees is moved now (m, + right): what was learnt plus the implement's.</summary>
        public double GuidanceShiftM => guidanceShiftM;

        public void ForgetFieldLearning()
        {
            fieldLearning.Clear();
            SaveFieldLearning();
            Log.EventWriter("Learning on the field forgotten for this field");
        }

        private string FieldLearningFile => isJobStarted && !string.IsNullOrEmpty(currentFieldDirectory) && !isEasyDriveMode
            ? Path.Combine(RegistrySettings.fieldsDirectory, currentFieldDirectory, "SteerLearning.csv") : null;

        private void SaveFieldLearning()
        {
            // the field the learning belongs to (it may have been closed since the last fix)
            string f = learningField.Length > 0 ? learningField : FieldLearningFile;
            if (f == null || !fieldLearning.Dirty) return;
            try { fieldLearning.Save(f); }
            catch (Exception ex) { Log.EventWriter("Learning on the field not saved: " + ex.Message); }
        }

        // the field's learning comes and goes with the field
        private void FollowLearningField()
        {
            string field = FieldLearningFile ?? "";
            if (field == learningField) return;
            if (learningField.Length > 0 && fieldLearning.Dirty)
            {
                try { fieldLearning.Save(learningField); } catch (Exception) { }
            }
            learningField = field;
            implementKeeper.Reset();
            try { fieldLearning.Load(field); }
            catch (Exception ex) { fieldLearning.Clear(); Log.EventWriter("Learning on the field not read: " + ex.Message); }
        }

        // holding a line now (forwards, autosteer, no U-turn or contour)
        private bool HoldingLine => isBtnAutoSteerOn && trk.idx > -1 && !ct.isContourBtnOn && !yt.isYouTurnTriggered && !isReverse && avgSpeed > 1;

        private bool LineSameWay => trk.idx > -1 && trk.gArr[trk.idx].mode == TrackMode.AB ? ABLine.isHeadingSameWay : curve.isHeadingSameWay;

        // every fix, before the guidance: the guidance sees the tractor moved sideways by what was learnt and the implement's shift
        private void ApplyGuidanceShift()
        {
            rawPivotE = pivotAxlePos.easting;
            rawPivotN = pivotAxlePos.northing;
            rawHeading = fixHeading;
            guidanceShiftM = 0;
            try
            {
                FollowLearningField();
                if (secondsSinceStart - learningSavedS > 120) { learningSavedS = secondsSinceStart; SaveFieldLearning(); }
                if (!HoldingLine || (!FieldLearningOn && !ImplementKeeperOn)) return;

                double shift = 0;
                if (FieldLearningOn)
                {
                    // the place a little ahead: the shift is in place when the tractor gets there
                    double ahead = Math.Max(1, avgSpeed / 3.6 * (SteerDeadTimeS + 0.5));
                    shift += fieldLearning.ShiftAt(rawPivotE + Math.Sin(rawHeading) * ahead, rawPivotN + Math.Cos(rawHeading) * ahead, rawHeading);
                }
                if (ImplementKeeperOn && tool.isToolTrailing) shift += implementKeeper.Shift(LineSameWay);
                shift = Math.Max(-1.2, Math.Min(1.2, shift));
                if (Math.Abs(shift) < 1e-4) return;

                // moved right: the guidance steers it back left, so the tractor really runs that much left
                double rightE = Math.Cos(fixHeading), rightN = -Math.Sin(fixHeading);
                shiftE = rightE * shift;
                shiftN = rightN * shift;
                pivotAxlePos.easting += shiftE;
                pivotAxlePos.northing += shiftN;
                steerAxlePos.easting += shiftE;
                steerAxlePos.northing += shiftN;
                guidanceShiftM = shift;
            }
            catch (Exception ex)
            {
                Log.EventWriter("Guidance shift error: " + ex.Message);
            }
        }

        // after the guidance: the position back where it really is (the drawing, the boundary, the U-turn use it).
        // guidanceShiftM stays: the next fix's recording and LearnFromFix need what the guidance saw.
        private void RestoreGuidanceShift()
        {
            if (guidanceShiftM == 0) return;
            pivotAxlePos.easting -= shiftE;
            pivotAxlePos.northing -= shiftN;
            steerAxlePos.easting -= shiftE;
            steerAxlePos.northing -= shiftN;
        }

        // every fix, after the guidance: what learning and the implement take from it
        private void LearnFromFix(double offLineM, bool rtkOnLine)
        {
            if (!HoldingLine)
            {
                implementKeeper.Pause();
                return;
            }
            // the guidance measured the moved position: the real distance off the line is without the shift
            double realOff = offLineM - guidanceShiftM;
            if (FieldLearningOn && rtkOnLine) fieldLearning.Learn(rawPivotE, rawPivotN, rawHeading, realOff);

            if (ImplementKeeperOn && tool.isToolTrailing && rtkOnLine)
            {
                // the guidance's sign for "right of the line", checked on the pivot (to measure the implement the same way)
                double pivotOff = SignedOffTrack(rawPivotE, rawPivotN);
                if (!double.IsNaN(pivotOff) && Math.Abs(realOff) > 0.03 && Math.Abs(pivotOff) > 0.03)
                    offLineSign = Math.Sign(pivotOff) == Math.Sign(realOff) ? 1 : -1;
                double toolOff = SignedOffTrack(toolPos.easting, toolPos.northing) * offLineSign;
                implementKeeper.Update(secondsSinceStart, LineSameWay, toolOff);
            }
        }

        // a point's distance off the current line (m), the AB line's or the curve's nearest stretch
        private double SignedOffTrack(double east, double north)
        {
            if (trk.idx < 0) return double.NaN;
            vec3 a, b;
            if (trk.gArr[trk.idx].mode == TrackMode.AB)
            {
                a = ABLine.currentLinePtA;
                b = ABLine.currentLinePtB;
            }
            else
            {
                var list = curve.curList;
                if (list == null || list.Count < 2) return double.NaN;
                int best = -1;
                double bestD = double.MaxValue;
                int from = Math.Max(0, curve.currentLocationIndex - 200), to = Math.Min(list.Count - 2, curve.currentLocationIndex + 200);
                for (int i = from; i <= to; i++)
                {
                    double dx = list[i + 1].easting - list[i].easting, dn = list[i + 1].northing - list[i].northing;
                    double len2 = dx * dx + dn * dn;
                    if (len2 < 1e-9) continue;
                    double u = Math.Max(0, Math.Min(1, ((east - list[i].easting) * dx + (north - list[i].northing) * dn) / len2));
                    double pe = list[i].easting + u * dx - east, pn = list[i].northing + u * dn - north;
                    double d = pe * pe + pn * pn;
                    if (d < bestD) { bestD = d; best = i; }
                }
                if (best < 0) return double.NaN;
                a = list[best];
                b = list[best + 1];
            }
            double ddx = b.easting - a.easting, ddy = b.northing - a.northing;
            double l = Math.Sqrt(ddx * ddx + ddy * ddy);
            if (l < 1e-6) return double.NaN;
            double off = (ddy * east - ddx * north + b.easting * a.northing - b.northing * a.easting) / l;
            return LineSameWay ? off : -off;
        }
    }
}
