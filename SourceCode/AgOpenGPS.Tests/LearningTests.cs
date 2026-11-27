using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.IO;

namespace AgOpenGPS.Tests
{
    public class LearningTests
    {
        [Test]
        public void ARepeatingErrorFadesOverAFewPasses()
        {
            // a slope pushes the tractor 10 cm right over one stretch, every pass the same way
            var f = new CFieldLearning();
            double heading = 0, lastError = 0;
            for (int pass = 0; pass < 8; pass++)
            {
                double sum = 0;
                int n = 0;
                for (double north = 0; north < 30; north += 0.25)   // 10 Hz at 9 km/h
                {
                    double shift = f.ShiftAt(1, north, heading);
                    double off = 0.10 - shift;                       // what the guidance, moved by the shift, leaves
                    f.Learn(1, north, heading, off);
                    sum += Math.Abs(off);
                    n++;
                }
                lastError = sum / n;
            }
            Assert.That(lastError, Is.LessThan(0.01), "still off after 8 passes");
            // the other way along the same place has learnt nothing
            Assert.That(f.ShiftAt(1, 10, Math.PI), Is.EqualTo(0));
        }

        [Test]
        public void WhatWasLearntIsKeptWithTheField()
        {
            var f = new CFieldLearning();
            for (int i = 0; i < 50; i++) f.Learn(100, 200, 0.3, 0.05);
            string path = Path.Combine(Path.GetTempPath(), "learn-" + Guid.NewGuid() + ".csv");
            try
            {
                f.Save(path);
                var g = new CFieldLearning();
                g.Load(path);
                Assert.That(g.Count, Is.EqualTo(1));
                Assert.That(g.ShiftAt(100.5, 200.5, 0.3), Is.EqualTo(f.ShiftAt(100, 200, 0.3)).Within(1e-4));
            }
            finally { File.Delete(path); }
        }

        [Test]
        public void TheTrailedImplementIsBroughtOntoTheLine()
        {
            // on a slope the implement runs 30 cm right of the tractor's track; the tractor moves left by the shift
            var k = new CImplementKeeper();
            double t = 0, implementOff = 0;
            for (; t < 40; t += 0.1)
            {
                implementOff = 0.30 - k.Shift(true);
                k.Update(t, true, implementOff);
            }
            Assert.That(Math.Abs(implementOff), Is.LessThan(0.01));
            Assert.That(k.Shift(false), Is.EqualTo(0), "the other way has its own");
        }

        [Test]
        public void ARecordingPlaysBackThroughTheEstimator()
        {
            // a recording of a tractor whose steer angle sensor reads 1.2 degrees short
            string folder = Path.Combine(Path.GetTempPath(), "rec-" + Guid.NewGuid());
            var rec = new CDriveRecorder();
            try
            {
                rec.Start(folder);
                var rnd = new Random(2);
                double e = 0, n = 0, h = 0.4, v = 2.5;
                int written = 0;
                for (double t = 0; t < 240; t += 0.1, written++)
                {
                    double steer = 4 * Math.Sin(0.15 * t) + 2 * Math.Sin(0.4 * t);
                    double hr = h;
                    h += v * Math.Tan(steer * Math.PI / 180) / 3.0 * 0.1;
                    e += v * Math.Sin(hr) * 0.1;
                    n += v * Math.Cos(hr) * 0.1;
                    rec.Write(new DriveRecord
                    {
                        T = t,
                        East = e + (rnd.NextDouble() - 0.5) * 0.02,
                        North = n + (rnd.NextDouble() - 0.5) * 0.02,
                        FixQuality = 4,
                        SpeedKmh = v * 3.6,
                        GpsHeadingDeg = h * 180 / Math.PI,
                        ImuHeadingDeg = double.NaN,
                        ImuRollDeg = double.NaN,
                        DualHeadingDeg = double.NaN,
                        WheelDeg = steer - 1.2,
                        AimDeg = steer - 1.2,
                        SentDeg = steer - 1.2,
                        OffLineCm = double.NaN,
                        Steering = true,
                        Method = "Hybrid",
                        WheelbaseM = 3.0
                    });
                }
                rec.Stop();
                List<DriveRecord> back = CDriveRecorder.Read(rec.FilePath);
                Assert.That(back.Count, Is.EqualTo(written));
                CDriveReplay.Findings found = CDriveReplay.Run(back);
                Assert.That(found.Estimator.SteerOffsetDeg, Is.EqualTo(1.2).Within(0.3));
            }
            finally
            {
                rec.Stop();
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
        }
    }
}
