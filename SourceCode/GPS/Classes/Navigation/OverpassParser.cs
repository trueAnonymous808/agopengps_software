using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using System.Globalization;

namespace AgOpenGPS.Navigation
{
    // Turns the answer of the OpenStreetMap Overpass API (every way tagged "highway" around a point, with its nodes)
    // into the road network a tractor may use.
    public static class OverpassParser
    {
        /// <summary>
        /// The radius of the road map downloaded for a trip (round its middle): the trip's own surroundings with 3 km to
        /// spare, at least 5 km - not the whole default circle, as the server's time grows with the area.
        /// </summary>
        public static double TripRadiusKm(double tripKm)
        {
            return System.Math.Max(5, tripKm / 2 + 3);
        }

        /// <summary>The Overpass query for the roads within radiusKm of a point.</summary>
        public static string Query(double lat, double lon, double radiusKm)
        {
            // only the road types a tractor uses (footways, cycleways and the like are most of a town's ways), the ways
            // with their tags and node lists, then their nodes without tags: a fraction of the size of everything
            var inv = CultureInfo.InvariantCulture;
            return "[out:json][timeout:300];way[\"highway\"~\"^(primary|primary_link|secondary|secondary_link|tertiary|tertiary_link|" +
                "unclassified|residential|living_street|service|track|road)$\"](around:" + ((int)(radiusKm * 1000)).ToString(inv) + "," +
                lat.ToString(inv) + "," + lon.ToString(inv) + ");out body qt;>;out skel qt;";
        }

        public static RoadNetwork Parse(string json, double centreLat, double centreLon, double radiusKm)
        {
            var net = new RoadNetwork(centreLat, centreLon, radiusKm);
            JArray elements = (JArray)JObject.Parse(json)["elements"];
            if (elements == null) return net;

            // nodes first (the answer lists them after the ways), then the ways through them
            foreach (JToken e in elements)
            {
                if ((string)e["type"] != "node") continue;
                net.AddNode((long)e["id"], (double)e["lat"], (double)e["lon"]);
            }
            foreach (JToken e in elements)
            {
                if ((string)e["type"] != "way") continue;
                JToken tags = e["tags"];
                string highway = (string)tags?["highway"];
                if (!TractorProfile.IsAllowed(highway)) continue;

                string oneway = (string)tags?["oneway"];
                bool isOneway = oneway == "yes" || oneway == "1" || oneway == "true";
                bool reversed = oneway == "-1";
                var ids = new List<long>();
                foreach (JToken id in (JArray)e["nodes"]) ids.Add((long)id);
                if (reversed) ids.Reverse();
                string name = (string)tags?["name"] ?? (string)tags?["ref"];
                net.AddWay(ids, TractorProfile.ClassOf(highway), isOneway || reversed, name);
            }
            return net.Compact();
        }
    }
}
