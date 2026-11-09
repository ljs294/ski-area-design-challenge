using System;
using System.IO;
using MountainPlanner.Domain.Design;

namespace MountainPlanner.Simulation
{
    /// <summary>A scripted feeder's order (LO-13; tests, the demo's <c>-feeder</c>): skiers who join a lift's line, no guests.</summary>
    public sealed class LiftFeed
    {
        public readonly ulong LiftId;
        /// <summary>Skiers who join at once.</summary>
        public readonly int Add;
        /// <summary>Skiers who join each real minute at 1× from now on (0 stops the feeder).</summary>
        public readonly int PerMinute;
        public LiftFeed(ulong liftId, int add, int perMinute)
        {
            if (add < 0 || add > 1_000_000) throw new ArgumentOutOfRangeException(nameof(add));
            if (perMinute < 0 || perMinute > 100_000) throw new ArgumentOutOfRangeException(nameof(perMinute));
            LiftId = liftId; Add = add; PerMinute = perMinute;
        }
    }

    /// <summary>
    /// Task 4.11, lift operations (p4-02 §6, LO-01…LO-13). Each lift loads one carrier every interval of its rope's run
    /// time (LO-01; RP-07 v2: a carrier's place is a pure function of the rope's travel, so the departures are too). The
    /// line is a FIFO ring N wide (LO-02); the front N board each departure. Riders are hidden for the compressed ride and
    /// released at the top on the next carrier arrival (LO-06); the carriers carry what boarded at real speed (LO-05).
    /// Hours, schedules and the way down decide the board (LO-07 v2, LO-09, LO-10); wind holds stop the rope (LO-11); the
    /// day ledger counts it all (LO-12). Integers only; every array is allocated when a design is folded, never per tick.
    /// </summary>
    public sealed partial class SimWorld
    {
        // ---- per lift, in the design's ID order (allocated in FoldLiftOps) ----
        int[] _intervalMs = Array.Empty<int>();
        int[] _lOpen = Array.Empty<int>(), _lLast = Array.Empty<int>(), _lStop = Array.Empty<int>();
        byte[] _downloading = Array.Empty<byte>(), _wayDown = Array.Empty<byte>();
        long[] _calmSince = Array.Empty<long>();
        int[] _feedPerMin = Array.Empty<int>();
        long[] _feedAcc = Array.Empty<long>();
        int[] _aboard = Array.Empty<int>();
        // the line (LO-02): a ring of MaxLine per lift
        long[] _lineJoin = Array.Empty<long>();
        int[] _lineSkier = Array.Empty<int>();
        int[] _lineHead = Array.Empty<int>(), _lineCount = Array.Empty<int>();
        // hidden riders (LO-06): a ring of MaxLine per lift, released by the rope's run time
        long[] _rideExit = Array.Empty<long>();
        int[] _rideSkier = Array.Empty<int>();
        int[] _rideHead = Array.Empty<int>(), _rideCount = Array.Empty<int>();
        // the carriers' riders (LO-05): one byte per carrier slot in flight on the way up
        byte[] _occ = Array.Empty<byte>();
        int[] _occOffset = Array.Empty<int>(), _occSlots = Array.Empty<int>();
        // the day's ledger (LO-12)
        int[] _rides = Array.Empty<int>(), _peakLine = Array.Empty<int>(), _waitCount = Array.Empty<int>(), _leftInLine = Array.Empty<int>(), _released = Array.Empty<int>();
        long[] _waitTicks = Array.Empty<long>(), _holdFaceMs = Array.Empty<long>();
        // LO-10's search, allocated with the network
        int[] _nodeBottom = Array.Empty<int>(), _bottomNext = Array.Empty<int>(), _searchStack = Array.Empty<int>();
        int[] _visitedAt = Array.Empty<int>();
        int _visitMark;
        byte[] _candidate = Array.Empty<byte>();
        /// <summary>Per network trail: 1 when an open lift's descent runs along it today.</summary>
        byte[] _trailReached = Array.Empty<byte>();

        // ---- the gust seam (LO-11, WX-39): per lift, the hour's gust at its top station, mph. 0 until 4.05's weather
        // writes it in HourStep; SetGust overrides every lift (tests, the demo) ----
        int[] _weatherGustMph = Array.Empty<int>();
        int _gustOverrideMph = -1;

        /// <summary>LO-11's gust at a lift's top station this hour, mph: the weather's (4.05) or the override.</summary>
        int GustAtTop(int i) => _gustOverrideMph >= 0 ? _gustOverrideMph : _weatherGustMph[i];

        // ================= folding =================

