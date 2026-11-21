using AgOpenGPS.Navigation;
using NUnit.Framework;

namespace AgOpenGPS.Tests.Navigation
{
    public class RouteSteerTests
    {
        // 222 m north on a track, then 228 m east on a public road
        private static Route Sample(RoadClass first = RoadClass.Track, RoadClass second = RoadClass.Tertiary)
        {
            var route = new Route { LengthM = 450, TimeS = 100 };
            route.Points.Add(new NavPoint(47.000, 19.000));
            route.Points.Add(new NavPoint(47.002, 19.000));
            route.Points.Add(new NavPoint(47.002, 19.003));
            route.Legs.Add(first);
            route.Legs.Add(second);
            return route;
        }

        [Test]
        public void Check_AllowsSteeringOnAFieldTrack()
        {
            Assert.That(RouteSteer.Check(Sample(), 0, 0.5, 10, false, 400), Is.EqualTo(SteerBlock.None));
        }

        [Test]
        public void Check_AllowsSteeringOnARoadToo()
        {
            Assert.That(RouteSteer.Check(Sample(RoadClass.Tertiary), 0, 0.5, 10, false, 400), Is.EqualTo(SteerBlock.None));
            Assert.That(RouteSteer.Check(Sample(), 1, 0.5, 10, false, 200), Is.EqualTo(SteerBlock.None));
        }

        [Test]
        public void LaneOffset_UsesTheRoadOrTheTrackSetting()
        {
            Assert.That(RouteSteer.LaneOffset(RoadClass.Track, 1.75, 0), Is.EqualTo(0));
            Assert.That(RouteSteer.LaneOffset(RoadClass.Track, 1.75, -0.5), Is.EqualTo(-0.5));
            Assert.That(RouteSteer.LaneOffset(RoadClass.Secondary, 1.75, 0), Is.EqualTo(1.75));
            Assert.That(RouteSteer.LaneOffset(RoadClass.Residential, -1.25, 0), Is.EqualTo(-1.25));
        }

        [Test]
        public void ClampOffset_KeepsTheStepsAndTheLimit()
        {
            Assert.That(RouteSteer.ClampOffset(1.8), Is.EqualTo(1.75));
            Assert.That(RouteSteer.ClampOffset(12), Is.EqualTo(RouteSteer.MaxOffsetM));
            Assert.That(RouteSteer.ClampOffset(-12), Is.EqualTo(-RouteSteer.MaxOffsetM));
            Assert.That(RouteSteer.ClampOffset(double.NaN), Is.EqualTo(0));
        }

        [Test]
        public void OffsetRight_ANegativeOffsetGoesLeft()
        {
            RouteSteer.OffsetRight(new[] { 0.0, 0.0 }, new[] { 0.0, 100.0 }, new[] { -1.5 }, out double[] e, out double[] n);
            Assert.That(e[0], Is.EqualTo(-1.5).Within(1e-9));
        }

        [Test]
        public void OffsetRight_MovesTheLineToTheRightOfItsDirection()
        {
            // northbound: right is east
            RouteSteer.OffsetRight(new[] { 0.0, 0.0 }, new[] { 0.0, 100.0 }, new[] { 2.0 }, out double[] e, out double[] n);
            Assert.That(e[0], Is.EqualTo(2).Within(1e-9));
            Assert.That(e[1], Is.EqualTo(2).Within(1e-9));
            Assert.That(n[1], Is.EqualTo(100).Within(1e-9));
            // southbound: right is west
            RouteSteer.OffsetRight(new[] { 0.0, 0.0 }, new[] { 100.0, 0.0 }, new[] { 2.0 }, out e, out n);
            Assert.That(e[0], Is.EqualTo(-2).Within(1e-9));
        }

