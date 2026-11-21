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

        // a straight track mapped only at its ends (1 km apart), joining a road at its north end
        private static RoadNetwork LongTrack()
        {
            var net = Net();
            net.AddNode(1, 47.000, 19.000);   // south end of the track
            net.AddNode(2, 47.009, 19.000);   // north end: the junction
            net.AddNode(3, 47.009, 19.004);   // east along the road
            net.AddNode(4, 47.009, 18.996);   // west along the road
            net.AddWay(new long[] { 1, 2 }, RoadClass.Track, false, "track");
            net.AddWay(new long[] { 4, 2, 3 }, RoadClass.Tertiary, false, "road");
            return net;
        }

        [Test]
        public void Plan_StartsOnTheRoadUnderTheTractorNotAtTheNearestNode()
        {
            // the tractor in the middle of the track, 2 m beside it
            Route route = RoutePlanner.Plan(LongTrack(), 47.0045, 19.00003, 47.009, 19.004);
            Assert.That(route, Is.Not.Null);
            Assert.That(route.Points[0].Lat, Is.EqualTo(47.0045).Within(1e-6));
            Assert.That(route.Points[0].Lon, Is.EqualTo(19.000).Within(1e-9));
            Assert.That(route.Points.Count, Is.EqualTo(3));   // start point, junction, east end
            Assert.That(route.Legs, Is.EqualTo(new[] { RoadClass.Track, RoadClass.Tertiary }));
            Assert.That(route.LengthM, Is.EqualTo(500 + 303).Within(10));
            Assert.That(route.Steps.Select(s => s.Turn), Is.EqualTo(new[] { Manoeuvre.Right, Manoeuvre.Arrive }));
            Assert.That(route.Steps[0].PointIndex, Is.EqualTo(1));
        }

        [Test]
        public void Plan_LeadsOutOfTheFieldToTheRoad()
        {
            // the tractor in a field, about 200 m east of the middle of the track
            Route route = RoutePlanner.Plan(LongTrack(), 47.0045, 19.0026, 47.009, 19.004);
            Assert.That(route.Points[0].Lat, Is.EqualTo(47.0045).Within(1e-9));
            Assert.That(route.Points[0].Lon, Is.EqualTo(19.0026).Within(1e-9));
            Assert.That(route.Points[1].Lon, Is.EqualTo(19.000).Within(1e-9));   // onto the track beside it
            Assert.That(route.Legs, Is.EqualTo(new[] { RoadClass.OffRoad, RoadClass.Track, RoadClass.Tertiary }));
            Assert.That(route.LengthM, Is.EqualTo(198 + 500 + 303).Within(15));
            // west across the field, right onto the track (north), right at the junction (east)
            Assert.That(route.Steps.Select(s => s.Turn), Is.EqualTo(new[] { Manoeuvre.Right, Manoeuvre.Right, Manoeuvre.Arrive }));
            Assert.That(route.Steps.Select(s => s.PointIndex), Is.EqualTo(new[] { 1, 2, 3 }));
        }

        [Test]
        public void Plan_LeadsFromTheRoadIntoTheFieldAtTheEnd()
        {
            // from the west end of the road to a point in a field 167 m north of its east end
            Route route = RoutePlanner.Plan(LongTrack(), 47.009, 18.996, 47.0105, 19.004);
            NavPoint last = route.Points[route.Points.Count - 1];
            Assert.That(last.Lat, Is.EqualTo(47.0105).Within(1e-9));
            Assert.That(route.Legs[route.Legs.Count - 1], Is.EqualTo(RoadClass.OffRoad));
            // east along the road, then left (north) off it into the field
            Assert.That(route.Steps.Select(s => s.Turn), Is.EqualTo(new[] { Manoeuvre.Left, Manoeuvre.Arrive }));
            Assert.That(route.Steps[1].PointIndex, Is.EqualTo(route.Points.Count - 1));
        }

        [Test]
        public void Plan_NoFieldLegWhenTheTractorIsOnTheRoad()
        {
            // 8 m beside the mapped line: map and GPS error, not a field
            Route route = RoutePlanner.Plan(LongTrack(), 47.0045, 19.0001, 47.009, 19.004);
            Assert.That(route.Legs[0], Is.EqualTo(RoadClass.Track));
        }

        [Test]
        public void Plan_EndsOnTheRoadNearestTheDestination()
        {
            // from the east end to a field gate halfway down the track
            Route route = RoutePlanner.Plan(LongTrack(), 47.009, 19.004, 47.0045, 18.9999);
            Assert.That(route.Points.Count, Is.EqualTo(3));   // east end, junction, the point on the track
            NavPoint last = route.Points[route.Points.Count - 1];
            Assert.That(last.Lat, Is.EqualTo(47.0045).Within(1e-6));
            Assert.That(last.Lon, Is.EqualTo(19.000).Within(1e-9));
            Assert.That(route.Legs, Is.EqualTo(new[] { RoadClass.Tertiary, RoadClass.Track }));
            Assert.That(route.Steps.Select(s => s.Turn), Is.EqualTo(new[] { Manoeuvre.Left, Manoeuvre.Arrive }));
        }

        [Test]
        public void Plan_BothPointsOnTheSameRoadGoStraightAlongIt()
        {
            Route route = RoutePlanner.Plan(LongTrack(), 47.002, 19.000, 47.006, 19.000);
            Assert.That(route.Points.Count, Is.EqualTo(2));
            Assert.That(route.LengthM, Is.EqualTo(445).Within(5));
            Assert.That(route.Legs, Is.EqualTo(new[] { RoadClass.Track }));
            Assert.That(route.Steps.Select(s => s.Turn), Is.EqualTo(new[] { Manoeuvre.Arrive }));
        }

        [Test]
        public void Plan_DoesNotGoTheWrongWayDownAOneWayStreetFromTheStart()
        {
            // a one-way street north to south; the tractor halfway along it wants to go north
            var net = Net();
            net.AddNode(1, 47.000, 19.000);
            net.AddNode(2, 47.002, 19.000);
            net.AddNode(3, 47.002, 19.002);
            net.AddNode(4, 47.000, 19.002);
            net.AddWay(new long[] { 2, 1 }, RoadClass.Residential, true, "one way");
            net.AddWay(new long[] { 1, 4, 3, 2 }, RoadClass.Residential, false, "around");

            Route route = RoutePlanner.Plan(net, 47.001, 19.000, 47.002, 19.000);
            Assert.That(route, Is.Not.Null);
            Assert.That(route.Points[1].Lat, Is.EqualTo(47.000).Within(1e-9));   // first south with the traffic
            Assert.That(route.Points.Count, Is.EqualTo(5));
        }

        [Test]
        public void Plan_StraightThroughAJunctionIsNoStep()
        {
            Route route = RoutePlanner.Plan(Corner(), 47.000, 19.000, 47.002, 19.000);
            Assert.That(route.Steps.Select(s => s.Turn), Is.EqualTo(new[] { Manoeuvre.Arrive }));
        }
    }
}
