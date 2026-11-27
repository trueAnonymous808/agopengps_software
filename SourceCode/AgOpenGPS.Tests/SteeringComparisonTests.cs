using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Text;

namespace AgOpenGPS.Tests
{
    /// <summary>
    /// Pure pursuit, adaptive pure pursuit and MPC on the same simulated tractor (dead time, turning speed limit, GPS
    /// noise) and the same paths: how far off the line each stays and how busy the steering is. Run by name; prints a
    /// table (it decides nothing on its own, apart from the checks at the end).
    /// </summary>
    public class SteeringComparisonTests
    {
        private const double Wheelbase = 3.0;

        private enum Method { PurePursuit, PurePursuitPreview, Adaptive, Mpc, Lqr, Hybrid, AdaptiveTurn }

        private sealed class Result
        {
            public double RmsCm, MaxCm, BusyDegS;
        }

        // ---- paths (dense polylines) ----

        private static List<vec2> Straight(double length)
        {
            var p = new List<vec2>();
            for (double s = -10; s <= length; s += 0.5) p.Add(new vec2(0, s));
            return p;
        }

        // straight, then a right arc of the radius through the angle, then straight again
        private static List<vec2> StraightArcStraight(double before, double radius, double angleDeg, double after)
        {
            var p = new List<vec2>();
            for (double s = -10; s < before; s += 0.5) p.Add(new vec2(0, s));
            double sweep = angleDeg * Math.PI / 180;
            for (double a = 0; a < sweep; a += 0.5 / radius) p.Add(new vec2(radius - radius * Math.Cos(a), before + radius * Math.Sin(a)));
            vec2 end = new vec2(radius - radius * Math.Cos(sweep), before + radius * Math.Sin(sweep));
            for (double s = 0; s <= after; s += 0.5) p.Add(new vec2(end.easting + s * Math.Sin(sweep), end.northing + s * Math.Cos(sweep)));
            return p;
        }

        // gentle S-curves: right, left, right, radius 30
        private static List<vec2> SCurve()
        {
            var p = new List<vec2>();
            double e = 0, n = -10, h = 0;
            for (double s = 0; s < 10; s += 0.5) { p.Add(new vec2(e, n)); n += 0.5; }
            int[] turns = { 1, -1, 1, -1 };
            foreach (int dir in turns)
            {
                for (double s = 0; s < 30; s += 0.5)
                {
                    p.Add(new vec2(e, n));
                    h += dir * 0.5 / 30;
                    e += 0.5 * Math.Sin(h);
                    n += 0.5 * Math.Cos(h);
                }
            }
            for (double s = 0; s < 300; s += 0.5) { p.Add(new vec2(e, n)); e += 0.5 * Math.Sin(h); n += 0.5 * Math.Cos(h); }
            return p;
        }

        // ---- on the path ----

        private static void Closest(List<vec2> path, double e, double n, ref int seg, out double side, out vec2 at)
        {
            int best = seg;
            double bestD = double.MaxValue;
            for (int i = Math.Max(0, seg - 5); i < Math.Min(path.Count - 1, seg + 40); i++)
            {
                double de = path[i + 1].easting - path[i].easting, dn = path[i + 1].northing - path[i].northing;
                double len2 = de * de + dn * dn;
                double u = Math.Max(0, Math.Min(1, ((e - path[i].easting) * de + (n - path[i].northing) * dn) / len2));
                double pe = path[i].easting + u * de, pn = path[i].northing + u * dn;
                double d = (pe - e) * (pe - e) + (pn - n) * (pn - n);
                if (d < bestD) { bestD = d; best = i; }
            }
            seg = best;
            double ee = path[seg + 1].easting - path[seg].easting, nn = path[seg + 1].northing - path[seg].northing;
            double l = Math.Sqrt(ee * ee + nn * nn);
            double uu = Math.Max(0, Math.Min(1, ((e - path[seg].easting) * ee + (n - path[seg].northing) * nn) / (l * l)));
            at = new vec2(path[seg].easting + uu * ee, path[seg].northing + uu * nn);
            side = ((e - path[seg].easting) * nn - (n - path[seg].northing) * ee) / l;   // + right of the path
        }

