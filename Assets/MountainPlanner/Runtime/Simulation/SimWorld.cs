using System;
using System.Collections.Generic;
using MountainPlanner.Domain.Design;
using MountainPlanner.Domain.Sky;

namespace MountainPlanner.Simulation
{
    /// <summary>
    /// The simulation's whole state and its step (SI-02, SI-05): one tick at a time, or an idle step of up to
    /// <see cref="Pacing.IdleTickFactor"/> ticks when no lift runs and no skier exists (SI-11 v2), never across a boundary, so
    /// every speed and every skip gives the same state (TM-03, SI-08). Integers only (SI-07). Nothing outside the worker
    /// reads or writes it: <see cref="SimulationHost"/> owns it on its thread, and the headless runner drives it directly.
    ///
    /// The clocks (TM-01, TM-02 v2): the motion clock is <see cref="Tick"/>, 100 ms of real time at 1×. The face clock is
    /// <see cref="FaceMs"/>, face milliseconds since 00:00 on October 1 of the start's label year; from the resort's opening
    /// to its stop on a season day a tick is 4 face seconds, every other hour 24 (TM-14: days nobody skis run whole at the
    /// night rate). A step stops exactly on each boundary: every whole hour, an hour before opening (where Skip to morning
    /// lands), the opening and the stop. The day's work on a boundary happens at the start of the tick that begins there.
    /// </summary>
    public sealed partial class SimWorld
    {
        public const long DayMs = 86_400_000;
        public const long HourMs = 3_600_000;

        public readonly Pacing Pacing;
        public readonly LabelCalendar Calendar;
        public readonly SimSite Site;
        /// <summary>The label year of the first October 1.</summary>
        public readonly int StartYear;

        // ---- the clocks ----
        public long Tick { get; private set; }
        public long FaceMs { get; private set; }

        // ---- the season (TM-11 v2, SI-15) ----
        public SeasonPhase Phase { get; private set; } = SeasonPhase.PreSeason;
        public bool Open { get; private set; }
        public int SeasonNumber { get; private set; } = 1;
        public int SeasonDay { get; private set; }
        public ulong SeasonSeed { get; private set; }
        public bool SeedPinned { get; private set; }

        // ---- the design (SI-20) ----
        public SimDesign Design { get; private set; }

        // ---- today ----
        int _todayOpen, _todayLastChair, _todayStop;
        bool _todayTrimmed;
        long _boundaryDoneAt = -1;

        // ---- the face-hour step: the hour's weather at zref (task 4.05, SimWorld.Weather) ----
        long _weatherHour = -1;
        int _weatherTenthsF, _weatherWindMph;
        int _hourSliceCursor;

        // ---- the board (SI-19): written by the hour step, flipped once when it finishes ----
        byte[] _liftOpen = Array.Empty<byte>(), _liftOpenNext = Array.Empty<byte>();
        byte[] _liftHold = Array.Empty<byte>();
        int _trailsReady, _trailsReadyNext;

        // ---- lifts, in the design's ID order ----
        int _liftCount;
        ulong[] _liftIds = Array.Empty<ulong>();
        bool[] _liftBuilt = Array.Empty<bool>();
        long[] _runMs = Array.Empty<long>();
        bool[] _running = Array.Empty<bool>();

        // ---- ledgers (SI-10's operations, LO-12, TM-15) ----
        long[] _runFaceMs = Array.Empty<long>(), _closedFaceMs = Array.Empty<long>();
        int _ledgerDay, _ledgerSeasonDay;
        DayType _ledgerType;
        SeasonPhase _ledgerPhase;
        readonly DayReport _lastReport;
        readonly int[] _historyFaceDay, _historySeasonDay, _historyRunMinutes, _historyLiftsRun;
        readonly byte[] _historyType, _historyPhase;
        int _historyCount;
        bool _newSeasonHistory;

        // ---- the step's interval (TickInterval) ----
        int _stepTicks, _stepMaxTicks;
        long _stepAdvance;
        bool _stepDayRate;

        // ---- commands (SI-03 v2) and events (SI-18) ----
        readonly SimCommand[] _staged;
        int _stagedCount;
        long _commandSeq;
        /// <summary>GU-02: view commands are counted apart from the state's, so watching a skier leaves the hash alone.</summary>
        long _viewSeq;
        public readonly EventRing Events;
        /// <summary>SI-22's log: every world command with its stamp, in order.</summary>
        public readonly List<SimCommand> Log = new List<SimCommand>();