        /// <summary>Carries each lift's operations over by ID into a new design's arrays (SI-20).</summary>
        void FoldLiftOps(ulong[] oldIds, int oldCount, ulong[] ids, int n)
        {
            int cap = Pacing.MaxLine;
            var interval = new int[n];
            var downloading = new byte[n]; var wayDown = new byte[n];
            var calm = new long[n]; var gust = new int[n]; var feed = new int[n]; var acc = new long[n]; var aboard = new int[n];
            var lineJoin = new long[n * cap]; var lineSkier = new int[n * cap]; var lineHead = new int[n]; var lineCount = new int[n];
            var rideExit = new long[n * cap]; var rideSkier = new int[n * cap]; var rideHead = new int[n]; var rideCount = new int[n];
            var occOffset = new int[n]; var occSlots = new int[n];
            var rides = new int[n]; var peak = new int[n]; var waitCount = new int[n]; var left = new int[n]; var released = new int[n];
            var waitTicks = new long[n]; var holdMs = new long[n];
            int total = 0;
            for (int i = 0; i < n; i++)
            {
                var l = Design.Lifts[i];
                interval[i] = Math.Max(1, l.IntervalMs / Pacing.ChairSpeedFactor);
                // The carriers on the way up at once: the ride over the interval, and one each end.
                occSlots[i] = (int)Math.Min(1 << 16, (long)l.RideMs / interval[i] + 2);
                occOffset[i] = total;
                total += occSlots[i];
            }
            var occ = new byte[total];
            for (int i = 0; i < n; i++)
            {
                calm[i] = -1;
                wayDown[i] = 1;
                int was = Array.BinarySearch(oldIds, 0, oldCount, ids[i]);
                if (was < 0 || was >= _intervalMs.Length) continue;
                downloading[i] = _downloading[was]; wayDown[i] = _wayDown[was];
                calm[i] = _calmSince[was]; gust[i] = _weatherGustMph[was]; feed[i] = _feedPerMin[was]; acc[i] = _feedAcc[was];
                rides[i] = _rides[was]; peak[i] = _peakLine[was]; waitCount[i] = _waitCount[was]; left[i] = _leftInLine[was];
                released[i] = _released[was]; waitTicks[i] = _waitTicks[was]; holdMs[i] = _holdFaceMs[was];
                // The line and the riders keep their order; a lift whose interval changed keeps them too (they ride the new one).
                lineCount[i] = _lineCount[was];
                for (int k = 0; k < lineCount[i]; k++)
                {
                    int from = was * cap + (_lineHead[was] + k) % cap;
                    lineJoin[i * cap + k] = _lineJoin[from]; lineSkier[i * cap + k] = _lineSkier[from];
                }
                rideCount[i] = _rideCount[was];
                for (int k = 0; k < rideCount[i]; k++)
                {
                    int from = was * cap + (_rideHead[was] + k) % cap;
                    rideExit[i * cap + k] = _rideExit[from]; rideSkier[i * cap + k] = _rideSkier[from];
                }
                if (_occSlots[was] == occSlots[i] && _intervalMs[was] == interval[i])
                {
                    Buffer.BlockCopy(_occ, _occOffset[was], occ, occOffset[i], occSlots[i]);
                    aboard[i] = _aboard[was];
                }
            }
            _intervalMs = interval; _downloading = downloading; _wayDown = wayDown; _calmSince = calm; _weatherGustMph = gust; _feedPerMin = feed; _feedAcc = acc;
            _aboard = aboard; _lineJoin = lineJoin; _lineSkier = lineSkier; _lineHead = lineHead; _lineCount = lineCount;
            _rideExit = rideExit; _rideSkier = rideSkier; _rideHead = rideHead; _rideCount = rideCount;
            _occ = occ; _occOffset = occOffset; _occSlots = occSlots;
            _rides = rides; _peakLine = peak; _waitCount = waitCount; _leftInLine = left; _released = released; _waitTicks = waitTicks; _holdFaceMs = holdMs;
            _lOpen = new int[n]; _lLast = new int[n]; _lStop = new int[n];
            FoldNetwork(n);
            LiftHoursToday();
        }

        void FoldNetwork(int n)
        {
            var net = Design.Network;
            _candidate = new byte[n];
            _bottomNext = new int[n];
            if (net == null)
            {
                _nodeBottom = Array.Empty<int>(); _searchStack = Array.Empty<int>(); _visitedAt = Array.Empty<int>(); _trailReached = Array.Empty<byte>();
                return;
            }
            _nodeBottom = new int[net.NodeCount];
            for (int k = 0; k < _nodeBottom.Length; k++) _nodeBottom[k] = -1;
            for (int i = n - 1; i >= 0; i--)
            {
                int b = Design.Lifts[i].BottomNode;
                _bottomNext[i] = -1;
                if (b < 0) continue;
                _bottomNext[i] = _nodeBottom[b];
                _nodeBottom[b] = i;
            }
            _searchStack = new int[net.NodeCount + 1];
            _visitedAt = new int[net.NodeCount];
            _visitMark = 0;
            _trailReached = new byte[net.TrailIds.Length];
        }

