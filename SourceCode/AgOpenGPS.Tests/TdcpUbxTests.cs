using AgLibrary.Gnss;
using AgOpenGPS.Tdcp;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;

namespace AgOpenGPS.Tests
{
    // u-blox UBX (RAWX + SFRBX) and ephemerides from the correction stream
    public partial class TdcpTests
    {
        private const double Sc = Math.PI;

        private static long Q(double value, double scale) => (long)Math.Round(value / scale);

        private static void Put2(byte[] b, int p1, int l1, int p2, int l2, long v)
        {
            Bits.Put(b, p1, l1, v >> l2);
            Bits.Put(b, p2, l2, v & ((1L << l2) - 1));
        }

        private static void PutSmAt(byte[] b, int pos, int len, double value)
        {
            long m = (long)Math.Round(Math.Abs(value));
            Bits.Put(b, pos, 1, value < 0 ? 1 : 0);
            Bits.Put(b, pos + 1, len - 1, m);
        }

        private static uint[] Words(byte[] b, int bitsPerWord, int shift, int count)
        {
            var w = new uint[count];
            for (int i = 0; i < count; i++) w[i] = (uint)(Bits.U(b, bitsPerWord * i, bitsPerWord) << shift);
            return w;
        }

        private static uint[] BigEndian(byte[] b)
        {
            var w = new uint[b.Length / 4];
            for (int i = 0; i < w.Length; i++) w[i] = (uint)(b[4 * i] << 24 | b[4 * i + 1] << 16 | b[4 * i + 2] << 8 | b[4 * i + 3]);
            return w;
        }

        private static byte[] Sfrbx(int gnssId, int sv, int sigId, int freqId, uint[] words)
        {
            var p = new byte[8 + 4 * words.Length];
            p[0] = (byte)gnssId;
            p[1] = (byte)sv;
            p[2] = (byte)sigId;
            p[3] = (byte)freqId;
            p[4] = (byte)words.Length;
            p[6] = 2;
            for (int i = 0; i < words.Length; i++) BitConverter.GetBytes(words[i]).CopyTo(p, 8 + 4 * i);
            return Rtcm3Frames.WrapUbx(0x02, 0x13, p);
        }

        // ---- GPS LNAV subframes 1-3 ----

        private static IEnumerable<byte[]> GpsSubframes(Ephemeris e)
        {
            var s1 = new byte[30];
            var s2 = new byte[30];
            var s3 = new byte[30];
            foreach (byte[] s in new[] { s1, s2, s3 }) Bits.Put(s, 0, 8, 0x8B);
            Bits.Put(s1, 43, 3, 1);
            Bits.Put(s2, 43, 3, 2);
            Bits.Put(s3, 43, 3, 3);
            Bits.Put(s1, 48, 10, 300);
            Bits.Put(s1, 64, 6, e.Healthy ? 0 : 1);
            Bits.Put(s1, 70, 2, 0);
            Bits.Put(s1, 168, 8, e.Iode);
            Bits.Put(s1, 176, 16, (long)(e.Toc / 16));
            Bits.Put(s1, 192, 8, Q(e.Af2, Math.Pow(2, -55)));
            Bits.Put(s1, 200, 16, Q(e.Af1, Math.Pow(2, -43)));
            Bits.Put(s1, 216, 22, Q(e.Af0, Math.Pow(2, -31)));

            Bits.Put(s2, 48, 8, e.Iode);
            Bits.Put(s2, 56, 16, Q(e.Crs, Math.Pow(2, -5)));
            Bits.Put(s2, 72, 16, Q(e.DeltaN / Sc, Math.Pow(2, -43)));
            Bits.Put(s2, 88, 32, Q(Norm(e.M0) / Sc, Math.Pow(2, -31)));
            Bits.Put(s2, 120, 16, Q(e.Cuc, Math.Pow(2, -29)));
            Bits.Put(s2, 136, 32, Q(e.E, Math.Pow(2, -33)));
            Bits.Put(s2, 168, 16, Q(e.Cus, Math.Pow(2, -29)));
            Bits.Put(s2, 184, 32, Q(e.SqrtA, Math.Pow(2, -19)));
            Bits.Put(s2, 216, 16, (long)(e.Toe / 16));

            Bits.Put(s3, 48, 16, Q(e.Cic, Math.Pow(2, -29)));
            Bits.Put(s3, 64, 32, Q(Norm(e.Omega0) / Sc, Math.Pow(2, -31)));
            Bits.Put(s3, 96, 16, Q(e.Cis, Math.Pow(2, -29)));
            Bits.Put(s3, 112, 32, Q(e.I0 / Sc, Math.Pow(2, -31)));
            Bits.Put(s3, 144, 16, Q(e.Crc, Math.Pow(2, -5)));
            Bits.Put(s3, 160, 32, Q(Norm(e.Omega) / Sc, Math.Pow(2, -31)));
            Bits.Put(s3, 192, 24, Q(e.OmegaDot / Sc, Math.Pow(2, -43)));
            Bits.Put(s3, 216, 8, e.Iode);
            Bits.Put(s3, 224, 14, Q(e.Idot / Sc, Math.Pow(2, -43)));
            foreach (byte[] s in new[] { s1, s2, s3 }) yield return Sfrbx(0, e.Prn, 0, 0, Words(s, 24, 6, 10));
        }

