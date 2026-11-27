using AgLibrary.Logging;
using AgOpenGPS.Core.Models;
using AgOpenGPS.Navigation;
using System;
using System.Collections.Generic;

namespace AgOpenGPS
{
    // EXPERIMENTAL route autosteer: while navigating, the autosteer module steers along the route - in the right-hand
    // lane on roads, up to 15 km/h, never in reverse and not on the last metres. The driver keeps speed and brakes and can
    // take over at any time: the steer switch, the menu, or leaving the conditions ends it.
    public partial class FormGPS
    {
        private bool routeSteerArmed;      // the driver switched it on (this session only, never saved)
        private bool routeSteerActive;     // it steers right now
        private bool routeSteerHasSteered; // it has steered since it was switched on
        private bool routeSteerSwitchWas;
        private SteerBlock routeSteerBlock = SteerBlock.NoRoute;

        public bool RouteSteerArmed => routeSteerArmed;

        /// <summary>Route autosteer on roads: metres right (+) or left (-) of the mapped centre line; saved at once.</summary>
        public double RouteSteerRoadOffset
        {
            get { return RouteSteer.ClampOffset(Properties.Settings.Default.setNavSteer_RoadOffset); }
            set { Properties.Settings.Default.setNavSteer_RoadOffset = RouteSteer.ClampOffset(value); Properties.Settings.Default.Save(); }
        }

        /// <summary>The same on field tracks.</summary>
        public double RouteSteerTrackOffset
        {
            get { return RouteSteer.ClampOffset(Properties.Settings.Default.setNavSteer_TrackOffset); }
            set { Properties.Settings.Default.setNavSteer_TrackOffset = RouteSteer.ClampOffset(value); Properties.Settings.Default.Save(); }
        }

        public void SetRouteSteer(bool on)
        {
            if (on && !IsNavigating) on = false;
            routeSteerArmed = on;
            routeSteerHasSteered = false;
            routeSteerSwitchWas = mc.steerSwitchHigh;
            routeSteerBlock = SteerBlock.NoRoute;
            if (!on) SetRouteSteerActive(false);
            Log.EventWriter("Route autosteer (experimental) " + (on ? "armed" : "off"));
        }

        // ends it with a message, so the driver knows the wheel is theirs again
        private void DisarmRouteSteer(string why)
        {
            if (!routeSteerArmed) return;
            SetRouteSteer(false);
            TimedMessageBox(4000, AgLibrary.Localization.UiTranslator.TrText("Route autosteer off"), why);
        }

        private void SetRouteSteerActive(bool active)
        {
            if (active == routeSteerActive) return;
            routeSteerActive = active;
            // always audible, whatever the sound settings: the driver must hear every handover
            try { (active ? sounds.sndAutoSteerOn : sounds.sndAutoSteerOff).Play(); }
            catch (Exception) { }
            if (active) routeSteerHasSteered = true;
        }

