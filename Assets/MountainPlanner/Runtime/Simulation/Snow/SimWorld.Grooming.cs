using System;
using MountainPlanner.Domain.Design;
using MountainPlanner.Domain.Snow;

namespace MountainPlanner.Simulation
{
    /// <summary>
    /// Task 4.25: grooming operations (GO-01…GO-12, SN-22 v4, SM-05), which replace 4.07's single 8 PM pass.
    /// - **Shifts (GO-03 v2):** swing from the lift stop and night to the next opening, splitting the time between in half
    ///   (8 h each at the default hours), day <see cref="Pacing.GroomDayStartHour"/>–<see cref="Pacing.GroomDayEndHour"/>
    ///   on closed trails only. A shift's first and last <see cref="Pacing.GroomTravelMinutes"/> are travel.
    /// - **Tasks (GO-07, SN-22 v4):** at each shift's start every trail gets a job (till, push, none) from its setting and
    ///   snow; the list is ordered High, pushes that open a closed trail, open trails by their skier passes since the last
    ///   groom, closed ones, Low; then trail id.
    /// - **Lanes (GO-05, GO-08):** Autopilot crews just enough, night before swing; pins win. Each task piece goes to the crewed
    ///   groomer free soonest (lowest number on a tie), splitting at a window's end; a trail with new snow tonight is laid
    ///   from the night's end backward (B2a-3: groomed after the snowfall). A W groomer takes a trail with a pitch past
    ///   <see cref="Pacing.GroomWinchGrade"/>; with none, the pitch is skipped.
    /// - **Work:** each hour roll moves every piece's share of its task on; a task done calls <see cref="GroomTrail"/> once.
    ///   A part-done trail keeps its share for the next shift.
    /// - **Morning (GO-09):** at the night's end, what wasn't reached and the minutes short (<see cref="SimEventType.GroomShortfall"/>).
    /// All state lives in per-trail arrays in the layout's trail order, keyed by <see cref="_gIds"/> so a save restores
    /// before its layout arrives (sidecar v13). A world never given a fleet hashes and saves as before.
    /// </summary>
    public sealed partial class SimWorld
    {
        // ---- the design's grooming (SetGrooming A = 0) ----
        SimGrooming _gPlan = SimGrooming.None;
        bool _gEver;                                   // a fleet arrived once: the grooming joins the hash and the sidecar
        int _gCount;
        readonly int[] _gNumber = new int[GroomFleet.MaxGroomers];
        readonly byte[] _gModel = new byte[GroomFleet.MaxGroomers];
        readonly byte[] _gDrive = new byte[GroomFleet.MaxGroomers];
        readonly bool[] _gTiller = new bool[GroomFleet.MaxGroomers];
        readonly int[] _gSeasonMin = new int[GroomFleet.MaxGroomers];

        // ---- per trail (layout order, keyed by _gIds) ----
        ulong[] _gIds = Array.Empty<ulong>();
        TrailSnowLayout _gLayout;                      // the layout the static figures below were taken from
        GroomSetting[] _gSetting = Array.Empty<GroomSetting>();
        GroomPriority[] _gPrio = Array.Empty<GroomPriority>();
        byte[] _gOrder = Array.Empty<byte>(), _gPowderNights = Array.Empty<byte>(), _gWarned = Array.Empty<byte>();
        // SN-22 v4: the snow (water, µm) each trail gained since its last groom, from its mean water once an hour (snowfall and
        // guns; melt and wear don't take it back), and that mean an hour ago. A trail never groomed counts all of its snow.
        int[] _gNewUm = Array.Empty<int>();
        // SN-22 v4's powder: the snow (water, µm) each trail gained since the last morning, so a steep trail keeps the fresh
        // powder of the latest storm for the next morning, not all season.
        int[] _gStormUm = Array.Empty<int>();
        long[] _gSwePrev = Array.Empty<long>();
        GroomRowState[] _gLast = Array.Empty<GroomRowState>();
        // PR B (GO-14), for the screen only (neither hashed nor saved): last night's lane per trail (its groomer and its
        // minutes from that night's first shift, -1 none) and the hours since each trail's last groom (255 none).
        short[] _gLastGroomer = Array.Empty<short>(), _gLastStart = Array.Empty<short>(), _gLastEnd = Array.Empty<short>();
        byte[] _gSinceH = Array.Empty<byte>();
        long[] _gGroomedHour = Array.Empty<long>();   // the hour roll (+1) each trail was last groomed this session; 0 unknown
        int _gLastZeroAbs, _gLastEndAbs;
        int[] _gCarryPpm = Array.Empty<int>();         // the share of an unfinished task already done (parts per million)
        byte[] _gTonight = Array.Empty<byte>();        // 1: groomed since this shift's plan began (a replan leaves it)
        int[] _gWorkedPending;                         // a load's worked blocks (their 8 m cells), marked when the layout comes
        // static per trail: centi-acres by slope band (≤ full speed, ≤ winch, ≤ winch max, beyond), width, mean grade
        int[] _gAcres = Array.Empty<int>(), _gWidthMm = Array.Empty<int>(), _gGrade = Array.Empty<int>();

        // ---- tonight ----
        int _gNight = -1, _gDayPlanned = -1, _gMorningDone = -1;
        long _gLastAbs = long.MinValue;
        readonly int[] _gShiftStart = new int[3], _gShiftEnd = new int[3], _gCrews = new int[3];
        bool _gSnowing;
        int _gWant, _gCrewMin, _gShort, _gCost;
        // tasks (one per trail at most) and their pieces (at most PiecesPerTask each)
        const int PiecesPerTask = 6;
        int _kCount, _pCount;
        int[] _kTrail = Array.Empty<int>(), _kDone = Array.Empty<int>(), _kNeed = Array.Empty<int>(), _kStart = Array.Empty<int>(), _kEnd = Array.Empty<int>();
        int[] _kPushYd3 = Array.Empty<int>();          // the task's pile, yd³ (taken at the plan)
        // GO-16 v2, planning scratch (Schedule only, never saved): a task's minutes saved by passes groomed on the way there
        // (ppm of its need) and whether it has a piece yet
        int[] _kWay = Array.Empty<int>();
        bool[] _kLaid = Array.Empty<bool>();
        byte[] _kJob = Array.Empty<byte>(), _kState = Array.Empty<byte>(), _kGroomer = Array.Empty<byte>(), _kFlags = Array.Empty<byte>();
        int[] _pTask = Array.Empty<int>(), _pFrom = Array.Empty<int>(), _pTo = Array.Empty<int>(), _pPpm = Array.Empty<int>();
        byte[] _pLane = Array.Empty<byte>();
        const byte KLate = 1, KWinch = 2, KSkipPitch = 4, KGreen = 8, KDay = 16;
        // GO-16 (sidecar v14): the task starts at the trail's tail (its foot) and works up; something on it was groomed
        const byte KFromTail = 32, KAny = 64;
        // A transit piece (GO-15, sidecar v14) has task -1; its share field packs the nodes it drives from and to.
        const int TransitTask = -1;
        // last night (GO-09)
        int _gLastNight = -1, _gLastNotReached, _gLastShort, _gLastWork, _gLastCost;
        // Round 3 (the drawn-resort capture, 2026-11-21: "0.0 h worked"): what the night actually worked on each trail (first
        // and last minute, the lane's groomer), kept across replans, which drop the pieces already worked; not saved (the
        // Last night strip isn't), cleared at each night's plan.
        int _gLastWorkShown = -1;
        int[] _gNightFrom = Array.Empty<int>(), _gNightTo = Array.Empty<int>(), _gNightLane = Array.Empty<int>(), _gNightMin = Array.Empty<int>();

        void ClearNightWorked()
        {
            int n = _gSetting.Length;
            if (_gNightFrom.Length != n) { _gNightFrom = new int[n]; _gNightTo = new int[n]; _gNightLane = new int[n]; _gNightMin = new int[n]; }
            for (int t = 0; t < n; t++) { _gNightFrom[t] = -1; _gNightTo[t] = -1; _gNightLane[t] = -1; _gNightMin[t] = 0; }
        }
        // the warnings raised lately (a ring, for the tab)
        int _gWarnCount;
        readonly byte[] _gWarnKind = new byte[GroomView.MaxWarnings];
        readonly int[] _gWarnTrail = new int[GroomView.MaxWarnings];
        // planning scratch (sized with the trails; no allocation per hour)
        long[] _gKey = Array.Empty<long>();
        int[] _gSort = Array.Empty<int>();
        readonly int[] _laneFree = new int[GroomFleet.MaxGroomers * 3], _laneLate = new int[GroomFleet.MaxGroomers * 3];
        readonly int[] _laneEnd = new int[GroomFleet.MaxGroomers * 3], _laneBegin = new int[GroomFleet.MaxGroomers * 3];
        readonly bool[] _laneOn = new bool[GroomFleet.MaxGroomers * 3];
        // screen only (not hashed or saved; a reopen starts them at 0): each lane's minutes worked this night or day, and
        // the minute the work has reached, for the Equipment page's hours
        readonly int[] _gLaneWorkMin = new int[GroomFleet.MaxGroomers * 3], _gLaneTransitMin = new int[GroomFleet.MaxGroomers * 3];
        int _gWorkedTo;
        int _gWayPasses;   // GO-16 v2, screen only: stretches groomed on the way since the shift began (a reopen starts at 0)
        // GO-15: the routes. Per layout trail its head and tail node in the design's network (-1: none), the depot (the foot
        // of the lowest built lift), each lane's node, and the distance rows from the nodes asked (mm over trails only),
        // built when first asked after the network or the layout changes (not per tick).
        SimNetwork _gNet;
        Domain.Snow.TrailSnowLayout _gRoutesLayout;
        int[] _gHead = Array.Empty<int>(), _gTail = Array.Empty<int>();
        int _gDepot = -1;
        ulong _gDepotLift;
        int[][] _gDistRow = Array.Empty<int[]>();
        int[][] _gPredRow = Array.Empty<int[]>();   // per distance row, each node's edge in from the source (-1: none)
        int[] _gLayoutOfNet = Array.Empty<int>();    // per network trail its layout trail (-1: none)
        int[] _hNode = Array.Empty<int>();
        long[] _hKey = Array.Empty<long>();
        readonly int[] _lanePos = new int[GroomFleet.MaxGroomers * 3];
        readonly bool[] _laneOut = new bool[GroomFleet.MaxGroomers * 3];
        // GO-05 v2: the order Auto crews the groomers in (a W first when a steep pitch waits, then the faster 800s)
        readonly int[] _gRank = new int[GroomFleet.MaxGroomers];

        // GO-14 v3 (owner 2026-11-21: the hours update as soon as you pick): during the day, tonight's plan made on a copy
        // whenever the grooming or the hour changes, and shown on the tab; everything it touched goes back, so it is never
        // hashed or saved and moves nothing.
        sealed class GroomPlanSet
        {
            public int KCount, PCount, Want, CrewMin, Short, Cost;
            public bool Snowing;
            public int[] KTrail = Array.Empty<int>(), KDone = Array.Empty<int>(), KNeed = Array.Empty<int>(), KStart = Array.Empty<int>(),
                         KEnd = Array.Empty<int>(), KPushYd3 = Array.Empty<int>(), KWay = Array.Empty<int>(),
                         PTask = Array.Empty<int>(), PFrom = Array.Empty<int>(), PTo = Array.Empty<int>(), PPpm = Array.Empty<int>();
            public byte[] KJob = Array.Empty<byte>(), KState = Array.Empty<byte>(), KGroomer = Array.Empty<byte>(), KFlags = Array.Empty<byte>(),
                          PLane = Array.Empty<byte>(), Warned = Array.Empty<byte>(), Tonight = Array.Empty<byte>();
            public bool[] KLaid = Array.Empty<bool>();
            public readonly int[] ShiftStart = new int[3], ShiftEnd = new int[3], Crews = new int[3], Rank = new int[GroomFleet.MaxGroomers];
        }

        readonly GroomPlanSet _gPreview = new GroomPlanSet();
        bool _gPreviewDirty = true, _gPreviewing, _gPreviewOn;
        int _gPreviewDay = -1;
        /// <summary>Tests: no preview (to show it changes nothing).</summary>
        internal bool GroomPreviewOff;

        /// <summary>The fleet (tests and the readout).</summary>
        internal int GroomerCount => _gCount;

        /// <summary>Swaps the live plan with the preview's (twice is none).</summary>
        void SwapPlan()
        {
            var o = _gPreview;
            Swap(ref _kTrail, ref o.KTrail); Swap(ref _kDone, ref o.KDone); Swap(ref _kNeed, ref o.KNeed); Swap(ref _kStart, ref o.KStart);
            Swap(ref _kEnd, ref o.KEnd); Swap(ref _kPushYd3, ref o.KPushYd3); Swap(ref _kWay, ref o.KWay); Swap(ref _kLaid, ref o.KLaid);
            Swap(ref _kJob, ref o.KJob); Swap(ref _kState, ref o.KState); Swap(ref _kGroomer, ref o.KGroomer); Swap(ref _kFlags, ref o.KFlags);
            Swap(ref _pTask, ref o.PTask); Swap(ref _pFrom, ref o.PFrom); Swap(ref _pTo, ref o.PTo); Swap(ref _pPpm, ref o.PPpm); Swap(ref _pLane, ref o.PLane);
            Swap(ref _gWarned, ref o.Warned); Swap(ref _gTonight, ref o.Tonight);
            Swap(ref _kCount, ref o.KCount); Swap(ref _pCount, ref o.PCount);
            Swap(ref _gWant, ref o.Want); Swap(ref _gCrewMin, ref o.CrewMin); Swap(ref _gShort, ref o.Short); Swap(ref _gCost, ref o.Cost);
            Swap(ref _gSnowing, ref o.Snowing);
            SwapValues(_gShiftStart, o.ShiftStart); SwapValues(_gShiftEnd, o.ShiftEnd); SwapValues(_gCrews, o.Crews); SwapValues(_gRank, o.Rank);
        }

