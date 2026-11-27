using AgLibrary.Gnss;
using System;
using System.Collections.Generic;

namespace AgOpenGPS.Tdcp
{
    /// <summary>
    /// Decodes u-blox UBX frames (F9P, F9R, M8T, X20P and others that do not send RTCM ephemerides): RXM-RAWX
    /// carrier phase observations and RXM-SFRBX raw navigation data, from which the broadcast ephemerides are built
    /// (GPS L1 C/A LNAV, Galileo I/NAV, BeiDou D1, GLONASS L1OF strings). The field positions follow the
    /// interface documents as RTKLIB reads them from u-blox receivers.
    /// </summary>
    public sealed class UbxDecoder
    {
        private const double Sc = Math.PI;

        public event Action<ObsEpoch> EpochReady;
        public event Action<Ephemeris> EphemerisReady;

        public int RawxFrames { get; private set; }
        public int SfrbxFrames { get; private set; }

        private readonly Dictionary<int, byte[][]> gpsSub = new Dictionary<int, byte[][]>();
        private readonly Dictionary<int, byte[][]> bdsSub = new Dictionary<int, byte[][]>();
        private readonly Dictionary<int, byte[][]> galWord = new Dictionary<int, byte[][]>();
        private readonly Dictionary<int, GloFrame> gloStr = new Dictionary<int, GloFrame>();
        private readonly Dictionary<long, long> emitted = new Dictionary<long, long>();   // the last ephemeris sent per satellite

        private sealed class GloFrame
        {
            public readonly byte[] Strings = new byte[40];
            public int Mask;
        }

        public void Decode(byte[] f)
        {
            if (!Rtcm3Frames.CheckUbx(f)) return;
            int len = f[4] | (f[5] << 8);
            if (f[2] != 0x02) return;
            if (f[3] == 0x15) DecodeRawx(f, 6, len);
            else if (f[3] == 0x13) DecodeSfrbx(f, 6, len);
        }

        private static int U2(byte[] b, int o) => b[o] | (b[o + 1] << 8);
        private static uint U4(byte[] b, int o) => (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));

        // ---- RXM-RAWX: one measurement per signal; per satellite the L1-band one is taken ----

        private static bool MapSystem(int gnssId, out GnssSystem sys)
        {
            switch (gnssId)
            {
                case 0: sys = GnssSystem.Gps; return true;
                case 2: sys = GnssSystem.Galileo; return true;
                case 3: sys = GnssSystem.BeiDou; return true;
                case 6: sys = GnssSystem.Glonass; return true;
                default: sys = GnssSystem.Gps; return false;   // SBAS, QZSS, NavIC: not used
            }
        }

        /// <summary>The carrier frequency (Hz) of a signal and its preference (lower is better); 0 when unknown.</summary>
        public static double Frequency(GnssSystem sys, int sigId, int freqId, out int rank)
        {
            rank = 99;
            switch (sys)
            {
                case GnssSystem.Gps:
                    if (sigId == 0) { rank = 0; return 1575.42e6; }
                    if (sigId == 3 || sigId == 4) { rank = 1; return 1227.60e6; }
                    if (sigId == 6 || sigId == 7) { rank = 2; return 1176.45e6; }
                    break;
                case GnssSystem.Galileo:
                    if (sigId == 0 || sigId == 1) { rank = 0; return 1575.42e6; }
                    if (sigId == 3 || sigId == 4) { rank = 1; return 1176.45e6; }
                    if (sigId == 5 || sigId == 6) { rank = 2; return 1207.14e6; }
                    break;
                case GnssSystem.BeiDou:
                    if (sigId == 0 || sigId == 1) { rank = 0; return 1561.098e6; }
                    if (sigId == 5 || sigId == 6) { rank = 1; return 1575.42e6; }
                    if (sigId == 7 || sigId == 8) { rank = 2; return 1176.45e6; }
                    if (sigId == 2 || sigId == 3) { rank = 3; return 1207.14e6; }
                    break;
                case GnssSystem.Glonass:
                    if (freqId > 13) break;
                    if (sigId == 0) { rank = 0; return 1602.0e6 + (freqId - 7) * 0.5625e6; }
                    if (sigId == 2) { rank = 1; return 1246.0e6 + (freqId - 7) * 0.4375e6; }
                    break;
            }
            return 0;
        }

