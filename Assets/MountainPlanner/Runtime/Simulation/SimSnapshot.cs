using System;
using System.Threading;

namespace MountainPlanner.Simulation
{
    /// <summary>Where the season is (TM-11 v2, TM-14). Values are saved: never renumber.</summary>
    public enum SeasonPhase : byte
    {
        /// <summary>October 1 until Day 1: build time, the clock at the night rate.</summary>
        PreSeason = 1,
        /// <summary>From Day 1 until the mountain closes.</summary>
        Season = 2,
        /// <summary>After the season, until the next October 1.</summary>
        Summer = 3,
    }

    /// <summary>A skip the host is running (TM-06 v2, TM-14). Values are logged: never renumber.</summary>
    public enum SkipKind : byte { None = 0, Morning = 1, NextThird = 2, NextMonth = 3 }

    /// <summary>A lift as a snapshot shows it.</summary>
    public enum LiftRunState : byte
    {
        /// <summary>Not built yet: a plan.</summary>
        Planned = 0,
        /// <summary>Built, rope stopped: outside the hours, the mountain closed, a hold.</summary>
        Parked = 1,
        /// <summary>The rope runs.</summary>
        Running = 2,
    }

    /// <summary>
    /// TM-15: the day report at closing. Guests, snow and guns don't exist yet, so most numbers stay zero until their tasks
    /// fill them; the lifts' run and closed minutes are real. Allocated once per snapshot and overwritten in place.
    /// </summary>
    public sealed class DayReport
    {
        public int FaceDay = -1;
        public int SeasonDay;
        public SeasonPhase Phase;
        public DayType DayType;
        public int OpenSecond, StopSecond;
        /// <summary>Q10: the resort's hours were cut to the day's daylight.</summary>
        public bool Trimmed;
        public int Target, Arrived, TurnedAway, WentHome, Stranded;
        public int LiftCount;
        public readonly int[] RunMinutes;
        public readonly int[] ClosedMinutes;
        public readonly int[] Rides;
        /// <summary>LO-12: the day's peak line, mean wait (motion ms), minutes on hold, skiers left in line at the stop, and
        /// utilisation (rides ÷ what the lift could carry in its day, thousandths).</summary>
        public readonly int[] PeakLine, MeanWaitMs, HoldMinutes, LeftInLine, UtilisationPermille;

        public DayReport(int maxLifts)
        {
            RunMinutes = new int[maxLifts];
            ClosedMinutes = new int[maxLifts];
            Rides = new int[maxLifts];
            PeakLine = new int[maxLifts];
            MeanWaitMs = new int[maxLifts];
            HoldMinutes = new int[maxLifts];
            LeftInLine = new int[maxLifts];
            UtilisationPermille = new int[maxLifts];
        }

        public bool IsValid => FaceDay >= 0;

        public void CopyFrom(DayReport o)
        {
            FaceDay = o.FaceDay; SeasonDay = o.SeasonDay; Phase = o.Phase; DayType = o.DayType;
            OpenSecond = o.OpenSecond; StopSecond = o.StopSecond; Trimmed = o.Trimmed;
            Target = o.Target; Arrived = o.Arrived; TurnedAway = o.TurnedAway; WentHome = o.WentHome; Stranded = o.Stranded;
            LiftCount = o.LiftCount;
            Array.Copy(o.RunMinutes, RunMinutes, LiftCount);
            Array.Copy(o.ClosedMinutes, ClosedMinutes, LiftCount);
            Array.Copy(o.Rides, Rides, LiftCount);
            Array.Copy(o.PeakLine, PeakLine, LiftCount);
            Array.Copy(o.MeanWaitMs, MeanWaitMs, LiftCount);
            Array.Copy(o.HoldMinutes, HoldMinutes, LiftCount);
            Array.Copy(o.LeftInLine, LeftInLine, LiftCount);
            Array.Copy(o.UtilisationPermille, UtilisationPermille, LiftCount);
        }

        internal const int SizeBytesPerLift = 32;
    }

