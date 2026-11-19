using AgOpenGPS.Navigation;
using NUnit.Framework;
using System.Linq;

namespace AgOpenGPS.Tests.Navigation
{
    public class RoutePlannerTests
    {
        // 0.001° of latitude is about 111 m, 0.001° of longitude about 76 m at 47°
        private static RoadNetwork Net()
        {
            return new RoadNetwork(47, 19, 10);
        }

        [Test]
        public void Plan_PrefersTheFasterRoadOverTheShorterTrack()
        {
            var net = Net();
            net.AddNode(1, 47.000, 19.000);   // A
            net.AddNode(2, 47.000, 19.004);   // B, 300 m east of A
            net.AddNode(3, 47.001, 19.000);   // C
            net.AddNode(4, 47.001, 19.004);   // D
            net.AddWay(new long[] { 1, 2 }, RoadClass.Track, false, "track");            // 300 m at 15 km/h = 72 s
            net.AddWay(new long[] { 1, 3, 4, 2 }, RoadClass.Primary, false, "main");     // 522 m at 40 km/h = 47 s

            Route route = RoutePlanner.Plan(net, 47.000, 19.000, 47.000, 19.004);

            Assert.That(route, Is.Not.Null);
            Assert.That(route.Points.Count, Is.EqualTo(4));
            Assert.That(route.LengthM, Is.EqualTo(522).Within(10));
            Assert.That(route.TimeS, Is.EqualTo(47).Within(2));
        }

        [Test]
        public void Plan_RespectsOneWayStreets()
        {
            var net = Net();
            net.AddNode(1, 47.000, 19.000);
            net.AddNode(2, 47.000, 19.004);
            net.AddNode(3, 47.001, 19.000);
            net.AddNode(4, 47.001, 19.004);
            net.AddWay(new long[] { 2, 1 }, RoadClass.Residential, true, "one way");       // only from B to A
            net.AddWay(new long[] { 1, 3, 4, 2 }, RoadClass.Residential, false, "around");

            Route route = RoutePlanner.Plan(net, 47.000, 19.000, 47.000, 19.004);

            Assert.That(route.Points.Count, Is.EqualTo(4));   // around, not along the one-way street
        }

        [Test]
        public void Plan_ReturnsNullWhenTheDestinationCannotBeReached()
        {
            var net = Net();
            net.AddNode(1, 47.000, 19.000);
            net.AddNode(2, 47.001, 19.000);
            net.AddNode(3, 47.010, 19.010);
            net.AddNode(4, 47.011, 19.010);
            net.AddWay(new long[] { 1, 2 }, RoadClass.Track, false, null);
            net.AddWay(new long[] { 3, 4 }, RoadClass.Track, false, null);   // an island

            Assert.That(RoutePlanner.Plan(net, 47.000, 19.000, 47.011, 19.010), Is.Null);
        }

        // north to a junction, then east (right) or west (left); a side road makes the corner a junction
        private static RoadNetwork Corner()
        {
            var net = Net();
            net.AddNode(1, 47.000, 19.000);
            net.AddNode(2, 47.001, 19.000);   // the junction
            net.AddNode(3, 47.001, 19.002);   // east
            net.AddNode(4, 47.001, 18.998);   // west
            net.AddNode(5, 47.002, 19.000);   // straight on
            net.AddWay(new long[] { 1, 2, 5 }, RoadClass.Tertiary, false, "north road");
            net.AddWay(new long[] { 4, 2, 3 }, RoadClass.Tertiary, false, "cross road");
            return net;
        }

        [Test]
        public void Plan_FindsARightTurn()
        {
            Route route = RoutePlanner.Plan(Corner(), 47.000, 19.000, 47.001, 19.002);
            Assert.That(route.Steps.Select(s => s.Turn), Is.EqualTo(new[] { Manoeuvre.Right, Manoeuvre.Arrive }));
            Assert.That(route.Steps[0].RoadName, Is.EqualTo("cross road"));
            Assert.That(route.Steps[0].PointIndex, Is.EqualTo(1));
        }

        [Test]
        public void Plan_FindsALeftTurn()
        {
            Route route = RoutePlanner.Plan(Corner(), 47.000, 19.000, 47.001, 18.998);
            Assert.That(route.Steps.Select(s => s.Turn), Is.EqualTo(new[] { Manoeuvre.Left, Manoeuvre.Arrive }));
        }

        [Test]
        public void Plan_StraightThroughAJunctionIsNoStep()
        {
            Route route = RoutePlanner.Plan(Corner(), 47.000, 19.000, 47.002, 19.000);
            Assert.That(route.Steps.Select(s => s.Turn), Is.EqualTo(new[] { Manoeuvre.Arrive }));
        }
    }
}