        private void DecodeRawx(byte[] f, int p, int len)
        {
            if (len < 16) return;
            int num = f[p + 11];
            if (len < 16 + 32 * num) return;
            RawxFrames++;
            var epoch = new ObsEpoch { GpsTowS = BitConverter.ToDouble(f, p) };
            if (double.IsNaN(epoch.GpsTowS) || epoch.GpsTowS < 0 || epoch.GpsTowS >= 604800) return;

            var best = new Dictionary<long, int>();   // satellite -> index in epoch.Obs
            var score = new List<int>();
            for (int m = 0; m < num; m++)
            {
                int o = p + 16 + 32 * m;
                int trk = f[o + 30];
                if ((trk & 2) == 0) continue;   // no valid carrier phase
                if (!MapSystem(f[o + 20], out GnssSystem sys)) continue;
                int sv = f[o + 21], sig = f[o + 22], fid = f[o + 23];
                if (sv < 1 || sv > 63 || (sys == GnssSystem.Glonass && sv > 24)) continue;
                double freq = Frequency(sys, sig, fid, out int rank);
                if (freq <= 0) continue;
                double cp = BitConverter.ToDouble(f, o + 8);
                if (cp == 0 || double.IsNaN(cp) || double.IsInfinity(cp)) continue;

                var obs = new PhaseObs
                {
                    System = sys,
                    Prn = sv,
                    PhaseRangeM = cp * GnssOrbit.C / freq,
                    LockMs = U2(f, o + 24),
                    HalfCycle = (trk & 4) == 0,
                    HalfState = (trk >> 2) & 3,
                    Cn0 = f[o + 26],
                    Signal = sig,
                };
                long key = ((long)sys << 8) | (uint)sv;
                int s = rank * 16 + sig;
                if (best.TryGetValue(key, out int at))
                {
                    if (s >= score[at]) continue;
                    epoch.Obs[at] = obs;
                    score[at] = s;
                }
                else
                {
                    best[key] = epoch.Obs.Count;
                    epoch.Obs.Add(obs);
                    score.Add(s);
                }
            }
            if (epoch.Obs.Count > 0) EpochReady?.Invoke(epoch);
        }

        // ---- RXM-SFRBX ----

        private void DecodeSfrbx(byte[] f, int p, int len)
        {
            if (len < 8) return;
            int gnss = f[p], sv = f[p + 1], sig = f[p + 2], fid = f[p + 3], nw = f[p + 4];
            if (len < 8 + 4 * nw) return;
            SfrbxFrames++;
            var w = new uint[nw];
            for (int i = 0; i < nw; i++) w[i] = U4(f, p + 8 + 4 * i);

            if (gnss == 0 && nw == 10 && sv >= 1 && sv <= 32) GpsLnav(sv, w);
            else if (gnss == 2 && nw == 8 && sv >= 1 && sv <= 36 && (sig == 0 || sig == 1 || sig == 5)) GalInav(sv, w);
            else if (gnss == 3 && nw == 10 && sv > 5 && sv < 59) BdsD1(sv, w);   // D1: not the geostationary ones
            else if (gnss == 6 && nw == 4 && sv >= 1 && sv <= 24) GloString(sv, fid, w);
        }

        private void Emit(Ephemeris e, long version)
        {
            if (e == null) return;
            long key = ((long)e.System << 8) | (uint)e.Prn;
            if (emitted.TryGetValue(key, out long v) && v == version) return;   // the same one again
            emitted[key] = version;
            EphemerisReady?.Invoke(e);
        }