        private static List<vec2> Ahead(List<vec2> path, int seg, vec2 at, double length)
        {
            var a = new List<vec2> { at };
            double gone = 0;
            for (int i = seg + 1; i < path.Count && gone < length; i++)
            {
                vec2 last = a[a.Count - 1];
                gone += Math.Sqrt((path[i].easting - last.easting) * (path[i].easting - last.easting) + (path[i].northing - last.northing) * (path[i].northing - last.northing));
                a.Add(path[i]);
            }
            return a;
        }

        // AgOpenGPS's look-ahead from hold 3, speed factor 1.5, acquire 0.9 (the defaults)
        private static double BaseLookAhead(double speedMs, double offLine)
        {
            const double hold = 3, mult = 1.5, acquire = 0.9;
            double kmh = speedMs * 3.6;
            double x = Math.Abs(offLine);
            double h = x <= 0.1 ? hold : x < 0.4 ? (1 - (x - 0.1) / 0.3) * (hold - hold * acquire) + hold * acquire : hold * acquire;
            return Math.Max(2, kmh * 0.05 * mult * h + h);
        }

        private static Result Drive(Method method, List<vec2> path, double startOff, double seconds, double speedMs, double deadTimeS, double rateDegS, int seed = 1,
            double mpcWeight = 3e-5, bool mpcQuick = false, CSteerLqr lqrSet = null, bool mpcCold = false,
            Func<double, double> speedAt = null, double speedAverage = 1, double speedScale = 1, double countFromS = 3,
            double deadZoneDeg = 0, int deadZoneDelay = 5, bool deadZoneBySpeed = false, double wheelShare = 1)
        {
            // the dead zone as the program has it: the asked angle within it of the wheels' for longer than the delay,
            // the angle is no longer sent (the wheels keep the last one); wheelShare: the wheels take this share of it
            // (the simulator's 0.9)
            int dzCount = 0;
            bool inDeadZone = false;
            // speedAt: the true speed over time (else speedMs all along); the controller sees it averaged as the program
            // does (speedAverage: the new speed's share per fix, 1: none) and scaled (a speed told wrong)
            double vTrue = speedAt?.Invoke(0) ?? speedMs, vCtrl = vTrue * speedScale;
            const double plantDt = 0.01, controlDt = 0.1;
            var rnd = new Random(seed);
            double e = startOff, n = 0, h = 0, wheel = 0;
            var sent = new List<double[]>();
            var mpc = new CSteerMpc { WeightMove = mpcWeight, Quick = mpcQuick };
            var lqr = lqrSet ?? new CSteerLqr();
            var blend = new CHybridBlend();
            var adaptive = new CAdaptiveLookAhead();
            double last = 0, next = 0, sumSq = 0, max = 0, busy = 0;
            int count = 0, seg = 0, trueSeg = 0;
            double gpsE = 0, gpsN = 0;
            double AskedAt(double t)
            {
                for (int i = sent.Count - 1; i >= 0; i--) if (sent[i][0] <= t) return sent[i][1];
                return 0;
            }
            for (double t = 0; t < seconds; t += plantDt)
            {
                vTrue = speedAt?.Invoke(t) ?? speedMs;
                if (t >= next - 1e-9)
                {
                    next += controlDt;
                    vCtrl = vCtrl * (1 - speedAverage) + vTrue * speedScale * speedAverage;
                    speedMs = vCtrl;
                    // what the receiver tells: 1 cm noise
                    gpsE = e + Gauss(rnd) * 0.01;
                    gpsN = n + Gauss(rnd) * 0.01;
                    Closest(path, gpsE, gpsN, ref seg, out double side, out vec2 at);
                    List<vec2> ahead = Ahead(path, seg, at, Math.Max(25, speedMs * 4 + 10));
                    double u;
                    double LqrU()
                    {
                        // off the line and heading off it, from the path's first segment; the angles sent, newest first
                        double pe = ahead[1].easting - ahead[0].easting, pn = ahead[1].northing - ahead[0].northing;
                        double psi = CPathAhead.Wrap(h - Math.Atan2(pe, pn));
                        int nSteps = lqr.DelaySteps(deadTimeS);
                        var chain = new double[nSteps];
                        for (int i = 0; i < nSteps; i++) chain[i] = AskedAt(t - (i + 1) * controlDt + 1e-6) * Math.PI / 180;
                        // each angle against the path's where it acts: the one sent i steps ago acts (n - i) steps from now
                        double FfAt(int i) => CSteerLqr.PathAngleAt(ahead, speedMs * (nSteps - i) * controlDt, Wheelbase);
                        return lqr.Steer(side, psi, chain, speedMs, Wheelbase, FfAt, 35 * Math.PI / 180) * 180 / Math.PI;
                    }
                    double MpcU()
                    {
                        if (mpcCold) mpc.ForgetWarmStart();
                        int steps = (int)Math.Round(deadTimeS / mpc.StepS);
                        var pending = new double[steps];
                        for (int k = 0; k < steps; k++) pending[k] = AskedAt(t - deadTimeS + k * mpc.StepS);
                        return mpc.Solve(new CSteerMpc.State
                        {
                            East = gpsE,
                            North = gpsN,
                            HeadingRad = h,
                            SpeedMs = speedMs,
                            WheelbaseM = Wheelbase,
                            WheelDeg = wheel,
                            MaxDeg = 35,
                            RateDegS = rateDegS,
                            LastDeg = last,
                            Pending = pending
                        }, ahead);
                    }
                    if (method == Method.Lqr) u = LqrU();
                    else if (method == Method.Mpc) u = MpcU();
                    else if (method == Method.Hybrid)
                    {
                        double he = ahead[1].easting - ahead[0].easting, hn = ahead[1].northing - ahead[0].northing;
                        double headingOff = CPathAhead.Wrap(h - Math.Atan2(he, hn)) * 180 / Math.PI;
                        double share = blend.Update(t, CAdaptiveLookAhead.TightestCurvature(ahead, speedMs * 3 + 5), side, headingOff);
                        u = CHybridBlend.Blend(LqrU(), share > 0.001 ? MpcU() : double.NaN, share);
                    }
                    else
                    {
                        double ld = BaseLookAhead(speedMs, side);
                        if (method == Method.Adaptive || method == Method.AdaptiveTurn)
                            ld = adaptive.Adapt(t, ld, speedMs, deadTimeS, CAdaptiveLookAhead.TightestCurvature(ahead, ld + speedMs * deadTimeS), side, t > 3);
                        vec2 goal = CPathAhead.PointAt(ahead, ld);
                        double dx = goal.easting - gpsE, dy = goal.northing - gpsN;
                        double lateral = dx * Math.Cos(h) - dy * Math.Sin(h);
                        u = Math.Atan(2 * Wheelbase * lateral / (dx * dx + dy * dy)) * 180 / Math.PI;
                        // (in a U-turn the program has no curve preview: only the adaptive look-ahead)
                        if (method != Method.PurePursuit && method != Method.AdaptiveTurn)
                            u += CPathAhead.CurvePreviewDeg(ahead, ld, speedMs, deadTimeS, Wheelbase, rateDegS);
                        u = Math.Max(-35, Math.Min(35, u));
                    }
                    if (t > 3) busy += Math.Abs(u - last);
                    double zone = deadZoneDeg;
                    if (deadZoneBySpeed) zone *= CVehicleDeadZone.SpeedFactor(vCtrl * 3.6);
                    if (zone > 0 && Math.Abs(u - wheel) < zone)
                    {
                        if (++dzCount > deadZoneDelay) inDeadZone = true;
                    }
                    else
                    {
                        dzCount = 0;
                        inDeadZone = false;
                    }
                    if (!inDeadZone)
                    {
                        last = u;
                        sent.Add(new[] { t, u * wheelShare });
                    }
                }
                double target = AskedAt(t - deadTimeS);
                double step = rateDegS * plantDt;
                wheel += Math.Max(-step, Math.Min(step, target - wheel));
                h += vTrue * Math.Tan(wheel * Math.PI / 180) / Wheelbase * plantDt;
                e += vTrue * Math.Sin(h) * plantDt;
                n += vTrue * Math.Cos(h) * plantDt;

                // the true distance off the path (followed all along), counted after the first seconds
                Closest(path, e, n, ref trueSeg, out double off, out _);
                if (t > countFromS)
                {
                    sumSq += off * off;
                    max = Math.Max(max, Math.Abs(off));
                    count++;
                }
            }
            return new Result { RmsCm = Math.Sqrt(sumSq / Math.Max(1, count)) * 100, MaxCm = max * 100, BusyDegS = busy / Math.Max(1, seconds - 3) };
        }