        static void Swap<T>(ref T a, ref T b) { var t = a; a = b; b = t; }

        static void SwapValues(int[] a, int[] b)
        {
            for (int i = 0; i < a.Length; i++) { int t = a[i]; a[i] = b[i]; b[i] = t; }
        }

        /// <summary>
        /// GO-14 v3: tonight's plan while no night is planned (the day, from the morning to the swing's start), made as
        /// <see cref="PlanNight"/> would make it now, on the preview's copy; warnings raise no events.
        /// </summary>
        void PreviewTonight()
        {
            _gPreviewDirty = false;
            _gPreviewOn = false;
            if (GroomPreviewOff || _gNight >= 0 || _trailLayout == null || _fine == null || _gSetting.Length != _trailLayout.Trails || _gCount == 0) return;
            int now = (int)Math.Min(int.MaxValue, FaceMs / 60_000);
            int day = now / 1440;
            NightTimes(day, out int swingStart, out int nightStart, out int nightEnd);
            if (now >= nightEnd) NightTimes(++day, out swingStart, out nightStart, out nightEnd);
            int n = _trailLayout.Trails;
            SwapPlan();
            _gPreviewing = true;
            try
            {
                SizeGroomTasks(n);
                if (_gWarned.Length != n) _gWarned = new byte[n]; else Array.Clear(_gWarned, 0, n);
                if (_gTonight.Length != n) _gTonight = new byte[n]; else Array.Clear(_gTonight, 0, n);
                _gShiftStart[0] = swingStart; _gShiftEnd[0] = nightStart;
                _gShiftStart[1] = nightStart; _gShiftEnd[1] = nightEnd;
                _gShiftStart[2] = 0; _gShiftEnd[2] = 0;
                var wx = WeatherNow;
                _gSnowing = wx.StormOrdinal >= 0 || (wx.Liquid > 0 && wx.Temp <= 340);
                BuildTasks(dayShift: false);
                DecideCrews(dayShift: false);
                Schedule(swingStart, dayShift: false);
                _gPreviewDay = day;
                _gPreviewOn = true;
            }
            finally
            {
                _gPreviewing = false;
                SwapPlan();
            }
        }

        bool ApplyGrooming(in SimCommand c)
        {
            if (c.A == 1)
            {
                if (!(c.Payload is SimGroomOrder o) || o.Order > GroomOrder.Skip) return false;
                int t = GroomIndexOf(o.TrailId);
                if (t < 0) return false;
                _gOrder[t] = (byte)o.Order;
                _gEver = true;
                _gPreviewDirty = true;
                // GO-14 v3: an order mid-night is planned at once from now (the tab's hours follow it)
                if (_gNight >= 0 && _gLastAbs != long.MinValue) ReplanNow((int)Math.Min(int.MaxValue, _gLastAbs));
                return true;
            }
            if (!(c.Payload is SimGrooming g) || g.Problem() != null) return false;
            if (g.SameAs(_gPlan)) return true;
            // GO-01: who joined and who left, by number.
            for (int i = 0; i < _gCount; i++)
                if (Array.IndexOf(g.Numbers, _gNumber[i]) < 0) Events.Add(Tick, SimEventType.GroomerSold, _gNumber[i], _gModel[i]);
            var oldSeason = new int[_gCount]; var oldNumbers = new int[_gCount];
            Array.Copy(_gSeasonMin, oldSeason, _gCount); Array.Copy(_gNumber, oldNumbers, _gCount);
            int oldCount = _gCount;
            _gCount = g.Numbers.Length;
            for (int i = 0; i < _gCount; i++)
            {
                int was = Array.IndexOf(oldNumbers, g.Numbers[i], 0, oldCount);
                if (was < 0) Events.Add(Tick, SimEventType.GroomerBought, g.Numbers[i], (long)g.Models[i]);
                _gNumber[i] = g.Numbers[i]; _gModel[i] = (byte)g.Models[i]; _gDrive[i] = (byte)g.Drives[i]; _gTiller[i] = g.Tillers[i];
                _gSeasonMin[i] = was >= 0 ? oldSeason[was] : 0;
            }
            for (int i = _gCount; i < GroomFleet.MaxGroomers; i++) { _gNumber[i] = 0; _gModel[i] = 0; _gDrive[i] = 0; _gTiller[i] = false; _gSeasonMin[i] = 0; }
            _gPlan = g;
            _gPreviewDirty = true;
            if (_gCount > 0) _gEver = true;
            MapGroomSettings();
            // A groomer sold mid-night leaves its pieces: their tasks are planned again at once from now.
            if (_kCount > 0 && _gLastAbs != long.MinValue) ReplanNow((int)Math.Min(int.MaxValue, _gLastAbs));
            return true;
        }

        // ---------- the layout and the settings ----------

        /// <summary>The per-trail arrays follow the layout by trail id (a new trail starts clean, a gone one is dropped).</summary>
        void GroomLayoutChanged()
        {
            _gPreviewDirty = true;   // GO-14 v3: a preview made for the old layout is never swapped in
            var L = _trailLayout;
            if (L == null) return;
            int n = L.Trails;
            bool same = _gIds.Length == n;
            for (int t = 0; same && t < n; t++) same = _gIds[t] == L.TrailId[t];
            if (!same)
            {
                var ids = new ulong[n];
                Array.Copy(L.TrailId, ids, n);
                var map = new int[_gIds.Length];
                for (int o = 0; o < _gIds.Length; o++) map[o] = Array.IndexOf(ids, _gIds[o]);
                _gOrder = Remap(_gOrder, map, n); _gPowderNights = Remap(_gPowderNights, map, n); _gWarned = Remap(_gWarned, map, n);
                _gNewUm = Remap(_gNewUm, map, n); _gSwePrev = Remap(_gSwePrev, map, n); _gStormUm = Remap(_gStormUm, map, n);
                _gLast = Remap(_gLast, map, n); _gCarryPpm = Remap(_gCarryPpm, map, n); _gTonight = Remap(_gTonight, map, n);
                _gGroomedHour = Remap(_gGroomedHour, map, n);
                _gLastGroomer = RemapLane(_gLastGroomer, map, n); _gLastStart = RemapLane(_gLastStart, map, n); _gLastEnd = RemapLane(_gLastEnd, map, n);
                // Tonight's tasks follow their trails; a task whose trail is gone goes with it.
                int kept = 0;
                var newIndex = new int[_kCount];
                for (int k = 0; k < _kCount; k++)
                {
                    int nt = _kTrail[k] >= 0 && _kTrail[k] < map.Length ? map[_kTrail[k]] : -1;
                    newIndex[k] = nt >= 0 ? kept : -1;
                    if (nt < 0) continue;
                    _kTrail[kept] = nt; _kDone[kept] = _kDone[k]; _kNeed[kept] = _kNeed[k]; _kStart[kept] = _kStart[k]; _kEnd[kept] = _kEnd[k];
                    _kJob[kept] = _kJob[k]; _kState[kept] = _kState[k]; _kGroomer[kept] = _kGroomer[k]; _kFlags[kept] = _kFlags[k]; _kPushYd3[kept] = _kPushYd3[k];
                    kept++;
                }
                int pk = 0;
                for (int p = 0; p < _pCount; p++)
                {
                    int nk = _pTask[p] == TransitTask ? TransitTask : newIndex[_pTask[p]];
                    if (nk < 0 && nk != TransitTask) continue;
                    _pTask[pk] = nk; _pFrom[pk] = _pFrom[p]; _pTo[pk] = _pTo[p]; _pPpm[pk] = _pPpm[p]; _pLane[pk] = _pLane[p];
                    pk++;
                }
                _gIds = ids;
                SizeGroomTasks(n);
                _kCount = kept; _pCount = pk;
            }
            else SizeGroomTasks(n);
            // A save mid-night: the blocks already groomed tonight (the morning's count) are marked again by their 8 m cell.
            if (_gWorkedPending != null)
            {
                foreach (int cell in _gWorkedPending) { int b = L.BlockOf(cell); if (b >= 0 && b < _blockWorked.Length) _blockWorked[b] = true; }
                _gWorkedPending = null;
            }
            _gSetting = new GroomSetting[n]; _gPrio = new GroomPriority[n];
            _gKey = new long[n]; _gSort = new int[n];
            MapGroomSettings();
            GroomStatics();
            GroomSince();
        }

        static T[] Remap<T>(T[] old, int[] map, int n)
        {
            var a = new T[n];
            for (int o = 0; o < map.Length && o < old.Length; o++) if (map[o] >= 0) a[map[o]] = old[o];
            return a;
        }

        static short[] RemapLane(short[] old, int[] map, int n)
        {
            var a = new short[n];
            for (int i = 0; i < n; i++) a[i] = -1;
            for (int o = 0; o < map.Length && o < old.Length; o++) if (map[o] >= 0) a[map[o]] = old[o];
            return a;
        }

        /// <summary>GO-14: the hours since each trail's last groom: its youngest groomed cell (fresh snow clears a cell's
        /// groom count, so a buried groom no longer counts), 255 when none is left. Hourly and after a layout; O(cells).</summary>
        void GroomSince()
        {
            var L = _trailLayout;
            if (L == null || _fine == null || _trailStart == null) return;
            int n = L.Trails;
            if (_gSinceH.Length != n) _gSinceH = new byte[n];
            var fine = _fine;
            for (int t = 0; t < n && t + 1 < _trailStart.Length; t++)
            {
                int best = int.MaxValue;
                for (int i = _trailStart[t]; i < _trailStart[t + 1]; i++)
                {
                    int f = _trailOrder[i];
                    if ((fine.Flags[f] & FineSnow.GroomMask) == 0 || fine.Swe[f] == 0) continue;
                    if (fine.Age[f] < best) best = fine.Age[f];
                }
                // No groomed cell left (never groomed, or snowed over): none. Else the groom's own hour when this session saw
                // it (a cell's age also drops with a trace of snow), else, after a reopen, the youngest groomed cell's age.
                if (best == int.MaxValue) { _gSinceH[t] = 255; continue; }
                long h = t < _gGroomedHour.Length && _gGroomedHour[t] > 0 ? HourIndex + 1 - _gGroomedHour[t] : best;
                _gSinceH[t] = (byte)Math.Max(0, Math.Min(254, h));
            }
        }

        static short[] ResizeLane(short[] old, int n)
        {
            var a = new short[n];
            for (int i = 0; i < n; i++) a[i] = i < old.Length ? old[i] : (short)-1;
            return a;
        }

        void SizeGroomTasks(int n)
        {
            if (_gGroomedHour.Length != n) Array.Resize(ref _gGroomedHour, n);
            if (_gLastGroomer.Length != n) { _gLastGroomer = ResizeLane(_gLastGroomer, n); _gLastStart = ResizeLane(_gLastStart, n); _gLastEnd = ResizeLane(_gLastEnd, n); }
            if (_kTrail.Length >= n && _gOrder.Length == n) return;
            Array.Resize(ref _kTrail, n); Array.Resize(ref _kDone, n); Array.Resize(ref _kNeed, n); Array.Resize(ref _kStart, n); Array.Resize(ref _kEnd, n);
            Array.Resize(ref _kJob, n); Array.Resize(ref _kState, n); Array.Resize(ref _kGroomer, n); Array.Resize(ref _kFlags, n); Array.Resize(ref _kPushYd3, n);
            Array.Resize(ref _kWay, n); Array.Resize(ref _kLaid, n);
            int pieces = n * PiecesPerTask * 2 + GroomFleet.MaxGroomers * 3;   // each work piece may follow a transit; one drive home a lane
            Array.Resize(ref _pTask, pieces); Array.Resize(ref _pFrom, pieces); Array.Resize(ref _pTo, pieces); Array.Resize(ref _pPpm, pieces);
            Array.Resize(ref _pLane, pieces);
            if (_gOrder.Length != n)
            {
                Array.Resize(ref _gOrder, n); Array.Resize(ref _gPowderNights, n); Array.Resize(ref _gWarned, n);
                Array.Resize(ref _gNewUm, n); Array.Resize(ref _gSwePrev, n); Array.Resize(ref _gStormUm, n);
                Array.Resize(ref _gLast, n); Array.Resize(ref _gCarryPpm, n); Array.Resize(ref _gTonight, n);
            }
        }

        /// <summary>Each layout trail's setting: the payload's by id, else 4.07's (Autopilot on a run, Never otherwise).</summary>
        void MapGroomSettings()
        {
            var L = _trailLayout;
            if (L == null || _gSetting.Length != L.Trails) return;
            for (int t = 0; t < L.Trails; t++)
            {
                int i = Array.BinarySearch(_gPlan.TrailIds, L.TrailId[t]);
                _gSetting[t] = i >= 0 ? _gPlan.Settings[i] : L.TrailGroomed[t] ? GroomSetting.Autopilot : GroomSetting.Never;
                _gPrio[t] = i >= 0 ? _gPlan.Priorities[i] : GroomPriority.Normal;
            }
        }

        int GroomIndexOf(ulong id)
        {
            for (int t = 0; t < _gIds.Length; t++) if (_gIds[t] == id) return t;
            return -1;
        }

