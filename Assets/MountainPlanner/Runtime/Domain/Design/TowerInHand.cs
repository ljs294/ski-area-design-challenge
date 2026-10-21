using System;
using System.Collections.Generic;
using MountainPlanner.Domain.Edits;

namespace MountainPlanner.Domain.Design
{
    /// <summary>
    /// Towers by hand (LF-04, task 09): the tower in hand slides up the line above the last placed tower (or the bottom
    /// terminal). This answers the tool's questions about it, in Domain and deterministic:
    /// - <see cref="Evaluate"/>: TW-13's status line for the tower at one place and height (OK or the red reason, its head
    ///   and TW-11 loads, the lowest chair clearance in the span behind it, how far the next tower can reach);
    /// - <see cref="Band"/>: TW-09's green band, every 5 ft where it could stand at its height.
    /// The span past the tower in hand isn't strung yet, so its own head is provisional: as if the rope ran on straight to
    /// the top terminal.
    /// </summary>
    public sealed class TowerInHand
    {
        public sealed class Status
        {
            public bool Ok => Problem == null;
            /// <summary>The red reason, with its rule, or null (LC-09: clearance, spacing, height).</summary>
            public LiftProblem? Problem { get; internal set; }
            /// <summary>An amber warning, with its rule, or null (LC-09: a head over its limits, the rope's angle into the
            /// station). It doesn't stop the tower being placed.</summary>
            public LiftProblem? Warning { get; internal set; }
            /// <summary>The head it would get (provisional) and what it carries.</summary>
            public LiftRope.Head Head { get; internal set; }
            public TowerForces.Loads Loads { get; internal set; }
            /// <summary>The lowest chair clearance in the span behind it, metres.</summary>
            public double LowestClearance { get; internal set; }
            /// <summary>TW-13: how far the next span can reach (horizontal metres) before the chairs drop below 15 ft, with a
            /// next tower as tall as this one, and with a 100 ft one.</summary>
            public double NextReach { get; internal set; }
            public double NextReachTallest { get; internal set; }
            /// <summary>TW-13: the rope will pull up within that reach (the ground rises faster than the rope can follow).</summary>
            public bool HoldDownsAhead { get; internal set; }
            /// <summary>F4: how it stands (its lean down the line, TW-14, radians; its foot's elevation, metres), for its ghost.</summary>
            public double Lean { get; internal set; }
            public double BaseY { get; internal set; }
        }

        readonly LiftType _type;
        readonly IHeightSource _ground;
        readonly LineGround _line;
        readonly double _bx, _by, _tx, _ty, _ux, _uy, _length;
        readonly LiftLevels _levels;
        readonly TowerStance _stance;
        readonly List<Tower> _placed;
        readonly LiftRope _strung;   // the placed towers alone, strung to the top terminal

        /// <param name="placed">The towers placed so far, all below the tower in hand.</param>
        public TowerInHand(LiftType type, IHeightSource ground, double bx, double by, double tx, double ty, LiftLevels levels,
                           IReadOnlyList<Tower> placed, TowerStance stance, LineGround line = null)
        {
            _type = type;
            _ground = ground;
            _bx = bx; _by = by; _tx = tx; _ty = ty;
            _length = Math.Sqrt((tx - bx) * (tx - bx) + (ty - by) * (ty - by));
            _ux = (tx - bx) / _length;
            _uy = (ty - by) / _length;
            _levels = levels;
            _stance = stance;
            _line = line ?? LineGround.Sample(ground, type, bx, by, tx, ty);
            _placed = new List<Tower>(placed ?? Array.Empty<Tower>());
            _placed.Sort((a, b) => a.AlongMm.CompareTo(b.AlongMm));
            _strung = Solve(_placed);
        }

        /// <summary>The placed towers alone, the rope strung from the last of them to the top terminal (Fill the rest's done
        /// test: the line is complete when this clears the ground).</summary>
        public LiftRope Strung => _strung;

        /// <summary>The terminal levels the tower in hand stands against (the profile strip).</summary>
        public LiftLevels Levels => _levels;