        /// <summary>LO-07 v2: each lift's hours today, its own (or the resort's) cut to today's trimmed hours.</summary>
        void LiftHoursToday()
        {
            for (int i = 0; i < _liftCount && i < _lOpen.Length; i++)
            {
                var h = Design.Lifts[i].Hours;
                int open = h?.OpenSecond ?? _todayOpen, last = h?.LastChairSecond ?? _todayLastChair, stop = h?.StopSecond ?? _todayStop;
                open = Math.Max(open, _todayOpen);
                stop = Math.Max(open, Math.Min(stop, _todayStop));
                last = Math.Max(open, Math.Min(last, stop));
                _lOpen[i] = open; _lLast[i] = last; _lStop[i] = stop;
            }
        }

        // ================= the board (LO-09, LO-10) =================

        /// <summary>LO-09: whether a lift's schedule runs it today (Peak days only: weekend and holiday day types).</summary>
        bool ScheduledToday(int i)
        {
            if (Design.Lifts[i].Schedule != LiftSchedule.PeakDays) return true;
            int faceDay = (int)(FaceMs / DayMs);
            return Calendar.TypeOf(faceDay % Calendar.YearFaceDays) != DayType.Weekday;
        }

        /// <summary>
        /// The board's lift column (SI-19) into <paramref name="into"/>: open today when it's the season, the lift is built,
        /// its schedule runs it today and it has a way down (LO-10), or it's downloading. The search also marks the trails
        /// an open lift reaches. A lift that loses its way down within its hours downloads (chairs) or closes (tows, carpets).
        /// </summary>
        void BoardLifts(byte[] into)
        {
            long sod = FaceMs % DayMs;
            for (int i = 0; i < _liftCount; i++)
                _candidate[i] = (byte)(Phase == SeasonPhase.Season && _liftBuilt[i] && ScheduledToday(i) ? 1 : 0);
            SearchWayDown();
            for (int i = 0; i < _liftCount; i++)
            {
                bool way = _wayDown[i] != 0;
                if (_candidate[i] == 0) { into[i] = 0; _downloading[i] = 0; continue; }
                bool inDay = sod >= _lOpen[i] * 1000L && sod < _lStop[i] * 1000L;
                if (!way && inDay && _liftOpen[i] != 0 && LiftKinds.CanDownload(Design.Lifts[i].Kind))
                {
                    if (_downloading[i] == 0)
                    {
                        _downloading[i] = 1;
                        _leftInLine[i] += _lineCount[i];
                        _lineCount[i] = 0; _lineHead[i] = 0;
                        Events.Add(Tick, SimEventType.LiftDownloading, i, (long)_liftIds[i]);
                    }
                    into[i] = 1;
                    continue;
                }
                _downloading[i] = 0;
                into[i] = (byte)(way ? 1 : 0);
            }
        }

        /// <summary>
        /// LO-10 v1: a candidate keeps its way down when a descent from its top (ready trails and paths, never a lift) reaches
        /// the bottom of a candidate lift, its own included (NW-03); candidates without one drop out until none does. No
        /// network yet: every candidate has one and every trail counts as reached.
        /// </summary>
        void SearchWayDown()
        {
            var net = Design.Network;
            if (net == null)
            {
                for (int i = 0; i < _liftCount; i++) _wayDown[i] = _candidate[i];
                return;
            }
            for (int i = 0; i < _liftCount; i++) _wayDown[i] = _candidate[i];
            bool changed = true;
            while (changed)
            {
                changed = false;
                for (int i = 0; i < _liftCount; i++)
                {
                    if (_wayDown[i] == 0) continue;
                    if (!Descend(i, mark: false)) { _wayDown[i] = 0; changed = true; }
                }
            }
            Array.Clear(_trailReached, 0, _trailReached.Length);
            for (int i = 0; i < _liftCount; i++) if (_wayDown[i] != 0) Descend(i, mark: true);
        }

        /// <summary>A depth-first descent from lift <paramref name="i"/>'s top; true when it meets a lift with a way down's bottom.</summary>
        bool Descend(int i, bool mark)
        {
            var net = Design.Network;
            int start = Design.Lifts[i].TopNode;
            if (start < 0) return false;
            if (++_visitMark == int.MaxValue) { Array.Clear(_visitedAt, 0, _visitedAt.Length); _visitMark = 1; }
            int sp = 0;
            _searchStack[sp++] = start;
            _visitedAt[start] = _visitMark;
            bool found = false;
            while (sp > 0)
            {
                int node = _searchStack[--sp];
                for (int j = _nodeBottom[node]; j >= 0; j = _bottomNext[j])
                    if (_wayDown[j] != 0) { found = true; if (!mark) return true; }
                int e0 = net.FirstEdge[node];
                if (e0 < 0) continue;
                for (int e = e0; e < e0 + net.EdgeCount[node]; e++)
                {
                    int t = net.EdgeTrail[e];
                    if (t >= 0 && !TrailReady(t)) continue;
                    if (mark && t >= 0) _trailReached[t] = 1;
                    int to = net.EdgeTo[e];
                    if (_visitedAt[to] == _visitMark) continue;
                    _visitedAt[to] = _visitMark;
                    _searchStack[sp++] = to;
                }
            }
            return found;
        }