        /// <summary>Each trail's centi-acres by slope band, its width (area over length) and mean grade, from the fine cells.</summary>
        void GroomStatics()
        {
            var L = _trailLayout;
            if (L == null || _fine == null || ReferenceEquals(_gLayout, L)) return;
            _gLayout = L;
            int n = L.Trails, bc = _fine.BlockCells;
            _gAcres = new int[n * 4]; _gWidthMm = new int[n]; _gGrade = new int[n];
            long cellMm2 = (long)L.FineMetres * L.FineMetres * 1_000_000L;
            for (int t = 0; t < n; t++)
            {
                int a = _trailStart[t], z = _trailStart[t + 1];
                long gradeSum = 0;
                var mm2 = new long[4];
                int maxAlong = 0;
                for (int i = a; i < z; i++)
                {
                    int f = _trailOrder[i];
                    int g = GradeAt(L.BlockCell[f / bc]);
                    gradeSum += g;
                    mm2[GradeBand(g)] += cellMm2;
                    if (L.AlongMm[f] > maxAlong) maxAlong = L.AlongMm[f];
                }
                // round 3 (owner 2026-11-21: blues shouldn't need a winch): a steep patch under the winch share of the run is
                // a slow stretch (half speed) any groomer works, not a pitch only a W can climb
                long all = mm2[0] + mm2[1] + mm2[2] + mm2[3];
                if (all > 0 && (mm2[2] + mm2[3]) * 1000 < all * Pacing.GroomWinchSharePermille) { mm2[1] += mm2[2] + mm2[3]; mm2[2] = 0; mm2[3] = 0; }
                for (int b = 0; b < 4; b++) _gAcres[t * 4 + b] = (int)(mm2[b] * 100 / 4_046_856_422L);
                long area = (long)(z - a) * cellMm2;
                _gWidthMm[t] = maxAlong > 0 ? (int)Math.Min(int.MaxValue, area / maxAlong) : 0;
                _gGrade[t] = z > a ? (int)(gradeSum / (z - a)) : 0;
            }
        }

        int GradeBand(int grade) =>
            grade <= Pacing.GroomFullSpeedGrade ? 0 : grade <= Pacing.GroomWinchGrade ? 1 : grade <= Pacing.GroomWinchMaxGrade ? 2 : 3;

        // ---------- the hour ----------

        /// <summary>Replaces 4.07's <c>Groom(hour)</c>: plans at each shift's start, works the pieces, reports at the night's end.</summary>
        void GroomHour(long hour)
        {
            if (_trailLayout == null || _gSetting.Length != _trailLayout.Trails) GroomLayoutChanged();
            if (_gSetting.Length == 0) return;
            GroomStatics();
            if (_gEver) GroomTally();
            long nowL = hour * 60, prevL = _gLastAbs == long.MinValue || _gLastAbs > nowL ? nowL - 60 : _gLastAbs;
            if (nowL - prevL > 36 * 60) prevL = nowL - 60;   // a long jump: only the last hour is worked
            int now = (int)Math.Min(int.MaxValue, nowL), prev = (int)Math.Min(int.MaxValue, prevL);
            _gLastAbs = nowL;
            // The shifts' boundaries in the hour, in time order; the work up to each is done before it.
            int nb = 0;
            for (int day = prev / 1440 - 1; day <= now / 1440; day++)
            {
                NightTimes(day, out int swingStart, out int nightStart, out int nightEnd);
                AddBoundary(ref nb, prev, now, day * 1440 + Pacing.GroomDayStartHour * 60, 0, day);
                AddBoundary(ref nb, prev, now, swingStart, 1, day);
                AddBoundary(ref nb, prev, now, nightStart, 2, day);
                AddBoundary(ref nb, prev, now, nightEnd, 3, day);
                AddBoundary(ref nb, prev, now, (day + 1) * 1440 + Design.Hours.OpenSecond / 60, 4, day);   // GO-09 v2: the report at opening
            }
            for (int a = 1; a < nb; a++)   // insertion sort by (minute, rank): a night's end before a day's start at the same minute
            {
                int at = _gBAt[a], kind = _gBKind[a], d = _gBDay[a], b = a - 1;
                while (b >= 0 && (_gBAt[b] > at || (_gBAt[b] == at && Rank(_gBKind[b]) > Rank(kind)))) { _gBAt[b + 1] = _gBAt[b]; _gBKind[b + 1] = _gBKind[b]; _gBDay[b + 1] = _gBDay[b]; b--; }
                _gBAt[b + 1] = at; _gBKind[b + 1] = kind; _gBDay[b + 1] = d;
            }
            int cursor = prev;
            for (int i = 0; i < nb; i++)
            {
                int at = _gBAt[i], day = _gBDay[i];
                Work(cursor, at);
                cursor = at;
                NightTimes(day, out int swingStart, out int nightStart, out int nightEnd);
                switch (_gBKind[i])
                {
                    case 0: PlanDay(day, at, day * 1440 + Pacing.GroomDayEndHour * 60); break;
                    case 1: PlanNight(day, swingStart, nightStart, nightEnd); break;
                    case 2: if (_gNight == day) ReplanNight(nightStart); break;
                    case 3: if (_gNight == day) Morning(day); break;
                    case 4: if (_gMorningDone == day && _gLastNight != day) Report(day); break;
                }
            }
            Work(cursor, now);
            _gPreviewDirty = true;   // the snow moved on: tonight's preview again at the next snapshot
        }

        readonly int[] _gBAt = new int[16], _gBKind = new int[16], _gBDay = new int[16];

        static int Rank(int kind) => kind == 3 ? -2 : kind == 4 ? -1 : kind;

        void AddBoundary(ref int nb, int prev, int now, int at, int kind, int day)
        {
            if (at <= prev || at > now || nb >= _gBAt.Length) return;
            _gBAt[nb] = at; _gBKind[nb] = kind; _gBDay[nb] = day; nb++;
        }

        /// <summary>GO-03: the night after face day <paramref name="day"/>'s stop (absolute face minutes).</summary>
        void NightTimes(int day, out int swingStart, out int nightStart, out int nightEnd)
        {
            int stopMin = (_todayStop > 0 ? _todayStop : Design.Hours.StopSecond) / 60;
            swingStart = day * 1440 + stopMin + Pacing.GroomSweepMinutes;
            nightEnd = (day + 1) * 1440 + Design.Hours.OpenSecond / 60 - Pacing.GroomNightEndsBeforeOpenMinutes;
            if (nightEnd < swingStart) nightEnd = swingStart;
            // GO-03 v2 (owner 2026-11-21): two equal shifts, 8 h each at the default hours (4:30 PM to 8:30 AM)
            nightStart = swingStart + (nightEnd - swingStart) / 2;
        }

        // ---------- planning ----------

        void PlanNight(int day, int swingStart, int nightStart, int nightEnd)
        {
            _gNight = day;
            _gShiftStart[0] = swingStart; _gShiftEnd[0] = nightStart;
            _gShiftStart[1] = nightStart; _gShiftEnd[1] = nightEnd;
            _gShiftStart[2] = 0; _gShiftEnd[2] = 0;
            // GO-07: "Unless it's snowing tonight" is decided once, at the swing start.
            var wx = WeatherNow;
            _gSnowing = wx.StormOrdinal >= 0 || (wx.Liquid > 0 && wx.Temp <= 340);
            Array.Clear(_gWarned, 0, _gWarned.Length);
            Array.Clear(_gTonight, 0, _gTonight.Length);
            Array.Clear(_blockWorked, 0, _blockWorked.Length);
            Array.Clear(_gLaneWorkMin, 0, _gLaneWorkMin.Length);
            Array.Clear(_gLaneTransitMin, 0, _gLaneTransitMin.Length);
            _gWayPasses = 0;
            ClearNightWorked();
            BuildTasks(dayShift: false);
            DecideCrews(dayShift: false);
            Schedule(swingStart, dayShift: false);
        }

        void ReplanNight(int nightStart)
        {
            BuildTasks(dayShift: false);
            // GO-05: the night's crew is decided again for what the night now holds (snow since the swing, what the swing
            // left); the swing's crew has worked and stays as it was.
            int swing = _gCrews[0];
            DecideCrews(dayShift: false);
            _gCrews[0] = swing;
            Schedule(nightStart, dayShift: false);
        }

        /// <summary>Once an hour: each trail's snow gained since its last groom (SN-22 v4's 2 in and 4 in).</summary>
        void GroomTally()
        {
            var fine = _fine;
            if (fine == null || _gNewUm.Length != _gSetting.Length) return;
            for (int t = 0; t < _gNewUm.Length; t++)
            {
                int a = _trailStart[t], z = _trailStart[t + 1];
                if (z <= a) continue;
                long swe = 0;
                for (int i = a; i < z; i++) swe += fine.Swe[_trailOrder[i]];
                long mean = swe / (z - a);
                long gain = mean - _gSwePrev[t];
                if (gain > 0)
                {
                    _gNewUm[t] = (int)Math.Min(int.MaxValue, _gNewUm[t] + gain);
                    _gStormUm[t] = (int)Math.Min(int.MaxValue, _gStormUm[t] + gain);
                }
                _gSwePrev[t] = mean;
            }
        }

        void ReplanNow(int now)
        {
            if (_trailLayout == null || _fine == null || _gSetting.Length != _trailLayout.Trails) return;
            if (_gNight >= 0 && now < _gShiftEnd[1]) { BuildTasks(false); DecideCrews(false); Schedule(now, false); }
        }

        void PlanDay(int day, int dayStart, int dayEnd)
        {
            if (_gNight >= 0 && _gShiftEnd[1] > dayStart) return;   // the night hasn't ended (hours set very late)
            _gDayPlanned = day;
            _gShiftStart[0] = 0; _gShiftEnd[0] = 0; _gShiftStart[1] = 0; _gShiftEnd[1] = 0;
            _gShiftStart[2] = dayStart; _gShiftEnd[2] = dayEnd;
            Array.Clear(_gTonight, 0, _gTonight.Length);
            Array.Clear(_gLaneWorkMin, 0, _gLaneWorkMin.Length);
            Array.Clear(_gLaneTransitMin, 0, _gLaneTransitMin.Length);
            _gWayPasses = 0;
            BuildTasks(dayShift: true);
            DecideCrews(dayShift: true);
            Schedule(dayStart, dayShift: true);
        }

        /// <summary>SN-22 v4, SM-05, GO-07: tonight's job for every trail, its row state and warnings, ordered (GO-08).</summary>
        void BuildTasks(bool dayShift)
        {
            var L = _trailLayout;
            int n = L.Trails;
            bool anyWinch = false;
            for (int g = 0; g < _gCount; g++) anyWinch |= IsWinch(g);
            _kCount = 0; _pCount = 0;
            for (int t = 0; t < n; t++)
            {
                _kStateFor(t, GroomRowState.None);
                if (_gTonight[t] != 0) { _kStateFor(t, GroomRowState.Done); continue; }
                var setting = _gSetting[t];
                var order = (GroomOrder)_gOrder[t];
                TrailSnapshot(t, out long passes, out int newUm, out bool icy, out long pileMm3, out bool uncured);
                var label = TrailLabelOf(t);
                int k = NetIndexOf(t);
                bool open = k >= 0 && k < _trailOpenNet.Length && _trailOpenNet[k] != 0 && (k >= _trailRunOpen.Length || _trailRunOpen[k] != 0);
                var job = GroomJob.None;
                var state = GroomRowState.None;
                bool pile = pileMm3 > 0;
                bool high = _gPrio[t] == GroomPriority.High;
                if (order == GroomOrder.Skip) state = GroomRowState.Skipped;
                else if (setting == GroomSetting.Never && order == GroomOrder.None)
                {
                    state = GroomRowState.Never;
                    if (pile && !open) Warn(t, GroomWarningKind.WhalesBlockOpening);
                }
                else if (order == GroomOrder.Push) job = GroomJob.Push;
                else if (order == GroomOrder.Till) job = GroomJob.Till;
                else if (setting == GroomSetting.WhenOrdered)
                {
                    if (pile && !open) Warn(t, GroomWarningKind.WhalesBlockOpening);
                }
                else if (setting == GroomSetting.UnlessSnowing && _gSnowing) state = GroomRowState.None;
                else
                {
                    bool scheduled = setting == GroomSetting.EveryNight || setting == GroomSetting.UnlessSnowing;
                    // SM-05: a whale waits for its cure unless the trail is High; a push that would open a closed trail goes.
                    if (pile && (!uncured || high)) job = GroomJob.Push;
                    else if (pile && !open && !uncured) job = GroomJob.Push;
                    else if (scheduled) job = GroomJob.Till;
                    // GO-16 v2 (owner 2026-11-21): a job left part done is finished by a later shift
                    else if (passes > 0 || newUm >= Pacing.GroomNewSnowUm || icy || _gCarryPpm[t] != 0) job = GroomJob.Till;
                    // SN-22 v4: Autopilot keeps the powder on a steep trail.
                    if (job == GroomJob.Till && !scheduled && _gStormUm[t] >= Pacing.GroomPowderKeepUm && _gGrade[t] >= Pacing.GroomPowderGrade && !dayShift)
                    {
                        job = GroomJob.None; state = GroomRowState.PowderHeld;
                        if (_gPowderNights[t] + 1 >= Pacing.GroomPowderWarnNights) Warn(t, GroomWarningKind.PowderHeld);
                    }
                    if (job == GroomJob.Till && label == TrailSnowLabel.Bare)
                    {
                        job = GroomJob.None; state = GroomRowState.TooBare;
                        if (scheduled) Warn(t, GroomWarningKind.TooBare);
                    }
                }
                // GO-03: the day shift works closed trails only, and only what the night left: an unfinished task, an order or a
                // push (a trail's nightly till is the night's).
                if (dayShift && job == GroomJob.Till && order == GroomOrder.None && _gCarryPpm[t] == 0) job = GroomJob.None;
                if (dayShift && (open || job == GroomJob.None)) { job = GroomJob.None; if (state != GroomRowState.Never) state = GroomRowState.None; }
                if (job == GroomJob.None) { if (!dayShift) _kStateFor(t, state); continue; }
                if (_gCount == 0) { _kStateFor(t, GroomRowState.NoGroomer); continue; }
                byte flags = 0;
                // GO-04: past the winch grade only a W works; past the winch's own limit no groomer does (that pitch is skipped)
                bool steep = _gAcres[t * 4 + 2] > 0, beyond = _gAcres[t * 4 + 3] > 0;
                if (steep && anyWinch) flags |= KWinch;
                if (steep && !anyWinch) { flags |= KSkipPitch; Warn(t, GroomWarningKind.PitchSkippedNoWinch); }
                if (beyond) flags |= KSkipPitch;
                if (_gSnowing && newUm > 0 && job == GroomJob.Till && !dayShift) flags |= KLate;
                if (job == GroomJob.Push && uncured) flags |= KGreen;
                if (dayShift) flags |= KDay;
                if (_gCarryPpm[t] < 0) flags |= KFromTail;   // a part-done trail goes on the way it started
                int i = _kCount++;
                _kTrail[i] = t; _kJob[i] = (byte)job; _kState[i] = (byte)GroomRowState.Planned; _kFlags[i] = flags;
                _kDone[i] = Math.Abs(_gCarryPpm[t]); _kNeed[i] = 0; _kGroomer[i] = 0; _kStart[i] = -1; _kEnd[i] = -1;
                _kPushYd3[i] = job == GroomJob.Push ? (int)Math.Min(int.MaxValue, pileMm3 / 1000 * 1308 / 1000) : 0;   // litres → m³ → yd³
                // GO-08's order: High; pushes that open; open by passes since the groom; closed; Low; then the trail id.
                int cls = high ? 0 : _gPrio[t] == GroomPriority.Low ? 4 : job == GroomJob.Push && !open ? 1 : open ? 2 : 3;
                _gKey[i] = ((long)cls << 40) - Math.Min(passes, (1L << 39) - 1);
                _gSort[i] = i;
            }
            // insertion sort of the tasks by (key, trail id): stable bytes, no allocation
            for (int a = 1; a < _kCount; a++)
            {
                int x = _gSort[a];
                int b = a - 1;
                while (b >= 0 && Before(x, _gSort[b])) { _gSort[b + 1] = _gSort[b]; b--; }
                _gSort[b + 1] = x;
            }
        }

