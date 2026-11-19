using System;

namespace AgOpenGPS
{
    // The decisions of the navigation map (the map drawn as the ground of the 3D view while no field is open), kept
    // apart from the drawing so they can be tested.
    public static class NavMapRules
    {
        public const string OpenStreetMap = "OpenStreetMap";
        public const string BingSatellite = "BingSatellite";

        // the map is a square of MosaicTiles x MosaicTiles tiles at this zoom level: at 47° latitude a zoom 16 tile is
        // about 410 m, so the square is about 3.3 km across with the tractor near its middle
        public const int Zoom = 16;
        public const int MosaicTiles = 8;

        // the sharp square near the tractor: about 0.4 m a pixel, 8 x 8 tiles of about 100 m at 47° latitude
        public const int DetailZoom = 18;

        // only on the way to a field: switched on (or a route being followed), no field open, and the position is known
        public static bool ShouldShow(bool enabled, bool jobStarted, bool hasFix, bool navigating = false)
        {
            return (enabled || navigating) && !jobStarted && hasFix;
        }

        // while a route is followed the ground is the map chosen for navigation in its menu (the street map unless the
        // driver picked the satellite picture), whatever the display setting says
        public static string SourceFor(string setting, bool navigating, string navigationMap)
        {
            return NormalizeSource(navigating ? navigationMap : setting);
        }

        public static string NormalizeSource(string key)
        {
            return key == BingSatellite ? BingSatellite : OpenStreetMap;
        }

        // a new square is made once the tractor is two tiles away from the centre tile of the present one
        public static bool NeedsNewMosaic(long centreX, long centreY, long tileX, long tileY)
        {
            return Math.Max(Math.Abs(tileX - centreX), Math.Abs(tileY - centreY)) >= 2;
        }

        // the first tile of the square, so that the centre tile lies in its middle
        public static long MosaicStart(long centre)
        {
            return centre - MosaicTiles / 2;
        }
    }
}
