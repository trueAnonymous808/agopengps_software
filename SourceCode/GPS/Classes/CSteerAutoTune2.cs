using System;
using System.Collections.Generic;

namespace AgOpenGPS
{
    /// <summary>
    /// Roll sensor zero from passes driven both ways over the same ground: the slope's roll changes sign with the
    /// direction, an error of the zero does not. Per 10 m cell the mean roll one way and the other way are averaged:
    /// what is left is the zero error. A zero 1° off puts a 3 m high antenna 5 cm to the side - gaps and overlaps
    /// between passes the GPS itself cannot see.
    /// </summary>
    public class CRollZeroTune
    {
        public const double CellM = 10, SampleEveryM = 1;
        public const int MinPerSide = 3, MinPairs = 20, MaxCells = 20000;
        public const double MaxSpreadDeg = 0.5, MinStepDeg = 0.1;

        private class Cell
        {
            public double RefHeading;
            public double SumA, SumB;
            public int CountA, CountB;
        }

        private readonly Dictionary<long, Cell> cells = new Dictionary<long, Cell>();
        private double lastE = double.NaN, lastN, run;
        private readonly object sync = new object();

        public void Reset()
        {
            lock (sync) { cells.Clear(); lastE = double.NaN; run = 0; }
        }

        /// <summary>One fix while autosteer holds a line; heading in radians, roll in degrees (zero already taken off).</summary>
        public void AddSample(double east, double north, double heading, double rollDeg, bool onLine)
        {
            lock (sync)
            {
                if (!onLine) { lastE = double.NaN; return; }
                if (double.IsNaN(lastE)) { lastE = east; lastN = north; run = 0; return; }
                run += Math.Sqrt((east - lastE) * (east - lastE) + (north - lastN) * (north - lastN));
                lastE = east;
                lastN = north;
                if (run < SampleEveryM) return;
                run = 0;

                long key = ((long)Math.Floor(east / CellM) << 32) ^ (uint)(int)Math.Floor(north / CellM);
                Cell c;
                if (!cells.TryGetValue(key, out c))
                {
                    if (cells.Count >= MaxCells) return;
                    c = new Cell { RefHeading = heading };
                    cells[key] = c;
                }
                double diff = Math.Abs(Math.IEEERemainder(heading - c.RefHeading, 2 * Math.PI)) * 180 / Math.PI;
                if (diff < 30) { c.SumA += rollDeg; c.CountA++; }
                else if (diff > 150) { c.SumB += rollDeg; c.CountB++; }
            }
        }

        /// <summary>The zero error (degrees, median over the cells driven both ways), its spread and how many cells.</summary>
        public void Measure(out double errorDeg, out double spreadDeg, out int pairs)
        {
            lock (sync)
            {
                var errors = new List<double>();
                foreach (Cell c in cells.Values)
                {
                    if (c.CountA < MinPerSide || c.CountB < MinPerSide) continue;
                    errors.Add((c.SumA / c.CountA + c.SumB / c.CountB) / 2);
                }
                pairs = errors.Count;
                errorDeg = 0;
                spreadDeg = 0;
                if (pairs == 0) return;
                errors.Sort();
                errorDeg = errors[pairs / 2];
                var dev = new List<double>();
                foreach (double e in errors) dev.Add(Math.Abs(e - errorDeg));
                dev.Sort();
                spreadDeg = dev[pairs / 2];
            }
        }

        /// <summary>The roll zero to use: the zero error moves into the zero (the sensor reading has it taken off).</summary>
        public TuneVerdict Recommend(double rollZero, out double newRollZero)
        {
            newRollZero = rollZero;
            Measure(out double error, out double spread, out int pairs);
            if (pairs < MinPairs || spread > MaxSpreadDeg) return TuneVerdict.Collecting;
            if (Math.Abs(error) < MinStepDeg) return TuneVerdict.Good;
            newRollZero = Math.Round(rollZero + error, 2);
            return TuneVerdict.RollZeroOff;
        }
    }

    /// <summary>
    /// The acquire factor (the look-ahead when far off the line, as a share of the normal one), from coming onto a
    /// line from more than 40 cm off: shooting past it wants a bigger factor, creeping onto it a smaller one.
    /// </summary>
    public class CAcquireTune
    {
        public const double SampleEveryM = 0.5, FarM = 0.4, OnM = 0.05, WatchM = 20;
        public const int MinEvents = 5, KeepEvents = 12;
        public const double OvershootM = 0.08, CleanM = 0.03, SlowM = 25, Step = 0.05;

        private enum Phase { Idle, Far, Coming, Watching }
        private Phase phase = Phase.Idle;
        private int side;
        private double comingFrom, watchFrom, peak, run, lastE = double.NaN, lastN;
        private readonly List<double> distances = new List<double>(), overshoots = new List<double>();
        private readonly object sync = new object();