    /// <summary>
    /// SI-16: one immutable record of the simulation, stamped with the tick it describes and a sequence number. Three are
    /// allocated up front and reused in turn (<see cref="SnapshotBuffers"/>); nothing changes one after it's published, and
    /// readers hold the newest for a frame. Within 64 KB (<see cref="SizeBytes"/>).
    /// </summary>
    public sealed class SimSnapshot
    {
        public long Seq;
        public long Tick;
        /// <summary>Face milliseconds since 00:00 on October 1 of the start's label year.</summary>
        public long FaceMs;
        public int FaceDay;
        public int LabelYear;
        public int FaceDayOfYear;
        public int SecondOfDay;
        /// <summary>The sun's date (WX-04): the label date's real calendar year and day of year.</summary>
        public int ViewYear, ViewDayOfYear;
        public int Month, DayOfMonth;
        public MonthThird Third;
        public DayType DayType;

        public SeasonPhase Phase;
        public bool Open;
        public int SeasonNumber;
        /// <summary>Day N of the season (TM-08), 0 before Day 1.</summary>
        public int SeasonDay;
        public ulong SeasonSeed;
        public bool SeedPinned;
        /// <summary>Today's hours, trimmed to daylight.</summary>
        public int OpenSecond, LastChairSecond, StopSecond;
        public bool HoursTrimmed;
        /// <summary>True while the clock runs at the day rate (TM-02 v2).</summary>
        public bool DayRate;

        // The host's
        /// <summary>The speed, 1–4 (1×, 2×, 4×, 8×), and the ticks per real tick it asks for.</summary>
        public int Speed, TicksPerRealTick;
        public bool Paused;
        /// <summary>The host is held (the weather's inputs are being built): nothing steps until it's released.</summary>
        public bool Held;
        /// <summary>SI-11: the rate the machine sustains, in thousandths of the one asked for.</summary>
        public int SustainedPermille;
        public SkipKind Skip;
        /// <summary>The skip's progress in thousandths.</summary>
        public int SkipPermille;
        /// <summary>BG-01, measured by the host on the clock it's given (App's), never inside the step: µs per tick.</summary>
        public int TickMeanMicros, TickP99Micros;
        public long TicksRun;

        public long DesignRevision;

        // Per lift, in ID order
        public int LiftCount;
        public readonly ulong[] LiftIds;
        /// <summary>RP-07 v2: how long each lift's rope has run, motion milliseconds. A chair's place follows it.</summary>
        public readonly long[] RunMs;
        public readonly LiftRunState[] LiftStates;
        /// <summary>The board's public lift column (SI-19): open today.</summary>
        public readonly byte[] LiftOpen;
        /// <summary>LO-02: skiers in each lift's line.</summary>
        public readonly int[] LineCount;
        /// <summary>LO-04 v1: the wait for a skier joining now, motion ms at 1× (divide by the speed for real time).</summary>
        public readonly int[] WaitMs;
        /// <summary>The Status word's source (Planned, Closed, Scheduled, Open, On hold, Downloading).</summary>
        public readonly LiftOpStatus[] LiftStatus;
        /// <summary>LO-05: riders on the carriers, and today's rides so far (LO-12).</summary>
        public readonly int[] Aboard, RidesToday;
        /// <summary>LO-01: the motion ms between carriers, and the seats a carrier takes (LO-03: rated = seats × 3,600,000 ÷ interval).</summary>
        public readonly int[] IntervalMs;
        public readonly byte[] Seats;
        /// <summary>LO-07 v2: each lift's opening today, seconds of the day (a Scheduled lift says when).</summary>
        public readonly int[] LiftOpenSecond;
        /// <summary>RD-2 (task 4.24 PR B): skiers in each lift's top line, and its wait (motion ms at 1×, as <see cref="WaitMs"/>).</summary>
        public readonly int[] TopLineCount, TopWaitMs;
        /// <summary>RD-2: today's rides down so far (not in <see cref="RidesToday"/>, which LO-12's utilisation reads).</summary>
        public readonly int[] RidesDownToday;
        /// <summary>RD-1: whether each lift carries riders down now (a gondola, or a chair that is downloading).</summary>
        public readonly bool[] CarriesDown;
        public int TrailsReady;

        // Per network trail, in ID order. TrailStatus: Open when an open lift's descent of open trails (SN-20 v4, LO-10) runs along it
        public int TrailCount;
        /// <summary>False until the app's network has reached the simulation: the trails' words stay placeholders.</summary>
        public bool TrailsKnown;
        public readonly ulong[] TrailIds;
        public readonly LiftOpStatus[] TrailStatus;
        /// <summary>
        /// Task 4.07 B1 part 2b, per network trail, packed to fit SI-16's 64 KB: bits 0–11 its walk in feet (a late-season
        /// Marginal trail's, at most 4,095), bits 12–13 its <see cref="TrailSnowLabel"/> (0 without trail snow), bits 14–15
        /// its snow rating's word (0 Poor, 1 Fair, 2 Good, 3 Excellent: SN-16's bands over the bands' mean). Read through
        /// <see cref="TrailLabelAt"/>, <see cref="TrailWalkFtAt"/> and <see cref="TrailRatingAt"/>.
        /// </summary>
        public readonly ushort[] TrailBoard;
        /// <summary>Per network trail: its covered fine cells, % (SN-20 v3).</summary>
        public readonly byte[] TrailCoverPct;