        private static long U2Bits(byte[] b, int p1, int l1, int p2, int l2) => (Bits.U(b, p1, l1) << l2) | Bits.U(b, p2, l2);

        private static long S2Bits(byte[] b, int p1, int l1, int p2, int l2)
        {
            long v = U2Bits(b, p1, l1, p2, l2);
            int n = l1 + l2;
            return (v & (1L << (n - 1))) != 0 ? v - (1L << n) : v;
        }

        private static byte[] BigEndianWords(uint[] w)
        {
            var b = new byte[w.Length * 4];
            for (int i = 0; i < w.Length; i++)
                for (int j = 0; j < 4; j++) b[4 * i + j] = (byte)(w[i] >> (24 - 8 * j));
            return b;
        }

        // GPS L1 C/A: subframes 1-3, ten 30-bit words each, 24 data bits per word (parity already checked)
        private void GpsLnav(int prn, uint[] w)
        {
            var b = new byte[30];
            for (int k = 0; k < 10; k++) Bits.Put(b, 24 * k, 24, (w[k] >> 6) & 0xFFFFFF);
            if (Bits.U(b, 0, 8) != 0x8B) return;   // TLM preamble
            int id = (int)Bits.U(b, 43, 3);
            if (id < 1 || id > 3) return;
            if (!gpsSub.TryGetValue(prn, out byte[][] sub)) gpsSub[prn] = sub = new byte[3][];
            sub[id - 1] = b;
            if (sub[0] == null || sub[1] == null || sub[2] == null) return;

            byte[] s1 = sub[0], s2 = sub[1], s3 = sub[2];
            int iodc = (int)((Bits.U(s1, 48 + 22, 2) << 8) | Bits.U(s1, 48 + 22 + 2 + 1 + 87 + 8, 8));
            int iode2 = (int)Bits.U(s2, 48, 8), iode3 = (int)Bits.U(s3, 216, 8);
            if (iode2 != iode3 || (iodc & 0xFF) != iode2) return;   // not one set yet

            var e = new Ephemeris { System = GnssSystem.Gps, Prn = prn, Iode = iode2 };
            int i1 = 48 + 10 + 2 + 4;
            e.Healthy = Bits.U(s1, i1, 6) == 0;
            i1 = 48 + 10 + 2 + 4 + 6 + 2 + 1 + 87 + 8 + 8;
            e.Toc = Bits.U(s1, i1, 16) * 16.0; i1 += 16;
            e.Af2 = Bits.S(s1, i1, 8) * Math.Pow(2, -55); i1 += 8;
            e.Af1 = Bits.S(s1, i1, 16) * Math.Pow(2, -43); i1 += 16;
            e.Af0 = Bits.S(s1, i1, 22) * Math.Pow(2, -31);

            int i = 48 + 8;
            e.Crs = Bits.S(s2, i, 16) * Math.Pow(2, -5); i += 16;
            e.DeltaN = Bits.S(s2, i, 16) * Math.Pow(2, -43) * Sc; i += 16;
            e.M0 = Bits.S(s2, i, 32) * Math.Pow(2, -31) * Sc; i += 32;
            e.Cuc = Bits.S(s2, i, 16) * Math.Pow(2, -29); i += 16;
            e.E = Bits.U(s2, i, 32) * Math.Pow(2, -33); i += 32;
            e.Cus = Bits.S(s2, i, 16) * Math.Pow(2, -29); i += 16;
            e.SqrtA = Bits.U(s2, i, 32) * Math.Pow(2, -19); i += 32;
            e.Toe = Bits.U(s2, i, 16) * 16.0;

            i = 48;
            e.Cic = Bits.S(s3, i, 16) * Math.Pow(2, -29); i += 16;
            e.Omega0 = Bits.S(s3, i, 32) * Math.Pow(2, -31) * Sc; i += 32;
            e.Cis = Bits.S(s3, i, 16) * Math.Pow(2, -29); i += 16;
            e.I0 = Bits.S(s3, i, 32) * Math.Pow(2, -31) * Sc; i += 32;
            e.Crc = Bits.S(s3, i, 16) * Math.Pow(2, -5); i += 16;
            e.Omega = Bits.S(s3, i, 32) * Math.Pow(2, -31) * Sc; i += 32;
            e.OmegaDot = Bits.S(s3, i, 24) * Math.Pow(2, -43) * Sc; i += 24 + 8;
            e.Idot = Bits.S(s3, i, 14) * Math.Pow(2, -43) * Sc;
            if (e.SqrtA < 5000 || e.SqrtA > 5300) return;
            Emit(e, ((long)iodc << 20) | (long)e.Toe);
        }