        /// <summary>SN-20's readiness of a network trail: every built trail until 4.07 writes its own bits.</summary>
        bool TrailReady(int trail) => true;

        // ================= the step (the Lifts row) =================

        /// <summary>LO-07 v2: whether the lift's rope may run now (its hours, the board, no hold).</summary>
        bool LiftRunsAt(int i, long sod) =>
            _stepDayRate && _liftBuilt[i] && _liftOpen[i] != 0 && _liftHold[i] == 0 && sod >= _lOpen[i] * 1000L && sod < _lStop[i] * 1000L;

        /// <summary>LO-02, LO-07: whether a skier may join the line now: Scheduled, Open or On hold (never past last chair).</summary>
        bool LineJoinable(int i, long sod) =>
            _liftBuilt[i] && _liftOpen[i] != 0 && _downloading[i] == 0 && sod < _lLast[i] * 1000L;

        internal void RunLifts()
        {
            long ms = (long)_stepTicks * Pacing.TickMs;
            long sod = FaceMs % DayMs;
            for (int i = 0; i < _liftCount; i++)
            {
                CheckWind(i, sod);
                Feed(i, sod);
                bool run = LiftRunsAt(i, sod);
                if (run != _running[i])
                {
                    _running[i] = run;
                    Events.Add(Tick, run ? SimEventType.LiftStarted : SimEventType.LiftStopped, i, (long)_liftIds[i]);
                }
                if (run) Advance(i, ms, sod);
                else if (!InLiftDay(i, sod) && (_lineCount[i] > 0 || _rideCount[i] > 0 || _aboard[i] > 0)) CloseOut(i);
                if (_liftHold[i] != 0 && InLiftDay(i, sod)) _holdFaceMs[i] += _stepAdvance;
            }
        }

        /// <summary>Today's lift day: from midnight (a line may form before opening) to the lift's stop, on the board.</summary>
        bool InLiftDay(int i, long sod) => Phase == SeasonPhase.Season && _liftOpen[i] != 0 && sod < _lStop[i] * 1000L;

        /// <summary>One step of a running rope: the carriers that reach the top unload, the ones leaving the bottom load.</summary>
        void Advance(int i, long ms, long sod)
        {
            long before = _runMs[i], after = before + ms;
            _runMs[i] = after;
            var lift = Design.Lifts[i];
            long iv = _intervalMs[i];
            long ride = lift.RideMs;
            // LO-05: the carriers that left at d·interval reach the top at d·interval + ride.
            if (after >= ride)
            {
                long a0 = before >= ride ? (before - ride) / iv + 1 : 0, a1 = (after - ride) / iv;
                for (long a = a0; a <= a1; a++)
                {
                    if (a * iv + ride <= before) continue;
                    int slot = _occOffset[i] + (int)(a % _occSlots[i]);
                    _aboard[i] -= _occ[slot];
                    _occ[slot] = 0;
                }
            }
            // LO-01, LO-02: a carrier leaves the bottom every interval of run time; the front N board it. LO-07: past last chair
            // no one joins, but the line keeps boarding until the stop.
            bool boarding = _downloading[i] == 0;
            for (long d = before / iv + 1; d <= after / iv; d++)
            {
                int slot = _occOffset[i] + (int)(d % _occSlots[i]);
                _aboard[i] -= _occ[slot];   // a slot's carrier is long gone by the time it comes round (the ring is sized for it)
                _occ[slot] = 0;
                if (!boarding) continue;
                int board = Math.Min(lift.Seats, Math.Min(_lineCount[i], Pacing.MaxLine - _rideCount[i]));
                long exit = ExitRun(lift, d * iv, iv);
                for (int k = 0; k < board; k++)
                {
                    int at = i * Pacing.MaxLine + _lineHead[i];
                    long joined = _lineJoin[at];
                    int skier = _lineSkier[at];
                    _lineHead[i] = (_lineHead[i] + 1) % Pacing.MaxLine;
                    _lineCount[i]--;
                    _waitTicks[i] += Tick - joined;
                    _waitCount[i]++;
                    int r = i * Pacing.MaxLine + (_rideHead[i] + _rideCount[i]) % Pacing.MaxLine;
                    _rideExit[r] = exit;
                    _rideSkier[r] = skier;
                    _rideCount[i]++;
                }
                if (_lineCount[i] == 0) _lineHead[i] = 0;
                _occ[slot] = (byte)board;
                _aboard[i] += board;
                _rides[i] += board;
            }
            // LO-06: hidden riders step off at the top.
            while (_rideCount[i] > 0 && _rideExit[i * Pacing.MaxLine + _rideHead[i]] <= after)
            {
                _rideHead[i] = (_rideHead[i] + 1) % Pacing.MaxLine;
                _rideCount[i]--;
                _released[i]++;
            }
            if (_rideCount[i] == 0) _rideHead[i] = 0;
        }

