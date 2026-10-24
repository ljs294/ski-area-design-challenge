using System;
using System.Collections.Generic;
using MountainPlanner.Domain.Edits;

namespace MountainPlanner.Domain.Design
{
    /// <summary>
    /// A simple stand-in for TW-12 (task 09 writes the real one), with TW-16 v3 (owner, task 07 round 3, SK21–SK26):
    /// 1. **Station towers first**, one by each terminal, carrying the rope where that terminal needs it: a hold-down row's
    ///    exit at its aim (14.3° on the Sessellift return), a support row's level last span (the Sessellift drive). Their steel
    ///    comes in 5 ft steps from 15 ft and the foundation shows the rest (TW-02 v3). The bottom one stands at the first
    ///    slot where the chairs have climbed to 15 ft without falling short of TW-01 v2's low zone; the top one at the
    ///    closest slot where it's 15 ft tall. Both stay within the 100 ft station zone (TW-03 v3).
    /// 2. **Line towers planned together** (TW-12 v2, LT45, the owner's findings E2–E4): the fewest towers whose spans keep
    ///    the chairs up and whose every head, the station towers' too, carries its bend, taller towers before more of them
    ///    (<see cref="PlanLine"/>); evenly spaced at most 250 ft apart where no plan fits.
    /// 3. **Only adding or raising:** a head too sharp gets a TW-06 partner; a span whose chairs fall short of TW-01 v2 gets
    ///    a taller or an added line tower, up to TW-02's 100 ft before another is added. Station spans are left to the
    ///    lift-line envelope (the bottom) and the unload ramp (the top), which <see cref="LiftBuild"/> applies with the
    ///    results here.
    /// Positions are on the 5 ft grid (TW-12).
    /// </summary>
    public static class TowerPlacement
    {
        /// <summary>Why the search stopped with the chairs short or a head too sharp (TW-12, LT40).</summary>
        public enum GiveUpKind : byte
        {
            /// <summary>The towers beside the low point are at TW-02's 100 ft (a tram's 250 ft), or none there may be raised.</summary>
            TowerLimit,
            /// <summary>A head too sharp with no room for a TW-06 partner, and raising its neighbours no longer eases it.</summary>
            TooSharp,
            /// <summary>The search ran out of rounds.</summary>
            Rounds,
        }

        /// <summary>One place the search gave up (s along the line, metres).</summary>
        public readonly struct GiveUp
        {
            public readonly double S;
            public readonly GiveUpKind Kind;
            public GiveUp(double s, GiveUpKind kind) { S = s; Kind = kind; }
            public override string ToString() => $"{Kind} at {S:F0} m";
        }

        public sealed class Result
        {
            public List<TowerSpec> Towers { get; } = new List<TowerSpec>();
            /// <summary>Where and why the search stopped short of the rules (TW-12, LT40), in the order it did; empty when it
            /// finished. Stretches left to the envelope and the unload ramp aren't here: LT34's level hints cover them.</summary>
            public List<GiveUp> GaveUp { get; } = new List<GiveUp>();
            public LiftRope Rope { get; internal set; }
            /// <summary>Where each station tower stands (s along the line, metres), where it carries the rope (elevation, metres)
            /// and how it leans (radians), as solved.</summary>
            public double BottomStationS { get; internal set; }
            public double BottomStationY { get; internal set; }
            public double BottomStationLean { get; internal set; }
            public double TopStationS { get; internal set; }
            public double TopStationY { get; internal set; }
            public double TopStationLean { get; internal set; }
            /// <summary>How deep the ground under the bottom station tower must come down for its 15 ft of steel to carry the
            /// rope where the terminal needs it (a pit, GR-11 v2), metres; 0 almost always.</summary>
            public double BottomPit { get; internal set; }
            /// <summary>How much higher the top terminal must stand on a built-up pad (TW-16 v3's unload ramp, owner Q6) for
            /// its level last span to clear the ground, metres, in 1 ft steps; 0 where the hill falls away.</summary>
            public double TopRaise { get; internal set; }
        }

        const double Grid = 1.524, LineHeight = 10.668, StationDefault = 12.192, PastFront = 4.572,
                     MaxSpan = 76.2, MinGap = 15.24, Tolerance = LiftRope.ClearanceTolerance;

        /// <summary>
        /// Places towers on the line from <paramref name="bottom"/> to <paramref name="top"/> (bullwheels, in the package's
        /// frame) over <paramref name="ground"/> (with the pads graded). <paramref name="bottomLevel"/> and
        /// <paramref name="topLevel"/> are the terminals' base elevations (their load and unload levels).
        /// </summary>
        /// <summary>
        /// Where a station tower at <paramref name="s"/> must carry the rope for the empty rope to leave its terminal at the
        /// end's aim (TW-16 v3; plumb, the solve's foundation reveal settles the lean): <paramref name="bottom"/> for the
        /// bottom end, whose rope leaves at (<paramref name="exitS"/>, <paramref name="exitY"/>); else the top, reached at
        /// (<paramref name="topExitS"/>, <paramref name="topExitY"/>). Tension is RP-03's from the bottom exit.
        /// </summary>
        public static double AimedY(LiftType type, bool bottom, double s, double exitS, double exitY, double topExitS, double topExitY)
        {
            double t0 = type.CounterweightNewtons, we = type.EmptyNewtonsPerMetre;
            double target = bottom ? Math.Tan(type.Bottom.Aim) : -Math.Tan(type.Top.Aim);
            double lo = (bottom ? exitY : topExitY) - 60, hi = (bottom ? exitY : topExitY) + 120;
            for (int k = 0; k < 60; k++)
            {
                double mid = (lo + hi) / 2;
                double slope = bottom
                    ? LiftRope.Catenary(exitS, exitY, s, mid, t0, we, exitY).Slope(exitS)
                    : LiftRope.Catenary(s, mid, topExitS, topExitY, t0, we, exitY).Slope(topExitS);
                // The bottom exit steepens as the point rises; the top arrival flattens.
                if (bottom == slope < target) lo = mid; else hi = mid;
            }
            return (lo + hi) / 2;
        }

