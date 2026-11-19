using System;
using System.Collections.Generic;

namespace AgOpenGPS.Navigation
{
    public enum Manoeuvre { Straight, SlightLeft, Left, SharpLeft, SlightRight, Right, SharpRight, Arrive }

    public struct NavPoint
    {
        public double Lat;
        public double Lon;
        public NavPoint(double lat, double lon) { Lat = lat; Lon = lon; }
    }

    public class RouteStep
    {
        public Manoeuvre Turn;
        public int PointIndex;      // the route point where the turn is made
        public string RoadName;     // the road the turn leads onto
    }

    public class Route
    {
        public List<NavPoint> Points = new List<NavPoint>();
        public List<RouteStep> Steps = new List<RouteStep>();
        public double LengthM;
        public double TimeS;
    }

    // Plans the quickest tractor route on a road network (A* on travel time) and finds the turns along it.
    public static class RoutePlanner
    {
        private const double FastestKmh = 40;

        /// <summary>The route between the network points nearest to the two positions; null when there is none.</summary>
        public static Route Plan(RoadNetwork net, double fromLat, double fromLon, double toLat, double toLon)
        {
            if (net == null || net.NodeCount == 0) return null;
            int start = net.Nearest(fromLat, fromLon), goal = net.Nearest(toLat, toLon);
            if (start < 0 || goal < 0) return null;

            var time = new Dictionary<int, double> { [start] = 0 };
            var came = new Dictionary<int, int>();
            var cameBy = new Dictionary<int, RoadEdge>();
            var closed = new HashSet<int>();
            var open = new MinHeap();
            open.Push(start, Heuristic(net, start, goal));

            while (open.Count > 0)
            {
                int node = open.Pop();
                if (node == goal) return Build(net, start, goal, came, cameBy, time[goal]);
                if (!closed.Add(node)) continue;
                double t = time[node];
                foreach (RoadEdge e in net.EdgesFrom(node))
                {
                    if (closed.Contains(e.To)) continue;
                    double candidate = t + e.LengthM / (TractorProfile.SpeedKmh(e.Class) / 3.6);
                    double known;
                    if (time.TryGetValue(e.To, out known) && known <= candidate) continue;
                    time[e.To] = candidate;
                    came[e.To] = node;
                    cameBy[e.To] = e;
                    open.Push(e.To, candidate + Heuristic(net, e.To, goal));
                }
            }
            return null;
        }

        private static double Heuristic(RoadNetwork net, int a, int b)
        {
            return RoadNetwork.DistanceM(net.Lat(a), net.Lon(a), net.Lat(b), net.Lon(b)) / (FastestKmh / 3.6);
        }

        private static Route Build(RoadNetwork net, int start, int goal, Dictionary<int, int> came, Dictionary<int, RoadEdge> cameBy, double timeS)
        {
            var nodes = new List<int> { goal };
            var via = new List<RoadEdge>();
            for (int n = goal; n != start; n = came[n])
            {
                via.Add(cameBy[n]);
                nodes.Add(came[n]);
            }
            nodes.Reverse();
            via.Reverse();   // via[i] leads from nodes[i] to nodes[i + 1]

            var route = new Route { TimeS = timeS };
            foreach (int n in nodes) route.Points.Add(new NavPoint(net.Lat(n), net.Lon(n)));
            foreach (RoadEdge e in via) route.LengthM += e.LengthM;

            for (int i = 1; i < nodes.Count - 1; i++)
            {
                if (!IsJunction(net, nodes[i])) continue;
                double change = TurnAngle(route.Points[i - 1], route.Points[i], route.Points[i + 1]);
                Manoeuvre turn = Classify(change);
                if (turn == Manoeuvre.Straight) continue;
                route.Steps.Add(new RouteStep { Turn = turn, PointIndex = i, RoadName = via[i].Name });
            }
            route.Steps.Add(new RouteStep { Turn = Manoeuvre.Arrive, PointIndex = nodes.Count - 1 });
            return route;
        }

        // a node where more than one other road meets: only there is a turn worth announcing
        private static bool IsJunction(RoadNetwork net, int node)
        {
            var neighbours = new HashSet<int>();
            foreach (RoadEdge e in net.EdgesFrom(node)) neighbours.Add(e.To);
            return neighbours.Count > 2;
        }

        /// <summary>The change of direction at b, in degrees: positive to the right, negative to the left.</summary>
        public static double TurnAngle(NavPoint a, NavPoint b, NavPoint c)
        {
            double change = Bearing(b, c) - Bearing(a, b);
            while (change > 180) change -= 360;
            while (change < -180) change += 360;
            return change;
        }

        /// <summary>Compass bearing from a to b in degrees (0 = north, 90 = east).</summary>
        public static double Bearing(NavPoint a, NavPoint b)
        {
            double dy = b.Lat - a.Lat;
            double dx = (b.Lon - a.Lon) * Math.Cos(a.Lat * Math.PI / 180);
            double deg = Math.Atan2(dx, dy) * 180 / Math.PI;
            return deg < 0 ? deg + 360 : deg;
        }

        public static Manoeuvre Classify(double change)
        {
            double a = Math.Abs(change);
            if (a < 20) return Manoeuvre.Straight;
            bool right = change > 0;
            if (a < 45) return right ? Manoeuvre.SlightRight : Manoeuvre.SlightLeft;
            if (a < 135) return right ? Manoeuvre.Right : Manoeuvre.Left;
            return right ? Manoeuvre.SharpRight : Manoeuvre.SharpLeft;
        }

        // a small binary heap of nodes by priority (the .NET Framework has no priority queue)
        private sealed class MinHeap
        {
            private readonly List<KeyValuePair<double, int>> items = new List<KeyValuePair<double, int>>();

            public int Count { get { return items.Count; } }

            public void Push(int node, double priority)
            {
                items.Add(new KeyValuePair<double, int>(priority, node));
                int i = items.Count - 1;
                while (i > 0)
                {
                    int parent = (i - 1) / 2;
                    if (items[parent].Key <= items[i].Key) break;
                    var swap = items[parent]; items[parent] = items[i]; items[i] = swap;
                    i = parent;
                }
            }

            public int Pop()
            {
                int top = items[0].Value;
                int last = items.Count - 1;
                items[0] = items[last];
                items.RemoveAt(last);
                int i = 0;
                while (true)
                {
                    int l = 2 * i + 1, r = l + 1, m = i;
                    if (l < items.Count && items[l].Key < items[m].Key) m = l;
                    if (r < items.Count && items[r].Key < items[m].Key) m = r;
                    if (m == i) break;
                    var swap = items[m]; items[m] = items[i]; items[i] = swap;
                    i = m;
                }
                return top;
            }
        }
    }
}
