using System;
using System.Collections.Generic;
using System.IO;

namespace AgOpenGPS.Navigation
{
    public struct RoadEdge
    {
        public int To;
        public double LengthM;
        public RoadClass Class;
        public string Name;
    }

    // The roads a tractor may use around a centre point, as a graph: nodes (positions) and directed edges between them.
    // Downloaded once from OpenStreetMap (OverpassParser) and stored on disk, so routes can be planned without internet.
    public class RoadNetwork
    {
        private const int FormatVersion = 1;

        private readonly List<double> lats = new List<double>();
        private readonly List<double> lons = new List<double>();
        private readonly List<List<RoadEdge>> edges = new List<List<RoadEdge>>();
        private readonly Dictionary<long, int> indexOfId = new Dictionary<long, int>();

        public double CentreLat { get; private set; }
        public double CentreLon { get; private set; }
        public double RadiusKm { get; private set; }
        public int NodeCount { get { return lats.Count; } }

        public RoadNetwork(double centreLat, double centreLon, double radiusKm)
        {
            CentreLat = centreLat;
            CentreLon = centreLon;
            RadiusKm = radiusKm;
        }

        /// <summary>Adds a node by its OpenStreetMap id (a second call with the same id is ignored).</summary>
        public void AddNode(long id, double lat, double lon)
        {
            if (indexOfId.ContainsKey(id)) return;
            indexOfId[id] = AddPoint(lat, lon);
        }

        private int AddPoint(double lat, double lon)
        {
            lats.Add(lat);
            lons.Add(lon);
            edges.Add(new List<RoadEdge>());
            return lats.Count - 1;
        }

        /// <summary>Adds a way through the given OpenStreetMap node ids (unknown ids are skipped).</summary>
        public void AddWay(IList<long> nodeIds, RoadClass roadClass, bool oneway, string name)
        {
            int previous = -1;
            foreach (long id in nodeIds)
            {
                int index;
                if (!indexOfId.TryGetValue(id, out index)) { previous = -1; continue; }
                if (previous >= 0 && previous != index)
                {
                    double length = DistanceM(lats[previous], lons[previous], lats[index], lons[index]);
                    edges[previous].Add(new RoadEdge { To = index, LengthM = length, Class = roadClass, Name = name });
                    if (!oneway) edges[index].Add(new RoadEdge { To = previous, LengthM = length, Class = roadClass, Name = name });
                }
                previous = index;
            }
        }

        /// <summary>Drops the nodes no road uses (the motorway nodes, for instance), keeping the graph small.</summary>
        public RoadNetwork Compact()
        {
            var used = new bool[NodeCount];
            for (int i = 0; i < NodeCount; i++)
            {
                if (edges[i].Count > 0) used[i] = true;
                foreach (RoadEdge e in edges[i]) used[e.To] = true;
            }
            var result = new RoadNetwork(CentreLat, CentreLon, RadiusKm);
            var map = new int[NodeCount];
            for (int i = 0; i < NodeCount; i++) map[i] = used[i] ? result.AddPoint(lats[i], lons[i]) : -1;
            for (int i = 0; i < NodeCount; i++)
            {
                if (!used[i]) continue;
                foreach (RoadEdge e in edges[i])
                    result.edges[map[i]].Add(new RoadEdge { To = map[e.To], LengthM = e.LengthM, Class = e.Class, Name = e.Name });
            }
            return result;
        }

        public IEnumerable<RoadEdge> EdgesFrom(int node)
        {
            return edges[node];
        }

        public double Lat(int node) { return lats[node]; }
        public double Lon(int node) { return lons[node]; }

        /// <summary>The node closest to a position, or -1 for an empty network.</summary>
        public int Nearest(double lat, double lon)
        {
            int best = -1;
            double bestD = double.MaxValue;
            double k = Math.Cos(lat * Math.PI / 180);
            for (int i = 0; i < lats.Count; i++)
            {
                double dy = lats[i] - lat, dx = (lons[i] - lon) * k;
                double d = dx * dx + dy * dy;
                if (d < bestD) { bestD = d; best = i; }
            }
            return best;
        }