        // Galileo I/NAV: an even and an odd page; a 128-bit word of 112 + 16 bits, word types 1-5
        private void GalInav(int prn, uint[] w)
        {
            byte[] b = BigEndianWords(w);
            if (Bits.U(b, 1, 1) == 1 || Bits.U(b, 129, 1) == 1) return;   // alert pages
            if (Bits.U(b, 0, 1) != 0 || Bits.U(b, 128, 1) != 1) return;   // even, then odd
            var crc = new byte[26];
            for (int i = 0; i < 15; i++) Bits.Put(crc, 4 + 8 * i, 8, Bits.U(b, 8 * i, 8));
            for (int i = 0; i < 11; i++) Bits.Put(crc, 118 + 8 * i, 8, Bits.U(b, 128 + 8 * i, 8));
            if (Rtcm3Frames.Crc24Q(crc, 25) != Bits.U(b, 128 + 82, 24)) return;

            int type = (int)Bits.U(b, 2, 6);
            if (type < 1 || type > 5) return;
            var word = new byte[16];
            for (int i = 0; i < 14; i++) Bits.Put(word, 8 * i, 8, Bits.U(b, 2 + 8 * i, 8));
            for (int i = 0; i < 2; i++) Bits.Put(word, 112 + 8 * i, 8, Bits.U(b, 130 + 8 * i, 8));
            if (!galWord.TryGetValue(prn, out byte[][] words)) galWord[prn] = words = new byte[6][];
            words[type] = word;
            if (words[1] == null || words[2] == null || words[3] == null || words[4] == null) return;
            long iod = Bits.U(words[1], 6, 10);
            if (Bits.U(words[2], 6, 10) != iod || Bits.U(words[3], 6, 10) != iod || Bits.U(words[4], 6, 10) != iod) return;

            var e = new Ephemeris { System = GnssSystem.Galileo, Prn = prn, Iode = (int)iod };
            byte[] w1 = words[1], w2 = words[2], w3 = words[3], w4 = words[4];
            e.Toe = Bits.U(w1, 16, 14) * 60.0;
            e.M0 = Bits.S(w1, 30, 32) * Math.Pow(2, -31) * Sc;
            e.E = Bits.U(w1, 62, 32) * Math.Pow(2, -33);
            e.SqrtA = Bits.U(w1, 94, 32) * Math.Pow(2, -19);
            e.Omega0 = Bits.S(w2, 16, 32) * Math.Pow(2, -31) * Sc;
            e.I0 = Bits.S(w2, 48, 32) * Math.Pow(2, -31) * Sc;
            e.Omega = Bits.S(w2, 80, 32) * Math.Pow(2, -31) * Sc;
            e.Idot = Bits.S(w2, 112, 14) * Math.Pow(2, -43) * Sc;
            e.OmegaDot = Bits.S(w3, 16, 24) * Math.Pow(2, -43) * Sc;
            e.DeltaN = Bits.S(w3, 40, 16) * Math.Pow(2, -43) * Sc;
            e.Cuc = Bits.S(w3, 56, 16) * Math.Pow(2, -29);
            e.Cus = Bits.S(w3, 72, 16) * Math.Pow(2, -29);
            e.Crc = Bits.S(w3, 88, 16) * Math.Pow(2, -5);
            e.Crs = Bits.S(w3, 104, 16) * Math.Pow(2, -5);
            e.Cic = Bits.S(w4, 22, 16) * Math.Pow(2, -29);
            e.Cis = Bits.S(w4, 38, 16) * Math.Pow(2, -29);
            e.Toc = Bits.U(w4, 54, 14) * 60.0;
            e.Af0 = Bits.S(w4, 68, 31) * Math.Pow(2, -34);
            e.Af1 = Bits.S(w4, 99, 21) * Math.Pow(2, -46);
            e.Af2 = Bits.S(w4, 120, 6) * Math.Pow(2, -59);
            if (words[5] != null) e.Healthy = Bits.U(words[5], 69, 2) == 0 && Bits.U(words[5], 72, 1) == 0;   // E1-B health, validity
            if (e.SqrtA < 5300 || e.SqrtA > 5600) return;
            Emit(e, (iod << 20) | (long)e.Toe | (e.Healthy ? 0 : 1L << 40));
        }