        [Test]
        public void OffsetRight_KeepsATrackLegInTheMiddle()
        {
            // a road leg north, then a track leg east
            RouteSteer.OffsetRight(new[] { 0.0, 0.0, 100.0 }, new[] { 0.0, 100.0, 100.0 }, new[] { 2.0, 0.0 }, out double[] e, out double[] n);
            Assert.That(e[0], Is.EqualTo(2).Within(1e-9));    // road: 2 m right
            Assert.That(n[2], Is.EqualTo(100).Within(1e-9));  // track end: on the line
            Assert.That(e[2], Is.EqualTo(100).Within(1e-9));
        }

        [Test]
        public void Check_StopsWhenFastReversingFarOffOrArriving()
        {
            Assert.That(RouteSteer.Check(Sample(), 0, 0, 16, false, 400), Is.EqualTo(SteerBlock.TooFast));
            Assert.That(RouteSteer.Check(Sample(), 0, 0, 5, true, 400), Is.EqualTo(SteerBlock.Reversing));
            Assert.That(RouteSteer.Check(Sample(), 0, 4.5, 5, false, 400), Is.EqualTo(SteerBlock.OffRoute));
            Assert.That(RouteSteer.Check(Sample(), 0, -4.5, 5, false, 400), Is.EqualTo(SteerBlock.OffRoute));
            Assert.That(RouteSteer.Check(Sample(), 0, 0, 5, false, 20), Is.EqualTo(SteerBlock.NearArrival));
        }

        [Test]
        public void Check_NeedsAClassForEveryLeg()
        {
            Route route = Sample();
            route.Legs.RemoveAt(1);
            Assert.That(RouteSteer.Check(route, 0, 0, 5, false, 400), Is.EqualTo(SteerBlock.NoRoute));
            Assert.That(RouteSteer.Check(null, 0, 0, 5, false, 400), Is.EqualTo(SteerBlock.NoRoute));
        }

        [Test]
        public void PurePursuit_GoesStraightOnTheLine()
        {
            SteerResult r = RouteSteer.PurePursuit(new[] { 0.0, 0.0 }, new[] { 0.0, 100.0 }, 0, 10, 0, 5, 3, 35);
            Assert.That(r.AngleDeg, Is.EqualTo(0).Within(0.01));
            Assert.That(r.CrossTrackM, Is.EqualTo(0).Within(0.01));
            Assert.That(r.GoalN, Is.EqualTo(15).Within(0.01));
        }

        [Test]
        public void PurePursuit_SteersBackToTheLine()
        {
            // heading north, the line 1 m to the right (east): steer right (positive)
            SteerResult right = RouteSteer.PurePursuit(new[] { 1.0, 1.0 }, new[] { 0.0, 100.0 }, 0, 10, 0, 5, 3, 35);
            Assert.That(right.AngleDeg, Is.GreaterThan(0));
            // the line 1 m to the left: steer left
            SteerResult left = RouteSteer.PurePursuit(new[] { -1.0, -1.0 }, new[] { 0.0, 100.0 }, 0, 10, 0, 5, 3, 35);
            Assert.That(left.AngleDeg, Is.LessThan(0));
            Assert.That(left.CrossTrackM, Is.EqualTo(-right.CrossTrackM).Within(0.01));
        }

        [Test]
        public void PurePursuit_FollowsTheBendAndKeepsTheAngleLimit()
        {
            // north, then a right-angle turn east just ahead
            SteerResult r = RouteSteer.PurePursuit(new[] { 0.0, 0.0, 50.0 }, new[] { 0.0, 10.0, 10.0 }, 0, 8, 0, 6, 3, 30);
            Assert.That(r.AngleDeg, Is.GreaterThan(0));
            Assert.That(r.AngleDeg, Is.LessThanOrEqualTo(30));
            Assert.That(r.GoalN, Is.EqualTo(10).Within(0.01));
            Assert.That(r.GoalE, Is.EqualTo(4).Within(0.01));
        }

