using NUnit.Framework;
using System;
using System.Collections.Generic;

namespace AgOpenGPS.Tests
{
    public class SteerMpcTests
    {
        private const double Wheelbase = 3.0, Speed = 2.5, DeadTime = 0.3, Rate = 20;

        // a tractor with a dead time and a turning speed limit, steered by MPC every 0.1 s along a path
        private sealed class Run
        {
            public double E, N, Heading, Wheel, PlantOffset, ModelOffset;
            public readonly List<double[]> Sent = new List<double[]>();   // t, angle
            public double Last;
            public double T;

            private readonly CSteerMpc mpc = new CSteerMpc();

            public void Drive(double seconds, Func<double, double, List<vec2>> pathAhead, Action<Run> each = null)
            {
                const double plantDt = 0.01, controlDt = 0.1;
                double nextControl = 0;
                double end = T + seconds;
                while (T < end)
                {
                    if (T >= nextControl - 1e-9)
                    {
                        nextControl += controlDt;
                        int steps = (int)Math.Round(DeadTime / mpc.StepS);
                        var pending = new double[steps];
                        for (int k = 0; k < steps; k++) pending[k] = AskedAt(T - DeadTime + k * mpc.StepS);
                        var st = new CSteerMpc.State
                        {
                            East = E,
                            North = N,
                            HeadingRad = Heading,
                            SpeedMs = Speed,
                            WheelbaseM = Wheelbase,
                            WheelDeg = Wheel,
                            MaxDeg = 35,
                            RateDegS = Rate,
                            BiasDeg = ModelOffset,
                            LastDeg = Last,
                            Pending = pending
                        };
                        Last = mpc.Solve(st, pathAhead(E, N));
                        Sent.Add(new[] { T, Last });
                        each?.Invoke(this);
                    }
                    // the wheels answer what was sent the dead time ago, at their speed
                    double target = AskedAt(T - DeadTime);
                    double move = target - Wheel, max = Rate * plantDt;
                    Wheel += Math.Max(-max, Math.Min(max, move));
                    Heading += Speed * Math.Tan((Wheel + PlantOffset) * Math.PI / 180) / Wheelbase * plantDt;
                    E += Speed * Math.Sin(Heading) * plantDt;
                    N += Speed * Math.Cos(Heading) * plantDt;
                    T += plantDt;
                }
            }

            private double AskedAt(double t)
            {
                for (int i = Sent.Count - 1; i >= 0; i--)
                    if (Sent[i][0] <= t) return Sent[i][1];
                return 0;
            }
        }

        // a straight line north along easting 0, from the closest point on
        private static List<vec2> Straight(double e, double n) => new List<vec2> { new vec2(0, n), new vec2(0, n + 40) };

        // a circle to the right, radius 30, through (0, 0) heading north, from the closest point on
        private static List<vec2> Circle(double e, double n)
        {
            const double r = 30;
            double theta = Math.Atan2(n, r - e);
            var path = new List<vec2>();
            for (int k = 0; k <= 40; k++)
            {
                double a = theta + k / r;
                path.Add(new vec2(r - r * Math.Cos(a), r * Math.Sin(a)));
            }
            return path;
        }

        [Test]
        public void ItComesOntoAStraightLineWithoutSwingingFarOver()
        {
            var run = new Run { E = 0.5 };
            double furthestOver = 0;
            run.Drive(20, Straight, r => furthestOver = Math.Min(furthestOver, r.E));
            Assert.That(Math.Abs(run.E), Is.LessThan(0.03), "still off the line");
            Assert.That(furthestOver, Is.GreaterThan(-0.15), "swung over to the other side");
        }

        [Test]
        public void ItHoldsAnArcDespiteTheDeadTime()
        {
            var run = new Run();
            double worst = 0;
            run.Drive(10, Circle);
            run.Drive(15, Circle, r =>
            {
                double off = Math.Abs(Math.Sqrt((r.E - 30) * (r.E - 30) + r.N * r.N) - 30);
                worst = Math.Max(worst, off);
            });
            Assert.That(worst, Is.LessThan(0.05));
        }

        [Test]
        public void AKnownOffsetIsSteeredOut()
        {
            // the tractor drives straight only with the wheels at -1.5° (a slope, a sensor zero): told to MPC
            var run = new Run { PlantOffset = 1.5, ModelOffset = 1.5 };
            run.Drive(25, Straight);
            Assert.That(Math.Abs(run.E), Is.LessThan(0.03));
        }

        [Test]
        public void TheCurvePreviewIsNothingOnAnEvenArc()
        {
            List<vec2> arc = Circle(0, 0);
            Assert.That(CPathAhead.MeanCurvature(arc, 2, 20), Is.EqualTo(1 / 30.0).Within(0.002));
            Assert.That(Math.Abs(CPathAhead.CurvePreviewDeg(arc, 5, Speed, DeadTime, Wheelbase)), Is.LessThan(0.3));
            Assert.That(CPathAhead.CurvePreviewDeg(Straight(0, 0), 5, Speed, DeadTime, Wheelbase), Is.EqualTo(0).Within(1e-9));
        }

        [Test]
        public void BeforeACurvePurePursuitIsHeldBackFromTurningInEarly()
        {
            // straight for 3 m, then a right turn of radius 20
            var path = new List<vec2>();
            for (int i = 0; i <= 3; i++) path.Add(new vec2(0, i));
            for (int k = 1; k <= 30; k++)
            {
                double a = k / 20.0;
                path.Add(new vec2(20 - 20 * Math.Cos(a), 3 + 20 * Math.Sin(a)));
            }
            double deg = CPathAhead.CurvePreviewDeg(path, 6, Speed, DeadTime, Wheelbase);
            Assert.That(deg, Is.LessThan(-0.5));
        }

        [Test]
        public void SmallWobblesAreCalmedRealMovesPass()
        {
            var s = new CSteerSmoother();
            s.Apply(0, 0, 20);
            Assert.That(s.Apply(0.1, 0.1, 20), Is.EqualTo(0.05).Within(1e-9));   // a wobble: half of it
            Assert.That(s.Apply(0.2, 1.05, 20), Is.EqualTo(1.05).Within(1e-9));  // a real move: all of it
            Assert.That(s.Apply(0.3, 10, 20), Is.EqualTo(1.05 + 2.4).Within(1e-9)); // never faster than 1.2 × 20 °/s
        }

        [Test]
        public void ThePlayIsMadeUpForInTheDirectionTheAngleMoves()
        {
            var p = new CPlayComp();
            Assert.That(p.Apply(0, 1.0), Is.EqualTo(0));        // standing: nothing
            Assert.That(p.Apply(0.5, 1.0), Is.EqualTo(1.0));     // moving right: half the play ahead
            Assert.That(p.Apply(0.45, 1.0), Is.EqualTo(0.95));   // a little back: still to the right (hysteresis)
            Assert.That(p.Apply(0.2, 1.0), Is.EqualTo(-0.3));    // back for good: to the left
            Assert.That(p.Apply(0.2, 0.1), Is.EqualTo(0.2));     // too little play: nothing
        }
    }
}
