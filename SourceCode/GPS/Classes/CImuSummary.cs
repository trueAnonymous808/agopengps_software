using System;

namespace AgOpenGPS
{
    /// <summary>
    /// What the IMU did between two fixes, from the modified module firmwares (PGN 0xB2, sent just before each
    /// PANDA): the PANDA carries one IMU reading per fix, the module reads it 50-100 times a second. The turning rate
    /// over the interval, the roll on average and its peaks, the fastest roll change (a pothole). Older firmwares do
    /// not send it: everything then goes on with the PANDA alone.
    /// </summary>
    public sealed class CImuSummary
    {
        public const int Pgn = 0xB2;
        public const double FreshS = 0.35;   // older than this (a fix or more missed): not used

        public double ReceivedT { get; private set; } = double.NaN;
        public double YawRateDegS { get; private set; }      // heading increasing = +
        public double RollAvgDeg { get; private set; }       // the roll as the module reads it (no invert, no zero)
        public double RollMinDeg { get; private set; }
        public double RollMaxDeg { get; private set; }
        public double RollLastDeg { get; private set; }
        public double RollRatePeakDegS { get; private set; } // the fastest roll change, signed
        public double HeadingDeg { get; private set; }
        public double SpanS { get; private set; }
        public int Samples { get; private set; }
        public int ImuType { get; private set; }             // 1 BNO085 serial (RVC), 2 BNO085 I2C
        public long Received { get; private set; }
        public double ModuleUptimeMs { get; private set; } = -1;   // since the module was switched on; -1: not sent (older firmware)
        public double AccX { get; private set; } = double.NaN;    // acceleration along the IMU's axes, m/s2 (gravity in it); NaN: none
        public double AccY { get; private set; } = double.NaN;
        public double AccZ { get; private set; } = double.NaN;
        public bool HasAccel => !double.IsNaN(AccX);

        /// <summary>IMU readings a second the module took.</summary>
        public double RateHz => SpanS > 0.01 ? Samples / SpanS : 0;

        /// <summary>A summary for the fix now (taken in, and of enough readings to say more than the PANDA).</summary>
        public bool Fresh(double now) => !double.IsNaN(ReceivedT) && now - ReceivedT >= 0 && now - ReceivedT < FreshS && Samples >= 3;

        /// <summary>Has a module ever sent one (the firmware knows it).</summary>
        public bool Seen => Received > 0;

        /// <summary>The PGN as it came (header, length, payload, crc - the crc checked already).</summary>
        public bool Parse(byte[] data, double now)
        {
            if (data == null || data.Length < 24 || data[3] != Pgn || data[4] < 18) return false;
            double I16(int i, double scale) => BitConverter.ToInt16(data, i) * scale;
            double yawRate = I16(5, 0.01), heading = I16(17, 0.1);
            if (Math.Abs(yawRate) > 300 || heading < -1 || heading > 361) return false;
            YawRateDegS = yawRate;
            RollAvgDeg = I16(7, 0.01);
            RollMinDeg = I16(9, 0.01);
            RollMaxDeg = I16(11, 0.01);
            RollLastDeg = I16(13, 0.01);
            RollRatePeakDegS = I16(15, 0.1);
            HeadingDeg = heading;
            SpanS = BitConverter.ToUInt16(data, 19) * 0.001;
            Samples = data[21];
            ImuType = data[22];
            ModuleUptimeMs = data[4] >= 22 && data.Length >= 28 ? (double)BitConverter.ToUInt32(data, 23) : -1;
            bool acc = data[4] >= 28 && data.Length >= 34 && BitConverter.ToInt16(data, 27) != short.MaxValue;
            AccX = acc ? I16(27, 0.01) : double.NaN;
            AccY = acc ? I16(29, 0.01) : double.NaN;
            AccZ = acc ? I16(31, 0.01) : double.NaN;
            ReceivedT = now;
            Received++;
            return true;
        }

        /// <summary>A module roll as AgOpenGPS uses it: inverted if set so, the zero taken off.</summary>
        public static double AsRoll(double moduleRollDeg, bool invert, double zeroDeg) => (invert ? -moduleRollDeg : moduleRollDeg) - zeroDeg;

        /// <summary>The PGN a module sends (for the tests and the simulator).</summary>
        public static byte[] Build(double yawRateDegS, double rollAvg, double rollMin, double rollMax, double rollLast, double rollRatePeak,
            double headingDeg, double spanS, int samples, int imuType, long uptimeMs = -1, double[] acc = null)
        {
            var p = new byte[acc != null ? 34 : uptimeMs >= 0 ? 28 : 24];
            p[0] = 0x80;
            p[1] = 0x81;
            p[2] = 0x7F;
            p[3] = Pgn;
            p[4] = (byte)(acc != null ? 28 : uptimeMs >= 0 ? 22 : 18);
            int i = 5;
            void Put(double v)
            {
                short s = (short)Math.Max(-32767, Math.Min(32767, Math.Round(v)));
                p[i++] = (byte)s;
                p[i++] = (byte)((ushort)s >> 8);
            }
            Put(yawRateDegS * 100);
            Put(rollAvg * 100);
            Put(rollMin * 100);
            Put(rollMax * 100);
            Put(rollLast * 100);
            Put(rollRatePeak * 10);
            Put(headingDeg * 10);
            ushort span = (ushort)Math.Max(0, Math.Min(65000, Math.Round(spanS * 1000)));
            p[i++] = (byte)span;
            p[i++] = (byte)(span >> 8);
            p[i++] = (byte)Math.Max(0, Math.Min(255, samples));
            p[i++] = (byte)imuType;
            if (uptimeMs >= 0 || acc != null)
            {
                BitConverter.GetBytes((uint)Math.Max(0, uptimeMs)).CopyTo(p, i);
                i += 4;
            }
            if (acc != null)
            {
                Put(acc[0] * 100);
                Put(acc[1] * 100);
                Put(acc[2] * 100);
            }
            byte crc = 0;
            for (int j = 2; j < i; j++) crc += p[j];
            p[i] = crc;
            return p;
        }
    }
}
