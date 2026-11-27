using AgLibrary.Gnss;
using AgOpenGPS.Tdcp;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;

namespace AgOpenGPS.Tests
{
    public partial class TdcpTests
    {
        // a generic place (not anyone's field)
        private const double Lat = 45.0, Lon = 10.0, Height = 100;
        private const double Tow0 = 100000;
        private const double LightMsM = 299792.458;

        // ---- synthetic GPS constellation: 6 planes x 4 satellites ----

        private static List<Ephemeris> Constellation()
        {
            var list = new List<Ephemeris>();
            int prn = 1;
            for (int plane = 0; plane < 6; plane++)
                for (int slot = 0; slot < 4; slot++)
                    list.Add(new Ephemeris
                    {
                        System = GnssSystem.Gps,
                        Prn = prn++,
                        Toe = 100800,
                        Toc = 100800,
                        SqrtA = 5153.6,
                        E = 0.01,
                        I0 = 55 * Math.PI / 180,
                        Omega0 = plane * Math.PI / 3,
                        Omega = 0.3,
                        M0 = slot * Math.PI / 2 + plane * Math.PI / 12,
                        DeltaN = 4.5e-9,
                        OmegaDot = -8e-9,
                        Af0 = 1e-5 * (prn % 5),
                        Af1 = 1e-12,
                    });
            return list;
        }

        private static int Visible(IEnumerable<Ephemeris> ephs, Ecef rx)
        {
            int n = 0;
            foreach (Ephemeris e in ephs)
            {
                GnssOrbit.Range(e, Tow0, rx, out _, out Ecef u);
                if (GnssOrbit.Elevation(u, Lat, Lon) > 15 * Math.PI / 180) n++;
            }
            return n;
        }

        // the receiver drives a curve; its clock drifts 100 m/s and jumps a little
        private static void Truth(int k, out double lat, out double lon, out double clockM)
        {
            double t = k * 0.1;
            double east = 3 * t, north = 0.02 * t * t;
            lat = Lat + north / 111132.0;
            lon = Lon + east / (111320.0 * Math.Cos(Lat * Math.PI / 180));
            clockM = 1000 + 100 * t + (k % 3) * 0.01;
        }

        private static ObsEpoch Observe(List<Ephemeris> ephs, int k, Func<int, double> extraM = null, Func<int, double> lockMs = null)
        {
            Truth(k, out double lat, out double lon, out double clockM);
            Ecef rx = GnssOrbit.FromLatLon(lat, lon, Height);
            double tow = Tow0 + k * 0.1;
            var epoch = new ObsEpoch { GpsTowS = tow };
            foreach (Ephemeris e in ephs)
            {
                double r = GnssOrbit.Range(e, tow, rx, out double satClock, out _);
                double ambiguity = 0.19 * 1000 * e.Prn;   // whole cycles never seen by TDCP
                epoch.Obs.Add(new PhaseObs
                {
                    System = e.System,
                    Prn = e.Prn,
                    PhaseRangeM = r - GnssOrbit.C * satClock + clockM + ambiguity + (extraM?.Invoke(e.Prn) ?? 0),
                    LockMs = lockMs?.Invoke(e.Prn) ?? 20000 + k * 100,
                    Cn0 = 45,
                });
            }
            return epoch;
        }

        private static void TrueMove(int k, out double east, out double north)
        {
            Truth(k - 1, out double la0, out double lo0, out _);
            Truth(k, out double la1, out double lo1, out _);
            Ecef d = GnssOrbit.FromLatLon(la1, lo1, Height) - GnssOrbit.FromLatLon(la0, lo0, Height);
            GnssOrbit.ToEnu(d, Lat, Lon, out east, out north, out _);
        }

        // a satellite well above the horizon (so the solver uses it)
        private static int HighSatellite(List<Ephemeris> ephs) => ephs.First(e =>
        {
            GnssOrbit.Range(e, Tow0, GnssOrbit.FromLatLon(Lat, Lon, Height), out _, out Ecef u);
            return GnssOrbit.Elevation(u, Lat, Lon) > 20 * Math.PI / 180;
        }).Prn;

        private static TdcpSolver Solver(List<Ephemeris> ephs)
        {
            var s = new TdcpSolver();
            foreach (Ephemeris e in ephs) s.SetEphemeris(e);
            s.SetPosition(Lat + 2 / 111132.0, Lon, Height + 3);   // a few metres off, as from a float solution
            return s;
        }

