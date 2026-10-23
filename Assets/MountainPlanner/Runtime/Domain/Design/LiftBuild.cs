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

            /// <summary>Why the lift is red (blocking: TW-01 v2, LC-07) or amber (TW-05, TW-06, TW-16 v3, LF-08, LF-12), each with its
            /// rule (LC-09, owner, LT14); empty when it's neither.</summary>
            public List<LiftProblem> Problems => _problems ??= With(LiftProblems.For(Rope, BottomPad.ClippedStepMetres, TopPad.ClippedStepMetres,
                                                                                     BottomSiteVariation, TopSiteVariation), Hints, Unplaced);
            /// <summary>LT34: where the line needs a terminal higher or lower than the player set it, by how much (amber).</summary>
            public List<LiftProblem> Hints { get; internal set; } = new List<LiftProblem>();
            /// <summary>LT34: how much higher (+) or lower (-) each terminal should stand for its row to take the rope and its
            /// chairs to clear, metres in 1 ft steps; 0 where it's fine (or nothing within reach fixes it).</summary>
            public double SuggestedBottomMetres { get; internal set; }
            public double SuggestedTopMetres { get; internal set; }
            /// <summary>TW-12 (LT40): why automatic placement left the chairs short where no level hint puts it right (red).</summary>
            public List<LiftProblem> Unplaced { get; internal set; } = new List<LiftProblem>();
            static List<LiftProblem> With(List<LiftProblem> a, List<LiftProblem> b, List<LiftProblem> c)
            {
                if (b != null) a.AddRange(b);
                if (c != null) a.AddRange(c);
                return a;
            }
            List<LiftProblem> _problems;

            /// <summary>Nothing blocks it (LC-09): it can be built; amber warnings may remain.</summary>
            public bool Ok => !LiftProblems.AnyBlocks(Problems);
            /// <summary>Within every rule, amber ones too.</summary>
            public bool Clean => Problems.Count == 0;
            /// <summary>How much the natural ground varies under each terminal's footprint (LF-08), metres.</summary>
            public double BottomSiteVariation { get; internal set; }
            public double TopSiteVariation { get; internal set; }
        }

        /// <summary>
        /// Each terminal's level where nothing raised it: the ground at its origin (LF-12), whole millimetres, as the build starts
        /// from. A lift from a v1 save, whose levels weren't stored, stands at these.
        /// </summary>
        public static LiftLevels GroundLevels(LiftType type, IHeightSource ground, double bx, double by, double tx, double ty)
        {
            double length = Math.Sqrt((tx - bx) * (tx - bx) + (ty - by) * (ty - by));
            double ux = (tx - bx) / length, uy = (ty - by) / length;
            return LiftLevels.FromMetres(ground.At(bx + ux * type.Bottom.OriginAlong, by + uy * type.Bottom.OriginAlong),
                                         ground.At(tx - ux * type.Top.OriginAlong, ty - uy * type.Top.OriginAlong));
        }

        /// <summary>LF-08 v2: a terminal cuts at most 10 ft into the hill; on a steeper site its pad is levelled with cut and fill (LF-12 v4).</summary>
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

        /// <summary>
        /// Each terminal's level before any search (LT19, LF-08 v2): the ground at its origin plus the level set by hand,
        /// raised in 1 ft steps where the site is steep until it cuts at most 10 ft into the hill. What towers by hand stand
        /// against (task 09), and where <see cref="Build"/> starts.
        /// </summary>
        public static LiftLevels StartLevels(LiftType type, IHeightSource current, double bx, double by, double tx, double ty,
                                             double bottomHand, double topHand, out double steepB, out double steepT)
        {
            double length = Math.Sqrt((tx - bx) * (tx - bx) + (ty - by) * (ty - by));
            double ux = (tx - bx) / length, uy = (ty - by) / length;
            double bottomLevel = current.At(bx + ux * type.Bottom.OriginAlong, by + uy * type.Bottom.OriginAlong) + bottomHand;
            double topLevel = current.At(tx - ux * type.Top.OriginAlong, ty - uy * type.Top.OriginAlong) + topHand;
            // LT34 (LF-12 v4): no raise for a steep site; the pad flattens under the terminal instead.
            steepB = steepT = 0;
            return LiftLevels.FromMetres(bottomLevel, topLevel);
        }

        /// <summary>
        /// Towers by hand (LF-04, task 09): the lift built on the towers given, with no search. Its terminals stand at
        /// <see cref="StartLevels"/>; the pads, trench and clearing come from <see cref="Earthwork"/>, as a replay's do. A
        /// line the towers don't finish is red with its reasons (<see cref="Result.Problems"/>), never changed behind the
        /// player's back.
        /// </summary>
        public static Result BuildWith(LiftType type, EditedHeights current, double bx, double by, double tx, double ty,
                                       IReadOnlyList<TowerSpec> towers, TowerStance stance, double bottomHand = 0, double topHand = 0)
        {
            if (current == null) throw new ArgumentNullException(nameof(current));
            if (towers == null || towers.Count == 0) throw new ArgumentException("Towers by hand need at least one tower.", nameof(towers));
            double length = Math.Sqrt((tx - bx) * (tx - bx) + (ty - by) * (ty - by));
            double ux = (tx - bx) / length, uy = (ty - by) / length;
            var levels = StartLevels(type, current, bx, by, tx, ty, bottomHand, topHand, out double steepB, out double steepT);
            return BuildWith(type, current, bx, by, tx, ty, towers, stance, levels, steepB, steepT);
        }

        /// <summary>Edit lift (LT2): the lift built on the towers and terminal levels given (absolute, as stored), no search.</summary>
        public static Result BuildWith(LiftType type, EditedHeights current, double bx, double by, double tx, double ty,
                                       IReadOnlyList<TowerSpec> towers, TowerStance stance, LiftLevels levels, double steepB = 0, double steepT = 0)
        {
            if (current == null) throw new ArgumentNullException(nameof(current));
            if (towers == null || towers.Count == 0) throw new ArgumentException("A lift needs at least one tower.", nameof(towers));
            double length = Math.Sqrt((tx - bx) * (tx - bx) + (ty - by) * (ty - by));
            double ux = (tx - bx) / length, uy = (ty - by) / length;
            var stored = new List<Tower>(towers.Count);
            for (int i = 0; i < towers.Count; i++) stored.Add(new Tower(TowerId.Make(1, i), towers[i].AlongMm, towers[i].HeightMm));
            stored.Sort((a, b) => a.AlongMm.CompareTo(b.AlongMm));
            var specs = new List<TowerSpec>(stored.Count);
            foreach (var t in stored) specs.Add(new TowerSpec(t.AlongMm, t.HeightMm));
            var work = Earthwork(type, current, bx, by, tx, ty, stored, levels, stance);
            return new Result
            {
                Hints = LevelHints(type, new EditedHeights(current.Source, current.Lattice, work.Edits), bx, by, tx, ty, stored, levels, stance, work, out double sb, out double st),
                SuggestedBottomMetres = sb, SuggestedTopMetres = st,
                Towers = specs, Edits = work.Edits, Rope = work.Rope, BottomPad = work.BottomPad, TopPad = work.TopPad, Envelope = work.Envelope,
                BottomRaise = steepB, TopRaise = steepT, BottomLevel = levels.BottomMetres, TopLevel = levels.TopMetres, Levels = levels,
                BottomSteepRaise = steepB, TopSteepRaise = steepT, ClearedCells = work.ClearedCells,
                BottomSiteVariation = SiteVariation(current, type.Bottom, bx, by, ux, uy),
                TopSiteVariation = SiteVariation(current, type.Top, tx, ty, -ux, -uy),
            };
        }

        /// <param name="bottomHand">How far the player set the bottom terminal above (or below) the ground at its origin
        /// while placing it (LT19, metres); the build starts there and may still raise it as a last resort.</param>
        public static Result Build(LiftType type, EditedHeights current, double bx, double by, double tx, double ty, TowerStance stance,
                                   Action<string> log = null, double bottomHand = 0, double topHand = 0)
        {
            if (current == null) throw new ArgumentNullException(nameof(current));
            double length = Math.Sqrt((tx - bx) * (tx - bx) + (ty - by) * (ty - by));
            double ux = (tx - bx) / length, uy = (ty - by) / length;
            double bottomLevel = current.At(bx + ux * type.Bottom.OriginAlong, by + uy * type.Bottom.OriginAlong) + bottomHand;
            double topLevel = current.At(tx - ux * type.Top.OriginAlong, ty - uy * type.Top.OriginAlong) + topHand;
            // LT34 (owner, 2026-10-20; LF-12 v4): a terminal stands where it's placed, at its ground plus the level set by hand.
            // The build never raises it (LF-08 v2's steep raise and the last resorts below are gone); its pad flattens under it,
            // and where the line needs it higher or lower the review says by how much (LevelHints).
            double steepB = 0, steepT = 0, raiseB = 0, raiseT = 0;
            TerminalPad.Result bottomPad = null, topPad = null;
            TowerPlacement.Result placed = null;
            LiftLineEnvelope.Result envelope = null;
            EditedHeights onPads = null;
            Works lastWork = null;
            LiftRope.Zones(type, length, out _, out _, out double edgeT, out double frontT);
            double Steps(double metres) => Math.Ceiling(metres / LiftRope.RaiseStep - 1e-9) * LiftRope.RaiseStep;
            List<TowerSpec> towers = null;
            LiftRope rope = null;
            // LT34: the first attempt, at the player's levels, is what's built. Where it isn't within the rules the old last
            // resorts carry on, only to work out how much higher the terminals would have to stand: the review's suggestion.
            const int Attempts = 8;
            List<TowerSpec> asPlaced = null;
            TowerPlacement.Result placedFirst = null;
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
                    steel = Math.Max(DesignUnits.StationTowerMinMm / 1000.0, Math.Min(type.TowerMaxMetres, steel));
                    return DesignUnits.FeetToMm((int)Math.Round(steel / 0.3048));
                }
                // TW-16 v4: past a terminal with its own station the first tower is a line tower (its height is placement's).
                bool ownB = type.Bottom.OwnStation;
                if (!ownB) specs[0] = new Tower(specs[0].Id, specs[0].AlongMm, SteelMm(specs[0], placed.BottomStationY, placed.BottomStationLean));
                if (last > 0 || ownB && last >= 0) specs[last] = new Tower(specs[last].Id, specs[last].AlongMm, SteelMm(specs[last], placed.TopStationY, placed.TopStationLean));
                int Step(int i, double reveal, double over)
                {
                    const double tolerance = 0.1 * Math.PI / 180;
                    int h = specs[i].HeightMm;
                    if (reveal <= 1e-6 && over > tolerance && h - DesignUnits.FiveFeetMm >= DesignUnits.StationTowerMinMm) return -1;
                    if (reveal >= LiftRope.MaxReveal - 1e-3 && over < -tolerance && h + DesignUnits.FiveFeetMm <= type.TowerMaxMetres * 1000 + 0.5) return 1;
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
                    int s0 = ownB ? 0 : Step(0, r.Towers[0].Reveal, r.BottomExitEmpty - type.Bottom.Aim);
                    if (s0 != 0) { specs[0] = Restep(specs[0], s0); changed = true; }
                    if (last > 0 || ownB && last >= 0)
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
                if (attempt == 0)
                {
                    asPlaced = towers;
                    placedFirst = placed;
                }

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
                // Past a terminal with its own station (TW-16 v4) the first line tower is already as low as the chairs allow:
                // up by what it would take for the rope to leave at the row's aim.
                bool ownStation = type.Bottom.OwnStation;
                if (rope.BottomExitEmpty > type.Bottom.AngleMax + 1e-9 && (ownStation || rope.Towers[0].Reveal <= 1e-6) && raiseB < LiftRope.MaxTerminalRaise - 1e-9
                    && envelope.NeededMetres <= LiftLineEnvelope.CapMetres + 1e-9)
                {
                    double up = LiftRope.RaiseStep;
                    if (ownStation)
                    {
                        var t0 = rope.Towers[0];
                        double exitY = bottomLevel + raiseB + type.Bottom.ExitRise;
                        up = Math.Max(up, Steps(t0.RopeY - exitY - Math.Tan(type.Bottom.Aim) * (t0.RopeS - type.Bottom.ExitAlong)));
                    }
                    raiseB = Math.Min(LiftRope.MaxTerminalRaise, raiseB + up);
                    again = true;
                }
                // The chairs still short of the bottom low zone's rising line with the trench at its deepest (task 09, SK29's
                // small shortfalls): the bottom terminal up 1 ft, the last resort (TW-16 v3).
                LiftRope.Zones(type, length, out double frontB, out double edgeB, out _, out _);
                // Past a terminal with its own station the first tower's height is the exit's, so the terminal goes up by the
                // shortfall at once.
                double lowB = rope.ShortfallBetween(frontB, edgeB + LiftRope.Clearance * 4, out _);
                if (!again && lowB > LiftRope.ClearanceTolerance
                    && (ownStation || envelope.DeepestMetres >= LiftLineEnvelope.CapMetres - 0.1 || attempt >= 2)   // the trench at its cap, or no further for two attempts
                    && raiseB < LiftRope.MaxTerminalRaise - 1e-9)
                {
                    raiseB = Math.Min(LiftRope.MaxTerminalRaise, raiseB + (ownStation ? Math.Max(LiftRope.RaiseStep, Steps(lowB)) : LiftRope.RaiseStep));
                    again = true;
                }
                if (envelope.NeededMetres > LiftLineEnvelope.CapMetres + 1e-9 && raiseB < LiftRope.MaxTerminalRaise - 1e-9)
                {
                    raiseB = Math.Min(LiftRope.MaxTerminalRaise, raiseB + Steps(envelope.NeededMetres - LiftLineEnvelope.CapMetres));
                    again = true;
                }
                if (!again) break;
            }
            // What the search found would fit (the suggestion), then back to the lift as placed.
            bool fits = rope.Shortfall <= LiftRope.ClearanceTolerance && rope.TerminalsOk && envelope.NeededMetres <= LiftLineEnvelope.CapMetres + 1e-9;
            double needB = fits ? raiseB : double.NaN, needT = fits ? raiseT : double.NaN;
            towers = asPlaced;
            raiseB = raiseT = 0;

            // The search is done: towers and levels are what's stored (TW-15, SK14, levels rounded to whole mm). The earthwork,
            // rope and clearing come from the same search-free function a replay calls (task 09, TL3), so the first build and
            // any later rebuild on the same ground give the same bytes.
            var levels = LiftLevels.FromMetres(bottomLevel + raiseB, topLevel + raiseT);
            var stored = new List<Tower>(towers.Count);
            for (int i = 0; i < towers.Count; i++) stored.Add(new Tower(TowerId.Make(1, i), towers[i].AlongMm, towers[i].HeightMm));
            var work = Earthwork(type, current, bx, by, tx, ty, stored, levels, stance, log);
            var built = new EditedHeights(current.Source, current.Lattice, work.Edits);
            var hints = LevelHints(type, built, bx, by, tx, ty, stored, levels, stance, work, out double sb, out double st, needB, needT);
            // TW-12 (LT40): a stretch still short that no level hint puts right says why placement couldn't.
            LiftRope.Zones(type, length, out _, out double bottomEdge, out _, out _);
            var unplaced = work.Rope.ClearanceOk ? new List<LiftProblem>()
                : LiftProblems.Unplaced(work.Rope, LineGround.Sample(built, type, bx, by, tx, ty), levels.BottomMetres, levels.TopMetres, placedFirst.GaveUp,
                                        sb != 0 ? bottomEdge + LiftRope.Clearance * 4 : double.NegativeInfinity, st != 0 ? edgeT : double.PositiveInfinity);
            return new Result
            {
                Hints = hints, Unplaced = unplaced,
                SuggestedBottomMetres = sb, SuggestedTopMetres = st,
                Towers = towers, Edits = work.Edits, Rope = work.Rope, BottomPad = work.BottomPad, TopPad = work.TopPad, Envelope = work.Envelope,
                BottomRaise = raiseB, TopRaise = raiseT, BottomLevel = levels.BottomMetres, TopLevel = levels.TopMetres, Levels = levels,
                BottomSteepRaise = steepB, TopSteepRaise = steepT, ClearedCells = work.ClearedCells,
                BottomSiteVariation = SiteVariation(current, type.Bottom, bx, by, ux, uy),
                TopSiteVariation = SiteVariation(current, type.Top, tx, ty, -ux, -uy),
            };
        }

        /// <summary>
        /// LT34: where the line needs a terminal at another level than the player set, how much: the rope leaving the bottom
        /// row too steeply (higher) or too flat (lower), the chairs short in the unload zone (the top higher), or the trench
        /// past its cap (the bottom higher). Found by solving the rope again with the end 1 ft higher (or lower) at a time, up
        /// to 30 ft (10 ft down). Amber: the player decides (Page Up / Page Down).
        /// </summary>
        static List<LiftProblem> LevelHints(LiftType type, EditedHeights ground, double bx, double by, double tx, double ty,
                                            IReadOnlyList<Tower> towers, LiftLevels levels, TowerStance stance, Works work,
                                            out double suggestBottom, out double suggestTop, double knownB = double.NaN, double knownT = double.NaN)
        {
            // Automatic: the build's own search says how much higher would fit (towers placed again at that level); by hand
            // (or where it found nothing) the towers stay and each end is tried 1 ft at a time.
            int Known(double m) => double.IsNaN(m) || m <= 1e-9 ? 0 : (int)Math.Ceiling(m / 0.3048 - 1e-6);
            suggestBottom = suggestTop = 0;
            var hints = new List<LiftProblem>();
            double length = Math.Sqrt((tx - bx) * (tx - bx) + (ty - by) * (ty - by));
            LiftRope.Zones(type, length, out double frontB, out double edgeB, out double edgeT, out double frontT);
            // A station tower's head follows its terminal (the rebuild sets its steel from the terminal's level), so it moves
            // with the end being tried.
            double zone = DesignUnits.StationZoneMm / 1000.0;
            int n = towers.Count;
            bool stationB = !type.Bottom.OwnStation && n > 0 && towers[0].AlongMm / 1000.0 <= zone + 1e-9;
            bool stationT = !type.Top.OwnStation && n > (stationB ? 1 : 0) && length - towers[n - 1].AlongMm / 1000.0 <= zone + 1e-9;
            var moved = new List<Tower>(towers);
            Tower Up(Tower w, double m) => new Tower(w.Id, w.AlongMm, Math.Max(DesignUnits.StationTowerMinMm, w.HeightMm + (int)Math.Round(m * 1000)));
            LiftRope Solve(double b, double t)
            {
                if (stationB) moved[0] = Up(towers[0], b);
                if (stationT) moved[n - 1] = Up(towers[n - 1], t);
                return LiftRope.Solve(type, ground, bx, by, tx, ty, moved, stance, type.Bottom.ExitAlong,
                                      levels.BottomMetres + b + type.Bottom.ExitRise, type.Top.ExitAlong, levels.TopMetres + t + type.Top.ExitRise);
            }
            bool BottomOk(LiftRope r) => type.Bottom.Takes(r.BottomExitLoaded) && type.Bottom.Takes(r.BottomExitEmpty);
            // The chairs clear from the bottom pad's edge to a little past the zone (TW-16 v3's old last resort's stretch).
            bool BottomClear(LiftRope r) => r.ShortfallBetween(frontB, edgeB + LiftRope.Clearance * 4, out _) <= LiftRope.ClearanceTolerance;
            bool TopClear(LiftRope r) => r.ShortfallBetween(edgeT, frontT, out _) <= LiftRope.ClearanceTolerance;
            bool TopTakes(LiftRope r) => type.Top.Takes(-r.TopEntryLoaded) && type.Top.Takes(-r.TopEntryEmpty);
            bool TopOk(LiftRope r) => TopClear(r) && TopTakes(r);
            // Every 3 ft until it fits, then back a foot or two to the least that does: a dozen rope solves, not 30. (Not halved:
            // what fits can be a window, too low and the rope leaves too steeply, too high and too flat.)
            int Find(Func<double, bool> ok, int dir, int max)
            {
                for (int ft = Math.Min(3, max); ; ft = Math.Min(max, ft + 3))
                {
                    if (ok(dir * ft * 0.3048))
                    {
                        int least = ft;
                        for (int back = ft - 1; back >= Math.Max(1, ft - 2) && ok(dir * back * 0.3048); back--) least = back;
                        return dir * least;
                    }
                    if (ft == max) return 0;
                }
            }
            string Ft(int ft) => Math.Abs(ft) + " ft";
            var rope = work.Rope;
            // A line whose chairs clear and whose rows take the rope needs nothing: the hints only say how to put a red or
            // amber line right (a trench past its cap shows up as the chairs short).
            if (rope.ClearanceOk && rope.TerminalsOk) return hints;
            double steep = Math.Max(rope.BottomExitLoaded, rope.BottomExitEmpty) * 180 / Math.PI, flat = Math.Min(rope.BottomExitLoaded, rope.BottomExitEmpty) * 180 / Math.PI;
            if (towers.Count > 0 && !(BottomOk(rope) && BottomClear(rope)))
            {
                // Too steep or the chairs low: higher; too flat: lower. Both put right where it can be, else the angle alone.
                bool angled = !BottomOk(rope), tooSteep = steep > type.Bottom.AngleMax * 180 / Math.PI + 1e-6;
                int dir = angled && !tooSteep ? -1 : 1, max = dir > 0 ? 30 : 10;
                int ft = dir > 0 ? Known(knownB) : 0;
                if (ft == 0) ft = Find(m => { var r = Solve(m, 0); return BottomOk(r) && BottomClear(r); }, dir, max);
                if (ft == 0 && angled) ft = Find(m => BottomOk(Solve(m, 0)), dir, max);
                string why = !angled ? "The chairs come in low past the bottom terminal"
                    : tooSteep ? $"The rope leaves the bottom terminal at {steep:F0}°, steeper than its row takes ({type.Bottom.AngleMax * 180 / Math.PI:F0}°)"
                               : $"The rope leaves the bottom terminal at {flat:F0}°, flatter than its row takes ({type.Bottom.AngleMin * 180 / Math.PI:F0}°)";
                suggestBottom = ft * 0.3048;
                hints.Add(new LiftProblem("LT34", ft != 0
                    ? $"{why}: {(ft > 0 ? "raise" : "lower")} it {Ft(ft)} ({(ft > 0 ? "Page Up" : "Page Down")})"
                    : $"{why}: move a tower or the terminal", 0, false));
            }
            if (towers.Count > 0 && !TopOk(rope))
            {
                // Up first (a flat summit's unload ramp, TW-16 v3's old last resort), then down.
                double atBottom = suggestBottom;
                int ft = Known(knownT);
                if (ft == 0) ft = Find(m => TopOk(Solve(atBottom, m)), 1, 30);
                if (ft == 0) ft = Find(m => TopOk(Solve(atBottom, m)), -1, 10);
                suggestTop = ft * 0.3048;
                string why = TopTakes(rope) ? "The chairs come in low at the top terminal"
                    : $"The rope comes into the top terminal at {Math.Abs(Math.Min(rope.TopEntryLoaded, rope.TopEntryEmpty)) * 180 / Math.PI:F0}°, more than its row takes";
                hints.Add(new LiftProblem("LT34", ft != 0 ? $"{why}: {(ft > 0 ? "raise" : "lower")} it {Ft(ft)} ({(ft > 0 ? "Page Up" : "Page Down")})"
                                                          : $"{why}: move a tower or the terminal", length, false));
            }
            if (work.Envelope != null && work.Envelope.NeededMetres > LiftLineEnvelope.CapMetres + 1e-9)
            {
                int ft = (int)Math.Ceiling((work.Envelope.NeededMetres - LiftLineEnvelope.CapMetres) / 0.3048 - 1e-9);
                suggestBottom = Math.Max(suggestBottom, ft * 0.3048);
                hints.Add(new LiftProblem("LT34", $"The chairs need a trench deeper than 10 ft below the bottom terminal: raise it {Ft(ft)} (Page Up)", 0, false));
            }
            return hints;
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
            // The trench starts at the bottom station tower; past a terminal with its own station (TW-16 v4), at its exit.
            double stationS = type.Bottom.OwnStation ? type.Bottom.ExitAlong : towers.Count > 0 ? towers[0].AlongMm / 1000.0 : 0;
            // The pit (TW-02 v3): where the station tower's stored steel, its foundation showing nothing, carries the rope
            // higher than its terminal's aim, the ground under it comes down by the difference.
            double Pit(LiftRope r)
            {
                if (type.Bottom.OwnStation) return 0;   // no station tower to dig under (TW-16 v4)
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
