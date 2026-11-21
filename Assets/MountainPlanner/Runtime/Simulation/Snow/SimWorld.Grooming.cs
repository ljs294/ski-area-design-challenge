using System;
using MountainPlanner.Domain.Design;
using MountainPlanner.Domain.Snow;

namespace MountainPlanner.Simulation
{
    /// <summary>
    /// Task 4.25: grooming operations (GO-01…GO-12, SN-22 v4, SM-05), which replace 4.07's single 8 PM pass.
    /// - **Shifts (GO-03):** swing from the sweep after the lift stop to <see cref="Pacing.GroomSwingEndHour"/>, night from
    ///   there to an hour before the next opening, day <see cref="Pacing.GroomDayStartHour"/>–<see cref="Pacing.GroomDayEndHour"/>
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
        byte[] _kJob = Array.Empty<byte>(), _kState = Array.Empty<byte>(), _kGroomer = Array.Empty<byte>(), _kFlags = Array.Empty<byte>();
        int[] _pTask = Array.Empty<int>(), _pFrom = Array.Empty<int>(), _pTo = Array.Empty<int>(), _pPpm = Array.Empty<int>();
        byte[] _pLane = Array.Empty<byte>();
        const byte KLate = 1, KWinch = 2, KSkipPitch = 4, KGreen = 8, KDay = 16;
        // last night (GO-09)
        int _gLastNight = -1, _gLastNotReached, _gLastShort, _gLastWork, _gLastCost;
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

        /// <summary>The fleet (tests and the readout).</summary>
        internal int GroomerCount => _gCount;