        [Test]
        public void Orbit_GpsSatelliteIsAtItsOrbitRadius()
        {
            foreach (Ephemeris e in Constellation())
            {
                Ecef p = GnssOrbit.Position(e, Tow0, out double clock);
                Assert.That(p.Norm, Is.EqualTo(26_560_000).Within(300_000));
                Assert.That(Math.Abs(clock), Is.LessThan(1e-3));
            }
            Assert.That(Visible(Constellation(), GnssOrbit.FromLatLon(Lat, Lon, Height)), Is.GreaterThanOrEqualTo(6));
        }

        [Test]
        public void Solver_RecoversTheMovementToMillimetres()
        {
            List<Ephemeris> ephs = Constellation();
            TdcpSolver s = Solver(ephs);
            Assert.That(s.Process(Observe(ephs, 0)).Reason, Is.EqualTo("first epoch"));
            for (int k = 1; k <= 50; k++)
            {
                TdcpResult r = s.Process(Observe(ephs, k));
                Assert.That(r.Ok, Is.True, r.Reason);
                TrueMove(k, out double e, out double n);
                Assert.That(r.East, Is.EqualTo(e).Within(0.002));
                Assert.That(r.North, Is.EqualTo(n).Within(0.002));
                Assert.That(r.Sats, Is.GreaterThanOrEqualTo(5));
            }
        }

        [Test]
        public void Solver_LeavesOutASatelliteWithACycleSlip()
        {
            List<Ephemeris> ephs = Constellation();
            TdcpSolver s = Solver(ephs);
            s.Process(Observe(ephs, 0));
            TdcpResult ok = s.Process(Observe(ephs, 1));
            int visibleSlip = HighSatellite(ephs);
            // that satellite lost lock and its phase jumped 3 cycles
            TdcpResult r = s.Process(Observe(ephs, 2, p => p == visibleSlip ? 3 * 0.19 : 0, p => p == visibleSlip ? 0 : 30000));
            Assert.That(r.Ok, Is.True, r.Reason);
            Assert.That(r.Slips, Is.EqualTo(1));
            Assert.That(r.Sats, Is.EqualTo(ok.Sats - 1));
            TrueMove(2, out double e2, out double n2);
            Assert.That(r.East, Is.EqualTo(e2).Within(0.002));
            Assert.That(r.North, Is.EqualTo(n2).Within(0.002));
        }

        [Test]
        public void Solver_DropsAnOutlierTheLockTimeMissed()
        {
            List<Ephemeris> ephs = Constellation();
            TdcpSolver s = Solver(ephs);
            s.Process(Observe(ephs, 0));
            TdcpResult ok = s.Process(Observe(ephs, 1));
            int high = HighSatellite(ephs);
            // multipath / a missed slip: 20 cm on one satellite, the lock time looks fine
            TdcpResult r = s.Process(Observe(ephs, 2, p => p == high ? 0.2 : 0));
            Assert.That(r.Ok, Is.True, r.Reason);
            Assert.That(r.Sats, Is.LessThan(ok.Sats));
            TrueMove(2, out double e2, out double n2);
            Assert.That(r.East, Is.EqualTo(e2).Within(0.003));
            Assert.That(r.North, Is.EqualTo(n2).Within(0.003));
        }

        [Test]
        public void Solver_RefusesWithTooFewSatellitesOrAGap()
        {
            List<Ephemeris> ephs = Constellation().Take(3).ToList();
            TdcpSolver s = Solver(ephs);
            s.Process(Observe(ephs, 0));
            TdcpResult r = s.Process(Observe(ephs, 1));
            Assert.That(r.Ok, Is.False);

            List<Ephemeris> all = Constellation();
            TdcpSolver g = Solver(all);
            g.Process(Observe(all, 0));
            Assert.That(g.Process(Observe(all, 30)).Reason, Is.EqualTo("gap"));   // 3 s later
        }

        [Test]
        public void Solver_WithoutEphemeridesOrPositionNothingComesOut()
        {
            List<Ephemeris> ephs = Constellation();
            var s = new TdcpSolver();
            foreach (Ephemeris e in ephs) s.SetEphemeris(e);
            Assert.That(s.Process(Observe(ephs, 0)).Reason, Is.EqualTo("no position"));

            var noEph = new TdcpSolver();
            noEph.SetPosition(Lat, Lon, Height);
            noEph.Process(Observe(ephs, 0));
            Assert.That(noEph.Process(Observe(ephs, 1)).Ok, Is.False);
        }

        // ---- RTCM frames ----

