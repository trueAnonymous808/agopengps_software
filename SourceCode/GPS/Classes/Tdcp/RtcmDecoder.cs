using System;
using System.Collections.Generic;

namespace AgOpenGPS.Tdcp
{
    public enum GnssSystem { Gps, Galileo, BeiDou, Glonass }

    /// <summary>One satellite's carrier phase in an epoch (one signal per satellite: L1 / E1 / B1).</summary>
    public struct PhaseObs
    {
        public GnssSystem System;
        public int Prn;
        public double PhaseRangeM;   // carrier phase as a range, metres
        public double LockMs;        // how long the signal has been tracked without a break
        public bool HalfCycle;       // half-cycle ambiguity flag
        public double Cn0;           // signal strength, dB-Hz
        public int Signal;           // which signal (a change breaks the phase chain like a slip)
        public int HalfState;        // receiver-specific half-cycle state (u-blox: resolved / subtracted)
    }

    /// <summary>All observations of one moment, GPS time of week (s).</summary>
    public sealed class ObsEpoch
    {
        public double GpsTowS;
        public readonly List<PhaseObs> Obs = new List<PhaseObs>();
    }

    /// <summary>Broadcast orbit and clock of one satellite (Keplerian; angles in radians).</summary>
    public sealed class Ephemeris
    {
        public GnssSystem System;
        public int Prn, Iode;
        public double Toe, Toc;               // seconds of week (system time)
        public double SqrtA, E, I0, Omega0, Omega, M0, DeltaN, OmegaDot, Idot;
        public double Cuc, Cus, Crc, Crs, Cic, Cis;
        public double Af0, Af1, Af2;
        public bool Healthy = true;
        public bool Geo;                      // BeiDou geostationary: its own orbit formula

        // GLONASS: position, velocity and luni-solar acceleration (PZ-90; m, m/s, m/s^2) at Toe, which is then
        // the GPS time of day (s); the clock is -TauN + GammaN * dt
        public double X, Y, Z, Vx, Vy, Vz, Ax, Ay, Az, TauN, GammaN;
        public int FreqChannel;
    }

    /// <summary>
    /// Decodes RTCM 3 frames: MSM4 to MSM7 observations of GPS (1074-1077), GLONASS (1084-1087), Galileo
    /// (1094-1097) and BeiDou (1124-1127), and the broadcast ephemerides 1019 (GPS), 1020 (GLONASS), 1045/1046
    /// (Galileo) and 1042 (BeiDou). Observations
    /// of the same moment from the different systems are gathered into one epoch.
    /// </summary>
    public sealed class RtcmDecoder
    {
        private const double LightMsM = 299792.458;          // metres light travels in a millisecond
        private const double Sc = Math.PI;                   // semicircles to radians
        private const double BdsMinusGpsS = -14;             // BeiDou time is 14 s behind GPS time

        private ObsEpoch pending;

        /// <summary>A complete epoch (all systems of one moment).</summary>
        public event Action<ObsEpoch> EpochReady;

        /// <summary>A new or updated ephemeris.</summary>
        public event Action<Ephemeris> EphemerisReady;

        public int Frames { get; private set; }
        public int MsmFrames { get; private set; }
        public int EphemerisFrames { get; private set; }

        /// <summary>
        /// One whole frame (0xD3 ... CRC). Bad or unknown frames are ignored. ephemerisOnly: a frame from the
        /// correction stream, whose observations are the base station's and must never be taken for the rover's.
        /// </summary>
        public void Decode(byte[] frame, bool ephemerisOnly = false)
        {
            if (frame == null || frame.Length < 8 || !AgLibrary.Gnss.Rtcm3Frames.CheckCrc(frame)) return;
            Frames++;
            int type = (int)Bits.U(frame, 24, 12);
            if (ephemerisOnly && !AgLibrary.Gnss.Rtcm3Frames.IsEphemeris(type)) return;
            switch (type)
            {
                case 1074: case 1075: case 1076: case 1077: DecodeMsm(frame, GnssSystem.Gps, type - 1070); break;
                case 1094: case 1095: case 1096: case 1097: DecodeMsm(frame, GnssSystem.Galileo, type - 1090); break;
                case 1124: case 1125: case 1126: case 1127: DecodeMsm(frame, GnssSystem.BeiDou, type - 1120); break;
                case 1084: case 1085: case 1086: case 1087: DecodeMsm(frame, GnssSystem.Glonass, type - 1080); break;
                case 1019: Emit(DecodeGpsEph(frame)); break;
                case 1020: Emit(DecodeGloEph(frame)); break;
                case 1045: case 1046: Emit(DecodeGalEph(frame)); break;
                case 1042: Emit(DecodeBdsEph(frame)); break;
            }
        }