        bool ApplyGrooming(in SimCommand c)
        {
            if (c.A == 1)
            {
                if (!(c.Payload is SimGroomOrder o) || o.Order > GroomOrder.Skip) return false;
                int t = GroomIndexOf(o.TrailId);
                if (t < 0) return false;
                _gOrder[t] = (byte)o.Order;
                _gEver = true;
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
                    int nk = newIndex[_pTask[p]];
                    if (nk < 0) continue;
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
        }

        static T[] Remap<T>(T[] old, int[] map, int n)
        {
            var a = new T[n];
            for (int o = 0; o < map.Length && o < old.Length; o++) if (map[o] >= 0) a[map[o]] = old[o];
            return a;
        }

        void SizeGroomTasks(int n)
        {
            if (_kTrail.Length >= n && _gOrder.Length == n) return;
            Array.Resize(ref _kTrail, n); Array.Resize(ref _kDone, n); Array.Resize(ref _kNeed, n); Array.Resize(ref _kStart, n); Array.Resize(ref _kEnd, n);
            Array.Resize(ref _kJob, n); Array.Resize(ref _kState, n); Array.Resize(ref _kGroomer, n); Array.Resize(ref _kFlags, n); Array.Resize(ref _kPushYd3, n);
            int pieces = n * PiecesPerTask;
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
                }
            }
            Work(cursor, now);
        }

        readonly int[] _gBAt = new int[16], _gBKind = new int[16], _gBDay = new int[16];

        static int Rank(int kind) => kind == 3 ? -1 : kind;

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
            nightStart = (day + 1) * 1440 + Pacing.GroomSwingEndHour * 60;
            nightEnd = (day + 1) * 1440 + Design.Hours.OpenSecond / 60 - Pacing.GroomNightEndsBeforeOpenMinutes;
            if (nightStart < swingStart) nightStart = swingStart;
            if (nightEnd < nightStart) nightEnd = nightStart;
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
                    else if (passes > 0 || newUm >= Pacing.GroomNewSnowUm || icy) job = GroomJob.Till;
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
                int i = _kCount++;
                _kTrail[i] = t; _kJob[i] = (byte)job; _kState[i] = (byte)GroomRowState.Planned; _kFlags[i] = flags;
                _kDone[i] = _gCarryPpm[t]; _kNeed[i] = 0; _kGroomer[i] = 0; _kStart[i] = -1; _kEnd[i] = -1;
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
        void Schedule(int from, bool dayShift)
        {
            // the lanes' windows
            for (int g = 0; g < GroomFleet.MaxGroomers; g++)
                for (int s = 0; s < 3; s++)
                {
                    int l = g * 3 + s;
                    bool on = g < _gCount && g < _gCrews[s] && _gShiftEnd[s] > _gShiftStart[s] && (dayShift ? s == 2 : s < 2);
                    int begin = _gShiftStart[s] + Pacing.GroomTravelMinutes, end = _gShiftEnd[s] - Pacing.GroomTravelMinutes;
                    if (on && _gDrive[g] == (byte)GroomerDrive.Electric) end = Math.Min(end, begin + Pacing.GroomElectricMinutes);
                    if (begin < from) begin = from;
                    _laneOn[l] = on && end > begin;
                    _laneBegin[l] = begin; _laneEnd[l] = end;
                    _laneFree[l] = begin; _laneLate[l] = end;
                }
            _pCount = 0;
            _gWant = 0; _gShort = 0;
            // late tasks (new snow tonight: B2a-3) are laid from the night's end backward, first, so the rest fills forward
            for (int pass = 0; pass < 2; pass++)
                for (int o = 0; o < _kCount; o++)
                {
                    int k = _gSort[o];
                    bool late = (_kFlags[k] & KLate) != 0;
                    if ((pass == 0) != late) continue;
                    PlaceTask(k, late);
                }
            _gCrewMin = 0;
            for (int l = 0; l < GroomFleet.MaxGroomers * 3; l++) if (_laneOn[l]) _gCrewMin += _laneEnd[l] - _laneBegin[l];
            _gCost = CostOf(dayShift);
        }

        void PlaceTask(int k, bool late)
        {
            bool needWinch = (_kFlags[k] & KWinch) != 0;
            int remaining = 1_000_000 - _kDone[k];
            bool any = false;
            int wantOn = -1;
            for (int guard = 0; guard < PiecesPerTask && remaining > 0 && _pCount < _pTask.Length; guard++)
            {
                // the lane free soonest (latest, for a late task), lowest groomer and shift on a tie
                int best = -1, bestAt = 0;
                for (int l = 0; l < GroomFleet.MaxGroomers * 3; l++)
                {
                    if (!_laneOn[l]) continue;
                    int g = l / 3;
                    if (needWinch && !IsWinch(g) && AnyWinchCrewed()) continue;
                    if (late && l % 3 != 1) continue;
                    int room = _laneLate[l] - _laneFree[l];
                    if (room <= 0) continue;
                    int at = late ? _laneLate[l] : _laneFree[l];
                    if (best < 0 || (late ? at > bestAt : at < bestAt)) { best = l; bestAt = at; }
                }
                if (best < 0 && late) { late = false; guard--; continue; }
                if (best < 0) break;
                int gg = best / 3;
                int need = NeedOn(k, gg);
                if (wantOn < 0) { wantOn = gg; _gWant += (int)((long)need * remaining / 1_000_000); }
                int len = (int)Math.Max(1, ((long)need * remaining + 999_999) / 1_000_000);
                int room2 = _laneLate[best] - _laneFree[best];
                int take = Math.Min(len, room2);
                int ppm = take >= len ? remaining : (int)((long)take * 1_000_000 / need);
                int p = _pCount++;
                _pTask[p] = k; _pLane[p] = (byte)best; _pPpm[p] = ppm;
                if (late) { _pTo[p] = _laneLate[best]; _pFrom[p] = _laneLate[best] - take; _laneLate[best] -= take; }
                else { _pFrom[p] = _laneFree[best]; _pTo[p] = _laneFree[best] + take; _laneFree[best] += take; }
                if (!any) { _kGroomer[k] = (byte)_gNumber[gg]; _kStart[k] = _pFrom[p]; _kNeed[k] = need; }
                _kStart[k] = Math.Min(_kStart[k], _pFrom[p]);
                _kEnd[k] = Math.Max(_kEnd[k], _pTo[p]);
                any = true;
                remaining -= ppm;
            }
            if (remaining > 0)
            {
                // out of hours: what's left is short (at the first lane's speed, else the first groomer's)
                int need = NeedOn(k, wantOn >= 0 ? wantOn : 0);
                if (wantOn < 0) _gWant += (int)((long)need * remaining / 1_000_000);
                _gShort += (int)((long)need * remaining / 1_000_000);
                _kState[k] = (byte)(any ? GroomRowState.Partial : GroomRowState.NotReached);
                if (!any) _kNeed[k] = need;
            }
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
            if (_pCount == 0) return;
            for (int p = 0; p < _pCount; p++)
            {
                int a = Math.Max(prev, _pFrom[p]), b = Math.Min(now, _pTo[p]);
                if (b <= a) continue;
                int k = _pTask[p];
                if (_kState[k] == (byte)GroomRowState.Done || _kState[k] == (byte)GroomRowState.PitchSkipped) continue;
                int span = Math.Max(1, _pTo[p] - _pFrom[p]);
                int share = (int)((long)_pPpm[p] * (b - a) / span);
                if (b == _pTo[p]) share = _pPpm[p] - (int)((long)_pPpm[p] * (a - _pFrom[p]) / span);   // the piece's last minute lands its rest exactly
                _kDone[k] = Math.Min(1_000_000, _kDone[k] + share);
                _gSeasonMin[_pLane[p] / 3] += b - a;
                if (_kState[k] == (byte)GroomRowState.Planned) _kState[k] = (byte)GroomRowState.Partial;
                _gCarryPpm[_kTrail[k]] = _kDone[k];
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
            var s = (push ? GroomStrength.Heavy(Pacing) : GroomStrength.Light(Pacing)).WithMaxGrade(winch ? Pacing.GroomWinchMaxGrade : Pacing.GroomWinchGrade);
            bool ok = GroomTrail(t, s);
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
            for (int p = 0; p < _pCount; p++) work += _pTo[p] - _pFrom[p];
            for (int t = 0; t < _gPowderNights.Length; t++)
                if ((GroomRowState)(_gWarned[t] >> 4) == GroomRowState.PowderHeld && _gPowderNights[t] < 255) _gPowderNights[t]++;
            _trailsLastGroom = 0;
            for (int t = 0; t < _gTonight.Length; t++) if (_gTonight[t] != 0) _trailsLastGroom++;
            Array.Clear(_gStormUm, 0, _gStormUm.Length);   // the morning: the next storm counts from here
            _trailBlocksLastGroom = 0;
            for (int b = 0; b < _blockWorked.Length; b++) if (_blockWorked[b]) _trailBlocksLastGroom++;
            _gLastNight = day; _gLastNotReached = notReached; _gLastShort = _gShort; _gLastWork = work; _gLastCost = _gCost;
            if (_gEver && (notReached > 0 || _gShort > 0)) Events.Add(Tick, SimEventType.GroomShortfall, notReached, _gShort);
            for (int t = 0; t < _gOrder.Length; t++) if (_gOrder[t] == (byte)GroomOrder.Skip) _gOrder[t] = 0;
            _gMorningDone = day;
            _gNight = -1;
            _kCount = 0; _pCount = 0;
        }

        // ---------- the snapshot, the hash, the sidecar ----------

        void WriteGrooming(SimSnapshot s)
        {
            var v = s.Grooming;
            v.Live = _gEver || _gSetting.Length > 0;
            v.NightDay = _gNight;
            v.SwingStartAbsMin = _gShiftStart[0]; v.SwingEndAbsMin = _gShiftEnd[0]; v.NightEndAbsMin = _gShiftEnd[1];
            v.CrewsSwing = _gCrews[0]; v.CrewsNight = _gCrews[1]; v.CrewsDay = _gCrews[2];
            v.SnowingTonight = _gSnowing;
            v.WantMinutes = _gWant; v.CrewMinutes = _gCrewMin; v.ShortMinutes = _gShort; v.CostUsd = _gCost;
            v.LastNotReached = _gLastNotReached; v.LastShortMinutes = _gLastShort; v.LastWorkMinutes = _gLastWork;
            v.LastCostUsd = _gLastCost; v.LastNightDay = _gLastNight;
            int trails = Math.Min(s.TrailCount, v.Job.Length);
            for (int k = 0; k < trails; k++)
            {
                v.Job[k] = GroomJob.None; v.State[k] = GroomRowState.None; v.LastState[k] = GroomRowState.None;
                v.Groomer[k] = 0; v.StartMin[k] = -1; v.EndMin[k] = -1; v.NeedMin[k] = 0;
            }
            int zero = _gShiftStart[0] != 0 ? _gShiftStart[0] : _gShiftStart[2];
            for (int t = 0; t < _gSetting.Length; t++)
            {
                int k = NetIndexOf(t);
                if (k < 0 || k >= trails) continue;
                v.State[k] = (GroomRowState)(_gWarned[t] >> 4);
                v.LastState[k] = t < _gLast.Length ? _gLast[t] : GroomRowState.None;
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
                v.Number[g] = _gNumber[g]; v.Model[g] = (GroomerModel)_gModel[g]; v.Tiller[g] = _gTiller[g]; v.SeasonMinutes[g] = _gSeasonMin[g];
                byte shifts = 0;
                for (int sh = 0; sh < 3; sh++) if (g < _gCount && g < _gCrews[sh] && _gShiftEnd[sh] > _gShiftStart[sh]) shifts |= (byte)(1 << sh);
                v.Shifts[g] = shifts;
                v.AtTrailA[g] = -1; v.AtTrailB[g] = -1; v.AtPpmA[g] = 0; v.AtPpmB[g] = 0; v.AtMinA[g] = 0; v.AtMinB[g] = 0; v.AtJob[g] = GroomJob.None;
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
            int act = -1, prev = -1, next = -1;
            for (int p = 0; p < _pCount; p++)
            {
                if (_pLane[p] / 3 != g) continue;
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
                return;
            }
            if (prev < 0 && next < 0) return;
            int travel = Pacing.GroomTravelMinutes;
            if (prev >= 0)
            {
                PieceShare(prev, out _, out int b);
                v.AtTrailA[g] = (short)NetIndexOf(_kTrail[_pTask[prev]]); v.AtPpmA[g] = b; v.AtMinA[g] = _pTo[prev];
            }
            else v.AtMinA[g] = _pFrom[next] - travel;
            if (next >= 0)
            {
                PieceShare(next, out int a, out _);
                v.AtTrailB[g] = (short)NetIndexOf(_kTrail[_pTask[next]]); v.AtPpmB[g] = a; v.AtMinB[g] = _pFrom[next];
            }
            else v.AtMinB[g] = _pTo[prev] + travel;
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

        void ReadGroomState(SidecarReader r)
        {
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
            _gNewUm = new int[n]; _gSwePrev = new long[n]; _gStormUm = new int[n];
            for (int t = 0; t < n; t++)
            {
                _gIds[t] = r.U64(); _gOrder[t] = r.U8(); _gPowderNights[t] = r.U8(); _gWarned[t] = r.U8();
                _gLast[t] = (GroomRowState)r.Enum(0, (int)GroomRowState.Skipped, "a trail's grooming row"); _gCarryPpm[t] = r.Ranged(0, 1_000_000, "a trail's grooming share"); _gTonight[t] = r.U8();
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
                _kJob[k] = r.Enum(0, (int)GroomJob.Push, "a task's job"); _kState[k] = r.Enum(0, (int)GroomRowState.Skipped, "a task's state");
                _kGroomer[k] = r.U8(); _kFlags[k] = r.U8(); _kPushYd3[k] = r.Ranged(0, int.MaxValue, "a task's pile");
            }
            _pCount = r.Count(n * PiecesPerTask);
            for (int p = 0; p < _pCount; p++)
            {
                _pTask[p] = r.Ranged(0, Math.Max(0, _kCount - 1), "a piece's task"); _pFrom[p] = r.I32(); _pTo[p] = r.I32();
                _pPpm[p] = r.Ranged(0, 1_000_000, "a piece's share"); _pLane[p] = (byte)r.Ranged(0, GroomFleet.MaxGroomers * 3 - 1, "a piece's lane");
            }
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
