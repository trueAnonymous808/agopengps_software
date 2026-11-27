using NUnit.Framework;
using System;

namespace AgOpenGPS.Tests
{
    public class StateEstimatorTests
    {
        private const double L = 3.0;

        // a tractor weaving gently along: the sensor reads the steer angle off by the offset, it slides sideways by
        // the slip, the IMU heading is off by its offset; GNSS with 1 cm noise, 10 Hz
        private static CStateEstimator Drive(double steerOffsetDeg, double slipMs, double imuOffsetDeg, bool dual, double seconds = 240, int seed = 3, double imuNoiseRad = 0.003, bool gyro = false, bool steerSensor = true)
        {
            var rnd = new Random(seed);
            double Gauss() => Math.Sqrt(-2 * Math.Log(1 - rnd.NextDouble())) * Math.Cos(2 * Math.PI * rnd.NextDouble());
            var f = new CStateEstimator();
            double e = 0, n = 0, h = 0.3, v = 2.5;
            const double dt = 0.1;
            for (double t = 0; t < seconds; t += dt)
            {
                double trueSteer = 4 * Math.Sin(0.15 * t) + 2 * Math.Sin(0.4 * t);   // degrees
                double hr = h;
                double hBefore = h;
                h += v * Math.Tan(trueSteer * Math.PI / 180) / L * dt;
                e += (v * Math.Sin(hr) + slipMs * Math.Cos(hr)) * dt;
                n += (v * Math.Cos(hr) - slipMs * Math.Sin(hr)) * dt;
                double course = h + Math.Atan2(slipMs, v);
                f.Step(new CStateEstimator.Inputs
                {
                    T = t,
                    East = e + Gauss() * 0.01,
                    North = n + Gauss() * 0.01,
                    PositionSigmaM = 0.02,
                    SpeedMs = v + Gauss() * 0.05,
                    CourseRad = dual ? double.NaN : course + Gauss() * 0.01,
                    ImuHeadingRad = CStateEstimator.Wrap(h + imuOffsetDeg * Math.PI / 180 + Gauss() * imuNoiseRad),
                    DualHeadingRad = dual ? CStateEstimator.Wrap(h + Gauss() * 0.004) : double.NaN,
                    SteerRad = steerSensor ? (trueSteer - steerOffsetDeg) * Math.PI / 180 : double.NaN,
                    WheelbaseM = L,
                    // the module's IMU summary: the turning over the interval, with the gyro's noise
                    HasYawRate = gyro,
                    YawRateRadS = (h - hBefore) / dt + Gauss() * 0.2 * Math.PI / 180
                });
            }
            return f;
        }

        [Test]
        public void ItFindsTheSteerAngleSensorsOffset()
        {
            CStateEstimator f = Drive(1.5, 0, 25, false);
            Assert.That(f.Ready, Is.True);
            Assert.That(f.SteerOffsetDeg, Is.EqualTo(1.5).Within(0.3));
            Assert.That(CStateEstimator.Wrap(f.ImuOffsetDeg * Math.PI / 180) * 180 / Math.PI, Is.EqualTo(25).Within(1));
        }

        [Test]
        public void WithADualAntennaItFindsTheSlip()
        {
            CStateEstimator f = Drive(-1.0, 0.06, -40, true);
            Assert.That(f.SlipMs, Is.EqualTo(0.06).Within(0.02));
            Assert.That(f.SteerOffsetDeg, Is.EqualTo(-1.0).Within(0.3));
        }

        [Test]
        public void ItFollowsThePositionClosely()
        {
            CStateEstimator f = Drive(0, 0, 0, false);
            Assert.That(f.PositionInnovationCm, Is.LessThan(3));
        }

        [Test]
        public void ItMeasuresHowNoisyTheImuIs()
        {
            // a quiet IMU (TM171-like, 0.1 degree) and a noisier one (BNO085-like, 0.6 degree)
            CStateEstimator quiet = Drive(0, 0, 10, false, 300, 5, 0.1 * Math.PI / 180);
            CStateEstimator noisy = Drive(0, 0, 10, false, 300, 5, 0.6 * Math.PI / 180);
            Assert.That(quiet.ImuNoiseDeg, Is.LessThan(0.2));
            Assert.That(noisy.ImuNoiseDeg, Is.GreaterThan(0.4).And.LessThan(0.9));
        }

        [Test]
        public void TheGyroFindsTheSteerOffsetSooner()
        {
            // a minute and a half only: with the turning rate the offset is there, without it still far less sure
            CStateEstimator without = Drive(1.5, 0, 25, false, 90, 7);
            CStateEstimator with = Drive(1.5, 0, 25, false, 90, 7, gyro: true);
            Assert.That(with.SteerOffsetDeg, Is.EqualTo(1.5).Within(0.2));
            Assert.That(with.Sigma(CStateEstimator.SteerOff), Is.LessThan(without.Sigma(CStateEstimator.SteerOff) * 0.7));
        }

        [Test]
        public void WithoutASteerSensorTheGyroCarriesTheHeading()
        {
            CStateEstimator without = Drive(0, 0, 10, false, 120, 9, steerSensor: false);
            CStateEstimator with = Drive(0, 0, 10, false, 120, 9, gyro: true, steerSensor: false);
            Assert.That(with.Sigma(CStateEstimator.H), Is.LessThan(without.Sigma(CStateEstimator.H)));
            Assert.That(with.PositionInnovationCm, Is.LessThan(3));
        }

        [Test]
        public void AJumpOfThePositionStartsItAgain()
        {
            var f = new CStateEstimator();
            var m = new CStateEstimator.Inputs { T = 0, East = 0, North = 0, PositionSigmaM = 0.02, SpeedMs = 2, CourseRad = 0, ImuHeadingRad = double.NaN, DualHeadingRad = double.NaN, SteerRad = 0, WheelbaseM = L };
            f.Step(m);
            m.T = 0.1;
            m.North = 0.2;
            f.Step(m);
            Assert.That(f.Started, Is.True);
            m.T = 0.2;
            m.East = 500;   // a new field origin
            f.Step(m);
            Assert.That(f.Started, Is.False);
        }
    }
}