        private static double Gauss(Random r) => Math.Sqrt(-2 * Math.Log(1 - r.NextDouble())) * Math.Cos(2 * Math.PI * r.NextDouble());

        // the paths and steering gears every comparison uses
        private static (string Name, Func<List<vec2>> Path, double Start, double Seconds)[] Scenarios() => new (string, Func<List<vec2>>, double, double)[]
        {
            ("Straight, on the line", () => Straight(400), 0, 60),
            ("Straight, 1 m off", () => Straight(400), 1.0, 30),
            ("Into a 25 m curve", () => StraightArcStraight(30, 25, 90, 300), 0, 45),
            ("Into a 12 m curve", () => StraightArcStraight(30, 12, 90, 300), 0, 40),
            ("S-curves 30 m", SCurve, 0, 70),
        };

        // a U-turn: north 30 m, a half circle of the radius to the right, then south
        private static List<vec2> UTurn(double radius)
        {
            var p = new List<vec2>();
            for (double s = -10; s < 30; s += 0.5) p.Add(new vec2(0, s));
            for (double a = 0; a < Math.PI; a += 0.5 / radius) p.Add(new vec2(radius - radius * Math.Cos(a), 30 + radius * Math.Sin(a)));
            for (double s = 0; s <= 200; s += 0.5) p.Add(new vec2(2 * radius, 30 - s));
            return p;
        }