        private static void AssertSameOrbit(Ephemeris src, Ephemeris got, double t)
        {
            Assert.That(got, Is.Not.Null);
            Ecef a = GnssOrbit.Position(src, t, out double ca), b = GnssOrbit.Position(got, t, out double cb);
            Assert.That((a - b).Norm, Is.LessThan(1.0));
            Assert.That(cb, Is.EqualTo(ca).Within(1e-9));
        }

        [Test]
        public void Ubx_GpsSubframesGiveTheEphemerisOnce()
        {
            Ephemeris src = Constellation()[3];
            src.Iode = 45;
            src.Crs = -20.5;
            src.Cis = 3e-7;
            var got = new List<Ephemeris>();
            var d = new UbxDecoder();
            d.EphemerisReady += e => got.Add(e);
            foreach (byte[] f in GpsSubframes(src)) d.Decode(f);
            foreach (byte[] f in GpsSubframes(src)) d.Decode(f);   // the next 30 s: the same set
            Assert.That(got.Count, Is.EqualTo(1));
            Assert.That(got[0].Prn, Is.EqualTo(src.Prn));
            Assert.That(got[0].Iode, Is.EqualTo(45));
            Assert.That(got[0].Toe, Is.EqualTo(src.Toe));
            Assert.That(got[0].Crs, Is.EqualTo(-20.5).Within(0.05));
            AssertSameOrbit(src, got[0], Tow0);

            src.Iode = 46;   // a new upload
            foreach (byte[] f in GpsSubframes(src)) d.Decode(f);
            Assert.That(got.Count, Is.EqualTo(2));
        }

        [Test]
        public void Ubx_GpsSubframesOfDifferentSetsAreNotMixed()
        {
            Ephemeris a = Constellation()[3], b = Constellation()[3];
            a.Iode = 10;
            b.Iode = 11;
            b.M0 += 0.5;
            var got = new List<Ephemeris>();
            var d = new UbxDecoder();
            d.EphemerisReady += e => got.Add(e);
            List<byte[]> fa = GpsSubframes(a).ToList(), fb = GpsSubframes(b).ToList();
            d.Decode(fa[0]);
            d.Decode(fa[1]);
            d.Decode(fb[2]);   // subframe 3 of the next set
            Assert.That(got, Is.Empty);
        }

        // ---- Galileo I/NAV ----

        private static byte[] GalWord(int type, Action<byte[]> fill)
        {
            var w = new byte[16];
            Bits.Put(w, 0, 6, type);
            fill(w);
            return w;
        }

        private static byte[] GalPage(int sv, byte[] word)
        {
            var b = new byte[32];
            for (int i = 0; i < 112; i++) Bits.Put(b, 2 + i, 1, Bits.U(word, i, 1));
            Bits.Put(b, 128, 1, 1);
            for (int i = 0; i < 16; i++) Bits.Put(b, 130 + i, 1, Bits.U(word, 112 + i, 1));
            var crc = new byte[26];
            for (int i = 0; i < 15; i++) Bits.Put(crc, 4 + 8 * i, 8, Bits.U(b, 8 * i, 8));
            for (int i = 0; i < 11; i++) Bits.Put(crc, 118 + 8 * i, 8, Bits.U(b, 128 + 8 * i, 8));
            Bits.Put(b, 128 + 82, 24, Rtcm3Frames.Crc24Q(crc, 25));
            return Sfrbx(2, sv, 1, 0, BigEndian(b));
        }

