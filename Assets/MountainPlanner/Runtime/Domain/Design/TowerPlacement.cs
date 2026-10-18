using System;
using System.Collections.Generic;
using MountainPlanner.Domain.Edits;

namespace MountainPlanner.Domain.Design
{
    /// <summary>
    /// A simple stand-in for TW-12 (task 09 writes the real one), used to author the style tile's towers, with TW-16 v2
    /// (owner, task 07, SK18, SK19):
    /// 1. **Station towers first**, one by each terminal, as close as the terminal allows: 40 ft from the bullwheel, or
    ///    15 ft past the terminal's front if that's farther (TW-03 v2), carrying the rope from 15 ft (TW-02 v2).
    /// 2. **The bottom:** the rope leaves the terminal's row climbing at the line's angle, up to the row's limit; where
    ///    the line climbs steeper, the next tower turns it up further and comes out a hold-down from the bend (TW-04),
    ///    with a second one where one is too sharp (TW-06).
    /// 3. **The top:** the rope reaches the terminal's row gently, a third of the row's limit.
    /// 4. **Line towers** every 250 ft at 35 ft, raised in 5 ft steps or added at the low point while a span's chairs come
    ///    within 15 ft of the ground (TW-01) or a head is too sharp.
    /// 5. **Last resort:** the bottom terminal is raised, in 1 ft steps, while the bottom station tower is already at
    ///    15 ft and the rope still leaves too steeply; the pad then fills up to it (LF-12).
    /// Positions are snapped to 5 ft (TW-12).
    /// </summary>
    public static class TowerPlacement
    {
        public sealed class Result
        {
            public List<TowerSpec> Towers { get; } = new List<TowerSpec>();
            /// <summary>How far the bottom terminal must stand above its pad's natural level (metres; 0 almost always).</summary>
            public double BottomRaise { get; internal set; }
            public LiftRope Rope { get; internal set; }
        }

        const double Grid = 1.524, LineHeight = 10.668, MaxHeight = 30.48, StationDefault = 12.192, PastFront = 4.572;

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
            double stationMin = DesignUnits.StationTowerMinMm / 1000.0;

            // Rounded to the 5 ft grid away from each terminal, so the 15 ft past its front always holds.
            double sb = Math.Ceiling(Math.Max(StationDefault, type.BottomAhead + PastFront) / Grid - 1e-9) * Grid;
            double st = Math.Floor((length - Math.Max(StationDefault, type.TopAhead + PastFront)) / Grid + 1e-9) * Grid;
            double exitS = type.BottomExitAlong, topBack = type.TopExitBack;
            double raise = 0;
            double ExitY() => bottomLevel + raise + type.BottomExitRise;
            double topExitY = topLevel + type.TopExitRise;
            double lineAngle = Math.Atan2(topExitY - ExitY(), (length - topBack) - exitS);
            double climb = Math.Min(type.BottomExitMax * 0.9, Math.Max(type.BottomExitMin * 1.1, lineAngle));
            double Height(double s, double ropeY, double min) => Math.Max(min, Math.Min(MaxHeight, Snap(ropeY - GroundAt(s))));

            var along = new List<double> { sb };
            var heights = new List<double> { Height(sb, ExitY() + (sb - exitS) * Math.Tan(climb), stationMin) };
            for (double s = sb + 76.2; s < st - 40; s += 76.2)
            {
                along.Add(Snap(s));
                heights.Add(LineHeight);
            }
            along.Add(st);
            heights.Add(Height(st, topExitY - (length - topBack - st) * Math.Tan(type.TopEntryMax / 3), stationMin));