        /// <summary>
        /// The road nearest to a position: the edge from-to and how far along it (0..1) the nearest point lies.
        /// A straight track is often mapped with nodes only at its ends, hundreds of metres apart, so the nearest
        /// node can be far away while the road runs right under the tractor. False for a network without roads.
        /// </summary>
        public bool NearestEdge(double lat, double lon, out int from, out int to, out double t)
        {
            from = to = -1;
            t = 0;
            double best = double.MaxValue;
            double k = Math.Cos(lat * Math.PI / 180);
            for (int i = 0; i < lats.Count; i++)
            {
                double ax = (lons[i] - lon) * k, ay = lats[i] - lat;
                foreach (RoadEdge e in edges[i])
                {
                    double bx = (lons[e.To] - lon) * k, by = lats[e.To] - lat;
                    double dx = bx - ax, dy = by - ay;
                    double len2 = dx * dx + dy * dy;
                    double s = len2 > 0 ? Math.Max(0, Math.Min(1, -(ax * dx + ay * dy) / len2)) : 0;
                    double px = ax + s * dx, py = ay + s * dy;
                    double d = px * px + py * py;
                    if (d < best) { best = d; from = i; to = e.To; t = s; }
                }
            }
            return from >= 0;
        }

        /// <summary>The edge from one node to another, if the road may be driven that way.</summary>
        public bool TryGetEdge(int from, int to, out RoadEdge edge)
        {
            foreach (RoadEdge e in edges[from])
            {
                if (e.To == to) { edge = e; return true; }
            }
            edge = default(RoadEdge);
            return false;
        }

        /// <summary>Whether a position lies inside the downloaded circle.</summary>
        public bool Covers(double lat, double lon)
        {
            return DistanceM(CentreLat, CentreLon, lat, lon) <= RadiusKm * 1000;
        }

        public void Save(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temp = path + ".tmp";
            using (var w = new BinaryWriter(File.Create(temp)))
            {
                w.Write(FormatVersion);
                w.Write(CentreLat);
                w.Write(CentreLon);
                w.Write(RadiusKm);
                w.Write(lats.Count);
                for (int i = 0; i < lats.Count; i++)
                {
                    w.Write(lats[i]);
                    w.Write(lons[i]);
                }
                for (int i = 0; i < lats.Count; i++)
                {
                    w.Write(edges[i].Count);
                    foreach (RoadEdge e in edges[i])
                    {
                        w.Write(e.To);
                        w.Write((float)e.LengthM);
                        w.Write((byte)e.Class);
                        w.Write(e.Name ?? "");
                    }
                }
            }
            // a half written file must never replace a good one
            if (File.Exists(path)) File.Delete(path);
            File.Move(temp, path);
        }

        /// <summary>Reads a stored network; null when there is none or it is unreadable.</summary>
        public static RoadNetwork Load(string path)
        {
            if (!File.Exists(path)) return null;
            try
            {
                using (var r = new BinaryReader(File.OpenRead(path)))
                {
                    if (r.ReadInt32() != FormatVersion) return null;
                    var net = new RoadNetwork(r.ReadDouble(), r.ReadDouble(), r.ReadDouble());
                    int count = r.ReadInt32();
                    for (int i = 0; i < count; i++) net.AddPoint(r.ReadDouble(), r.ReadDouble());
                    for (int i = 0; i < count; i++)
                    {
                        int n = r.ReadInt32();
                        for (int j = 0; j < n; j++)
                        {
                            int to = r.ReadInt32();
                            float length = r.ReadSingle();
                            var cls = (RoadClass)r.ReadByte();
                            string name = r.ReadString();
                            net.edges[i].Add(new RoadEdge { To = to, LengthM = length, Class = cls, Name = name.Length == 0 ? null : name });
                        }
                    }
                    return net;
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>Great-circle distance in metres.</summary>
        public static double DistanceM(double lat1, double lon1, double lat2, double lon2)
        {
            const double R = 6371000;
            double p1 = lat1 * Math.PI / 180, p2 = lat2 * Math.PI / 180;
            double dp = p2 - p1, dl = (lon2 - lon1) * Math.PI / 180;
            double a = Math.Sin(dp / 2) * Math.Sin(dp / 2) + Math.Cos(p1) * Math.Cos(p2) * Math.Sin(dl / 2) * Math.Sin(dl / 2);
            return 2 * R * Math.Asin(Math.Min(1, Math.Sqrt(a)));
        }
    }
}
