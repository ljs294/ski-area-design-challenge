using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using MountainPlanner.App.Edits;
using MountainPlanner.Domain.Design;
using MountainPlanner.Domain.Edits;
using MountainPlanner.Domain.Geo;

namespace MountainPlanner.App
{
    /// <summary>
    /// The style tile (task 07 â›³): one graded, cleared trail and one Sessellift FGQ-4 on Jackson Hole 5 km, built by
    /// command with no tool UI (<c>-styletile</c>), so the look is settled before tasks 08 and 09 build the tools.
    /// Everything goes through the design's commands and the terrain edit pipeline, as the tools will:
    /// - the trail: <see cref="AddTrail"/> (built, graded, one 100 ft stroke along the centreline) with the grader's edits
    ///   (<see cref="TrailGrader"/>: GR-01 to GR-08, CL-01, CL-02);
    /// - the lift: <see cref="AddLift"/> (built, Leaned, its towers) with CL-03's corridor.
    /// The points are fixed below (metres from the site's centre), so every run builds the same design, and it saves and
    /// loads through save v1 unchanged. The towers were placed by <see cref="PlaceTowers"/> once and are listed as authored.
    /// </summary>
    public static class StyleTile
    {
        /// <summary>
        /// The trail's centreline, head first: local x (east), z (north), metres. Empty: traced down the fall line from
        /// <see cref="Head"/> (<see cref="TraceFallLine"/>) and logged, so the points can be frozen here.
        /// </summary>
        public static float[] Centreline = Array.Empty<float>();

        /// <summary>Where the traced trail starts (local metres), and how long it runs.</summary>
        public static float[] Head = { 0f, -360f };
        public const double TraceMetres = 850;

        public const int TrailWidthFeet = 100;
        /// <summary>GR-12: the faces' crest and toe rounded over 10 ft (owner SK2; tuned at 07: 5 ft, under two 1 m cells, still stair-stepped on the heightmap).</summary>
        public const double RoundMetres = 3.048;
        public const string TrailName = "Style Tile Run";
        public const string LiftName = "Style Tile Quad";

        /// <summary>The lift's bottom and top bullwheels, local metres; empty: beside the trail, <see cref="LiftOffset"/> to its right looking up.</summary>
        public static float[] LiftLine = Array.Empty<float>();
        public const float LiftOffset = 75f;

        /// <summary>
        /// Where the rope leaves the return (bottom) and meets the drive (top), from the models' rope_*_out sockets: metres
        /// along the line from the bullwheel, and above the ground at the bullwheel (the terminals sit on the ground there).
        /// LiftView solves with the sockets themselves; these match them for placement.
        /// </summary>
        public static double ExitS = 5.889, ExitRise = 3.543, TopBack = 3.1, TopRise = 3.039;

        /// <summary>Towers as (feet along the line from the bottom bullwheel, feet of height).</summary>
        public static (int AlongFeet, int HeightFeet)[] Towers = Array.Empty<(int, int)>();