        public static Result Place(LiftType type, IHeightSource ground, double bx, double by, double tx, double ty,
                                   double bottomLevel, double topLevel, TowerStance stance)
        {
            double length = Math.Sqrt((tx - bx) * (tx - bx) + (ty - by) * (ty - by));
            double ux = (tx - bx) / length, uy = (ty - by) / length;
            double GroundAt(double s) => ground.At(bx + ux * s, by + uy * s);
            double Snap(double v) => Math.Round(v / Grid) * Grid;
            double Floor5(double v) => Math.Floor(v / Grid + 1e-9) * Grid;
            var line = LineGround.Sample(ground, type, bx, by, tx, ty);
            double stationMin = DesignUnits.StationTowerMinMm / 1000.0, zone = DesignUnits.StationZoneMm / 1000.0;
            double t0 = type.CounterweightNewtons, wl = type.LoadedNewtonsPerMetre, we = type.EmptyNewtonsPerMetre;
            double drop = type.ChairDropMetres;
            double exitS = type.Bottom.ExitAlong, exitY = bottomLevel + type.Bottom.ExitRise;
            double topBack = type.Top.ExitAlong, topExitS = length - topBack, topExitY = topLevel + type.Top.ExitRise;
            LiftRope.Zones(type, length, out double frontB, out double edgeB, out double edgeT, out double frontT);
            var result = new Result();
            double MaxHeight = type.TowerMaxMetres;   // TW-02: 100 ft; a tram's 250 ft (LC-08)

            double Aimed(bool bottom, double s) => AimedY(type, bottom, s, exitS, exitY, topExitS, topExitY);
            // TW-01 v2 along a station span (from the terminal's front to the tower), both load cases: how far the chairs
            // fall short of the low zone's rising line at worst.
            double SpanShortfall(bool bottom, double s, double y)
            {
                var loaded = bottom ? LiftRope.Catenary(exitS, exitY, s, y, t0, wl, exitY) : LiftRope.Catenary(s, y, topExitS, topExitY, t0, wl, exitY);
                var empty = bottom ? LiftRope.Catenary(exitS, exitY, s, y, t0, we, exitY) : LiftRope.Catenary(s, y, topExitS, topExitY, t0, we, exitY);
                double front = bottom ? frontB : frontT, edge = bottom ? edgeB : edgeT;
                double Clear(double x) => Math.Min(loaded.Y(x), empty.Y(x)) - drop - line.UnderChairs(x);
                double start = bottom ? Math.Ceiling(front) : Math.Floor(front);
                double level = bottom ? exitY - type.Bottom.ExitRise : topExitY - type.Top.ExitRise;
                double c0 = Math.Max(0, Math.Min(LiftRope.Clearance, Math.Min(loaded.Y(start), empty.Y(start)) - drop - level));
                double worst = 0;
                double from = bottom ? start : Math.Ceiling(s), to = bottom ? Math.Floor(s) : start;
                for (double x = from; x <= to; x += 1)
                {
                    double need = LiftRope.Clearance;
                    double f = bottom ? (x - start) / (edge - start) : (start - x) / (start - edge);
                    if (f < 1) need = Math.Min(need, c0 + (LiftRope.Clearance - c0) * Math.Max(0, f));
                    worst = Math.Max(worst, need - Clear(x));
                }
                return worst;
            }

            // 1. The bottom station tower: the first slot where its steel is 15 ft or more, the chairs never fall short on the
            //    way and reach 15 ft at the tower; else the slot with the most clearance at the tower; else the closest, on a pit.
            //    TW-16 v4 (LT32): a terminal with its own station (the Monta drive) has none; the line starts at its exit.
            bool ownB = type.Bottom.OwnStation;
            int stationIdx = ownB ? -1 : 0;   // the bottom station tower's index in the line (none: -1)
            double sb = -1, yb = 0, bestClear = double.MinValue;
            double first = Math.Ceiling(Math.Max(StationDefault, type.Bottom.Ahead + PastFront) / Grid - 1e-9) * Grid;
            if (ownB) { sb = exitS; yb = exitY; }
            for (double s = first; !ownB && s <= zone + 1e-9; s += Grid)
            {
                double y = Aimed(true, s), g = GroundAt(s);
                if (y - g < stationMin) continue;
                double atTower = y - drop - line.UnderChairs(s);
                if (atTower >= LiftRope.Clearance && SpanShortfall(true, s, y) <= Tolerance) { sb = s; yb = y; break; }
                if (atTower > bestClear) { bestClear = atTower; sb = s; yb = y; }
            }
            if (sb < 0)
            {
                sb = first;
                yb = Aimed(true, sb);   // its pit is worked out from the solved rope below
            }
            // Past a terminal with its own station, the first line tower stands no nearer than a station tower would.
            double firstLine = ownB ? first : sb + MinGap;
            // 2. The top station tower: the closest slot where it's 15 ft tall with its station span clear; else the closest
            //    15 ft slot; else the closest, with the top terminal raised on its pad until the level span clears.
            double st = -1, yt = 0;
            double last = Math.Floor((length - Math.Max(StationDefault, type.Top.Ahead + PastFront)) / Grid + 1e-9) * Grid;
            double fallback = -1;
            for (double s = last; s >= length - zone - 1e-9 && s > sb + MinGap; s -= Grid)
            {
                double y = Aimed(false, s);
                if (y - GroundAt(s) < stationMin) continue;
                if (fallback < 0) fallback = s;
                if (SpanShortfall(false, s, y) <= Tolerance) { st = s; yt = y; break; }
            }
            if (st < 0)
            {
                st = fallback >= 0 ? fallback : last;
                yt = Aimed(false, st);
                double raise = Math.Max(stationMin - (yt - GroundAt(st)), SpanShortfall(false, st, yt));
                result.TopRaise = Math.Ceiling(raise / LiftRope.RaiseStep - 1e-9) * LiftRope.RaiseStep;
            }

            // 3. Line towers planned together (TW-12 v2); evenly between, at most 250 ft apart, where no plan fits.
            var along = new List<double>();
            var heights = new List<double>();
            if (!ownB)
            {
                along.Add(sb);
                heights.Add(Math.Max(0, Floor5(yb - GroundAt(sb))));
            }
            var planned = PlanLine(type, line, GroundAt, exitS, exitY, ownB, sb, yb, firstLine, st, yt, topExitS, topExitY, edgeB, edgeT);
            if (planned != null)
                foreach (var (s, h) in planned)
                {
                    along.Add(s);
                    heights.Add(h);
                }
            else
            {
                int spans = Math.Max(1, (int)Math.Ceiling((st - sb) / MaxSpan - 1e-9));
                for (int k = 1; k < spans; k++)
                {
                    along.Add(Math.Max(firstLine, Snap(sb + (st - sb) * k / spans)));
                    heights.Add(LineHeight);
                }
            }
            along.Add(st);
            heights.Add(Math.Max(0, Floor5(yt - GroundAt(st))));

            // 4. Only adding or raising line towers, while a head is too sharp or a span's chairs fall short. In a low zone a
            //    tower is added where there's room (50 ft from its neighbours); where there isn't, the zone is left to the
            //    envelope (the bottom) or the unload ramp (the top) rather than to ever taller towers.
            LiftRope rope = null;
            bool Inner(int i) => i > stationIdx && i < along.Count - 1;
            // Which towers the clearance rules may raise: past a terminal with its own station, the first line tower's height
            // is the exit's (TW-16 v4), so its span's shortfall goes to the trench instead.
            bool Raisable(int i) => Inner(i) && !(ownB && i == 0);
            var raisedNeighbour = new[] { -1, -1 };
            var neighbourDone = new bool[2];
            bool undone = false;
            var stuck = new HashSet<double>();   // towers (by position) too sharp with nothing left to try (TW-06: left red)
            // Stretches no tower can clear, between the towers either side (TW-12, LT40: left red with the reason); the search
            // carries on with the rest of the line.
            var abandoned = new List<(double From, double To)>();
            double ShortfallOutside(LiftRope r, double from, double to, out double at)
            {
                at = from;
                double worst = 0;
                for (double x = Math.Ceiling(from); x <= to; x += 1)
                {
                    bool skip = false;
                    foreach (var (a, b) in abandoned) if (x >= a && x <= b) { skip = true; break; }
                    if (skip || !r.ClearanceAt(x, out double clear, out double need)) continue;
                    if (need - clear > worst) { worst = need - clear; at = x; }
                }
                return worst;
            }
            double bottomLeftTo = double.MinValue, topLeftFrom = double.MaxValue;   // stretches left to the envelope and the ramp
            // The plan left the first span's chairs to the trench, up to 15 ft short of its first line tower (GR-11 v2, as
            // LiftLineEnvelope digs it): no tower squeezed in there.
            if (planned != null && planned.Count > 0) bottomLeftTo = planned[0].S - LiftLineEnvelope.TowerKeepMetres;
            var eased = new List<int>();   // how many 5 ft steps each tower has been raised to ease a sharp crest beside it
            int exitFix = 0;               // TW-16 v4's steps on the first line tower
            const int Rounds = 500;
            for (int round = 0; round < Rounds; round++)
            {
                var specs = new List<Tower>(along.Count);
                for (int i = 0; i < along.Count; i++)
                    specs.Add(new Tower(TowerId.Make(1, i), (int)Math.Round(along[i] * 1000), (int)Math.Round(heights[i] * 1000)));
                rope = LiftRope.Solve(type, ground, bx, by, tx, ty, specs, stance, exitS, exitY, topBack, topExitY, line);
                int sharp = -1;
                for (int i = 0; i < rope.Towers.Count; i++)
                    if (rope.Towers[i].Head == LiftRope.Head.TooSharp && !stuck.Contains(along[i])) { sharp = i; break; }
                // A station tower's neighbour raised past what its head can take: the last step back, and the stretch goes on
                // to the trench or the ramp instead.
                for (int e = 0; e < 2; e++)
                {
                    int r = raisedNeighbour[e];
                    if (r < 0 || neighbourDone[e]) continue;
                    if (r < rope.Towers.Count && rope.Towers[r].Head == LiftRope.Head.TooSharp)
                    {
                        heights[r] -= Grid;
                        neighbourDone[e] = true;
                        undone = true;
                    }
                }
                if (undone) { undone = false; continue; }
                // TW-16 v4: past a terminal with its own station, the first line tower keeps the rope leaving the row within its
                // range: lower or further out while it leaves too steeply, taller while it leaves too flat.
                if (ownB && along.Count > 1 && exitFix < 80)
                {
                    double steep = Math.Max(rope.BottomExitLoaded, rope.BottomExitEmpty), flat = Math.Min(rope.BottomExitLoaded, rope.BottomExitEmpty);
                    // Lower first (to 15 ft in the station zone, 25 ft past it); then nearer where the ground climbs faster than the
                    // row's aim, further where it doesn't. A line still too steep says so (the terminal's level is the player's, LT34).
                    double minH = (along[0] <= zone + 1e-9 ? stationMin : DesignUnits.TowerMinMm / 1000.0) - 1e-9;
                    // Never so low that the chairs passing it fall short of the rule there (its rising line in the zone).
                    if (rope.ClearanceAt(along[0], out _, out double needThere))
                        minH = Math.Max(minH, Math.Ceiling((needThere + drop) / Grid - 1e-9) * Grid - 1e-9);
                    bool climbs = (GroundAt(along[0]) - GroundAt(exitS)) / Math.Max(1, along[0] - exitS) > Math.Tan(type.Bottom.Aim);
                    if (steep > type.Bottom.AngleMax + 1e-9)
                    {
                        exitFix++;
                        if (heights[0] - Grid >= minH) heights[0] -= Grid;
                        else if (climbs && along[0] - Grid >= firstLine - 1e-9) along[0] -= Grid;
                        else if (!climbs && along[0] + Grid <= along[1] - MinGap + 1e-9) along[0] += Grid;
                        else exitFix = 80;
                        continue;
                    }
                    if (flat < type.Bottom.AngleMin - 1e-9 && heights[0] + Grid <= MaxHeight)
                    {
                        exitFix++;
                        heights[0] += Grid;
                        continue;
                    }
                }
                double from = ownB ? exitS : rope.Towers[0].RopeS, to = rope.Towers[rope.Towers.Count - 1].RopeS;
                from = Math.Max(from, bottomLeftTo);
                to = Math.Min(to, topLeftFrom);
                double shortfall = abandoned.Count == 0 ? rope.ShortfallBetween(from, to, out double low) : ShortfallOutside(rope, from, to, out low);
                if (shortfall <= Tolerance && sharp < 0) break;
                if (round == Rounds - 1) result.GaveUp.Add(new GiveUp(sharp >= 0 ? rope.Towers[sharp].BaseS : low, GiveUpKind.Rounds));
                double at = sharp >= 0 ? rope.Towers[sharp].BaseS : low;
                bool inZone = sharp < 0 && (at < edgeB || at > edgeT);
                int next = along.FindIndex(x => x > at);
                if (next < 0) next = along.Count;
                int prev = next - 1;
                // A low span between two line towers: raise both, up to TW-02's 100 ft before another tower (LT45; the 60 ft this
                // stopped at made the lines bunch, finding E3).
                if (sharp < 0 && !inZone && prev >= 0 && next < along.Count && Raisable(prev) && Raisable(next)
                    && heights[prev] + Grid <= MaxHeight + 1e-9 && heights[next] + Grid <= MaxHeight + 1e-9)
                {
                    heights[prev] += Grid;
                    heights[next] += Grid;
                    continue;
                }
                // TW-06: a tower that bends the rope too sharply gets a partner on the side of the bend. A low span gets a tower
                // where it's lowest, kept 50 ft from its neighbours (TW-03): beside a station tower, whose height its
                // terminal sets, that's the tower that carries the chairs on up (or down) to 15 ft.
                // A partner shares the bend at the middle of the span on the bend's side, where there's room for that (a group
                // spaced by its span, not bunched 50 ft apart; finding E3), else 50 ft away.
                double put = sharp >= 0 ? Snap(at + (rope.Towers[sharp].BendLoaded > 0 ? -1 : 1) * MinGap) : Snap(at);
                if (sharp >= 0)
                {
                    int side = rope.Towers[sharp].BendLoaded > 0 ? sharp - 1 : sharp + 1;
                    if (side >= 0 && side < along.Count && Math.Abs(along[side] - along[sharp]) >= 2 * MinGap - 1e-9)
                        put = Snap((along[side] + along[sharp]) / 2);
                }
                if (sharp < 0 && prev >= 0 && next < along.Count)
                    put = Math.Max(along[prev] + MinGap, Math.Min(along[next] - MinGap, put));
                put = Math.Max(firstLine, Math.Min(st - MinGap, put));
                bool Roomy(double p)
                {
                    int k = along.FindIndex(x => x > p);
                    double below = k > 0 ? along[k - 1] : ownB && k == 0 ? firstLine - MinGap : double.NaN;
                    return !double.IsNaN(below) && k >= 0 && k < along.Count && p - below >= MinGap - 1e-9 && along[k] - p >= MinGap - 1e-9;
                }
                // TW-06: no room on the bend's side (a station tower there, say), so the partner goes on the other.
                if (sharp >= 0 && !Roomy(put))
                {
                    double other = Snap(at + (rope.Towers[sharp].BendLoaded > 0 ? 1 : -1) * MinGap);
                    other = Math.Max(firstLine, Math.Min(st - MinGap, other));
                    if (Roomy(other)) put = other;
                }
                int ins = along.FindIndex(x => x > put);
                if (ins < 0) ins = along.Count;
                bool roomy = Roomy(put);
                if (roomy)
                {
                    along.Insert(ins, put);
                    if (ins < eased.Count) eased.Insert(ins, 0);
                    double ropeThere = Math.Max(LiftRope.RopeAt(rope.Loaded, put), line.UnderChairs(put) + drop + LiftRope.Clearance);
                    heights.Insert(ins, Math.Max(DesignUnits.TowerMinMm / 1000.0, Math.Min(MaxHeight, Snap(ropeThere - GroundAt(put)))));
                    continue;
                }
                // No room beside a station tower (its height is its terminal's): the line tower on the other side of the
                // short span stands taller first (TW-16 v3's remedies in order: line towers before the trench; task 09,
                // SK29's small shortfalls just past a low zone).
                if (sharp < 0 && prev == stationIdx && next < along.Count && Raisable(next) && !neighbourDone[0] && heights[next] < MaxHeight - 1e-9)
                {
                    heights[next] += Grid;
                    raisedNeighbour[0] = next;
                    continue;
                }
                if (sharp < 0 && next == along.Count - 1 && prev >= 0 && Raisable(prev) && !neighbourDone[1] && heights[prev] < MaxHeight - 1e-9)
                {
                    heights[prev] += Grid;
                    raisedNeighbour[1] = prev;
                    continue;
                }
                // Still short there, or in a low zone: the stretch up to the next tower goes to the envelope (the bottom) or the
                // unload ramp (the top).
                if (sharp < 0 && (inZone || prev == stationIdx || next == along.Count - 1))
                {
                    if (at < length / 2) bottomLeftTo = Math.Max(bottomLeftTo, next < along.Count ? along[next] : at + 1);
                    else topLeftFrom = Math.Min(topLeftFrom, prev >= 0 ? along[prev] : at - 1);
                    continue;
                }
                if (sharp >= 0)
                {
                    // No room for a partner. A breakover too sharp eases when the towers either side stand taller; a hold-down
                    // when it stands taller itself. Never the other way round, which only sharpens the bend.
                    bool crest = rope.Towers[sharp].BendLoaded < 0;
                    bool raised = false;
                    if (crest)
                    {
                        // Up to TW-02's 100 ft (LT45; the 20 ft this stopped at left crests red or bunched, finding E3).
                        while (eased.Count < along.Count) eased.Add(0);
                        foreach (int i in new[] { sharp - 1, sharp + 1 })
                            if (i >= 0 && i < along.Count && Raisable(i) && heights[i] + Grid <= MaxHeight + 1e-9) { heights[i] += Grid; eased[i]++; raised = true; }
                    }
                    else if (Raisable(sharp) && heights[sharp] < MaxHeight) { heights[sharp] += Grid; raised = true; }
                    if (!raised)
                    {
                        stuck.Add(along[sharp]);
                        result.GaveUp.Add(new GiveUp(rope.Towers[sharp].BaseS, GiveUpKind.TooSharp));
                    }
                    continue;
                }
                // No room: the nearer line tower stands taller.
                int near = prev >= 0 && (next >= along.Count || at - along[prev] < along[next] - at) ? prev : next;
                if (near < 0 || near >= along.Count || !Raisable(near)) near = prev >= 0 && Raisable(prev) ? prev : next < along.Count && Raisable(next) ? next : -1;
                if (near >= 0 && heights[near] < MaxHeight) heights[near] += Grid;
                else
                {
                    result.GaveUp.Add(new GiveUp(at, GiveUpKind.TowerLimit));
                    abandoned.Add((prev >= 0 ? along[prev] : from, next < along.Count ? along[next] : to));
                }
            }
            result.Rope = rope;
            var bottomTower = rope.Towers[0];
            var topTower = rope.Towers[rope.Towers.Count - 1];
            result.BottomStationS = sb;
            result.BottomStationY = ownB ? exitY : bottomTower.RopeY;
            result.BottomStationLean = ownB ? 0 : bottomTower.Lean;
            result.TopStationS = st;
            result.TopStationY = topTower.RopeY;
            result.TopStationLean = topTower.Lean;
            // The pit, from the solved (leaned) rope point: a leaning mast's head sits nearer its terminal and lower.
            double pit = GroundAt(sb) + stationMin * Math.Cos(bottomTower.Lean) - bottomTower.RopeY;
            result.BottomPit = !ownB && pit > 0.001 ? pit : 0;
            // Stored heights: 5 ft steps (TW-02); a station tower at least 15 ft, its foundation showing the rest.
            for (int i = 0; i < along.Count; i++)
            {
                double h = Inner(i) ? heights[i] : Math.Max(stationMin, heights[i]);
                result.Towers.Add(new TowerSpec(DesignUnits.FeetToMm((int)Math.Round(along[i] / 0.3048 / 5) * 5),
                                                DesignUnits.FeetToMm((int)Math.Round(h / 0.3048 / 5) * 5)));
            }
            return result;
        }

