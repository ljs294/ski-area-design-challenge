using System;
using System.Collections.Generic;
using MountainPlanner.Domain.Edits;

namespace MountainPlanner.Domain.Design
{
    /// <summary>
    /// One build path for every lift (task 07 round 3: the owner's rule that whatever a player draws gets the same result
    /// with no hand tuning). In order:
    /// 1. LF-12 v2's pads, level at each terminal's origin (raised by any last resort);
    /// 2. TW-12's placement with TW-16 v3's station towers (<see cref="TowerPlacement"/>);
    /// 3. the last resorts, at most three times: the top terminal on a built-up unload ramp where its level last span can't
    ///    clear the ground (owner Q6), the bottom terminal raised where the trench would be deeper than its 10 ft;
    /// 4. GR-11 v2's lift-line envelope under the chairs at the bottom (<see cref="LiftLineEnvelope"/>);
    /// 5. the station towers' 5 ft steel on the final ground, their foundations showing the rest (TW-02 v3);
    /// 6. CL-03's corridor and CL-09 round each terminal.
    /// Raises live in the pads' fill, which is saved in the terrain edits, so a saved design rebuilds the same rope.
    /// Positions in the edit lattice's frame.
    /// </summary>
    public static class LiftBuild
    {
        public sealed class Result
        {
            public List<TowerSpec> Towers { get; internal set; }
            /// <summary>The edits with the lift's pads, envelope and clearing on top.</summary>
            public TerrainEdits Edits { get; internal set; }
            public LiftRope Rope { get; internal set; }
            public TerminalPad.Result BottomPad { get; internal set; }
            public TerminalPad.Result TopPad { get; internal set; }
            public LiftLineEnvelope.Result Envelope { get; internal set; }
            /// <summary>How much higher each terminal stands than the ground at its origin (metres; 0 almost always).</summary>
            public double BottomRaise { get; internal set; }
            public double TopRaise { get; internal set; }
            public int ClearedCells { get; internal set; }
            /// <summary>Each terminal's base elevation as built (its origin's ground plus any raise), metres; the view stands the
            /// terminals there (the raise isn't in the terrain: pads only cut, LF-12 v3).</summary>
            public double BottomLevel { get; internal set; }
            public double TopLevel { get; internal set; }
            /// <summary>The levels stored with the lift (SK14, save v2): <see cref="BottomLevel"/> and <see cref="TopLevel"/> in
            /// whole millimetres, which the earthwork used.</summary>
            public LiftLevels Levels { get; internal set; }
            /// <summary>How much of each raise came from a steep site (LF-08 v2), metres.</summary>
            public double BottomSteepRaise { get; internal set; }
            public double TopSteepRaise { get; internal set; }

            /// <summary>Why the lift is red, with each rule (TW-01 v2, TW-05, TW-06, TW-16 v3, RP-05, LF-12); empty when it isn't.</summary>
            public List<LiftProblem> Problems => _problems ??= LiftProblems.For(Rope, BottomPad.ClippedStepMetres, TopPad.ClippedStepMetres);
            List<LiftProblem> _problems;

            /// <summary>Every rule the lift's geometry answers to holds.</summary>
            public bool Ok => Problems.Count == 0;
        }

        /// <summary>LF-08 v2: a terminal cuts at most 10 ft into the hill; on a steeper site it stands higher on its pier.</summary>
        public const double SitingVariationMetres = 3.048;

        /// <summary>
        /// LF-08: how much the ground varies under <paramref name="end"/>'s footprint with its bullwheel at
        /// (<paramref name="x"/>, <paramref name="y"/>) and (<paramref name="ux"/>, <paramref name="uy"/>) pointing toward the
        /// other terminal, sampled every metre (metres).
        /// </summary>
        public static double SiteVariation(IHeightSource ground, TerminalEnd end, double x, double y, double ux, double uy) =>
            SiteHighest(ground, end, x, y, ux, uy, out double lowest) - lowest;

        /// <summary>The highest (and lowest) ground under <paramref name="end"/>'s footprint, every metre.</summary>
        public static double SiteHighest(IHeightSource ground, TerminalEnd end, double x, double y, double ux, double uy, out double lowest)
        {
            double lo = double.MaxValue, hi = double.MinValue;
            for (double a = -end.Behind; a <= end.Ahead + 1e-9; a += 1)
                for (double w = -end.HalfWidth; w <= end.HalfWidth + 1e-9; w += 1)
                {
                    double h = ground.At(x + ux * a + uy * w, y + uy * a - ux * w);
                    lo = Math.Min(lo, h);
                    hi = Math.Max(hi, h);
                }
            lowest = lo;
            return hi;
        }