        [Test]
        public void Frames_ComeOutOfMixedNmeaAndBinary()
        {
            byte[] f1 = Rtcm3Frames.Wrap(new byte[] { 0x3F, 0x20, 1, 2, 3 });
            byte[] f2 = Rtcm3Frames.Wrap(new byte[40]);
            byte[] nmea = System.Text.Encoding.ASCII.GetBytes("$GNGGA,1,2,3*00\r\n");
            byte[] broken = (byte[])f1.Clone();
            broken[4] ^= 0xFF;
            var stream = nmea.Concat(f1).Concat(nmea).Concat(broken).Concat(f2).Concat(nmea).ToArray();

            var splitter = new Rtcm3Frames();
            var frames = new List<byte[]>();
            var text = new List<byte>();
            // in odd-sized pieces, as a serial port delivers them
            for (int i = 0; i < stream.Length; i += 7)
            {
                int n = Math.Min(7, stream.Length - i);
                var piece = new byte[n];
                Array.Copy(stream, i, piece, 0, n);
                splitter.Feed(piece, n, frames, text);
            }
            Assert.That(frames.Count, Is.EqualTo(2));
            Assert.That(frames[0], Is.EqualTo(f1));
            Assert.That(frames[1], Is.EqualTo(f2));
            string s = System.Text.Encoding.ASCII.GetString(text.Where(b => b < 128).ToArray());
            Assert.That(s.Split(new[] { "$GNGGA" }, StringSplitOptions.None).Length - 1, Is.EqualTo(3));
            Assert.That(Rtcm3Frames.CheckCrc(f1), Is.True);
            Assert.That(Rtcm3Frames.CheckCrc(broken), Is.False);
        }

        // ---- encoders for the decoder round trip ----

        private sealed class Writer
        {
            public readonly byte[] Buf = new byte[1000];
            public int Pos;
            public void Put(int len, long v) { Bits.Put(Buf, Pos, len, v); Pos += len; }
            public byte[] Frame() => Rtcm3Frames.Wrap(Buf.Take((Pos + 7) / 8).ToArray());
        }

        private static byte[] Encode1019(Ephemeris e)
        {
            const double Sc = Math.PI;
            var w = new Writer();
            w.Put(12, 1019);
            w.Put(6, e.Prn);
            w.Put(10, 300);
            w.Put(4, 0);
            w.Put(2, 1);
            w.Put(14, (long)Math.Round(e.Idot / Sc / Math.Pow(2, -43)));
            w.Put(8, e.Iode);
            w.Put(16, (long)(e.Toc / 16));
            w.Put(8, (long)Math.Round(e.Af2 / Math.Pow(2, -55)));
            w.Put(16, (long)Math.Round(e.Af1 / Math.Pow(2, -43)));
            w.Put(22, (long)Math.Round(e.Af0 / Math.Pow(2, -31)));
            w.Put(10, e.Iode);
            w.Put(16, (long)Math.Round(e.Crs / Math.Pow(2, -5)));
            w.Put(16, (long)Math.Round(e.DeltaN / Sc / Math.Pow(2, -43)));
            w.Put(32, (long)Math.Round(e.M0 / Sc / Math.Pow(2, -31)));
            w.Put(16, (long)Math.Round(e.Cuc / Math.Pow(2, -29)));
            w.Put(32, (long)Math.Round(e.E / Math.Pow(2, -33)));
            w.Put(16, (long)Math.Round(e.Cus / Math.Pow(2, -29)));
            w.Put(32, (long)Math.Round(e.SqrtA / Math.Pow(2, -19)));
            w.Put(16, (long)(e.Toe / 16));
            w.Put(16, (long)Math.Round(e.Cic / Math.Pow(2, -29)));
            w.Put(32, (long)Math.Round(Norm(e.Omega0) / Sc / Math.Pow(2, -31)));
            w.Put(16, (long)Math.Round(e.Cis / Math.Pow(2, -29)));
            w.Put(32, (long)Math.Round(e.I0 / Sc / Math.Pow(2, -31)));
            w.Put(16, (long)Math.Round(e.Crc / Math.Pow(2, -5)));
            w.Put(32, (long)Math.Round(Norm(e.Omega) / Sc / Math.Pow(2, -31)));
            w.Put(24, (long)Math.Round(e.OmegaDot / Sc / Math.Pow(2, -43)));
            w.Put(8, 0);
            w.Put(6, e.Healthy ? 0 : 1);
            w.Put(1, 0);
            w.Put(1, 0);
            return w.Frame();
        }