        bool Before(int x, int y) => _gKey[x] != _gKey[y] ? _gKey[x] < _gKey[y] : _gIds[_kTrail[x]] < _gIds[_kTrail[y]];

        // A trail with no task tonight keeps a row state in _gLast's place until the morning: held in _gWarned's high bits.
        void _kStateFor(int t, GroomRowState s) { _gWarned[t] = (byte)((_gWarned[t] & 0x0F) | ((int)s << 4)); }

        void Warn(int t, GroomWarningKind kind)
        {
            if (_gCount == 0) return;   // the tab says "No groomers" instead
            int bit = 1 << ((int)kind - 1);
            if ((_gWarned[t] & bit) != 0) return;
            _gWarned[t] = (byte)(_gWarned[t] | bit);
            if (_gPreviewing) return;   // GO-14 v3: a preview's warning shows on its row but raises no event
            int k = NetIndexOf(t);
            Events.Add(Tick, SimEventType.GroomWarning, k, (long)kind);
            int slot = _gWarnCount % GroomView.MaxWarnings;
            _gWarnKind[slot] = (byte)kind; _gWarnTrail[slot] = k;
            _gWarnCount++;
        }

        /// <summary>A trail's skier passes since its groom, new snow (µm water, mean of its squares), whether it's icy, its
        /// piles' volume over <see cref="Pacing.WhaleRiseMm"/> (mm × m², i.e. litres) and whether any pile is still curing.</summary>
        void TrailSnapshot(int t, out long passes, out int newUm, out bool icy, out long pileMm3, out bool uncured)
        {
            var fine = _fine;
            var L = _trailLayout;
            int a = _trailStart[t], z = _trailStart[t + 1];
            passes = 0; long depth = 0; int ice = 0, cells = z - a;
            for (int i = a; i < z; i++)
            {
                int f = _trailOrder[i];
                passes += fine.Passes[f];
                depth += fine.DepthUm(f);
                var type = (SnowType)fine.Type[f];
                if (type == SnowType.IS || type == SnowType.FG) ice++;
            }
            newUm = t < _gNewUm.Length ? _gNewUm[t] : 0;
            icy = cells > 0 && ice * 10 >= cells;
            pileMm3 = 0; uncured = false;
            if (cells == 0) return;
            long mean = depth / cells, rise = Pacing.WhaleRiseMm * 1000L;
            long cellM2 = (long)L.FineMetres * L.FineMetres;
            for (int i = a; i < z; i++)
            {
                int f = _trailOrder[i];
                long over = fine.DepthUm(f) - mean - rise;
                if (over <= 0) continue;
                pileMm3 += over / 1000 * cellM2;
                if (fine.Age[f] < Pacing.GroomCureHours) uncured = true;
            }
        }

        bool IsWinch(int g) => _gModel[g] == (byte)GroomerModel.S400W || _gModel[g] == (byte)GroomerModel.S800W;

        /// <summary>The minutes groomer <paramref name="g"/> needs for task <paramref name="k"/> in full (GO-04, GO-07).</summary>
        int NeedOn(int k, int g)
        {
            int t = _kTrail[k], m = _gModel[g] - 1;
            if (m < 0) return int.MaxValue / 4;
            long till = Pacing.GroomTillCentiAcres[m];
            if (_gTiller[g] && _gWidthMm[t] >= Pacing.GroomTillerMinWidthMm) till = till * Pacing.GroomTillerPermille / 1000;
            // half speed past the full-speed grade, and past the winch grade only on a W (else that pitch is skipped)
            long acres = _gAcres[t * 4] + 2L * _gAcres[t * 4 + 1] + (IsWinch(g) ? 2L * _gAcres[t * 4 + 2] : 0);
            long minutes = Pacing.GroomTaskMinutes + acres * 60 / Math.Max(1, till);
            if (_kJob[k] == (byte)GroomJob.Push) minutes += (long)_kPushYd3[k] * 60 / Math.Max(1, Pacing.GroomPushYd3[m]);
            return (int)Math.Min(24 * 60, Math.Max(1, minutes));
        }

        /// <summary>GO-05: Autopilot crews just enough, night before swing (day only for the day's tasks); pins win.</summary>
        void DecideCrews(bool dayShift)
        {
            long need = 0;
            for (int i = 0; i < _kCount; i++) need += ((long)NeedOn(i, 0) * (1_000_000 - _kDone[i]) + 999_999) / 1_000_000;   // up: a task nearly done still needs a crew
            if (dayShift)
            {
                int cap = Math.Max(1, _gShiftEnd[2] - _gShiftStart[2] - 2 * Pacing.GroomTravelMinutes);
                _gCrews[2] = _gPlan.DayPin >= 0 ? Math.Min(_gPlan.DayPin, _gCount) : (int)Math.Min(_gCount, (need + cap - 1) / cap);
                return;
            }
            int nightCap = Math.Max(0, _gShiftEnd[1] - _gShiftStart[1] - 2 * Pacing.GroomTravelMinutes);
            int swingCap = Math.Max(0, _gShiftEnd[0] - _gShiftStart[0] - 2 * Pacing.GroomTravelMinutes);
            int night = _gPlan.NightPin >= 0 ? Math.Min(_gPlan.NightPin, _gCount)
                      : nightCap == 0 ? 0 : (int)Math.Min(_gCount, (need + nightCap - 1) / nightCap);
            long rest = Math.Max(0, need - (long)night * nightCap);
            int swing = _gPlan.SwingPin >= 0 ? Math.Min(_gPlan.SwingPin, _gCount)
                      : swingCap == 0 ? 0 : (int)Math.Min(_gCount, (rest + swingCap - 1) / swingCap);
            _gCrews[0] = swing; _gCrews[1] = night; _gCrews[2] = 0;
        }

        /// <summary>GO-08: list scheduling from <paramref name="from"/> over the crewed lanes (lane = groomer × shift).</summary>
        /// <summary>GO-05 v2 (owner 2026-11-21): Auto crews the groomers in this order: a W first when a task has a pitch only a
        /// W works, then the 800s (faster), then fleet order. A shift's crews are its first that many.</summary>
        void RankGroomers()
        {
            bool winch = false;
            for (int k = 0; k < _kCount && !winch; k++) winch = (_kFlags[k] & KWinch) != 0;
            for (int g = 0; g < GroomFleet.MaxGroomers; g++)
            {
                if (g >= _gCount) { _gRank[g] = g; continue; }
                int key = RankKey(g, winch), r = 0;
                for (int h = 0; h < _gCount; h++) { int kh = RankKey(h, winch); if (kh < key || (kh == key && h < g)) r++; }
                _gRank[g] = r;
            }
        }

        int RankKey(int g, bool winch) =>
            (winch && IsWinch(g) ? 0 : 2) + (_gModel[g] == (byte)GroomerModel.S800 || _gModel[g] == (byte)GroomerModel.S800W ? 0 : 1);

        /// <summary>Whether groomer <paramref name="g"/> has a crew on shift <paramref name="s"/> (GO-05 v2's order).</summary>
        bool CrewedOn(int g, int s) => g < _gCount && _gRank[g] < _gCrews[s] && _gShiftEnd[s] > _gShiftStart[s];

        void Schedule(int from, bool dayShift)
        {
            GroomRoutes();
            RankGroomers();
            // GO-15: where each lane is at the replan: the end of its last piece laid before it, else the depot
            for (int l = 0; l < GroomFleet.MaxGroomers * 3; l++) { _lanePos[l] = _gDepot; _laneOut[l] = false; _laneLate[l] = int.MinValue; }
            for (int p = 0; p < _pCount; p++)
            {
                int l = _pLane[p];
                if (_pFrom[p] >= from || _pFrom[p] < _laneLate[l]) continue;
                _laneLate[l] = _pFrom[p];
                _lanePos[l] = PieceEndNode(p);
                _laneOut[l] = _lanePos[l] != _gDepot || _pTask[p] != TransitTask;
            }
            // the lanes' windows: the whole shift (transit is laid as its own pieces)
            for (int g = 0; g < GroomFleet.MaxGroomers; g++)
                for (int s = 0; s < 3; s++)
                {
                    int l = g * 3 + s;
                    bool on = CrewedOn(g, s) && (dayShift ? s == 2 : s < 2);
                    int begin = _gShiftStart[s], end = _gShiftEnd[s];
                    if (on && _gDrive[g] == (byte)GroomerDrive.Electric) end = Math.Min(end, begin + Pacing.GroomElectricMinutes);
                    bool started = _laneLate[l] != int.MinValue;
                    if (begin < from) begin = from;
                    if (!started) { _laneOut[l] = false; _lanePos[l] = _gDepot; }
                    _laneOn[l] = on && end > begin;
                    _laneBegin[l] = begin; _laneEnd[l] = end;
                    _laneFree[l] = begin;
                }
            _pCount = 0;
            _gWant = 0; _gShort = 0;
            for (int k = 0; k < _kCount; k++) { _kWay[k] = 0; _kLaid[k] = false; }
            // GO-08 v2 (owner 2026-11-21: cover as much ground as possible): every task forward from the shift's start, no
            // gaps; a trail with new snow tonight (B2a-3) goes after the rest, on the lane that reaches it soonest (round 3,
            // owner 2026-11-21: the swing takes it rather than sit idle).
            for (int pass = 0; pass < 2; pass++)
                for (int o = 0; o < _kCount; o++)
                {
                    int k = _gSort[o];
                    bool late = (_kFlags[k] & KLate) != 0;
                    if ((pass == 1) != late) continue;
                    PlaceTask(k, late);
                }
            // GO-15: each lane that went out drives back to the depot before its shift ends
            for (int l = 0; l < GroomFleet.MaxGroomers * 3; l++)
            {
                if (!_laneOn[l] || !_laneOut[l] || _lanePos[l] == _gDepot && _gDepot >= 0) continue;
                int back = Math.Max(0, TransitMinutes(_lanePos[l], _gDepot));
                int at = Math.Min(_laneFree[l], Math.Max(_laneBegin[l], _laneEnd[l] - back));
                AddTransit(l, at, at + back, _lanePos[l], _gDepot);
                _lanePos[l] = _gDepot;
            }
            _gCrewMin = 0;
            for (int l = 0; l < GroomFleet.MaxGroomers * 3; l++) if (_laneOn[l]) _gCrewMin += _laneEnd[l] - _laneBegin[l];
            _gCost = CostOf(dayShift);
        }