        // ---- what the runner and the host read ----
        /// <summary>The face hour (since the epoch) the last hour step ran for, and the state's hash just after it (SI-08).</summary>
        public long HourIndex { get; private set; } = -1;
        public ulong HourHash { get; private set; }

        SimSnapshot _publishTo;

        /// <summary>SI-17: the snow depths, the worker's own (mm).</summary>
        public readonly SimSnow Snow = new SimSnow();

        /// <param name="weather">The area's weather (task 4.05), or null for the stand-in hour (tests, the title).</param>
        public SimWorld(Pacing pacing, SimDesign design, SimSite site, int startYear, ulong? pinnedSeed = null, SimWeather weather = null)
            : this(pacing, design, site, startYear, restoring: false)
        {
            AdoptWeather(weather);
            FoldDesign(design);
            SeasonSeed = pinnedSeed ?? DrawSeasonSeed();
            SeedPinned = pinnedSeed.HasValue;
            // Owner, 2026-11-08 (plan question 2): a new game starts on October 1 at the resort's opening.
            BeginFaceDay(0, false);
            FaceMs = _todayOpen * 1000L;
            ResetLedger();
            RefreshBoard();
        }

        /// <summary>The fixed parts only; a new game folds the design and starts its day, a sidecar reads the rest (SI-12 v2).</summary>
        internal SimWorld(Pacing pacing, SimDesign design, SimSite site, int startYear, bool restoring)
        {
            Pacing = pacing ?? throw new ArgumentNullException(nameof(pacing));
            Design = design ?? throw new ArgumentNullException(nameof(design));
            if (startYear < 2 || startYear > 9000) throw new ArgumentOutOfRangeException(nameof(startYear));
            Calendar = new LabelCalendar(pacing);
            _tuning = SimWeather.Tuning(pacing);
            Site = site;
            StartYear = startYear;
            Events = new EventRing(pacing.EventRingSize);
            _staged = new SimCommand[256];
            _lastReport = new DayReport(pacing.MaxLifts);
            int h = pacing.HistoryDays;
            _historyFaceDay = new int[h]; _historySeasonDay = new int[h]; _historyRunMinutes = new int[h]; _historyLiftsRun = new int[h];
            _historyType = new byte[h]; _historyPhase = new byte[h];
            // The skier columns exist before anything is folded or read: a lift's line is a chain over their slots, so
            // the sidecar's lift block needs them already there (SI-12).
            _guests = new GuestColumns(pacing);
            SkierRender = new SkierRenderBuffers(pacing.SkierCap);
            for (int a = 0; a < Pacing.Archetypes; a++) _rep[a] = pacing.StartRep;
            if (restoring) Design = design;
        }

        // ================= the step =================

        /// <summary>
        /// Runs one step of up to <paramref name="maxTicks"/> ticks (more than one only while idle and never across a boundary);
        /// returns the ticks it covered. <paramref name="publishTo"/>, when given, receives the snapshot (its host fields are the
        /// host's to fill).
        /// </summary>
        public int Step(int maxTicks, SimSnapshot publishTo = null)
        {
            if (maxTicks < 1) throw new ArgumentOutOfRangeException(nameof(maxTicks));
            _stepMaxTicks = maxTicks;
            _publishTo = publishTo;
            var rows = SystemsTable.Rows;
            for (int i = 0; i < rows.Length; i++) rows[i].Run(this);
            Tick += _stepTicks;
            FaceMs += _stepAdvance;
            _publishTo = null;
            return _stepTicks;
        }

        /// <summary>Stages a world command for the next step (the host's queue hands them over at the tick boundary).</summary>
        public bool Stage(in SimCommand c)
        {
            if (_stagedCount == _staged.Length) { Reject(c, SimRefusal.QueueFull); return false; }
            _staged[_stagedCount++] = c;
            return true;
        }

        /// <summary>Applies a world command now, without time moving (SI-03 v2: a command while paused), stamped with the next tick.</summary>
        public void ApplyNow(in SimCommand c) => Apply(c);

        /// <summary>Whether a step starting now could cover more than one tick (SI-11 v2): no lift running, no skier.</summary>
        public bool Idle
        {
            get
            {
                for (int i = 0; i < _liftCount; i++) if (_running[i]) return false;
                return !AnyFeeding() && !AnySkier && !ArrivalsDueToday;
            }
        }

        // ================= the rows =================

        internal void RunCommands()
        {
            for (int i = 0; i < _stagedCount; i++)
            {
                Apply(_staged[i]);
                _staged[i] = default;
            }
            _stagedCount = 0;
        }