        private void Emit(Ephemeris eph)
        {
            if (eph == null) return;
            EphemerisFrames++;
            EphemerisReady?.Invoke(eph);
        }

        // ---- MSM ----

        // the signals used per system (the RTCM signal mask numbers): L1 C/A, E1, B1I first
        private static readonly int[] GpsSignals = { 2, 32, 31, 30, 3, 4 };
        private static readonly int[] GalSignals = { 2, 5, 4, 3, 6 };
        private static readonly int[] BdsSignals = { 2, 4, 3, 30, 32, 31 };
        private static readonly int[] GloSignals = { 2, 3, 8, 9 };

        /// <summary>GPS minus UTC (leap seconds); unchanged since 2017, and leap seconds are to be abolished.</summary>
        public const double GpsUtcLeapS = 18;

        private double lastTowS = double.NaN;   // the last GPS / Galileo / BeiDou epoch, to place GLONASS ones

        private static double Mod(double a, double m) => a - m * Math.Floor(a / m);

        // the MSM epoch time to GPS time of week: GLONASS sends day of week and Moscow time of day
        private bool EpochTow(GnssSystem sys, long raw, out double towS)
        {
            if (sys != GnssSystem.Glonass)
            {
                towS = raw / 1000.0;
                if (sys == GnssSystem.BeiDou) towS -= BdsMinusGpsS;   // to GPS time
                towS = Mod(towS, 604800);
                lastTowS = towS;
                return true;
            }
            int dow = (int)(raw >> 27) & 7;
            double todS = (raw & ((1L << 27) - 1)) / 1000.0;
            if (dow < 7)
            {
                towS = Mod(dow * 86400 + todS - 10800 + GpsUtcLeapS, 604800);
                return true;
            }
            if (!double.IsNaN(lastTowS))
            {
                // day unknown: the moment of that time of day nearest to the other systems' epoch
                double todGps = Mod(todS - 10800 + GpsUtcLeapS, 86400);
                towS = Mod(lastTowS + GnssOrbit.DayWrap(todGps - Mod(lastTowS, 86400)), 604800);
                return true;
            }
            towS = 0;
            return false;
        }

