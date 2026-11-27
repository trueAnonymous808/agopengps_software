namespace AgOpenGPS
{
    // What a tuner found, in words: shown on its card and given as the reason of an automatic change.
    internal static class SteerVerdicts
    {
        private static string Tr(string english) => AgLibrary.Localization.UiTranslator.TrText(english);

        public static string Text(TuneVerdict v, bool speedFactor = false)
        {
            switch (v)
            {
                case TuneVerdict.Good: return Tr("Fine as it is");
                case TuneVerdict.Oscillates: return Tr("Swings round the wanted angle: lower gain");
                case TuneVerdict.DeadBand: return Tr("Stops short of the wanted angle: higher minimum PWM");
                case TuneVerdict.Slow: return Tr("Follows late: higher gain");
                case TuneVerdict.Weaves: return speedFactor ? Tr("Weaves only when fast: bigger speed factor") : Tr("Weaves across the line: longer look-ahead");
                case TuneVerdict.ReturnsSlowly: return speedFactor ? Tr("Stays off the line only when fast: smaller speed factor") : Tr("Stays off the line: shorter look-ahead");
                case TuneVerdict.Recommend: return Tr("New value found");
                case TuneVerdict.ZoneBelowNoise: return Tr("Smaller than the sensor noise: the motor chases noise");
                case TuneVerdict.ZoneAboveNoise: return Tr("Much bigger than the sensor noise: reacts late");
                case TuneVerdict.ZoneFlickers: return Tr("Switches on and off too often: longer delay");
                case TuneVerdict.MotorHunts: return Tr("The motor hunts back and forth: shorter delay");
                case TuneVerdict.RollZeroOff: return Tr("Passes both ways disagree: the zero is off");
                case TuneVerdict.AcquireOvershoots: return Tr("Shoots past the line: bigger acquire factor");
                case TuneVerdict.AcquireSlow: return Tr("Creeps onto the line: smaller acquire factor");
                case TuneVerdict.StaysToOneSide: return Tr("Stays to one side: more integral");
                case TuneVerdict.IntegralWaves: return Tr("Slow wave across the line: less integral");
                case TuneVerdict.TurnOvershoots: return Tr("Turns past the next line: less compensation");
                case TuneVerdict.TurnEndsWide: return Tr("Ends wide of the next line: more compensation");
                case TuneVerdict.FusionDrifts: return Tr("The IMU drifts off the GPS heading: faster fusion");
                case TuneVerdict.FusionNoisy: return Tr("GPS heading noise gets through: slower fusion");
                case TuneVerdict.SensorNotLinear: return Tr("The sensor reads differently at bigger angles: correction");
                default: return Tr("collecting...");
            }
        }
    }
}