        internal void RunHourStep()
        {
            if (_boundaryDoneAt == FaceMs) return;
            long sod = FaceMs % DayMs;
            if (!IsBoundary(sod)) return;
            _boundaryDoneAt = FaceMs;
            int faceDay = (int)(FaceMs / DayMs);
            if (sod == 0) BeginFaceDay(faceDay, true);
            if (sod == LandingSecond(_todayOpen) * 1000L) PlanTheDay(faceDay);
            if (sod == _todayOpen * 1000L) AtOpening();
            if (sod % HourMs == 0) HourStep(FaceMs / HourMs);
        }

        internal void RunTickInterval()
        {
            long sod = FaceMs % DayMs;
            _stepDayRate = DayRateAt(sod);
            int rate = _stepDayRate ? Pacing.DayFaceMsPerTick : Pacing.NightFaceMsPerTick;
            bool running = _stepDayRate && AnyBuiltLift() || AnyFeeding() || AnySkier || ArrivalsDueToday;
            // A running world steps one tick at a time, so only an idle step stops at the lifts' wind checks (TM-03).
            long remaining = NextBoundary(sod, windChecks: !running) - sod;
            int ticksToBoundary = (int)((remaining + rate - 1) / rate);
            int k = running ? 1 : Math.Min(Math.Min(_stepMaxTicks, Pacing.IdleTickFactor), ticksToBoundary);
            if (k < 1) k = 1;
            _stepTicks = k;
            _stepAdvance = Math.Min((long)k * rate, remaining);
        }

        internal void RunLedgers()
        {
            long sod = FaceMs % DayMs;
            if (_boundaryDoneAt == FaceMs && sod == _todayStop * 1000L) WriteDayReport();
            if (_boundaryDoneAt == FaceMs && sod == 0 && Tick > 0) { WriteHistoryLine(); ResetLedger(); }
            if (_newSeasonHistory) { _historyCount = 0; _newSeasonHistory = false; }
            for (int i = 0; i < _liftCount; i++)
            {
                if (_running[i]) _runFaceMs[i] += _stepAdvance;
                else if (_stepDayRate && _liftBuilt[i]) _closedFaceMs[i] += _stepAdvance;
            }
        }

        internal void RunPublish()
        {
            if (_publishTo != null) Write(_publishTo);
            // GU-16: the skiers' places go over beside the snapshot, by a pointer swap, because at thousands of them the
            // column is far bigger than SI-16's 64 KB and copying it into the snapshot would cost more than the tick.
            if (_publishTo != null) PublishSkierRender();
        }

        // ================= commands =================

        void Apply(in SimCommand c)
        {
            if (c.IssuedAgainstTick >= 0 && Tick - c.IssuedAgainstTick > Pacing.StaleCommandTicks) { Reject(c, SimRefusal.Stale); return; }
            var stamped = c;
            stamped.Tick = Tick;
            // GU-02: a view command is stamped from its own sequence, so watching a skier leaves the state's alone.
            stamped.Seq = c.Kind == SimCommandKind.InspectSkier ? _viewSeq++ : _commandSeq++;
            switch (c.Kind)
            {
                case SimCommandKind.SetMountainOpen:
                    if (!SetOpen(c.A != 0, c)) return;
                    break;
                case SimCommandKind.PinSeasonSeed:
                    SeasonSeed = unchecked((ulong)c.A);
                    SeedPinned = true;
                    Events.Add(Tick, SimEventType.SeasonSeedPinned, -1, c.A);
                    break;
                case SimCommandKind.UnpinSeasonSeed:
                    if (!SeedPinned) { Reject(c, SimRefusal.NoChange); return; }
                    SeedPinned = false;
                    SeasonSeed = DrawSeasonSeed();
                    break;
                case SimCommandKind.SetDesign:
                    if (!(c.Payload is SimDesign d)) { Reject(c, SimRefusal.Unknown); return; }
                    FoldDesign(d);
                    Events.Add(Tick, SimEventType.DesignApplied, -1, d.Revision);
                    break;
                case SimCommandKind.SetSnowpack:
                    if (!(c.Payload is SimSnowpack p) || !Snow.Apply(p)) { Reject(c, SimRefusal.Unknown); return; }
                    Events.Add(Tick, SimEventType.SnowpackApplied, -1, (long)p.Width * p.Height);
                    break;
                case SimCommandKind.SetWeather:
                    if (!(c.Payload is SimWeather wx) || !ApplyWeather(wx)) { Reject(c, SimRefusal.Unknown); return; }
                    break;
                case SimCommandKind.SetGust:
                case SimCommandKind.FeedLine:
                    if (!ApplyLiftCommand(c)) return;
                    break;
                case SimCommandKind.InspectSkier:
                case SimCommandKind.SpawnSkiers:
                    if (!ApplyGuestCommand(c)) return;
                    break;
                default:
                    Reject(c, SimRefusal.Unknown);
                    return;
            }
            Log.Add(stamped);
        }