        private void DecodeMsm(byte[] f, GnssSystem sys, int msm)
        {
            if (msm < 4 || msm > 7) return;
            MsmFrames++;
            int i = 24 + 12;
            i += 12;                                   // station id
            long tow = Bits.U(f, i, 30); i += 30;      // epoch time, ms
            bool more = Bits.U(f, i, 1) == 1; i += 1;
            i += 3 + 7 + 2 + 2 + 1 + 3;                // IODS, reserved, clock steering, external clock, smoothing
            var sats = new List<int>();
            for (int k = 1; k <= 64; k++, i++) if (Bits.U(f, i, 1) == 1) sats.Add(k);
            var sigs = new List<int>();
            for (int k = 1; k <= 32; k++, i++) if (Bits.U(f, i, 1) == 1) sigs.Add(k);
            int nSat = sats.Count, nSig = sigs.Count;
            if (nSat * nSig > 64) return;
            var cell = new bool[nSat, nSig];
            int nCell = 0;
            for (int s = 0; s < nSat; s++)
                for (int g = 0; g < nSig; g++, i++)
                    if (cell[s, g] = Bits.U(f, i, 1) == 1) nCell++;

            // satellite data
            var roughInt = new double[nSat];
            var roughMod = new double[nSat];
            for (int s = 0; s < nSat; s++, i += 8) roughInt[s] = Bits.U(f, i, 8);
            if (msm == 5 || msm == 7) i += 4 * nSat;   // extended satellite info
            for (int s = 0; s < nSat; s++, i += 10) roughMod[s] = Bits.U(f, i, 10) / 1024.0;
            if (msm == 5 || msm == 7) i += 14 * nSat;  // rough phase range rates

            // signal data
            bool ext = msm >= 6;
            int prBits = ext ? 20 : 15, cpBits = ext ? 24 : 22, lockBits = ext ? 10 : 4, cnBits = ext ? 10 : 6;
            double cpScale = ext ? Math.Pow(2, -31) : Math.Pow(2, -29);
            var phase = new double[nCell];
            var lockMs = new double[nCell];
            var half = new bool[nCell];
            var cn = new double[nCell];
            i += prBits * nCell;                       // fine pseudoranges (not used)
            long cpInvalid = -(1L << (cpBits - 1));
            for (int c = 0; c < nCell; c++, i += cpBits)
            {
                long v = Bits.S(f, i, cpBits);
                phase[c] = v == cpInvalid ? double.NaN : v * cpScale;
            }
            for (int c = 0; c < nCell; c++, i += lockBits) lockMs[c] = ext ? LockExt((int)Bits.U(f, i, lockBits)) : Lock4((int)Bits.U(f, i, lockBits));
            for (int c = 0; c < nCell; c++, i++) half[c] = Bits.U(f, i, 1) == 1;
            for (int c = 0; c < nCell; c++, i += cnBits) cn[c] = Bits.U(f, i, cnBits) * (ext ? 0.0625 : 1);
            if (i > (f.Length - 3) * 8) return;        // shorter than its masks say: broken

            if (!EpochTow(sys, tow, out double towS)) return;

            int[] wanted = sys == GnssSystem.Gps ? GpsSignals : sys == GnssSystem.Galileo ? GalSignals
                : sys == GnssSystem.BeiDou ? BdsSignals : GloSignals;
            var epoch = GetEpoch(towS);
            int cellIndex = 0;
            for (int s = 0; s < nSat; s++)
            {
                int best = -1, bestRank = int.MaxValue;
                int bestCell = -1;
                for (int g = 0; g < nSig; g++)
                {
                    if (!cell[s, g]) continue;
                    int rank = Array.IndexOf(wanted, sigs[g]);
                    if (rank >= 0 && rank < bestRank) { bestRank = rank; best = g; bestCell = cellIndex; }
                    cellIndex++;
                }
                if (best < 0 || roughInt[s] == 255 || double.IsNaN(phase[bestCell])) continue;
                double ms = roughInt[s] + roughMod[s] + phase[bestCell];
                epoch.Obs.Add(new PhaseObs
                {
                    System = sys,
                    Prn = sats[s],
                    PhaseRangeM = ms * LightMsM,
                    LockMs = lockMs[bestCell],
                    HalfCycle = half[bestCell],
                    Cn0 = cn[bestCell],
                    Signal = sigs[best],
                });
            }
            if (!more) Flush();
        }

        private ObsEpoch GetEpoch(double towS)
        {
            if (pending != null && Math.Abs(pending.GpsTowS - towS) > 1e-3) Flush();   // a new moment: the old one is done
            if (pending == null) pending = new ObsEpoch { GpsTowS = towS };
            return pending;
        }

        private void Flush()
        {
            ObsEpoch done = pending;
            pending = null;
            if (done != null && done.Obs.Count > 0) EpochReady?.Invoke(done);
        }

        // lock time indicator (DF402, 4 bit) to the minimum lock time in ms
        private static double Lock4(int i) => i == 0 ? 0 : 1 << (i + 4);