        private static IEnumerable<byte[]> GalPages(Ephemeris e, int iod)
        {
            yield return GalPage(e.Prn, GalWord(1, w =>
            {
                Bits.Put(w, 6, 10, iod);
                Bits.Put(w, 16, 14, (long)(e.Toe / 60));
                Bits.Put(w, 30, 32, Q(Norm(e.M0) / Sc, Math.Pow(2, -31)));
                Bits.Put(w, 62, 32, Q(e.E, Math.Pow(2, -33)));
                Bits.Put(w, 94, 32, Q(e.SqrtA, Math.Pow(2, -19)));
            }));
            yield return GalPage(e.Prn, GalWord(2, w =>
            {
                Bits.Put(w, 6, 10, iod);
                Bits.Put(w, 16, 32, Q(Norm(e.Omega0) / Sc, Math.Pow(2, -31)));
                Bits.Put(w, 48, 32, Q(e.I0 / Sc, Math.Pow(2, -31)));
                Bits.Put(w, 80, 32, Q(Norm(e.Omega) / Sc, Math.Pow(2, -31)));
                Bits.Put(w, 112, 14, Q(e.Idot / Sc, Math.Pow(2, -43)));
            }));
            yield return GalPage(e.Prn, GalWord(3, w =>
            {
                Bits.Put(w, 6, 10, iod);
                Bits.Put(w, 16, 24, Q(e.OmegaDot / Sc, Math.Pow(2, -43)));
                Bits.Put(w, 40, 16, Q(e.DeltaN / Sc, Math.Pow(2, -43)));
                Bits.Put(w, 104, 16, Q(e.Crs, Math.Pow(2, -5)));
            }));
            yield return GalPage(e.Prn, GalWord(4, w =>
            {
                Bits.Put(w, 6, 10, iod);
                Bits.Put(w, 16, 6, e.Prn);
                Bits.Put(w, 54, 14, (long)(e.Toc / 60));
                Bits.Put(w, 68, 31, Q(e.Af0, Math.Pow(2, -34)));
                Bits.Put(w, 99, 21, Q(e.Af1, Math.Pow(2, -46)));
            }));
            yield return GalPage(e.Prn, GalWord(5, w => Bits.Put(w, 69, 2, e.Healthy ? 0 : 1)));
        }

        private static Ephemeris GalSat()
        {
            Ephemeris e = Constellation()[8];
            e.System = GnssSystem.Galileo;
            e.Prn = 12;
            e.SqrtA = 5440.6;
            e.Toe = e.Toc = 100800;   // a multiple of 60
            e.Crs = 33.25;
            return e;
        }

        [Test]
        public void Ubx_GalileoPagesGiveTheEphemeris()
        {
            Ephemeris src = GalSat();
            Ephemeris got = null;
            var d = new UbxDecoder();
            d.EphemerisReady += e => got = e;
            foreach (byte[] f in GalPages(src, 77)) d.Decode(f);
            Assert.That(got.System, Is.EqualTo(GnssSystem.Galileo));
            Assert.That(got.Iode, Is.EqualTo(77));
            Assert.That(got.Healthy, Is.True);
            Assert.That(got.Crs, Is.EqualTo(33.25).Within(0.05));
            AssertSameOrbit(src, got, Tow0);
        }

        [Test]
        public void Ubx_GalileoPageWithABadCrcIsDropped()
        {
            Ephemeris src = GalSat();
            Ephemeris got = null;
            var d = new UbxDecoder();
            d.EphemerisReady += e => got = e;
            List<byte[]> pages = GalPages(src, 77).ToList();
            byte[] bad = (byte[])pages[0].Clone();
            bad[6 + 8 + 5] ^= 0x10;                               // a flipped data bit
            byte[] rewrapped = Rtcm3Frames.WrapUbx(0x02, 0x13, bad.Skip(6).Take(bad.Length - 8).ToArray());
            d.Decode(rewrapped);
            foreach (byte[] f in pages.Skip(1)) d.Decode(f);
            Assert.That(got, Is.Null);
        }