        [Test, Explicit("U-turns, run by name")]
        public void CompareInUTurns()
        {
            var sb = new StringBuilder();
            foreach (var g in new (string Name, double Dead, double Rate)[] { ("quick 0.15 s 30°/s", 0.15, 30), ("medium 0.3 s 20°/s", 0.3, 20), ("slow 0.6 s 12°/s", 0.6, 12) })
                foreach (double kmh in new[] { 6.0, 9.0, 12.0 })
                    foreach (double r in new[] { 6.0, 9.0 })
                    {
                        sb.Append((g.Name + ", " + kmh + " km/h, radius " + r + " m").PadRight(42));
                        foreach (Method m in new[] { Method.PurePursuit, Method.AdaptiveTurn })
                        {
                            Result res = Drive(m, UTurn(r), 0, 25, kmh / 3.6, g.Dead, g.Rate);
                            sb.Append(("  " + m + " " + res.RmsCm.ToString("0.0") + "/" + res.MaxCm.ToString("0")).PadRight(28));
                        }
                        sb.AppendLine();
                    }
            System.IO.File.WriteAllText(System.IO.Path.Combine(TestContext.CurrentContext.WorkDirectory, "uturn.txt"), sb.ToString());
        }

        [Test, Explicit("speeding up fast, run by name")]
        public void SpeedingUp()
        {
            var sb = new StringBuilder();
            // straight on the line at 10 km/h, at 8 s up to the speed within 2 s, then on (max cm after the change)
            foreach (double kmh in new[] { 25.0, 40.0 })
                foreach (var c in new (string Name, double Avg, double Scale)[] { ("speed exact", 1, 1), ("speed averaged as now", 0.25, 1), ("averaged, told 4 % high", 0.25, 1.04) })
                {
                    double top = kmh / 3.6;
                    Func<double, double> prof = t => t < 8 ? 10 / 3.6 : t < 10 ? 10 / 3.6 + (top - 10 / 3.6) * (t - 8) / 2 : top;
                    sb.Append((kmh + " km/h, " + c.Name).PadRight(40));
                    foreach (Method m in new[] { Method.PurePursuit, Method.Mpc, Method.Lqr, Method.Hybrid })
                    {
                        Result r = Drive(m, Straight(3000), 0, 30, 10 / 3.6, 0.15, 30, 1, 3e-5, false, null, false, prof, c.Avg, c.Scale, 8);
                        sb.Append(("  " + m + " " + r.RmsCm.ToString("0.0") + "/" + r.MaxCm.ToString("0.0")).PadRight(26));
                    }
                    sb.AppendLine();
                }
            System.IO.File.WriteAllText(System.IO.Path.Combine(TestContext.CurrentContext.WorkDirectory, "speeding-up.txt"), sb.ToString());
        }