        bool SetOpen(bool open, in SimCommand c)
        {
            if (open == Open) { Reject(c, SimRefusal.NoChange); return false; }
            if (open && Phase == SeasonPhase.Summer) { Reject(c, SimRefusal.NotInSummer); return false; }
            Open = open;
            Events.Add(Tick, open ? SimEventType.MountainOpened : SimEventType.MountainClosed, -1, 0);
            // TM-11 v2: switching to Closed ends a season that has begun.
            if (!open && Phase == SeasonPhase.Season) EndSeason();
            RefreshBoard();
            return true;
        }

        internal void Reject(in SimCommand c, SimRefusal why) => Events.Add(Tick, SimEventType.CommandRejected, -1, (long)c.Kind, (ushort)why);

        /// <summary>SI-20, SI-21: swaps in a design revision; each lift keeps its rope's travel by ID.</summary>
        void FoldDesign(SimDesign d)
        {
            bool newSeed = Design != null && Design.ResortSeed != d.ResortSeed;
            var previous = Design;
            Design = d;
            // SI-15: a different resort seed (a save opened over the start) draws this season's seed again, unless pinned.
            if (newSeed && !SeedPinned) SeasonSeed = DrawSeasonSeed();
            int n = Math.Min(d.Lifts.Length, Pacing.MaxLifts);
            var oldIds = _liftIds;
            int oldCount = _liftCount;
            var ids = new ulong[n];
            var built = new bool[n];
            var runMs = new long[n];
            var running = new bool[n];
            var runFace = new long[n];
            var closedFace = new long[n];
            var open = new byte[n];
            var openNext = new byte[n];
            var hold = new byte[n];
            for (int i = 0; i < n; i++)
            {
                ids[i] = d.Lifts[i].Id;
                built[i] = d.Lifts[i].Built;
                int was = Array.BinarySearch(_liftIds, 0, _liftCount, ids[i]);
                if (was >= 0)
                {
                    runMs[i] = _runMs[was];
                    running[i] = _running[was];
                    runFace[i] = _runFaceMs[was];
                    closedFace[i] = _closedFaceMs[was];
                    hold[i] = _liftHold[was];
                    open[i] = _liftOpen[was];   // LO-10: the board as it was, so a lift that loses its way down mid-day downloads
                    openNext[i] = _liftOpenNext[was];
                }
            }
            _liftIds = ids; _liftBuilt = built; _runMs = runMs; _running = running; _runFaceMs = runFace; _closedFaceMs = closedFace;
            _liftOpen = open; _liftOpenNext = openNext; _liftHold = hold;
            _liftCount = n;
            FoldGuests(previous, d);
            FoldLiftOps(oldIds, oldCount, ids, n);
            RefreshBoard();
        }

        // ================= the day =================

        /// <summary>A new face day at midnight: the label, October 1's new season number, April 1's summer, today's hours.</summary>
        void BeginFaceDay(int faceDay, bool announce)
        {
            int j = faceDay % Calendar.YearFaceDays;
            if (announce)
            {
                if (j == 0)
                {
                    // A new label year: the season count, the seed and pre-season follow (TM-11 v2).
                    if (Phase == SeasonPhase.Season) EndSeason();
                    SeasonNumber++;
                    Phase = SeasonPhase.PreSeason;
                    if (Open) { Open = false; Events.Add(Tick, SimEventType.MountainClosed, -1, 0); }
                    SeasonDay = 0;
                    SeedPinned = false;
                    SeasonSeed = DrawSeasonSeed();
                    _newSeasonHistory = true;   // after the last day's line (RunLedgers)
                    Events.Add(Tick, SimEventType.SeasonNumberRolled, -1, SeasonNumber);
                }
                else if (j == Calendar.April1FaceDay && Phase == SeasonPhase.PreSeason) Phase = SeasonPhase.Summer;   // no season this year
                Events.Add(Tick, SimEventType.FaceDayBegan, -1, faceDay);
            }
            HoursFor(faceDay, out _todayOpen, out _todayLastChair, out _todayStop, out _todayTrimmed);
            _dayStartTick = Tick;
            ResetTrafficDay();
            LiftHoursToday();
            if (_todayTrimmed && announce) Events.Add(Tick, SimEventType.HoursTrimmed, -1, _todayOpen * 100000L + _todayStop);
            RefreshBoard();
        }