        void PlaceTask(int k, bool late)
        {
            int t = _kTrail[k];
            bool needWinch = (_kFlags[k] & KWinch) != 0;
            int remaining = 1_000_000 - _kDone[k];
            bool any = false, routed = false;
            bool dirSet = _kDone[k] > 0;   // a part-done trail goes on from the end it started at
            int wantOn = -1;
            for (int guard = 0; guard < PiecesPerTask && remaining > 0 && _pCount + 2 < _pTask.Length; guard++)
            {
                // the lane that reaches the trail soonest (a night lane first for a late task), lowest groomer and shift on a tie
                int best = -1, bestAt = 0, bestTr = 0, bestRoom = 0;
                bool bestTail = false, bestPre = false;
                for (int pick = 0; pick < 2 && best < 0; pick++)
                {
                    if (pick == 1 && !late) break;
                    for (int l = 0; l < GroomFleet.MaxGroomers * 3; l++)
                    {
                        if (!_laneOn[l]) continue;
                        int g = l / 3;
                        if (needWinch && !IsWinch(g) && AnyWinchCrewed()) continue;
                        // round 3 (owner 2026-11-21): a late task no longer waits for a night lane while the swing has room
                        int pos = _lanePos[l];
                        int trHead = TransitMinutes(pos, _gHead[t]), trTail = TransitMinutes(pos, _gTail[t]);
                        bool tail;
                        if (dirSet) tail = (_kFlags[k] & KFromTail) != 0;
                        else tail = trTail >= 0 && (trHead < 0 || trTail < trHead);   // GO-16: the nearer end (the head on a tie)
                        int tr = tail ? trTail : trHead;
                        if (tr < 0) continue;   // no way there over the trails
                        routed = true;
                        // GO-15: a swing lane not yet out leaves the depot before the shift starts (during the open hours)
                        bool pre = l % 3 == 0 && !_laneOut[l] && _laneFree[l] == _gShiftStart[0];
                        int at = pre ? _laneFree[l] : _laneFree[l] + tr;
                        int back = Math.Max(0, TransitMinutes(tail ? _gHead[t] : _gTail[t], _gDepot));
                        int need = NeedOn(k, g);
                        int len = (int)Math.Max(1, ((long)need * Math.Max(0, remaining - _kWay[k]) + 999_999) / 1_000_000);
                        int room = _laneEnd[l] - back - at;
                        if (room < Math.Min(len, Pacing.GroomTaskMinutes + 5)) continue;   // no room for a useful piece
                        if (best < 0 || at < bestAt) { best = l; bestAt = at; bestTr = tr; bestRoom = room; bestTail = tail; bestPre = pre; }
                    }
                }
                if (best < 0) break;
                int gg = best / 3;
                int startNode = bestTail ? _gTail[t] : _gHead[t], farNode = bestTail ? _gHead[t] : _gTail[t];
                // GO-16 v2 (owner 2026-11-21): a drive (not the one before the shift) that runs along runs due tonight
                // grooms a pass of each on the way, at the tilling speed, when the longer drive still leaves a useful piece
                int way = 0;
                if (!bestPre && bestTr > 0)
                {
                    way = WayWalk(gg, _lanePos[best], startNode, apply: false);
                    if (way > 0 && bestRoom - way >= Pacing.GroomTaskMinutes + 5) WayWalk(gg, _lanePos[best], startNode, apply: true);
                    else way = 0;
                }
                bestAt += way; bestRoom -= way;
                int needG = NeedOn(k, gg);
                int eff = Math.Max(0, remaining - _kWay[k]);
                if (wantOn < 0) { wantOn = gg; _gWant += (int)((long)needG * eff / 1_000_000); }
                if (!dirSet)
                {
                    dirSet = true;
                    if (bestTail) _kFlags[k] = (byte)(_kFlags[k] | KFromTail); else _kFlags[k] = (byte)(_kFlags[k] & ~KFromTail);
                }
                if (bestTr > 0) AddTransit(best, bestPre ? bestAt - bestTr : _laneFree[best], bestAt, _lanePos[best], startNode, way > 0);
                int lenG = (int)Math.Max(1, ((long)needG * eff + 999_999) / 1_000_000);
                int take = Math.Min(lenG, bestRoom);
                int ppm = take >= lenG ? remaining : _kWay[k] > 0 ? (int)((long)remaining * take / lenG) : (int)((long)take * 1_000_000 / needG);
                _kWay[k] = take >= lenG ? 0 : (int)((long)_kWay[k] * (lenG - take) / lenG);
                _kLaid[k] = true;
                int p = _pCount++;
                _pTask[p] = k; _pLane[p] = (byte)best; _pPpm[p] = ppm;
                _pFrom[p] = bestAt; _pTo[p] = bestAt + take;
                _laneFree[best] = bestAt + take;
                _lanePos[best] = take >= lenG ? farNode : startNode;
                _laneOut[best] = true;
                if (!any) { _kGroomer[k] = (byte)_gNumber[gg]; _kStart[k] = _pFrom[p]; _kNeed[k] = needG; }
                _kStart[k] = Math.Min(_kStart[k], _pFrom[p]);
                _kEnd[k] = Math.Max(_kEnd[k], _pTo[p]);
                any = true;
                remaining -= ppm;
            }
            if (remaining > 0)
            {
                // out of hours: what's left is short (at the first lane's speed, else the first groomer's); a run no groomer
                // can drive to isn't short of hours (more groomers wouldn't reach it) and counts in neither
                int need = NeedOn(k, wantOn >= 0 ? wantOn : 0);
                var st = any ? GroomRowState.Partial : !routed && AnyLaneOn() ? GroomRowState.NoRoute : GroomRowState.NotReached;
                if (st != GroomRowState.NoRoute)
                {
                    if (wantOn < 0) _gWant += (int)((long)need * remaining / 1_000_000);
                    _gShort += (int)((long)need * remaining / 1_000_000);
                }
                _kState[k] = (byte)st;
                if (!any) _kNeed[k] = need;
            }
        }

        bool AnyLaneOn()
        {
            for (int l = 0; l < GroomFleet.MaxGroomers * 3; l++) if (_laneOn[l]) return true;
            return false;
        }

        void AddTransit(int lane, int from, int to, int fromNode, int toNode, bool way = false)
        {
            if (to <= from || _pCount >= _pTask.Length) return;
            int p = _pCount++;
            _pTask[p] = TransitTask; _pLane[p] = (byte)lane; _pFrom[p] = from; _pTo[p] = to;
            // 15 bits a node (+1, 0 none) and bit 30: it grooms on the way (GO-16 v2)
            _pPpm[p] = ((fromNode + 1) & NodeMask) | (((toNode + 1) & NodeMask) << 15) | (way ? WayBit : 0);
        }

        const int NodeMask = 0x7FFF, WayBit = 1 << 30;
        int TransitFromNode(int p) => (_pPpm[p] & NodeMask) - 1;
        int TransitToNode(int p) => ((_pPpm[p] >> 15) & NodeMask) - 1;

        /// <summary>Layout trail <paramref name="t"/>'s task tonight, or -1.</summary>
        int TaskOf(int t)
        {
            for (int k = 0; k < _kCount; k++) if (_kTrail[k] == t) return k;
            return -1;
        }

        /// <summary>
        /// GO-16 v2: the drive from node <paramref name="a"/> to <paramref name="b"/> for groomer <paramref name="g"/>, along
        /// the shortest path: each trail segment of a run with a tilling job tonight not yet laid is driven at the tilling speed
        /// (one <see cref="Pacing.GroomPassMm"/> pass). Returns the extra minutes over the plain drive; with
        /// <paramref name="apply"/> each such task's need shrinks by the pass's minutes.
        /// </summary>
        int WayWalk(int g, int a, int b, bool apply)
        {
            if (a < 0 || b < 0 || a == b || _gNet == null || _gDistRow.Length == 0) return 0;
            var row = DistanceRow(a);
            if (row[b] == int.MaxValue) return 0;
            var pred = _gPredRow[a];
            var net = _gNet;
            int m = _gModel[g] - 1;
            if (m < 0) return 0;
            long extraMilli = 0;
            int guard = net.NodeCount;
            for (int u = b; u != a && guard-- > 0;)
            {
                int e = pred[u];
                if (e < 0) break;
                int prevNode = net.EdgeTo[e] == u ? net.EdgeFrom[e] : net.EdgeTo[e];
                u = prevNode;
                if (net.EdgeKindOf[e] != (byte)SimEdgeKind.TrailSegment) continue;
                int nk = net.EdgeTrail[e];
                int t = nk >= 0 && nk < _gLayoutOfNet.Length ? _gLayoutOfNet[nk] : -1;
                if (t < 0) continue;
                int k = TaskOf(t);
                if (k < 0 || _kLaid[k] || _kJob[k] != (byte)GroomJob.Till || _kDone[k] >= 1_000_000) continue;
                if ((_kFlags[k] & KWinch) != 0 && !IsWinch(g)) continue;
                long len = Math.Max(0, net.EdgeLengthMm[e]);
                long till = Pacing.GroomTillCentiAcres[m];
                if (_gTiller[g] && _gWidthMm[t] >= Pacing.GroomTillerMinWidthMm) till = till * Pacing.GroomTillerPermille / 1000;
                // a centi-acre is 40.4686 m²: the tilling speed is the acres an hour over one pass's width
                long groomMilli = len * 60_000 * Pacing.GroomPassMm / Math.Max(1, till * 40_468_600L);
                long driveMilli = len * 60_000 / Math.Max(1, (long)Pacing.GroomTransitKmh * 1_000_000);
                extraMilli += Math.Max(0, groomMilli - driveMilli);
                if (apply)
                {
                    int need = NeedOn(k, g);
                    long credit = groomMilli * 1000 / Math.Max(1, need);
                    _kWay[k] = (int)Math.Min(Math.Max(0, 1_000_000 - _kDone[k] - 1), _kWay[k] + credit);
                }
            }
            return (int)((extraMilli + 999) / 1000);
        }

        /// <summary>GO-16 v2: transit piece <paramref name="p"/> done: the pass on the way, on each run due tonight it drove
        /// along (the next pass across its width, on that stretch).</summary>
        void GroomOnTheWay(int p)
        {
            int a = TransitFromNode(p), b = TransitToNode(p);
            if (a < 0 || b < 0 || a == b || _gNet == null || _gDistRow.Length == 0 || a >= _gNet.NodeCount || b >= _gNet.NodeCount) return;
            var row = DistanceRow(a);
            if (row[b] == int.MaxValue) return;
            var pred = _gPredRow[a];
            var net = _gNet;
            var L = _trailLayout;
            int lane = _pLane[p];
            int guard = net.NodeCount;
            for (int u = b; u != a && guard-- > 0;)
            {
                int e = pred[u];
                if (e < 0) break;
                u = net.EdgeTo[e] == u ? net.EdgeFrom[e] : net.EdgeTo[e];
                if (net.EdgeKindOf[e] != (byte)SimEdgeKind.TrailSegment) continue;
                int nk = net.EdgeTrail[e];
                int t = nk >= 0 && nk < _gLayoutOfNet.Length ? _gLayoutOfNet[nk] : -1;
                if (t < 0) continue;
                int k = TaskOf(t);
                if (k < 0 || _kJob[k] != (byte)GroomJob.Till || _kDone[k] >= 1_000_000) continue;
                var st = (GroomRowState)_kState[k];
                if (st != GroomRowState.Planned && st != GroomRowState.Partial) continue;
                // the segment's stretch along its trail (a network without them, as in tests: the trail's first segment length)
                int lo = e < net.EdgeStartMm.Length ? net.EdgeStartMm[e] : 0;
                int hi = e < net.EdgeEndMm.Length ? net.EdgeEndMm[e] : lo + Math.Max(0, net.EdgeLengthMm[e]);
                if (hi < lo) { int x = lo; lo = hi; hi = x; }
                int from = AlongIndex(t, lo), upTo = AlongIndex(t, hi + 1);
                from = StationEdge(t, from); upTo = StationEdge(t, upTo);
                if (upTo <= from) continue;
                int w = (int)Math.Min(1_000_000, (long)Pacing.GroomPassMm * 1_000_000 / Math.Max(1, _gWidthMm[t]));
                int band = Math.Min(_kDone[k], 1_000_000 - w);
                bool winch = IsWinch(lane / 3);
                var s = GroomStrength.Light(Pacing).WithMaxGrade(winch ? Pacing.GroomWinchMaxGrade : Pacing.GroomWinchGrade);
                if (!GroomTrail(t, s, from, upTo, Math.Max(0, band), band + w)) continue;
                _kFlags[k] = (byte)(_kFlags[k] | KAny);
                if (t < _gGroomedHour.Length) _gGroomedHour[t] = HourIndex + 1;
                _gWayPasses++;
            }
        }

        /// <summary>The first index in trail <paramref name="t"/>'s cells (along order) at or past <paramref name="mm"/> along.</summary>
        int AlongIndex(int t, int mm)
        {
            int lo = _trailStart[t], hi = _trailStart[t + 1];
            var along = _trailLayout.AlongMm;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (along[_trailOrder[mid]] < mm) lo = mid + 1; else hi = mid;
            }
            return lo;
        }

        /// <summary>GO-16 v2: fine cell <paramref name="f"/>'s place across trail <paramref name="t"/>, ppm from its left edge
        /// looking downhill (the order the passes go in).</summary>
        int LateralPpm(int t, int f)
        {
            long half = Math.Max(1, (t < _gWidthMm.Length ? _gWidthMm[t] : 0) / 2);
            long v = (half - _trailLayout.LateralMm[f]) * 1_000_000 / (2 * half);
            return (int)Math.Max(0, Math.Min(999_999, v));
        }

        /// <summary>Where piece <paramref name="p"/> leaves its groomer: a transit's far node, a work piece's far end when it
        /// finishes its task, else the end it started at.</summary>
        int PieceEndNode(int p)
        {
            if (_pTask[p] == TransitTask) return TransitToNode(p);
            int k = _pTask[p], t = _kTrail[k];
            if (t < 0 || t >= _gHead.Length) return _gDepot;
            bool tail = (_kFlags[k] & KFromTail) != 0;
            PieceShare(p, out _, out int to);
            return to >= 1_000_000 ? (tail ? _gHead[t] : _gTail[t]) : (tail ? _gTail[t] : _gHead[t]);
        }

        // ---------- GO-15: routes over the trails ----------

        /// <summary>The trails' ends and the depot, again when the network or the layout changed.</summary>
        void GroomRoutes()
        {
            var L = _trailLayout;
            var net = Design.Network;
            if (L == null) return;
            if (ReferenceEquals(net, _gNet) && ReferenceEquals(L, _gRoutesLayout) && _gHead.Length == L.Trails) return;
            _gNet = net; _gRoutesLayout = L;
            int n = L.Trails;
            _gHead = new int[n]; _gTail = new int[n];
            for (int t = 0; t < n; t++) { _gHead[t] = -1; _gTail[t] = -1; }
            _gDepot = -1;
            if (net == null || net.NodeCount == 0) { _gDistRow = Array.Empty<int[]>(); _gPredRow = Array.Empty<int[]>(); _gLayoutOfNet = Array.Empty<int>(); return; }
            var layoutOf = new int[net.TrailIds.Length];
            _gLayoutOfNet = layoutOf;
            for (int k = 0; k < layoutOf.Length; k++) layoutOf[k] = -1;
            for (int t = 0; t < n; t++) { int k = NetIndexOf(t); if (k >= 0 && k < layoutOf.Length) layoutOf[k] = t; }
            var headAt = new int[n]; var tailAt = new int[n];
            var starts = net.EdgeStartMm; var ends = net.EdgeEndMm;
            for (int e = 0; e < net.EdgeFrom.Length; e++)
            {
                if (net.EdgeKindOf[e] != (byte)SimEdgeKind.TrailSegment) continue;
                int k = net.EdgeTrail[e];
                int t = k >= 0 && k < layoutOf.Length ? layoutOf[k] : -1;
                if (t < 0) continue;
                int sm = e < starts.Length ? starts[e] : 0, em = e < ends.Length ? ends[e] : 0;
                if (_gHead[t] < 0 || sm < headAt[t]) { _gHead[t] = net.EdgeFrom[e]; headAt[t] = sm; }
                if (_gTail[t] < 0 || em > tailAt[t]) { _gTail[t] = net.EdgeTo[e]; tailAt[t] = em; }
            }
            // the depot: the foot of the lowest built lift a trail or path reaches (the first by id on a tie); a lift foot no
            // trail touches is an island the groomers could never leave (the drawn resort's lowest lift, 2026-11-21)
            int level = int.MaxValue;
            _gDepotLift = 0;
            foreach (var lift in Design.Lifts)
                if (lift.Built && lift.BottomNode >= 0 && lift.BottomNode < net.NodeCount && lift.BottomLevelCm < level && Driveable(net, lift.BottomNode))
                { level = lift.BottomLevelCm; _gDepot = lift.BottomNode; _gDepotLift = lift.Id; }
            if (_gDepot < 0) for (int t = 0; t < n && _gDepot < 0; t++) _gDepot = _gTail[t];   // no such lift: the first trail's foot
            _gDistRow = new int[net.NodeCount][];
            _gPredRow = new int[net.NodeCount][];
            int cap = 2 * (net.EdgeFrom.Length + net.NodeCount) + 4;
            if (_hNode.Length < cap) { _hNode = new int[cap]; _hKey = new long[cap]; }
        }