        /// <summary>
        /// Builds the design into the session's history: the trail with its grading and clearing, then the lift with its
        /// corridor. Returns a one-line report for the log.
        /// </summary>
        public static string Build(TerrainEditSession session, LocalFrame frame, MountainPlanner.World.Lifts.LiftModelSet models = null)
        {
            var history = session.History;
            var heights = session.Pipeline.Heights;
            var report = new StringBuilder();

            // The trail.
            if (Centreline.Length == 0) Centreline = TraceFallLine(heights, frame, Head[0], Head[1], TraceMetres);
            report.Append("centreline [").Append(Join(Centreline)).Append("]; ");
            var line = new double[Centreline.Length];
            var points = new PointMm[Centreline.Length / 2];
            for (int i = 0; i < points.Length; i++)
            {
                var a = frame.ToAlbers(Centreline[2 * i], Centreline[2 * i + 1]);
                line[2 * i] = a.X;
                line[2 * i + 1] = a.Y;
                points[i] = PointMm.FromMetres(Centreline[2 * i], Centreline[2 * i + 1]);
            }
            int width = DesignUnits.FeetToMm(TrailWidthFeet);
            var id = TrailId.Make(history.Document.Seed, history.Document.NextOrdinal);
            var graded = TrailGrader.Grade(heights, line, width / 1000.0, true, history.Document.Seed, id, RoundMetres);
            var parts = new[] { new FootprintPart(new[] { new Stroke(width, false, points) }, Array.Empty<Cut>()) };
            var trail = new AddTrail(history.Revision, TrailName, TrailSettings.GradedRun, parts, points[0], points[points.Length - 1], WorkStage.Built);
            var r1 = history.Submit(trail, graded.Edits);
            report.AppendFormat(CultureInfo.InvariantCulture,
                "trail {0}: {1:F0} ft, {2} stations ({3} ungraded, {4:F0} ft), cut {5:F0} ydÂ³, fill {6:F0} ydÂ³, {7} cells cleared, edge step {8:F2} m; ",
                r1.Accepted ? "built" : "REJECTED " + r1, graded.LengthMetres / 0.3048, graded.Stations, graded.UngradedStations,
                graded.UngradedMetres / 0.3048, graded.CutCubicMetres * 1.30795, graded.FillCubicMetres * 1.30795, graded.ClearedCells, graded.EdgeStepMetres);

            // The lift.
            if (LiftLine.Length == 0) LiftLine = BesideTheTrail(heights, frame);
            report.Append("lift line [").Append(Join(LiftLine)).Append("]; ");
            var bottom = PointMm.FromMetres(LiftLine[0], LiftLine[1]);
            var top = PointMm.FromMetres(LiftLine[2], LiftLine[3]);
            var b = frame.ToAlbers(LiftLine[0], LiftLine[1]);
            var t = frame.ToAlbers(LiftLine[2], LiftLine[3]);
            var after = new EditedHeights(heights.Source, heights.Lattice, history.Terrain);
            if (MountainPlanner.Presentation.Lifts.LiftView.RopeExit(models, "sessellift_fgq4_return", out float ra, out float rr)) { ExitS = ra; ExitRise = rr; }
            if (MountainPlanner.Presentation.Lifts.LiftView.RopeExit(models, "sessellift_fgq4_drive", out float da, out float dr)) { TopBack = da; TopRise = dr; }
            var towers = Towers.Length > 0 ? Authored() : PlaceTowers(after, b, t, ExitS, after.At(b.X, b.Y) + ExitRise, TopBack, after.At(t.X, t.Y) + TopRise);
            var corridor = LiftClearing.Corridor(history.Terrain, heights.Lattice, b.X, b.Y, t.X, t.Y, out int cleared);
            // CL-09: round each terminal, its model's footprint plus the margin and the loading or unloading zone.
            double lx = t.X - b.X, ly = t.Y - b.Y, ll = Math.Sqrt(lx * lx + ly * ly);
            lx /= ll;
            ly /= ll;
            foreach (var (asset, at, sx, sy) in new[] { ("sessellift_fgq4_return", b, lx, ly), ("sessellift_fgq4_drive", t, -lx, -ly) })
            {
                if (!MountainPlanner.Presentation.Lifts.LiftView.TerminalExtent(models, asset, out float behind, out float ahead, out float half))
                    (behind, ahead, half) = (10f, 6f, 4f);   // without the models: about the FGQ-4's size
                corridor = LiftClearing.Terminal(corridor, heights.Lattice, at.X, at.Y, sx, sy, behind, ahead, half, out int around);
                cleared += around;
                report.AppendFormat(CultureInfo.InvariantCulture, "{0} footprint {1:F1} m behind, {2:F1} m ahead, +-{3:F1} m; ", asset, behind, ahead, half);
            }
            var lift = new AddLift(history.Revision, LiftName, LiftCatalogIds.SesselliftFgq4, bottom, top, TowerMode.ByHand, TowerStance.Leaned,
                                   towers, WorkStage.Built);
            var r2 = history.Submit(lift, corridor);
            report.AppendFormat(CultureInfo.InvariantCulture, "lift {0}: {1:F0} ft, rise {2:F0} ft, {3} towers [{4}], corridor {5} cells",
                r2.Accepted ? "built" : "REJECTED " + r2, Math.Sqrt(bottom.DistanceSquared(top)) / 304.8,
                (after.At(t.X, t.Y) - after.At(b.X, b.Y)) / 0.3048, towers.Count, Describe(towers), cleared);
            return report.ToString();
        }