        // angles into -pi..pi, as the signed fields hold them
        private static double Norm(double a) => Math.IEEERemainder(a, 2 * Math.PI);

        // MSM4 with one signal (L1 C/A, mask number 2) per satellite
        private static byte[] EncodeMsm4(int type, double towS, IList<PhaseObs> obs, bool more, int lockIndicator = 10, long raw = -1)
        {
            var w = new Writer();
            w.Put(12, type);
            w.Put(12, 0);
            w.Put(30, raw >= 0 ? raw : (long)Math.Round(towS * 1000));
            w.Put(1, more ? 1 : 0);
            w.Put(3 + 7 + 2 + 2 + 1 + 3, 0);
            var prns = obs.Select(o => o.Prn).ToList();
            for (int k = 1; k <= 64; k++) w.Put(1, prns.Contains(k) ? 1 : 0);
            for (int k = 1; k <= 32; k++) w.Put(1, k == 2 ? 1 : 0);
            var sorted = obs.OrderBy(o => o.Prn).ToList();
            foreach (PhaseObs o in sorted) w.Put(1, 1);
            var ms = sorted.Select(o => o.PhaseRangeM / LightMsM).ToList();
            var whole = ms.Select(m => Math.Floor(m)).ToList();
            var mod = ms.Select((m, i) => Math.Floor((m - whole[i]) * 1024) / 1024).ToList();
            foreach (double v in whole) w.Put(8, (long)v);
            foreach (double v in mod) w.Put(10, (long)Math.Round(v * 1024));
            foreach (PhaseObs o in sorted) w.Put(15, 0);
            for (int i = 0; i < sorted.Count; i++) w.Put(22, (long)Math.Round((ms[i] - whole[i] - mod[i]) / Math.Pow(2, -29)));
            foreach (PhaseObs o in sorted) w.Put(4, lockIndicator);
            foreach (PhaseObs o in sorted) w.Put(1, o.HalfCycle ? 1 : 0);
            foreach (PhaseObs o in sorted) w.Put(6, (long)o.Cn0);
            return w.Frame();
        }

        [Test]
        public void Decoder_ReadsBackAGpsEphemeris()
        {
            Ephemeris src = Constellation()[5];
            src.Iode = 77;
            src.Crs = 12.5;
            src.Cuc = 1e-6;
            Ephemeris got = null;
            var d = new RtcmDecoder();
            d.EphemerisReady += e => got = e;
            d.Decode(Encode1019(src));
            Assert.That(got, Is.Not.Null);
            Assert.That(got.Prn, Is.EqualTo(src.Prn));
            Assert.That(got.Iode, Is.EqualTo(77));
            Assert.That(got.SqrtA, Is.EqualTo(src.SqrtA).Within(1e-5));
            Assert.That(got.E, Is.EqualTo(src.E).Within(1e-9));
            Assert.That(got.Toe, Is.EqualTo(src.Toe));
            Assert.That(got.Crs, Is.EqualTo(12.5).Within(0.05));
            Assert.That(got.Healthy, Is.True);
            // the decoded orbit puts the satellite where the original one is, to centimetres
            Ecef a = GnssOrbit.Position(src, Tow0, out _), b = GnssOrbit.Position(got, Tow0, out _);
            Assert.That((a - b).Norm, Is.LessThan(1.0));
        }

        [Test]
        public void Decoder_GathersTheSystemsOfOneMomentIntoOneEpoch()
        {
            var gps = new List<PhaseObs>
            {
                new PhaseObs { Prn = 3, PhaseRangeM = 21_345_678.123, Cn0 = 44 },
                new PhaseObs { Prn = 17, PhaseRangeM = 24_000_001.5, Cn0 = 38, HalfCycle = true },
            };
            var gal = new List<PhaseObs> { new PhaseObs { Prn = 11, PhaseRangeM = 25_111_222.333, Cn0 = 41 } };
            var bds = new List<PhaseObs> { new PhaseObs { Prn = 30, PhaseRangeM = 22_222_222.222, Cn0 = 40 } };
            var epochs = new List<ObsEpoch>();
            var d = new RtcmDecoder();
            d.EpochReady += e => epochs.Add(e);

            d.Decode(EncodeMsm4(1074, 1234.5, gps, true));
            d.Decode(EncodeMsm4(1094, 1234.5, gal, true));
            d.Decode(EncodeMsm4(1124, 1234.5 - 14, bds, false));   // BeiDou time runs 14 s behind
            Assert.That(epochs.Count, Is.EqualTo(1));
            ObsEpoch ep = epochs[0];
            Assert.That(ep.GpsTowS, Is.EqualTo(1234.5).Within(1e-6));
            Assert.That(ep.Obs.Count, Is.EqualTo(4));
            PhaseObs g17 = ep.Obs.Single(o => o.System == GnssSystem.Gps && o.Prn == 17);
            Assert.That(g17.PhaseRangeM, Is.EqualTo(24_000_001.5).Within(0.002));
            Assert.That(g17.HalfCycle, Is.True);
            Assert.That(g17.Cn0, Is.EqualTo(38));
            Assert.That(g17.LockMs, Is.EqualTo(1 << 14));
            Assert.That(ep.Obs.Single(o => o.System == GnssSystem.Galileo).PhaseRangeM, Is.EqualTo(25_111_222.333).Within(0.002));
            Assert.That(ep.Obs.Single(o => o.System == GnssSystem.BeiDou).Prn, Is.EqualTo(30));
        }