        [Test, Explicit("the dead zone at speed, run by name")]
        public void DeadZoneAtSpeed()
        {
            var sb = new StringBuilder();
            foreach (double kmh in new[] { 10.0, 25.0, 40.0 })
                foreach (var c in new (string Name, double Zone, bool BySpeed)[] { ("no dead zone", 0, false), ("dead zone 0.1, fixed", 0.1, false), ("dead zone 0.1, by speed", 0.1, true) })
                {
                    sb.Append((kmh + " km/h, " + c.Name).PadRight(36));
                    foreach (Method m in new[] { Method.PurePursuit, Method.Mpc, Method.Lqr, Method.Hybrid })
                    {
                        Result r = Drive(m, Straight(3000), 0, 40, kmh / 3.6, 0.15, 30, 1, 3e-5, false, null, false, null, 1, 1, 5, c.Zone, 5, c.BySpeed, 0.9);
                        sb.Append(("  " + m + " " + r.RmsCm.ToString("0.0") + "/" + r.MaxCm.ToString("0.0")).PadRight(26));
                    }
                    sb.AppendLine();
                }
            System.IO.File.WriteAllText(System.IO.Path.Combine(TestContext.CurrentContext.WorkDirectory, "dead-zone.txt"), sb.ToString());
        }

        [Test, Explicit("fast driving, run by name")]
        public void CompareAtHighSpeed()
        {
            var sb = new StringBuilder();
            foreach (double kmh in new[] { 30.0, 60.0, 100.0, 120.0 })
            {
                double v = kmh / 3.6;
                foreach (var sc in new (string Name, Func<List<vec2>> Path, double Start)[]
                {
                    ("Straight, 0.5 m off", () => Straight(5000), 0.5),
                    ("Into a 400 m curve", () => StraightArcStraight(60, 400, 60, 5000), 0),
                })
                {
                    sb.Append((kmh + " km/h " + sc.Name).PadRight(34));
                    foreach (Method m in new[] { Method.PurePursuit, Method.Mpc, Method.Lqr, Method.Hybrid })
                    {
                        Result r = Drive(m, sc.Path(), sc.Start, 30, v, 0.15, 30);
                        sb.Append(("  " + m + " " + r.RmsCm.ToString("0.0") + "/" + r.MaxCm.ToString("0") + "/" + r.BusyDegS.ToString("0.0")).PadRight(30));
                    }
                    sb.AppendLine();
                }
            }
            System.IO.File.WriteAllText(System.IO.Path.Combine(TestContext.CurrentContext.WorkDirectory, "high-speed.txt"), sb.ToString());
        }

        [Test, Explicit("MPC's time a step, run by name")]
        public void TimeTheMpc()
        {
            var sb = new StringBuilder();
            foreach (bool cold in new[] { true, false })
            {
                var w = new System.Diagnostics.Stopwatch();
                int solves = 0;
                double rms = 0;
                foreach (var sc in Scenarios())
                {
                    w.Start();
                    Result r = Drive(Method.Mpc, sc.Path(), sc.Start, sc.Seconds, 2.5, 0.3, 20, 1, 3e-5, false, null, cold);
                    w.Stop();
                    solves += (int)(sc.Seconds * 10);
                    rms += r.RmsCm;
                }
                sb.AppendLine((cold ? "whole range every time" : "from the last answer") + ": " + (w.Elapsed.TotalMilliseconds / solves).ToString("0.00") +
                    " ms a step (with the simulation), sum RMS " + rms.ToString("0.0") + " cm");
            }
            System.IO.File.WriteAllText(System.IO.Path.Combine(TestContext.CurrentContext.WorkDirectory, "mpc-timing.txt"), sb.ToString());
        }

        [Test, Explicit("a sweep of the LQR's weights, run by name")]
        public void SweepTheLqrWeights()
        {
            var sb = new StringBuilder();
            var gears = new (double Dead, double Rate)[] { (0.15, 30), (0.6, 12) };
            foreach (double wh in new[] { 0.5, 2.0, 8.0 })
                foreach (double ws in new[] { 3.0, 10.0, 30.0, 100.0 })
                {
                    double rms = 0, busy = 0, worst = 0;
                    int n = 0;
                    foreach (var g in gears)
                        foreach (double v in new[] { 2.5, 4.0 })
                            foreach (var sc in Scenarios())
                            {
                                var l = new CSteerLqr { WeightHeading = wh, WeightSteer = ws };
                                Result r = Drive(Method.Lqr, sc.Path(), sc.Start, sc.Seconds, v, g.Dead, g.Rate, 1, 3e-5, false, l);
                                rms += r.RmsCm;
                                busy += r.BusyDegS;
                                worst = Math.Max(worst, r.MaxCm);
                                n++;
                            }
                    sb.AppendLine("heading " + wh + ", steer " + ws + ": sum RMS " + rms.ToString("0.0") + " cm, worst " + worst.ToString("0") + " cm, steering " + (busy / n).ToString("0.0") + " °/s");
                }
            TestContext.Out.WriteLine(sb.ToString());
            System.IO.File.WriteAllText(System.IO.Path.Combine(TestContext.CurrentContext.WorkDirectory, "lqr-sweep.txt"), sb.ToString());
        }