        /// <summary>Whether a trail or path starts or ends at node <paramref name="u"/>.</summary>
        static bool Driveable(SimNetwork net, int u)
        {
            for (int j = 0; j < net.EdgeCount[u]; j++) if (Drives(net.EdgeKindOf[net.FirstEdge[u] + j])) return true;
            for (int j = 0; j < net.IntoCount[u]; j++) if (Drives(net.EdgeKindOf[net.IntoEdge[net.IntoFirst[u] + j]])) return true;
            return false;
        }

        static bool Drives(byte kind) => kind == (byte)SimEdgeKind.TrailSegment || kind == (byte)SimEdgeKind.Path;

        /// <summary>
        /// GO-15: the minutes from node <paramref name="a"/> to node <paramref name="b"/> at <see cref="Pacing.GroomTransitKmh"/>
        /// over the trails and paths only (either way along them), 0 when the same, -1 when no trail leads there. With no network (a
        /// design with no geometry) a move from or to an unknown place is <see cref="Pacing.GroomTravelMinutes"/>.
        /// </summary>
        int TransitMinutes(int a, int b)
        {
            if (a == b) return 0;
            if (a < 0 || b < 0 || _gNet == null || _gDistRow.Length == 0) return Pacing.GroomTravelMinutes;
            var row = DistanceRow(a);
            int mm = row[b];
            if (mm == int.MaxValue) return -1;
            long perHour = (long)Pacing.GroomTransitKmh * 1_000_000;
            return (int)Math.Max(1, Math.Min(24 * 60, ((long)mm * 60 + perHour - 1) / perHour));
        }

        /// <summary>The trail distance (mm) from <paramref name="src"/> to every node: Dijkstra over trail segments both ways,
        /// ties by node index; kept until the network changes. Paths count: the lifts' automatic paths join the trails to the depot.</summary>
        int[] DistanceRow(int src)
        {
            var row = _gDistRow[src];
            if (row != null) return row;
            var net = _gNet;
            int n = net.NodeCount;
            row = new int[n];
            var pred = new int[n];
            for (int i = 0; i < n; i++) { row[i] = int.MaxValue; pred[i] = -1; }
            row[src] = 0;
            int size = 0;
            HeapPush(ref size, src, 0);
            while (size > 0)
            {
                int u = HeapPop(ref size, out long du);
                if (du > row[u]) continue;
                for (int pass = 0; pass < 2; pass++)
                {
                    int first = pass == 0 ? net.FirstEdge[u] : net.IntoFirst[u], count = pass == 0 ? net.EdgeCount[u] : net.IntoCount[u];
                    for (int j = 0; j < count; j++)
                    {
                        int e = pass == 0 ? first + j : net.IntoEdge[first + j];
                        if (!Drives(net.EdgeKindOf[e])) continue;   // trails and paths (cat tracks, work roads)
                        int v = pass == 0 ? net.EdgeTo[e] : net.EdgeFrom[e];
                        long dv = du + Math.Max(0, net.EdgeLengthMm[e]);
                        if (dv >= row[v] || dv >= int.MaxValue) continue;
                        row[v] = (int)dv;
                        pred[v] = e;
                        HeapPush(ref size, v, dv);
                    }
                }
            }
            _gDistRow[src] = row;
            _gPredRow[src] = pred;
            return row;
        }

        void HeapPush(ref int size, int node, long key)
        {
            if (size >= _hNode.Length) { Array.Resize(ref _hNode, size * 2 + 4); Array.Resize(ref _hKey, size * 2 + 4); }
            int i = size++;
            while (i > 0)
            {
                int up = (i - 1) / 2;
                if (_hKey[up] < key || (_hKey[up] == key && _hNode[up] <= node)) break;
                _hNode[i] = _hNode[up]; _hKey[i] = _hKey[up]; i = up;
            }
            _hNode[i] = node; _hKey[i] = key;
        }

        int HeapPop(ref int size, out long key)
        {
            int top = _hNode[0]; key = _hKey[0];
            int lastN = _hNode[--size]; long lastK = _hKey[size];
            int i = 0;
            while (size > 0)
            {
                int c = 2 * i + 1;
                if (c >= size) break;
                if (c + 1 < size && (_hKey[c + 1] < _hKey[c] || (_hKey[c + 1] == _hKey[c] && _hNode[c + 1] < _hNode[c]))) c++;
                if (lastK < _hKey[c] || (lastK == _hKey[c] && lastN <= _hNode[c])) break;
                _hNode[i] = _hNode[c]; _hKey[i] = _hKey[c]; i = c;
            }
            if (size > 0) { _hNode[i] = lastN; _hKey[i] = lastK; }
            return top;
        }

        bool AnyWinchCrewed()
        {
            for (int l = 0; l < GroomFleet.MaxGroomers * 3; l++) if (_laneOn[l] && IsWinch(l / 3)) return true;
            return false;
        }

        /// <summary>GO-06: the night's priced estimate: each crew's shift at the operator's rate, fuel by the minutes worked.</summary>
        int CostOf(bool dayShift)
        {
            long usd = 0;
            for (int s = 0; s < 3; s++)
            {
                if (dayShift != (s == 2)) continue;
                long len = Math.Max(0, _gShiftEnd[s] - _gShiftStart[s]);
                usd += _gCrews[s] * len * Pacing.GroomPayUsdHour * Pacing.GroomShiftPayPermille[s] / 60_000;
            }
            for (int p = 0; p < _pCount; p++)
            {
                int g = _pLane[p] / 3;
                int m = _gModel[g] - 1;
                if (m >= 0) usd += (long)(_pTo[p] - _pFrom[p]) * Pacing.GroomFuelUsd[m] / 60;
            }
            return (int)Math.Min(int.MaxValue, usd);
        }

        // ---------- the work ----------

        void Work(int prev, int now)
        {
            _gWorkedTo = now;
            if (_pCount == 0) return;
            for (int p = 0; p < _pCount; p++)
            {
                int a = Math.Max(prev, _pFrom[p]), b = Math.Min(now, _pTo[p]);
                if (b <= a) continue;
                int k = _pTask[p];
                if (k == TransitTask)
                {
                    _gSeasonMin[_pLane[p] / 3] += b - a; _gLaneTransitMin[_pLane[p]] += b - a;
                    if (b == _pTo[p] && (_pPpm[p] & WayBit) != 0) GroomOnTheWay(p);
                    continue;
                }
                if (_kState[k] == (byte)GroomRowState.Done || _kState[k] == (byte)GroomRowState.PitchSkipped) continue;
                int span = Math.Max(1, _pTo[p] - _pFrom[p]);
                int share = (int)((long)_pPpm[p] * (b - a) / span);
                if (b == _pTo[p]) share = _pPpm[p] - (int)((long)_pPpm[p] * (a - _pFrom[p]) / span);   // the piece's last minute lands its rest exactly
                int before = _kDone[k];
                _kDone[k] = Math.Min(1_000_000, _kDone[k] + share);
                _gSeasonMin[_pLane[p] / 3] += b - a;
                _gLaneWorkMin[_pLane[p]] += b - a;
                int wt = _kTrail[k];
                if (_gNightFrom.Length != _gSetting.Length) ClearNightWorked();   // a new layout mid-night starts the record again
                if (wt >= 0 && wt < _gNightFrom.Length)
                {
                    if (_gNightFrom[wt] < 0 || a < _gNightFrom[wt]) { _gNightFrom[wt] = a; _gNightLane[wt] = _pLane[p] / 3; }
                    if (b > _gNightTo[wt]) _gNightTo[wt] = b;
                    _gNightMin[wt] += b - a;
                }
                if (_kState[k] == (byte)GroomRowState.Planned) _kState[k] = (byte)GroomRowState.Partial;
                GroomSlice(k, before, _kDone[k], _pLane[p]);
                _gCarryPpm[_kTrail[k]] = (_kFlags[k] & KFromTail) != 0 ? -_kDone[k] : _kDone[k];
            }
            for (int k = 0; k < _kCount; k++)
            {
                if (_kDone[k] < 1_000_000 || _kState[k] == (byte)GroomRowState.Done || _kState[k] == (byte)GroomRowState.PitchSkipped) continue;
                FinishTask(k);
            }
        }

        void FinishTask(int k)
        {
            int t = _kTrail[k];
            bool push = _kJob[k] == (byte)GroomJob.Push;
            // GO-04: a W works to the winch's limit, any other groomer to the winch grade; steeper pitches are left as they are
            bool winch = LaneOfTaskIsWinch(k);
            bool ok = (_kFlags[k] & KAny) != 0;   // GO-16: the snow was groomed as the work went
            if (ok && push && (_kFlags[k] & KGreen) != 0) MarkPushedGreen(t);
            long skipped = _gAcres[t * 4 + 3] + (winch ? 0 : _gAcres[t * 4 + 2]);
            _kState[k] = (byte)(!ok ? GroomRowState.TooBare : skipped > 0 ? GroomRowState.PitchSkipped : GroomRowState.Done);
            _gCarryPpm[t] = 0;
            _gTonight[t] = 1;
            _gOrder[t] = 0;
            _gPowderNights[t] = 0;
            if (ok) _gNewUm[t] = 0;
            if (ok)
            {
                _trailsGroomedToday++;
                if (t < _trailGroomedNow.Length) _trailGroomedNow[t] = true;
                Events.Add(Tick, SimEventType.TrailGroomed, NetIndexOf(t), (_gLastAbs / 60) % 24);
            }
        }

        /// <summary>
        /// GO-16 v2 (owner 2026-11-21): the share of task <paramref name="k"/> done from <paramref name="d0"/> to
        /// <paramref name="d1"/> (ppm) grooms that band across the trail's width, the whole length (full-length passes side
        /// by side, from the left edge looking downhill), so a run cut short is groomed across part of its width and finished
        /// by a later shift, and the map shows which part.
        /// </summary>
        void GroomSlice(int k, int d0, int d1, int lane)
        {
            if (d1 <= d0) return;
            int t = _kTrail[k];
            if (_trailStart[t + 1] <= _trailStart[t]) return;
            int bandLo = d0 <= 0 ? 0 : d0, bandHi = d1 >= 1_000_000 ? int.MaxValue : d1;
            bool push = _kJob[k] == (byte)GroomJob.Push;
            bool winch = IsWinch(lane / 3);
            var s = (push ? GroomStrength.Heavy(Pacing) : GroomStrength.Light(Pacing)).WithMaxGrade(winch ? Pacing.GroomWinchMaxGrade : Pacing.GroomWinchGrade);
            if (!GroomTrail(t, s, -1, -1, bandLo, bandHi)) return;
            _kFlags[k] = (byte)(_kFlags[k] | KAny);
            if (t < _gGroomedHour.Length) _gGroomedHour[t] = HourIndex + 1;   // GO-14 (screen only)
        }

        /// <summary>Index <paramref name="i"/> in trail <paramref name="t"/>'s cells moved on to the start of the next station.</summary>
        int StationEdge(int t, int i)
        {
            int a = _trailStart[t], z = _trailStart[t + 1], st = _trailLayout.FineMetres * 1000;
            var along = _trailLayout.AlongMm;
            while (i > a && i < z && along[_trailOrder[i]] / st == along[_trailOrder[i - 1]] / st) i++;
            return i;
        }

        bool LaneOfTaskIsWinch(int k)
        {
            for (int p = 0; p < _pCount; p++) if (_pTask[p] == k && IsWinch(_pLane[p] / 3)) return true;
            return false;
        }

        /// <summary>SN-27 v2: a push before the whale cured leaves its snow set up hard, rated lower until groomed again.</summary>
        void MarkPushedGreen(int t)
        {
            var fine = _fine;
            for (int i = _trailStart[t]; i < _trailStart[t + 1]; i++)
            {
                int f = _trailOrder[i];
                if (fine.Age[f] != 0 || fine.Swe[f] == 0) continue;   // worked tonight
                fine.Flags[f] = (byte)(fine.Flags[f] | FineSnow.PushedGreen);
                fine.Type[f] = (byte)SnowType.HP;
            }
        }

