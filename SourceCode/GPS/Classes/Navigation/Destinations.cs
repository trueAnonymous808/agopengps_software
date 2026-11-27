using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace AgOpenGPS.Navigation
{
    public class Destination
    {
        public string Name;
        public double Lat;
        public double Lon;
        public double DistanceKm;   // straight line from where the tractor was when the list was made

        /// <summary>
        /// A search result only to the street or the village (the map has no house number there): the driver pins
        /// the exact place on the map.
        /// </summary>
        public bool Approximate;
    }

    // Where the driver can be taken: the saved fields (their start position) and the places an address search finds.
    public static class Destinations
    {
        /// <summary>The saved fields, nearest first. A folder without a readable Field.txt is left out.</summary>
        public static List<Destination> Fields(string fieldsDirectory, double lat, double lon)
        {
            var result = new List<Destination>();
            if (string.IsNullOrEmpty(fieldsDirectory) || !Directory.Exists(fieldsDirectory)) return result;
            foreach (string dir in Directory.GetDirectories(fieldsDirectory))
            {
                string file = Path.Combine(dir, "Field.txt");
                if (!File.Exists(file)) continue;
                try
                {
                    // the start position is the 9th line, after the header the "Drive In" search skips too
                    string[] lines = File.ReadLines(file).Take(9).ToArray();
                    if (lines.Length < 9) continue;
                    string[] parts = lines[8].Split(',');
                    double fLat = double.Parse(parts[0], CultureInfo.InvariantCulture);
                    double fLon = double.Parse(parts[1], CultureInfo.InvariantCulture);
                    result.Add(new Destination
                    {
                        Name = Path.GetFileName(dir),
                        Lat = fLat,
                        Lon = fLon,
                        DistanceKm = RoadNetwork.DistanceM(lat, lon, fLat, fLon) / 1000
                    });
                }
                catch (Exception)
                {
                    // a damaged field file is just not offered
                }
            }
            return result.OrderBy(d => d.DistanceKm).ToList();
        }

        /// <summary>The OpenStreetMap Nominatim address search for a query, with results in the given language.</summary>
        public static string NominatimUrl(string query, string language)
        {
            return "https://nominatim.openstreetmap.org/search?q=" + Uri.EscapeDataString(query ?? "") +
                "&format=jsonv2&limit=8&accept-language=" + Uri.EscapeDataString(language ?? "en");
        }

        /// <summary>"46.25, 20.15", "46,25 20,15" or "46.25;20.15": a point; anything else: null.</summary>
        public static Destination ParseCoordinates(string text)
        {
            var numbers = System.Text.RegularExpressions.Regex.Matches(text, @"-?\d+(?:[.,]\d+)?");
            if (numbers.Count != 2) return null;
            string rest = System.Text.RegularExpressions.Regex.Replace(text, @"-?\d+(?:[.,]\d+)?", "");
            if (rest.Trim(' ', ',', ';', '\t').Length > 0) return null;
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            if (!double.TryParse(numbers[0].Value.Replace(',', '.'), System.Globalization.NumberStyles.Float, inv, out double lat)) return null;
            if (!double.TryParse(numbers[1].Value.Replace(',', '.'), System.Globalization.NumberStyles.Float, inv, out double lon)) return null;
            if (Math.Abs(lat) > 90 || Math.Abs(lon) > 180) return null;
            return new Destination { Name = lat.ToString("0.00000", inv) + ", " + lon.ToString("0.00000", inv), Lat = lat, Lon = lon };
        }

        public static List<Destination> ParseNominatim(string json)
        {
            var result = new List<Destination>();
            foreach (JToken place in JArray.Parse(json))
            {
                // place_rank 30: a house or a building; less: a street (26), a village, a region...
                JToken rank = place["place_rank"];
                result.Add(new Destination
                {
                    Name = (string)place["display_name"],
                    Lat = double.Parse((string)place["lat"], CultureInfo.InvariantCulture),
                    Lon = double.Parse((string)place["lon"], CultureInfo.InvariantCulture),
                    Approximate = rank != null && rank.Type == JTokenType.Integer && (int)rank < 30
                });
            }
            return result;
        }
    }
}