        // BeiDou D1 (MEO / IGSO): subframes 1-3, ten 30-bit words with their parity in place
        private void BdsD1(int prn, uint[] w)
        {
            var b = new byte[38];
            for (int i = 0; i < 10; i++) Bits.Put(b, 30 * i, 30, w[i] & 0x3FFFFFFF);
            int id = (int)Bits.U(b, 15, 3);
            if (id < 1 || id > 3) return;
            if (!bdsSub.TryGetValue(prn, out byte[][] sub)) bdsSub[prn] = sub = new byte[3][];
            sub[id - 1] = b;
            if (sub[0] == null || sub[1] == null || sub[2] == null) return;
            byte[] s1 = sub[0], s2 = sub[1], s3 = sub[2];
            long sow1 = U2Bits(s1, 18, 8, 30, 12), sow2 = U2Bits(s2, 18, 8, 30, 12), sow3 = U2Bits(s3, 18, 8, 30, 12);
            if (sow2 != sow1 + 6 || sow3 != sow2 + 6) return;   // three subframes of one frame

            var e = new Ephemeris { System = GnssSystem.BeiDou, Prn = prn };
            e.Healthy = Bits.U(s1, 42, 1) == 0;
            e.Toc = U2Bits(s1, 73, 9, 90, 8) * 8.0;
            e.Af2 = Bits.S(s1, 214, 11) * Math.Pow(2, -66);
            e.Af0 = S2Bits(s1, 225, 7, 240, 17) * Math.Pow(2, -33);
            e.Af1 = S2Bits(s1, 257, 5, 270, 17) * Math.Pow(2, -50);
            e.Iode = (int)Bits.U(s1, 287, 5);

            e.DeltaN = S2Bits(s2, 42, 10, 60, 6) * Math.Pow(2, -43) * Sc;
            e.Cuc = S2Bits(s2, 66, 16, 90, 2) * Math.Pow(2, -31);
            e.M0 = S2Bits(s2, 92, 20, 120, 12) * Math.Pow(2, -31) * Sc;
            e.E = U2Bits(s2, 132, 10, 150, 22) * Math.Pow(2, -33);
            e.Cus = Bits.S(s2, 180, 18) * Math.Pow(2, -31);
            e.Crc = S2Bits(s2, 198, 4, 210, 14) * Math.Pow(2, -6);
            e.Crs = S2Bits(s2, 224, 8, 240, 10) * Math.Pow(2, -6);
            e.SqrtA = U2Bits(s2, 250, 12, 270, 20) * Math.Pow(2, -19);
            long toe1 = Bits.U(s2, 290, 2);

            long toe2 = U2Bits(s3, 42, 10, 60, 5);
            e.I0 = S2Bits(s3, 65, 17, 90, 15) * Math.Pow(2, -31) * Sc;
            e.Cic = S2Bits(s3, 105, 7, 120, 11) * Math.Pow(2, -31);
            e.OmegaDot = S2Bits(s3, 131, 11, 150, 13) * Math.Pow(2, -43) * Sc;
            e.Cis = S2Bits(s3, 163, 9, 180, 9) * Math.Pow(2, -31);
            e.Idot = S2Bits(s3, 189, 13, 210, 1) * Math.Pow(2, -43) * Sc;
            e.Omega0 = S2Bits(s3, 211, 21, 240, 11) * Math.Pow(2, -31) * Sc;
            e.Omega = S2Bits(s3, 251, 11, 270, 21) * Math.Pow(2, -31) * Sc;
            e.Toe = ((toe1 << 15) | toe2) * 8.0;
            if (e.SqrtA < 5000 || e.SqrtA > 6600) return;   // MEO about 5282, IGSO about 6493
            Emit(e, ((long)e.Iode << 20) | (long)e.Toe);
        }