        [Test, Explicit("a sweep of MPC's steering style, run by name")]
        public void SweepTheMpcStyle()
        {
            var sb = new StringBuilder();
            var gears = new (double Dead, double Rate)[] { (0.15, 30), (0.6, 12) };
            foreach (bool quick in new[] { false, true })
                for (int calm = 1; calm <= 10; calm++)
                {
                    double weight = 3e-5 * Math.Pow(2, (calm - 5) * 0.6);
                    double rms = 0, busy = 0, worst = 0;
                    int n = 0;
                    foreach (var g in gears)
                        foreach (double v in new[] { 2.5, 4.0 })
                            foreach (var sc in Scenarios())
                            {
                                Result r = Drive(Method.Mpc, sc.Path(), sc.Start, sc.Seconds, v, g.Dead, g.Rate, 1, weight, quick);
                                rms += r.RmsCm;
                                busy += r.BusyDegS;
                                worst = Math.Max(worst, r.MaxCm);
                                n++;
                            }
                    sb.AppendLine((quick ? "quick " : "fine  ") + "calm " + calm.ToString().PadLeft(2) + ": sum RMS " + rms.ToString("0.0") +
                        " cm, worst " + worst.ToString("0") + " cm, steering " + (busy / n).ToString("0.0") + " °/s");
                }
            TestContext.Out.WriteLine(sb.ToString());
            System.IO.File.WriteAllText(System.IO.Path.Combine(TestContext.CurrentContext.WorkDirectory, "mpc-sweep.txt"), sb.ToString());
        }

        [Test, Explicit("a comparison table, run by name")]
        public void CompareTheSteeringMethods()
        {
            var scenarios = new (string Name, Func<List<vec2>> Path, double Start, double Seconds)[]
            {
                ("Straight, on the line", () => Straight(400), 0, 60),
                ("Straight, 1 m off", () => Straight(400), 1.0, 30),
                ("Into a 25 m curve", () => StraightArcStraight(30, 25, 90, 300), 0, 45),
                ("Into a 12 m curve", () => StraightArcStraight(30, 12, 90, 300), 0, 40),
                ("S-curves 30 m", SCurve, 0, 70),
            };
            var gears = new (string Name, double Dead, double Rate)[] { ("quick steering 0.15 s, 30°/s", 0.15, 30), ("slow steering 0.6 s, 12°/s", 0.6, 12) };
            var speeds = new[] { 2.5, 4.0 };
            var methods = new[] { Method.Adaptive, Method.Mpc, Method.Lqr, Method.Hybrid };

            var sb = new StringBuilder();
            var total = new double[methods.Length];
            foreach (var g in gears)
                foreach (double v in speeds)
                {
                    sb.AppendLine();
                    sb.AppendLine("== " + g.Name + ", " + (v * 3.6).ToString("0") + " km/h ==   RMS cm / max cm / steering °/s");
                    foreach (var sc in scenarios)
                    {
                        sb.Append(sc.Name.PadRight(24));
                        for (int m = 0; m < methods.Length; m++)
                        {
                            Result r = Drive(methods[m], sc.Path(), sc.Start, sc.Seconds, v, g.Dead, g.Rate);
                            total[m] += r.RmsCm;
                            sb.Append(("  " + methods[m] + " " + r.RmsCm.ToString("0.0") + "/" + r.MaxCm.ToString("0") + "/" + r.BusyDegS.ToString("0.0")).PadRight(34));
                        }
                        sb.AppendLine();
                    }
                }
            sb.AppendLine();
            for (int m = 0; m < methods.Length; m++) sb.AppendLine(methods[m] + ": sum of RMS " + total[m].ToString("0.0") + " cm");
            TestContext.Out.WriteLine(sb.ToString());
            System.IO.File.WriteAllText(System.IO.Path.Combine(TestContext.CurrentContext.WorkDirectory, "steering-comparison.txt"), sb.ToString());
        }
    }
}