        [Test]
        public void Decoder_IgnoresBrokenAndUnknownFrames()
        {
            var d = new RtcmDecoder();
            int n = 0;
            d.EpochReady += e => n++;
            d.Decode(null);
            d.Decode(new byte[] { 0xD3, 0, 1, 2 });
            d.Decode(Rtcm3Frames.Wrap(new byte[] { 0x3F, 0x20 }));        // 1010: not ours
            var msm = EncodeMsm4(1074, 10, new[] { new PhaseObs { Prn = 1, PhaseRangeM = 2e7 } }, false);
            d.Decode(Rtcm3Frames.Wrap(msm.Skip(3).Take(6).ToArray()));   // cut short
            Assert.That(n, Is.EqualTo(0));
        }

        // ---- the whole chain: frames in, movement out ----

        [Test]
        public void Engine_FromFramesToTheMovementOfEachFix()
        {
            List<Ephemeris> ephs = Constellation();
            // what the receiver broadcasts, read back as the engine will
            var decoded = new List<Ephemeris>();
            var dec = new RtcmDecoder();
            dec.EphemerisReady += e => decoded.Add(e);
            var ephFrames = ephs.Select(Encode1019).ToList();
            foreach (byte[] f in ephFrames) dec.Decode(f);
            Assert.That(decoded.Count, Is.EqualTo(ephs.Count));

            var engine = new TdcpEngine();
            engine.SetPosition(Lat, Lon, Height);
            foreach (byte[] f in ephFrames) engine.Feed(f);
            Assert.That(engine.EphemerisCount, Is.EqualTo(ephs.Count));

            double sumE = 0, sumN = 0, gotE = 0, gotN = 0;
            for (int k = 0; k <= 20; k++)
            {
                ObsEpoch ep = Observe(decoded, k);
                engine.Feed(EncodeMsm4(1074, ep.GpsTowS, ep.Obs, false));
                if (k > 0)
                {
                    TrueMove(k, out double e, out double n);
                    sumE += e;
                    sumN += n;
                }
                if (k % 2 == 0)   // the fix loop runs slower than the observations
                {
                    engine.Take(out double te, out double tn, out bool valid);
                    if (k == 0) { Assert.That(valid, Is.False); continue; }
                    Assert.That(valid, Is.True, engine.Last?.Reason);
                    gotE += te;
                    gotN += tn;
                }
            }
            Assert.That(engine.Alive, Is.True);
            Assert.That(engine.GoodEpochs, Is.EqualTo(20));
            Assert.That(gotE, Is.EqualTo(sumE).Within(0.01));
            Assert.That(gotN, Is.EqualTo(sumN).Within(0.01));
            engine.Take(out _, out _, out bool none);
            Assert.That(none, Is.False);   // nothing new since the last take
        }

        // ---- GLONASS ----

        private const double MuGlo = 3.9860044e14, WGlo = 7.292115e-5, RGlo = 25_510_000;

        // a circular orbit's state vector at Toe, in the Earth-fixed frame of that moment
        private static Ephemeris GloSat(int slot, double nodeRad, double argRad, double toeTod, double incRad = 64.8 * Math.PI / 180)
        {
            double v = Math.Sqrt(MuGlo / RGlo);
            double cO = Math.Cos(nodeRad), sO = Math.Sin(nodeRad), cu = Math.Cos(argRad), su = Math.Sin(argRad), ci = Math.Cos(incRad), si = Math.Sin(incRad);
            double x = RGlo * (cO * cu - sO * su * ci), y = RGlo * (sO * cu + cO * su * ci), z = RGlo * su * si;
            double vx = v * (-cO * su - sO * cu * ci), vy = v * (-sO * su + cO * cu * ci), vz = v * cu * si;
            return new Ephemeris
            {
                System = GnssSystem.Glonass,
                Prn = slot,
                Toe = toeTod,
                Toc = toeTod,
                X = x,
                Y = y,
                Z = z,
                Vx = vx + WGlo * y,
                Vy = vy - WGlo * x,
                Vz = vz,
                TauN = 1e-5 * slot,
                GammaN = 1e-12,
            };
        }

