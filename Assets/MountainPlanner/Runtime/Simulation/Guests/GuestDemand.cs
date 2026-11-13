using System;

namespace MountainPlanner.Simulation
{
    /// <summary>
    /// GU-03's stack, line by line. The ids are stable and saved: the economy appends lines (a ticket price, marketing, an
    /// amenity's pull) and 4.17 renders whatever is there, so a new line is never a new rule. Phase 4 fills the first
    /// eight; the rest are reserved so their meaning is fixed before anything uses them.
    /// </summary>
    public enum DemandLine : ushort
    {
        None = 0,
        /// <summary>GU-03: the location's own potential, the skiers who would come on a peak day to a mountain they love.</summary>
        Draw = 1,
        /// <summary>GU-03: the share of the crowd this archetype is.</summary>
        Mix = 2,
        /// <summary>GU-18: what this archetype thinks of the mountain.</summary>
        Reputation = 3,
        /// <summary>TM-07: a weekday, a weekend day or a holiday.</summary>
        DayType = 4,
        /// <summary>GU-04: this morning's snow report.</summary>
        Conditions = 5,
        /// <summary>GU-03: the ticket window (1.0 until the economy arrives).</summary>
        Price = 6,
        /// <summary>WX-44: the overnight snow at mid-mountain.</summary>
        PowderPull = 7,
        /// <summary>WX-44: today's weather as the crowd saw it forecast last evening.</summary>
        Forecast = 8,
        /// <summary>GU-01: skiers who found the hill at its cap. Not a multiplier: the count it took off the top.</summary>
        Cap = 9,
        Marketing = 10,
        Amenities = 11,
        LocalPull = 12,
    }

    public sealed partial class SimWorld
    {
        /// <summary>How many lines the stack can hold (every value of <see cref="DemandLine"/>, by id).</summary>
        public const int DemandLines = 16;

        // ---- today's crowd (GU-03), per archetype ----
        int[] _demand = new int[Pacing.Archetypes];
        int[] _demandUncapped = new int[Pacing.Archetypes];
        int[] _issued = new int[Pacing.Archetypes];
        int[] _partyOrdinal = new int[Pacing.Archetypes];
        int[] _arrived = new int[Pacing.Archetypes];
        int[] _arrivedClosed = new int[Pacing.Archetypes];
        int[] _capped = new int[Pacing.Archetypes];
        int[] _wentHome = new int[Pacing.Archetypes];
        int[] _stranded = new int[Pacing.Archetypes];
        int[] _leftCount = new int[Pacing.Archetypes];
        long[] _leftSatSum = new long[Pacing.Archetypes];
        /// <summary>The factor each line put in, permille, per archetype (<see cref="DemandLine"/> by id).</summary>
        int[] _stack = new int[Pacing.Archetypes * DemandLines];
        int _partyCounter, _serialCounter;
        /// <summary>The face day the demand above was worked out for, so it is never planned twice or missed.</summary>
        int _demandDay = -1;

        /// <summary>GU-18: what each archetype thinks of the mountain, thousandths. Saved, hashed, in the season's history.</summary>
        int[] _rep = new int[Pacing.Archetypes];

        // ---- the arrival window (GU-06) ----
        long _arrivalFromTick;
        int _arrivalWindowTicks;
        long _arrivalWeightTotal;
        /// <summary>The tick the face day began: the day record's times are offsets from it.</summary>
        long _dayStartTick;

        // ---- WX-44's two lines, which 4.05 fills (the seam) ----
        int _powderPull = 1000, _forecastPull = 1000;

        /// <summary>WX-44: 4.05 sets this morning's pull from the overnight snow and the evening's forecast, permille.</summary>
        public void SetWeatherPull(int powderPermille, int forecastPermille)
        {
            _powderPull = Math.Max(0, Math.Min(10_000, powderPermille));
            _forecastPull = Math.Max(0, Math.Min(10_000, forecastPermille));
        }

        /// <summary>GU-18: an archetype's reputation, thousandths.</summary>
        public int ReputationOf(int archetype) => archetype >= 0 && archetype < Pacing.Archetypes ? _rep[archetype] : 0;