        double ExitY => _levels.BottomMetres + _type.Bottom.ExitRise;
        double TopExitY => _levels.TopMetres + _type.Top.ExitRise;
        double GroundAt(double s) => _ground.At(_bx + _ux * s, _by + _uy * s);

        LiftRope Solve(IReadOnlyList<Tower> towers) =>
            LiftRope.Solve(_type, _ground, _bx, _by, _tx, _ty, towers, _stance, _type.Bottom.ExitAlong, ExitY, _type.Top.ExitAlong, TopExitY, _line);

        /// <summary>Where the tower in hand may start: 50 ft past the last placed tower (TW-03), or the bottom station zone's
        /// first slot past the terminal's front (TW-03 v2's station tower).</summary>
        public double FirstAlong
        {
            get
            {
                if (_placed.Count > 0) return _placed[_placed.Count - 1].AlongMm / 1000.0 + DesignUnits.TowerGapMm / 1000.0;
                return Math.Ceiling((_type.Bottom.Ahead + DesignUnits.StationPastFrontMm / 1000.0) / 1.524 - 1e-9) * 1.524;
            }
        }

        /// <summary>TW-13 for the tower in hand at <paramref name="along"/> metres up the line, <paramref name="heightMm"/> tall.</summary>
        public Status Evaluate(double along, int heightMm)
        {
            var status = new Status();
            int alongMm = (int)Math.Round(along * 1000);
            var towers = new List<Tower>(_placed) { new Tower(TowerId.Make(2, 0), alongMm, heightMm) };
            // TW-02, TW-03: the document's own rules first.
            long lengthSq = (long)Math.Round(_length * 1000) * (long)Math.Round(_length * 1000);
            string spacing = along < FirstAlong - 1e-6 ? "Too close to the tower below it: towers stand at least 50 ft apart"
                : along > _length - DesignUnits.TowerGapMm / 1000.0 ? "Too close to the top terminal" : null;
            string height = DesignRules.TowerHeightProblem(heightMm, DesignRules.InStationZone(lengthSq, alongMm), LiftCatalogIds.TowerMaxMm(_type.CatalogId));
            var rope = Solve(towers);
            int me = towers.Count - 1;
            var sol = rope.Towers[me];
            status.Head = sol.Head;
            status.Loads = sol.Loads;
            status.Lean = sol.Lean;
            status.BaseY = sol.BaseY;
            // The span behind it, TW-01 v2.
            // The trench (GR-11 v2) digs the first span, and past the bottom station tower up to 15 ft before the next (as Reach).
            double from = me > 0 ? rope.Towers[me - 1].RopeS : 0, lowest = double.MaxValue, worst = 0, worstS = 0, over = 0;
            for (double s = Math.Ceiling(from); s <= sol.RopeS; s += 1)
                if (rope.ClearanceAt(s, out double clear, out double need))
                {
                    lowest = Math.Min(lowest, clear);
                    if (need - clear > worst) { worst = need - clear; worstS = s; }
                    double allow = me == 0 || (me == 1 && s < sol.RopeS - LiftLineEnvelope.TowerKeepMetres) ? LiftLineEnvelope.CapMetres : LiftRope.ClearanceTolerance;
                    over = Math.Max(over, need - clear - allow);
                }
            status.LowestClearance = lowest == double.MaxValue ? LiftRope.Clearance : lowest;
            // A placed tower made too sharp by this one (TW-06), or the bottom terminal's row out of range (TW-16 v3).
            string sharp = null;
            for (int i = 0; i < me; i++)
                if (rope.Towers[i].Head == LiftRope.Head.TooSharp)
                    sharp = rope.Towers[i].BendLoaded > 0 ? "Makes the tower below too sharp a hold-down: add one before the rise"
                                                          : "Makes the tower below too sharp a breakover: add one on the crest";
            bool rowOk = _type.Bottom.Takes(rope.BottomExitLoaded) && _type.Bottom.Takes(rope.BottomExitEmpty);
            if (spacing != null) status.Problem = new LiftProblem("TW-03", spacing, along);
            else if (height != null) status.Problem = new LiftProblem("TW-02", height, along);
            // The first span's shortfall, up to GR-11 v2's 10 ft, is the trench's to dig at Break ground (the lift-line
            // envelope, as on an automatic lift).
            else if (over > 0)
                status.Problem = new LiftProblem("TW-01", $"The chairs pass {Ft1(LiftRope.Clearance - worst)} over the ground at {Ft(worstS)}; they need 15 ft", worstS);
            if (sharp != null) status.Warning = new LiftProblem("TW-06", sharp, along, false);
            else if (me == 0 && !rowOk)
                status.Warning = new LiftProblem("TW-16", "The rope would leave the bottom terminal outside its row's range", 0, false);
            // TW-13's next reach, at this height and at 100 ft.
            // Past the bottom station tower the trench still reaches, up to 15 ft before the next tower (GR-11 v2).
            double slack = me == 0 ? LiftLineEnvelope.CapMetres : 0;
            // "At this height": a station tower's own (from 15 ft) says nothing of the line tower after it, so that reads
            // TW-12's modest 35 ft.
            bool station = DesignRules.InStationZone(lengthSq, alongMm);
            int nextHeight = station ? DesignUnits.FeetToMm(35) : Math.Max(heightMm, DesignUnits.TowerMinMm);
            status.NextReach = Reach(sol, nextHeight / 1000.0, slack, out bool upA);
            status.NextReachTallest = Reach(sol, _type.TowerMaxMetres, slack, out bool upB);
            status.HoldDownsAhead = upA;
            // Its own head, provisionally: as if the next tower stood as far up as it can reach, as tall as this one.
            if (status.NextReach > 0)
            {
                int nextMm = (int)Math.Round((along + status.NextReach) * 1000 / DesignUnits.FiveFeetMm) * DesignUnits.FiveFeetMm;
                if (nextMm > alongMm && nextMm < (long)Math.Round(_length * 1000) - DesignUnits.TowerGapMm)
                {
                    var ahead = new List<Tower>(towers) { new Tower(TowerId.Make(2, 1), nextMm, Math.Max(heightMm, DesignUnits.TowerMinMm)) };
                    var provisional = Solve(ahead).Towers[me];
                    status.Head = provisional.Head;
                    status.Loads = provisional.Loads;
                }
            }
            return status;
        }