        public static Result Build(LiftType type, EditedHeights current, double bx, double by, double tx, double ty, TowerStance stance,
                                   Action<string> log = null)
        {
            if (current == null) throw new ArgumentNullException(nameof(current));
            double length = Math.Sqrt((tx - bx) * (tx - bx) + (ty - by) * (ty - by));
            double ux = (tx - bx) / length, uy = (ty - by) / length;
            double bottomLevel = current.At(bx + ux * type.Bottom.OriginAlong, by + uy * type.Bottom.OriginAlong);
            double topLevel = current.At(tx - ux * type.Top.OriginAlong, ty - uy * type.Top.OriginAlong);
            // LF-08 v2 (owner, round 4, SK30): steep sites are allowed; the terminal stands higher on its pier until it cuts at
            // most 10 ft into the hill (its pad only cuts, LF-12 v3).
            double steepB = Math.Max(0, SiteHighest(current, type.Bottom, bx, by, ux, uy, out _) - SitingVariationMetres - bottomLevel);
            double steepT = Math.Max(0, SiteHighest(current, type.Top, tx, ty, -ux, -uy, out _) - SitingVariationMetres - topLevel);
            double raiseB = Math.Ceiling(steepB / LiftRope.RaiseStep - 1e-9) * LiftRope.RaiseStep;
            double raiseT = Math.Ceiling(steepT / LiftRope.RaiseStep - 1e-9) * LiftRope.RaiseStep;
            TerminalPad.Result bottomPad = null, topPad = null;
            TowerPlacement.Result placed = null;
            LiftLineEnvelope.Result envelope = null;
            EditedHeights onPads = null;
            Works lastWork = null;
            LiftRope.Zones(type, length, out _, out _, out double edgeT, out double frontT);
            double Steps(double metres) => Math.Ceiling(metres / LiftRope.RaiseStep - 1e-9) * LiftRope.RaiseStep;
            List<TowerSpec> towers = null;
            LiftRope rope = null;
            const int Attempts = 8;
            for (int attempt = 0; attempt < Attempts; attempt++)
            {
                bottomPad = TerminalPad.Grade(current, bx, by, ux, uy, type.Bottom.Behind, type.Bottom.Ahead, type.Bottom.HalfWidth, bottomLevel + raiseB);
                var withBottom = new EditedHeights(current.Source, current.Lattice, bottomPad.Edits);
                topPad = TerminalPad.Grade(withBottom, tx, ty, -ux, -uy, type.Top.Behind, type.Top.Ahead, type.Top.HalfWidth, topLevel + raiseT);
                onPads = new EditedHeights(current.Source, current.Lattice, topPad.Edits);
                placed = TowerPlacement.Place(type, onPads, bx, by, tx, ty, bottomLevel + raiseB, topLevel + raiseT, stance);
                // The station towers' steel: their rope point is what the terminal needs, so the ground under them changes only
                // how much steel and foundation show. Every judgement below reads the lift as built from what would be stored
                // (Earthwork), so the first build and any replay agree (task 09).
                int last = placed.Towers.Count - 1;
                var levelsNow = LiftLevels.FromMetres(bottomLevel + raiseB, topLevel + raiseT);
                var specs = new List<Tower>(placed.Towers.Count);
                for (int i = 0; i < placed.Towers.Count; i++) specs.Add(new Tower(TowerId.Make(1, i), placed.Towers[i].AlongMm, placed.Towers[i].HeightMm));
                // Steel along the (leaned) mast in 5 ft steps; the foundation shows the rest, straight up.
                int SteelMm(Tower t, double ropeY, double lean)
                {
                    double s = t.AlongMm / 1000.0, g = onPads.At(bx + ux * s, by + uy * s);
                    double steel = Math.Floor((ropeY - g) / Math.Cos(lean) / 1.524 + 1e-6) * 1.524;
                    steel = Math.Max(DesignUnits.StationTowerMinMm / 1000.0, Math.Min(DesignUnits.TowerMaxMm / 1000.0, steel));
                    return DesignUnits.FeetToMm((int)Math.Round(steel / 0.3048));
                }
                specs[0] = new Tower(specs[0].Id, specs[0].AlongMm, SteelMm(specs[0], placed.BottomStationY, placed.BottomStationLean));
                if (last > 0) specs[last] = new Tower(specs[last].Id, specs[last].AlongMm, SteelMm(specs[last], placed.TopStationY, placed.TopStationLean));
                int Step(int i, double reveal, double over)
                {
                    const double tolerance = 0.1 * Math.PI / 180;
                    int h = specs[i].HeightMm;
                    if (reveal <= 1e-6 && over > tolerance && h - DesignUnits.FiveFeetMm >= DesignUnits.StationTowerMinMm) return -1;
                    if (reveal >= LiftRope.MaxReveal - 1e-3 && over < -tolerance && h + DesignUnits.FiveFeetMm <= DesignUnits.TowerMaxMm) return 1;
                    return 0;
                }
                Tower Restep(Tower t, int steps) => new Tower(t.Id, t.AlongMm, t.HeightMm + steps * DesignUnits.FiveFeetMm);
                // A leaning station tower carries its head toward or away from its terminal, so the foundation alone may not
                // reach the aim: the steel then steps 5 ft down (the foundation at nothing, the rope too high) or up (at its full
                // 5 ft, too low), and the foundation takes up the rest again.
                for (int pass = 0; pass < 5; pass++)
                {
                    lastWork = Earthwork(type, current, bx, by, tx, ty, specs, levelsNow, stance);
                    var r = lastWork.Rope;
                    bool changed = false;
                    int s0 = Step(0, r.Towers[0].Reveal, r.BottomExitEmpty - type.Bottom.Aim);
                    if (s0 != 0) { specs[0] = Restep(specs[0], s0); changed = true; }
                    if (last > 0)
                    {
                        int s1 = Step(last, r.Towers[last].Reveal, -r.TopEntryEmpty - type.Top.Aim);
                        if (s1 != 0) { specs[last] = Restep(specs[last], s1); changed = true; }
                    }
                    if (!changed) break;
                }
                rope = lastWork.Rope;
                envelope = lastWork.Envelope;
                towers = new List<TowerSpec>(specs.Count);
                foreach (var t in specs) towers.Add(new TowerSpec(t.AlongMm, t.HeightMm));

                log?.Invoke($"attempt {attempt}: raise {raiseB:F2}/{raiseT:F2} m, station {placed.BottomStationS:F1} m pit {placed.BottomPit:F2} m, " +
                            $"envelope needed {envelope.NeededMetres:F2} deepest {envelope.DeepestMetres:F2} m, steel {towers[0].HeightMm / 304.8:F0} ft, " +
                            $"reveal {rope.Towers[0].Reveal:F2} m, lean {rope.Towers[0].Lean * 180 / Math.PI:F1}°, exit {rope.BottomExitEmpty * 180 / Math.PI:F2}°, " +
                            $"top raise asked {placed.TopRaise:F2} m, shortfall {rope.Shortfall:F2} m at {rope.ShortfallS:F0} m");
                // The last resorts, read off the final rope: the top on a built-up unload ramp while its level span or low
                // zone falls short; the bottom raised where the trench would go deeper than its 10 ft.
                // The last attempt keeps what it built, so the result's rope and levels always agree.
                if (attempt == Attempts - 1) break;
                bool again = false;
                double top = Math.Max(placed.TopRaise, Steps(rope.ShortfallBetween(edgeT, frontT, out _)));
                // A top station tower too sharp a hold-down (the line arriving from well above the level span, as at a flat
                // summit): the ramp lifts the span to meet it, 5 ft at a time.
                var lastTower = rope.Towers[rope.Towers.Count - 1];
                if (lastTower.Head == LiftRope.Head.TooSharp && lastTower.BendLoaded > 0) top = Math.Max(top, 1.524);
                if (top > LiftRope.ClearanceTolerance && raiseT < LiftRope.MaxTerminalRaise - 1e-9)
                {
                    raiseT = Math.Min(LiftRope.MaxTerminalRaise, raiseT + Math.Max(LiftRope.RaiseStep, top));
                    again = true;
                }
                // A bottom still leaving too steeply (its station tower at its 15 ft with no foundation showing): up 1 ft more.
                if (rope.BottomExitEmpty > type.Bottom.AngleMax + 1e-9 && rope.Towers[0].Reveal <= 1e-6 && raiseB < LiftRope.MaxTerminalRaise - 1e-9
                    && envelope.NeededMetres <= LiftLineEnvelope.CapMetres + 1e-9)
                {
                    raiseB = Math.Min(LiftRope.MaxTerminalRaise, raiseB + LiftRope.RaiseStep);
                    again = true;
                }
                // The chairs still short of the bottom low zone's rising line with the trench at its deepest (task 09, SK29's
                // small shortfalls): the bottom terminal up 1 ft, the last resort (TW-16 v3).
                LiftRope.Zones(type, length, out double frontB, out double edgeB, out _, out _);
                if (!again && rope.ShortfallBetween(frontB, edgeB + LiftRope.Clearance * 4, out _) > LiftRope.ClearanceTolerance
                    && rope.Towers[0].Reveal <= 1e-6 && raiseB < LiftRope.MaxTerminalRaise - 1e-9)
                {
                    raiseB = Math.Min(LiftRope.MaxTerminalRaise, raiseB + LiftRope.RaiseStep);
                    again = true;
                }
                if (envelope.NeededMetres > LiftLineEnvelope.CapMetres + 1e-9 && raiseB < LiftRope.MaxTerminalRaise - 1e-9)
                {
                    raiseB = Math.Min(LiftRope.MaxTerminalRaise, raiseB + Steps(envelope.NeededMetres - LiftLineEnvelope.CapMetres));
                    again = true;
                }
                if (!again) break;
            }

            // The search is done: towers and levels are what's stored (TW-15, SK14, levels rounded to whole mm). The earthwork,
            // rope and clearing come from the same search-free function a replay calls (task 09, TL3), so the first build and
            // any later rebuild on the same ground give the same bytes.
            var levels = LiftLevels.FromMetres(bottomLevel + raiseB, topLevel + raiseT);
            var stored = new List<Tower>(towers.Count);
            for (int i = 0; i < towers.Count; i++) stored.Add(new Tower(TowerId.Make(1, i), towers[i].AlongMm, towers[i].HeightMm));
            var work = Earthwork(type, current, bx, by, tx, ty, stored, levels, stance, log);
            return new Result
            {
                Towers = towers, Edits = work.Edits, Rope = work.Rope, BottomPad = work.BottomPad, TopPad = work.TopPad, Envelope = work.Envelope,
                BottomRaise = raiseB, TopRaise = raiseT, BottomLevel = levels.BottomMetres, TopLevel = levels.TopMetres, Levels = levels,
                BottomSteepRaise = steepB, TopSteepRaise = steepT, ClearedCells = work.ClearedCells,
            };
        }

