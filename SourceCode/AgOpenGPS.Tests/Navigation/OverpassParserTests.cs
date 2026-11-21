using AgOpenGPS.Navigation;
using NUnit.Framework;

namespace AgOpenGPS.Tests.Navigation
{
    public class OverpassParserTests
    {
        [Test]
        public void TripRadiusKm_CoversTheTripWithRoomToSpare()
        {
            // a 10 km trip: 5 km to either end from the middle, plus 3 km
            Assert.That(OverpassParser.TripRadiusKm(10), Is.EqualTo(8));
            Assert.That(OverpassParser.TripRadiusKm(40), Is.EqualTo(23));
        }

        [Test]
        public void TripRadiusKm_IsNeverSmallerThanFiveKm()
        {
            Assert.That(OverpassParser.TripRadiusKm(0), Is.EqualTo(5));
            Assert.That(OverpassParser.TripRadiusKm(2), Is.EqualTo(5));
        }

        [Test]
        public void Parse_BuildsTheNetworkFromWaysAndNodes()
        {
            const string json = @"{""elements"":[
                {""type"":""way"",""id"":1,""nodes"":[10,11],""tags"":{""highway"":""track""}},
                {""type"":""way"",""id"":2,""nodes"":[11,12],""tags"":{""highway"":""footway""}},
                {""type"":""node"",""id"":10,""lat"":47.0,""lon"":19.0},
                {""type"":""node"",""id"":11,""lat"":47.001,""lon"":19.0},
                {""type"":""node"",""id"":12,""lat"":47.002,""lon"":19.0}]}";
            RoadNetwork net = OverpassParser.Parse(json, 47, 19, 5);
            Assert.That(net.NodeCount, Is.EqualTo(2));   // the footway and its own node are left out
        }
    }
}
