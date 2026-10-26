using System;
using System.Collections.Generic;
using MountainPlanner.Domain.Edits;

namespace MountainPlanner.Domain.Design
{
    /// <summary>
    /// Fill the rest (LF-04, task 09): towers by hand finished automatically from the last placed tower to the top terminal.
    /// Each next tower goes at the furthest place it can stand (TW-09's green band, the span behind it clear), at TW-12's
    /// modest heights first (35, 30, 40 ft), taller only where none of those fits, until the rope strung to the top
    /// terminal clears the ground (TW-01 v2). Deterministic: the same towers in give the same towers out. A line it can't
    /// finish stops with a named reason, never a silent clearance failure.
    /// </summary>
    public static class TowerFill
    {
        public sealed class Result
        {
            /// <summary>Every tower, the placed ones first, in order up the line.</summary>
            public List<TowerSpec> Towers { get; internal set; }
            /// <summary>How many it added.</summary>
            public int Added { get; internal set; }
            /// <summary>Why it stopped short (TW-12), or null when the line is complete.</summary>
            public LiftProblem? Problem { get; internal set; }
            public bool Ok => Problem == null;
        }

        const int MaxTowers = 200;

        /// <param name="bottomHand">The terminals' levels set by hand (LT19), metres over the ground at their origins: the
        /// towers stand against <see cref="LiftBuild.StartLevels"/>, as <see cref="LiftBuild.BuildWith"/> builds them.</param>
        public static Result Fill(LiftType type, EditedHeights ground, double bx, double by, double tx, double ty, double bottomHand, double topHand,
                                  IReadOnlyList<Tower> placed, TowerStance stance)
        {
            if (type == null) throw new ArgumentNullException(nameof(type));
            if (ground == null) throw new ArgumentNullException(nameof(ground));
            var levels = LiftBuild.StartLevels(type, ground, bx, by, tx, ty, bottomHand, topHand, out _, out _);
            var towers = new List<Tower>(placed ?? Array.Empty<Tower>());
            towers.Sort((a, b) => a.AlongMm.CompareTo(b.AlongMm));
            int kept = towers.Count;
            var line = LineGround.Sample(ground, type, bx, by, tx, ty);
            int maxMm = LiftCatalogIds.TowerMaxMm(type.CatalogId);
            var modest = new[] { DesignUnits.FeetToMm(35), DesignUnits.FeetToMm(30), DesignUnits.FeetToMm(40) };
            double length = Math.Sqrt((tx - bx) * (tx - bx) + (ty - by) * (ty - by));
            // The automatic lift on the same line (TW-12, with TW-16 v3's station towers and their steel): its next tower past
            // the last placed one is taken whenever it checks out as the tower in hand; the green band decides only where it
            // doesn't.
            var auto = LiftBuild.Build(type, ground, bx, by, tx, ty, stance, null, bottomHand, topHand).Towers;
            LiftProblem? problem = null;
            while (towers.Count < MaxTowers)
            {
                var hand = new TowerInHand(type, ground, bx, by, tx, ty, levels, towers, stance, line);
                if (Done(type, hand.Strung, towers.Count, length)) break;
                double first = hand.FirstAlong;
                int next = auto.FindIndex(t => t.AlongMm / 1000.0 >= first - 1e-6);
                if (next >= 0 && hand.Evaluate(auto[next].AlongMm / 1000.0, auto[next].HeightMm).Ok)
                {
                    towers.Add(new Tower(TowerId.Make(3, towers.Count), auto[next].AlongMm, auto[next].HeightMm));
                    continue;
                }
                // The furthest green place at a modest height; else the shortest taller tower that stands anywhere; else, for
                // the bottom station tower, the station heights under 25 ft.
                double bestS = -1;
                int bestH = 0;
                foreach (int h in modest) Furthest(hand, h, ref bestS, ref bestH);
                for (int h = DesignUnits.FeetToMm(45); bestS < 0 && h <= maxMm; h += DesignUnits.FiveFeetMm) Furthest(hand, h, ref bestS, ref bestH);
                for (int h = DesignUnits.FeetToMm(25); bestS < 0 && h >= DesignUnits.StationTowerMinMm; h -= DesignUnits.FiveFeetMm) Furthest(hand, h, ref bestS, ref bestH);
                if (bestS < 0)
                {
                    double from = towers.Count > 0 ? towers[towers.Count - 1].AlongMm / 1000.0 : 0;
                    problem = new LiftProblem("TW-12", $"No tower fits past {Math.Round(from / 0.3048):#,0} ft up the line: move the last tower or a terminal", from);
                    break;
                }
                int alongMm = (int)Math.Round(bestS * 1000 / DesignUnits.FiveFeetMm) * DesignUnits.FiveFeetMm;
                if (towers.Count > 0 && alongMm <= towers[towers.Count - 1].AlongMm) alongMm = towers[towers.Count - 1].AlongMm + DesignUnits.TowerGapMm;
                towers.Add(new Tower(TowerId.Make(3, towers.Count), alongMm, bestH));
            }
            if (towers.Count >= MaxTowers && problem == null)
                problem = new LiftProblem("TW-12", "Too many towers: this line can't be finished automatically", 0);
            var specs = new List<TowerSpec>(towers.Count);
            foreach (var t in towers) specs.Add(t.Spec);
            return new Result { Towers = specs, Added = towers.Count - kept, Problem = problem };
        }

        // The line is complete when the chairs clear the ground past the bottom station (its first span and low zone), fall
        // short there by no more than GR-11 v2's trench digs at Break ground (the green band's own allowance for it), and the
        // rope comes into the top terminal within its row's range.
        /// <summary>Whether the towers placed so far finish the line (the tool's Enter: on to the review).</summary>
        public static bool IsComplete(LiftType type, TowerInHand hand, int placed, double length) => Done(type, hand.Strung, placed, length);

        static bool Done(LiftType type, LiftRope strung, int towers, double length)
        {
            if (towers == 0 || strung.Towers.Count == 0) return false;
            LiftRope.Zones(type, length, out _, out double edgeB, out _, out _);
            double station = Math.Max(strung.Towers[0].RopeS, edgeB + LiftRope.Clearance * 4);
            return strung.ShortfallBetween(station, length, out _) <= LiftRope.ClearanceTolerance
                && strung.ShortfallBetween(0, station, out _) <= LiftLineEnvelope.CapMetres
                // ... and the top terminal's row takes the rope: a top station tower's foundation aims it at Break ground
                // (TW-16 v3); past a line tower it must already (no terminal raise makes up for it now, LT34).
                && (TopStation(type, strung, length) || type.Top.Takes(-strung.TopEntryLoaded) && type.Top.Takes(-strung.TopEntryEmpty));
        }

        static bool TopStation(LiftType type, LiftRope strung, double length) =>
            !type.Top.OwnStation && length - strung.Towers[strung.Towers.Count - 1].BaseS <= DesignUnits.StationZoneMm / 1000.0 + 1e-9;

        static void Furthest(TowerInHand hand, int heightMm, ref double bestS, ref int bestH)
        {
            var band = hand.Band(heightMm);
            if (band.Count == 0) return;
            double s = band[band.Count - 1].To;
            if (s > bestS + 1e-6) { bestS = s; bestH = heightMm; }
        }
    }
}
