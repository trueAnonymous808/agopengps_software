using AgOpenGPS.Navigation;
using NUnit.Framework;
using System.IO;
using System.Linq;

namespace AgOpenGPS.Tests.Navigation
{
    public class RoadNetworkTests
    {
        // three ways: a motorway (1-2), a two-way track (2-3), a one-way residential street (3-4)
        private const string Sample = @"{ ""elements"": [
            { ""type"": ""node"", ""id"": 1, ""lat"": 47.0000, ""lon"": 19.0000 },
            { ""type"": ""node"", ""id"": 2, ""lat"": 47.0010, ""lon"": 19.0000 },
            { ""type"": ""node"", ""id"": 3, ""lat"": 47.0010, ""lon"": 19.0015 },
            { ""type"": ""node"", ""id"": 4, ""lat"": 47.0020, ""lon"": 19.0015 },
            { ""type"": ""way"", ""id"": 10, ""nodes"": [1, 2], ""tags"": { ""highway"": ""motorway"" } },
            { ""type"": ""way"", ""id"": 11, ""nodes"": [2, 3], ""tags"": { ""highway"": ""track"", ""name"": ""Dűlő"" } },
            { ""type"": ""way"", ""id"": 12, ""nodes"": [3, 4], ""tags"": { ""highway"": ""residential"", ""oneway"": ""yes"" } }
        ] }";

        [TestCase("track", true)]
        [TestCase("unclassified", true)]
        [TestCase("primary", true)]
        [TestCase("motorway", false)]
        [TestCase("trunk_link", false)]
        [TestCase("footway", false)]
        [TestCase("cycleway", false)]
        [TestCase("steps", false)]
        public void TractorProfile_KnowsWhereATractorMayGo(string highway, bool allowed)
        {
            Assert.That(TractorProfile.IsAllowed(highway), Is.EqualTo(allowed));
        }

        [Test]
        public void TractorProfile_SpeedsFollowTheRoadClass()
        {
            Assert.That(TractorProfile.SpeedKmh(RoadClass.Primary), Is.EqualTo(40));
            Assert.That(TractorProfile.SpeedKmh(RoadClass.Unclassified), Is.EqualTo(30));
            Assert.That(TractorProfile.SpeedKmh(RoadClass.Track), Is.EqualTo(15));
        }

        [Test]
        public void Parse_KeepsOnlyAllowedWaysAndTheirDirections()
        {
            RoadNetwork net = OverpassParser.Parse(Sample, 47.001, 19.001, 5);
            int n2 = net.Nearest(47.0010, 19.0000), n3 = net.Nearest(47.0010, 19.0015), n4 = net.Nearest(47.0020, 19.0015);

            // the motorway is gone: node 1 is not part of the network
            Assert.That(net.NodeCount, Is.EqualTo(3));
            // the track goes both ways and keeps its name
            Assert.That(net.EdgesFrom(n2).Any(e => e.To == n3 && e.Class == RoadClass.Track && e.Name == "Dűlő"), Is.True);
            Assert.That(net.EdgesFrom(n3).Any(e => e.To == n2), Is.True);
            // the one-way street only from 3 to 4
            Assert.That(net.EdgesFrom(n3).Any(e => e.To == n4), Is.True);
            Assert.That(net.EdgesFrom(n4).Any(e => e.To == n3), Is.False);
            // about 113 m of track (0.0015° of longitude at 47°)
            Assert.That(net.EdgesFrom(n2).First(e => e.To == n3).LengthM, Is.EqualTo(114).Within(3));
        }

        [Test]
        public void Covers_InsideTheRadiusOnly()
        {
            RoadNetwork net = OverpassParser.Parse(Sample, 47.0, 19.0, 5);
            Assert.That(net.Covers(47.02, 19.02), Is.True);     // about 2.7 km away
            Assert.That(net.Covers(47.2, 19.0), Is.False);      // about 22 km away
        }

        [Test]
        public void SaveAndLoad_KeepTheNetwork()
        {
            RoadNetwork net = OverpassParser.Parse(Sample, 47.001, 19.001, 5);
            string path = Path.Combine(Path.GetTempPath(), "agog-roads-test.bin");
            try
            {
                net.Save(path);
                RoadNetwork back = RoadNetwork.Load(path);
                Assert.That(back.NodeCount, Is.EqualTo(net.NodeCount));
                Assert.That(back.CentreLat, Is.EqualTo(47.001));
                Assert.That(back.RadiusKm, Is.EqualTo(5));
                int b3 = back.Nearest(47.0010, 19.0015), b4 = back.Nearest(47.0020, 19.0015);
                Assert.That(back.EdgesFrom(b3).Any(e => e.To == b4), Is.True);
                Assert.That(back.EdgesFrom(b4).Any(e => e.To == b3), Is.False);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Test]
        public void Query_AsksForRoadsAroundThePoint()
        {
            string q = OverpassParser.Query(47.5, 19.25, 30);
            Assert.That(q, Does.Contain("[out:json]"));
            Assert.That(q, Does.Contain("around:30000,47.5,19.25"));
            Assert.That(q, Does.Contain("highway"));
        }
    }
}