        /// <summary>TM-11 v2: at opening, Day 1 when the mountain is Open and a lift serves a run; each later day counts on.</summary>
        void AtOpening()
        {
            if (Phase == SeasonPhase.PreSeason && Open && Design.LiftServesRun)
            {
                Phase = SeasonPhase.Season;
                SeasonDay = 1;
                Events.Add(Tick, SimEventType.SeasonOpened, -1, SeasonNumber);
                RefreshBoard();
            }
            else if (Phase == SeasonPhase.Season) SeasonDay++;
            // GU-03: normally the landing an hour before opening planned the day; a game that starts at the opening (and
            // a save opened there) plans it now, so the first morning has a crowd like any other.
            int today = (int)(FaceMs / DayMs);
            if (_demandDay != today) PlanTheDay(today);
            AnchorArrivals();
            // The ledger's day is the one that opened.
            _ledgerSeasonDay = SeasonDay;
            _ledgerPhase = Phase;
        }

        void EndSeason()
        {
            Phase = SeasonPhase.Summer;
            Events.Add(Tick, SimEventType.SeasonEnded, -1, SeasonNumber);
            RefreshBoard();
        }

        /// <summary>
        /// WX-06 and WX-08's call order: the weather first (4.05: the hour at zref, each lift's top-station gust), then snow
        /// (4.07), lakes (4.09) and trail readiness (4.07's stand-in: every built trail is Ready). The board flips once at the
        /// end (SI-19): the hour's work is one slice today, so it finishes in the tick it starts.
        /// </summary>
        void HourStep(long hour)
        {
            HourStepWeather(hour);
            _hourSliceCursor = 0;
            _trailsReadyNext = Design.BuiltTrails;
            BoardLifts(_liftOpenNext);
            FlipBoard();
            HourIndex = hour;
            HourHash = Hash();
        }

        void FlipBoard()
        {
            var t = _liftOpen; _liftOpen = _liftOpenNext; _liftOpenNext = t;
            _trailsReady = _trailsReadyNext;
        }

        /// <summary>The board between hours: an open/close or a new design changes it at once rather than waiting for the hour.</summary>
        void RefreshBoard()
        {
            if (_candidate.Length < _liftCount) return;   // mid-fold: FoldDesign refreshes once the lifts' operations are in
            BoardLifts(_liftOpen);
            _trailsReady = Design.BuiltTrails;
        }

        bool AnyBuiltLift()
        {
            for (int i = 0; i < _liftCount; i++) if (_liftBuilt[i] && _liftOpen[i] != 0 && _liftHold[i] == 0) return true;
            return false;
        }

        /// <summary>TM-02 v2: the day rate runs from the resort's opening to its stop on a season day.</summary>
        bool DayRateAt(long sod) => Phase == SeasonPhase.Season && sod >= _todayOpen * 1000L && sod < _todayStop * 1000L;

        /// <summary>Whether a step starting at <paramref name="sod"/> starts on a boundary (so the state never depends on step size).</summary>
        bool IsBoundary(long sod) => sod % HourMs == 0 || sod == _todayOpen * 1000L || sod == _todayStop * 1000L
            || sod == LandingSecond(_todayOpen) * 1000L;

        /// <summary>The next boundary after <paramref name="sod"/> today, in face ms of the day (the next whole hour at the latest).</summary>
        long NextBoundary(long sod, bool windChecks)
        {
            long next = (sod / HourMs + 1) * HourMs;
            long land = LandingSecond(_todayOpen) * 1000L, open = _todayOpen * 1000L, stop = _todayStop * 1000L;
            if (land > sod && land < next) next = land;
            if (open > sod && open < next) next = open;
            if (stop > sod && stop < next) next = stop;
            return LiftBoundary(sod, next, windChecks);
        }

        /// <summary>Owner, 2026-11-08 (plan question 8): Skip to morning lands an hour before opening.</summary>
        int LandingSecond(int openSecond) => Math.Max(0, openSecond - Pacing.SkipLandsBeforeOpenMinutes * 60);