        public TrailSnowLabel TrailLabelAt(int k) => (TrailSnowLabel)((TrailBoard[k] >> 12) & 3);
        public int TrailWalkFtAt(int k) => TrailBoard[k] & 0xFFF;
        /// <summary>0 Poor, 1 Fair, 2 Good, 3 Excellent.</summary>
        public int TrailRatingAt(int k) => TrailBoard[k] >> 14;

        /// <summary>
        /// Task 4.07 B2, per network trail, for the trail card: bits 0–3 its main snow type (<see cref="SnowType"/>, 0
        /// before the snow is live), bit 4 moguls, bit 5 some ice, bit 6 <see cref="TrailSnowOpen"/> (its snow has it open on
        /// Auto; the switch is the design's, so the card works out SN-20 v4's open itself). Copied only when
        /// <see cref="TrailSnowStamp"/> moves (an hour's board, a design, a reopened save), as <see cref="WeatherFields"/>
        /// is, so it sits beside SI-16's per-tick budget, not in it.
        /// </summary>
        public readonly byte[] TrailSnow;
        public long TrailSnowStamp;
        public const int TrailSnowOpen = 64;
        public SnowType TrailMainTypeAt(int k) => (SnowType)(TrailSnow[k] & 15);
        public bool TrailMogulsAt(int k) => (TrailSnow[k] & 16) != 0;
        public bool TrailIceAt(int k) => (TrailSnow[k] & 32) != 0;

        public readonly DayReport LastDayReport;
        public int HistoryCount;

        // SI-18
        public long FirstEventSeq;
        public int EventCount;
        public readonly SimEvent[] Events;

        // SI-17: the staging ring's slots written so far (read them up to here), and whether the field has snow yet.
        public long StagedTiles;
        public bool SnowSet;
        /// <summary>Task 4.07 PR A2: the snow is simulated; an hour of it is in flight; the day's ledger so far and the last day's.</summary>
        public bool SnowLive, SnowInFlight;
        public SnowLedger SnowToday, SnowLastDay;
        /// <summary>Task 4.07 B1 part 2a, for the F1 readout: the fine size (0: no trail snow), its cells, the trails' mean depth
        /// (mm) and packed share (‰) at the last hour roll, the trails groomed this face day, and the water the trails took off
        /// the 8 m snow in all (µm, the ledger's own term; saved since 2c).</summary>
        public int TrailFineMetres, TrailFineCells, TrailDepthMm, TrailPackedPermille, TrailsGroomedToday;
        /// <summary>The trails' 8 m cells (blocks), and how many of them the Snow conditions layer shows as machine groomed.</summary>
        public int TrailBlocks, TrailBlocksGroomed;
        /// <summary>Task 4.07 B1 part 2a: the 8 m squares and trails the last nightly pass worked (the readout's check).</summary>
        public int TrailBlocksLastGroom, TrailsLastGroom;
        /// <summary>Task 4.07 B1 part 2a: trail squares whose 8 m type is powder (P or WP), for the readout.</summary>
        public int TrailBlocksPowder;
        public long TrailRemovedTotal;
        /// <summary>Task 4.07 B1 part 2b: trails Covered, Marginal and Bare at the last flip, and the late season.</summary>
        public int TrailsCovered, TrailsMarginal, TrailsBare;
        public bool LateSeason;
        /// <summary>Task 4.07 B1 part 2c: a reopened save's trail snow waits for the layout (the game holds the clock), and the
        /// save's blocks taken back exactly or seeded again when it came (the log).</summary>
        public bool TrailSnowWaiting;
        public int TrailBlocksRestored, TrailBlocksReseeded;

        /// <summary>GU-03, GU-13, GU-18: the crowd as the Guests tab shows it, and the selected skier's day (GU-02).</summary>
        public readonly GuestView Guests;