        /// <summary>GU-18: the headline reputation, the mix-weighted mean, thousandths.</summary>
        public int Reputation
        {
            get
            {
                long sum = 0, weight = 0;
                for (int a = 0; a < Pacing.Archetypes; a++) { sum += (long)_rep[a] * Pacing.ArchetypeMix[a]; weight += Pacing.ArchetypeMix[a]; }
                return weight == 0 ? 0 : (int)(sum / weight);
            }
        }

        /// <summary>GU-03: today's demand for an archetype, and what each line of the stack did to it (4.17's tab).</summary>
        public int DemandOf(int archetype) => archetype >= 0 && archetype < Pacing.Archetypes ? _demandUncapped[archetype] : 0;
        public int StackLine(int archetype, DemandLine line) =>
            archetype >= 0 && archetype < Pacing.Archetypes && (int)line < DemandLines ? _stack[archetype * DemandLines + (int)line] : 0;

        /// <summary>The day's guest ledger (SI-10 balances it; TM-15 reports it).</summary>
        public (int demand, int arrived, int arrivedClosed, int capped, int wentHome, int stranded, int left) GuestLedger()
        {
            int demand = 0, arrived = 0, closed = 0, capped = 0, home = 0, strand = 0, left = 0;
            for (int a = 0; a < Pacing.Archetypes; a++)
            {
                demand += _demandUncapped[a]; arrived += _arrived[a]; closed += _arrivedClosed[a]; capped += _capped[a];
                home += _wentHome[a]; strand += _stranded[a]; left += _leftCount[a];
            }
            return (demand, arrived, closed, capped, home, strand, left);
        }

        /// <summary>GU-18: an archetype's arrivals today and its mean end-of-day satisfaction, thousandths.</summary>
        public (int arrived, int meanSat) VerdictOf(int archetype)
        {
            if (archetype < 0 || archetype >= Pacing.Archetypes) return (0, 0);
            int n = _leftCount[archetype];
            return (_arrived[archetype], n == 0 ? 0 : (int)(_leftSatSum[archetype] / n));
        }

        // ================= the day's crowd =================

        /// <summary>
        /// GU-03 v4: the day's demand, archetype by archetype, an hour before opening (where WX-44 puts the morning read).
        /// It is **demand, never capacity**: nothing about the lifts or the open acres sizes it. The player's answer to it
        /// is what they build, and the strain shows up as lines, crowding, lost satisfaction and, tomorrow, reputation.
        /// </summary>
        void PlanTheDay(int faceDay)
        {
            WeatherAtLanding(faceDay);   // WX-44 (4.05 PR C): the night's snow and the evening's forecast set the two weather lines
            _demandDay = faceDay;
            Array.Clear(_demand, 0, _demand.Length);
            Array.Clear(_demandUncapped, 0, _demandUncapped.Length);
            Array.Clear(_issued, 0, _issued.Length);
            Array.Clear(_partyOrdinal, 0, _partyOrdinal.Length);
            Array.Clear(_arrived, 0, _arrived.Length);
            Array.Clear(_arrivedClosed, 0, _arrivedClosed.Length);
            Array.Clear(_capped, 0, _capped.Length);
            Array.Clear(_wentHome, 0, _wentHome.Length);
            Array.Clear(_stranded, 0, _stranded.Length);
            Array.Clear(_leftCount, 0, _leftCount.Length);
            Array.Clear(_leftSatSum, 0, _leftSatSum.Length);
            Array.Clear(_stack, 0, _stack.Length);
            // The day's counters start over. Popularity keeps yesterday's figures until the first skier of today moves
            // one, because UpdatePopularity leaves them alone while every counter is still zero.
            ResetGuestLedger();
            ArrivalWindow();
            int conditions = ConditionsFactor();
            var type = Calendar.TypeOf(faceDay % Calendar.YearFaceDays);
            int dayType = Pacing.DayTypeDemand[type == DayType.Holiday ? 2 : type == DayType.Weekend ? 1 : 0];
            for (int a = 0; a < Pacing.Archetypes; a++)
            {
                int rep = Pacing.RepFloor + (1000 - Pacing.RepFloor) * _rep[a] / 1000;
                int at = a * DemandLines;
                _stack[at + (int)DemandLine.Draw] = 1000;
                _stack[at + (int)DemandLine.Mix] = Pacing.ArchetypeMix[a];
                _stack[at + (int)DemandLine.Reputation] = rep;
                _stack[at + (int)DemandLine.DayType] = dayType;
                _stack[at + (int)DemandLine.Conditions] = conditions;
                _stack[at + (int)DemandLine.Price] = 1000;
                _stack[at + (int)DemandLine.PowderPull] = _powderPull;
                _stack[at + (int)DemandLine.Forecast] = _forecastPull;
                long v = Pacing.LocationDraw;
                v = v * Pacing.ArchetypeMix[a] / 1000;
                v = v * rep / 1000;
                v = v * dayType / 1000;
                v = v * conditions / 1000;
                v = v * _powderPull / 1000;
                v = v * _forecastPull / 1000;
                _demandUncapped[a] = (int)Math.Max(0, Math.Min(int.MaxValue, v));
                _demand[a] = _demandUncapped[a];
            }
            Events.Add(Tick, SimEventType.DayDemand, -1, _demandUncapped[0] + _demandUncapped[1] + _demandUncapped[2] + _demandUncapped[3]);
        }