        // extended lock time indicator (DF407, 10 bit) to ms
        private static double LockExt(int i)
        {
            if (i < 64) return i;
            if (i < 96) return 2 * i - 64;
            if (i < 128) return 4 * i - 256;
            if (i < 160) return 8 * i - 768;
            if (i < 192) return 16 * i - 2048;
            if (i < 224) return 32 * i - 5120;
            if (i < 256) return 64 * i - 12288;
            if (i < 288) return 128 * i - 28672;
            if (i < 320) return 256 * i - 65536;
            if (i < 352) return 512 * i - 147456;
            if (i < 384) return 1024 * i - 327680;
            if (i < 416) return 2048 * i - 720896;
            if (i < 448) return 4096 * i - 1572864;
            if (i < 480) return 8192 * i - 3407872;
            if (i < 512) return 16384 * i - 7340032;
            if (i < 544) return 32768 * i - 15728640;
            if (i < 576) return 65536 * i - 33554432;
            if (i < 608) return 131072 * i - 71303168;
            if (i < 640) return 262144 * i - 150994944;
            if (i < 672) return 524288 * i - 318767104;
            if (i < 704) return 1048576 * i - 671088640;
            return 67108864;
        }

        // ---- ephemerides ----

        private static Ephemeris DecodeGpsEph(byte[] f)
        {
            int i = 24 + 12;
            var e = new Ephemeris { System = GnssSystem.Gps };
            e.Prn = (int)Bits.U(f, i, 6); i += 6;
            i += 10 + 4 + 2;                                         // week, URA, L2 code
            e.Idot = Bits.S(f, i, 14) * Math.Pow(2, -43) * Sc; i += 14;
            e.Iode = (int)Bits.U(f, i, 8); i += 8;
            e.Toc = Bits.U(f, i, 16) * 16.0; i += 16;
            e.Af2 = Bits.S(f, i, 8) * Math.Pow(2, -55); i += 8;
            e.Af1 = Bits.S(f, i, 16) * Math.Pow(2, -43); i += 16;
            e.Af0 = Bits.S(f, i, 22) * Math.Pow(2, -31); i += 22;
            i += 10;                                                 // IODC
            e.Crs = Bits.S(f, i, 16) * Math.Pow(2, -5); i += 16;
            e.DeltaN = Bits.S(f, i, 16) * Math.Pow(2, -43) * Sc; i += 16;
            e.M0 = Bits.S(f, i, 32) * Math.Pow(2, -31) * Sc; i += 32;
            e.Cuc = Bits.S(f, i, 16) * Math.Pow(2, -29); i += 16;
            e.E = Bits.U(f, i, 32) * Math.Pow(2, -33); i += 32;
            e.Cus = Bits.S(f, i, 16) * Math.Pow(2, -29); i += 16;
            e.SqrtA = Bits.U(f, i, 32) * Math.Pow(2, -19); i += 32;
            e.Toe = Bits.U(f, i, 16) * 16.0; i += 16;
            e.Cic = Bits.S(f, i, 16) * Math.Pow(2, -29); i += 16;
            e.Omega0 = Bits.S(f, i, 32) * Math.Pow(2, -31) * Sc; i += 32;
            e.Cis = Bits.S(f, i, 16) * Math.Pow(2, -29); i += 16;
            e.I0 = Bits.S(f, i, 32) * Math.Pow(2, -31) * Sc; i += 32;
            e.Crc = Bits.S(f, i, 16) * Math.Pow(2, -5); i += 16;
            e.Omega = Bits.S(f, i, 32) * Math.Pow(2, -31) * Sc; i += 32;
            e.OmegaDot = Bits.S(f, i, 24) * Math.Pow(2, -43) * Sc; i += 24;
            i += 8;                                                  // TGD
            e.Healthy = Bits.U(f, i, 6) == 0;
            return e.Prn >= 1 && e.SqrtA > 1000 ? e : null;
        }

