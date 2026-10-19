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
    /// 2. **Line towers** evenly spaced between them, at most 250 ft apart, from 35 ft (TW-12).
    /// 3. **Only adding or raising:** a head too sharp gets a TW-06 partner; a span whose chairs fall short of TW-01 v2 gets
    ///    a taller or an added line tower. Station spans are left to the lift-line envelope (the bottom) and the unload
    ///    ramp (the top), which <see cref="LiftBuild"/> applies with the results here.
    /// Positions are on the 5 ft grid (TW-12).
    /// </summary>
    public static class TowerPlacement
    {
        public sealed class Result
        {
            public List<TowerSpec> Towers { get; } = new List<TowerSpec>();
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

        const double Grid = 1.524, LineHeight = 10.668, MaxHeight = 30.48, StationDefault = 12.192, PastFront = 4.572,
                     MaxSpan = 76.2, MinGap = 15.24, Tolerance = LiftRope.ClearanceTolerance;

        /// <summary>
        /// Places towers on the line from <paramref name="bottom"/> to <paramref name="top"/> (bullwheels, in the package's
        /// frame) over <paramref name="ground"/> (with the pads graded). <paramref name="bottomLevel"/> and
        /// <paramref name="topLevel"/> are the terminals' base elevations (their load and unload levels).
        /// </summary>
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

            // Where a station tower at s must carry the rope for the empty rope to leave its terminal at the end's aim
            // (plumb; the solve's foundation reveal settles the lean).
            double Aimed(bool bottom, double s)
            {
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
            double sb = -1, yb = 0, bestClear = double.MinValue;
            double first = Math.Ceiling(Math.Max(StationDefault, type.Bottom.Ahead + PastFront) / Grid - 1e-9) * Grid;
            for (double s = first; s <= zone + 1e-9; s += Grid)
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

            // 3. Line towers evenly between, at most 250 ft apart.
            var along = new List<double> { sb };
            var heights = new List<double> { Math.Max(0, Floor5(yb - GroundAt(sb))) };
            int spans = Math.Max(1, (int)Math.Ceiling((st - sb) / MaxSpan - 1e-9));
            for (int k = 1; k < spans; k++)
            {
                along.Add(Snap(sb + (st - sb) * k / spans));
                heights.Add(LineHeight);
            }
            along.Add(st);
            heights.Add(Math.Max(0, Floor5(yt - GroundAt(st))));

            // 4. Only adding or raising line towers, while a head is too sharp or a span's chairs fall short. In a low zone a
            //    tower is added where there's room (50 ft from its neighbours); where there isn't, the zone is left to the
            //    envelope (the bottom) or the unload ramp (the top) rather than to ever taller towers.
            LiftRope rope = null;
            bool Inner(int i) => i > 0 && i < along.Count - 1;
            bool sharpStuck = false;
            double bottomLeftTo = double.MinValue, topLeftFrom = double.MaxValue;   // stretches left to the envelope and the ramp
            var eased = new List<int>();   // how many 5 ft steps each tower has been raised to ease a sharp crest beside it
            for (int round = 0; round < 500; round++)
            {
                var specs = new List<Tower>(along.Count);
                for (int i = 0; i < along.Count; i++)
                    specs.Add(new Tower(TowerId.Make(1, i), (int)Math.Round(along[i] * 1000), (int)Math.Round(heights[i] * 1000)));
                rope = LiftRope.Solve(type, ground, bx, by, tx, ty, specs, stance, exitS, exitY, topBack, topExitY, line);
                int sharp = -1;
                if (!sharpStuck)
                    for (int i = 0; i < rope.Towers.Count; i++) if (rope.Towers[i].Head == LiftRope.Head.TooSharp) { sharp = i; break; }
                double from = rope.Towers[0].RopeS, to = rope.Towers[rope.Towers.Count - 1].RopeS;
                from = Math.Max(from, bottomLeftTo);
                to = Math.Min(to, topLeftFrom);
                double shortfall = rope.ShortfallBetween(from, to, out double low);
                if (shortfall <= Tolerance && sharp < 0) break;
                double at = sharp >= 0 ? rope.Towers[sharp].BaseS : low;
                bool inZone = sharp < 0 && (at < edgeB || at > edgeT);
                int next = along.FindIndex(x => x > at);
                if (next < 0) next = along.Count;
                int prev = next - 1;
                // A low span between two modest line towers: raise both (TW-12's modest towers first).
                if (sharp < 0 && !inZone && prev >= 0 && next < along.Count && Inner(prev) && Inner(next) && heights[prev] < 18.29 && heights[next] < 18.29)
                {
                    heights[prev] += Grid;
                    heights[next] += Grid;
                    continue;
                }
                // TW-06: a tower that bends the rope too sharply gets a partner on the side of the bend. A low span gets a tower
                // where it's lowest, kept 50 ft from its neighbours (TW-03): beside a station tower, whose height its
                // terminal sets, that's the tower that carries the chairs on up (or down) to 15 ft.
                double put = sharp >= 0 ? Snap(at + (rope.Towers[sharp].BendLoaded > 0 ? -1 : 1) * MinGap) : Snap(at);
                if (sharp < 0 && prev >= 0 && next < along.Count)
                    put = Math.Max(along[prev] + MinGap, Math.Min(along[next] - MinGap, put));
                put = Math.Max(sb + MinGap, Math.Min(st - MinGap, put));
                bool Roomy(double p)
                {
                    int k = along.FindIndex(x => x > p);
                    return k > 0 && k < along.Count && p - along[k - 1] >= MinGap - 1e-9 && along[k] - p >= MinGap - 1e-9;
                }
                // TW-06: no room on the bend's side (a station tower there, say), so the partner goes on the other.
                if (sharp >= 0 && !Roomy(put))
                {
                    double other = Snap(at + (rope.Towers[sharp].BendLoaded > 0 ? 1 : -1) * MinGap);
                    other = Math.Max(sb + MinGap, Math.Min(st - MinGap, other));
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
                // No room beside a station tower (its height is its terminal's), or in a low zone: the stretch up to the
                // next tower goes to the envelope (the bottom) or the unload ramp (the top).
                if (sharp < 0 && (inZone || prev == 0 || next == along.Count - 1))
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
                        // At most 20 ft each: past that the crest is too sharp for the room (TW-06, the line stays red).
                        while (eased.Count < along.Count) eased.Add(0);
                        foreach (int i in new[] { sharp - 1, sharp + 1 })
                            if (i >= 0 && i < along.Count && Inner(i) && heights[i] < MaxHeight && eased[i] < 4) { heights[i] += Grid; eased[i]++; raised = true; }
                    }
                    else if (Inner(sharp) && heights[sharp] < MaxHeight) { heights[sharp] += Grid; raised = true; }
                    if (!raised) sharpStuck = true;
                    continue;
                }
                // No room: the nearer line tower stands taller.
                int near = prev >= 0 && (next >= along.Count || at - along[prev] < along[next] - at) ? prev : next;
                if (near < 0 || near >= along.Count || !Inner(near)) near = prev >= 0 && Inner(prev) ? prev : next < along.Count && Inner(next) ? next : -1;
                if (near >= 0 && heights[near] < MaxHeight) heights[near] += Grid;
                else break;
            }
            result.Rope = rope;
            var bottomTower = rope.Towers[0];
            var topTower = rope.Towers[rope.Towers.Count - 1];
            result.BottomStationS = sb;
            result.BottomStationY = bottomTower.RopeY;
            result.BottomStationLean = bottomTower.Lean;
            result.TopStationS = st;
            result.TopStationY = topTower.RopeY;
            result.TopStationLean = topTower.Lean;
            // The pit, from the solved (leaned) rope point: a leaning mast's head sits nearer its terminal and lower.
            double pit = GroundAt(sb) + stationMin * Math.Cos(bottomTower.Lean) - bottomTower.RopeY;
            result.BottomPit = pit > 0.001 ? pit : 0;
            // Stored heights: 5 ft steps (TW-02); a station tower at least 15 ft, its foundation showing the rest.
            for (int i = 0; i < along.Count; i++)
            {
                double h = Inner(i) ? heights[i] : Math.Max(stationMin, heights[i]);
                result.Towers.Add(new TowerSpec(DesignUnits.FeetToMm((int)Math.Round(along[i] / 0.3048 / 5) * 5),
                                                DesignUnits.FeetToMm((int)Math.Round(h / 0.3048 / 5) * 5)));
            }
            return result;
        }
    }
}