        /// <summary>GO-09: the night's end: what wasn't reached, the minutes short, and the rows kept for the morning.</summary>
        void Morning(int day)
        {
            int notReached = 0, work = 0;
            for (int t = 0; t < _gLast.Length; t++) _gLast[t] = (GroomRowState)(_gWarned[t] >> 4);
            for (int k = 0; k < _kCount; k++)
            {
                var st = (GroomRowState)_kState[k];
                if (st == GroomRowState.Planned || st == GroomRowState.Partial) st = _kDone[k] > 0 ? GroomRowState.Partial : GroomRowState.NotReached;
                if (st == GroomRowState.NotReached || st == GroomRowState.Partial) notReached++;
                _gLast[_kTrail[k]] = st;
            }
            for (int p = 0; p < _pCount; p++) if (_pTask[p] != TransitTask) work += _pTo[p] - _pFrom[p];
            // the tab's figure: the minutes the lanes worked (a replan drops the pieces already worked, so the plan's pieces
            // undercount); not saved, as the Last night strip isn't
            _gLastWorkShown = 0;
            for (int l = 0; l < _gLaneWorkMin.Length; l++) _gLastWorkShown += _gLaneWorkMin[l];
            for (int t = 0; t < _gPowderNights.Length; t++)
                if ((GroomRowState)(_gWarned[t] >> 4) == GroomRowState.PowderHeld && _gPowderNights[t] < 255) _gPowderNights[t]++;
            _trailsLastGroom = 0;
            for (int t = 0; t < _gTonight.Length; t++) if (_gTonight[t] != 0) _trailsLastGroom++;
            Array.Clear(_gStormUm, 0, _gStormUm.Length);   // the morning: the next storm counts from here
            _trailBlocksLastGroom = 0;
            for (int b = 0; b < _blockWorked.Length; b++) if (_blockWorked[b]) _trailBlocksLastGroom++;
            // GO-14: the night's lanes, kept for the Last night strip until the next morning (not saved).
            int zero = _gShiftStart[0] != 0 ? _gShiftStart[0] : _gShiftStart[2];
            _gLastZeroAbs = zero; _gLastEndAbs = Math.Max(_gShiftEnd[1], Math.Max(_gShiftEnd[0], _gShiftEnd[2]));   // a day shift has no night
            for (int t = 0; t < _gLastGroomer.Length; t++) { _gLastGroomer[t] = -1; _gLastStart[t] = -1; _gLastEnd[t] = -1; }
            for (int k = 0; k < _kCount; k++)
            {
                int t = _kTrail[k];
                if (t < 0 || t >= _gLastGroomer.Length || _kStart[k] < 0) continue;
                // The task row keeps its groomer's number as a byte (saved, sidecar v13); the lane keeps its index in the fleet.
                int lane = -1;
                for (int g = 0; g < _gCount; g++) if ((byte)_gNumber[g] == _kGroomer[k]) { lane = g; break; }
                _gLastGroomer[t] = (short)lane;
                _gLastStart[t] = (short)Math.Min(short.MaxValue, _kStart[k] - zero);
                _gLastEnd[t] = -1;   // a later task on the trail replaces an earlier one whole, never half
                // An unfinished task: its lane runs as far as it got (its share of the minutes it needed).
                if (_kEnd[k] < 0 && _kDone[k] <= 0) continue;   // started on paper only: not reached
                int end = _kEnd[k] >= 0 ? _kEnd[k] : _kStart[k] + Math.Max(1, (int)((long)_kNeed[k] * _kDone[k] / 1_000_000));
                _gLastEnd[t] = (short)Math.Min(short.MaxValue, end - zero);
            }
            // what was worked wins over the last plan's rows (a replan at the night's start re-lays only what was left)
            for (int t = 0; t < _gNightFrom.Length && t < _gLastGroomer.Length; t++)
            {
                if (_gNightFrom[t] < 0) continue;
                _gLastGroomer[t] = (short)_gNightLane[t];
                _gLastStart[t] = (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, _gNightFrom[t] - zero));
                // round 4 (owner 2026-11-21: "the hours math doesn't line up"): as long as the minutes worked, so a run
                // worked in two stretches (a replan between) shows what it took, and the rows, lanes and header add up
                int got = Math.Max(1, Math.Min(_gNightMin[t], _gNightTo[t] - _gNightFrom[t]));
                _gLastEnd[t] = (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, _gNightFrom[t] + got - zero));
            }
            _gLastNotReached = notReached; _gLastShort = _gShort; _gLastWork = work; _gLastCost = _gCost;
            for (int t = 0; t < _gOrder.Length; t++) if (_gOrder[t] == (byte)GroomOrder.Skip) _gOrder[t] = 0;
            _gMorningDone = day;
            _gNight = -1;
            _kCount = 0; _pCount = 0;
        }

        /// <summary>GO-09 v2 (owner 2026-11-21): the night's report comes out at the opening (Skip to morning lands there): the
        /// Last night view turns over and the shortfall event goes out. The night itself closed at its end (<see cref="Morning"/>).</summary>
        void Report(int day)
        {
            _gLastNight = day;
            if (_gEver && (_gLastNotReached > 0 || _gLastShort > 0)) Events.Add(Tick, SimEventType.GroomShortfall, _gLastNotReached, _gLastShort);
        }

        // ---------- the snapshot, the hash, the sidecar ----------

        void WriteGrooming(SimSnapshot s)
        {
            var v = s.Grooming;
            // after a reopen (the sidecar keeps no hours-since) the first snapshot fills them, not the next hour
            if (_gSinceH.Length == 0 && _gSetting.Length > 0) GroomSince();
            // GO-14 v3: while no night is planned the tab shows tonight's preview (swapped in for the writing, then back)
            if (_gPreviewDirty) PreviewTonight();
            // only a preview made for this layout (its per-trail arrays are this long)
            bool pv = _gPreviewOn && _gNight < 0 && !_gPreviewDirty && _gPreview.Warned.Length == _gSetting.Length && _gPreview.Tonight.Length == _gSetting.Length;
            if (pv) SwapPlan();
            try
            {
                v.Preview = pv;
                v.Live = _gEver || _gSetting.Length > 0;
                v.NightDay = pv ? _gPreviewDay : _gNight;
                v.SwingStartAbsMin = _gShiftStart[0]; v.SwingEndAbsMin = _gShiftEnd[0]; v.NightEndAbsMin = _gShiftEnd[1];
                v.CrewsSwing = _gCrews[0]; v.CrewsNight = _gCrews[1]; v.CrewsDay = _gCrews[2];
                v.SnowingTonight = _gSnowing;
                v.WantMinutes = _gWant; v.CrewMinutes = _gCrewMin; v.ShortMinutes = _gShort; v.CostUsd = _gCost;
                v.LastNotReached = _gLastNotReached; v.LastShortMinutes = _gLastShort; v.LastWorkMinutes = _gLastWorkShown >= 0 ? _gLastWorkShown : _gLastWork;
                v.LastCostUsd = _gLastCost; v.LastNightDay = _gLastNight;
                v.LastSwingStartAbsMin = _gLastZeroAbs; v.LastNightEndAbsMin = _gLastEndAbs;
                int trails = Math.Min(s.TrailCount, v.Job.Length);
                for (int k = 0; k < trails; k++)
                {
                    v.Job[k] = GroomJob.None; v.State[k] = GroomRowState.None; v.LastState[k] = GroomRowState.None;
                    v.Groomer[k] = 0; v.StartMin[k] = -1; v.EndMin[k] = -1; v.NeedMin[k] = 0;
                    v.Lane[k] = 127;
                }
                int zero = _gShiftStart[0] != 0 ? _gShiftStart[0] : _gShiftStart[2];
                for (int t = 0; t < _gSetting.Length; t++)
                {
                    int k = NetIndexOf(t);
                    if (k < 0 || k >= trails) continue;
                    v.State[k] = (GroomRowState)(_gWarned[t] >> 4);
                    v.LastState[k] = t < _gLast.Length ? _gLast[t] : GroomRowState.None;
                    int since = t < _gSinceH.Length && _gSinceH[t] != 255 ? _gSinceH[t] : -1;
                    v.Lane[k] = GroomView.PackLane(t < _gLastGroomer.Length ? _gLastGroomer[t] : -1, t < _gLastStart.Length ? _gLastStart[t] : -1, t < _gLastEnd.Length ? _gLastEnd[t] : -1, since);
                }
                for (int i = 0; i < _kCount; i++)
                {
                    int k = NetIndexOf(_kTrail[i]);
                    if (k < 0 || k >= trails) continue;
                    v.Job[k] = (GroomJob)_kJob[i]; v.State[k] = (GroomRowState)_kState[i]; v.Groomer[k] = (byte)Math.Min(255, (int)_kGroomer[i]);
                    v.StartMin[k] = (short)(_kStart[i] < 0 ? -1 : Math.Min(short.MaxValue, _kStart[i] - zero));
                    v.EndMin[k] = (short)(_kEnd[i] < 0 ? -1 : Math.Min(short.MaxValue, _kEnd[i] - zero));
                    v.NeedMin[k] = (short)Math.Min(short.MaxValue, _kNeed[i]);
                }
                v.GroomerCount = _gCount;
                for (int g = 0; g < GroomFleet.MaxGroomers; g++)
                {
                    v.Number[g] = _gNumber[g]; v.Model[g] = (GroomerModel)_gModel[g]; v.Drive[g] = (GroomerDrive)_gDrive[g]; v.Tiller[g] = _gTiller[g]; v.SeasonMinutes[g] = _gSeasonMin[g];
                    byte shifts = 0;
                    for (int sh = 0; sh < 3; sh++) if (CrewedOn(g, sh)) shifts |= (byte)(1 << sh);
                    v.Shifts[g] = shifts;
                    v.AtTrailA[g] = -1; v.AtTrailB[g] = -1; v.AtPpmA[g] = 0; v.AtPpmB[g] = 0; v.AtMinA[g] = 0; v.AtMinB[g] = 0; v.AtJob[g] = GroomJob.None;
                    v.AtTail[g] = 0; v.AtDrive[g] = 0;
                }
                // GO-14, GO-15: each lane's hours, worked plus still planned, and its drives
                for (int l = 0; l < GroomFleet.MaxGroomers * 3; l++)
                {
                    long plan = pv ? 0 : _gLaneWorkMin[l], drive = pv ? 0 : _gLaneTransitMin[l];
                    for (int p = 0; p < _pCount; p++)
                    {
                        if (_pLane[p] != l) continue;
                        int left = Math.Max(0, _pTo[p] - Math.Max(_pFrom[p], _gWorkedTo));
                        if (_pTask[p] == TransitTask) drive += left; else plan += left;
                    }
                    v.PlanMin[l] = (short)Math.Min(short.MaxValue, plan);
                    v.TransitMin[l] = (short)Math.Min(short.MaxValue, drive);
                }
                // GO-14, GO-15: the pieces for the night strip (minutes from its zero), drives and work
                int tc = 0;
                for (int p = 0; p < _pCount && tc < GroomView.MaxTransit; p++)
                {
                    int nk = _pTask[p] == TransitTask ? -1 : NetIndexOf(_kTrail[_pTask[p]]);
                    if (_pTask[p] != TransitTask && nk < 0) continue;
                    v.TransitTrail[tc] = (short)Math.Min(short.MaxValue, nk);
                    v.TransitLane[tc] = (byte)(_pLane[p] / 3);
                    v.TransitFrom[tc] = (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, _pFrom[p] - zero));
                    v.TransitTo[tc] = (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, _pTo[p] - zero));
                    tc++;
                }
                v.TransitCount = tc;
                v.DepotLift = _gDepotLift;
                v.WayPasses = _gWayPasses;
            }
            finally
            {
                if (pv) SwapPlan();   // the live plan back (the dots stand where it has them), whatever happened
            }
            int nowMin = (int)Math.Min(int.MaxValue, FaceMs / 60_000);
            for (int g = 0; g < _gCount && g < GroomFleet.MaxGroomers; g++) PlaceGroomer(v, g, nowMin);
            v.WarningCount = Math.Min(_gWarnCount, GroomView.MaxWarnings);
            for (int w = 0; w < v.WarningCount; w++)
            {
                int slot = (_gWarnCount - 1 - w) % GroomView.MaxWarnings;   // newest first
                v.WarningKind[w] = (GroomWarningKind)_gWarnKind[slot]; v.WarningTrail[w] = (short)_gWarnTrail[slot];
            }
        }

        /// <summary>GO-13: groomer <paramref name="g"/>'s piece now, or the pieces either side of now (a pass over the pieces).</summary>
        void PlaceGroomer(GroomView v, int g, int now)
        {
            int act = -1, prev = -1, next = -1, first = -1, last = int.MinValue;
            for (int p = 0; p < _pCount; p++)
            {
                if (_pLane[p] / 3 != g) continue;
                if (_pTask[p] == TransitTask)
                {
                    if (first < 0 || _pFrom[p] < first) first = _pFrom[p];
                    if (_pTo[p] > last) last = _pTo[p];
                    continue;
                }
                if (_pFrom[p] <= now && now < _pTo[p]) { act = p; break; }
                if (_pTo[p] <= now && (prev < 0 || _pTo[p] > _pTo[prev])) prev = p;
                if (_pFrom[p] > now && (next < 0 || _pFrom[p] < _pFrom[next])) next = p;
            }
            if (act >= 0)
            {
                PieceShare(act, out int a, out int b);
                short trail = (short)NetIndexOf(_kTrail[_pTask[act]]);
                v.AtTrailA[g] = trail; v.AtPpmA[g] = a; v.AtMinA[g] = _pFrom[act];
                v.AtTrailB[g] = trail; v.AtPpmB[g] = b; v.AtMinB[g] = _pTo[act];
                v.AtJob[g] = (GroomJob)_kJob[_pTask[act]];
                v.AtTail[g] = (byte)((_kFlags[_pTask[act]] & KFromTail) != 0 ? 3 : 0);
                return;
            }
            if (prev < 0 && next < 0) return;
            int travel = Pacing.GroomTravelMinutes;
            if (prev >= 0)
            {
                PieceShare(prev, out _, out int b);
                v.AtTrailA[g] = (short)NetIndexOf(_kTrail[_pTask[prev]]); v.AtPpmA[g] = b; v.AtMinA[g] = _pTo[prev];
                if ((_kFlags[_pTask[prev]] & KFromTail) != 0) v.AtTail[g] |= 1;
            }
            else v.AtMinA[g] = first >= 0 && first < _pFrom[next] ? first : _pFrom[next] - travel;
            if (next >= 0)
            {
                PieceShare(next, out int a, out _);
                v.AtTrailB[g] = (short)NetIndexOf(_kTrail[_pTask[next]]); v.AtPpmB[g] = a; v.AtMinB[g] = _pFrom[next];
                if ((_kFlags[_pTask[next]] & KFromTail) != 0) v.AtTail[g] |= 2;
            }
            else v.AtMinB[g] = last > _pTo[prev] ? last : _pTo[prev] + travel;
            // the drive in the gap, as the sim laid it: the dot leaves when it does and takes as long
            int gapA = v.AtMinA[g], gapB = v.AtMinB[g];
            for (int p = 0; p < _pCount; p++)
            {
                if (_pTask[p] != TransitTask || _pLane[p] / 3 != g || _pFrom[p] < gapA || _pTo[p] > gapB) continue;
                if (prev >= 0) v.AtMinA[g] = _pFrom[p];
                v.AtDrive[g] = (short)Math.Min(short.MaxValue, _pTo[p] - _pFrom[p]);
                break;
            }
        }

        /// <summary>
        /// The share of its task piece <paramref name="p"/> works (ppm): the task's pieces in time order, after what the task
        /// had done when they were laid (one less the pieces' sum, never below 0).
        /// </summary>
        void PieceShare(int p, out int from, out int to)
        {
            int k = _pTask[p];
            long sum = 0, before = 0;
            for (int q = 0; q < _pCount; q++)
            {
                if (_pTask[q] != k) continue;
                sum += _pPpm[q];
                if (_pFrom[q] < _pFrom[p] || (_pFrom[q] == _pFrom[p] && q < p)) before += _pPpm[q];
            }
            long start = Math.Max(0, 1_000_000 - sum) + before;
            from = (int)Math.Min(1_000_000, start);
            to = (int)Math.Min(1_000_000, start + _pPpm[p]);
        }

        void HashGrooming(ref StateHasher h)
        {
            if (!_gEver) return;   // a world never given a fleet hashes as before (the goldens)
            h.Add(_gCount);
            for (int g = 0; g < _gCount; g++) { h.Add(_gNumber[g]); h.Add(_gModel[g]); h.Add(_gDrive[g]); h.Add(_gTiller[g] ? 1 : 0); h.Add(_gSeasonMin[g]); }
            h.Add(_gPlan.SwingPin); h.Add(_gPlan.NightPin); h.Add(_gPlan.DayPin);
            h.Add(_gPlan.TrailIds.Length);
            for (int i = 0; i < _gPlan.TrailIds.Length; i++) { h.Add(_gPlan.TrailIds[i]); h.Add((int)_gPlan.Settings[i]); h.Add((int)_gPlan.Priorities[i]); }
            h.Add(_gIds.Length);
            for (int t = 0; t < _gIds.Length; t++)
            {
                h.Add(_gIds[t]); h.Add(_gOrder[t]); h.Add(_gPowderNights[t]); h.Add(_gWarned[t]); h.Add((int)_gLast[t]); h.Add(_gCarryPpm[t]); h.Add(_gTonight[t]);
                h.Add(_gNewUm[t]); h.Add(_gSwePrev[t]); h.Add(_gStormUm[t]);
            }
            h.Add(_gNight); h.Add(_gDayPlanned); h.Add(_gMorningDone); h.Add(_gLastAbs);
            for (int s = 0; s < 3; s++) { h.Add(_gShiftStart[s]); h.Add(_gShiftEnd[s]); h.Add(_gCrews[s]); }
            h.Add(_gSnowing ? 1 : 0); h.Add(_gWant); h.Add(_gCrewMin); h.Add(_gShort); h.Add(_gCost);
            h.Add(_kCount);
            for (int k = 0; k < _kCount; k++)
            {
                h.Add(_kTrail[k]); h.Add(_kDone[k]); h.Add(_kNeed[k]); h.Add(_kStart[k]); h.Add(_kEnd[k]);
                h.Add(_kJob[k]); h.Add(_kState[k]); h.Add(_kGroomer[k]); h.Add(_kFlags[k]); h.Add(_kPushYd3[k]);
            }
            h.Add(_pCount);
            for (int p = 0; p < _pCount; p++) { h.Add(_pTask[p]); h.Add(_pFrom[p]); h.Add(_pTo[p]); h.Add(_pPpm[p]); h.Add(_pLane[p]); }
            h.Add(_gLastNight); h.Add(_gLastNotReached); h.Add(_gLastShort); h.Add(_gLastWork); h.Add(_gLastCost);
            h.Add(_gWarnCount);
        }

        /// <summary>
        /// Sidecar v13 (task 4.25): a flag, then (when a fleet ever came) the fleet with its season minutes, the payload's pins
        /// and trail settings, the per-trail state by id, tonight (times, crews, tasks, pieces), last night and the warnings.
        /// </summary>
        void WriteGroomState(SidecarWriter o)
        {
            o.Bool(_gEver);
            if (!_gEver) return;
            o.I32(_gCount);
            for (int g = 0; g < _gCount; g++) { o.I32(_gNumber[g]); o.U8(_gModel[g]); o.U8(_gDrive[g]); o.Bool(_gTiller[g]); o.I32(_gSeasonMin[g]); }
            o.I32(_gPlan.SwingPin); o.I32(_gPlan.NightPin); o.I32(_gPlan.DayPin);
            o.I32(_gPlan.TrailIds.Length);
            for (int i = 0; i < _gPlan.TrailIds.Length; i++) { o.U64(_gPlan.TrailIds[i]); o.U8((byte)_gPlan.Settings[i]); o.U8((byte)_gPlan.Priorities[i]); }
            o.I32(_gIds.Length);
            for (int t = 0; t < _gIds.Length; t++)
            {
                o.U64(_gIds[t]); o.U8(_gOrder[t]); o.U8(_gPowderNights[t]); o.U8(_gWarned[t]); o.U8((byte)_gLast[t]); o.I32(_gCarryPpm[t]); o.U8(_gTonight[t]);
                o.I32(_gNewUm[t]); o.I64(_gSwePrev[t]); o.I32(_gStormUm[t]);
            }
            o.I32(_gNight); o.I32(_gDayPlanned); o.I32(_gMorningDone); o.I64(_gLastAbs);
            for (int s = 0; s < 3; s++) { o.I32(_gShiftStart[s]); o.I32(_gShiftEnd[s]); o.I32(_gCrews[s]); }
            o.Bool(_gSnowing); o.I32(_gWant); o.I32(_gCrewMin); o.I32(_gShort); o.I32(_gCost);
            o.I32(_kCount);
            for (int k = 0; k < _kCount; k++)
            {
                o.I32(_kTrail[k]); o.I32(_kDone[k]); o.I32(_kNeed[k]); o.I32(_kStart[k]); o.I32(_kEnd[k]);
                o.U8(_kJob[k]); o.U8(_kState[k]); o.U8(_kGroomer[k]); o.U8(_kFlags[k]); o.I32(_kPushYd3[k]);
            }
            o.I32(_pCount);
            for (int p = 0; p < _pCount; p++) { o.I32(_pTask[p]); o.I32(_pFrom[p]); o.I32(_pTo[p]); o.I32(_pPpm[p]); o.U8(_pLane[p]); }
            o.I32(_gLastNight); o.I32(_gLastNotReached); o.I32(_gLastShort); o.I32(_gLastWork); o.I32(_gLastCost);
            o.I32(_gWarnCount);
            for (int w = 0; w < GroomView.MaxWarnings; w++) { o.U8(_gWarnKind[w]); o.I32(_gWarnTrail[w]); }
            // the blocks groomed tonight, by 8 m cell ascending (the layout's block order)
            if (_gWorkedPending != null)
            {
                o.I32(_gWorkedPending.Length);
                foreach (int cell in _gWorkedPending) o.I32(cell);
            }
            else
            {
                int worked = 0;
                var L = _trailLayout;
                int blocks = L == null ? 0 : Math.Min(_blockWorked.Length, L.Blocks);
                for (int b = 0; b < blocks; b++) if (_blockWorked[b]) worked++;
                o.I32(worked);
                for (int b = 0; b < blocks; b++) if (_blockWorked[b]) o.I32(L.BlockCell[b]);
            }
        }

        void ReadGroomState(SidecarReader r, int version)
        {
            bool v14 = version >= SimSidecar.TransitVersion;   // GO-15/16: transit pieces, a signed share, NoRoute
            var maxState = v14 ? GroomRowState.NoRoute : GroomRowState.Skipped;
            _gEver = r.Bool();
            if (!_gEver) return;
            _gCount = r.Count(GroomFleet.MaxGroomers);
            var num = new int[_gCount]; var mod = new GroomerModel[_gCount]; var drv = new GroomerDrive[_gCount]; var til = new bool[_gCount];
            for (int g = 0; g < _gCount; g++)
            {
                _gNumber[g] = num[g] = r.I32();
                _gModel[g] = r.Enum((int)GroomerModel.S400, (int)GroomerModel.S800W, "a groomer's model"); mod[g] = (GroomerModel)_gModel[g];
                _gDrive[g] = r.Enum((int)GroomerDrive.Diesel, (int)GroomerDrive.Electric, "a groomer's drive"); drv[g] = (GroomerDrive)_gDrive[g];
                _gTiller[g] = til[g] = r.Bool();
                _gSeasonMin[g] = r.I32();
            }
            int sp = r.I32(), np = r.I32(), dp = r.I32();   // left to right
            int nt = r.Count(1 << 20);
            var ids = new ulong[nt]; var set = new GroomSetting[nt]; var pri = new GroomPriority[nt];
            for (int i = 0; i < nt; i++)
            {
                ids[i] = r.U64();
                set[i] = (GroomSetting)r.Enum((int)GroomSetting.Autopilot, (int)GroomSetting.Never, "a trail's grooming");
                pri[i] = (GroomPriority)r.Enum((int)GroomPriority.Normal, (int)GroomPriority.Low, "a trail's grooming priority");
            }
            _gPlan = new SimGrooming(num, mod, drv, til, sp, np, dp, ids, set, pri);
            if (_gPlan.Problem() is string problem) throw new System.IO.InvalidDataException("The grooming is damaged: " + problem + ".");
            int n = r.Count(1 << 16);
            _gIds = new ulong[n];
            _gOrder = new byte[n]; _gPowderNights = new byte[n]; _gWarned = new byte[n]; _gLast = new GroomRowState[n]; _gCarryPpm = new int[n]; _gTonight = new byte[n];
            // GO-14's screen-only state isn't saved: a reopen starts it empty (no lanes until the next morning).
            _gLastGroomer = Array.Empty<short>(); _gLastStart = Array.Empty<short>(); _gLastEnd = Array.Empty<short>();
            _gSinceH = Array.Empty<byte>(); _gGroomedHour = new long[n]; _gLastZeroAbs = 0; _gLastEndAbs = 0;
            _gNewUm = new int[n]; _gSwePrev = new long[n]; _gStormUm = new int[n];
            for (int t = 0; t < n; t++)
            {
                _gIds[t] = r.U64(); _gOrder[t] = r.U8(); _gPowderNights[t] = r.U8(); _gWarned[t] = r.U8();
                _gLast[t] = (GroomRowState)r.Enum(0, (int)maxState, "a trail's grooming row"); _gCarryPpm[t] = r.Ranged(v14 ? -1_000_000 : 0, 1_000_000, "a trail's grooming share"); _gTonight[t] = r.U8();
                _gNewUm[t] = r.Ranged(0, int.MaxValue, "a trail's snow since its groom"); _gSwePrev[t] = r.I64(); _gStormUm[t] = r.Ranged(0, int.MaxValue, "a trail's snow since the morning");
            }
            SizeGroomTasks(n);
            _gNight = r.I32(); _gDayPlanned = r.I32(); _gMorningDone = r.I32(); _gLastAbs = r.I64();
            for (int s = 0; s < 3; s++) { _gShiftStart[s] = r.I32(); _gShiftEnd[s] = r.I32(); _gCrews[s] = r.Ranged(0, GroomFleet.MaxGroomers, "a shift's crews"); }
            _gSnowing = r.Bool(); _gWant = r.I32(); _gCrewMin = r.I32(); _gShort = r.I32(); _gCost = r.I32();
            _kCount = r.Count(n);
            for (int k = 0; k < _kCount; k++)
            {
                _kTrail[k] = r.Ranged(0, n - 1, "a task's trail"); _kDone[k] = r.Ranged(0, 1_000_000, "a task's share"); _kNeed[k] = r.I32();
                _kStart[k] = r.I32(); _kEnd[k] = r.I32();
                _kJob[k] = r.Enum(0, (int)GroomJob.Push, "a task's job"); _kState[k] = r.Enum(0, (int)maxState, "a task's state");
                _kGroomer[k] = r.U8(); _kFlags[k] = r.U8(); _kPushYd3[k] = r.Ranged(0, int.MaxValue, "a task's pile");
            }
            _pCount = r.Count(_pTask.Length);
            for (int p = 0; p < _pCount; p++)
            {
                _pTask[p] = r.Ranged(v14 ? TransitTask : 0, Math.Max(0, _kCount - 1), "a piece's task"); _pFrom[p] = r.I32(); _pTo[p] = r.I32();
                _pPpm[p] = _pTask[p] == TransitTask ? r.I32() : r.Ranged(0, 1_000_000, "a piece's share");
                _pLane[p] = (byte)r.Ranged(0, GroomFleet.MaxGroomers * 3 - 1, "a piece's lane");
            }
            RankGroomers();
            _gLastNight = r.I32(); _gLastNotReached = r.I32(); _gLastShort = r.I32(); _gLastWork = r.I32(); _gLastCost = r.I32();
            _gWarnCount = r.I32();
            for (int w = 0; w < GroomView.MaxWarnings; w++) { _gWarnKind[w] = r.U8(); _gWarnTrail[w] = r.I32(); }
            int wc = r.Count(64_000_000);
            _gWorkedPending = new int[wc];
            for (int i = 0; i < wc; i++)
            {
                _gWorkedPending[i] = r.I32();
                if (_gWorkedPending[i] < 0 || (i > 0 && _gWorkedPending[i] <= _gWorkedPending[i - 1])) throw new System.IO.InvalidDataException("The groomed blocks are out of order.");
            }
        }
    }
}
