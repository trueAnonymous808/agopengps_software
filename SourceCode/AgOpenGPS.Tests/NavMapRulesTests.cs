using NUnit.Framework;

namespace AgOpenGPS.Tests
{
    public class NavMapRulesTests
    {
        [Test]
        public void ShouldShow_OnlyWhenEnabledWithoutFieldAndWithFix()
        {
            Assert.That(NavMapRules.ShouldShow(enabled: true, jobStarted: false, hasFix: true), Is.True);
            Assert.That(NavMapRules.ShouldShow(enabled: false, jobStarted: false, hasFix: true), Is.False);
            Assert.That(NavMapRules.ShouldShow(enabled: true, jobStarted: true, hasFix: true), Is.False);
            Assert.That(NavMapRules.ShouldShow(enabled: true, jobStarted: false, hasFix: false), Is.False);
        }

        [Test]
        public void ShouldShow_NavigatingShowsTheMapEvenWhenSwitchedOff()
        {
            Assert.That(NavMapRules.ShouldShow(enabled: false, jobStarted: false, hasFix: true, navigating: true), Is.True);
            Assert.That(NavMapRules.ShouldShow(enabled: false, jobStarted: false, hasFix: true, navigating: false), Is.False);
            // never over a field, never without a position
            Assert.That(NavMapRules.ShouldShow(enabled: false, jobStarted: true, hasFix: true, navigating: true), Is.False);
            Assert.That(NavMapRules.ShouldShow(enabled: true, jobStarted: false, hasFix: false, navigating: true), Is.False);
        }

        // display setting, navigating, map chosen in the navigation menu -> map shown
        [TestCase("BingSatellite", false, "OpenStreetMap", "BingSatellite")]
        [TestCase("BingSatellite", true, "OpenStreetMap", "OpenStreetMap")]
        [TestCase("OpenStreetMap", true, "BingSatellite", "BingSatellite")]
        [TestCase("OpenStreetMap", false, "BingSatellite", "OpenStreetMap")]
        [TestCase(null, true, null, "OpenStreetMap")]
        public void SourceFor_NavigationUsesTheMapChosenForIt(string setting, bool navigating, string navigationMap, string expected)
        {
            Assert.That(NavMapRules.SourceFor(setting, navigating, navigationMap), Is.EqualTo(expected));
        }

        [TestCase("BingSatellite", "BingSatellite")]
        [TestCase("OpenStreetMap", "OpenStreetMap")]
        [TestCase(null, "OpenStreetMap")]
        [TestCase("", "OpenStreetMap")]
        [TestCase("BingHybrid", "OpenStreetMap")]
        public void NormalizeSource_KnowsTwoSources(string key, string expected)
        {
            Assert.That(NavMapRules.NormalizeSource(key), Is.EqualTo(expected));
        }

        // the map under the tractor is a square of tiles around a centre tile; it is made again once the tractor is
        // two tiles away from that centre (so there is always at least a tile and a half of map ahead)
        [TestCase(100, 200, 100, 200, false)]
        [TestCase(100, 200, 101, 199, false)]
        [TestCase(100, 200, 102, 200, true)]
        [TestCase(100, 200, 100, 198, true)]
        [TestCase(100, 200, 97, 203, true)]
        public void NeedsNewMosaic_WhenTwoTilesFromTheCentre(long centreX, long centreY, long tileX, long tileY, bool expected)
        {
            Assert.That(NavMapRules.NeedsNewMosaic(centreX, centreY, tileX, tileY), Is.EqualTo(expected));
        }

        [Test]
        public void MosaicStart_PutsTheCentreTileInTheMiddle()
        {
            // 8 tiles across: the centre tile is the fifth (index 4), so the square starts 4 tiles before it
            Assert.That(NavMapRules.MosaicStart(100), Is.EqualTo(96));
        }
    }
}