        /// <summary>
        /// GU-04: today's conditions as the crowd reads them this morning: how much of the mountain is open, and how good
        /// the snow is. 4.07 fills the quality; until then every open trail reads the stand-in.
        /// </summary>
        int ConditionsFactor()
        {
            int built = Design.BuiltTrails;
            int open = 0;
            var net = _guestsNetwork;
            if (net != null)
                for (int k = 0; k < net.TrailIds.Length; k++) if (TrailOpen(k)) open++;
            int openShare = built <= 0 ? 0 : (int)Math.Min(1000L, open * 1000L / built);
            // 0.5 + 0.5 × share × sqrt(quality ÷ 0.9), clamped 0.3–1.2. An integer root keeps it off the floats.
            int quality = StandInQuality;
            int ratio = quality * 1000 / 900;
            int root = Isqrt(ratio * 1000);                    // sqrt in thousandths
            int v = 500 + 500 * openShare / 1000 * root / 1000;
            return Math.Max(300, Math.Min(1200, v));
        }

        /// <summary>An integer square root (SI-09: no transcendental on a path the goldens depend on).</summary>
        internal static int Isqrt(long v)
        {
            if (v <= 0) return 0;
            long x = v, y = (x + 1) / 2;
            while (y < x) { x = y; y = (x + v / x) / 2; }
            return (int)x;
        }

        /// <summary>
        /// GU-06: the arrival window, from opening to <see cref="Pacing.ArrivalEndsBeforeLastChairMinutes"/> before last
        /// chair, in equal parts with the morning's weights. A player who moves the hours gets the same shape.
        /// </summary>
        void ArrivalWindow()
        {
            long open = _todayOpen * 1000L, end = _todayLastChair * 1000L - Pacing.ArrivalEndsBeforeLastChairMinutes * 60_000L;
            if (end <= open) { _arrivalWindowTicks = 0; _arrivalWeightTotal = 0; return; }
            int ticksPerHour = Pacing.TicksPerFaceHour(true);
            long ms = end - open;
            _arrivalWindowTicks = (int)Math.Max(1, ms * ticksPerHour / 3_600_000);
            // The anchor is set at the opening (AnchorArrivals), not here: the landing an hour earlier runs at the night
            // rate, so there is no honest way to count the ticks from here to the opening.
            _arrivalFromTick = long.MaxValue;
            _arrivalWeightTotal = 0;
            int parts = Pacing.ArrivalWeights.Length;
            for (int h = 0; h < parts; h++) _arrivalWeightTotal += (long)Pacing.ArrivalWeights[h] * PartTicks(h);
            if (_arrivalWeightTotal <= 0) _arrivalWindowTicks = 0;
        }