        public int Events { get { lock (sync) return distances.Count; } }

        public void Reset()
        {
            lock (sync) { distances.Clear(); overshoots.Clear(); phase = Phase.Idle; lastE = double.NaN; }
        }

        public void AddSample(double east, double north, double offLineM, bool onLine)
        {
            lock (sync)
            {
                if (!onLine) { phase = Phase.Idle; lastE = double.NaN; return; }
                if (double.IsNaN(lastE)) { lastE = east; lastN = north; return; }
                double step = Math.Sqrt((east - lastE) * (east - lastE) + (north - lastN) * (north - lastN));
                if (step < SampleEveryM) return;
                run += step;
                lastE = east;
                lastN = north;
                double off = Math.Abs(offLineM);
                int s = Math.Sign(offLineM);

                switch (phase)
                {
                    case Phase.Idle:
                        if (off > FarM) { phase = Phase.Far; side = s; }
                        break;
                    case Phase.Far:
                        if (off <= FarM) { phase = Phase.Coming; comingFrom = run; }
                        break;
                    case Phase.Coming:
                        if (off > FarM) { phase = Phase.Far; side = s; }
                        else if (off < OnM || (s != 0 && s != side))
                        {
                            distances.Add(run - comingFrom);
                            phase = Phase.Watching;
                            watchFrom = run;
                            peak = 0;
                        }
                        break;
                    case Phase.Watching:
                        if (s == -side) peak = Math.Max(peak, off);   // past the line, on the other side
                        if (run - watchFrom >= WatchM)
                        {
                            overshoots.Add(peak);
                            if (distances.Count > KeepEvents) { distances.RemoveAt(0); overshoots.RemoveAt(0); }
                            phase = Phase.Idle;
                        }
                        break;
                }
            }
        }

        public void Measure(out double overshootM, out double distanceM, out int events)
        {
            lock (sync)
            {
                events = overshoots.Count;
                overshootM = Median(overshoots);
                distanceM = Median(distances.GetRange(0, Math.Min(distances.Count, overshoots.Count)));
            }
        }

        public TuneVerdict Recommend(double factor, double min, double max, out double newFactor)
        {
            newFactor = factor;
            Measure(out double overshoot, out double distance, out int events);
            if (events < MinEvents) return TuneVerdict.Collecting;
            if (overshoot > OvershootM && factor < max)
            {
                newFactor = Math.Min(max, Math.Round(factor + Step, 2));
                return TuneVerdict.AcquireOvershoots;
            }
            if (distance > SlowM && overshoot < CleanM && factor > min)
            {
                newFactor = Math.Max(min, Math.Round(factor - Step, 2));
                return TuneVerdict.AcquireSlow;
            }
            return TuneVerdict.Good;
        }

        internal static double Median(List<double> values)
        {
            if (values.Count == 0) return 0;
            var sorted = new List<double>(values);
            sorted.Sort();
            return sorted[sorted.Count / 2];
        }
    }

    /// <summary>
    /// The pure pursuit integral gain from long straight runs: staying off to one side wants more, a slow wave across
    /// the line (the integral winding up) wants less.
    /// </summary>
    public class CIntegralTune
    {
        public const double SampleEveryM = 0.5, BandM = 0.01;
        public const int MinSamples = 300, MaxSamples = 400;     // 150 m to judge, the last 200 m kept
        public const double BiasM = 0.02, WaveRmsM = 0.03, WaveMinPer100m = 1, WaveMaxPer100m = 6;

        private readonly List<double> offsets = new List<double>();
        private double lastE = double.NaN, lastN, run;
        private readonly object sync = new object();

        public int Count { get { lock (sync) return offsets.Count; } }

        public void Reset() { lock (sync) { offsets.Clear(); lastE = double.NaN; run = 0; } }

        public void AddSample(double east, double north, double offLineM, bool onLine)
        {
            lock (sync)
            {
                if (!onLine) { lastE = double.NaN; return; }
                if (double.IsNaN(lastE)) { lastE = east; lastN = north; run = 0; return; }
                run += Math.Sqrt((east - lastE) * (east - lastE) + (north - lastN) * (north - lastN));
                lastE = east;
                lastN = north;
                if (run < SampleEveryM) return;
                run = 0;
                offsets.Add(offLineM);
                if (offsets.Count > MaxSamples) offsets.RemoveAt(0);
            }
        }

