namespace AgOpenGPS.Navigation
{
    // OffRoad (always last: the stored road map keeps the numbers) is the straight way across a field to or from the road
    public enum RoadClass { Primary, Secondary, Tertiary, Unclassified, Residential, Service, Track, Other, OffRoad }

    // Where a tractor may drive and how fast it goes there. Motorways and expressways are closed to it, and so are the
    // ways meant for walking or cycling; farm and field tracks are just what it needs to reach the fields.
    public static class TractorProfile
    {
        public static bool IsAllowed(string highway)
        {
            switch (highway)
            {
                case null:
                case "":
                case "motorway":
                case "motorway_link":
                case "trunk":
                case "trunk_link":
                case "footway":
                case "cycleway":
                case "path":
                case "steps":
                case "pedestrian":
                case "bridleway":
                case "construction":
                case "proposed":
                case "platform":
                case "corridor":
                case "elevator":
                case "bus_guideway":
                case "raceway":
                    return false;
                default:
                    return true;
            }
        }

        public static RoadClass ClassOf(string highway)
        {
            switch (highway)
            {
                case "primary":
                case "primary_link": return RoadClass.Primary;
                case "secondary":
                case "secondary_link": return RoadClass.Secondary;
                case "tertiary":
                case "tertiary_link": return RoadClass.Tertiary;
                case "unclassified": return RoadClass.Unclassified;
                case "residential":
                case "living_street": return RoadClass.Residential;
                case "service": return RoadClass.Service;
                case "track": return RoadClass.Track;
                default: return RoadClass.Other;
            }
        }

        public static double SpeedKmh(RoadClass roadClass)
        {
            switch (roadClass)
            {
                case RoadClass.Primary:
                case RoadClass.Secondary: return 40;
                case RoadClass.Tertiary:
                case RoadClass.Unclassified:
                case RoadClass.Residential: return 30;
                case RoadClass.Service:
                case RoadClass.Track: return 15;
                case RoadClass.OffRoad: return 8;
                default: return 20;
            }
        }
    }
}
