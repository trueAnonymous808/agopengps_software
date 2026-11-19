using AgOpenGPS.Navigation;
using NUnit.Framework;

namespace AgOpenGPS.Tests.Navigation
{
    public class RouteGuideTests
    {
        // 222 m north from A to B, then a right turn and 228 m east to C
        private static Route Sample()
        {
            var route = new Route { LengthM = 450, TimeS = 54 };
            route.Points.Add(new NavPoint(47.000, 19.000));
            route.Points.Add(new NavPoint(47.002, 19.000));
            route.Points.Add(new NavPoint(47.002, 19.003));
            route.Steps.Add(new RouteStep { Turn = Manoeuvre.Right, PointIndex = 1, RoadName = "east road" });
            route.Steps.Add(new RouteStep { Turn = Manoeuvre.Arrive, PointIndex = 2 });
            return route;
        }

        [Test]
        public void Update_GivesTheNextTurnAndItsDistance()
        {
            GuideState s = new RouteGuide(Sample()).Update(47.001, 19.000);
            Assert.That(s.NextTurn, Is.EqualTo(Manoeuvre.Right));
            Assert.That(s.NextRoadName, Is.EqualTo("east road"));
            Assert.That(s.NextTurnDistanceM, Is.EqualTo(111).Within(2));
            Assert.That(s.OffRoute, Is.False);
        }

        [Test]
        public void Update_AfterTheTurnTheNextStepIsTheArrival()
        {
            GuideState s = new RouteGuide(Sample()).Update(47.002, 19.001);
            Assert.That(s.NextTurn, Is.EqualTo(Manoeuvre.Arrive));
            Assert.That(s.NextTurnDistanceM, Is.EqualTo(152).Within(3));
        }

        [Test]
        public void Update_RemainingShrinksAlongTheRoute()
        {
            var guide = new RouteGuide(Sample());
            GuideState early = guide.Update(47.0005, 19.000);
            GuideState later = guide.Update(47.0015, 19.000);
            Assert.That(later.RemainingM, Is.LessThan(early.RemainingM));
            Assert.That(early.RemainingM, Is.EqualTo(394).Within(5));
            Assert.That(early.RemainingS, Is.EqualTo(394 * 54.0 / 450).Within(1));
        }

        [Test]
        public void Update_AsksForANewRouteAfterThreeFixesOffTheRoute()
        {
            var guide = new RouteGuide(Sample());
            double offEast = 19.000 + 60 / 76000.0;   // about 60 m east of the first leg
            GuideState first = guide.Update(47.001, offEast);
            GuideState second = guide.Update(47.001, offEast);
            GuideState third = guide.Update(47.001, offEast);
            Assert.That(first.OffRoute, Is.True);
            Assert.That(second.NeedsReplan, Is.False);
            Assert.That(third.NeedsReplan, Is.True);
        }

        [Test]
        public void Update_BackOnTheRouteForgetsTheOffRouteFixes()
        {
            var guide = new RouteGuide(Sample());
            double offEast = 19.000 + 60 / 76000.0;
            guide.Update(47.001, offEast);
            guide.Update(47.001, offEast);
            guide.Update(47.001, 19.000);
            Assert.That(guide.Update(47.001, offEast).NeedsReplan, Is.False);
        }

        [Test]
        public void Update_TellsWhereOnTheRouteTheTractorIs()
        {
            // 10 m east of the first leg: the nearest route point is on that leg, straight west of the tractor
            GuideState s = new RouteGuide(Sample()).Update(47.001, 19.000 + 10 / 76000.0);
            Assert.That(s.Segment, Is.EqualTo(0));
            Assert.That(s.OnRoute.Lat, Is.EqualTo(47.001).Within(0.00001));
            Assert.That(s.OnRoute.Lon, Is.EqualTo(19.000).Within(0.00001));

            // after the turn: on the second leg
            GuideState later = new RouteGuide(Sample()).Update(47.002, 19.001);
            Assert.That(later.Segment, Is.EqualTo(1));
            Assert.That(later.OnRoute.Lon, Is.EqualTo(19.001).Within(0.00001));
        }

        [Test]
        public void Update_ArrivesWithinThirtyMetres()
        {
            var guide = new RouteGuide(Sample());
            Assert.That(guide.Update(47.002, 19.0028).Arrived, Is.True);    // about 15 m before C
            Assert.That(new RouteGuide(Sample()).Update(47.002, 19.0020).Arrived, Is.False);
        }
    }
}