        public void Measure(out double meanM, out double rmsM, out double crossingsPer100m)
        {
            lock (sync)
            {
                meanM = rmsM = crossingsPer100m = 0;
                if (offsets.Count == 0) return;
                double sum = 0, sq = 0;
                int side = 0, crossings = 0;
                foreach (double x in offsets)
                {
                    sum += x;
                    sq += x * x;
                    int s = x > BandM ? 1 : x < -BandM ? -1 : 0;
                    if (s != 0)
                    {
                        if (side != 0 && s != side) crossings++;
                        side = s;
                    }
                }
                meanM = sum / offsets.Count;
                rmsM = Math.Sqrt(sq / offsets.Count);
                crossingsPer100m = crossings / (offsets.Count * SampleEveryM / 100);
            }
        }

        public TuneVerdict Recommend(double gain, double min, double max, out double newGain)
        {
            newGain = gain;
            if (Count < MinSamples) return TuneVerdict.Collecting;
            Measure(out double mean, out double rms, out double crossings);
            if (Math.Abs(mean) > BiasM && gain < max)
            {
                newGain = Math.Min(max, gain < 0.05 ? 0.1 : Math.Round(gain * 1.25, 2));
                return TuneVerdict.StaysToOneSide;
            }
            if (gain > min && rms > WaveRmsM && crossings >= WaveMinPer100m && crossings <= WaveMaxPer100m)
            {
                newGain = Math.Max(min, Math.Round(gain * 0.8, 2));
                return TuneVerdict.IntegralWaves;
            }
            return TuneVerdict.Good;
        }
    }

    /// <summary>
    /// U-turn compensation from how the tractor comes onto the next line after a U-turn: shooting across it wants
    /// less compensation, ending wide and creeping onto it wants more.
    /// </summary>
    public class CUTurnTune
    {
        public const double SampleEveryM = 0.5, WatchM = 20, CrossM = 0.03, OvershootM = 0.08, WideM = 0.15, Step = 0.1;
        public const int MinTurns = 4, KeepTurns = 10;

        public enum Outcome { Fine, Over, Wide }

        private bool turning, watching, crossed;
        private int startSide;
        private double firstOff, peak, run, lastE = double.NaN, lastN;
        private readonly List<Outcome> outcomes = new List<Outcome>();
        private readonly object sync = new object();

        public int Turns { get { lock (sync) return outcomes.Count; } }

        public void Reset() { lock (sync) { outcomes.Clear(); turning = watching = false; lastE = double.NaN; } }

        /// <summary>One fix: inTurn while the U-turn is driven, onLine while autosteer holds the line after it.</summary>
        public void AddSample(double east, double north, double offLineM, bool inTurn, bool onLine)
        {
            lock (sync)
            {
                if (inTurn) { turning = true; watching = false; lastE = double.NaN; return; }
                if (turning && onLine)
                {
                    // the turn has just ended: watch the next 20 m
                    turning = false;
                    watching = true;
                    crossed = false;
                    firstOff = offLineM;
                    startSide = Math.Sign(offLineM);
                    peak = 0;
                    run = 0;
                    lastE = east;
                    lastN = north;
                    return;
                }
                if (!watching) return;
                if (!onLine) { watching = false; return; }
                run += Math.Sqrt((east - lastE) * (east - lastE) + (north - lastN) * (north - lastN));
                lastE = east;
                lastN = north;
                if (startSide != 0 && Math.Sign(offLineM) == -startSide && Math.Abs(offLineM) > CrossM) crossed = true;
                if (crossed) peak = Math.Max(peak, Math.Abs(offLineM));
                if (run < WatchM) return;

                watching = false;
                Outcome o = crossed && peak > OvershootM ? Outcome.Over : !crossed && Math.Abs(firstOff) > WideM ? Outcome.Wide : Outcome.Fine;
                outcomes.Add(o);
                if (outcomes.Count > KeepTurns) outcomes.RemoveAt(0);
            }
        }

        public void Measure(out int over, out int wide, out int turns)
        {
            lock (sync)
            {
                over = wide = 0;
                foreach (Outcome o in outcomes)
                {
                    if (o == Outcome.Over) over++;
                    else if (o == Outcome.Wide) wide++;
                }
                turns = outcomes.Count;
            }
        }

        public TuneVerdict Recommend(double comp, double min, double max, out double newComp)
        {
            newComp = comp;
            Measure(out int over, out int wide, out int turns);
            if (turns < MinTurns) return TuneVerdict.Collecting;
            if (over * 10 >= turns * 6 && comp > min)
            {
                newComp = Math.Max(min, Math.Round(comp - Step, 1));
                return TuneVerdict.TurnOvershoots;
            }
            if (wide * 10 >= turns * 6 && comp < max)
            {
                newComp = Math.Min(max, Math.Round(comp + Step, 1));
                return TuneVerdict.TurnEndsWide;
            }
            return TuneVerdict.Good;
        }
    }
}
