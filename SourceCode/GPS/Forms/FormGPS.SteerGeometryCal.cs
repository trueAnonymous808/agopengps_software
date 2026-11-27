namespace AgOpenGPS
{
    // Steering geometry calibration: while its window is open, every GPS fix goes to the calibrator.
    public partial class FormGPS
    {
        public readonly CSteerGeometryCal steerGeometryCal = new CSteerGeometryCal();
        public bool isSteerGeometryCalOn;

        private bool geometryCalFailed;

        private void FeedSteerGeometryCal()
        {
            if (geometryCalFailed || timerSim.Enabled) return;   // nothing learnt in the simulator
            try
            {
                FeedSteerGeometryCalNow();
            }
            catch (System.Exception ex)
            {
                // the calibration runs in the position path: an error must never stop the position
                geometryCalFailed = true;
                AgLibrary.Logging.Log.EventWriter("Steering geometry calibration switched off after an error: " + ex);
            }
        }

        private void FeedSteerGeometryCalNow()
        {
            // while its window is open, or all the time for fully automatic tuning
            if (!isSteerGeometryCalOn && !SteerAutoOn) return;
            // an RTK fix (4) is needed for a heading change per metre that means something; the simulator is exact
            bool goodFix = pn.fixQuality == 4 || timerSim.Enabled;
            steerGeometryCal.AddFix(secondsSinceStart, pivotAxlePos.easting, pivotAxlePos.northing, fixHeading,
                mc.actualSteerAngleDegrees, avgSpeed, isReverse, goodFix, vehicle.VehicleConfig.Wheelbase);
        }
    }
}
