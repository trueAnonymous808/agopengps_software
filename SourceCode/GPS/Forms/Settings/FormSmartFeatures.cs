using AgLibrary.Logging;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace AgOpenGPS
{
    /// <summary>The values the smart features read and write, through the steer settings' own controls.</summary>
    public class SteerTuneValues
    {
        public Func<int> Kp, MinPwm;
        public Action<int, int> SetMotor;
        public Func<double> Hold;
        public double HoldMin, HoldMax;
        public Action<double> SetHold;
        public Func<double> SideHill;
        public double SideHillMin, SideHillMax;
        public Action<double> SetSideHill;
        public Func<double> DeadZone;          // degrees
        public double DeadZoneMin, DeadZoneMax;
        public Func<int> DeadZoneDelay;
        public int DelayMin, DelayMax;
        public Action<double, int> SetDeadZone;
        public Func<double> SpeedFactor;       // the look-ahead's speed factor
        public double SpeedFactorMin, SpeedFactorMax;
        public Action<double> SetSpeedFactor;
        public Func<int> Cpd, Ackermann;
        public Action<int, int> SetGeometry;   // counts per degree, Ackermann
        public Action<int> SetWasOffset;
        public Func<double> AcquireFactor;
        public double AcquireMin, AcquireMax;
        public Action<double> SetAcquireFactor;
        public Func<double> Integral;
        public double IntegralMin, IntegralMax;
        public Action<double> SetIntegral;
        public Func<double> UTurnComp;
        public double UTurnMin, UTurnMax;
        public Action<double> SetUTurnComp;
        public Action ApplySmartWas;           // the steer settings' own Smart WAS button
    }

    // All smart steering helpers in one window (from the steer settings), one page each for things that belong
    // together, picked from the menu on the left: fully automatic and its log; calibration (Smart WAS, geometry);
    // sensors; the steer motor; following the line; GPS and RTK. Everything learns in the background; nothing
    // changes until an Apply is pressed - or by itself, within limits, when fully automatic is on.
    public partial class FormSmartFeatures : Form
    {
        private const int W = 620, Gap = 12, MenuW = 150, MenuH = 54, PageH = 500, BottomBar = 58;
        private const int PageX = Gap + MenuW + Gap, PageW = W - PageX - Gap;
        private const int TunerH = 112, TunerStep = TunerH + 8;

        private readonly FormGPS mf;
        private readonly SteerTuneValues values;
        private readonly Timer refresh = new Timer { Interval = 500 };

        private readonly CheckBox autoSwitch;
        private readonly Button restore;
        private readonly Label autoStatus;
        private readonly ListBox autoLog;
        private int autoLogShown = -1;
        private string autoLogTop;
        private readonly Label heldLines;
        private readonly Label gearFigures, gearWarning, leadState, toolState;
        private readonly Button leadButton;
        private readonly CheckBox toolSwitch;
        private readonly Label rollFigures, rollSuggestion, pitFigures, pitSuggestion;
        private readonly Button rollApply, pitApply;
        private readonly CheckBox pitSwitch;
        private readonly CheckBox smoothSwitch, playSwitch;
        private readonly Label smoothState, playState;
        private double rollNewShift, rollNewGain, pitNewRate;
        private readonly Button newStart;
        private string heldShown;
        private bool settingSwitch;

        private readonly Button wasApply;
        private readonly Label wasStatus, wasSamples, wasConfidence;

        private readonly Label geoRight, geoLeft, geoCpd, geoAckermann, geoConfidence;
        private readonly Button geoApply;
        private int? geoNewCpd, geoNewAckermann;

        private readonly Tuner motor, deadZone, lookAhead, speedFactor, sideHill, rollZero, acquire, integral, uTurn, linearity, fusion, fastKp;
        private int newKp, newMinPwm, newDelay, newKpFast;
        private double newHold, newSideHill, newZone, newFactor, newRollZero, newAcquire, newIntegral, newUTurn, newLinMiddle, newLinBig, newFusion;
        private readonly Label gpsStatus, gpsStats, gpsOffAfter, bridgeStatus, bridgeStats, bridgeMax, examLine;
        private readonly CheckBox gpsSwitch, bridgeSwitch, examSwitch;
        private readonly Label tdcpStatus, tdcpExams;
        private readonly Button tdcpMode;
        private readonly Panel[] pages;
        private readonly Button[] menu;
        private readonly string[] menuNames;
        private Tuner[][] pageTuners;
        private static int lastPage;
        private const int BridgeExtra = 46;   // the bridge card is taller: its exams
        private const int TdcpExtra = BridgeExtra + 34;   // the TDCP card: its receiver hint and longer messages

        private static string Tr(string english) => AgLibrary.Localization.UiTranslator.TrText(english);

        private class Tuner
        {
            public Label Status, Suggestion;
            public Button Apply;
        }

        public FormSmartFeatures(FormGPS mf, SteerTuneValues values)
        {
            this.mf = mf;
            this.values = values;
            Text = Tr("Smart features");
            FormBorderStyle = FormBorderStyle.FixedToolWindow;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            BackColor = Color.FromArgb(236, 239, 243);
            Font = new Font("Segoe UI", 10f);
            ClientSize = new Size(W, Gap + PageH + BottomBar);

            // ---- the menu on the left, a page for each ----
            menuNames = new[] { Tr("Automatic"), Tr("Calibration"), Tr("Sensors"), Tr("Steer motor"), Tr("Line following"), Tr("GPS and RTK"), Tr("Measurements"), Tr("Directions"), Tr("Comparison"), Tr("Learning"), Tr("Status") };
            Image[] icons =
            {
                Properties.Resources.ST_SmartFeatures, SmartIcons.SmartWas(), SmartIcons.Linearity(), SmartIcons.Motor(), SmartIcons.LookAhead(), SmartIcons.Gps(),
                SmartIcons.Geometry(), SmartIcons.Directions(), SmartIcons.Speed(), SmartIcons.Integral(), SmartIcons.Gps()
            };
            // the menu buttons a little closer when there are many, so all fit beside the page
            int menuStep = Math.Min(MenuH + 8, (PageH + 4 - MenuH) / Math.Max(1, menuNames.Length - 1));
            pages = new Panel[menuNames.Length];
            menu = new Button[menuNames.Length];
            for (int i = 0; i < menuNames.Length; i++)
            {
                pages[i] = new Panel { Location = new Point(PageX, Gap), Size = new Size(PageW, PageH), AutoScroll = true, Visible = false };
                menu[i] = new Button
                {
                    Text = menuNames[i],
                    Image = new Bitmap(icons[i], new Size(32, 32)),
                    ImageAlign = ContentAlignment.MiddleLeft,
                    TextAlign = ContentAlignment.MiddleLeft,
                    TextImageRelation = TextImageRelation.ImageBeforeText,
                    Location = new Point(Gap, Gap + i * menuStep),
                    Size = new Size(MenuW, Math.Min(MenuH, menuStep - 3)),
                    FlatStyle = FlatStyle.Flat,
                    UseVisualStyleBackColor = false,
                    Padding = new Padding(4, 0, 2, 0),
                    Font = new Font("Segoe UI Semibold", 10f)
                };
                menu[i].FlatAppearance.BorderSize = 0;
                int page = i;
                menu[i].Click += (s, e) => ShowPage(page);
                Controls.Add(pages[i]);
                Controls.Add(menu[i]);
            }
            int colW = PageW - SystemInformation.VerticalScrollBarWidth - 2;   // room for a scroll bar, if a page needs one
            Panel pAuto = pages[0], pCal = pages[1], pSensors = pages[2], pMotor = pages[3], pLine = pages[4], pGps = pages[5];

            // ---- fully automatic ----
            autoSwitch = new CheckBox
            {
                Appearance = Appearance.Button,
                Text = Tr("Fully automatic"),
                Image = new Bitmap(Properties.Resources.ST_SmartFeatures, new Size(34, 34)),
                ImageAlign = ContentAlignment.MiddleLeft,
                TextAlign = ContentAlignment.MiddleCenter,
                TextImageRelation = TextImageRelation.ImageBeforeText,
                Location = new Point(0, 0),
                Size = new Size(220, 48),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(246, 248, 251),
                UseVisualStyleBackColor = false,
                Font = new Font("Segoe UI Semibold", 12f),
                Checked = mf.SteerAutoOn
            };
            autoSwitch.FlatAppearance.BorderColor = Color.FromArgb(205, 211, 220);
            autoSwitch.FlatAppearance.CheckedBackColor = Color.FromArgb(104, 159, 56);
            autoSwitch.CheckedChanged += (s, e) => ToggleAuto();
            restore = Tile(Tr("Restore starting values"), 228, 0, colW - 228, 48, 10f);
            restore.Click += (s, e) => Restore();
            var note = Lbl(0, 56, colW, 40, 10f, false);
            note.Text = Tr("Learns in the background whenever autosteer works");
            pAuto.Controls.AddRange(new Control[] { autoSwitch, restore, note });

            // what the limits hold back, and taking today's values as the new start (the limits move with them)
            Panel held = Card(pAuto, 0, 100, colW, 112, new Bitmap(Properties.Resources.ST_SmartFeatures, new Size(28, 28)), Tr("Held back by the limits"));
            heldLines = Lbl(10, 36, colW - 162, 70, 9f, false);
            newStart = Tile(Tr("New starting values"), colW - 146, 40, 136, 60, 10f);
            newStart.Click += (s, e) => NewStartingValues();
            held.Controls.AddRange(new Control[] { heldLines, newStart });

            // what fully automatic has changed, when and why
            Panel log = Card(pAuto, 0, 220, colW, PageH - 224, new Bitmap(Properties.Resources.ST_SmartFeatures, new Size(28, 28)), Tr("Automatic changes"));
            autoLog = new ListBox
            {
                Location = new Point(8, 38),
                Size = new Size(colW - 18, PageH - 224 - 46),
                Font = new Font("Segoe UI", 9.5f),
                BorderStyle = BorderStyle.None,
                IntegralHeight = false,
                HorizontalScrollbar = true
            };
            log.Controls.Add(autoLog);

            // the state of fully automatic, under every page
            autoStatus = new Label
            {
                Location = new Point(Gap, Gap + PageH + 6),
                Size = new Size(W - 2 * Gap - 96, BottomBar - 12),
                TextAlign = ContentAlignment.MiddleLeft,
                Font = new Font("Segoe UI Semibold", 10f)
            };
            Controls.Add(autoStatus);

            // ---- calibration: Smart WAS ----
            Panel was = Card(pCal, 0, 0, colW, 150, SmartIcons.SmartWas(), Tr("Smart WAS"));
            wasApply = Tile("", 10, 42, 160, 96, 20f);
            wasApply.Click += (s, e) => ApplyWas();
            wasStatus = Lbl(182, 42, colW - 192, 26, 11.5f, true);
            wasSamples = Lbl(182, 70, colW - 192, 22, 10f, false);
            wasConfidence = Lbl(182, 92, colW - 192, 22, 10f, false);
            var wasHint = Lbl(182, 116, colW - 192, 20, 9f, false);
            wasHint.Text = Tr("Autosteer on, any line");
            wasHint.ForeColor = Color.Gray;
            was.Controls.AddRange(new Control[] { wasApply, wasStatus, wasSamples, wasConfidence, wasHint });

            // ---- calibration: steering geometry ----
            Panel geo = Card(pCal, 0, 158, colW, 330, SmartIcons.Geometry(), Tr("Steering geometry calibration"));
            var wheelbase = Lbl(10, 40, colW - 20, 22, 9.5f, true);
            wheelbase.Text = Tr("Wheelbase") + ": " + mf.vehicle.VehicleConfig.Wheelbase.ToString("0.00") + " m - " + Tr("the calculation uses this, check it first");
            var how = Lbl(10, 62, colW - 20, 46, 9f, false);
            how.Text = Tr("Drive steady circles to the right and to the left, 2-10 km/h, wheels at 5-35°, with RTK fix. Set the WAS zero first (Smart WAS).");
            int boxW = (colW - 28) / 2;
            geoRight = Box(10, 110, boxW);
            geoLeft = Box(10 + boxW + 8, 110, boxW);
            geoCpd = Lbl(10, 192, colW - 20, 24, 11f, true);
            geoAckermann = Lbl(10, 216, colW - 20, 24, 11f, true);
            geoConfidence = Lbl(10, 240, colW - 20, 22, 10f, false);
            geoApply = Tile(Tr("Apply"), 10, 268, 170, 48, 11.5f);
            geoApply.Click += (s, e) => ApplyGeometry();
            Button geoRestart = Tile(Tr("Start again"), 188, 268, 140, 48, 10.5f);
            geoRestart.Click += (s, e) => { mf.steerGeometryCal.Reset(); ShowState(); };
            geo.Controls.AddRange(new Control[] { wheelbase, how, geoRight, geoLeft, geoCpd, geoAckermann, geoConfidence, geoApply, geoRestart });

            // ---- sensors ----
            linearity = AddTuner(pSensors, 0, 0, colW, SmartIcons.Linearity(), Tr("Sensor linearity"), Tr("From the geometry calibration: circles at small, middle and big angles"));
            rollZero = AddTuner(pSensors, 0, TunerStep, colW, SmartIcons.RollZero(), Tr("Roll sensor zero"), Tr("Autosteer on lines driven both ways, IMU needed"));
            fusion = AddTuner(pSensors, 0, 2 * TunerStep, colW, SmartIcons.Fusion(), Tr("IMU fusion"), Tr("Single antenna with IMU, straight on a line above 5 km/h"));
            sideHill = AddTuner(pSensors, 0, 3 * TunerStep, colW, SmartIcons.SideHill(), Tr("Side-hill compensation"), Tr("Autosteer on a straight line across a slope, IMU needed"));

            // ---- steer motor ----
            motor = AddTuner(pMotor, 0, 0, colW, SmartIcons.Motor(), Tr("Steer motor (Kp, minimum PWM)"), Tr("Autosteer on, any line"));
            fastKp = AddTuner(pMotor, 0, TunerStep, colW, SmartIcons.FastGain(), Tr("Kp above 12 km/h"), Tr("Learns above 11 km/h. Up to 8 km/h the normal Kp, from 12 km/h this one, a blend between"));
            deadZone = AddTuner(pMotor, 0, 2 * TunerStep, colW, SmartIcons.DeadZone(), Tr("Dead zone (heading, delay)"), Tr("Autosteer on, any line"));
            integral = AddTuner(pMotor, 0, 3 * TunerStep, colW, SmartIcons.Integral(), Tr("Pure pursuit integral"), Tr("Autosteer on long straight lines, pure pursuit"));

            // ---- following the line ----
            lookAhead = AddTuner(pLine, 0, 0, colW, SmartIcons.LookAhead(), Tr("Look-ahead"), Tr("Autosteer on an AB line or curve, pure pursuit"));
            speedFactor = AddTuner(pLine, 0, TunerStep, colW, SmartIcons.Speed(), Tr("Speed factor of the look-ahead"), Tr("As the look-ahead, slowly (up to 8 km/h) and fast (from 10 km/h)"));
            acquire = AddTuner(pLine, 0, 2 * TunerStep, colW, SmartIcons.Acquire(), Tr("Coming onto the line"), Tr("Autosteer on a line, coming on from more than 40 cm off"));
            uTurn = AddTuner(pLine, 0, 3 * TunerStep, colW, SmartIcons.UTurn(), Tr("U-turn compensation"), Tr("Autosteer with automatic U-turns"));

            pageTuners = new[]
            {
                new Tuner[0], new Tuner[0], new[] { linearity, rollZero, fusion, sideHill },
                new[] { motor, fastKp, deadZone, integral }, new[] { lookAhead, speedFactor, acquire, uTurn }, new Tuner[0], new Tuner[0]
            };

            // ---- GPS and RTK: the quality guard ----
            Panel gps = Card(pGps, 0, 0, colW, TunerH, SmartIcons.Gps(), Tr("GPS quality guard"));
            gpsStatus = Lbl(10, 36, colW - 136, 36, 10f, true);
            gpsStats = Lbl(10, 74, colW - 136, 32, 9f, false);
            gpsSwitch = new CheckBox
            {
                Appearance = Appearance.Button,
                Location = new Point(colW - 118, 34),
                Size = new Size(108, 34),
                FlatStyle = FlatStyle.Flat,
                TextAlign = ContentAlignment.MiddleCenter,
                BackColor = Color.FromArgb(246, 248, 251),
                UseVisualStyleBackColor = false,
                Font = new Font("Segoe UI Semibold", 10.5f),
                Checked = Properties.Settings.Default.setGpsGuard_Enabled
            };
            gpsSwitch.FlatAppearance.CheckedBackColor = Color.FromArgb(104, 159, 56);
            gpsSwitch.CheckedChanged += (s, e) =>
            {
                Properties.Settings.Default.setGpsGuard_Enabled = gpsSwitch.Checked;
                Properties.Settings.Default.Save();
                ShowState();
            };
            Button gpsLess = Tile("-", colW - 118, 72, 32, 32, 12f);
            gpsOffAfter = Lbl(colW - 86, 72, 44, 32, 9.5f, true);
            gpsOffAfter.TextAlign = ContentAlignment.MiddleCenter;
            Button gpsMore = Tile("+", colW - 42, 72, 32, 32, 12f);
            gpsLess.Click += (s, e) => StepGpsOffAfter(-5);
            gpsMore.Click += (s, e) => StepGpsOffAfter(5);
            gps.Controls.AddRange(new Control[] { gpsStatus, gpsStats, gpsSwitch, gpsLess, gpsOffAfter, gpsMore });

            // the RTK bridge under the guard
            Panel bridge = Card(pGps, 0, TunerStep, colW, TunerH + BridgeExtra, SmartIcons.Bridge(), Tr("RTK bridge"));
            bridgeStatus = Lbl(10, 36, colW - 136, 36, 10f, true);
            bridgeStats = Lbl(10, 74, colW - 136, 32, 9f, false);
            bridgeSwitch = new CheckBox
            {
                Appearance = Appearance.Button,
                Location = new Point(colW - 118, 34),
                Size = new Size(108, 34),
                FlatStyle = FlatStyle.Flat,
                TextAlign = ContentAlignment.MiddleCenter,
                BackColor = Color.FromArgb(246, 248, 251),
                UseVisualStyleBackColor = false,
                Font = new Font("Segoe UI Semibold", 10.5f),
                Checked = Properties.Settings.Default.setRtkBridge_Enabled
            };
            bridgeSwitch.FlatAppearance.CheckedBackColor = Color.FromArgb(104, 159, 56);
            bridgeSwitch.CheckedChanged += (s, e) =>
            {
                Properties.Settings.Default.setRtkBridge_Enabled = bridgeSwitch.Checked;
                Properties.Settings.Default.Save();
                ShowState();
            };
            Button bridgeLess = Tile("-", colW - 118, 72, 32, 32, 12f);
            bridgeMax = Lbl(colW - 86, 72, 44, 32, 9.5f, true);
            bridgeMax.TextAlign = ContentAlignment.MiddleCenter;
            Button bridgeMore = Tile("+", colW - 42, 72, 32, 32, 12f);
            bridgeLess.Click += (s, e) => StepBridgeMax(-30);
            bridgeMore.Click += (s, e) => StepBridgeMax(30);
            // the exams: virtual outages in the background, what they and the real outages have shown
            examLine = Lbl(10, 108, colW - 136, 46, 9f, false);
            examSwitch = new CheckBox
            {
                Appearance = Appearance.Button,
                Location = new Point(colW - 118, 112),
                Size = new Size(108, 34),
                FlatStyle = FlatStyle.Flat,
                TextAlign = ContentAlignment.MiddleCenter,
                BackColor = Color.FromArgb(246, 248, 251),
                UseVisualStyleBackColor = false,
                Font = new Font("Segoe UI Semibold", 9.5f),
                Checked = Properties.Settings.Default.setRtkBridge_Exams
            };
            examSwitch.FlatAppearance.CheckedBackColor = Color.FromArgb(104, 159, 56);
            examSwitch.CheckedChanged += (s, e) =>
            {
                Properties.Settings.Default.setRtkBridge_Exams = examSwitch.Checked;
                Properties.Settings.Default.Save();
                ShowState();
            };
            bridge.Controls.AddRange(new Control[] { bridgeStatus, bridgeStats, bridgeSwitch, bridgeLess, bridgeMax, bridgeMore, examLine, examSwitch });

            // TDCP under the bridge: off, learn only, automatic
            Panel tdcpCard = Card(pGps, 0, 2 * TunerStep + BridgeExtra, colW, TunerH + TdcpExtra, SmartIcons.Tdcp(), Tr("TDCP (carrier phase)"));
            // any receiver with raw carrier phase: RTCM 3 (most makes) or u-blox UBX
            var tdcpHint = Lbl(10, 32, colW - 20, 32, 8.5f, false);
            tdcpHint.Text = Tr("Receiver, GPS port, 5-10 Hz: RTCM 3 MSM + ephemerides (1019/1020/1042/1046), or u-blox RXM-RAWX + RXM-SFRBX");
            tdcpHint.ForeColor = Color.Gray;
            tdcpStatus = Lbl(10, 68, colW - 136, 54, 9f, true);
            tdcpExams = Lbl(10, 126, colW - 20, 40, 9f, false);
            tdcpMode = Tile("", colW - 118, 70, 108, 44, 10f);
            tdcpMode.Click += (s, e) =>
            {
                Properties.Settings.Default.setTdcp_Mode = (mf.TdcpMode + 1) % 3;
                Properties.Settings.Default.Save();
                ShowState();
            };
            tdcpCard.Controls.AddRange(new Control[] { tdcpHint, tdcpStatus, tdcpExams, tdcpMode });

            // ---- measurements: the steering gear, values per implement, rough ground, the helpers on the angle sent ----
            // (all on one screen: cards a little tighter, buttons on the right; the steering method is in the steer settings)
            Panel pMeasure = pages[6];
            pMeasure.AutoScroll = false;
            const int MGap = 6, BtnW = 108;
            int textW = colW - BtnW - 28;

            Panel gear = Card(pMeasure, 0, 0, colW, 108, SmartIcons.Motor(), Tr("Steering gear"));
            gearFigures = Lbl(10, 32, colW - 218, 38, 9f, true);
            gearWarning = Lbl(10, 72, colW - 218, 30, 8.5f, false);
            gearWarning.ForeColor = Color.DarkOrange;
            leadButton = Tile("", colW - 200, 32, 190, 36, 9.5f);
            leadButton.Click += (s, e) => ToggleLead();
            leadState = Lbl(colW - 200, 72, 190, 30, 8.5f, false);
            leadState.TextAlign = ContentAlignment.TopCenter;
            gear.Controls.AddRange(new Control[] { gearFigures, gearWarning, leadState, leadButton });

            int y = 108 + MGap;
            Panel tools = Card(pMeasure, 0, y, colW, 70, SmartIcons.FastGain(), Tr("Steering values per implement"));
            toolState = Lbl(10, 36, textW, 28, 9f, false);
            toolSwitch = HelperSwitch(colW - BtnW - 10, 32, mf.PerToolTuning, on => { mf.SetPerToolTuning(on); ShowState(); });
            tools.Controls.AddRange(new Control[] { toolState, toolSwitch });

            // rough ground: the roll compensation's timing and height, and pit mode
            y += 70 + MGap;
            const int RoughH = 94;
            Panel rollCard = Card(pMeasure, 0, y, colW, RoughH, SmartIcons.RollZero(), Tr("Roll compensation on rough ground"));
            rollFigures = Lbl(10, 32, textW, 42, 8.5f, false);
            rollSuggestion = Lbl(10, 72, textW, 20, 9.5f, true);
            rollApply = Tile(Tr("Apply"), colW - BtnW - 10, 36, BtnW, 50, 11f);
            rollApply.Click += (s, e) => { mf.ApplyRollTune(rollNewShift, rollNewGain); ShowState(); };
            rollCard.Controls.AddRange(new Control[] { rollFigures, rollSuggestion, rollApply });

            y += RoughH + MGap;
            Panel pitCard = Card(pMeasure, 0, y, colW, RoughH, SmartIcons.SideHill(), Tr("Pit mode"));
            pitFigures = Lbl(10, 32, textW, 42, 8.5f, false);
            pitSuggestion = Lbl(10, 72, textW, 20, 9.5f, true);
            pitSwitch = HelperSwitch(colW - BtnW - 10, 30, mf.PitGuardOn, on => { mf.SetPitGuard(on); ShowState(); });
            pitSwitch.Height = 30;
            pitApply = Tile(Tr("Apply"), colW - BtnW - 10, 63, BtnW, 28, 10f);
            pitApply.Click += (s, e) => { mf.ApplyPitRate(pitNewRate); ShowState(); };
            pitCard.Controls.AddRange(new Control[] { pitFigures, pitSuggestion, pitSwitch, pitApply });

            // the helpers on the angle sent: a name and one short line each
            y += RoughH + MGap;
            Panel helpers = Card(pMeasure, 0, y, colW, PageH - y - 2, SmartIcons.Motor(), Tr("Steering helpers"));
            Label HelperName(int top, string name)
            {
                Label l = Lbl(10, top, textW, 19, 9.5f, true);
                l.Text = name;
                l.AutoEllipsis = true;
                helpers.Controls.Add(l);
                return l;
            }
            Label HelperLine(int top)
            {
                Label l = Lbl(10, top + 18, textW, 18, 8.5f, false);
                l.AutoEllipsis = true;
                helpers.Controls.Add(l);
                return l;
            }
            HelperName(32, Tr("Calm steering"));
            smoothState = HelperLine(32);
            smoothSwitch = HelperSwitch(colW - BtnW - 10, 32, mf.SteerSmoothOn, on => mf.SetSteerSmooth(on));
            smoothSwitch.Height = 30;
            HelperName(70, Tr("Play compensation"));
            playState = HelperLine(70);
            playSwitch = HelperSwitch(colW - BtnW - 10, 68, mf.PlayCompOn, on => mf.SetPlayComp(on));
            playSwitch.Height = 30;
            helpers.Controls.AddRange(new Control[] { smoothSwitch, playSwitch });

            BuildDirectionsPage(pages[7], colW);
            BuildComparePage(pages[8], colW);
            BuildLearningPage(pages[9], colW);
            BuildStatusPage(pages[10], colW);

            linearity.Apply.Click += (s, e) => ApplyLinearity();
            fusion.Apply.Click += (s, e) => ApplyFusion();
            fastKp.Apply.Click += (s, e) => ApplyFastKp();
            motor.Apply.Click += (s, e) => ApplyMotor();
            deadZone.Apply.Click += (s, e) => ApplyDeadZone();
            lookAhead.Apply.Click += (s, e) => ApplyLookAhead();
            speedFactor.Apply.Click += (s, e) => ApplySpeedFactor();
            sideHill.Apply.Click += (s, e) => ApplySideHill();
            rollZero.Apply.Click += (s, e) => ApplyRollZero();
            acquire.Apply.Click += (s, e) => ApplyAcquire();
            integral.Apply.Click += (s, e) => ApplyIntegral();
            uTurn.Apply.Click += (s, e) => ApplyUTurn();

            // ---- bottom ----
            Button close = new Button
            {
                Location = new Point(W - Gap - 80, Gap + PageH + 6),
                Size = new Size(80, 44),
                FlatStyle = FlatStyle.Flat,
                Image = new Bitmap(Properties.Resources.OK64, new Size(40, 40)),
                DialogResult = DialogResult.OK
            };
            close.FlatAppearance.BorderSize = 0;
            close.Click += (s, e) => Close();   // shown without ShowDialog: the dialog result alone does not close it
            CancelButton = close;
            Controls.Add(close);

            // the geometry collects while this window is open (and always when fully automatic)
            if (!mf.SteerAutoOn) mf.steerGeometryCal.Reset();
            mf.isSteerGeometryCalOn = true;

            // buttons and switches above the labels on their cards: a label reaching into one must not paint over its edge
            foreach (Panel page in pages)
                foreach (Control card in page.Controls)
                    foreach (Control c in card.Controls)
                        if (c is ButtonBase) c.BringToFront();

            ShowPage(Math.Max(0, Math.Min(pages.Length - 1, lastPage)));
            refresh.Tick += (s, e) => ShowState();
            refresh.Start();
            ShowState();
        }

        // ---- pages ----

        private void ShowPage(int index)
        {
            lastPage = index;
            for (int i = 0; i < pages.Length; i++) pages[i].Visible = i == index;
            UpdateMenu();
        }

        // the selected entry stands out (in the day and the night theme); a dot where a suggestion waits for Apply
        private void UpdateMenu()
        {
            if (menu == null || pageTuners == null) return;
            for (int i = 0; i < menu.Length; i++)
            {
                bool waiting = false;
                // a page without tuners (none listed for it) has nothing waiting
                if (i < pageTuners.Length)
                    foreach (Tuner t in pageTuners[i]) waiting |= t.Apply.Enabled;
                if (i == 1) waiting |= geoApply.Enabled;
                string text = menuNames[i] + (waiting ? "  \u25CF" : "");
                if (menu[i].Text != text) menu[i].Text = text;

                Color back = pages[i].Visible ? AgLibrary.Localization.UiTheme.MenuActive : AgLibrary.Localization.UiTheme.MenuInactive;
                if (menu[i].BackColor.ToArgb() != back.ToArgb()) menu[i].BackColor = back;
                double lum = (0.299 * back.R + 0.587 * back.G + 0.114 * back.B) / 255;
                Color fore = lum >= 0.5 ? Color.FromArgb(20, 20, 20) : Color.FromArgb(240, 240, 240);
                if (menu[i].ForeColor.ToArgb() != fore.ToArgb()) menu[i].ForeColor = fore;
            }
        }

        // ---- measurements ----

        private void ShowMeasurements()
        {
            CSteerDynamics d = mf.steerDynamics;
            if (!d.Ready)
            {
                gearFigures.Text = Tr("Measuring while autosteer works") + ": " + Math.Min(d.Moving, CSteerDynamics.MinMoving) + " / " + CSteerDynamics.MinMoving;
                gearWarning.Text = "";
            }
            else
            {
                gearFigures.Text = Tr("Dead time") + ": " + d.DelayS.ToString("0.00") + " s   " +
                    Tr("Turning speed") + ": " + (double.IsNaN(d.MaxRateDegS) ? Tr("not reached") : d.MaxRateDegS.ToString("0") + " °/s") + "\n" +
                    Tr("Play") + ": " + (double.IsNaN(d.BacklashDeg) ? "-" : d.BacklashDeg.ToString("0.0") + "°");
                var warn = new List<string>();
                if (d.MuchPlay) warn.Add(Tr("Much play: check the steering joints and the WAS mounting"));
                if (d.SlowAnswer) warn.Add(Tr("Slow answer: check the hydraulics or the motor"));
                if (d.SlowMotor) warn.Add(Tr("The steering turns slowly at its limit"));
                gearWarning.Text = warn.Count == 0 ? Tr("Nothing to fix") : string.Join("\n", warn);
                gearWarning.ForeColor = warn.Count == 0 ? Color.ForestGreen : Color.DarkOrange;
            }
            double lead = mf.SteerLeadS;
            leadState.Text = Tr("Dead-time compensation") + ": " + (lead > 0 ? lead.ToString("0.00") + " s" : Tr("Off"));
            leadButton.Text = lead > 0 ? Tr("Turn off") : Tr("Turn on with the measured value");
            leadButton.Enabled = lead > 0 || d.Ready;

            // roll compensation
            CRollTune rt = mf.rollTune;
            SteerBaseline cur = mf.CurrentSteerValues();
            string inUse = Tr("In use") + ": " + cur.RollShift.ToString("0.00") + " s, " + Tr("height") + " × " + cur.RollGain.ToString("0.00");
            if (!rt.Ready)
            {
                rollFigures.Text = Tr("Learns from rocking on straight lines") + ": " + Math.Min(rt.Rocking, CRollTune.MinRocking) + " / " + CRollTune.MinRocking +
                    "\n" + inUse;
            }
            else
            {
                rollFigures.Text = Tr("Roll delay") + ": " + rt.ShiftS.ToString("0.00") + " s   " + Tr("Real height") + ": " + rt.HeightM.ToString("0.00") + " m" +
                    "   (" + (rt.Fit * 100).ToString("0") + " %)\n" + inUse;
            }
            TuneVerdict rv = mf.RollTuneRecommend(cur, out rollNewShift, out rollNewGain);
            rollSuggestion.Text = rv == TuneVerdict.Recommend ? "→ " + rollNewShift.ToString("0.00") + " s, × " + rollNewGain.ToString("0.00")
                : rv == TuneVerdict.Good ? Tr("Nothing to change") : "";
            rollSuggestion.ForeColor = rv == TuneVerdict.Recommend ? Color.DarkOrange : Color.ForestGreen;
            rollApply.Enabled = rv == TuneVerdict.Recommend;

            // pit mode
            CPitGuard pg = mf.pitGuard;
            pitSwitch.Text = mf.PitGuardOn ? Tr("On") : Tr("Off");
            pitFigures.Text = Tr("Rocks the compensation did not take out") + ": " + pg.Events + "\n" +
                Tr("Trying") + " " + mf.PitRateDegS.ToString("0.0") + " / " + pg.OtherRate(mf.PitRateDegS).ToString("0.0") + " °/s: " +
                pg.EventsNow + " / " + pg.EventsOther + " (" + CPitGuard.MinEvents + ")";
            pitSuggestion.Text = "";
            pitApply.Enabled = false;
            if (pg.EventsNow >= CPitGuard.MinEvents && pg.EventsOther >= CPitGuard.MinEvents)
            {
                // (asked only when both have enough: the answer moves the try-out on)
                TuneVerdict pv = pg.Recommend(mf.PitRateDegS, out pitNewRate, out double nowCm, out double otherCm);
                if (pv == TuneVerdict.Recommend)
                {
                    pitSuggestion.Text = "→ " + pitNewRate.ToString("0.0") + " °/s (" + nowCm.ToString("0.0") + " → " + otherCm.ToString("0.0") + " cm)";
                    pitSuggestion.ForeColor = Color.DarkOrange;
                    pitApply.Enabled = true;
                }
            }

            toolSwitch.Text = mf.PerToolTuning ? Tr("On") : Tr("Off");
            toolState.Text = mf.PerToolTuning ? Tr("Kept for implements") + ": " + mf.ToolTuneCount : Tr("One set of values for every implement");

            // the helpers
            smoothSwitch.Text = mf.SteerSmoothOn ? Tr("On") : Tr("Off");
            smoothState.Text = Tr("Small wobbles smoothed") +
                (double.IsNaN(d.MaxRateDegS) ? "" : ", max " + (d.MaxRateDegS * 1.2).ToString("0") + " °/s");
            playSwitch.Text = mf.PlayCompOn ? Tr("On") : Tr("Off");
            playState.Text = double.IsNaN(d.BacklashDeg) ? Tr("The play is not measured yet")
                : d.BacklashDeg < CPlayComp.MinPlayDeg ? Tr("Too little play") + " (" + d.BacklashDeg.ToString("0.0") + "°)"
                : Tr("Ahead by") + " " + (Math.Min(CPlayComp.MaxPlayDeg, d.BacklashDeg) / 2).ToString("0.00") + "°";
        }

        // an on / off switch as the others on this page
        private static CheckBox HelperSwitch(int x, int y, bool on, Action<bool> set)
        {
            var c = new CheckBox
            {
                Appearance = Appearance.Button,
                Location = new Point(x, y),
                Size = new Size(108, 34),
                FlatStyle = FlatStyle.Flat,
                TextAlign = ContentAlignment.MiddleCenter,
                BackColor = Color.FromArgb(246, 248, 251),
                UseVisualStyleBackColor = false,
                Font = new Font("Segoe UI Semibold", 10.5f),
                Checked = on
            };
            c.FlatAppearance.CheckedBackColor = Color.FromArgb(104, 159, 56);
            c.CheckedChanged += (s, e) => set(c.Checked);
            return c;
        }

        // dead time measured: send the angle ahead by most of it (the module and the hydraulics answer the rest)
        private void ToggleLead()
        {
            if (mf.SteerLeadS > 0) mf.SetSteerLead(0);
            else if (mf.steerDynamics.Ready) mf.SetSteerLead(mf.steerDynamics.DelayS * 0.8);
            ShowState();
        }

        // today's values as the new starting values: the limits move with them, after the driver says so
        private void NewStartingValues()
        {
            if (AgOpenGPS.Forms.FormDialog.ShowQuestion(Tr("New starting values"),
                Tr("Today's steering values become the new starting values, and the limits move with them. Do it only when the tractor steers well now. Go on?"),
                AgOpenGPS.Forms.DialogSeverity.Warning) != DialogResult.OK) return;
            mf.TakeSteerBaselineNow();
            ShowState();
        }

        // ---- building blocks ----

        private static Panel Card(Control parent, int x, int y, int w, int h, Image icon, string title)
        {
            var card = new Panel { Location = new Point(x, y), Size = new Size(w, h), BackColor = Color.White, BorderStyle = BorderStyle.FixedSingle };
            int iconW = icon.Height > 0 ? Math.Min(66, icon.Width * 28 / icon.Height) : 28;
            var pic = new PictureBox { Image = icon, Location = new Point(8, 6), Size = new Size(iconW, 28), SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Transparent };
            var label = new Label
            {
                Text = title,
                Location = new Point(8 + iconW + 8, 6),
                Size = new Size(w - iconW - 26, 28),
                TextAlign = ContentAlignment.MiddleLeft,
                Font = new Font("Segoe UI Semibold", 11.5f)
            };
            card.Controls.AddRange(new Control[] { pic, label });
            parent.Controls.Add(card);
            return card;
        }

        private Tuner AddTuner(Control parent, int x, int y, int w, Image icon, string title, string when)
        {
            Panel card = Card(parent, x, y, w, TunerH, icon, title);
            var hint = Lbl(10, 34, w - 20, 18, 8.5f, false);
            hint.Text = when;
            hint.ForeColor = Color.Gray;
            var t = new Tuner
            {
                Status = Lbl(10, 52, w - 136, 34, 9f, false),
                Suggestion = Lbl(10, 86, w - 136, 20, 9.5f, true),
                Apply = Tile(Tr("Apply"), w - 118, 56, 108, 50, 11f)
            };
            card.Controls.AddRange(new Control[] { hint, t.Status, t.Suggestion, t.Apply });
            return t;
        }

        private static Button Tile(string text, int x, int y, int w, int h, float size)
        {
            var b = new Button
            {
                Text = text,
                Location = new Point(x, y),
                Size = new Size(w, h),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(246, 248, 251),
                UseVisualStyleBackColor = false,
                Font = new Font("Segoe UI Semibold", size)
            };
            b.FlatAppearance.BorderColor = Color.FromArgb(205, 211, 220);
            return b;
        }

        private static Label Lbl(int x, int y, int w, int h, float size, bool bold)
        {
            return new Label { Location = new Point(x, y), Size = new Size(w, h), Font = new Font(bold ? "Segoe UI Semibold" : "Segoe UI", size) };
        }

        private static Label Box(int x, int y, int w)
        {
            return new Label
            {
                Location = new Point(x, y),
                Size = new Size(w, 76),
                BorderStyle = BorderStyle.FixedSingle,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI Semibold", 9.5f)
            };
        }

        // ---- showing the state ----

        private static string VerdictText(TuneVerdict v, bool speed = false) => SteerVerdicts.Text(v, speed);

        private static void Show(Tuner t, TuneVerdict v, string details, string suggestion, bool speed = false)
        {
            t.Status.Text = details + "\n" + VerdictText(v, speed);
            bool change = v != TuneVerdict.Collecting && v != TuneVerdict.Good;
            t.Suggestion.Text = change ? suggestion : "";
            t.Status.ForeColor = v == TuneVerdict.Good ? Color.ForestGreen : v == TuneVerdict.Collecting ? Color.Gray : Color.DarkOrange;
            t.Apply.Enabled = change;
        }

        private void ShowState()
        {
            UpdateMenu();
            ShowMeasurements();
            ShowDirections();
            ShowCompare();
            ShowLearning();
            ShowStatus();
            if (IsDisposed) return;

            // fully automatic
            restore.Enabled = mf.SteerAutoBaseline != null;
            if (!mf.SteerAutoOn)
            {
                autoStatus.Text = Tr("Off - suggestions wait for Apply");
                autoStatus.ForeColor = Color.Gray;
            }
            else if (mf.SteerWizardOpen)
            {
                autoStatus.Text = Tr("Paused while the Steer Wizard is open");
                autoStatus.ForeColor = Color.DarkOrange;
            }
            else
            {
                double wait = mf.SteerAutoWaitS;
                autoStatus.Text = (mf.SteerAutoStatus ?? Tr("Learning...")) +
                    (wait > 0 ? "\n" + Tr("Next change at the earliest in") + " " + Math.Ceiling(wait).ToString("0") + " s" : "");
                autoStatus.ForeColor = Color.ForestGreen;
            }
            // the suggestions the limits hold back
            IList<string> heldBack = mf.SteerAutoHeldBack;
            string heldText = heldBack.Count == 0 ? Tr("Nothing is held back by the limits") : string.Join("\n", heldBack);
            if (heldText != heldShown)
            {
                heldShown = heldText;
                heldLines.Text = heldText;
                heldLines.ForeColor = heldBack.Count == 0 ? Color.Gray : Color.DarkOrange;
            }
            newStart.Enabled = mf.SteerAutoOn;

            // (the log keeps 30 lines: a new one at the top changes the first line, not the count)
            string top = mf.SteerAutoLog.Count > 0 ? mf.SteerAutoLog[0] : null;
            if (mf.SteerAutoLog.Count != autoLogShown || top != autoLogTop)
            {
                autoLogShown = mf.SteerAutoLog.Count;
                autoLogTop = top;
                autoLog.Items.Clear();
                if (mf.SteerAutoLog.Count == 0) autoLog.Items.Add(Tr("No automatic change yet"));
                foreach (string entry in mf.SteerAutoLog) autoLog.Items.Add(entry);
            }

            // Smart WAS
            CSmartWAS sw = mf.smartWAS;
            wasSamples.Text = Tr("Samples") + ": " + sw.SampleCount;
            wasConfidence.Text = Tr("Confidence") + ": " + sw.Confidence.ToString("0") + " %";
            bool wasReady = sw.IsCollecting && sw.HasValidCalibration;
            wasStatus.Text = wasReady ? Tr("Ready to Apply") : sw.IsCollecting ? Tr("Collecting...") : "Stopped";
            wasStatus.ForeColor = wasReady ? Color.ForestGreen : Color.DarkOrange;
            wasApply.Text = wasReady ? sw.RecommendedOffset.ToString("+0.0;-0.0") + "°" : "-";
            wasApply.Cursor = wasReady ? Cursors.Hand : Cursors.Default;

            // geometry
            CSteerGeometryCal cal = mf.steerGeometryCal;
            geoRight.Text = SideText(Tr("Right turn"), true);
            geoLeft.Text = SideText(Tr("Left turn"), false);
            geoRight.ForeColor = cal.RightReady ? Color.ForestGreen : Color.DarkOrange;
            geoLeft.ForeColor = cal.LeftReady ? Color.ForestGreen : Color.DarkOrange;
            int cpd = values.Cpd(), ack = values.Ackermann();
            geoNewCpd = cal.RecommendedCpd(cpd);
            geoNewAckermann = cal.RecommendedAckermann(ack);
            geoCpd.Text = Tr("Counts per degree") + ": " + cpd + (geoNewCpd.HasValue ? "  →  " + geoNewCpd.Value : "  (" + Tr("collecting...") + ")");
            geoAckermann.Text = Tr("Ackermann") + ": " + ack + (geoNewAckermann.HasValue ? "  →  " + geoNewAckermann.Value : "  (" + Tr("collecting...") + ")");
            geoConfidence.Text = Tr("Confidence") + ": " + cal.Confidence.ToString("0") + " %";
            geoApply.Enabled = (geoNewCpd.HasValue && geoNewCpd.Value != cpd) || (geoNewAckermann.HasValue && geoNewAckermann.Value != ack);

            // motor
            int kp = values.Kp(), pwm = values.MinPwm();
            TuneVerdict mv = mf.motorTune.Recommend(kp, pwm, out newKp, out newMinPwm);
            mf.motorTune.Measure(out double delay, out double swing, out _, out double shortfall, out int held);
            Show(motor, mv,
                Tr("Delay") + ": " + (double.IsNaN(delay) ? "-" : delay.ToString("0.0") + " s") + "   " +
                Tr("Swing") + ": " + swing.ToString("0.0") + "/s   " + Tr("Short") + ": " + shortfall.ToString("0.0") + "°   " +
                Tr("Samples") + ": " + held,
                "Kp: " + kp + " → " + newKp + "    " + Tr("Minimum PWM") + ": " + pwm + " → " + newMinPwm);

            // dead zone
            double zone = values.DeadZone();
            int zoneDelay = values.DeadZoneDelay();
            TuneVerdict dv = mf.deadZoneTune.Recommend(zone, zoneDelay, values.DeadZoneMin, values.DeadZoneMax,
                values.DelayMin, values.DelayMax, out newZone, out newDelay);
            mf.deadZoneTune.Measure(out double noise, out double switches, out double hunts, out int zoneHeld);
            Show(deadZone, dv,
                Tr("Sensor noise") + ": " + noise.ToString("0.00") + "°   " + Tr("Switches") + ": " + switches.ToString("0") + "/min   " +
                Tr("Hunting") + ": " + hunts.ToString("0.0") + "/s   " + Tr("Samples") + ": " + zoneHeld,
                Tr("Heading") + ": " + zone.ToString("0.0") + " → " + newZone.ToString("0.0") + "    " + Tr("Delay") + ": " + zoneDelay + " → " + newDelay);

            // look-ahead
            double hold = values.Hold();
            TuneVerdict lv = mf.lookAheadTune.Recommend(hold, values.HoldMin, values.HoldMax, out newHold);
            mf.lookAheadTune.Measure(out double rms, out double crossings);
            Show(lookAhead, lv,
                Tr("Off the line") + ": " + (rms * 100).ToString("0.0") + " cm   " + Tr("Crossings") + ": " + crossings.ToString("0.0") + " / 10 m   " +
                (mf.lookAheadTune.Count * CLookAheadTune.SampleEveryM).ToString("0") + " m",
                Tr("Look-ahead") + ": " + hold.ToString("0.0") + " → " + newHold.ToString("0.0"));

            // speed factor
            double factor = values.SpeedFactor();
            TuneVerdict fv = mf.lookAheadSpeedTune.Recommend(factor, values.SpeedFactorMin, values.SpeedFactorMax, out newFactor);
            mf.lookAheadSpeedTune.Measure(false, out double slowRms, out _);
            mf.lookAheadSpeedTune.Measure(true, out double fastRms, out _);
            Show(speedFactor, fv,
                Tr("Slowly") + ": " + (slowRms * 100).ToString("0.0") + " cm, " + (mf.lookAheadSpeedTune.SlowCount * CLookAheadTune.SampleEveryM).ToString("0") + " m   " +
                Tr("Fast") + ": " + (fastRms * 100).ToString("0.0") + " cm, " + (mf.lookAheadSpeedTune.FastCount * CLookAheadTune.SampleEveryM).ToString("0") + " m",
                Tr("Speed factor") + ": " + factor.ToString("0.0") + " → " + newFactor.ToString("0.0"), true);

            // GPS guard
            bool guardOn = Properties.Settings.Default.setGpsGuard_Enabled;
            gpsSwitch.Text = guardOn ? Tr("On") : Tr("Off");
            double offAfter = Properties.Settings.Default.setGpsGuard_OffAfterS;
            gpsOffAfter.Text = offAfter > 0 ? offAfter.ToString("0") + " s" : "-";
            CGpsGuard guard = mf.gpsGuard;
            string q = "fix " + mf.pn.fixQuality + ", " + Tr("correction") + " " + mf.pn.age.ToString("0.0") + " s";
            if (!guardOn) { gpsStatus.Text = Tr("Off"); gpsStatus.ForeColor = Color.Gray; }
            else if (guard.Now == CGpsGuard.State.Off) { gpsStatus.Text = Tr("Waiting for an RTK fix") + "\n" + q; gpsStatus.ForeColor = Color.Gray; }
            else if (guard.Now == CGpsGuard.State.Good) { gpsStatus.Text = "RTK fix" + "\n" + q; gpsStatus.ForeColor = Color.ForestGreen; }
            else { gpsStatus.Text = Tr("RTK fix lost") + " - " + ((int)guard.DegradedForS) + " s\n" + q; gpsStatus.ForeColor = Color.Firebrick; }
            gpsStats.Text = Tr("Drop-outs") + ": " + guard.Events + "   " + Tr("Marked") + ": " + mf.GpsGuardMarkedM.ToString("0") + " m" +
                "\n" + Tr("Autosteer off after") + ":";

            gpsStats.Text = Tr("Drop-outs") + ": " + guard.Events + " (" + Tr("corrections") + " " + mf.GpsLinkDrops + ", " + Tr("receiver") + " " + mf.GpsSkyDrops + ")   " +
                Tr("Marked") + ": " + mf.GpsGuardMarkedM.ToString("0") + " m\n" + Tr("Autosteer off after") + ":";

            // RTK bridge
            bool bridgeOn = Properties.Settings.Default.setRtkBridge_Enabled;
            bridgeSwitch.Text = bridgeOn ? Tr("On") : Tr("Off");
            bridgeMax.Text = Properties.Settings.Default.setRtkBridge_MaxS.ToString("0") + " s";
            CRtkBridge rb = mf.rtkBridge;
            rb.Learnt(out _, out double imuDrift, out bool learnt);
            if (!bridgeOn) { bridgeStatus.Text = Tr("Off"); bridgeStatus.ForeColor = Color.Gray; }
            else if (rb.Active)
            {
                bridgeStatus.Text = Tr("Bridging") + " " + ((int)rb.Seconds / 60) + ":" + ((int)rb.Seconds % 60).ToString("00") + "  ±" + rb.ErrorCm.ToString("0") + " cm\n" +
                    (rb.UsesImu ? "IMU" : Tr("steer angle"));
                bridgeStatus.ForeColor = Color.DarkOrange;
            }
            else
            {
                bridgeStatus.Text = Tr("Ready") + "\n" + (learnt ? "IMU " + Tr("drift") + ": " + (imuDrift * 60).ToString("0.00") + "°/min" : Tr("IMU learning (RTK, driving)"));
                bridgeStatus.ForeColor = Color.ForestGreen;
            }
            bridgeStats.Text = Tr("Bridges") + ": " + rb.Bridges +
                (double.IsNaN(rb.LastErrorAtEndCm) ? "" : "   " + Tr("Last") + ": " + rb.LastBridgeS.ToString("0") + " s, " + Tr("off by") + " " + rb.LastErrorAtEndCm.ToString("0.0") + " cm") +
                "\n" + Tr("Longest bridge") + ":";

            // the bridge's exams
            examSwitch.Text = Tr("Exams") + ": " + (Properties.Settings.Default.setRtkBridge_Exams ? Tr("On") : Tr("Off"));
            string method = mf.BridgeMethod;
            if (method.Length == 0) examLine.Text = Tr("Exams") + ": " + Tr("no heading source (IMU, dual antenna or steer module)");
            else
            {
                CBridgeExams ex = mf.bridgeExams;
                int count = ex.Count(method);
                string name = method == "DR-Dual" ? Tr("dual antenna") : method == "DR-IMU" ? "IMU" : Tr("steer angle");
                string line = Tr("Exams") + " (" + name + "): " + count;
                if (count < CBridgeExams.MinExams)
                    line += " / " + CBridgeExams.MinExams + " - " + Tr("the built-in estimate is used until then");
                else
                {
                    double p60 = ex.P95(method, 1), p120 = ex.P95(method, 2);
                    line += "   60 s: " + (double.IsNaN(p60) ? "-" : p60.ToString("0.0") + " cm") + "   120 s: " + (double.IsNaN(p120) ? "-" : p120.ToString("0.0") + " cm") + " (95%)" +
                        "\n" + Tr("Trusted for") + ": " + ex.TrustedSeconds(method, Properties.Settings.Default.setRtkBridge_MaxCm) + " s";
                }
                if (mf.ExamRunning) line += "\n" + Tr("An exam is running in the background");
                examLine.Text = line;
            }

            // TDCP
            int mode = mf.TdcpMode;
            tdcpMode.Text = mode == 0 ? Tr("Off") : mode == 1 ? Tr("Learn only") : Tr("Automatic");
            // strong colours with their own text colour: readable in the day and the night theme alike
            tdcpMode.BackColor = mode == 2 ? Color.FromArgb(67, 140, 45) : mode == 1 ? Color.FromArgb(240, 170, 40) : Color.FromArgb(110, 116, 125);
            tdcpMode.ForeColor = mode == 1 ? Color.FromArgb(25, 25, 25) : Color.White;
            tdcpMode.FlatAppearance.BorderColor = tdcpMode.BackColor;
            var engine = mf.tdcp;
            if (mode == 0) { tdcpStatus.Text = Tr("Off"); tdcpStatus.ForeColor = Color.Gray; }
            else if (engine.SinceFrameS > 5)
            {
                tdcpStatus.Text = Tr("No raw data from the receiver") + "\n" + Tr("Set the receiver as above; nothing else changes meanwhile");
                tdcpStatus.ForeColor = Color.DarkOrange;
            }
            else if (engine.SinceEpochS > 5)
            {
                // frames come, but none with carrier phase (only ephemerides, or other messages)
                tdcpStatus.Text = engine.Format + ": " + Tr("Raw data comes, but no carrier phase") + "\n" + Tr("Turn on MSM (RTCM) or RXM-RAWX (u-blox)");
                tdcpStatus.ForeColor = Color.DarkOrange;
            }
            else if (engine.EphemerisCount == 0)
            {
                tdcpStatus.Text = engine.Format + ": " + Tr("Carrier phase comes, but no ephemerides yet") + "\n" +
                    Tr("Turn on the ephemeris messages or RXM-SFRBX, or use an NTRIP caster that sends them");
                tdcpStatus.ForeColor = Color.DarkOrange;
            }
            else
            {
                var last = engine.Last;
                string lastText = last == null ? Tr("waiting for observations")
                    : last.Ok ? last.Sats + " " + Tr("satellites") + ", ±" + (last.RmsM * 1000).ToString("0.0") + " mm"
                    : last.Reason;
                int viaNtrip = engine.EphemerisFromCorrections;
                tdcpStatus.Text = engine.Format + "   " + Tr("Ephemerides") + ": " + engine.EphemerisCount +
                    (viaNtrip > 0 ? " (" + viaNtrip + " " + Tr("from NTRIP") + ")" : "") +
                    "\n" + Tr("Epochs") + ": " + engine.GoodEpochs + " / " + engine.Epochs + "   " + Tr("Last") + ": " + lastText;
                tdcpStatus.ForeColor = engine.Alive && last != null && last.Ok ? Color.ForestGreen : Color.DarkOrange;
            }
            int tdcpCount = mf.bridgeExams.Count(FormGPS.TdcpMethod);
            string tdcpLine = Tr("Exams") + ": " + tdcpCount;
            if (tdcpCount < CBridgeExams.MinExams) tdcpLine += " / " + CBridgeExams.MinExams;
            else
            {
                double t60 = mf.bridgeExams.P95(FormGPS.TdcpMethod, 1), t120 = mf.bridgeExams.P95(FormGPS.TdcpMethod, 2);
                tdcpLine += "   60 s: " + (double.IsNaN(t60) ? "-" : t60.ToString("0.0") + " cm") + "   120 s: " + (double.IsNaN(t120) ? "-" : t120.ToString("0.0") + " cm") + " (95%)";
            }
            bool trusted = mf.TdcpTrusted(mf.BridgeMethod);
            tdcpLine += "\n" + (mode == 2
                ? (mf.BridgeUsesTdcp ? Tr("In use now") : trusted ? Tr("Takes over at the next RTK loss") : Tr("Not proven better than dead reckoning yet"))
                : mode == 1 ? Tr("Learns only, never steers") : "");
            tdcpExams.Text = tdcpLine;

            // sensor linearity
            var vsx = Properties.VehicleSettings.Default;
            TuneVerdict lin = mf.LinearityRecommend(out newLinMiddle, out newLinBig);
            int[] bandN = mf.steerGeometryCal.BandCounts();
            Show(linearity, lin,
                Tr("Samples") + ": " + bandN[0] + " / " + bandN[1] + " / " + bandN[2] + "  (5-12° / 12-20° / 20°+)",
                Tr("Middle") + ": " + vsx.setAS_WasLinMiddle.ToString("0.00") + " → " + newLinMiddle.ToString("0.00") + "    " +
                Tr("Big") + ": " + vsx.setAS_WasLinBig.ToString("0.00") + " → " + newLinBig.ToString("0.00"));

            // fast Kp
            TuneVerdict kfv = mf.FastKpRecommend(out newKpFast);
            mf.motorTuneFast.Measure(out double fDelay, out double fSwing, out _, out _, out int fHeld);
            Show(fastKp, kfv,
                Tr("Delay") + ": " + (double.IsNaN(fDelay) ? "-" : fDelay.ToString("0.0") + " s") + "   " + Tr("Swing") + ": " + fSwing.ToString("0.0") + "/s   " +
                Tr("Samples") + ": " + fHeld,
                Tr("Kp above 12 km/h") + ": " + (vsx.setAS_KpFast > 0 ? vsx.setAS_KpFast.ToString() : "-") + " → " + newKpFast);

            // IMU fusion
            mf.fusionTune.Measure(out double headingNoise, out double drift);
            TuneVerdict fuv = mf.FusionTunable ? mf.fusionTune.Recommend(mf.ahrs.fusionWeight, 0.002, 0.4, out newFusion) : TuneVerdict.Collecting;
            Show(fusion, fuv,
                mf.FusionTunable
                    ? Tr("Heading noise") + ": " + headingNoise.ToString("0.00") + "°   " + Tr("Drift") + ": " + drift.ToString("0.00") + "°   " + Tr("Samples") + ": " + mf.fusionTune.Count
                    : Tr("Only with a single antenna and an IMU"),
                Tr("IMU fusion") + ": " + mf.ahrs.fusionWeight.ToString("0.000") + " → " + newFusion.ToString("0.000"));

            // roll zero
            double zero = mf.ahrs.rollZero;
            TuneVerdict rv = mf.rollZeroTune.Recommend(zero, out newRollZero);
            mf.rollZeroTune.Measure(out double zeroError, out double zeroSpread, out int cells);
            Show(rollZero, rv,
                Tr("Zero error") + ": " + zeroError.ToString("+0.00;-0.00") + "°   " + Tr("Spread") + ": " + zeroSpread.ToString("0.00") + "°   " +
                Tr("Cells") + ": " + cells + " / " + CRollZeroTune.MinPairs,
                Tr("Roll zero") + ": " + zero.ToString("0.00") + " → " + newRollZero.ToString("0.00"));

            // coming onto the line
            double acq = values.AcquireFactor();
            TuneVerdict av = mf.acquireTune.Recommend(acq, values.AcquireMin, values.AcquireMax, out newAcquire);
            mf.acquireTune.Measure(out double overshoot, out double comingOn, out int events);
            Show(acquire, av,
                Tr("Overshoot") + ": " + (overshoot * 100).ToString("0") + " cm   " + Tr("Coming on") + ": " + comingOn.ToString("0") + " m   " +
                Tr("Approaches") + ": " + events + " / " + CAcquireTune.MinEvents,
                Tr("Acquire factor") + ": " + acq.ToString("0.00") + " → " + newAcquire.ToString("0.00"));

            // integral
            double gain = values.Integral();
            TuneVerdict iv = mf.integralTune.Recommend(gain, values.IntegralMin, values.IntegralMax, out newIntegral);
            mf.integralTune.Measure(out double meanOff, out double integralRms, out double waves);
            Show(integral, iv,
                Tr("Mean off") + ": " + (meanOff * 100).ToString("+0.0;-0.0") + " cm   " + Tr("Waves") + ": " + waves.ToString("0.0") + " / 100 m   " +
                (mf.integralTune.Count * CIntegralTune.SampleEveryM).ToString("0") + " m",
                Tr("Integral") + ": " + gain.ToString("0.00") + " → " + newIntegral.ToString("0.00"));

            // U-turn
            double uc = values.UTurnComp();
            TuneVerdict uv = mf.uTurnTune.Recommend(uc, values.UTurnMin, values.UTurnMax, out newUTurn);
            mf.uTurnTune.Measure(out int over, out int wide, out int turns);
            Show(uTurn, uv,
                Tr("Turns") + ": " + turns + " / " + CUTurnTune.MinTurns + "   " + Tr("Over") + ": " + over + "   " + Tr("Wide") + ": " + wide,
                Tr("U-turn compensation") + ": " + uc.ToString("0.0") + " → " + newUTurn.ToString("0.0"));

            // side-hill
            double comp = values.SideHill();
            TuneVerdict sv = mf.sideHillTune.Recommend(comp, values.SideHillMin, values.SideHillMax, out newSideHill);
            mf.sideHillTune.Measure(out double slope, out double fit, out double spread);
            Show(sideHill, sv,
                Tr("Wheel per roll") + ": " + slope.ToString("0.00") + "   " + Tr("Fit") + ": " + fit.ToString("0.00") + "   " +
                Tr("Roll seen") + ": " + spread.ToString("0.0") + "°   " + Tr("Samples") + ": " + mf.sideHillTune.Count,
                Tr("Side-hill compensation") + ": " + comp.ToString("0.00") + " → " + newSideHill.ToString("0.00"));
        }

        private string SideText(string name, bool right)
        {
            CSteerGeometryCal cal = mf.steerGeometryCal;
            int count = right ? cal.RightCount : cal.LeftCount;
            double ratio = cal.Ratio(right, out double spread);
            string text = name + "\n" + Tr("Samples") + ": " + count + " / " + CSteerGeometryCal.MinSamples;
            if (!double.IsNaN(ratio))
                text += "\n" + Tr("Real / sensor") + ": " + ratio.ToString("0.000") + "  (±" + (spread * 100).ToString("0") + " %)";
            return text;
        }

        // ---- fully automatic ----

        private void ToggleAuto()
        {
            if (settingSwitch) return;
            if (autoSwitch.Checked)
            {
                DialogResult answer = AgOpenGPS.Forms.FormDialog.ShowQuestion(Tr("Fully automatic"),
                    Tr("The suggestions are then applied by themselves, also while this window is open: one small change a minute at most, and never further than set limits from today's values. Every change is shown with its time and reason, and Restore starting values brings today's values back. Turn it on?"));
                if (answer != DialogResult.OK)
                {
                    settingSwitch = true;
                    autoSwitch.Checked = false;
                    settingSwitch = false;
                    return;
                }
            }
            mf.SetSteerAuto(autoSwitch.Checked);
            ShowState();
        }

        private void Restore()
        {
            SteerBaseline b = mf.SteerAutoBaseline;
            if (b == null) return;
            // everything fully automatic has learnt goes: ask first
            if (AgOpenGPS.Forms.FormDialog.ShowQuestion(Tr("Restore starting values"),
                Tr("All the values fully automatic has changed go back to where they were when it was switched on. Are you sure?"),
                AgOpenGPS.Forms.DialogSeverity.Warning) != DialogResult.OK) return;
            if (!mf.RestoreSteerBaseline()) return;
            // the open steer settings show the restored values too, or closing them would write the old ones back
            values.SetMotor(b.Kp, b.MinPwm);
            values.SetDeadZone(b.DeadZoneHeading * 0.01, b.DeadZoneDelay);
            values.SetHold(b.Hold);
            values.SetSpeedFactor(b.SpeedFactor);
            values.SetSideHill(b.SideHill);
            values.SetGeometry(b.Cpd, b.Ackermann);
            values.SetWasOffset(b.WasOffset);
            // (fusion, linearity and the fast Kp have no bars here: RestoreSteerBaseline has set them)
            if (b.HasExtras)
            {
                values.SetAcquireFactor(b.AcquireFactor);
                values.SetIntegral(b.Integral);
                values.SetUTurnComp(b.UTurnComp);
            }
            mf.TimedMessageBox(3000, Tr("Smart features"), Tr("Starting values restored"));
            ShowState();
        }

        // ---- applying ----

        private void ApplyWas()
        {
            if (!mf.smartWAS.IsCollecting || !mf.smartWAS.HasValidCalibration) return;
            values.ApplySmartWas();
            ShowState();
        }

        private void ApplyGeometry()
        {
            int oldCpd = values.Cpd(), oldAck = values.Ackermann();
            int cpd = geoNewCpd ?? oldCpd, ack = geoNewAckermann ?? oldAck;
            values.SetGeometry(cpd, ack);
            mf.steerGeometryCal.Reset();   // measured with the old values: measure again with the new ones
            mf.ResetLineTuners();
            Done(Tr("Counts per degree") + " " + oldCpd + " → " + cpd + ", " + Tr("Ackermann") + " " + oldAck + " → " + ack);
        }

        private void ApplyMotor()
        {
            int kp = values.Kp(), pwm = values.MinPwm();
            values.SetMotor(newKp, newMinPwm);
            mf.motorTune.Reset();
            Done("Kp " + kp + " → " + newKp + ", " + Tr("Minimum PWM") + " " + pwm + " → " + newMinPwm);
        }

        private void ApplyDeadZone()
        {
            double zone = values.DeadZone();
            int delay = values.DeadZoneDelay();
            values.SetDeadZone(newZone, newDelay);
            mf.deadZoneTune.Reset();
            Done(Tr("Heading") + " " + zone.ToString("0.0") + " → " + newZone.ToString("0.0") + ", " + Tr("Delay") + " " + delay + " → " + newDelay);
        }

        private void ApplyLookAhead()
        {
            double hold = values.Hold();
            values.SetHold(newHold);
            mf.lookAheadTune.Reset();
            mf.lookAheadSpeedTune.Reset();   // the speed factor was judged with the old look-ahead
            Done(Tr("Look-ahead") + " " + hold.ToString("0.0") + " → " + newHold.ToString("0.0"));
        }

        private void ApplySpeedFactor()
        {
            double factor = values.SpeedFactor();
            values.SetSpeedFactor(newFactor);
            mf.lookAheadSpeedTune.Reset();
            mf.lookAheadTune.Reset();
            Done(Tr("Speed factor") + " " + factor.ToString("0.0") + " → " + newFactor.ToString("0.0"));
        }

        private void ApplySideHill()
        {
            double comp = values.SideHill();
            values.SetSideHill(newSideHill);
            mf.sideHillTune.Reset();
            mf.ResetLineTuners();
            Done(Tr("Side-hill compensation") + " " + comp.ToString("0.00") + " → " + newSideHill.ToString("0.00"));
        }

        private void StepBridgeMax(int step)
        {
            double now = Properties.Settings.Default.setRtkBridge_MaxS;
            Properties.Settings.Default.setRtkBridge_MaxS = Math.Max(30, Math.Min(600, now + step));
            Properties.Settings.Default.Save();
            ShowState();
        }

        private void StepGpsOffAfter(int step)
        {
            double now = Properties.Settings.Default.setGpsGuard_OffAfterS;
            Properties.Settings.Default.setGpsGuard_OffAfterS = Math.Max(0, Math.Min(120, now + step));
            Properties.Settings.Default.Save();
            ShowState();
        }

        private void ApplyLinearity()
        {
            var vs = Properties.VehicleSettings.Default;
            string text = Tr("Sensor linearity") + " " + vs.setAS_WasLinMiddle.ToString("0.00") + "/" + vs.setAS_WasLinBig.ToString("0.00") + " → " +
                newLinMiddle.ToString("0.00") + "/" + newLinBig.ToString("0.00");
            mf.SetLinearity(newLinMiddle, newLinBig);
            Done(text);
        }

        private void ApplyFusion()
        {
            double old = mf.ahrs.fusionWeight;
            mf.SetFusionWeight(newFusion);
            mf.fusionTune.Reset();
            Done(Tr("IMU fusion") + " " + old.ToString("0.000") + " → " + newFusion.ToString("0.000"));
        }

        private void ApplyFastKp()
        {
            int old = Properties.VehicleSettings.Default.setAS_KpFast;
            mf.SetKpFast(newKpFast);
            mf.motorTuneFast.Reset();
            Done(Tr("Kp above 12 km/h") + " " + (old > 0 ? old.ToString() : "-") + " → " + newKpFast);
        }

        private void ApplyRollZero()
        {
            double old = mf.ahrs.rollZero;
            Properties.VehicleSettings.Default.setIMU_rollZero = mf.ahrs.rollZero = newRollZero;
            Properties.VehicleSettings.Default.Save();
            mf.rollZeroTune.Reset();
            mf.ResetLineTuners();
            Done(Tr("Roll zero") + " " + old.ToString("0.00") + " → " + newRollZero.ToString("0.00"));
        }

        private void ApplyAcquire()
        {
            double old = values.AcquireFactor();
            values.SetAcquireFactor(newAcquire);
            mf.acquireTune.Reset();
            Done(Tr("Acquire factor") + " " + old.ToString("0.00") + " → " + newAcquire.ToString("0.00"));
        }

        private void ApplyIntegral()
        {
            double old = values.Integral();
            values.SetIntegral(newIntegral);
            mf.integralTune.Reset();
            Done(Tr("Integral") + " " + old.ToString("0.00") + " → " + newIntegral.ToString("0.00"));
        }

        private void ApplyUTurn()
        {
            double old = values.UTurnComp();
            values.SetUTurnComp(newUTurn);
            mf.uTurnTune.Reset();
            Done(Tr("U-turn compensation") + " " + old.ToString("0.0") + " → " + newUTurn.ToString("0.0"));
        }

        private void Done(string what)
        {
            Log.EventWriter("Smart features: " + what);
            mf.TimedMessageBox(3000, Tr("Smart features"), what);
            ShowState();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            mf.isSteerGeometryCalOn = false;
            refresh.Stop();
            refresh.Dispose();
            base.OnFormClosed(e);
        }
    }
}
