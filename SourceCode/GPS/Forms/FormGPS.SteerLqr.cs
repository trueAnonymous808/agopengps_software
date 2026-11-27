using AgLibrary.Logging;
using System;

namespace AgOpenGPS
{
    // LQR steering, the fourth method: a linear state feedback (distance and heading off the line, the angles still on
    // their way through the dead time) with gains from the Riccati equation, the path's curvature fed forward. Calmer
    // than MPC, cheaper to work out; it shares MPC's steering style, the learnt offset and the state estimator.
    public partial class FormGPS
    {
        /// <summary>LQR steering chosen (kept with the implement, as the others).</summary>
        public bool isLqrUsed;

        public bool LqrActive => isLqrUsed && !isStanleyUsed && !isMpcUsed;

        /// <summary>Hybrid steering chosen: LQR holds the line, MPC takes the curves and coming onto the line.</summary>
        public bool isHybridUsed;

        public bool HybridActive => isHybridUsed && !isStanleyUsed && !isMpcUsed && !isLqrUsed;

        /// <summary>A model based method steers (MPC, LQR or the hybrid of the two): it knows the dead time itself, no lead on top.</summary>
        public bool ModelSteers => MpcActive || LqrActive || HybridActive;

        private readonly CHybridBlend hybridBlend = new CHybridBlend();

        /// <summary>MPC's share of the hybrid's angle now (0: LQR alone, 1: MPC alone).</summary>
        public double HybridMpcShare => hybridBlend.MpcShare;

        private readonly CSteerLqr lqr = new CSteerLqr();
        private const double LqrStepS = 0.1;

        /// <summary>The angle the model based method steers with (degrees, without the side-hill part); NaN: keep pure pursuit's.</summary>
        public double ModelSteerDeg() => MpcActive ? MpcSteerDeg() : LqrActive ? LqrSteerDeg() : HybridActive ? HybridSteerDeg() : double.NaN;

        /// <summary>
        /// The hybrid's angle: LQR's and MPC's blended by MPC's share - up quickly before a tight curve, far off the line
        /// or turned away from it, down slowly once on the line again. MPC is worked out only while it has a share.
        /// </summary>
        public double HybridSteerDeg()
        {
            if (!HybridActive || isReverse || pathAhead.Count < 2 || avgSpeed < 1)
            {
                hybridBlend.Reset();
                return double.NaN;
            }
            double pe = pathAhead[1].easting - pathAhead[0].easting, pn = pathAhead[1].northing - pathAhead[0].northing;
            double len = Math.Sqrt(pe * pe + pn * pn);
            if (len < 0.01) return double.NaN;
            double offLine = ((pivotAxlePos.easting - pathAhead[0].easting) * pn - (pivotAxlePos.northing - pathAhead[0].northing) * pe) / len;
            double headingOffDeg = glm.toDegrees(CPathAhead.Wrap(fixHeading - Math.Atan2(pe, pn)));
            double v = avgSpeed / 3.6;
            double share = hybridBlend.Update(secondsSinceStart, CAdaptiveLookAhead.TightestCurvature(pathAhead, v * 3 + 5), offLine, headingOffDeg);

            double lqrDeg = LqrSteerDeg();
            double mpcDeg = share > 0.001 ? MpcSteerDeg() : double.NaN;
            double u = CHybridBlend.Blend(lqrDeg, mpcDeg, share);
            if (!double.IsNaN(u)) mpcLastDeg = u;   // MPC goes on from the angle really asked for
            return u;
        }

        /// <summary>The LQR's angle on the path ahead (degrees, without the side-hill compensation the guidance adds); NaN: not now.</summary>
        public double LqrSteerDeg()
        {
            if (!(LqrActive || HybridActive) || isReverse || pathAhead.Count < 2 || avgSpeed < 1)
            {
                mpcLastS = -1e9;
                mpcLastHeading = double.NaN;
                return double.NaN;
            }
            try
            {
                double t = secondsSinceStart;
                double v = avgSpeed / 3.6;
                double L = ModelWheelbaseM;
                double sideHill = ahrs.imuRoll != 88888 ? ahrs.imuRoll * -gyd.sideHillCompFactor : 0;
                var vs = Properties.VehicleSettings.Default;
                double reading = mc.actualSteerAngleDegrees;
                double wheels = reading * CWasLinearity.Factor(reading, vs.setAS_WasLinMiddle, vs.setAS_WasLinBig) - sideHill;
                LearnMpcBias(t, v, L, wheels);   // the same simple offset learning as MPC's, until the estimator is ready
                double bias = MpcUsesKalman ? estimator.SteerOffsetDeg + sideHill : mpcBiasDeg;

                // off the line and the heading off it, from the path's first stretch
                double pe = pathAhead[1].easting - pathAhead[0].easting, pn = pathAhead[1].northing - pathAhead[0].northing;
                double len = Math.Sqrt(pe * pe + pn * pn);
                if (len < 0.01) return double.NaN;
                double offLine = ((pivotAxlePos.easting - pathAhead[0].easting) * pn - (pivotAxlePos.northing - pathAhead[0].northing) * pe) / len;
                double headingOff = CPathAhead.Wrap(fixHeading - Math.Atan2(pe, pn));

                // the angles sent in the dead time, as the wheels will take them (offset in, side-hill part out)
                int n = lqr.DelaySteps(SteerDeadTimeS);
                var chain = new double[n];
                for (int i = 0; i < n; i++)
                    chain[i] = glm.toRadians(AskedAt(t - (i + 1) * LqrStepS + 1e-6, wheels + sideHill) * ModelWheelShare - sideHill + bias);

                // the driver's style (shared with MPC): calmer is a heavier weight on the angle; below 6 it would swing
                var ts = Properties.ToolSettings.Default;
                lqr.WeightSteer = Math.Max(6, 10 * Math.Pow(2, (Math.Max(1, Math.Min(10, ts.setMpc_Calm)) - 5) * 0.4));

                double FfAt(int i) => CSteerLqr.PathAngleAt(pathAhead, v * (n - i) * LqrStepS, L);
                double u = lqr.Steer(offLine, headingOff, chain, v, L, FfAt, glm.toRadians(vehicle.maxSteerAngle));
                mpcLastS = t;
                // what to send so the wheels really take u: the learnt offset taken off
                // (and asked so much more that the wheels take it: the simulator's take only 90 %)
                return (glm.toDegrees(u) - bias) / ModelWheelShare;
            }
            catch (Exception ex)
            {
                Log.EventWriter("LQR steering error, pure pursuit used: " + ex.Message);
                return double.NaN;
            }
        }
    }
}