        private static List<Ephemeris> GloConstellation()
        {
            var list = new List<Ephemeris>();
            double toe = Tow0 % 86400 + 300;
            for (int plane = 0; plane < 3; plane++)
                for (int k = 0; k < 8; k++)
                    list.Add(GloSat(plane * 8 + k + 1, plane * 2 * Math.PI / 3, k * Math.PI / 4 + plane * Math.PI / 12, toe));
            return list;
        }

        [Test]
        public void Glonass_IntegratedOrbitMatchesTheKeplerOrbit()
        {
            Ephemeris e = GloSat(1, 0.4, 0.2, 1000, 1.1);
            const double Dt = 900;   // a quarter of an hour: as far as a GLONASS ephemeris is used
            Ecef p = GnssOrbit.Position(e, 1000 + Dt, out double clock);
            // the same circle without J2, turned into the Earth-fixed frame of that moment
            double v = Math.Sqrt(MuGlo / RGlo), n = v / RGlo, u = 0.2 + n * Dt;
            double cO = Math.Cos(0.4), sO = Math.Sin(0.4), ci = Math.Cos(1.1), si = Math.Sin(1.1);
            double xi = RGlo * (cO * Math.Cos(u) - sO * Math.Sin(u) * ci), yi = RGlo * (sO * Math.Cos(u) + cO * Math.Sin(u) * ci);
            double zi = RGlo * Math.Sin(u) * si, w = WGlo * Dt;
            var expected = new Ecef(xi * Math.Cos(w) + yi * Math.Sin(w), -xi * Math.Sin(w) + yi * Math.Cos(w), zi);
            Assert.That((p - expected).Norm, Is.LessThan(100));   // J2 moves it a few tens of metres in this time
            Assert.That(clock, Is.EqualTo(-1e-5 + 1e-12 * Dt).Within(1e-15));
            // there and back again
            var back = new Ephemeris { System = GnssSystem.Glonass, Toe = 1000 + Dt, X = p.X, Y = p.Y, Z = p.Z };
            Ecef q0 = GnssOrbit.Position(e, 1000 + Dt + 1, out _), q1 = GnssOrbit.Position(e, 1000 + Dt - 1, out _);
            back.Vx = (q0.X - q1.X) / 2;
            back.Vy = (q0.Y - q1.Y) / 2;
            back.Vz = (q0.Z - q1.Z) / 2;
            Ecef start = GnssOrbit.Position(back, 1000, out _);
            Assert.That((start - new Ecef(e.X, e.Y, e.Z)).Norm, Is.LessThan(5));
            // over midnight: the time of day wraps
            var late = GloSat(2, 0.4, 0.2, 86300, 1.1);
            Assert.That((GnssOrbit.Position(late, 86300 + Dt - 86400, out _) - GnssOrbit.Position(late, 86300 + Dt, out _)).Norm, Is.LessThan(1e-6));
        }

        [Test]
        public void Glonass_AloneGivesTheMovementToMillimetres()
        {
            List<Ephemeris> ephs = GloConstellation();
            Assert.That(Visible(ephs, GnssOrbit.FromLatLon(Lat, Lon, Height)), Is.GreaterThanOrEqualTo(5));
            TdcpSolver s = Solver(ephs);
            s.Process(Observe(ephs, 0));
            for (int k = 1; k <= 20; k++)
            {
                TdcpResult r = s.Process(Observe(ephs, k));
                Assert.That(r.Ok, Is.True, r.Reason);
                TrueMove(k, out double e, out double n);
                Assert.That(r.East, Is.EqualTo(e).Within(0.002));
                Assert.That(r.North, Is.EqualTo(n).Within(0.002));
            }
        }

        [Test]
        public void Glonass_AStaleEphemerisIsNotUsed()
        {
            List<Ephemeris> ephs = GloConstellation();
            foreach (Ephemeris e in ephs) e.Toe = e.Toc = (e.Toe + 3 * 3600) % 86400;   // three hours away
            TdcpSolver s = Solver(ephs);
            s.Process(Observe(ephs, 0));
            Assert.That(s.Process(Observe(ephs, 1)).Reason, Is.EqualTo("too few satellites"));
        }