        // Task 4.05: the weather. False until the first face hour with weather.
        public bool WeatherKnown;
        /// <summary>The typical mountain's rows (the area has no weather history yet), and new rows waiting for October 1.</summary>
        public bool WeatherGeneric, WeatherWaiting;
        /// <summary>WX-30: the last face hour at zref.</summary>
        public Domain.Weather.WeatherHour WeatherHour;
        /// <summary>WX-35: the reference points' samples.</summary>
        public WeatherSample WeatherBase, WeatherMid, WeatherSummit;
        /// <summary>LO-11, WX-39: each lift's gust at its top station this hour, 0.1 mph (the weather's, not SetGust's override).</summary>
        public readonly int[] LiftGustTenths;
        /// <summary>
        /// WX-02 v2 (PR B2): the last face hour's tables over the map, read with <see cref="WeatherFields.At(int, out WeatherCell)"/>.
        /// Copied only when the hour moves, so it sits beside SI-16's per-tick budget, not in it.
        /// </summary>
        public readonly WeatherFields WeatherFields = new WeatherFields();
        /// <summary>WX-41 (PR C): the lasting weather events under way (bits of <see cref="WeatherEventKind"/>), and the snow at mid the last landing read (0.1 in).</summary>
        public int WeatherEvents, OvernightSnowMid;
        /// <summary>WX-44 (PR C): today's two weather lines of the crowd's demand (‰, 1000 = none), as the landing set them.</summary>
        public int PowderPull = 1000, ForecastPull = 1000;
        /// <summary>WX-05 v3 (PR C): the evening sheet (or the landing's refresh), copied only when the world makes it again.</summary>
        public readonly ForecastSheet Forecast;
        public int ForecastVersion = -1;

        // Task 4.09: every water body's surface (LK-08 v2), in the body table's order (body 0 is the streams body). Zero
        // bodies until the world has a table.
        public int WaterBodyCount;
        public readonly ulong[] WaterBodyIds;
        public readonly Domain.Water.WaterSurfaceState[] WaterStates;
        /// <summary>Ice (0.01 in) and snow on it (0.1 in).</summary>
        public readonly int[] IceHundredthsIn, IceSnowTenthsIn;
        /// <summary>The label year's ice-on and ice-out face days, −1 for not yet.</summary>
        public readonly short[] IceOnDay, IceOutDay;

        // Task 4.14 PR B: every snowmaking source's water (WS-02…WS-07), in id order. Zero until the world has sources.
        public int SourceCount;
        public readonly ulong[] SourceIds;
        /// <summary>Gallons held, at capacity (full pool) and kept back (MinPool or a lake's drawdown limit).</summary>
        public readonly long[] SourceHeldGallons, SourceCapacityGallons, SourceFloorGallons;
        /// <summary>How far under full pool the water stands, micrometres.</summary>
        public readonly int[] SourceBelowFullMicrons;
        /// <summary>The last hour in and out (gallons a minute of calendar time), and the test draw.</summary>
        public readonly int[] SourceInGpm, SourceOutGpm, SourceTestGpm;
        /// <summary><see cref="SimWorld.SourceAtFloor"/> and the rest.</summary>
        public readonly byte[] SourceFlags;

        // Task 4.15 (HY-08): the snowmaking network's last answers, by reference (made once a solve, never copied), or null.
        public Domain.Snowmaking.SnowSolution Snowmaking, SnowCheck;
        /// <summary>A solve is asked for and lands within <see cref="Pacing.SolveLatencyTicks"/> ticks (the screen greys the answer).</summary>
        public bool SnowSolvePending;

