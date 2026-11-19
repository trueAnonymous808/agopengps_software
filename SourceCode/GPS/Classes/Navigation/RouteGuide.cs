using System;
using System.Collections.Generic;

namespace AgOpenGPS.Navigation
{
    public class GuideState
    {
        public Manoeuvre NextTurn;
        public double NextTurnDistanceM;
        public string NextRoadName;
        public double RemainingM;
        public double RemainingS;
        public bool OffRoute;       // this fix is more than OffRouteM from the route
        public bool NeedsReplan;    // off the route for ReplanAfterFixes fixes in a row
        public bool Arrived;
        public int Segment;         // the route leg the tractor is on (from Points[Segment] to Points[Segment + 1])
        public NavPoint OnRoute;    // the point of the route nearest to the tractor: the route ahead starts here
    }

    // Follows a route fix by fix: where on the route the tractor is, what comes next and how far, how much is left,
    // and whether it has left the route or arrived.
    public class RouteGuide
    {
        public const double OffRouteM = 50;
        public const int ReplanAfterFixes = 3;
        public const double ArriveM = 30;

        private const double MetresPerDegree = 6371000 * Math.PI / 180;

        private readonly Route route;
        private readonly double[] along;   // distance from the start to each point
        private int offFixes;

        public Route Route { get { return route; } }

        public RouteGuide(Route route)
        {
            this.route = route;
            along = new double[route.Points.Count];
            for (int i = 1; i < route.Points.Count; i++)
                along[i] = along[i - 1] + RoadNetwork.DistanceM(route.Points[i - 1].Lat, route.Points[i - 1].Lon, route.Points[i].Lat, route.Points[i].Lon);
        }

        public GuideState Update(double lat, double lon)
        {
            List<NavPoint> p = route.Points;
            var state = new GuideState();
            double total = along[along.Length - 1];

            // the nearest point of the route: segment and how far along it
            int segment = 0;
            double bestOff = double.MaxValue, bestAlong = 0, bestT = 0;
            double k = Math.Cos(lat * Math.PI / 180);
            for (int i = 0; i + 1 < p.Count; i++)
            {
                double ax = (p[i].Lon - lon) * k * MetresPerDegree, ay = (p[i].Lat - lat) * MetresPerDegree;
                double bx = (p[i + 1].Lon - lon) * k * MetresPerDegree, by = (p[i + 1].Lat - lat) * MetresPerDegree;
                double dx = bx - ax, dy = by - ay;
                double len2 = dx * dx + dy * dy;
                double t = len2 > 0 ? Math.Max(0, Math.Min(1, -(ax * dx + ay * dy) / len2)) : 0;
                double px = ax + t * dx, py = ay + t * dy;
                double off = Math.Sqrt(px * px + py * py);
                if (off < bestOff)
                {
                    bestOff = off;
                    segment = i;
                    bestT = t;
                    bestAlong = along[i] + t * Math.Sqrt(len2);
                }
            }
            if (p.Count == 1) bestOff = RoadNetwork.DistanceM(lat, lon, p[0].Lat, p[0].Lon);

            state.Segment = segment;
            state.OnRoute = p.Count == 1 ? p[0] : new NavPoint(
                p[segment].Lat + bestT * (p[segment + 1].Lat - p[segment].Lat),
                p[segment].Lon + bestT * (p[segment + 1].Lon - p[segment].Lon));

            state.OffRoute = bestOff > OffRouteM;
            offFixes = state.OffRoute ? offFixes + 1 : 0;
            state.NeedsReplan = offFixes >= ReplanAfterFixes;

            state.RemainingM = Math.Max(0, total - bestAlong);
            state.RemainingS = route.LengthM > 0 ? state.RemainingM * route.TimeS / route.LengthM : 0;

            NavPoint end = p[p.Count - 1];
            state.Arrived = RoadNetwork.DistanceM(lat, lon, end.Lat, end.Lon) <= ArriveM;

            // the first step that lies ahead of the segment the tractor is on
            foreach (RouteStep step in route.Steps)
            {
                if (step.PointIndex <= segment) continue;
                state.NextTurn = step.Turn;
                state.NextRoadName = step.RoadName;
                state.NextTurnDistanceM = Math.Max(0, along[step.PointIndex] - bestAlong);
                break;
            }
            return state;
        }
    }
}
