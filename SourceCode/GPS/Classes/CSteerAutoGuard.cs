using System;
using System.Globalization;

namespace AgOpenGPS
{
    /// <summary>
    /// The steering values as they were when fully automatic tuning was switched on: the limits the automatic changes
    /// stay within, and what "restore" goes back to. Stored as one line of text in the settings.
    /// </summary>
    public class SteerBaseline
    {
        public int Kp, MinPwm, Cpd, Ackermann, WasOffset, DeadZoneHeading, DeadZoneDelay;
        public double Hold, SpeedFactor, SideHill;
        public double RollZero = double.NaN, AcquireFactor = double.NaN, Integral = double.NaN, UTurnComp = double.NaN;
        public double FusionWeight = double.NaN, LinMiddle = double.NaN, LinBig = double.NaN, KpFast = double.NaN;
        public double RollShift = double.NaN, RollGain = double.NaN, PitRate = double.NaN;

        /// <summary>False for a baseline stored before the roll timing, the roll height factor and pit mode were tuned.</summary>
        public bool HasExtras3 => !double.IsNaN(RollShift) && !double.IsNaN(RollGain) && !double.IsNaN(PitRate);

        /// <summary>False for a baseline stored before fusion, linearity and the fast Kp were tuned.</summary>
        public bool HasExtras2 => !double.IsNaN(FusionWeight) && !double.IsNaN(LinMiddle) && !double.IsNaN(LinBig) && !double.IsNaN(KpFast);

        /// <summary>False for a baseline stored before roll zero, acquire, integral and U-turn were tuned.</summary>
        public bool HasExtras => !double.IsNaN(RollZero) && !double.IsNaN(AcquireFactor) && !double.IsNaN(Integral) && !double.IsNaN(UTurnComp);

        public string Save()
        {
            var c = CultureInfo.InvariantCulture;
            return string.Join(";", Kp.ToString(c), MinPwm.ToString(c), Cpd.ToString(c), Ackermann.ToString(c), WasOffset.ToString(c),
                DeadZoneHeading.ToString(c), DeadZoneDelay.ToString(c), Hold.ToString("R", c), SpeedFactor.ToString("R", c), SideHill.ToString("R", c),
                RollZero.ToString("R", c), AcquireFactor.ToString("R", c), Integral.ToString("R", c), UTurnComp.ToString("R", c),
                FusionWeight.ToString("R", c), LinMiddle.ToString("R", c), LinBig.ToString("R", c), KpFast.ToString("R", c),
                RollShift.ToString("R", c), RollGain.ToString("R", c), PitRate.ToString("R", c));
        }

        /// <summary>The baseline from its stored line; null when there is none or it cannot be read.</summary>
        public static SteerBaseline Load(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return null;
            string[] p = line.Split(';');
            if (p.Length != 10 && p.Length != 14 && p.Length != 18 && p.Length != 21) return null;
            var c = CultureInfo.InvariantCulture;
            try
            {
                return new SteerBaseline
                {
                    Kp = int.Parse(p[0], c),
                    MinPwm = int.Parse(p[1], c),
                    Cpd = int.Parse(p[2], c),
                    Ackermann = int.Parse(p[3], c),
                    WasOffset = int.Parse(p[4], c),
                    DeadZoneHeading = int.Parse(p[5], c),
                    DeadZoneDelay = int.Parse(p[6], c),
                    Hold = double.Parse(p[7], c),
                    SpeedFactor = double.Parse(p[8], c),
                    SideHill = double.Parse(p[9], c),
                    RollZero = p.Length > 10 ? double.Parse(p[10], c) : double.NaN,
                    AcquireFactor = p.Length > 10 ? double.Parse(p[11], c) : double.NaN,
                    Integral = p.Length > 10 ? double.Parse(p[12], c) : double.NaN,
                    UTurnComp = p.Length > 10 ? double.Parse(p[13], c) : double.NaN,
                    FusionWeight = p.Length > 14 ? double.Parse(p[14], c) : double.NaN,
                    LinMiddle = p.Length > 14 ? double.Parse(p[15], c) : double.NaN,
                    LinBig = p.Length > 14 ? double.Parse(p[16], c) : double.NaN,
                    KpFast = p.Length > 14 ? double.Parse(p[17], c) : double.NaN,
                    RollShift = p.Length > 18 ? double.Parse(p[18], c) : double.NaN,
                    RollGain = p.Length > 18 ? double.Parse(p[19], c) : double.NaN,
                    PitRate = p.Length > 18 ? double.Parse(p[20], c) : double.NaN
                };
            }
            catch (FormatException) { return null; }
            catch (OverflowException) { return null; }
        }
    }