        public SimSnapshot(Pacing p)
        {
            int sources = p.MaxSources;
            SourceIds = new ulong[sources];
            SourceHeldGallons = new long[sources]; SourceCapacityGallons = new long[sources]; SourceFloorGallons = new long[sources];
            SourceBelowFullMicrons = new int[sources];
            SourceInGpm = new int[sources]; SourceOutGpm = new int[sources]; SourceTestGpm = new int[sources];
            SourceFlags = new byte[sources];
            int bodies = 1 + p.MaxLakes + p.MaxPonds;
            WaterBodyIds = new ulong[bodies];
            WaterStates = new Domain.Water.WaterSurfaceState[bodies];
            IceHundredthsIn = new int[bodies];
            IceSnowTenthsIn = new int[bodies];
            IceOnDay = new short[bodies];
            IceOutDay = new short[bodies];
            Guests = new GuestView(p);
            LiftIds = new ulong[p.MaxLifts];
            RunMs = new long[p.MaxLifts];
            LiftStates = new LiftRunState[p.MaxLifts];
            LiftOpen = new byte[p.MaxLifts];
            LineCount = new int[p.MaxLifts];
            WaitMs = new int[p.MaxLifts];
            LiftStatus = new LiftOpStatus[p.MaxLifts];
            Aboard = new int[p.MaxLifts];
            RidesToday = new int[p.MaxLifts];
            IntervalMs = new int[p.MaxLifts];
            Seats = new byte[p.MaxLifts];
            LiftOpenSecond = new int[p.MaxLifts];
            TopLineCount = new int[p.MaxLifts];
            TopWaitMs = new int[p.MaxLifts];
            RidesDownToday = new int[p.MaxLifts];
            CarriesDown = new bool[p.MaxLifts];
            LiftGustTenths = new int[p.MaxLifts];
            Forecast = new ForecastSheet(p.MaxLifts);
            TrailIds = new ulong[p.MaxTrails];
            TrailStatus = new LiftOpStatus[p.MaxTrails];
            TrailBoard = new ushort[p.MaxTrails]; TrailCoverPct = new byte[p.MaxTrails];
            TrailSnow = new byte[p.MaxTrails];
            LastDayReport = new DayReport(p.MaxLifts);
            Events = new SimEvent[p.SnapshotEvents];
        }

        /// <summary>The lift's index here, or −1.</summary>
        public int IndexOfLift(ulong id)
        {
            int lo = 0, hi = LiftCount - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                if (LiftIds[mid] == id) return mid;
                if (LiftIds[mid] < id) lo = mid + 1; else hi = mid - 1;
            }
            return -1;
        }

        /// <summary>SI-16's budget: the bytes this snapshot's arrays and fields take at their full size.</summary>
        public int SizeBytes => 256 + 256 + LiftIds.Length * (8 + 8 + 1 + 1 + 4 + 4 + 1 + 4 + 4 + 4 + 1 + 4 + 1 + 4 + 4 + 4 + 4 + 1 + DayReport.SizeBytesPerLift)
                                + TrailIds.Length * (8 + 1 + 1 + 2 + 1) + Events.Length * 24
                                + Guests.SizeBytes + Forecast.SizeBytes + WaterBodyIds.Length * (8 + 4 + 4 + 4 + 2 + 2)
                                + SourceIds.Length * (8 + 8 + 8 + 8 + 4 + 4 + 4 + 4 + 1);

        /// <summary>The trail's index here, or −1.</summary>
        public int IndexOfTrail(ulong id)
        {
            int lo = 0, hi = TrailCount - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                if (TrailIds[mid] == id) return mid;
                if (TrailIds[mid] < id) lo = mid + 1; else hi = mid - 1;
            }
            return -1;
        }
    }

    /// <summary>
    /// SI-16's triple buffer: the worker writes one snapshot while the newest waits and the reader holds a third, so neither
    /// side ever waits or sees a snapshot change. Latest wins: a reader that's slow skips snapshots, never events
    /// (<see cref="EventReader"/>).
    /// </summary>
    public sealed class SnapshotBuffers
    {
        readonly SimSnapshot[] _buffers;
        int _back = 0, _front = 2;
        int _middle = 1;           // the newest published; the top bit says it's new to the reader
        const int Fresh = 1 << 30;

        public SnapshotBuffers(Pacing p)
        {
            _buffers = new[] { new SimSnapshot(p), new SimSnapshot(p), new SimSnapshot(p) };
        }

        /// <summary>The worker's buffer to fill.</summary>
        public SimSnapshot Back => _buffers[_back];

        /// <summary>The worker publishes what it filled; its next buffer is the one the reader isn't holding.</summary>
        public void Publish()
        {
            int previous = Interlocked.Exchange(ref _middle, _back | Fresh);
            _back = previous & ~Fresh;
        }

        /// <summary>The reader's newest snapshot: a fresh one if the worker published since, else the one it holds (null before the first).</summary>
        public SimSnapshot Latest
        {
            get
            {
                if ((Volatile.Read(ref _middle) & Fresh) != 0)
                {
                    int previous = Interlocked.Exchange(ref _middle, _front);
                    _front = previous & ~Fresh;
                    _any = true;
                }
                return _any ? _buffers[_front] : null;
            }
        }

        bool _any;
    }
}