        // ---------- TW-12 v2: the line planned (LT45; the owner's findings E2–E4, 2026-10-23) ----------

        /// <summary>Steel the plan tries for a line tower, feet: modest first, up to TW-02's 100 ft (the repair refines in 5 ft).</summary>
        static readonly double[] PlanFeet = { 35, 45, 60, 80, 100 };
        /// <summary>Candidates every 25 ft along the line.</summary>
        const double PlanStep = 7.62;
        /// <summary>The plan keeps each head within this share of what its kit's largest head carries (the solve's leans and
        /// catenaries differ a little from the plan's parabolas).</summary>
        const double PlanMargin = 0.9;

        /// <summary>
        /// TW-12 v2: the line towers between the station towers, chosen together as the fewest towers whose spans keep the
        /// loaded chairs 15 ft up (TW-01 v2; the low zones are the trench's and the unload ramp's) and whose every head, the
        /// station towers' included, carries its bend in both load cases with the spans' sag counted (TW-04/TW-05, on its
        /// kit's largest heads); among those, heights nearest a modest 35 ft and spans nearest even. So a break gets as few
        /// towers as its heads need, taller ones before more (LT45), and a detachable's level end shares its turn with the
        /// towers after it (LT43) instead of one head taking it all. A shortest path over candidates every 25 ft with
        /// <see cref="PlanFeet"/> of steel; each tower then comes down in 5 ft steps toward 35 ft while it all still fits.
        /// Null where no line fits (the caller spaces them evenly and repairs).
        /// </summary>
        static List<(double S, double H)> PlanLine(LiftType type, LineGround line, Func<double, double> groundAt, double exitS, double exitY,
                                                   bool ownB, double sb, double yb, double firstLine, double st, double yt,
                                                   double topExitS, double topExitY, double edgeB, double edgeT)
        {
            double t0 = type.CounterweightNewtons, wl = type.LoadedNewtonsPerMetre, we = type.EmptyNewtonsPerMetre;
            double drop = type.ChairDropMetres, maxH = Math.Min(type.TowerMaxMetres, 30.48);
            var kit = type.Heads ?? TowerForces.HeadKit.Quad;
            int maxS = kit.MaxSheaves;
            // Candidate positions and nodes: 0 is the bottom station tower (or the exit past a terminal with its own station),
            // then each position × height, then the top station tower.
            var ps = new List<double>();
            for (double s = Math.Ceiling(firstLine / Grid - 1e-9) * Grid; s <= st - MinGap + 1e-9; s += PlanStep) ps.Add(Math.Round(s / Grid) * Grid);
            var hs = new List<double>();
            foreach (double f in PlanFeet) if (f * 0.3048 <= maxH + 1e-9) hs.Add(f * 0.3048);
            int nh = hs.Count, n = ps.Count * nh + 2, end = n - 1;
            var ns = new double[n];
            var ny = new double[n];
            var nH = new double[n];
            ns[0] = ownB ? exitS : sb; ny[0] = ownB ? exitY : yb;
            ns[end] = st; ny[end] = yt;
            for (int p = 0; p < ps.Count; p++)
            {
                double g = groundAt(ps[p]);
                for (int k = 0; k < nh; k++)
                {
                    int i = 1 + p * nh + k;
                    ns[i] = ps[p];
                    nH[i] = hs[k];
                    ny[i] = g + hs[k];
                }
            }
            // What a head at each node can carry (TW-05 per sheave: 4.5° or 1,500 lbf; TW-04's group minimum decides the kind).
            double Cap(int sheaves, double t) => Math.Min(sheaves * TowerForces.SheaveBendLimit, 2 * Math.Asin(Math.Min(1, sheaves * TowerForces.SheaveLimitNewtons / (2 * t))));
            double Floor(double t) => 2 * Math.Asin(Math.Min(1, TowerForces.GroupMinimumNewtons / (2 * t)));
            var tl = new double[n];
            var te = new double[n];
            var capL = new double[n];
            var capE = new double[n];
            var cap4L = new double[n];
            var cap4E = new double[n];
            var minL = new double[n];
            var minE = new double[n];
            var sagL = new double[n];   // w / 2T: how much a metre of span beside the head turns the rope down over it
            var sagE = new double[n];
            for (int i = 0; i < n; i++)
            {
                tl[i] = Math.Max(t0, t0 + wl * (ny[i] - exitY));
                te[i] = Math.Max(t0, t0 + we * (ny[i] - exitY));
                capL[i] = PlanMargin * Cap(maxS, tl[i]);
                capE[i] = PlanMargin * Cap(maxS, te[i]);
                cap4L[i] = PlanMargin * Cap(4, tl[i]);
                cap4E[i] = PlanMargin * Cap(4, te[i]);
                minL[i] = Floor(tl[i]);
                minE[i] = Floor(te[i]);
                sagL[i] = wl / (2 * tl[i]);
                sagE[i] = we / (2 * te[i]);
            }
            // The highest ground under the chairs, a metre at a time.
            var under = new double[line.Count];
            for (int j = 0; j < under.Length; j++) under[j] = Math.Max(line.UnderUp[j], line.UnderDown[j]);
            // How far the turn from a span (angle a1, length l1) to the next (a2, l2) is past what the head at node v carries,
            // radians (0 where it carries it): the tangents' difference, each span's sag turning the rope down over the head
            // by w·l/(2T). Past it isn't forbidden but costs heavily, so where nothing fits (a steep pitch right up to a level
            // top) the plan is still the least sharp line, and the repair and LT34's level hints take it from there.
            double Excess(int v, double a1, double l1, double a2, double l2)
            {
                double bl = a2 - a1 - sagL[v] * (l1 + l2), be = a2 - a1 - sagE[v] * (l1 + l2);
                if (bl <= -minL[v] && be <= -minE[v]) return Math.Max(0, Math.Max(-bl - capL[v], -be - capE[v]));   // support
                if (bl >= minL[v] && be >= minE[v]) return Math.Max(0, Math.Max(bl - capL[v], be - capE[v]));       // hold-down
                return Math.Max(0, Math.Max(Math.Abs(bl) - cap4L[v], Math.Abs(be) - cap4E[v]));                     // combination
            }
            const double PerDegreeOver = 3000;   // three towers' worth for each degree a head is past what it carries
            double Over(double excess) => excess * 180 / Math.PI * PerDegreeOver;
            // How far a span's loaded chairs (the parabola of its sag) fall short of 15 ft outside the low zones; NaN where they
            // can't. The first span's shortfall, up to 15 ft short of its far tower, is the trench's (GR-11 v2, at most 10 ft).
            double Short(double a, double ya, double b, double y2, bool trench)
            {
                double len = b - a, slope = (y2 - ya) / len;
                double k = wl * (1 + slope * slope) / (2 * Math.Max(t0, t0 + wl * (Math.Min(ya, y2) - exitY)));
                double worst = 0, need = LiftRope.Clearance + 0.05 + drop, keep = b - LiftLineEnvelope.TowerKeepMetres;
                // Every 2 m between the low zones, on the metre samples.
                int j0 = Math.Max(0, (int)Math.Ceiling(Math.Max(a + 1, edgeB))), j1 = Math.Min(under.Length - 1, (int)Math.Floor(Math.Min(b - 1e-9, edgeT)));
                for (int j = j0; j <= j1; j += 2)
                {
                    double gap = need + under[j] - (ya + slope * (j - a) - k * (j - a) * (b - j));
                    if (gap <= 0) continue;
                    if (!trench || j > keep || gap > LiftLineEnvelope.CapMetres - 0.1) return double.NaN;
                    if (gap > worst) worst = gap;
                }
                return worst;
            }
            var dug = new List<double>();   // each span's trench, metres (it costs: the plan digs no more than it must)
            double ideal = (st - (ownB ? exitS : sb)) / Math.Max(1, Math.Ceiling((st - (ownB ? exitS : sb)) / MaxSpan - 1e-9));
            // A span's cost: the tower it reaches (a thousand; its height's distance from a modest 35 ft), the span's distance
            // from even, and its trench.
            double Cost(int v, double len, double trench)
            {
                double c = 50 * ((len - ideal) / ideal) * ((len - ideal) / ideal) + 200 * trench;
                if (v == end) return c;
                double over = (nH[v] / 0.3048 - 35) / 10;
                return 1000 + 10 * over * over + c;
            }
            // The spans: every pair 50–250 ft apart that clears, by the node they leave.
            var from = new List<int>();
            var to = new List<int>();
            var angle = new List<double>();
            var length = new List<double>();
            var outOf = new List<int>[n];
            var into = new List<int>[n];
            for (int i = 0; i < n; i++) { outOf[i] = new List<int>(); into[i] = new List<int>(); }
            void Span(int u, int v)
            {
                double len = ns[v] - ns[u];
                if (len < MinGap - 1e-6 && !(ownB && u == 0) || len > MaxSpan + 1e-6 || len <= 0) return;
                double shortBy = Short(ns[u], ny[u], ns[v], ny[v], u == 0);
                if (double.IsNaN(shortBy)) return;
                int e = from.Count;
                dug.Add(shortBy);
                from.Add(u);
                to.Add(v);
                angle.Add(Math.Atan2(ny[v] - ny[u], len));
                length.Add(len);
                outOf[u].Add(e);
                into[v].Add(e);
            }
            for (int v = 1; v < end; v++) Span(0, v);
            Span(0, end);
            for (int u = 1; u < end; u++)
            {
                for (int v = u + 1; v < end; v++) { if (ns[v] - ns[u] > MaxSpan + 1e-6) break; Span(u, v); }
                Span(u, end);
            }
            // The fixed spans either side: from the bottom terminal's exit to its station tower, and on from the top's.
            double inA = Math.Atan2(yb - exitY, sb - exitS), inL = Math.Max(1e-3, sb - exitS);
            double outA = Math.Atan2(topExitY - yt, topExitS - st), outL = Math.Max(1e-3, topExitS - st);
            var best = new double[from.Count];
            var prev = new int[from.Count];
            var spanCost = new double[from.Count];
            var spanAngle = angle.ToArray();
            var spanLength = length.ToArray();
            var outs = new int[n][];
            for (int i = 0; i < n; i++) outs[i] = outOf[i].ToArray();
            for (int e = 0; e < spanCost.Length; e++) spanCost[e] = Cost(to[e], length[e], dug[e]);
            for (int e = 0; e < best.Length; e++) { best[e] = double.PositiveInfinity; prev[e] = -1; }
            foreach (int e in outOf[0])
            {
                double over;
                if (ownB)
                {
                    // Past a terminal with its own station the rope leaves its row within the row's range (TW-16 v4).
                    double leave = angle[e] - wl * length[e] / (2 * tl[0]), leaveE = angle[e] - we * length[e] / (2 * te[0]);
                    over = type.Bottom.Takes(leave) && type.Bottom.Takes(leaveE) ? 0 : double.PositiveInfinity;
                }
                else over = Over(Excess(0, inA, inL, angle[e], length[e]));
                best[e] = spanCost[e] + over;
            }
            var order = new List<int>();
            for (int v = 1; v < end; v++)
            {
                var o = outs[v];
                // The cheapest ways in first: the rest mostly stop at the first comparison, before the bend's sums.
                order.Clear();
                order.AddRange(into[v]);
                order.Sort((x, y) => best[x].CompareTo(best[y]) != 0 ? best[x].CompareTo(best[y]) : x.CompareTo(y));
                foreach (int e in order)
                {
                    double be0 = best[e];
                    if (double.IsPositiveInfinity(be0)) continue;
                    double a1 = spanAngle[e], l1 = spanLength[e];
                    for (int k = 0; k < o.Length; k++)
                    {
                        int f = o[k];
                        double c = be0 + spanCost[f];
                        if (c >= best[f]) continue;
                        c += Over(Excess(v, a1, l1, spanAngle[f], spanLength[f]));
                        if (c >= best[f]) continue;
                        best[f] = c;
                        prev[f] = e;
                    }
                }
            }
            int last = -1;
            double total = double.PositiveInfinity;
            foreach (int e in into[end])
            {
                double c = best[e] + Over(Excess(end, angle[e], length[e], outA, outL));
                if (c < total) { total = c; last = e; }
            }
            if (last < 0) return null;
            var path = new List<int>();
            for (int e = last; e >= 0; e = prev[e]) if (to[e] != end) path.Add(to[e]);
            path.Reverse();
            // Each tower down in 5 ft steps toward a modest 35 ft while its spans still clear and no head beside it turns more
            // sharply than it can (or than it already did, where nothing fit).
            var s5 = new List<double>();
            var h5 = new List<double>();
            foreach (int v in path) { s5.Add(ns[v]); h5.Add(nH[v]); }
            int firstSpan = last;
            while (prev[firstSpan] >= 0) firstSpan = prev[firstSpan];
            double firstDug = dug[firstSpan];
            // The heads beside tower i: how far past what they carry, radians in all; NaN where a span no longer clears.
            double Local(int i)
            {
                double SAt(int j) => j < 0 ? (ownB ? exitS : sb) : j >= s5.Count ? st : s5[j];
                double YAt(int j) => j < 0 ? (ownB ? exitY : yb) : j >= s5.Count ? yt : groundAt(s5[j]) + h5[j];
                for (int j = i - 1; j <= i; j++)
                {
                    // No deeper a trench than the plan dug (a lower first tower would only dig more).
                    double shortBy = Short(SAt(j), YAt(j), SAt(j + 1), YAt(j + 1), j == -1);
                    if (double.IsNaN(shortBy) || shortBy > (j == -1 ? firstDug + 1e-9 : 0)) return double.NaN;
                }
                double over = 0;
                for (int j = i - 1; j <= i + 1; j++)
                {
                    if (j < -1 || j > s5.Count) continue;
                    if (j == -1 && ownB) continue;
                    double a1 = j == -1 ? inA : Math.Atan2(YAt(j) - YAt(j - 1), SAt(j) - SAt(j - 1)), l1 = j == -1 ? inL : SAt(j) - SAt(j - 1);
                    double a2 = j == s5.Count ? outA : Math.Atan2(YAt(j + 1) - YAt(j), SAt(j + 1) - SAt(j)), l2 = j == s5.Count ? outL : SAt(j + 1) - SAt(j);
                    // The node's tensions and caps at its new height.
                    double y = YAt(j);
                    double tL = Math.Max(t0, t0 + wl * (y - exitY)), tE = Math.Max(t0, t0 + we * (y - exitY));
                    double bl = a2 - a1 - wl * (l1 + l2) / (2 * tL), be = a2 - a1 - we * (l1 + l2) / (2 * tE);
                    double mL = Floor(tL), mE = Floor(tE);
                    over += bl <= -mL && be <= -mE ? Math.Max(0, Math.Max(-bl - PlanMargin * Cap(maxS, tL), -be - PlanMargin * Cap(maxS, tE)))
                          : bl >= mL && be >= mE ? Math.Max(0, Math.Max(bl - PlanMargin * Cap(maxS, tL), be - PlanMargin * Cap(maxS, tE)))
                          : Math.Max(0, Math.Max(Math.Abs(bl) - PlanMargin * Cap(4, tL), Math.Abs(be) - PlanMargin * Cap(4, tE)));
                }
                return over;
            }
            double lowest = DesignUnits.TowerMinMm / 1000.0, modest = 35 * 0.3048;
            for (int i = 0; i < s5.Count; i++)
                while (h5[i] - Grid >= Math.Max(lowest, modest) - 1e-9)
                {
                    double before = Local(i);
                    h5[i] -= Grid;
                    double after = Local(i);
                    if (double.IsNaN(after) || after > before + 1e-9) { h5[i] += Grid; break; }
                }
            var planned = new List<(double S, double H)>(s5.Count);
            for (int i = 0; i < s5.Count; i++) planned.Add((s5[i], h5[i]));
            return planned;
        }
    }
}
