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
        public List<RoadClass> Legs = new List<RoadClass>();   // the road class from Points[i] to Points[i + 1]
        public double LengthM;
        public double TimeS;
    }

    // Plans the quickest tractor route on a road network (A* on travel time) and finds the turns along it.
    public static class RoutePlanner
    {
        private const double FastestKmh = 40;

        // where a position meets the road network: a point on an edge, with the edge's ends and how far along it lies
        private struct Snap
        {
            public int A, B;          // the edge (A to B always exists)
            public double T;          // 0 at A, 1 at B
            public NavPoint Point;
            public RoadEdge Edge;     // A to B
        }

        private static bool SnapTo(RoadNetwork net, double lat, double lon, out Snap snap)
        {
            snap = new Snap();
            int a, b;
            double t;
            if (!net.NearestEdge(lat, lon, out a, out b, out t)) return false;
            RoadEdge edge;
            net.TryGetEdge(a, b, out edge);
            snap.A = a;
            snap.B = b;
            snap.T = t;
            snap.Edge = edge;
            snap.Point = new NavPoint(net.Lat(a) + t * (net.Lat(b) - net.Lat(a)), net.Lon(a) + t * (net.Lon(b) - net.Lon(a)));
            return true;
        }

        private static double Seconds(double metres, RoadClass roadClass)
        {
            return metres / (TractorProfile.SpeedKmh(roadClass) / 3.6);
        }

        public const double OffRoadMinM = 15;     // closer to the road than this: already on it (map and GPS are a few metres off)
        public const double OffRoadMaxM = 2000;   // farther than this: not a way across a field

        /// <summary>
        /// The quickest route between two positions: across the field to the nearest road when the tractor stands in a
        /// field, along the roads, and from the road to the destination when that lies off the road. Null when there is none.
        /// </summary>
        public static Route Plan(RoadNetwork net, double fromLat, double fromLon, double toLat, double toLon)
        {
            Route route = PlanOnRoads(net, fromLat, fromLon, toLat, toLon);
            if (route != null) AddOffRoadEnds(route, new NavPoint(fromLat, fromLon), new NavPoint(toLat, toLon));
            return route;
        }

        /// <summary>
        /// Adds the straight ways across the field: from the tractor to where the route meets the road, and from the
        /// end of the road to the destination, each with the turn where it meets the road.
        /// </summary>
        public static void AddOffRoadEnds(Route route, NavPoint from, NavPoint to)
        {
            List<NavPoint> p = route.Points;
            if (p.Count == 0) return;

            double toGoal = DistanceM(p[p.Count - 1], to);
            if (toGoal > OffRoadMinM && toGoal <= OffRoadMaxM)
            {
                int roadEnd = p.Count - 1;
                p.Add(to);
                route.Legs.Add(RoadClass.OffRoad);
                route.LengthM += toGoal;
                route.TimeS += Seconds(toGoal, RoadClass.OffRoad);
                route.Steps.RemoveAll(s => s.Turn == Manoeuvre.Arrive);
                if (roadEnd >= 1 && DistanceM(p[roadEnd - 1], p[roadEnd]) > SameSpotM)
                {
                    Manoeuvre leave = Classify(TurnAngle(p[roadEnd - 1], p[roadEnd], to));
                    if (leave != Manoeuvre.Straight) route.Steps.Add(new RouteStep { Turn = leave, PointIndex = roadEnd });
                }
                route.Steps.Add(new RouteStep { Turn = Manoeuvre.Arrive, PointIndex = p.Count - 1 });
            }

            double toRoad = DistanceM(from, p[0]);
            if (toRoad > OffRoadMinM && toRoad <= OffRoadMaxM)
            {
                p.Insert(0, from);
                route.Legs.Insert(0, RoadClass.OffRoad);
                route.LengthM += toRoad;
                route.TimeS += Seconds(toRoad, RoadClass.OffRoad);
                foreach (RouteStep s in route.Steps) s.PointIndex++;
                if (p.Count > 2 && DistanceM(p[1], p[2]) > SameSpotM)
                {
                    // which way to go once on the road
                    Manoeuvre join = Classify(TurnAngle(p[0], p[1], p[2]));
                    if (join != Manoeuvre.Straight) route.Steps.Insert(0, new RouteStep { Turn = join, PointIndex = 1 });
                }
            }
        }

        // the quickest route between the points of the road network nearest to the two positions (anywhere along a
        // road, not only at its mapped nodes); null when there is none
        private static Route PlanOnRoads(RoadNetwork net, double fromLat, double fromLon, double toLat, double toLon)
        {
            if (net == null || net.NodeCount == 0) return null;
            Snap start, goal;
            if (!SnapTo(net, fromLat, fromLon, out start) || !SnapTo(net, toLat, toLon, out goal)) return null;

            // from the start point onto the network: forwards to B, or back to A where the road allows it
            var time = new Dictionary<int, double>();
            var came = new Dictionary<int, int>();
            var cameBy = new Dictionary<int, RoadEdge>();
            var firstLeg = new Dictionary<int, RoadEdge>();   // the start edge (its direction) leading to each source node
            var closed = new HashSet<int>();
            var open = new MinHeap();
            RoadEdge back;
            bool canGoBack = net.TryGetEdge(start.B, start.A, out back);
            AddSource(net, start.B, Seconds((1 - start.T) * start.Edge.LengthM, start.Edge.Class), start.Edge, goal, time, firstLeg, open);
            if (canGoBack) AddSource(net, start.A, Seconds(start.T * back.LengthM, back.Class), back, goal, time, firstLeg, open);

            // the same road for both: straight along it when the direction is allowed
            Route direct = Direct(start, goal, canGoBack, back);

            // the goal point is reached from A (driving A to B) or from B (driving back, where allowed)
            RoadEdge goalBack;
            bool goalFromB = net.TryGetEdge(goal.B, goal.A, out goalBack);
            double bestTime = direct != null ? direct.TimeS : double.MaxValue;
            int bestEnd = -1;

            while (open.Count > 0)
            {
                int node = open.Pop();
                if (!closed.Add(node)) continue;
                double t = time[node];
                // the straight-line estimate never overrates what is left: past this, nothing cheaper can follow
                if (t + Heuristic(net, node, goal.Point) >= bestTime) break;
                if (node == goal.A)
                {
                    double total = t + Seconds(goal.T * goal.Edge.LengthM, goal.Edge.Class);
                    if (total < bestTime) { bestTime = total; bestEnd = node; }
                }
                if (node == goal.B && goalFromB)
                {
                    double total = t + Seconds((1 - goal.T) * goalBack.LengthM, goalBack.Class);
                    if (total < bestTime) { bestTime = total; bestEnd = node; }
                }
                foreach (RoadEdge e in net.EdgesFrom(node))
                {
                    if (closed.Contains(e.To)) continue;
                    double candidate = t + Seconds(e.LengthM, e.Class);
                    double known;
                    if (time.TryGetValue(e.To, out known) && known <= candidate) continue;
                    time[e.To] = candidate;
                    came[e.To] = node;
                    cameBy[e.To] = e;
                    firstLeg.Remove(e.To);   // reached over the network now, not straight from the start point
                    open.Push(e.To, candidate + Heuristic(net, e.To, goal.Point));
                }
            }

            if (bestEnd < 0) return direct;
            RoadEdge lastLeg = bestEnd == goal.A ? goal.Edge : goalBack;
            return Build(net, start, goal, bestEnd, came, cameBy, firstLeg, lastLeg, bestTime);
        }

        private static void AddSource(RoadNetwork net, int node, double seconds, RoadEdge leg, Snap goal,
            Dictionary<int, double> time, Dictionary<int, RoadEdge> firstLeg, MinHeap open)
        {
            double known;
            if (time.TryGetValue(node, out known) && known <= seconds) return;
            time[node] = seconds;
            firstLeg[node] = leg;
            open.Push(node, seconds + Heuristic(net, node, goal.Point));
        }

        // both points on the same road: the stretch between them, when it may be driven that way
        private static Route Direct(Snap start, Snap goal, bool canGoBack, RoadEdge back)
        {
            double goalT;
            if (goal.A == start.A && goal.B == start.B) goalT = goal.T;
            else if (goal.A == start.B && goal.B == start.A) goalT = 1 - goal.T;
            else return null;
            bool forwards = goalT >= start.T;
            if (!forwards && !canGoBack) return null;
            RoadEdge edge = forwards ? start.Edge : back;
            double metres = Math.Abs(goalT - start.T) * start.Edge.LengthM;
            var route = new Route { LengthM = metres, TimeS = Seconds(metres, edge.Class) };
            route.Points.Add(start.Point);
            route.Points.Add(goal.Point);
            route.Legs.Add(edge.Class);
            route.Steps.Add(new RouteStep { Turn = Manoeuvre.Arrive, PointIndex = 1 });
            return route;
        }

        private static double Heuristic(RoadNetwork net, int a, NavPoint goal)
        {
            return RoadNetwork.DistanceM(net.Lat(a), net.Lon(a), goal.Lat, goal.Lon) / (FastestKmh / 3.6);
        }

        private const double SameSpotM = 1;

        private static Route Build(RoadNetwork net, Snap start, Snap goal, int end, Dictionary<int, int> came,
            Dictionary<int, RoadEdge> cameBy, Dictionary<int, RoadEdge> firstLeg, RoadEdge lastLeg, double timeS)
        {
            var nodes = new List<int> { end };
            var via = new List<RoadEdge>();
            for (int n = end; came.ContainsKey(n); n = came[n])
            {
                via.Add(cameBy[n]);
                nodes.Add(came[n]);
            }
            nodes.Reverse();
            via.Reverse();   // via[i] leads from nodes[i] to nodes[i + 1]

            // the points with the road leading on from each: start point, the network nodes, goal point
            var points = new List<NavPoint>();
            var legs = new List<RoadEdge>();
            var nodeOf = new List<int>();   // the network node of each point, -1 for the start and goal points
            NavPoint firstNode = new NavPoint(net.Lat(nodes[0]), net.Lon(nodes[0]));
            if (DistanceM(start.Point, firstNode) > SameSpotM)
            {
                points.Add(start.Point);
                legs.Add(firstLeg[nodes[0]]);
                nodeOf.Add(-1);
            }
            for (int i = 0; i < nodes.Count; i++)
            {
                points.Add(new NavPoint(net.Lat(nodes[i]), net.Lon(nodes[i])));
                nodeOf.Add(nodes[i]);
                if (i < via.Count) legs.Add(via[i]);
            }
            if (DistanceM(goal.Point, points[points.Count - 1]) > SameSpotM)
            {
                legs.Add(lastLeg);
                points.Add(goal.Point);
                nodeOf.Add(-1);
            }

            var route = new Route { TimeS = timeS, Points = points };
            for (int i = 0; i + 1 < points.Count; i++)
            {
                route.Legs.Add(legs[i].Class);
                route.LengthM += DistanceM(points[i], points[i + 1]);
            }

            for (int i = 1; i < points.Count - 1; i++)
            {
                if (nodeOf[i] < 0 || !IsJunction(net, nodeOf[i])) continue;
                double change = TurnAngle(points[i - 1], points[i], points[i + 1]);
                Manoeuvre turn = Classify(change);
                if (turn == Manoeuvre.Straight) continue;
                route.Steps.Add(new RouteStep { Turn = turn, PointIndex = i, RoadName = legs[i].Name });
            }
            route.Steps.Add(new RouteStep { Turn = Manoeuvre.Arrive, PointIndex = points.Count - 1 });
            return route;
        }

        private static double DistanceM(NavPoint a, NavPoint b)
        {
            return RoadNetwork.DistanceM(a.Lat, a.Lon, b.Lat, b.Lon);
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