        /// <summary>
        /// LO-06: the rope run time a rider who boarded at <paramref name="boardRun"/> steps off: the ride ÷ the compression
        /// (tows and carpets: the whole ride, on the snow), then the next carrier arrival at the top.
        /// </summary>
        long ExitRun(in LiftInput lift, long boardRun, long iv)
        {
            long ride = lift.RideMs;
            if (!LiftKinds.Compressed(lift.Kind)) return boardRun + ride;
            long due = boardRun + ride / Pacing.RideCompression;
            long phase = ride % iv;
            long k = due - phase <= 0 ? 0 : (due - phase + iv - 1) / iv;
            return k * iv + phase;
        }

        /// <summary>LO-07: past the lift's stop the line leaves (counted) and riders still up step off; the chairs come in empty.</summary>
        void CloseOut(int i)
        {
            _leftInLine[i] += _lineCount[i];
            _released[i] += _rideCount[i];
            _lineCount[i] = 0; _lineHead[i] = 0;
            _rideCount[i] = 0; _rideHead[i] = 0;
            Array.Clear(_occ, _occOffset[i], _occSlots[i]);
            _aboard[i] = 0;
        }

        /// <summary>LO-13's feeder: whole skiers join each step from the rate (thousandths carried), or once from an order.</summary>
        void Feed(int i, long sod)
        {
            if (_feedPerMin[i] == 0) return;
            if (!LineJoinable(i, sod)) { _feedAcc[i] = 0; return; }
            _feedAcc[i] += (long)_feedPerMin[i] * _stepTicks * Pacing.TickMs;
            while (_feedAcc[i] >= 60_000)
            {
                _feedAcc[i] -= 60_000;
                Join(i, -1);
            }
        }

        /// <summary>LO-02: a skier joins the back of the line (false when it's full).</summary>
        internal bool Join(int i, int skier)
        {
            if (_lineCount[i] == Pacing.MaxLine) return false;
            int at = i * Pacing.MaxLine + (_lineHead[i] + _lineCount[i]) % Pacing.MaxLine;
            _lineJoin[at] = Tick;
            _lineSkier[at] = skier;
            _lineCount[i]++;
            if (_lineCount[i] > _peakLine[i]) _peakLine[i] = _lineCount[i];
            return true;
        }

        /// <summary>
        /// LO-11 v2: once an hour, at a keyed offset per lift, the gust at the top × a keyed factor against the lift's
        /// limit. A held lift reads it every <see cref="Pacing.HoldRecheckFaceMinutes"/> and resumes after the gust has
        /// been the margin below for <see cref="Pacing.HoldResumeFaceMinutes"/> (hysteresis; owner 2026-11-08: sooner).
        /// </summary>
        void CheckWind(int i, long sod)
        {
            var lift = Design.Lifts[i];
            int k = LiftKinds.HoldIndex(lift.Kind);
            if (k < 0 || !_liftBuilt[i] || Phase != SeasonPhase.Season) return;
            ulong key = SimKey.Hash(Design.ResortSeed, SimKey.WindCheckTag, _liftIds[i]);
            long period = _liftHold[i] != 0 ? Pacing.HoldRecheckFaceMinutes * 60_000L : HourMs;
            long offset = (long)(key % (ulong)period);
            long inHour = FaceMs % period;
            if (offset < inHour || offset >= inHour + Math.Max(1, _stepAdvance)) return;
            int j = Pacing.GustJitterPercent;
            int factor = 100 + (int)((key >> 32) % (ulong)(2 * j + 1)) - j;
            int gust = GustAtTop(i) * factor / 100;
            int limit = HoldLimitMph(Pacing, lift);
            if (_liftHold[i] == 0)
            {
                if (gust <= limit) return;
                _liftHold[i] = 1;
                _calmSince[i] = -1;
                Events.Add(Tick, SimEventType.LiftHeld, i, gust);
                return;
            }
            if (gust > limit - Pacing.HoldResumeMarginMph) { _calmSince[i] = -1; return; }
            if (_calmSince[i] < 0) { _calmSince[i] = FaceMs; return; }
            if (FaceMs - _calmSince[i] < Pacing.HoldResumeFaceMinutes * 60_000L) return;
            _liftHold[i] = 0;
            _calmSince[i] = -1;
            Events.Add(Tick, SimEventType.LiftResumed, i, gust);
        }