        private static Ephemeris DecodeGalEph(byte[] f)
        {
            int i = 24 + 12;
            var e = new Ephemeris { System = GnssSystem.Galileo };
            e.Prn = (int)Bits.U(f, i, 6); i += 6;
            i += 12;                                                 // week
            e.Iode = (int)Bits.U(f, i, 10); i += 10;
            i += 8;                                                  // SISA
            e.Idot = Bits.S(f, i, 14) * Math.Pow(2, -43) * Sc; i += 14;
            e.Toc = Bits.U(f, i, 14) * 60.0; i += 14;
            e.Af2 = Bits.S(f, i, 6) * Math.Pow(2, -59); i += 6;
            e.Af1 = Bits.S(f, i, 21) * Math.Pow(2, -46); i += 21;
            e.Af0 = Bits.S(f, i, 31) * Math.Pow(2, -34); i += 31;
            e.Crs = Bits.S(f, i, 16) * Math.Pow(2, -5); i += 16;
            e.DeltaN = Bits.S(f, i, 16) * Math.Pow(2, -43) * Sc; i += 16;
            e.M0 = Bits.S(f, i, 32) * Math.Pow(2, -31) * Sc; i += 32;
            e.Cuc = Bits.S(f, i, 16) * Math.Pow(2, -29); i += 16;
            e.E = Bits.U(f, i, 32) * Math.Pow(2, -33); i += 32;
            e.Cus = Bits.S(f, i, 16) * Math.Pow(2, -29); i += 16;
            e.SqrtA = Bits.U(f, i, 32) * Math.Pow(2, -19); i += 32;
            e.Toe = Bits.U(f, i, 14) * 60.0; i += 14;
            e.Cic = Bits.S(f, i, 16) * Math.Pow(2, -29); i += 16;
            e.Omega0 = Bits.S(f, i, 32) * Math.Pow(2, -31) * Sc; i += 32;
            e.Cis = Bits.S(f, i, 16) * Math.Pow(2, -29); i += 16;
            e.I0 = Bits.S(f, i, 32) * Math.Pow(2, -31) * Sc; i += 32;
            e.Crc = Bits.S(f, i, 16) * Math.Pow(2, -5); i += 16;
            e.Omega = Bits.S(f, i, 32) * Math.Pow(2, -31) * Sc; i += 32;
            e.OmegaDot = Bits.S(f, i, 24) * Math.Pow(2, -43) * Sc;
            return e.Prn >= 1 && e.SqrtA > 1000 ? e : null;
        }

        private static Ephemeris DecodeBdsEph(byte[] f)
        {
            int i = 24 + 12;
            var e = new Ephemeris { System = GnssSystem.BeiDou };
            e.Prn = (int)Bits.U(f, i, 6); i += 6;
            i += 13 + 4;                                             // week, URAI
            e.Idot = Bits.S(f, i, 14) * Math.Pow(2, -43) * Sc; i += 14;
            e.Iode = (int)Bits.U(f, i, 5); i += 5;
            e.Toc = Bits.U(f, i, 17) * 8.0; i += 17;
            e.Af2 = Bits.S(f, i, 11) * Math.Pow(2, -66); i += 11;
            e.Af1 = Bits.S(f, i, 22) * Math.Pow(2, -50); i += 22;
            e.Af0 = Bits.S(f, i, 24) * Math.Pow(2, -33); i += 24;
            i += 5;                                                  // AODC
            e.Crs = Bits.S(f, i, 18) * Math.Pow(2, -6); i += 18;
            e.DeltaN = Bits.S(f, i, 16) * Math.Pow(2, -43) * Sc; i += 16;
            e.M0 = Bits.S(f, i, 32) * Math.Pow(2, -31) * Sc; i += 32;
            e.Cuc = Bits.S(f, i, 18) * Math.Pow(2, -31); i += 18;
            e.E = Bits.U(f, i, 32) * Math.Pow(2, -33); i += 32;
            e.Cus = Bits.S(f, i, 18) * Math.Pow(2, -31); i += 18;
            e.SqrtA = Bits.U(f, i, 32) * Math.Pow(2, -19); i += 32;
            e.Toe = Bits.U(f, i, 17) * 8.0; i += 17;
            e.Cic = Bits.S(f, i, 18) * Math.Pow(2, -31); i += 18;
            e.Omega0 = Bits.S(f, i, 32) * Math.Pow(2, -31) * Sc; i += 32;
            e.Cis = Bits.S(f, i, 18) * Math.Pow(2, -31); i += 18;
            e.I0 = Bits.S(f, i, 32) * Math.Pow(2, -31) * Sc; i += 32;
            e.Crc = Bits.S(f, i, 18) * Math.Pow(2, -6); i += 18;
            e.Omega = Bits.S(f, i, 32) * Math.Pow(2, -31) * Sc; i += 32;
            e.OmegaDot = Bits.S(f, i, 24) * Math.Pow(2, -43) * Sc; i += 24;
            i += 10 + 10;                                            // TGD1, TGD2
            e.Healthy = Bits.U(f, i, 1) == 0;
            // the geostationary ones (C01-C05, C59-C63) have their own orbit formula
            e.Geo = e.Prn <= 5 || e.Prn >= 59;
            return e.Prn >= 1 && e.SqrtA > 1000 ? e : null;
        }