        /// <summary>
        /// LO-07 v2 with question 10 (owner, 2026-11-08): the resort's hours on a face day, cut to civil dawn and dusk at the
        /// site on the label date (the lighting's sun function, WX-04). Whole minutes: dawn rounds up, dusk down.
        /// </summary>
        void HoursFor(int faceDay, out int open, out int lastChair, out int stop, out bool trimmed)
        {
            var h = Design.Hours;
            open = h.OpenSecond; lastChair = h.LastChairSecond; stop = h.StopSecond;
            trimmed = false;
            if (!Site.Known) return;
            int y = StartYear + faceDay / Calendar.YearFaceDays, j = faceDay % Calendar.YearFaceDays;
            var sun = new SolarDay(Site.Latitude, Site.Longitude, Calendar.CalendarYear(y, j), Calendar.DayOfYear(y, j));
            double dawn = sun.CivilDawn(), dusk = sun.CivilDusk();
            int first, last;
            if (double.IsNaN(dawn) || double.IsNaN(dusk))
            {
                bool light = sun.SunAt(sun.SolarNoon()).GeometricElevation > SolarDay.CivilDegrees;
                first = 0; last = light ? DayHours.SecondsPerDay : 0;
            }
            else
            {
                first = (int)Math.Ceiling(dawn / 60.0) * 60;
                last = (int)Math.Floor(dusk / 60.0) * 60;
            }
            if (open >= first && stop <= last) return;
            trimmed = true;
            open = Math.Max(open, first);
            stop = Math.Max(open, Math.Min(stop, last));
            lastChair = Math.Max(open, Math.Min(lastChair, stop));
        }

        ulong DrawSeasonSeed() => SimKey.Hash(Design.ResortSeed, SimKey.SeasonSeedTag, (ulong)SeasonNumber);

        // ================= ledgers =================

        void ResetLedger()
        {
            _ledgerDay = (int)(FaceMs / DayMs);
            _ledgerType = Calendar.TypeOf(_ledgerDay % Calendar.YearFaceDays);
            _ledgerSeasonDay = SeasonDay;
            _ledgerPhase = Phase;
            Array.Clear(_runFaceMs, 0, _runFaceMs.Length);
            Array.Clear(_closedFaceMs, 0, _closedFaceMs.Length);
            ResetLiftLedger();
        }

        /// <summary>TM-15: one report at closing, built from the ledgers; zeros where guests and snow don't exist yet.</summary>
        void WriteDayReport()
        {
            var r = _lastReport;
            r.FaceDay = _ledgerDay;
            r.SeasonDay = _ledgerSeasonDay;
            r.Phase = _ledgerPhase;
            r.DayType = _ledgerType;
            r.OpenSecond = _todayOpen;
            r.StopSecond = _todayStop;
            r.Trimmed = _todayTrimmed;
            var g = GuestLedger();
            r.Target = g.demand;
            r.Arrived = g.arrived;
            r.TurnedAway = g.arrivedClosed + g.capped;
            r.WentHome = g.wentHome;
            r.Stranded = g.stranded;
            r.LiftCount = _liftCount;
            for (int i = 0; i < _liftCount; i++)
            {
                r.RunMinutes[i] = (int)(_runFaceMs[i] / 60_000);
                r.ClosedMinutes[i] = (int)(_closedFaceMs[i] / 60_000);
            }
            WriteLiftReport(r);
            Events.Add(Tick, SimEventType.DayReport, -1, _ledgerDay);
        }

        /// <summary>TM-15's season history: a line for every face day, at midnight.</summary>
        void WriteHistoryLine()
        {
            if (_historyCount == _historyFaceDay.Length) return;
            int k = _historyCount++;
            int run = 0, lifts = 0;
            for (int i = 0; i < _liftCount; i++)
            {
                run += (int)(_runFaceMs[i] / 60_000);
                if (_runFaceMs[i] > 0) lifts++;
            }
            _historyFaceDay[k] = _ledgerDay;
            _historySeasonDay[k] = _ledgerSeasonDay;
            _historyType[k] = (byte)_ledgerType;
            _historyPhase[k] = (byte)_ledgerPhase;
            _historyRunMinutes[k] = run;
            _historyLiftsRun[k] = lifts;
        }

        public int HistoryCount => _historyCount;
        public int HistoryRunMinutes(int k) => _historyRunMinutes[k];
        public int HistoryFaceDay(int k) => _historyFaceDay[k];
        public DayType HistoryType(int k) => (DayType)_historyType[k];
        public DayReport LastReport => _lastReport;

        // ================= skips (TM-06 v2, TM-14) =================

