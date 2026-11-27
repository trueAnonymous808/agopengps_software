using AgOpenGPS.Core.Models;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;

namespace AgOpenGPS.Tests
{
    /// <summary>
    /// What each calculation costs per fix: an hour of driving (36000 fixes at 10 Hz) through every learner and
    /// estimator, the average and the worst single call, and the memory it allocates. Run by name; the table goes to
    /// fix-cost.txt in the test folder.
    /// </summary>
    public class FixCostTests
    {
        private sealed class Cost
        {
            public string Name;
            public double TotalMs, WorstMs;
            public long Calls, Bytes;
        }

        private readonly Dictionary<string, Cost> costs = new Dictionary<string, Cost>();
        private readonly Stopwatch w = new Stopwatch();

        private void Time(string name, Action a)
        {
            if (!costs.TryGetValue(name, out Cost c)) costs[name] = c = new Cost { Name = name };
            long before = AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize;
            w.Restart();
            a();
            w.Stop();
            c.Bytes += AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize - before;
            double ms = w.Elapsed.TotalMilliseconds;
            c.TotalMs += ms;
            c.WorstMs = Math.Max(c.WorstMs, ms);
            c.Calls++;
        }

        [Test, Explicit("the cost of every calculation per fix, run by name")]
        public void MeasureEveryCalculation()
        {
            AppDomain.MonitoringIsEnabled = true;
            var rnd = new Random(1);
            double Noise(double s) => (rnd.NextDouble() - 0.5) * 2 * s;

            var est = new CStateEstimator();
            var motor = new CMotorTune();
            var look = new CLookAheadTune();
            var side = new CSideHillTune();
            var dead = new CDeadZoneTune();
            var lookSpeed = new CLookAheadSpeedTune();
            var rollZero = new CRollZeroTune();
            var acquire = new CAcquireTune();
            var integral = new CIntegralTune();
            var uturn = new CUTurnTune();
            var fusion = new CFusionTune();
            var dyn = new CSteerDynamics();
            var change = new CChangeVerifier();
            var roll = new CRollTune();
            var pit = new CPitGuard();
            var passes = new CPassTracker();
            var field = new CFieldLearning();
            var start = new CStartDirection();
            var lqr = new CSteerLqr();
            var was = new List<double>();
            var wasBuf = new double[2001];
            int wasSince = 0;
            var path = new List<vec2>();
            for (int i = 0; i < 60; i++) path.Add(new vec2(0, i));

            double e = 0, n = 0, h = 0, v = 2.5;
            var sent = new List<double>(new double[3]);
            for (int k = 0; k < 36000; k++)
            {
                double t = k * 0.1;
                double steer = 3 * Math.Sin(t * 0.2);
                h += v * Math.Tan(steer * Math.PI / 180) / 3 * 0.1;
                e += v * Math.Sin(h) * 0.1;
                n += v * Math.Cos(h) * 0.1;
                double off = 0.03 * Math.Sin(t * 0.3) + Noise(0.01), rollDeg = 2 * Math.Sin(t * 0.05) + Noise(0.3);
                bool onLine = (k / 600) % 10 != 9;

                Time("Kalman (state estimator)", () => est.Step(new CStateEstimator.Inputs
                {
                    T = t,
                    East = e + Noise(0.01),
                    North = n + Noise(0.01),
                    PositionSigmaM = 0.02,
                    SpeedMs = v,
                    CourseRad = h + Noise(0.01),
                    ImuHeadingRad = h + 0.3,
                    DualHeadingRad = double.NaN,
                    SteerRad = steer * Math.PI / 180,
                    WheelbaseM = 3,
                    HasYawRate = true,
                    YawRateRadS = v * Math.Tan(steer * Math.PI / 180) / 3
                }));
                Time("Tuners (11 of them)", () =>
                {
                    motor.AddSample(t, steer, steer + Noise(0.2), 9, true);
                    dead.AddSample(t, steer, steer + Noise(0.2), false, 9, true);
                    look.AddSample(e, n, off, onLine);
                    lookSpeed.AddSample(e, n, off, onLine, 9);
                    side.AddSample(e, n, rollDeg, steer, off, onLine);
                    rollZero.AddSample(e, n, h, rollDeg, onLine);
                    integral.AddSample(e, n, off, onLine);
                    acquire.AddSample(e, n, off, onLine);
                    uturn.AddSample(e, n, off, false, onLine);
                    fusion.AddSample(h, h + Noise(0.002), onLine);
                    change.Feed(t, off, 9, false, onLine);
                });
                Time("Steering gear (sample)", () => dyn.AddSample(t, steer, steer + Noise(0.2), true));
                if (k % 100 == 0) Time("Steering gear (analysis, every 10 s)", () => dyn.Analyze());
                Time("Roll tune + pit mode (sample)", () =>
                {
                    roll.AddSample(t, e, n, h, rollDeg, 0.01, 9, true);
                    pit.Update(t, rollDeg, 0.01, true, 4, double.NaN);
                    pit.FeedXte(t, off, onLine);
                });
                if (k % 150 == 0) Time("Roll tune (analysis, every 15 s)", () => roll.Analyze());
                Time("Passes", () => passes.Feed(t, onLine, "1+", "Pure pursuit", e, n, off, onLine));
                Time("Field learning", () =>
                {
                    field.ShiftAt(e, n, h);
                    field.Learn(e, n, h, off);
                });
                Time("Start direction", () => start.Feed(t, v * 3.6, 0.1, -0.5, 9.8, true, false));
                Time("Smart WAS (sorted in a kept buffer, every tenth sample)", () =>
                {
                    was.Add(steer);
                    if (was.Count > 2000) was.RemoveAt(0);
                    if (was.Count >= 200 && ++wasSince >= 10)
                    {
                        wasSince = 0;
                        was.CopyTo(wasBuf, 0);
                        Array.Sort(wasBuf, 0, was.Count);
                        GC.KeepAlive(wasBuf[was.Count / 2]);
                    }
                });
                if (k < 6000)
                {
                    // the model steering (a few minutes is enough for its cost)
                    sent[0] = sent[1];
                    sent[1] = sent[2];
                    sent[2] = steer * Math.PI / 180;
                    Time("LQR", () => lqr.Steer(off, 0.01, sent, v, 3, 0.0, 0.6));
                }
            }

            var sb = new StringBuilder();
            sb.AppendLine("calculation | calls | average ms | worst ms | bytes per call");
            foreach (Cost c in costs.Values.OrderByDescending(c => c.TotalMs / c.Calls))
                sb.AppendLine(c.Name + " | " + c.Calls + " | " + (c.TotalMs / c.Calls).ToString("0.0000") + " | " + c.WorstMs.ToString("0.000") + " | " + (c.Bytes / c.Calls));
            System.IO.File.WriteAllText(System.IO.Path.Combine(TestContext.CurrentContext.WorkDirectory, "fix-cost.txt"), sb.ToString());
        }
    }
}