        // GLONASS L1OF: strings 1-4 of one frame (each sent every 2 s); string 1 starts the set
        private void GloString(int slot, int freqId, uint[] w)
        {
            byte[] b = BigEndianWords(w);
            int m = (int)Bits.U(b, 1, 4);
            if (m < 1 || m > 4) return;   // the almanac strings are not needed
            if (!gloStr.TryGetValue(slot, out GloFrame fr)) gloStr[slot] = fr = new GloFrame();
            if (m == 1) fr.Mask = 0;
            else if ((fr.Mask & (1 << (m - 2))) == 0) return;   // not right after the one before
            for (int i = 0; i < 80; i += 8) Bits.Put(fr.Strings, 80 * (m - 1) + i, 8, Bits.U(b, i, 8));
            fr.Mask |= 1 << (m - 1);
            if (fr.Mask != 0xF) return;

            byte[] s = fr.Strings;
            var e = new Ephemeris { System = GnssSystem.Glonass, Prn = slot, FreqChannel = freqId - 7 };
            int i0 = 1 + 4 + 2 + 2 + 12;
            double v = Math.Pow(2, -20) * 1000, p = Math.Pow(2, -11) * 1000, a = Math.Pow(2, -30) * 1000;
            e.Vx = Bits.SM(s, i0, 24) * v; i0 += 24;
            e.Ax = Bits.SM(s, i0, 5) * a; i0 += 5;
            e.X = Bits.SM(s, i0, 27) * p;
            int j = 81 + 4;
            int bn = (int)Bits.U(s, j, 3); j += 3 + 1;
            double tb = Bits.U(s, j, 7) * 900.0; j += 7 + 5;
            e.Vy = Bits.SM(s, j, 24) * v; j += 24;
            e.Ay = Bits.SM(s, j, 5) * a; j += 5;
            e.Y = Bits.SM(s, j, 27) * p;
            int k = 161 + 4 + 1;
            e.GammaN = Bits.SM(s, k, 11) * Math.Pow(2, -40); k += 11 + 1 + 2 + 1;
            e.Vz = Bits.SM(s, k, 24) * v; k += 24;
            e.Az = Bits.SM(s, k, 5) * a; k += 5;
            e.Z = Bits.SM(s, k, 27) * p;
            int l = 241 + 4;
            e.TauN = Bits.SM(s, l, 22) * Math.Pow(2, -30); l += 22 + 5 + 5 + 14 + 1 + 4 + 3 + 11;
            int n = (int)Bits.U(s, l, 5);
            if (n >= 1 && n <= 24) e.Prn = n;

            e.Toe = e.Toc = GnssOrbit.DayWrap(tb - 10800 + RtcmDecoder.GpsUtcLeapS);
            if (e.Toe < 0) e.Toe = e.Toc = e.Toe + 86400;
            e.Iode = (int)(tb / 900);
            e.Healthy = (bn & 4) == 0;
            double r = Math.Sqrt(e.X * e.X + e.Y * e.Y + e.Z * e.Z);
            if (r < 2.4e7 || r > 2.7e7) return;
            Emit(e, ((long)e.Iode << 1) | (e.Healthy ? 0L : 1L));
        }
    }
}