        /// <summary>
        /// Where a skip of <paramref name="kind"/> would land, in face ms, or a refusal. Morning: an hour before the next
        /// opening, from the night or a day nobody skis, never while the lifts run (TM-06 v2). The next third: only before the
        /// season (TM-14). The next month: only in summer (TM-08).
        /// </summary>
        public bool SkipTarget(SkipKind kind, out long target)
        {
            target = 0;
            long sod = FaceMs % DayMs;
            long dayStart = FaceMs - sod;
            int faceDay = (int)(FaceMs / DayMs);
            switch (kind)
            {
                case SkipKind.Morning:
                {
                    if (DayRateAt(sod)) return false;
                    long landToday = dayStart + LandingSecond(_todayOpen) * 1000L;
                    if (FaceMs < landToday) { target = landToday; return true; }
                    if (Phase == SeasonPhase.Season && sod < _todayOpen * 1000L) return false;   // already morning
                    target = LandingOn(faceDay + 1);
                    return true;
                }
                case SkipKind.NextThird:
                {
                    if (Phase != SeasonPhase.PreSeason) return false;
                    int j = faceDay % Calendar.YearFaceDays;
                    target = LandingOn(faceDay - j + Calendar.NextThirdStart(j));
                    return true;
                }
                case SkipKind.NextMonth:
                {
                    if (Phase != SeasonPhase.Summer) return false;
                    int j = faceDay % Calendar.YearFaceDays;
                    target = LandingOn(faceDay - j + Calendar.NextMonthStart(j));
                    return true;
                }
                default:
                    return false;
            }
        }

        long LandingOn(int faceDay)
        {
            HoursFor(faceDay, out int open, out _, out _, out _);
            return faceDay * DayMs + LandingSecond(open) * 1000L;
        }

        // ================= reading =================

        /// <summary>Writes the world's part of a snapshot (SI-16).</summary>
        public void Write(SimSnapshot s)
        {
            s.Tick = Tick;
            s.FaceMs = FaceMs;
            int faceDay = (int)(FaceMs / DayMs);
            int j = faceDay % Calendar.YearFaceDays;
            s.FaceDay = faceDay;
            s.LabelYear = StartYear + faceDay / Calendar.YearFaceDays;
            s.FaceDayOfYear = j;
            s.SecondOfDay = (int)(FaceMs % DayMs / 1000);
            s.ViewYear = Calendar.CalendarYear(s.LabelYear, j);
            s.ViewDayOfYear = Calendar.DayOfYear(s.LabelYear, j);
            s.Month = Calendar.Month(j);
            s.DayOfMonth = Calendar.Day(j);
            s.Third = Calendar.Third(j);
            s.DayType = Calendar.TypeOf(j);
            s.Phase = Phase;
            s.Open = Open;
            s.SeasonNumber = SeasonNumber;
            s.SeasonDay = SeasonDay;
            s.SeasonSeed = SeasonSeed;
            s.SeedPinned = SeedPinned;
            s.OpenSecond = _todayOpen;
            s.LastChairSecond = _todayLastChair;
            s.StopSecond = _todayStop;
            s.HoursTrimmed = _todayTrimmed;
            s.DayRate = DayRateAt(FaceMs % DayMs);
            s.DesignRevision = Design.Revision;
            s.LiftCount = _liftCount;
            for (int i = 0; i < _liftCount; i++)
            {
                s.LiftIds[i] = _liftIds[i];
                s.RunMs[i] = _runMs[i];
                s.LiftStates[i] = !_liftBuilt[i] ? LiftRunState.Planned : _running[i] ? LiftRunState.Running : LiftRunState.Parked;
                s.LiftOpen[i] = _liftOpen[i];
            }
            WriteLifts(s, FaceMs % DayMs);
            s.TrailsReady = _trailsReady;
            if (_lastReport.FaceDay != s.LastDayReport.FaceDay || _lastReport.LiftCount != s.LastDayReport.LiftCount)
                s.LastDayReport.CopyFrom(_lastReport);
            s.HistoryCount = _historyCount;
            WriteGuests(s);
            s.FirstEventSeq = Events.CopyNewest(s.Events, out s.EventCount);
            s.SnowSet = Snow.Set;
            WriteWeather(s);
        }

        /// <summary>The run time of a lift by ID, ms, or −1 (tests).</summary>
        public long RunMsOf(ulong liftId)
        {
            int i = Array.BinarySearch(_liftIds, 0, _liftCount, liftId);
            return i >= 0 ? _runMs[i] : -1;
        }

