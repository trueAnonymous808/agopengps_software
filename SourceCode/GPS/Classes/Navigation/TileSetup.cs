using GMap.NET;
using GMap.NET.MapProviders;
using System;
using System.IO;
using System.Windows.Forms;

namespace AgOpenGPS.Navigation
{
    // One setup for every map the program downloads tiles for (the navigation map on the ground, the map picker):
    // modern TLS, the tile cache in the AgOpenGPS folder, and the program's own name towards OpenStreetMap, whose
    // tile servers block programs that pose as a browser (GMap.NET's default) with an "Access blocked" tile.
    public static class TileSetup
    {
        private static bool ready;

        /// <summary>How the program names itself to OpenStreetMap, as its tile usage policy asks.</summary>
        public static string Identity
        {
            get { return "AgOpenGPS/" + Application.ProductVersion + " (+https://github.com/AgOpenGPS-Official/AgOpenGPS)"; }
        }

        /// <summary>Done once: TLS, cache folder, and dropping OpenStreetMap tiles stored before the program named itself.</summary>
        public static void Prepare(string cacheFolder)
        {
            NetSetup.EnsureModernTls();
            if (ready) return;
            ready = true;
            GMap.NET.WindowsForms.GMapImageProxy.Enable();
            GMaps.Instance.Mode = AccessMode.ServerAndCache;
            // one name for every request: the user agent is shared by all maps and their download threads, so a
            // per-map name could send an OpenStreetMap tile request under the wrong one (Bing serves it just the same)
            GMapProvider.UserAgent = Identity;
            try
            {
                Directory.CreateDirectory(cacheFolder);
                GMaps.Instance.PrimaryCache = new GMap.NET.CacheProviders.SQLitePureImageCache { CacheLocation = cacheFolder };
                // a tile fetched while the program still posed as a browser may be OpenStreetMap's "Access blocked"
                // picture, and it would stay in the cache for good: those tiles are dropped once
                string marker = Path.Combine(cacheFolder, "osm-identity-1.txt");
                if (!File.Exists(marker))
                {
                    GMaps.Instance.PrimaryCache.DeleteOlderThan(DateTime.Now.AddDays(1), OpenStreetMapProvider.Instance.DbId);
                    File.WriteAllText(marker, "OpenStreetMap tiles stored before " + DateTime.Now.ToString("u") + " were removed.");
                }
            }
            catch (Exception)
            {
                // without the cache folder the maps still work online
            }
        }
    }
}