    /// <summary>
    /// How far fully automatic tuning may take each value from the baseline: whatever the tuners say, the steering never
    /// drifts further than this without the driver.
    /// </summary>
    public static class CSteerAutoGuard
    {
        public const double CooldownS = 60;          // at most one automatic change a minute, over all tuners
        public const double MinWasStepDeg = 0.2;     // smaller zero corrections are noise, not worth a change

        public static bool KpOk(SteerBaseline b, int kp) => kp >= Math.Max(1, b.Kp * 0.5) && kp <= b.Kp * 2.0 + 1;
        public static bool MinPwmOk(SteerBaseline b, int pwm) => Math.Abs(pwm - b.MinPwm) <= 20;
        public static bool CpdOk(SteerBaseline b, int cpd) => Math.Abs(cpd - b.Cpd) <= b.Cpd * 0.3 + 1;
        public static bool AckermannOk(SteerBaseline b, int ackermann) => Math.Abs(ackermann - b.Ackermann) <= 30;
        public static bool WasOffsetOk(SteerBaseline b, int offset, int cpd) => Math.Abs(offset - b.WasOffset) <= 3 * Math.Max(1, cpd);   // 3°
        public static bool DeadZoneOk(SteerBaseline b, int headingCentiDeg) => headingCentiDeg <= Math.Max(100, 3 * b.DeadZoneHeading);
        public static bool DelayOk(SteerBaseline b, int delay) => Math.Abs(delay - b.DeadZoneDelay) <= 4;
        public static bool HoldOk(SteerBaseline b, double hold) => hold >= b.Hold * 0.5 - 1e-9 && hold <= b.Hold * 1.5 + 1e-9;
        public static bool SpeedFactorOk(SteerBaseline b, double factor) => factor >= b.SpeedFactor * 0.5 - 1e-9 && factor <= b.SpeedFactor * 1.5 + 1e-9;
        public static bool SideHillOk(SteerBaseline b, double comp) => Math.Abs(comp - b.SideHill) <= 0.5 + 1e-9;
        public static bool RollZeroOk(SteerBaseline b, double zero) => Math.Abs(zero - b.RollZero) <= 3 + 1e-9;
        public static bool AcquireOk(SteerBaseline b, double factor) => factor >= b.AcquireFactor * 0.5 - 1e-9 && factor <= b.AcquireFactor * 1.5 + 1e-9;
        public static bool IntegralOk(SteerBaseline b, double gain) => gain >= 0 && gain <= b.Integral + 0.5 + 1e-9;
        public static bool UTurnOk(SteerBaseline b, double comp) => Math.Abs(comp - b.UTurnComp) <= 0.5 + 1e-9;
        public static bool FusionOk(SteerBaseline b, double weight) => weight >= b.FusionWeight * 0.5 - 1e-9 && weight <= b.FusionWeight * 2 + 1e-9;
        public static bool RollShiftOk(SteerBaseline b, double shift) => Math.Abs(shift - b.RollShift) <= 0.3 + 1e-9;
        public static bool RollGainOk(SteerBaseline b, double gain) => gain >= b.RollGain * 0.7 - 1e-9 && gain <= b.RollGain * 1.3 + 1e-9;
        public static bool PitRateOk(SteerBaseline b, double rate) => rate >= b.PitRate * 0.5 - 1e-9 && rate <= b.PitRate * 2 + 1e-9;
        // each linearity factor (1 = linear) at most 0.1 from its starting value (the whole range is 0.85-1.18)
        public static bool LinearityOk(SteerBaseline b, double middle, double big) => Math.Abs(middle - b.LinMiddle) <= 0.1 + 1e-9 && Math.Abs(big - b.LinBig) <= 0.1 + 1e-9;
        public static bool KpFastOk(SteerBaseline b, int kpFast) => kpFast >= Math.Max(1, b.Kp * 0.5) && kpFast <= b.Kp * 2.0 + 1;
    }
}