        // ---- BeiDou D1 ----

        private static IEnumerable<byte[]> BdsSubframes(Ephemeris e, long sow)
        {
            var s1 = new byte[38];
            var s2 = new byte[38];
            var s3 = new byte[38];
            int k = 0;
            foreach (byte[] s in new[] { s1, s2, s3 })
            {
                Bits.Put(s, 0, 11, 0x712);
                Bits.Put(s, 15, 3, k + 1);
                Put2(s, 18, 8, 30, 12, sow + 6 * k);
                k++;
            }
            Bits.Put(s1, 42, 1, e.Healthy ? 0 : 1);
            Put2(s1, 73, 9, 90, 8, (long)(e.Toc / 8));
            Bits.Put(s1, 214, 11, Q(e.Af2, Math.Pow(2, -66)));
            Put2(s1, 225, 7, 240, 17, Q(e.Af0, Math.Pow(2, -33)));
            Put2(s1, 257, 5, 270, 17, Q(e.Af1, Math.Pow(2, -50)));
            Bits.Put(s1, 287, 5, e.Iode);

            Put2(s2, 42, 10, 60, 6, Q(e.DeltaN / Sc, Math.Pow(2, -43)));
            Put2(s2, 66, 16, 90, 2, Q(e.Cuc, Math.Pow(2, -31)));
            Put2(s2, 92, 20, 120, 12, Q(Norm(e.M0) / Sc, Math.Pow(2, -31)));
            Put2(s2, 132, 10, 150, 22, Q(e.E, Math.Pow(2, -33)));
            Bits.Put(s2, 180, 18, Q(e.Cus, Math.Pow(2, -31)));
            Put2(s2, 198, 4, 210, 14, Q(e.Crc, Math.Pow(2, -6)));
            Put2(s2, 224, 8, 240, 10, Q(e.Crs, Math.Pow(2, -6)));
            Put2(s2, 250, 12, 270, 20, Q(e.SqrtA, Math.Pow(2, -19)));
            long toe = (long)(e.Toe / 8);
            Bits.Put(s2, 290, 2, toe >> 15);

            Put2(s3, 42, 10, 60, 5, toe & 0x7FFF);
            Put2(s3, 65, 17, 90, 15, Q(e.I0 / Sc, Math.Pow(2, -31)));
            Put2(s3, 105, 7, 120, 11, Q(e.Cic, Math.Pow(2, -31)));
            Put2(s3, 131, 11, 150, 13, Q(e.OmegaDot / Sc, Math.Pow(2, -43)));
            Put2(s3, 163, 9, 180, 9, Q(e.Cis, Math.Pow(2, -31)));
            Put2(s3, 189, 13, 210, 1, Q(e.Idot / Sc, Math.Pow(2, -43)));
            Put2(s3, 211, 21, 240, 11, Q(Norm(e.Omega0) / Sc, Math.Pow(2, -31)));
            Put2(s3, 251, 11, 270, 21, Q(Norm(e.Omega) / Sc, Math.Pow(2, -31)));
            foreach (byte[] s in new[] { s1, s2, s3 }) yield return Sfrbx(3, e.Prn, 0, 0, Words(s, 30, 0, 10));
        }

        [Test]
        public void Ubx_BeiDouD1SubframesGiveTheEphemeris()
        {
            Ephemeris src = Constellation()[10];
            src.System = GnssSystem.BeiDou;
            src.Prn = 23;
            src.SqrtA = 5282.6;
            src.Iode = 9;
            src.Crc = 150.5;
            src.Idot = -4e-10;
            Ephemeris got = null;
            var d = new UbxDecoder();
            d.EphemerisReady += e => got = e;
            foreach (byte[] f in BdsSubframes(src, 86000)) d.Decode(f);
            Assert.That(got.System, Is.EqualTo(GnssSystem.BeiDou));
            Assert.That(got.Iode, Is.EqualTo(9));
            Assert.That(got.Toe, Is.EqualTo(src.Toe));
            Assert.That(got.Crc, Is.EqualTo(150.5).Within(0.05));
            Assert.That(got.Idot, Is.EqualTo(-4e-10).Within(1e-12));
            AssertSameOrbit(src, got, Tow0);

            // subframes that are not of one frame (their seconds of week do not follow): nothing
            Ephemeris none = null;
            var d2 = new UbxDecoder();
            d2.EphemerisReady += e => none = e;
            List<byte[]> a = BdsSubframes(src, 86000).ToList(), b = BdsSubframes(src, 90000).ToList();
            d2.Decode(a[0]);
            d2.Decode(a[1]);
            d2.Decode(b[2]);
            Assert.That(none, Is.Null);
        }