        private static void PutSM(Writer w, int len, double value)
        {
            long m = (long)Math.Round(Math.Abs(value));
            w.Put(1, value < 0 ? 1 : 0);
            w.Put(len - 1, m);
        }

        private static byte[] Encode1020(Ephemeris e, int tbIndex)
        {
            var w = new Writer();
            w.Put(12, 1020);
            w.Put(6, e.Prn);
            w.Put(5, e.FreqChannel + 7);
            w.Put(1 + 1 + 2 + 12, 0);
            w.Put(1, e.Healthy ? 0 : 1);
            w.Put(1, 0);
            w.Put(7, tbIndex);
            double v = Math.Pow(2, -20) * 1000, p = Math.Pow(2, -11) * 1000, a = Math.Pow(2, -30) * 1000;
            PutSM(w, 24, e.Vx / v);
            PutSM(w, 27, e.X / p);
            PutSM(w, 5, e.Ax / a);
            PutSM(w, 24, e.Vy / v);
            PutSM(w, 27, e.Y / p);
            PutSM(w, 5, e.Ay / a);
            PutSM(w, 24, e.Vz / v);
            PutSM(w, 27, e.Z / p);
            PutSM(w, 5, e.Az / a);
            w.Put(1, 0);
            PutSM(w, 11, e.GammaN / Math.Pow(2, -40));
            w.Put(3, 0);
            PutSM(w, 22, e.TauN / Math.Pow(2, -30));
            w.Put(5 + 5 + 1 + 4 + 11 + 2 + 1 + 11 + 32 + 5 + 22 + 1 + 7, 0);
            return w.Frame();
        }

        [Test]
        public void Decoder_ReadsBackAGlonassEphemeris()
        {
            Ephemeris src = GloSat(9, 1.0, 2.0, 0, 1.13);
            src.FreqChannel = -4;
            src.Ax = 2.8e-6;
            src.Az = -1.9e-6;
            Ephemeris got = null;
            var d = new RtcmDecoder();
            d.EphemerisReady += x => got = x;
            d.Decode(Encode1020(src, 41));   // 10:15 Moscow time
            Assert.That(got, Is.Not.Null);
            Assert.That(got.System, Is.EqualTo(GnssSystem.Glonass));
            Assert.That(got.Prn, Is.EqualTo(9));
            Assert.That(got.FreqChannel, Is.EqualTo(-4));
            Assert.That(got.X, Is.EqualTo(src.X).Within(0.5));
            Assert.That(got.Vy, Is.EqualTo(src.Vy).Within(0.001));
            Assert.That(got.Az, Is.EqualTo(src.Az).Within(1e-6));
            Assert.That(got.TauN, Is.EqualTo(src.TauN).Within(1e-9));
            Assert.That(got.Toe, Is.EqualTo(41 * 900 - 10800 + RtcmDecoder.GpsUtcLeapS));   // in GPS time of day
            Assert.That(got.Healthy, Is.True);
        }

        [Test]
        public void Decoder_PutsGlonassObservationsIntoTheSameMoment()
        {
            var epochs = new List<ObsEpoch>();
            var d = new RtcmDecoder();
            d.EpochReady += x => epochs.Add(x);
            var one = new List<PhaseObs> { new PhaseObs { Prn = 5, PhaseRangeM = 20_000_000, Cn0 = 40 } };
            double tow = 2 * 86400 + 1234.5;   // Tuesday, 00:20:34.5 GPS time
            double moscowTod = 1234.5 - RtcmDecoder.GpsUtcLeapS + 10800;

            d.Decode(EncodeMsm4(1074, tow, one, true));
            d.Decode(EncodeMsm4(1084, 0, one, false, raw: (2L << 27) | (long)Math.Round(moscowTod * 1000)));
            Assert.That(epochs.Count, Is.EqualTo(1));
            Assert.That(epochs[0].Obs.Count, Is.EqualTo(2));
            Assert.That(epochs[0].Obs.Count(o => o.System == GnssSystem.Glonass), Is.EqualTo(1));

            // a receiver that does not send the day: the time of day nearest the other systems' epoch
            d.Decode(EncodeMsm4(1074, tow + 0.1, one, true));
            d.Decode(EncodeMsm4(1084, 0, one, false, raw: (7L << 27) | (long)Math.Round((moscowTod + 0.1) * 1000)));
            Assert.That(epochs.Count, Is.EqualTo(2));
            Assert.That(epochs[1].GpsTowS, Is.EqualTo(tow + 0.1).Within(1e-6));
            Assert.That(epochs[1].Obs.Count, Is.EqualTo(2));
        }