        /// <summary>A lift's earthwork as built from what's stored.</summary>
        public sealed class Works
        {
            public TerrainEdits Edits { get; internal set; }
            public LiftRope Rope { get; internal set; }
            public TerminalPad.Result BottomPad { get; internal set; }
            public TerminalPad.Result TopPad { get; internal set; }
            public LiftLineEnvelope.Result Envelope { get; internal set; }
            public int ClearedCells { get; internal set; }
        }

        /// <summary>
        /// A lift's earthwork from what's stored (task 09; P3-08's replay, TL3): its towers, its terminal levels and its stance,
        /// over <paramref name="current"/>, with no search, so the same lift on the same ground always gives the same bytes:
        /// 1. LF-12 v3's pads at the stored levels, cut only;
        /// 2. the rope on them from the stored towers, and GR-11 v2's trench (a pit under the bottom station tower where its
        ///    stored steel would carry the rope higher than its terminal needs), deepened at most three times while the chairs
        ///    still fall short;
        /// 3. CL-03's corridor and CL-09 round each terminal.
        /// The rope hangs from the stored towers and levels, so ground a trail changes elsewhere under the line (never within
        /// 15 ft of a tower base, TF-11) only changes how much is cut.
        /// </summary>
        public static Works Earthwork(LiftType type, EditedHeights current, double bx, double by, double tx, double ty,
                                      IReadOnlyList<Tower> towers, LiftLevels levels, TowerStance stance, Action<string> log = null)
        {
            if (current == null) throw new ArgumentNullException(nameof(current));
            double length = Math.Sqrt((tx - bx) * (tx - bx) + (ty - by) * (ty - by));
            double ux = (tx - bx) / length, uy = (ty - by) / length;
            double bottomLevel = levels.BottomMetres, topLevel = levels.TopMetres;
            var bottomPad = TerminalPad.Grade(current, bx, by, ux, uy, type.Bottom.Behind, type.Bottom.Ahead, type.Bottom.HalfWidth, bottomLevel);
            var withBottom = new EditedHeights(current.Source, current.Lattice, bottomPad.Edits);
            var topPad = TerminalPad.Grade(withBottom, tx, ty, -ux, -uy, type.Top.Behind, type.Top.Ahead, type.Top.HalfWidth, topLevel);
            var onPads = new EditedHeights(current.Source, current.Lattice, topPad.Edits);
            LiftRope Solve(EditedHeights g) => LiftRope.Solve(type, g, bx, by, tx, ty, towers, stance,
                                                             type.Bottom.ExitAlong, bottomLevel + type.Bottom.ExitRise,
                                                             type.Top.ExitAlong, topLevel + type.Top.ExitRise);
            double stationS = towers.Count > 0 ? towers[0].AlongMm / 1000.0 : 0;
            // The pit (TW-02 v3): where the station tower's stored steel, its foundation showing nothing, carries the rope
            // higher than its terminal's aim, the ground under it comes down by the difference.
            double Pit(LiftRope r)
            {
                if (r.Towers.Count == 0 || r.Towers[0].Reveal > 1e-6) return 0;
                var t0 = r.Towers[0];
                double aim = TowerPlacement.AimedY(type, true, t0.BaseS, type.Bottom.ExitAlong, bottomLevel + type.Bottom.ExitRise,
                                                   length - type.Top.ExitAlong, topLevel + type.Top.ExitRise);
                double pit = t0.BaseY + DesignUnits.StationTowerMinMm / 1000.0 * Math.Cos(t0.Lean) - aim;
                return pit > 0.01 ? pit : 0;
            }
            var rope = Solve(onPads);
            var envelope = LiftLineEnvelope.Grade(onPads, rope, LineGround.Sample(onPads, type, bx, by, tx, ty), type, bx, by, tx, ty, stationS, Pit(rope));
            log?.Invoke($"earthwork: pass 0 pit {Pit(rope):F2} deepest {envelope.DeepestMetres:F2} needed {envelope.NeededMetres:F2}, reveal {rope.Towers[0].Reveal:F2}, exit {rope.BottomExitEmpty * 180 / Math.PI:F2}");
            var final = new EditedHeights(current.Source, current.Lattice, envelope.Edits);
            rope = Solve(final);
            for (int deepen = 0; deepen < 3; deepen++)
            {
                var more = LiftLineEnvelope.Grade(final, rope, LineGround.Sample(final, type, bx, by, tx, ty), type, bx, by, tx, ty,
                                                  stationS, Pit(rope), LiftLineEnvelope.OverCutMetres + 0.25 * deepen);
                log?.Invoke($"earthwork: deepen {deepen} pit {Pit(rope):F2} more {more.DeepestMetres:F2} needed {more.NeededMetres:F2}, reveal {rope.Towers[0].Reveal:F2}, exit {rope.BottomExitEmpty * 180 / Math.PI:F2}, shortfall {rope.Shortfall:F2} at {rope.ShortfallS:F0}");
                if (more.CutCubicMetres <= 0) break;
                envelope = envelope.Then(more);
                final = new EditedHeights(current.Source, current.Lattice, envelope.Edits);
                rope = Solve(final);
            }
            var edits = LiftClearing.Corridor(envelope.Edits, current.Lattice, bx, by, tx, ty, out int cleared);
            edits = LiftClearing.Terminal(edits, current.Lattice, bx, by, ux, uy, type.Bottom.Behind, type.Bottom.Ahead, type.Bottom.HalfWidth, out int aroundB);
            edits = LiftClearing.Terminal(edits, current.Lattice, tx, ty, -ux, -uy, type.Top.Behind, type.Top.Ahead, type.Top.HalfWidth, out int aroundT);
            return new Works
            {
                Edits = edits, Rope = rope, BottomPad = bottomPad, TopPad = topPad, Envelope = envelope,
                ClearedCells = cleared + aroundB + aroundT + bottomPad.ClearedCells + topPad.ClearedCells + envelope.ClearedCells,
            };
        }
    }
}