        // ---- GLONASS strings ----

        private static IEnumerable<byte[]> GloStrings(Ephemeris e, int tbIndex, int freqId)
        {
            double v = Math.Pow(2, -20) * 1000, p = Math.Pow(2, -11) * 1000, a = Math.Pow(2, -30) * 1000;
            var s = new byte[4][];
            for (int m = 0; m < 4; m++)
            {
                s[m] = new byte[16];
                Bits.Put(s[m], 1, 4, m + 1);
            }
            PutSmAt(s[0], 21, 24, e.Vx / v);
            PutSmAt(s[0], 45, 5, e.Ax / a);
            PutSmAt(s[0], 50, 27, e.X / p);
            Bits.Put(s[1], 5, 3, e.Healthy ? 0 : 4);
            Bits.Put(s[1], 9, 7, tbIndex);
            PutSmAt(s[1], 21, 24, e.Vy / v);
            PutSmAt(s[1], 45, 5, e.Ay / a);
            PutSmAt(s[1], 50, 27, e.Y / p);
            PutSmAt(s[2], 6, 11, e.GammaN / Math.Pow(2, -40));
            PutSmAt(s[2], 21, 24, e.Vz / v);
            PutSmAt(s[2], 45, 5, e.Az / a);
            PutSmAt(s[2], 50, 27, e.Z / p);
            PutSmAt(s[3], 5, 22, e.TauN / Math.Pow(2, -30));
            Bits.Put(s[3], 70, 5, e.Prn);
            foreach (byte[] str in s) yield return Sfrbx(6, e.Prn, 0, freqId, BigEndian(str));
        }

        [Test]
        public void Ubx_GlonassStringsGiveTheEphemeris()
        {
            Ephemeris src = GloSat(14, 2.0, 0.7, 0, 1.13);
            src.Ax = -2.8e-6;
            src.TauN = -3.5e-5;
            Ephemeris got = null;
            var d = new UbxDecoder();
            d.EphemerisReady += e => got = e;
            List<byte[]> strings = GloStrings(src, 50, 3).ToList();
            d.Decode(strings[1]);   // joined in the middle of a frame: wait for string 1
            d.Decode(strings[2]);
            d.Decode(strings[3]);
            Assert.That(got, Is.Null);
            foreach (byte[] f in strings) d.Decode(f);
            Assert.That(got, Is.Not.Null);
            Assert.That(got.System, Is.EqualTo(GnssSystem.Glonass));
            Assert.That(got.Prn, Is.EqualTo(14));
            Assert.That(got.FreqChannel, Is.EqualTo(-4));
            Assert.That(got.X, Is.EqualTo(src.X).Within(0.5));
            Assert.That(got.Vz, Is.EqualTo(src.Vz).Within(0.001));
            Assert.That(got.Ax, Is.EqualTo(src.Ax).Within(1e-6));
            Assert.That(got.TauN, Is.EqualTo(src.TauN).Within(1e-9));
            Assert.That(got.Toe, Is.EqualTo(50 * 900 - 10800 + RtcmDecoder.GpsUtcLeapS));
        }

        // ---- RXM-RAWX ----

        private sealed class RawMeas
        {
            public int Gnss, Sv, Sig, FreqId = 7, Lock = 30000, Trk = 0x07, Cno = 42;
            public double Cycles;
        }

