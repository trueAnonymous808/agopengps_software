using System;

namespace AgOpenGPS.Tdcp
{
    /// <summary>Earth-centred, Earth-fixed coordinates (metres).</summary>
    public struct Ecef
    {
        public double X, Y, Z;
        public Ecef(double x, double y, double z) { X = x; Y = y; Z = z; }
        public static Ecef operator -(Ecef a, Ecef b) => new Ecef(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Ecef operator +(Ecef a, Ecef b) => new Ecef(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public double Norm => Math.Sqrt(X * X + Y * Y + Z * Z);
    }

    /// <summary>Satellite positions and clocks from broadcast ephemerides; WGS-84 helpers.</summary>
    public static class GnssOrbit
    {
        public const double C = 299792458.0;
        public const double OmegaEarth = 7.2921151467e-5;
        public const double MuGps = 3.9860050e14, MuGal = 3.986004418e14, MuBds = 3.986004418e14;
        public const double OmegaEarthBds = 7.2921150e-5;
        private const double F = -4.442807633e-10;         // relativistic clock term, s/m^0.5
        private const double WgsA = 6378137.0, WgsF = 1 / 298.257223563;

        /// <summary>Time difference within a week, wrapped to half a week.</summary>
        public static double Wrap(double dt)
        {
            if (dt > 302400) dt -= 604800;
            else if (dt < -302400) dt += 604800;
            return dt;
        }

        /// <summary>
        /// The satellite's position (ECEF at the time it sent the signal) and its clock offset (s, with the
        /// relativistic term). t: GPS time of week of the transmission (BeiDou ephemerides are in BeiDou time).
        /// </summary>
        public static Ecef Position(Ephemeris e, double tGps, out double clockS)
        {
            if (e.System == GnssSystem.Glonass) return GlonassPosition(e, tGps, out clockS);
            double t = e.System == GnssSystem.BeiDou ? tGps - 14 : tGps;
            double mu = e.System == GnssSystem.Gps ? MuGps : e.System == GnssSystem.Galileo ? MuGal : MuBds;
            double we = e.System == GnssSystem.BeiDou ? OmegaEarthBds : OmegaEarth;

            double a = e.SqrtA * e.SqrtA;
            double tk = Wrap(t - e.Toe);
            double n = Math.Sqrt(mu / (a * a * a)) + e.DeltaN;
            double m = e.M0 + n * tk;
            double ek = m;
            for (int i = 0; i < 30; i++)
            {
                double next = m + e.E * Math.Sin(ek);
                if (Math.Abs(next - ek) < 1e-14) { ek = next; break; }
                ek = next;
            }
            double sinE = Math.Sin(ek), cosE = Math.Cos(ek);
            double v = Math.Atan2(Math.Sqrt(1 - e.E * e.E) * sinE, cosE - e.E);
            double phi = v + e.Omega;
            double s2 = Math.Sin(2 * phi), c2 = Math.Cos(2 * phi);
            double u = phi + e.Cus * s2 + e.Cuc * c2;
            double r = a * (1 - e.E * cosE) + e.Crs * s2 + e.Crc * c2;
            double inc = e.I0 + e.Cis * s2 + e.Cic * c2 + e.Idot * tk;
            double xp = r * Math.Cos(u), yp = r * Math.Sin(u);
            double dtc = Wrap(t - e.Toc);
            clockS = e.Af0 + e.Af1 * dtc + e.Af2 * dtc * dtc + F * e.E * e.SqrtA * sinE;

            if (e.Geo)
            {
                // BeiDou geostationary (BDS ICD): the orbit in an inertial frame tilted by 5 degrees, then turned
                // into the Earth-fixed frame
                double omg = e.Omega0 + e.OmegaDot * tk - we * e.Toe;
                double cg = Math.Cos(omg), sg = Math.Sin(omg), cig = Math.Cos(inc), sig = Math.Sin(inc);
                double xg = xp * cg - yp * cig * sg, yg = xp * sg + yp * cig * cg, zg = yp * sig;
                double c5 = Math.Cos(-5 * Math.PI / 180), s5 = Math.Sin(-5 * Math.PI / 180);
                double y1 = c5 * yg + s5 * zg, z1 = -s5 * yg + c5 * zg;
                double cw = Math.Cos(we * tk), sw = Math.Sin(we * tk);
                return new Ecef(cw * xg + sw * y1, -sw * xg + cw * y1, z1);
            }

            double om = e.Omega0 + (e.OmegaDot - we) * tk - we * e.Toe;
            double co = Math.Cos(om), so = Math.Sin(om), ci = Math.Cos(inc), si = Math.Sin(inc);
            return new Ecef(xp * co - yp * ci * so, xp * so + yp * ci * co, yp * si);
        }

        /// <summary>Whether an ephemeris is fresh enough for a time (GLONASS ones last half an hour, others hours).</summary>
        public static bool Usable(Ephemeris e, double tGps)
        {
            if (e.System == GnssSystem.Glonass) return Math.Abs(DayWrap(tGps - e.Toe)) <= 5400;
            double t = e.System == GnssSystem.BeiDou ? tGps - 14 : tGps;
            return Math.Abs(Wrap(t - e.Toe)) <= 4 * 3600;
        }

        /// <summary>A time difference within a day, wrapped to half a day.</summary>
        public static double DayWrap(double dt) => dt - 86400 * Math.Round(dt / 86400);

        // ---- GLONASS: the broadcast state vector carried along by numerical integration (GLONASS ICD) ----

        private const double MuGlo = 3.9860044e14, AeGlo = 6378136.0, J2Glo = 1.0826257e-3, OmegaGlo = 7.292115e-5;

        private static Ecef GlonassPosition(Ephemeris e, double tGps, out double clockS)
        {
            double dt = DayWrap(tGps - e.Toe);
            clockS = -e.TauN + e.GammaN * dt;
            var s = new[] { e.X, e.Y, e.Z, e.Vx, e.Vy, e.Vz };
            double left = dt;
            while (Math.Abs(left) > 1e-9)
            {
                double h = Math.Abs(left) <= 30 ? left : Math.Sign(left) * 30;
                Rk4(s, h, e);
                left -= h;
            }
            return new Ecef(s[0], s[1], s[2]);
        }

        private static void Rk4(double[] s, double h, Ephemeris e)
        {
            double[] k1 = Deriv(s, e);
            double[] k2 = Deriv(Add(s, k1, h / 2), e);
            double[] k3 = Deriv(Add(s, k2, h / 2), e);
            double[] k4 = Deriv(Add(s, k3, h), e);
            for (int i = 0; i < 6; i++) s[i] += h / 6 * (k1[i] + 2 * k2[i] + 2 * k3[i] + k4[i]);
        }

        private static double[] Add(double[] s, double[] k, double h)
        {
            var r = new double[6];
            for (int i = 0; i < 6; i++) r[i] = s[i] + k[i] * h;
            return r;
        }

        // Earth's gravity with J2, the turning Earth-fixed frame, and the broadcast luni-solar acceleration
        private static double[] Deriv(double[] s, Ephemeris e)
        {
            double x = s[0], y = s[1], z = s[2], vx = s[3], vy = s[4], vz = s[5];
            double r2 = x * x + y * y + z * z, r = Math.Sqrt(r2), r3 = r2 * r, r5 = r3 * r2;
            double mu = MuGlo / r3, j = 1.5 * J2Glo * MuGlo * AeGlo * AeGlo / r5, z2 = 5 * z * z / r2, w2 = OmegaGlo * OmegaGlo;
            return new[]
            {
                vx, vy, vz,
                -mu * x - j * x * (1 - z2) + w2 * x + 2 * OmegaGlo * vy + e.Ax,
                -mu * y - j * y * (1 - z2) + w2 * y - 2 * OmegaGlo * vx + e.Ay,
                -mu * z - j * z * (3 - z2) + e.Az,
            };
        }

        /// <summary>
        /// The geometric range from a receiver at a reception time (GPS time of week), with the signal's travel time
        /// and the Earth turning under it while it travels. Also the satellite clock (s) and the unit vector to it.
        /// </summary>
        public static double Range(Ephemeris e, double tRx, Ecef rx, out double clockS, out Ecef unit)
        {
            double tau = 0.075;
            Ecef sat = default;
            clockS = 0;
            for (int i = 0; i < 3; i++)
            {
                sat = Position(e, tRx - tau, out clockS);
                // the Earth turns while the signal travels: the satellite's position in today's frame
                double rot = OmegaEarth * tau;
                double x = sat.X * Math.Cos(rot) + sat.Y * Math.Sin(rot);
                double y = -sat.X * Math.Sin(rot) + sat.Y * Math.Cos(rot);
                sat = new Ecef(x, y, sat.Z);
                tau = (sat - rx).Norm / C;
            }
            Ecef d = sat - rx;
            double range = d.Norm;
            unit = new Ecef(d.X / range, d.Y / range, d.Z / range);
            return range;
        }

        public static Ecef FromLatLon(double latDeg, double lonDeg, double h)
        {
            double lat = latDeg * Math.PI / 180, lon = lonDeg * Math.PI / 180;
            double e2 = WgsF * (2 - WgsF);
            double n = WgsA / Math.Sqrt(1 - e2 * Math.Sin(lat) * Math.Sin(lat));
            return new Ecef((n + h) * Math.Cos(lat) * Math.Cos(lon), (n + h) * Math.Cos(lat) * Math.Sin(lon), (n * (1 - e2) + h) * Math.Sin(lat));
        }

        /// <summary>A small ECEF change as east, north, up at a place.</summary>
        public static void ToEnu(Ecef d, double latDeg, double lonDeg, out double east, out double north, out double up)
        {
            double lat = latDeg * Math.PI / 180, lon = lonDeg * Math.PI / 180;
            double sl = Math.Sin(lat), cl = Math.Cos(lat), so = Math.Sin(lon), co = Math.Cos(lon);
            east = -so * d.X + co * d.Y;
            north = -sl * co * d.X - sl * so * d.Y + cl * d.Z;
            up = cl * co * d.X + cl * so * d.Y + sl * d.Z;
        }

        /// <summary>Elevation (rad) of a direction seen from a place.</summary>
        public static double Elevation(Ecef unit, double latDeg, double lonDeg)
        {
            ToEnu(unit, latDeg, lonDeg, out double e, out double n, out double u);
            return Math.Atan2(u, Math.Sqrt(e * e + n * n));
        }
    }
}