        // GLONASS 1020: a state vector every half hour (sign-magnitude fields, km units)
        private static Ephemeris DecodeGloEph(byte[] f)
        {
            int i = 24 + 12;
            var e = new Ephemeris { System = GnssSystem.Glonass };
            e.Prn = (int)Bits.U(f, i, 6); i += 6;
            e.FreqChannel = (int)Bits.U(f, i, 5) - 7; i += 5;
            i += 1 + 1 + 2 + 12;                                     // almanac health, its availability, P1, tk
            bool bad = Bits.U(f, i, 1) == 1; i += 1;                  // Bn: the satellite is not healthy
            i += 1;                                                  // P2
            double tb = Bits.U(f, i, 7) * 900.0; i += 7;              // Moscow time of day
            double v = Math.Pow(2, -20) * 1000, p = Math.Pow(2, -11) * 1000, a = Math.Pow(2, -30) * 1000;
            e.Vx = Bits.SM(f, i, 24) * v; i += 24;
            e.X = Bits.SM(f, i, 27) * p; i += 27;
            e.Ax = Bits.SM(f, i, 5) * a; i += 5;
            e.Vy = Bits.SM(f, i, 24) * v; i += 24;
            e.Y = Bits.SM(f, i, 27) * p; i += 27;
            e.Ay = Bits.SM(f, i, 5) * a; i += 5;
            e.Vz = Bits.SM(f, i, 24) * v; i += 24;
            e.Z = Bits.SM(f, i, 27) * p; i += 27;
            e.Az = Bits.SM(f, i, 5) * a; i += 5;
            i += 1;                                                  // P3
            e.GammaN = Bits.SM(f, i, 11) * Math.Pow(2, -40); i += 11;
            i += 2 + 1;                                              // P, ln
            e.TauN = Bits.SM(f, i, 22) * Math.Pow(2, -30);
            e.Toe = e.Toc = Mod(tb - 10800 + GpsUtcLeapS, 86400);
            e.Iode = (int)(tb / 900);
            e.Healthy = !bad;
            double r = Math.Sqrt(e.X * e.X + e.Y * e.Y + e.Z * e.Z);
            return e.Prn >= 1 && e.Prn <= 24 && r > 2.4e7 && r < 2.7e7 ? e : null;
        }
    }

    /// <summary>Big-endian bit fields of a frame.</summary>
    public static class Bits
    {
        public static long U(byte[] b, int pos, int len)
        {
            long v = 0;
            for (int i = pos; i < pos + len; i++)
            {
                int byteIndex = i >> 3;
                int bit = byteIndex < b.Length ? (b[byteIndex] >> (7 - (i & 7))) & 1 : 0;
                v = (v << 1) | (long)bit;
            }
            return v;
        }

        public static long S(byte[] b, int pos, int len)
        {
            long v = U(b, pos, len);
            if (len < 64 && (v & (1L << (len - 1))) != 0) v -= 1L << len;   // two's complement
            return v;
        }

        /// <summary>Sign-magnitude (GLONASS fields): the top bit is the sign.</summary>
        public static long SM(byte[] b, int pos, int len)
        {
            long v = U(b, pos, len);
            long mag = v & ((1L << (len - 1)) - 1);
            return (v >> (len - 1)) == 1 ? -mag : mag;
        }

        public static void Put(byte[] b, int pos, int len, long value)
        {
            for (int i = 0; i < len; i++)
            {
                int bit = (int)((value >> (len - 1 - i)) & 1);
                int p = pos + i;
                if (bit == 1) b[p >> 3] |= (byte)(1 << (7 - (p & 7)));
                else b[p >> 3] &= (byte)~(1 << (7 - (p & 7)));
            }
        }
    }
}