        private static byte[] Rawx(double tow, IList<RawMeas> meas)
        {
            var p = new byte[16 + 32 * meas.Count];
            BitConverter.GetBytes(tow).CopyTo(p, 0);
            BitConverter.GetBytes((ushort)2300).CopyTo(p, 8);
            p[10] = 18;
            p[11] = (byte)meas.Count;
            p[13] = 1;
            for (int i = 0; i < meas.Count; i++)
            {
                int o = 16 + 32 * i;
                RawMeas m = meas[i];
                BitConverter.GetBytes(m.Cycles * 0.19).CopyTo(p, o);   // pseudorange: not used
                BitConverter.GetBytes(m.Cycles).CopyTo(p, o + 8);
                p[o + 20] = (byte)m.Gnss;
                p[o + 21] = (byte)m.Sv;
                p[o + 22] = (byte)m.Sig;
                p[o + 23] = (byte)m.FreqId;
                BitConverter.GetBytes((ushort)m.Lock).CopyTo(p, o + 24);
                p[o + 26] = (byte)m.Cno;
                p[o + 30] = (byte)m.Trk;
            }
            return Rtcm3Frames.WrapUbx(0x02, 0x15, p);
        }

        [Test]
        public void Ubx_RawxTakesTheL1SignalOfEachSatellite()
        {
            const double L1 = 1575.42e6, L2 = 1227.60e6;
            var meas = new List<RawMeas>
            {
                new RawMeas { Gnss = 0, Sv = 5, Sig = 3, Cycles = 1.1e8 },                  // GPS L2C, before its L1
                new RawMeas { Gnss = 0, Sv = 5, Sig = 0, Cycles = 1.2e8 },                  // GPS L1 C/A
                new RawMeas { Gnss = 0, Sv = 8, Sig = 3, Cycles = 0.9e8 },                  // only L2: taken
                new RawMeas { Gnss = 6, Sv = 4, Sig = 0, FreqId = 2, Cycles = 1.3e8 },      // GLONASS k = -5
                new RawMeas { Gnss = 2, Sv = 11, Sig = 1, Cycles = 1.25e8, Trk = 0x01 },    // no valid phase
                new RawMeas { Gnss = 1, Sv = 3, Sig = 0, Cycles = 1.2e8 },                  // SBAS: not used
                new RawMeas { Gnss = 3, Sv = 30, Sig = 0, Cycles = 1.15e8, Trk = 0x03 },    // half cycle not resolved
            };
            ObsEpoch ep = null;
            var d = new UbxDecoder();
            d.EpochReady += e => ep = e;
            d.Decode(Rawx(345678.9, meas));
            Assert.That(ep, Is.Not.Null);
            Assert.That(ep.GpsTowS, Is.EqualTo(345678.9));
            Assert.That(ep.Obs.Count, Is.EqualTo(4));
            PhaseObs g5 = ep.Obs.Single(o => o.System == GnssSystem.Gps && o.Prn == 5);
            Assert.That(g5.Signal, Is.EqualTo(0));
            Assert.That(g5.PhaseRangeM, Is.EqualTo(1.2e8 * GnssOrbit.C / L1).Within(1e-6));
            Assert.That(g5.LockMs, Is.EqualTo(30000));
            Assert.That(ep.Obs.Single(o => o.Prn == 8).PhaseRangeM, Is.EqualTo(0.9e8 * GnssOrbit.C / L2).Within(1e-6));
            Assert.That(ep.Obs.Single(o => o.System == GnssSystem.Glonass).PhaseRangeM,
                Is.EqualTo(1.3e8 * GnssOrbit.C / (1602e6 - 5 * 0.5625e6)).Within(1e-6));
            Assert.That(ep.Obs.Single(o => o.System == GnssSystem.BeiDou).HalfCycle, Is.True);
        }