        /// <summary>SI-08: a hash of the whole state, integers only, in a fixed order (no allocation).</summary>
        public ulong Hash()
        {
            var h = new StateHasher();
            h.Add(Pacing.ContentsHash);
            h.Add(Tick); h.Add(FaceMs);
            h.Add((long)Phase); h.Add(Open ? 1 : 0); h.Add(SeasonNumber); h.Add(SeasonDay); h.Add(SeasonSeed); h.Add(SeedPinned ? 1 : 0);
            h.Add(Design.RevisionHash); h.Add(Design.ResortSeed);
            h.Add(_todayOpen); h.Add(_todayLastChair); h.Add(_todayStop); h.Add(_todayTrimmed ? 1 : 0); h.Add(_boundaryDoneAt);
            h.Add(_weatherHour); h.Add(_weatherTenthsF); h.Add(_weatherWindMph); h.Add(_hourSliceCursor);
            h.Add(_trailsReady);
            h.Add(_liftCount);
            for (int i = 0; i < _liftCount; i++)
            {
                h.Add(_liftIds[i]); h.Add(_liftBuilt[i] ? 1 : 0); h.Add(_runMs[i]); h.Add(_running[i] ? 1 : 0);
                h.Add(_liftOpen[i]); h.Add(_liftHold[i]); h.Add(_runFaceMs[i]); h.Add(_closedFaceMs[i]);
            }
            h.Add(_ledgerDay); h.Add(_ledgerSeasonDay); h.Add((long)_ledgerType); h.Add((long)_ledgerPhase);
            h.Add(_historyCount);
            for (int k = 0; k < _historyCount; k++) { h.Add(_historyFaceDay[k]); h.Add(_historyRunMinutes[k]); h.Add(_historyLiftsRun[k]); h.Add(_historyType[k]); }
            h.Add(_lastReport.FaceDay);
            h.Add(_commandSeq);
            HashLifts(ref h);
            HashWeather(ref h);
            // The snow joined in PR B (SI-17): a world that has never had any hashes as before, so the goldens stand.
            if (Snow.Set || Snow.Width > 0) Snow.AddTo(ref h);
            HashGuests(ref h);
            return h.Value;
        }
    }

    /// <summary>SI-06: the simulation's own tag register, apart from <see cref="DesignKey"/>'s 1–15. A tag is never reused.</summary>
    public static class SimKey
    {
        const ulong Base = 0x5349_4D00_0000_0000UL;   // "SIM"
        /// <summary>SI-15: a season's seed from the resort's seed and the season number.</summary>
        public const ulong SeasonSeedTag = Base + 1;
        /// <summary>LO-11 v1: a lift's wind check offset within the hour and its gust factor.</summary>
        public const ulong WindCheckTag = Base + 2;
        /// <summary>GU-06: which base a party arrives at.</summary>
        public const ulong ArrivalTag = Base + 3;
        /// <summary>GU-05: a skier's ability within its band.</summary>
        public const ulong AbilityTag = Base + 4;
        /// <summary>GU-06: how many laps a skier means to ski.</summary>
        public const ulong StayTag = Base + 5;
        /// <summary>GU-10: a skier's speed against its band's base.</summary>
        public const ulong SpeedTag = Base + 6;
        /// <summary>GU-02: how many skiers a party is.</summary>
        public const ulong PartyTag = Base + 7;
        /// <summary>GU-16, SN-24: which lateral band of a trail a skier takes.</summary>
        public const ulong LaneTag = Base + 8;
        /// <summary>GU-07: a party's weighted draw between the runs and lifts on offer.</summary>
        public const ulong ChoiceTag = Base + 9;

        public static ulong Hash(ulong seed, ulong tag, ulong a) => DesignKey.Hash(seed, tag, a);
        public static ulong Hash(ulong seed, ulong tag, ulong a, ulong b) => DesignKey.Hash(seed, tag, a, b);
    }

    /// <summary>FNV-1a over 64-bit words: the state hash (SI-08). A struct, so hashing allocates nothing.</summary>
    public struct StateHasher
    {
        ulong _h;
        bool _started;

        public void Add(long v) => Add(unchecked((ulong)v));

        public void Add(ulong v)
        {
            if (!_started) { _h = 14695981039346656037UL; _started = true; }
            for (int i = 0; i < 8; i++)
            {
                _h ^= (byte)(v >> (8 * i));
                _h *= 1099511628211UL;
            }
        }

        public ulong Value => _started ? _h : 14695981039346656037UL;
    }
}