        /// <summary>
        /// LO-11 v2 (owner 2026-11-08): the gust that holds <paramref name="lift"/>, mph, or −1 when it never holds (tows,
        /// carpets). Fixed grips hold later than detachables, and more seats (a bigger, heavier carrier) hold later still:
        /// a fixed-grip double 40, triple 43, quad 46; a detachable quad 35, six 38, eight 41; gondolas 45; trams 55.
        /// </summary>
        public static int HoldLimitMph(Pacing pacing, in LiftInput lift)
        {
            int k = LiftKinds.HoldIndex(lift.Kind);
            if (k < 0) return -1;
            return pacing.HoldGustMph[k] + pacing.HoldGustPerSeatTenths[k] * Math.Max(1, lift.Seats) / 10;
        }

        /// <summary>Whether any feeder could add a skier this step (it keeps the step to one tick, so join ticks are exact).</summary>
        bool AnyFeeding()
        {
            long sod = FaceMs % DayMs;
            for (int i = 0; i < _liftCount; i++) if (_feedPerMin[i] != 0 && LineJoinable(i, sod)) return true;
            return false;
        }

        /// <summary>The next lift-hours boundary after <paramref name="sod"/>, before <paramref name="next"/> (a step never crosses one).</summary>
        long LiftBoundary(long sod, long next)
        {
            for (int i = 0; i < _liftCount; i++)
            {
                long o = _lOpen[i] * 1000L, l = _lLast[i] * 1000L, s = _lStop[i] * 1000L;
                if (o > sod && o < next) next = o;
                if (l > sod && l < next) next = l;
                if (s > sod && s < next) next = s;
            }
            return next;
        }

        // ================= commands =================

        bool ApplyLiftCommand(in SimCommand c)
        {
            switch (c.Kind)
            {
                case SimCommandKind.SetGust:
                    if (c.A < -1 || c.A > 500) { Reject(c, SimRefusal.Unknown); return false; }
                    _gustOverrideMph = (int)c.A;
                    return true;
                case SimCommandKind.FeedLine:
                {
                    if (!(c.Payload is LiftFeed f)) { Reject(c, SimRefusal.Unknown); return false; }
                    int i = Array.BinarySearch(_liftIds, 0, _liftCount, f.LiftId);
                    if (i < 0) { Reject(c, SimRefusal.Unknown); return false; }
                    _feedPerMin[i] = f.PerMinute;
                    _feedAcc[i] = 0;
                    long sod = FaceMs % DayMs;
                    if (f.Add > 0 && LineJoinable(i, sod))
                        for (int k = 0; k < f.Add; k++) if (!Join(i, -1)) break;
                    return true;
                }
                default:
                    Reject(c, SimRefusal.Unknown);
                    return false;
            }
        }

        // ================= ledgers =================

        void ResetLiftLedger()
        {
            Array.Clear(_rides, 0, _rides.Length); Array.Clear(_peakLine, 0, _peakLine.Length); Array.Clear(_waitCount, 0, _waitCount.Length);
            Array.Clear(_leftInLine, 0, _leftInLine.Length); Array.Clear(_released, 0, _released.Length);
            Array.Clear(_waitTicks, 0, _waitTicks.Length); Array.Clear(_holdFaceMs, 0, _holdFaceMs.Length);
            for (int i = 0; i < _liftCount; i++) if (_lineCount[i] > _peakLine[i]) _peakLine[i] = _lineCount[i];
        }

        void WriteLiftReport(DayReport r)
        {
            for (int i = 0; i < _liftCount; i++)
            {
                r.Rides[i] = _rides[i];
                r.PeakLine[i] = _peakLine[i];
                r.MeanWaitMs[i] = _waitCount[i] == 0 ? 0 : (int)Math.Min(int.MaxValue, _waitTicks[i] * Pacing.TickMs / _waitCount[i]);
                r.HoldMinutes[i] = (int)(_holdFaceMs[i] / 60_000);
                r.LeftInLine[i] = _leftInLine[i];
                // LO-12: rides ÷ what the lift could carry in its day's real minutes (8 face hours: 12 minutes at the defaults).
                long dayRealMs = (long)(_lStop[i] - _lOpen[i]) * Pacing.DayHourRealSeconds * 1000L / 3600;
                long could = dayRealMs / _intervalMs[i] * Design.Lifts[i].Seats;
                r.UtilisationPermille[i] = could <= 0 ? 0 : (int)Math.Min(1000, _rides[i] * 1000L / could);
            }
        }

        // ================= reading =================

        /// <summary>The snapshot's status word for a lift now (the HUD's Status column).</summary>
        LiftOpStatus StatusOf(int i, long sod)
        {
            if (!_liftBuilt[i]) return LiftOpStatus.Planned;
            if (_liftOpen[i] == 0 || Phase != SeasonPhase.Season) return LiftOpStatus.Closed;
            if (sod < _lOpen[i] * 1000L) return _liftHold[i] != 0 ? LiftOpStatus.OnHold : LiftOpStatus.Scheduled;
            if (sod >= _lStop[i] * 1000L) return LiftOpStatus.Closed;
            if (_downloading[i] != 0) return LiftOpStatus.Downloading;
            if (_liftHold[i] != 0) return LiftOpStatus.OnHold;
            if (sod >= _lLast[i] * 1000L) return LiftOpStatus.Closed;
            return LiftOpStatus.Open;
        }