        /// <summary>How many ticks the <paramref name="part"/>th equal part of the window covers.</summary>
        int PartTicks(int part)
        {
            int parts = Pacing.ArrivalWeights.Length;
            long a = (long)_arrivalWindowTicks * part / parts, b = (long)_arrivalWindowTicks * (part + 1) / parts;
            return (int)(b - a);
        }

        /// <summary>GU-06: the arrival window starts now, at the opening, where the day rate starts (TM-02).</summary>
        void AnchorArrivals() => _arrivalFromTick = Tick;

        /// <summary>GU-06: whether any party is still due today (SI-11: the step then stays at one tick).</summary>
        bool ArrivalsDueToday
        {
            get
            {
                if (_arrivalWindowTicks <= 0 || _arrivalFromTick == long.MaxValue) return false;
                if (Tick > _arrivalFromTick + _arrivalWindowTicks) return false;
                for (int a = 0; a < Pacing.Archetypes; a++) if (_issued[a] < _demand[a]) return true;
                return false;
            }
        }

        /// <summary>The weight of the window's first <paramref name="ticks"/> ticks (GU-06's exact schedule).</summary>
        long WeightSoFar(long ticks)
        {
            long w = 0;
            int parts = Pacing.ArrivalWeights.Length;
            long at = 0;
            for (int h = 0; h < parts; h++)
            {
                int len = PartTicks(h);
                if (ticks <= at) break;
                long inThis = Math.Min(len, ticks - at);
                w += (long)Pacing.ArrivalWeights[h] * inThis;
                at += len;
            }
            return w;
        }

        // ================= reputation (GU-18) =================

        /// <summary>
        /// GU-18: at the stop each archetype with at least <see cref="Pacing.RepMinSample"/> arrivals moves toward the
        /// day's verdict. A good week lifts one by about 0.3, a bad one drops it as fast, so a mountain with no green
        /// terrain loses its beginners over a fortnight rather than overnight. An archetype that didn't come doesn't move.
        /// </summary>
        void UpdateReputation()
        {
            for (int a = 0; a < Pacing.Archetypes; a++)
            {
                if (_arrived[a] < Pacing.RepMinSample || _leftCount[a] == 0) continue;
                int mean = (int)(_leftSatSum[a] / _leftCount[a]);
                int verdict = mean <= Pacing.RepSatLow ? 0
                    : mean >= Pacing.RepSatHigh ? 1000
                    : (mean - Pacing.RepSatLow) * 1000 / (Pacing.RepSatHigh - Pacing.RepSatLow);
                _rep[a] += (verdict - _rep[a]) * Pacing.RepRate / 1000;
                if (_rep[a] < 0) _rep[a] = 0; else if (_rep[a] > 1000) _rep[a] = 1000;
            }
        }

        // ================= the capacity the player is building against (GU-03) =================

        /// <summary>GU-03: how many skiers today's open lifts could carry at four laps each, read-only beside the demand.</summary>
        public int LiftCapacityToday()
        {
            long boardings = 0;
            for (int i = 0; i < LiftsNow; i++)
            {
                if (!_liftBuilt[i] || _liftOpen[i] == 0) continue;
                long dayRealMs = (long)(_lStop[i] - _lOpen[i]) * Pacing.DayHourRealSeconds * 1000L / 3600;
                boardings += dayRealMs / Math.Max(1, _intervalMs[i]) * Design.Lifts[i].Seats;
            }
            return (int)Math.Min(int.MaxValue, boardings / Math.Max(1, Pacing.LapsTarget));
        }

        /// <summary>GU-03: how many skiers the open trails hold at a comfortable density, read-only beside the demand.</summary>
        public int TerrainCapacityToday()
        {
            var net = _guestsNetwork;
            if (net == null) return 0;
            long m2 = 0;
            for (int e = 0; e < net.EdgeFrom.Length; e++)
            {
                if (net.EdgeKindOf[e] != (byte)SimEdgeKind.TrailSegment) continue;
                int t = net.EdgeTrail[e];
                if (!TrailOpen(t)) continue;
                m2 += net.EdgeAreaM2[e];
            }
            return (int)Math.Min(int.MaxValue, m2 * Pacing.SkiersPerOpenAcre / 4047);
        }
    }
}