        // ---- BeiDou geostationary ----

        [Test]
        public void BeiDouGeo_StaysOverTheEquatorAndInPlace()
        {
            const double Toe = 100800;
            var geo = new Ephemeris
            {
                System = GnssSystem.BeiDou,
                Prn = 3,
                Geo = true,
                Toe = Toe,
                Toc = Toe,
                SqrtA = Math.Sqrt(42_164_170),
                E = 0,
                I0 = 5 * Math.PI / 180,       // GEO elements are given in a frame tilted by 5 degrees
                Omega0 = GnssOrbit.OmegaEarthBds * Toe + Math.PI,
                M0 = 1.0,
            };
            Ecef first = GnssOrbit.Position(geo, Toe + 14 - 1800, out _);
            for (double t = -1800; t <= 1800; t += 600)
            {
                Ecef p = GnssOrbit.Position(geo, Toe + 14 + t, out _);
                Assert.That(Math.Asin(p.Z / p.Norm) * 180 / Math.PI, Is.EqualTo(0).Within(0.5));
                Assert.That(p.Norm, Is.EqualTo(42_164_170).Within(1000));
                Assert.That((p - first).Norm, Is.LessThan(30_000));   // hangs over one place on the Earth
            }
            // the same elements through the ordinary formula would put it far off the equator
            geo.Geo = false;
            Ecef wrong = GnssOrbit.Position(geo, Toe + 14 + 1800, out _);
            Assert.That(Math.Abs(Math.Asin(wrong.Z / wrong.Norm) * 180 / Math.PI), Is.GreaterThan(1));
        }

        // ---- the bridge with TDCP ----

        [Test]
        public void Bridge_WithTdcpBridgesWithoutAnyHeadingSensor()
        {
            var bridge = new CRtkBridge { MaxSeconds = 120, MaxErrorCm = 50, UseExternal = true };
            const double Dt = 0.1;
            double t = 0, e = 0, n = 0, heading = 0, worst = 0;
            for (; t < 60; t += Dt, n += 3 * Dt)
            {
                bridge.SetExternalStep(0, 3 * Dt, true);
                bridge.Step(t, true, 4, e, n, 3, 0, double.NaN, double.NaN, 0, out _, out _);
            }
            // RTK gone; the tractor turns slowly; the receiver's float is 0.5 m off
            // (e, n) is already where the tractor is at t; (de, dn) is the movement that brought it there
            double de = 0, dn = 3 * Dt;
            for (double end = t + 40; t < end; t += Dt, heading += 0.004, de = 3 * Dt * Math.Sin(heading), dn = 3 * Dt * Math.Cos(heading), e += de, n += dn)
            {
                bridge.SetExternalStep(de, dn, true);
                bridge.Step(t, false, 5, e + 0.5, n - 0.3, 3, heading * 180 / Math.PI, double.NaN, double.NaN, 0, out double be, out double bn);
                Assert.That(bridge.Active, Is.True);
                worst = Math.Max(worst, Math.Sqrt((be - e) * (be - e) + (bn - n) * (bn - n)));
            }
            Assert.That(worst, Is.LessThan(0.05));
        }

        [Test]
        public void Bridge_WhenTdcpStopsDeadReckoningCarriesOn()
        {
            var bridge = new CRtkBridge { MaxSeconds = 120, MaxErrorCm = 50, UseExternal = true };
            const double Dt = 0.1;
            double t = 0, n = 0, worst = 0;
            for (; t < 60; t += Dt, n += 3 * Dt) bridge.Step(t, true, 4, 0, n, 3, 0, double.NaN, 0, 3, out _, out _);
            for (double end = t + 20; t < end; t += Dt, n += 3 * Dt)
            {
                bridge.SetExternalStep(double.NaN, 0, (int)(t * 10) % 2 == 0);   // garbage or nothing
                bridge.Step(t, false, 5, 0.5, n - 0.3, 3, 0, double.NaN, 0, 3, out double be, out double bn);
                Assert.That(bridge.Active, Is.True);
                worst = Math.Max(worst, Math.Sqrt(be * be + (bn - n) * (bn - n)));
            }
            Assert.That(worst, Is.LessThan(0.05));
        }
    }
}
