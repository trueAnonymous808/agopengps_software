using AgLibrary.Gnss;
using System;
using System.Collections.Generic;

namespace AgOpenGPS.Tdcp
{
    /// <summary>
    /// TDCP from the receiver's raw data to movement for the position loop: frames come in on the network thread
    /// (Feed), the movement is summed up, and the position loop takes the sum once per fix (Take). Thread-safe.
    /// Takes RTCM 3 (MSM and ephemerides: most receivers) and u-blox UBX (RAWX and SFRBX); ephemerides may also come
    /// from the correction stream.
    /// </summary>
    public sealed class TdcpEngine
    {
        private readonly object sync = new object();
        private readonly RtcmDecoder decoder = new RtcmDecoder();
        private readonly UbxDecoder ubx = new UbxDecoder();
        private readonly TdcpSolver solver = new TdcpSolver();
        private readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        private readonly HashSet<long> fromCorrections = new HashSet<long>();

        private double sumE, sumN;
        private int sumEpochs;
        private bool sumBroken, feedingCorrections;
        private double lastFrameS = -1e9, lastEpochS = -1e9;

        public int Epochs { get; private set; }
        public int GoodEpochs { get; private set; }
        public TdcpResult Last { get; private set; }

        /// <summary>The receiver's raw data format as seen last: "RTCM 3", "u-blox UBX" or "".</summary>
        public string Format { get; private set; } = "";

        public TdcpEngine()
        {
            decoder.EphemerisReady += OnEphemeris;
            ubx.EphemerisReady += OnEphemeris;
            decoder.EpochReady += OnEpoch;
            ubx.EpochReady += OnEpoch;
        }

        /// <summary>Frames from the receiver arrive (seconds since the last one); 1e9 when none ever did.</summary>
        public double SinceFrameS { get { lock (sync) return clock.Elapsed.TotalSeconds - lastFrameS; } }

        /// <summary>Observation epochs arrive (the receiver sends its carrier phase); seconds since the last one.</summary>
        public double SinceEpochS { get { lock (sync) return clock.Elapsed.TotalSeconds - lastEpochS; } }

        public int EphemerisCount { get { lock (sync) return solver.EphemerisCount; } }

        /// <summary>Satellites whose ephemeris came from the correction stream (not from the receiver).</summary>
        public int EphemerisFromCorrections { get { lock (sync) return fromCorrections.Count; } }

        /// <summary>Raw data comes in and epochs are solved: what the window shows as "TDCP data alive".</summary>
        public bool Alive => SinceEpochS < 2;

        /// <summary>
        /// One loopback datagram: a tag (rover / corrections) and a frame, or an untagged frame (the rover's).
        /// </summary>
        public void Feed(byte[] data)
        {
            if (data == null || data.Length < 2) return;
            byte[] frame = data;
            bool corrections = false;
            if (data[0] == Rtcm3Frames.TagRover || data[0] == Rtcm3Frames.TagCorrections)
            {
                corrections = data[0] == Rtcm3Frames.TagCorrections;
                frame = new byte[data.Length - 1];
                Array.Copy(data, 1, frame, 0, frame.Length);
            }
            lock (sync)
            {
                try
                {
                    feedingCorrections = corrections;
                    if (frame[0] == Rtcm3Frames.Preamble)
                    {
                        decoder.Decode(frame, corrections);
                        if (!corrections) Format = "RTCM 3";
                    }
                    else if (frame[0] == Rtcm3Frames.UbxSync1 && !corrections)
                    {
                        ubx.Decode(frame);
                        Format = "u-blox UBX";
                    }
                    else return;
                    if (!corrections) lastFrameS = clock.Elapsed.TotalSeconds;
                }
                catch (Exception) { /* a broken frame is dropped */ }
                finally { feedingCorrections = false; }
            }
        }

        private void OnEphemeris(Ephemeris e)
        {
            solver.SetEphemeris(e);
            long key = ((long)e.System << 8) | (uint)e.Prn;
            if (feedingCorrections) fromCorrections.Add(key);
            else fromCorrections.Remove(key);
        }

        private void OnEpoch(ObsEpoch epoch)
        {
            lastEpochS = clock.Elapsed.TotalSeconds;
            TdcpResult r = solver.Process(epoch);
            Last = r;
            Epochs++;
            if (r.Ok)
            {
                GoodEpochs++;
                sumE += r.East;
                sumN += r.North;
                sumEpochs++;
            }
            else if (r.Reason != "first epoch" && r.Reason != "no position")
            {
                sumBroken = true;   // a gap in the chain: this stretch cannot be trusted
            }
        }

        /// <summary>The receiver's position to a few metres, for the directions to the satellites.</summary>
        public void SetPosition(double latDeg, double lonDeg, double heightM)
        {
            lock (sync) solver.SetPosition(latDeg, lonDeg, heightM);
        }

        /// <summary>
        /// The movement summed since the last call: valid when at least one epoch came and none failed in between.
        /// </summary>
        public void Take(out double east, out double north, out bool valid)
        {
            lock (sync)
            {
                east = sumE;
                north = sumN;
                valid = sumEpochs > 0 && !sumBroken;
                sumE = sumN = 0;
                sumEpochs = 0;
                sumBroken = false;
            }
        }
    }
}