        /// <summary>LO-04 v1: the wait for a skier joining now, motion ms at 1× (the HUD divides by the speed).</summary>
        long WaitMsOf(int i)
        {
            int seats = Design.Lifts[i].Seats;
            return (long)((_lineCount[i] + seats - 1) / seats) * _intervalMs[i];
        }

        void WriteLifts(SimSnapshot s, long sod)
        {
            for (int i = 0; i < _liftCount; i++)
            {
                s.LineCount[i] = _lineCount[i];
                s.WaitMs[i] = (int)Math.Min(int.MaxValue, WaitMsOf(i));
                s.LiftStatus[i] = StatusOf(i, sod);
                s.Aboard[i] = _aboard[i];
                s.RidesToday[i] = _rides[i];
                s.IntervalMs[i] = _intervalMs[i];
                s.Seats[i] = (byte)Design.Lifts[i].Seats;
                s.LiftOpenSecond[i] = _lOpen[i];
            }
            var net = Design.Network;
            int t = net == null ? 0 : Math.Min(net.TrailIds.Length, s.TrailIds.Length);
            s.TrailCount = t;
            s.TrailsKnown = net != null;
            bool season = Phase == SeasonPhase.Season;
            for (int k = 0; k < t; k++)
            {
                s.TrailIds[k] = net.TrailIds[k];
                bool reached = season && _trailReached[k] != 0;
                s.TrailStatus[k] = !reached ? LiftOpStatus.Closed
                    : sod < _todayOpen * 1000L ? LiftOpStatus.Scheduled
                    : sod < _todayStop * 1000L ? LiftOpStatus.Open : LiftOpStatus.Closed;
            }
        }

        /// <summary>A lift's line by ID, or −1 (tests and the demo).</summary>
        public int LineOf(ulong liftId)
        {
            int i = Array.BinarySearch(_liftIds, 0, _liftCount, liftId);
            return i >= 0 ? _lineCount[i] : -1;
        }

        /// <summary>LO-04: the wait for a skier joining a lift's line now, motion ms at 1×, or −1.</summary>
        public long WaitMs(ulong liftId)
        {
            int i = Array.BinarySearch(_liftIds, 0, _liftCount, liftId);
            return i >= 0 ? WaitMsOf(i) : -1;
        }

        /// <summary>Today's rides so far, peak line, hidden riders and carrier riders of a lift (tests).</summary>
        public (int rides, int peakLine, int riding, int aboard, int released, int leftInLine) LedgerOf(ulong liftId)
        {
            int i = Array.BinarySearch(_liftIds, 0, _liftCount, liftId);
            return i < 0 ? default : (_rides[i], _peakLine[i], _rideCount[i], _aboard[i], _released[i], _leftInLine[i]);
        }

        public LiftOpStatus StatusOf(ulong liftId)
        {
            int i = Array.BinarySearch(_liftIds, 0, _liftCount, liftId);
            return i < 0 ? LiftOpStatus.Planned : StatusOf(i, FaceMs % DayMs);
        }

        public bool HeldNow(ulong liftId)
        {
            int i = Array.BinarySearch(_liftIds, 0, _liftCount, liftId);
            return i >= 0 && _liftHold[i] != 0;
        }

        void HashLifts(ref StateHasher h)
        {
            h.Add(_gustOverrideMph);
            for (int i = 0; i < _liftCount; i++)
            {
                h.Add(_weatherGustMph[i]); h.Add(_downloading[i]); h.Add(_wayDown[i]); h.Add(_calmSince[i]); h.Add(_feedPerMin[i]); h.Add(_feedAcc[i]); h.Add(_aboard[i]);
                h.Add(_rides[i]); h.Add(_peakLine[i]); h.Add(_waitTicks[i]); h.Add(_waitCount[i]); h.Add(_holdFaceMs[i]); h.Add(_leftInLine[i]); h.Add(_released[i]);
                h.Add(_lineCount[i]);
                for (int k = 0; k < _lineCount[i]; k++)
                {
                    int at = i * Pacing.MaxLine + (_lineHead[i] + k) % Pacing.MaxLine;
                    h.Add(_lineJoin[at]); h.Add(_lineSkier[at]);
                }
                h.Add(_rideCount[i]);
                for (int k = 0; k < _rideCount[i]; k++)
                {
                    int at = i * Pacing.MaxLine + (_rideHead[i] + k) % Pacing.MaxLine;
                    h.Add(_rideExit[at]); h.Add(_rideSkier[at]);
                }
                for (int k = 0; k < _occSlots[i]; k++) h.Add(_occ[_occOffset[i] + k]);
            }
            for (int k = 0; k < _trailReached.Length; k++) h.Add(_trailReached[k]);
        }

        // ================= the sidecar (v2's lift-operations block) =================