        [Test]
        public void Ubx_TheWholeChainFromRawxAndSfrbx()
        {
            List<Ephemeris> ephs = Constellation();
            var engine = new TdcpEngine();
            engine.SetPosition(Lat, Lon, Height);
            // what the receiver's SFRBX carries, decoded as the engine will
            var decoded = new List<Ephemeris>();
            var probe = new UbxDecoder();
            probe.EphemerisReady += e => decoded.Add(e);
            foreach (Ephemeris e in ephs)
                foreach (byte[] f in GpsSubframes(e))
                {
                    probe.Decode(f);
                    engine.Feed(Rtcm3Frames.Tagged(Rtcm3Frames.TagRover, f));
                }
            Assert.That(engine.EphemerisCount, Is.EqualTo(ephs.Count));
            Assert.That(engine.Format, Is.EqualTo("u-blox UBX"));

            double sumE = 0, sumN = 0, gotE = 0, gotN = 0;
            for (int k = 0; k <= 10; k++)
            {
                ObsEpoch ep = Observe(decoded, k);
                var meas = ep.Obs.Select(o => new RawMeas { Gnss = 0, Sv = o.Prn, Sig = 0, Cycles = o.PhaseRangeM * 1575.42e6 / GnssOrbit.C }).ToList();
                engine.Feed(Rtcm3Frames.Tagged(Rtcm3Frames.TagRover, Rawx(ep.GpsTowS, meas)));
                if (k > 0)
                {
                    TrueMove(k, out double e, out double n);
                    sumE += e;
                    sumN += n;
                }
                engine.Take(out double te, out double tn, out bool valid);
                if (k == 0) continue;
                Assert.That(valid, Is.True, engine.Last?.Reason);
                gotE += te;
                gotN += tn;
            }
            Assert.That(gotE, Is.EqualTo(sumE).Within(0.01));
            Assert.That(gotN, Is.EqualTo(sumN).Within(0.01));
        }

        // ---- the correction stream ----

        [Test]
        public void Corrections_OnlyTheirEphemeridesAreTaken()
        {
            List<Ephemeris> ephs = Constellation();
            var engine = new TdcpEngine();
            engine.SetPosition(Lat, Lon, Height);
            foreach (Ephemeris e in ephs) engine.Feed(Rtcm3Frames.Tagged(Rtcm3Frames.TagCorrections, Encode1019(e)));
            Assert.That(engine.EphemerisCount, Is.EqualTo(ephs.Count));
            Assert.That(engine.EphemerisFromCorrections, Is.EqualTo(ephs.Count));
            Assert.That(engine.SinceFrameS, Is.GreaterThan(1e6));   // corrections are not the receiver's raw data

            // the base station's observations must never be taken for the rover's
            for (int k = 0; k < 5; k++)
            {
                ObsEpoch ep = Observe(ephs, k);
                engine.Feed(Rtcm3Frames.Tagged(Rtcm3Frames.TagCorrections, EncodeMsm4(1074, ep.GpsTowS, ep.Obs, false)));
            }
            Assert.That(engine.Epochs, Is.EqualTo(0));

            // the receiver sends one itself: from then on that one counts as the receiver's
            engine.Feed(Rtcm3Frames.Tagged(Rtcm3Frames.TagRover, Encode1019(ephs[0])));
            Assert.That(engine.EphemerisFromCorrections, Is.EqualTo(ephs.Count - 1));
            Assert.That(engine.Format, Is.EqualTo("RTCM 3"));
        }

        [Test]
        public void Frames_UbxRtcmAndNmeaComeApart()
        {
            byte[] ubx = Rawx(1000, new[] { new RawMeas { Gnss = 0, Sv = 1, Cycles = 1e8 } });
            byte[] rtcm = Rtcm3Frames.Wrap(new byte[] { 0x3F, 0x20, 9 });
            byte[] nmea = System.Text.Encoding.ASCII.GetBytes("$GNRMC,1,2*00\r\n");
            byte[] badUbx = (byte[])ubx.Clone();
            badUbx[20] ^= 1;
            var stream = nmea.Concat(ubx).Concat(nmea).Concat(badUbx).Concat(rtcm).Concat(nmea).ToArray();
            var splitter = new Rtcm3Frames();
            var frames = new List<byte[]>();
            var text = new List<byte>();
            for (int i = 0; i < stream.Length; i += 5)
            {
                int n = Math.Min(5, stream.Length - i);
                splitter.Feed(stream.Skip(i).Take(n).ToArray(), n, frames, text);
            }
            Assert.That(frames.Count, Is.EqualTo(2));
            Assert.That(frames[0], Is.EqualTo(ubx));
            Assert.That(frames[1], Is.EqualTo(rtcm));
            string s = System.Text.Encoding.ASCII.GetString(text.Where(b => b < 128).ToArray());
            Assert.That(s.Split(new[] { "$GNRMC" }, StringSplitOptions.None).Length - 1, Is.EqualTo(3));
            Assert.That(Rtcm3Frames.RtcmType(Rtcm3Frames.Wrap(new byte[] { 0x3F, 0xB0 })), Is.EqualTo(1019));
        }
    }
}