        /// <summary>
        /// How far a span from <paramref name="here"/> can reach with the next tower <paramref name="nextHeight"/> tall with its
        /// chairs clear of the ground (the furthest such place, in 5 ft steps up to 1,000 ft); <paramref name="pullsUp"/> where, within that reach, the
        /// rope over this tower would turn up (the ground rising past it).
        /// </summary>
        double Reach(LiftRope.TowerSolution here, double nextHeight, double trench, out bool pullsUp)
        {
            pullsUp = false;
            double t0 = _type.CounterweightNewtons, wl = _type.LoadedNewtonsPerMetre, we = _type.EmptyNewtonsPerMetre, drop = _type.ChairDropMetres;
            double best = 0;
            double incoming = here.RopeS > 0 ? Math.Atan2(here.RopeY - ExitY, here.RopeS - _type.Bottom.ExitAlong) : 0;
            for (double d = 15.24; d <= 304.8 + 1e-6; d += 1.524)
            {
                double s2 = here.RopeS + d;
                if (s2 > _length - _type.Top.ExitAlong) break;
                double y2 = GroundAt(s2) + nextHeight;
                var loaded = LiftRope.Catenary(here.RopeS, here.RopeY, s2, y2, t0, wl, ExitY);
                var empty = LiftRope.Catenary(here.RopeS, here.RopeY, s2, y2, t0, we, ExitY);
                bool clear = true;
                for (double s = Math.Ceiling(here.RopeS); s <= s2 && clear; s += 1)
                {
                    int j = (int)Math.Min(_line.Count - 1, Math.Max(0, Math.Round(s)));
                    double chairs = Math.Min(loaded.Y(s), empty.Y(s)) - drop;
                    double slack = s < s2 - LiftLineEnvelope.TowerKeepMetres ? trench : 0;
                    if (chairs - Math.Max(_line.UnderUp[j], _line.UnderDown[j]) < Need(s) - LiftRope.ClearanceTolerance - slack) clear = false;
                }
                if (!clear) continue;   // a longer span may still clear: the furthest one that does counts
                best = d;
                // The rope over this tower turns up when the span ahead leaves steeper than the one arriving.
                if (Math.Atan(empty.Slope(here.RopeS)) > incoming + 1e-6 && nextHeight < _type.TowerMaxMetres - 1e-6) pullsUp = true;
            }
            return best;
        }