        void WriteLiftState(SidecarWriter o)
        {
            o.I32(_gustOverrideMph);
            o.I32(_liftCount);
            for (int i = 0; i < _liftCount; i++)
            {
                o.I32(_weatherGustMph[i]); o.U8(_downloading[i]); o.U8(_wayDown[i]); o.I64(_calmSince[i]); o.I32(_feedPerMin[i]); o.I64(_feedAcc[i]); o.I32(_aboard[i]);
                o.I32(_rides[i]); o.I32(_peakLine[i]); o.I64(_waitTicks[i]); o.I32(_waitCount[i]); o.I64(_holdFaceMs[i]); o.I32(_leftInLine[i]); o.I32(_released[i]);
                o.I32(_lineCount[i]);
                for (int k = 0; k < _lineCount[i]; k++)
                {
                    int at = i * Pacing.MaxLine + (_lineHead[i] + k) % Pacing.MaxLine;
                    o.I64(_lineJoin[at]); o.I32(_lineSkier[at]);
                }
                o.I32(_rideCount[i]);
                for (int k = 0; k < _rideCount[i]; k++)
                {
                    int at = i * Pacing.MaxLine + (_rideHead[i] + k) % Pacing.MaxLine;
                    o.I64(_rideExit[at]); o.I32(_rideSkier[at]);
                }
                o.I32(_occSlots[i]);
                for (int k = 0; k < _occSlots[i]; k++) o.U8(_occ[_occOffset[i] + k]);
            }
            var r = _lastReport;
            o.I32(r.LiftCount);
            for (int i = 0; i < r.LiftCount; i++)
            {
                o.I32(r.PeakLine[i]); o.I32(r.MeanWaitMs[i]); o.I32(r.HoldMinutes[i]); o.I32(r.LeftInLine[i]); o.I32(r.UtilisationPermille[i]);
            }
            o.I32(_trailReached.Length);
            for (int k = 0; k < _trailReached.Length; k++) o.U8(_trailReached[k]);
        }

        /// <summary>Reads v2's block over arrays <see cref="FoldLiftOps"/> just sized for the saved lifts.</summary>
        void ReadLiftState(SidecarReader r)
        {
            _gustOverrideMph = r.I32();
            if (_gustOverrideMph < -1 || _gustOverrideMph > 500) throw new InvalidDataException("The gust override is out of range.");
            int n = r.Count(Pacing.MaxLifts);
            if (n != _liftCount) throw new InvalidDataException("The lift operations don't match the lifts.");
            for (int i = 0; i < n; i++)
            {
                _weatherGustMph[i] = r.I32();
                if (_weatherGustMph[i] < 0 || _weatherGustMph[i] > 500) throw new InvalidDataException("A top-station gust is out of range.");
                _downloading[i] = r.Enum(0, 1, "A lift's downloading flag"); _wayDown[i] = r.Enum(0, 1, "A lift's way down");
                _calmSince[i] = r.I64(); _feedPerMin[i] = r.I32(); _feedAcc[i] = r.I64(); _aboard[i] = r.I32();
                _rides[i] = r.I32(); _peakLine[i] = r.I32(); _waitTicks[i] = r.I64(); _waitCount[i] = r.I32(); _holdFaceMs[i] = r.I64();
                _leftInLine[i] = r.I32(); _released[i] = r.I32();
                _lineHead[i] = 0;
                _lineCount[i] = r.Count(Pacing.MaxLine);
                for (int k = 0; k < _lineCount[i]; k++) { _lineJoin[i * Pacing.MaxLine + k] = r.I64(); _lineSkier[i * Pacing.MaxLine + k] = r.I32(); }
                _rideHead[i] = 0;
                _rideCount[i] = r.Count(Pacing.MaxLine);
                for (int k = 0; k < _rideCount[i]; k++) { _rideExit[i * Pacing.MaxLine + k] = r.I64(); _rideSkier[i * Pacing.MaxLine + k] = r.I32(); }
                int slots = r.Count(1 << 16);
                if (slots != _occSlots[i]) throw new InvalidDataException("A lift's carriers don't match its design.");
                for (int k = 0; k < slots; k++) _occ[_occOffset[i] + k] = r.U8();
            }
            var rep = _lastReport;
            int m = r.Count(Pacing.MaxLifts);
            if (m != rep.LiftCount) throw new InvalidDataException("The day report's lifts don't match.");
            for (int i = 0; i < m; i++)
            {
                rep.PeakLine[i] = r.I32(); rep.MeanWaitMs[i] = r.I32(); rep.HoldMinutes[i] = r.I32(); rep.LeftInLine[i] = r.I32(); rep.UtilisationPermille[i] = r.I32();
            }
            int t = r.Count(8192);
            if (t != _trailReached.Length) throw new InvalidDataException("The trails don't match the design's network.");
            for (int k = 0; k < t; k++) _trailReached[k] = r.Enum(0, 1, "A trail's reach");
        }
    }
}