        [Test]
        public void RoundCorners_TurnsARightAngleIntoAnArc()
        {
            // north 50 m, then east 50 m: a 10 m arc from (0, 40) to (10, 50) round (10, 40)
            RouteSteer.RoundCorners(new[] { 0.0, 0.0, 50.0 }, new[] { 0.0, 50.0, 50.0 }, 10, out var e, out var n);
            Assert.That(e[0], Is.EqualTo(0).Within(1e-9));
            Assert.That(n[0], Is.EqualTo(0).Within(1e-9));
            Assert.That(e[e.Count - 1], Is.EqualTo(50).Within(1e-9));
            Assert.That(n[n.Count - 1], Is.EqualTo(50).Within(1e-9));
            Assert.That(e[1], Is.EqualTo(0).Within(1e-6));
            Assert.That(n[1], Is.EqualTo(40).Within(1e-6));
            Assert.That(e[e.Count - 2], Is.EqualTo(10).Within(1e-6));
            Assert.That(n[n.Count - 2], Is.EqualTo(50).Within(1e-6));
            for (int i = 1; i < e.Count - 1; i++)
            {
                double d = System.Math.Sqrt((e[i] - 10) * (e[i] - 10) + (n[i] - 40) * (n[i] - 40));
                Assert.That(d, Is.EqualTo(10).Within(1e-6));
            }
            Assert.That(e.Count, Is.GreaterThan(10));   // about a metre apart along the 15.7 m arc
        }

        [Test]
        public void RoundCorners_TurnsLeftTheOtherWay()
        {
            // north, then west: the arc centre lies to the west at (-10, 40)
            RouteSteer.RoundCorners(new[] { 0.0, 0.0, -50.0 }, new[] { 0.0, 50.0, 50.0 }, 10, out var e, out var n);
            for (int i = 1; i < e.Count - 1; i++)
            {
                double d = System.Math.Sqrt((e[i] + 10) * (e[i] + 10) + (n[i] - 40) * (n[i] - 40));
                Assert.That(d, Is.EqualTo(10).Within(1e-6));
            }
        }

        [Test]
        public void RoundCorners_UsesASmallerArcOnShortLegs()
        {
            // legs of 8 m: at most 4 m of each is used, so the radius shrinks to 4 m
            RouteSteer.RoundCorners(new[] { 0.0, 0.0, 8.0 }, new[] { 0.0, 8.0, 8.0 }, 10, out var e, out var n);
            Assert.That(n[1], Is.EqualTo(4).Within(1e-6));
            Assert.That(e[e.Count - 2], Is.EqualTo(4).Within(1e-6));
        }

        [Test]
        public void RoundCorners_LeavesAStraightLineAlone()
        {
            RouteSteer.RoundCorners(new[] { 0.0, 0.0, 0.0 }, new[] { 0.0, 50.0, 100.0 }, 10, out var e, out var n);
            Assert.That(e.Count, Is.EqualTo(3));
        }

        [Test]
        public void CornerRadius_IsAtLeastHalfAgainTheTightestTurn()
        {
            // 3 m wheelbase at 30°: the tightest turn is 5.2 m, so 10 m wins
            Assert.That(RouteSteer.CornerRadius(3, 30), Is.EqualTo(RouteSteer.CornerRadiusM));
            // 4 m wheelbase at 20°: 11 m tightest, 16.5 m
            Assert.That(RouteSteer.CornerRadius(4, 20), Is.EqualTo(1.5 * 4 / System.Math.Tan(20 * System.Math.PI / 180)).Within(1e-9));
        }

        [Test]
        public void PurePursuit_NeverAimsCloserThanTheMinimum()
        {
            SteerResult r = RouteSteer.PurePursuit(new[] { 0.0, 0.0 }, new[] { 0.0, 100.0 }, 0, 10, 0, 0.5, 3, 35);
            Assert.That(r.GoalN, Is.EqualTo(10 + RouteSteer.MinLookAheadM).Within(0.01));
        }
    }
}