            LiftRope rope = null;
            bool Inner(int i) => i > 0 && i < along.Count - 1;
            bool sharpStuck = false;
            int lastStep = 0;   // the bottom station tower's last height step: +1 up, -1 down   // a crest or hollow too sharp for the room left: reported (red), the rest still fixed
            for (int round = 0; round < 500; round++)
            {
                var specs = new List<Tower>();
                for (int i = 0; i < along.Count; i++)
                    specs.Add(new Tower(TowerId.Make(1, i), (int)Math.Round(along[i] * 1000), (int)Math.Round(heights[i] * 1000)));
                rope = LiftRope.Solve(type, ground, bx, by, tx, ty, specs, stance, exitS, ExitY(), topBack, topExitY);
                int last = along.Count - 1;
                // The return's row works between its minimum and maximum exit (TW-16 v2). Close to the terminal a 5 ft
                // step swings the angle more than that window, so when the height flips between too flat and too steep the
                // station tower moves 5 ft farther out and tries again.
                int want = rope.BottomExitAngle < type.BottomExitMin ? 1 : rope.BottomExitAngle > type.BottomExitMax ? -1 : 0;
                if (want != 0 && want == -lastStep && along.Count > 1 && along[0] + Grid < along[1] - 15.24
                    && along[0] + Grid <= DesignUnits.StationZoneMm / 1000.0)
                {
                    along[0] += Grid;
                    lastStep = 0;
                    continue;
                }
                if (rope.BottomExitAngle < type.BottomExitMin)
                {
                    // Too flat for the return's row: the bottom station tower stands taller, so the rope climbs out.
                    lastStep = 1;
                    if (heights[0] + Grid <= MaxHeight) { heights[0] += Grid; continue; }
                    break;
                }
                if (Math.Abs(rope.BottomExitAngle) > type.BottomExitMax)
                {
                    lastStep = rope.BottomExitAngle > 0 ? -1 : 1;
                    double h = heights[0] + (rope.BottomExitAngle > 0 ? -Grid : Grid);
                    if (h >= stationMin - 1e-9 && h <= MaxHeight) { heights[0] = h; continue; }
                    if (rope.BottomExitAngle > 0 && raise < LiftRope.MaxTerminalRaise - 1e-9) { raise += LiftRope.RaiseStep; continue; }
                    break;
                }
                if (Math.Abs(rope.TopEntryAngle) > type.TopEntryMax)
                {
                    double h = heights[last] + (rope.TopEntryAngle > 0 ? Grid : -Grid);
                    if (h < stationMin - 1e-9 || h > MaxHeight) break;
                    heights[last] = h;
                    continue;
                }
                int sharp = -1;
                if (!sharpStuck)
                    for (int i = 0; i < rope.Towers.Count; i++) if (rope.Towers[i].Head == LiftRope.Head.TooSharp) { sharp = i; break; }
                if (rope.LowestClearance >= LiftRope.Clearance && sharp < 0) break;
                double at = sharp >= 0 ? rope.Towers[sharp].BaseS : rope.LowestClearanceS;
                int next = along.FindIndex(x => x > at);
                if (next < 0) next = along.Count;
                int prev = next - 1;
                bool canRaise = (prev < 0 || !Inner(prev) || heights[prev] < 18.29) && (next >= along.Count || !Inner(next) || heights[next] < 18.29)
                                && ((prev >= 0 && Inner(prev)) || (next < along.Count && Inner(next)));
                if (canRaise && sharp < 0)
                {
                    if (prev >= 0 && Inner(prev)) heights[prev] += Grid;
                    if (next < along.Count && Inner(next)) heights[next] += Grid;
                    continue;
                }
                // TW-06: a tower that bends the rope too sharply gets a partner on the side of the bend; a low span a tower.
                double put = sharp >= 0
                    ? Snap(at + (rope.Towers[sharp].BendLoaded > 0 ? -1 : 1) * 15.24)
                    : Snap(at);
                put = Math.Max(sb + 15.24, Math.Min(st - 15.24, put));
                int ins = along.FindIndex(x => x > put);
                if (ins < 0) ins = along.Count;
                bool roomy = (ins == 0 || put - along[ins - 1] >= 15.24) && (ins >= along.Count || along[ins] - put >= 15.24);
                if (roomy)
                {
                    along.Insert(ins, put);
                    heights.Insert(ins, Height(put, LiftRope.RopeAt(rope.Loaded, put), DesignUnits.TowerMinMm / 1000.0));
                    continue;
                }
                int near = prev >= 0 && (next >= along.Count || at - along[prev] < along[next] - at) ? prev : next;
                if (near >= 0 && near < along.Count && Inner(near) && heights[near] < MaxHeight) heights[near] += Grid;
                else if (sharp >= 0) sharpStuck = true;
                else break;
            }
            var result = new Result { BottomRaise = raise, Rope = rope };
            for (int i = 0; i < along.Count; i++)
                result.Towers.Add(new TowerSpec(DesignUnits.FeetToMm((int)Math.Round(along[i] / 0.3048 / 5) * 5),
                                                DesignUnits.FeetToMm((int)Math.Round(heights[i] / 0.3048 / 5) * 5)));
            return result;
        }
    }
}