        /// <summary>
        /// TW-09: every 5 ft from <see cref="FirstAlong"/> up the line where a tower <paramref name="heightMm"/> tall could stand
        /// (green), as runs [from, to] in metres. Recomputed only when the height or a placed part changes.
        /// </summary>
        public List<(double From, double To)> Band(int heightMm)
        {
            var runs = new List<(double, double)>();
            double start = -1, end = 0;
            double last = _length - DesignUnits.TowerGapMm / 1000.0;
            for (double s = FirstAlong; s <= last + 1e-6; s += 1.524)
            {
                bool green = Green(s, heightMm);
                if (green)
                {
                    if (start < 0) start = s;
                    end = s;
                }
                else if (start >= 0) { runs.Add((start, end)); start = -1; }
            }
            if (start >= 0) runs.Add((start, end));
            return runs;
        }

        /// <summary>TW-09's quick test (proxy P7): the chairs' clearance in the span behind the candidate, plumb (only what
        /// blocks, LC-09); <see cref="Evaluate"/> confirms the place picked and adds the amber warnings.</summary>
        bool Green(double s, int heightMm)
        {
            double t0 = _type.CounterweightNewtons, wl = _type.LoadedNewtonsPerMetre, we = _type.EmptyNewtonsPerMetre, drop = _type.ChairDropMetres;
            double ps, py;
            if (_placed.Count > 0)
            {
                var prev = _strung.Towers[_strung.Towers.Count - 1];
                ps = prev.RopeS;
                py = prev.RopeY;
            }
            else
            {
                ps = _type.Bottom.ExitAlong;
                py = ExitY;
            }
            double y = GroundAt(s) + heightMm / 1000.0;
            var loaded = LiftRope.Catenary(ps, py, s, y, t0, wl, ExitY);
            var empty = LiftRope.Catenary(ps, py, s, y, t0, we, ExitY);
            LiftRope.Zones(_type, _length, out double frontB, out _, out _, out _);
            for (double x = Math.Ceiling(Math.Max(ps, frontB)); x <= s; x += 1)
            {
                int j = (int)Math.Min(_line.Count - 1, Math.Max(0, Math.Round(x)));
                double need = Need(x);
                double chairs = Math.Min(loaded.Y(x), empty.Y(x)) - drop;
                // The trench's (GR-11 v2): the first span, and past the bottom station tower up to 15 ft before the next (as Reach).
                double slack = _placed.Count == 0 || (_placed.Count == 1 && x < s - LiftLineEnvelope.TowerKeepMetres)
                    ? LiftLineEnvelope.CapMetres : LiftRope.ClearanceTolerance;
                if (chairs - Math.Max(_line.UnderUp[j], _line.UnderDown[j]) < need - slack) return false;
            }
            // Head loads and the rope's angle into the station are amber (LC-09, LT14): they don't make the band red.
            return true;
        }

        /// <summary>TW-01 v2's need at <paramref name="s"/>: 15 ft, rising from nothing across the bottom low zone (a proxy:
        /// the full check starts the rising line from the chairs' height over the pad).</summary>
        double Need(double s)
        {
            LiftRope.Zones(_type, _length, out double frontB, out double edgeB, out _, out _);
            return s < edgeB ? LiftRope.Clearance * Math.Max(0, (s - frontB) / (edgeB - frontB)) : LiftRope.Clearance;
        }

        static string Ft(double metres) => (metres / 0.3048).ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + " ft";
        static string Ft1(double metres) => (metres / 0.3048).ToString("N1", System.Globalization.CultureInfo.InvariantCulture) + " ft";
    }
}