        /// <summary>Called on every fix before the autosteer packet is filled: sets the steer angle while it steers.</summary>
        private void UpdateRouteSteer()
        {
            if (!routeSteerArmed) return;
            try
            {
                if (!IsNavigating || isJobStarted || vehicle.isInFreeDriveMode) { SetRouteSteer(false); return; }

                // the steer switch released (the driver took the wheel or switched it off)
                bool steerSwitch = mc.steerSwitchHigh;
                if (steerSwitch && !routeSteerSwitchWas)
                {
                    routeSteerSwitchWas = steerSwitch;
                    DisarmRouteSteer(AgLibrary.Localization.UiTranslator.TrText("Steer switch"));
                    return;
                }
                routeSteerSwitchWas = steerSwitch;

                RouteGuide guide = navGuide;
                GuideState s = navState;
                if (guide == null || s == null || AppModel.LocalPlane == null) { Block(SteerBlock.NoRoute); return; }

                // the route near the tractor in local metres: one point behind, then ahead until well past the look-ahead
                List<NavPoint> points = guide.Route.Points;
                List<RoadClass> legs = guide.Route.Legs;
                int first = Math.Max(0, s.Segment - 1);
                var east = new List<double>();
                var north = new List<double>();
                double lookAhead = vehicle.UpdateGoalPointDistance();
                double reach = Math.Max(RouteSteer.MinLookAheadM, lookAhead) + 30, run = 0;
                for (int i = first; i < points.Count; i++)
                {
                    GeoCoord g = AppModel.LocalPlane.ConvertWgs84ToGeoCoord(new Wgs84(points[i].Lat, points[i].Lon));
                    if (east.Count > 0)
                    {
                        double de = g.Easting - east[east.Count - 1], dn = g.Northing - north[north.Count - 1];
                        if (i > s.Segment) run += Math.Sqrt(de * de + dn * dn);
                    }
                    east.Add(g.Easting);
                    north.Add(g.Northing);
                    if (run > reach) break;
                }

                // roads are mapped on their centre line: drive in the right-hand lane (field tracks in the middle)
                var offsets = new List<double>();
                for (int k = 0; k + 1 < east.Count; k++)
                    offsets.Add(first + k < legs.Count ? RouteSteer.LaneOffset(legs[first + k], RouteSteerRoadOffset, RouteSteerTrackOffset) : 0);
                RouteSteer.OffsetRight(east, north, offsets, out double[] laneE, out double[] laneN);

                // junctions are sharp points on the map: drive them as arcs the tractor can follow smoothly
                double cornerRadius = RouteSteer.CornerRadius(vehicle.VehicleConfig.Wheelbase, vehicle.maxSteerAngle);
                RouteSteer.RoundCorners(laneE, laneN, cornerRadius, out List<double> pathE, out List<double> pathN);

                SteerResult r = RouteSteer.PurePursuit(pathE, pathN, pivotAxlePos.easting, pivotAxlePos.northing, fixHeading,
                    lookAhead, vehicle.VehicleConfig.Wheelbase, vehicle.maxSteerAngle);
                SteerBlock block = RouteSteer.Check(guide.Route, s.Segment, r.CrossTrackM, avgSpeed, isReverse, s.RemainingM);
                // the vehicle's own safe steering speed counts too, when it is lower
                if (block == SteerBlock.None && !timerSim.Enabled && avgSpeed > vehicle.maxSteerSpeed) block = SteerBlock.TooFast;
                // an RTK fix with fresh corrections only: with a float, DGPS or single position (metres off) it would
                // steer the tractor beside its lane - and corrections older than a few seconds are on their way there
                if (block == SteerBlock.None && !timerSim.Enabled && (pn.fixQuality != 4 || pn.age > RouteSteer.MaxCorrectionAgeS)) block = SteerBlock.NoRtk;

                if (block != SteerBlock.None) { Block(block); return; }

                routeSteerBlock = SteerBlock.None;
                SetRouteSteerActive(true);
                guidanceLineSteerAngle = (short)(r.AngleDeg * 100);
                guidanceLineDistanceOff = GuidanceDistanceMm(r.CrossTrackM);
            }
            catch (Exception ex)
            {
                Log.EventWriter("Route autosteer error: " + ex.Message);
                SetRouteSteer(false);
            }
        }

        // not now: before it first steered it keeps waiting (e.g. still on the public road to the track);
        // once it has steered, any stop ends it - it never takes the wheel back by itself
        private void Block(SteerBlock block)
        {
            routeSteerBlock = block;
            SetRouteSteerActive(false);
            if (routeSteerHasSteered) DisarmRouteSteer(RouteSteerReason(block));
        }

        /// <summary>The third line of the navigation bar while route autosteer is on; null when it is off.</summary>
        private string RouteSteerStatus()
        {
            if (!routeSteerArmed) return null;
            if (routeSteerActive) return AgLibrary.Localization.UiTranslator.TrText("Autosteer on the route (experimental)");
            return AgLibrary.Localization.UiTranslator.TrText("Autosteer waiting") + ": " + RouteSteerReason(routeSteerBlock);
        }

        private static string RouteSteerReason(SteerBlock block)
        {
            switch (block)
            {
                case SteerBlock.TooFast: return AgLibrary.Localization.UiTranslator.TrText("faster than 15 km/h");
                case SteerBlock.OffRoute: return AgLibrary.Localization.UiTranslator.TrText("too far from the route");
                case SteerBlock.Reversing: return AgLibrary.Localization.UiTranslator.TrText("reversing");
                case SteerBlock.NearArrival: return AgLibrary.Localization.UiTranslator.TrText("almost there");
                case SteerBlock.NoRtk: return AgLibrary.Localization.UiTranslator.TrText("no RTK fix");
                default: return AgLibrary.Localization.UiTranslator.TrText("no route");
            }
        }
    }
}