        static string Join(float[] v)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < v.Length; i++) sb.Append(i == 0 ? "" : ", ").Append(v[i].ToString("F1", CultureInfo.InvariantCulture)).Append('f');
            return sb.ToString();
        }

        /// <summary>
        /// The fall line from a local point: 20 m steps down the ground's gradient over Â±15 m, each step turning at most
        /// 12Â° from the last, so the centreline curves as a trail would. Local metres, head first.
        /// </summary>
        public static float[] TraceFallLine(IHeightSource ground, LocalFrame frame, float x, float z, double length)
        {
            var points = new List<float> { x, z };
            double dx = 0, dz = 0;
            for (double walked = 0; walked < length; walked += 20)
            {
                var a = frame.ToAlbers(x, z);
                double gx = ground.At(a.X + 15, a.Y) - ground.At(a.X - 15, a.Y), gz = ground.At(a.X, a.Y + 15) - ground.At(a.X, a.Y - 15);
                double len = Math.Sqrt(gx * gx + gz * gz);
                if (len < 1e-6) break;
                double nx = -gx / len, nz = -gz / len;
                if (dx != 0 || dz != 0)
                {
                    double turn = Math.Atan2(dx * nz - dz * nx, dx * nx + dz * nz);
                    turn = Math.Max(-0.21, Math.Min(0.21, turn));
                    double c = Math.Cos(turn), sn = Math.Sin(turn);
                    nx = dx * c - dz * sn;
                    nz = dx * sn + dz * c;
                }
                dx = nx;
                dz = nz;
                x += (float)(dx * 20);
                z += (float)(dz * 20);
                points.Add((float)Math.Round(x, 1));
                points.Add((float)Math.Round(z, 1));
            }
            return points.ToArray();
        }

        /// <summary>
        /// The lift beside the trail: the bottom at the trail's tail and the top at its head, both moved
        /// <see cref="LiftOffset"/> to the right looking uphill; the top comes down the line until the rise is within about 850 ft (RP-05's ~900 ft for the type).
        /// </summary>
        static float[] BesideTheTrail(IHeightSource ground, LocalFrame frame)
        {
            int n = Centreline.Length / 2;
            float hx = Centreline[0], hz = Centreline[1], tx = Centreline[2 * n - 2], tz = Centreline[2 * n - 1];
            float ux = hx - tx, uz = hz - tz;
            float len = (float)Math.Sqrt(ux * ux + uz * uz);
            ux /= len;
            uz /= len;
            float rx = uz, rz = -ux;   // right of uphill
            float bx = tx + rx * LiftOffset, bz = tz + rz * LiftOffset;
            float top = len;
            double Rise(float d)
            {
                var b = frame.ToAlbers(bx, bz);
                var t = frame.ToAlbers(bx + ux * d, bz + uz * d);
                return ground.At(t.X, t.Y) - ground.At(b.X, b.Y);
            }
            while (top > 300 && Rise(top) > 259) top -= 10;
            return new[] { bx, bz, bx + ux * top, bz + uz * top };
        }

        static List<TowerSpec> Authored()
        {
            var list = new List<TowerSpec>();
            foreach (var (along, height) in Towers) list.Add(new TowerSpec(DesignUnits.FeetToMm(along), DesignUnits.FeetToMm(height)));
            return list;
        }

        static string Describe(List<TowerSpec> towers)
        {
            var sb = new StringBuilder();
            foreach (var tw in towers)
            {
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(Math.Round(tw.AlongMm / 304.8)).Append('/').Append(Math.Round(tw.HeightMm / 304.8));
            }
            return sb.ToString();
        }

        /// <summary>
        /// A simple stand-in for TW-12 (task 09 writes the real one), used to author the style tile's towers. First the
        /// stations' breakovers (TW-16, owner): one 70 ft from each bullwheel, its height set so the rope leaves the bottom
        /// terminal's hold-down row, and reaches the top terminal's support row, within the rows' limits, gently. Then line
        /// towers every 250 ft at 35 ft on a 5 ft grid; while a span's chairs come within 15 ft of the ground (TW-01) or a
        /// head is too sharp, the towers either side of the low point go up 5 ft, or a tower goes in at the low point once
        /// they reach 60 ft; while a terminal's angle is over its limit, its breakover moves 5 ft toward the rope's level.
        /// </summary>
        public static List<TowerSpec> PlaceTowers(IHeightSource ground, AlbersPoint bottom, AlbersPoint top,
                                                  double bottomExitS, double bottomExitY, double topExitBack, double topExitY)
        {
            var type = LiftType.Of(LiftCatalogIds.SesselliftFgq4);
            double length = Math.Sqrt((top.X - bottom.X) * (top.X - bottom.X) + (top.Y - bottom.Y) * (top.Y - bottom.Y));
            double ux = (top.X - bottom.X) / length, uy = (top.Y - bottom.Y) / length;
            double GroundAt(double s) => ground.At(bottom.X + ux * s, bottom.Y + uy * s);
            const double grid = 1.524, minH = 7.62, maxH = 30.48, station = 21.336;   // 5 ft; 25-100 ft (TW-02); 70 ft
            double Snap(double v) => Math.Round(v / grid) * grid;
            double Height(double s, double ropeY) => Math.Max(minH, Math.Min(maxH, Snap(ropeY - GroundAt(s))));
            // The stations' breakovers: the rope from the bottom row climbing at half its limit, into the top row at a third.
            double sb = Snap(station), st = Snap(length - station);
            var along = new List<double> { sb };
            var heights = new List<double> { Height(sb, bottomExitY + (sb - bottomExitS) * Math.Tan(type.BottomExitMax / 2)) };
            for (double s = sb + 76.2; s < st - 40; s += 76.2)
            {
                along.Add(Snap(s));
                heights.Add(10.668);   // 35 ft
            }
            along.Add(st);
            heights.Add(Height(st, topExitY - (length - topExitBack - st) * Math.Tan(type.TopEntryMax / 3)));
            double raise = 0;   // TW-16: the bottom terminal raised when the ground is too steep for its row (owner)
            for (int round = 0; round < 400; round++)
            {
                var specs = new List<Tower>();
                for (int i = 0; i < along.Count; i++) specs.Add(new Tower(TowerId.Make(1, i), (int)Math.Round(along[i] * 1000), (int)Math.Round(heights[i] * 1000)));
                var rope = LiftRope.Solve(type, ground, bottom.X, bottom.Y, top.X, top.Y, specs, TowerStance.Leaned, bottomExitS, bottomExitY + raise, topExitBack, topExitY);
                // TW-16 first: a terminal's angle over its limit moves its breakover toward the rope's level.
                if (Math.Abs(rope.BottomExitAngle) > type.BottomExitMax)
                {
                    double h = heights[0] + (rope.BottomExitAngle > 0 ? -grid : grid);
                    if (h >= minH && h <= maxH) { heights[0] = h; continue; }
                    if (rope.BottomExitAngle > 0 && raise < LiftRope.MaxTerminalRaise) { raise += LiftRope.RaiseStep; continue; }   // owner: raise it
                    break;
                }
                int last = along.Count - 1;
                if (Math.Abs(rope.TopEntryAngle) > type.TopEntryMax)
                {
                    double h = heights[last] + (rope.TopEntryAngle > 0 ? grid : -grid);
                    if (h < minH || h > maxH) break;
                    heights[last] = h;
                    continue;
                }
                int sharp = -1;
                for (int i = 1; i < rope.Towers.Count - 1; i++) if (rope.Towers[i].Head == LiftRope.Head.TooSharp) sharp = i;
                if (rope.LowestClearance >= LiftRope.Clearance && sharp < 0) break;
                double at = sharp >= 0 ? rope.Towers[sharp].BaseS : rope.LowestClearanceS;
                int next = along.FindIndex(a => a > at);
                if (next < 0) next = along.Count;
                int prev = next - 1;
                bool inner(int i) => i > 0 && i < along.Count - 1;   // the stations' breakovers keep their heights
                bool canRaise = (!inner(prev) || heights[prev] < 18.29) && (!inner(next) || heights[next] < 18.29) && (inner(prev) || inner(next));
                if (canRaise && sharp < 0)
                {
                    if (inner(prev)) heights[prev] += grid;
                    if (inner(next)) heights[next] += grid;
                }
                else
                {
                    double put = Snap(Math.Max(sb + 15.24, Math.Min(st - 15.24, at)));
                    if ((prev >= 0 && put - along[prev] < 15.24) || (next < along.Count && along[next] - put < 15.24))
                    {
                        // Too close to a neighbour to add one: raise the nearest line tower instead, up to 100 ft (TW-02).
                        int near = prev >= 0 && (next >= along.Count || at - along[prev] < along[next] - at) ? prev : next;
                        if (inner(near) && heights[near] < maxH) heights[near] += grid; else break;
                    }
                    else
                    {
                        along.Insert(next, put);
                        heights.Insert(next, 10.668);
                    }
                }
            }
            var list = new List<TowerSpec>();
            for (int i = 0; i < along.Count; i++)
                list.Add(new TowerSpec(DesignUnits.FeetToMm((int)Math.Round(along[i] / 0.3048 / 5) * 5), DesignUnits.FeetToMm((int)Math.Round(heights[i] / 0.3048 / 5) * 5)));
            return list;
        }
    }
}
