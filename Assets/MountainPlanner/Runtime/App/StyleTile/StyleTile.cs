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
    /// loads through save v1 unchanged. Like a player's drawing, only the lines are given: the towers, pads and grading come
    /// from the engine on every build (round 3: no style-tile tuning).
    /// </summary>
    public static class StyleTile
    {
        /// <summary>
        /// The trail's centreline, head first: local x (east), z (north), metres. Traced once down the fall line from
        /// <see cref="Head"/> (<see cref="TraceFallLine"/>, 2026-10-17) and frozen here; empty traces it again.
        /// </summary>
        public static float[] Centreline =
        {
            0.0f, -360.0f, 11.1f, -376.6f, 25.4f, -390.6f, 42.3f, -401.3f,
            61.0f, -408.3f, 79.4f, -416.3f, 96.0f, -427.4f, 112.8f, -438.2f,
            129.3f, -449.5f, 147.4f, -458.2f, 163.9f, -469.4f, 178.8f, -482.8f,
            190.7f, -498.8f, 200.8f, -516.1f, 211.5f, -533.0f, 222.6f, -549.7f,
            233.5f, -566.4f, 243.6f, -583.6f, 252.8f, -601.4f, 263.1f, -618.6f,
            274.2f, -635.2f, 287.7f, -649.9f, 301.7f, -664.2f, 315.4f, -678.8f,
            330.0f, -692.5f, 346.8f, -703.3f, 364.7f, -712.3f, 383.2f, -719.8f,
            400.9f, -729.2f, 419.5f, -736.5f, 438.6f, -742.3f, 458.2f, -746.5f,
            477.4f, -752.1f, 495.2f, -761.1f, 514.6f, -766.2f, 533.1f, -773.8f,
            549.6f, -785.0f, 563.5f, -799.4f, 574.0f, -816.4f, 581.0f, -835.2f,
            590.1f, -853.0f, 595.4f, -872.3f, 604.6f, -890.0f, 610.9f, -909.0f,
        };

        /// <summary>Where the traced trail starts (local metres), and how long it runs.</summary>
        public static float[] Head = { 0f, -360f };
        public const double TraceMetres = 850;

        public const int TrailWidthFeet = 100;
        public const string TrailName = "Style Tile Run";
        public const string LiftName = "Style Tile Quad";

        /// <summary>The lift's bottom and top bullwheels, local metres; empty: beside the trail, <see cref="LiftOffset"/> to its right looking up.</summary>
        public static float[] LiftLine = { 661.0f, -853.2f, 131.9f, -377.7f };
        public const float LiftOffset = 75f;

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
            var graded = TrailGrader.Grade(heights, line, width / 1000.0, true, history.Document.Seed, id);
            var parts = new[] { new FootprintPart(new[] { new Stroke(width, false, points) }, Array.Empty<Cut>()) };
            var trail = new AddTrail(history.Revision, TrailName, TrailSettings.GradedRun, parts, points[0], points[points.Length - 1], WorkStage.Built);
            var r1 = history.Submit(trail, graded.Edits);
            report.AppendFormat(CultureInfo.InvariantCulture,
                "trail {0}: {1:F0} ft, {2} stations ({3} ungraded, {4:F0} ft), cut {5:F0} ydÂ³, fill {6:F0} ydÂ³, {7} cells cleared, edge step {8:F2} m; ",
                r1.Accepted ? "built" : "REJECTED " + r1, graded.LengthMetres / 0.3048, graded.Stations, graded.UngradedStations,
                graded.UngradedMetres / 0.3048, graded.CutCubicMetres * 1.30795, graded.FillCubicMetres * 1.30795, graded.ClearedCells, graded.EdgeStepMetres);

            // The lift.
            if (LiftLine.Length == 0) LiftLine = BesideTheTrail(heights, frame);
            BuildLift(session, frame, models, LiftLine, LiftName, report);
            return report.ToString();
        }

        /// <summary>
        /// A second lift for checking that the same build path works on other ground (round 3): straight up the fall line
        /// from a local point, <paramref name="length"/> metres, built exactly as the style tile's own. Returns its report.
        /// </summary>
        public static string BuildUphill(TerrainEditSession session, LocalFrame frame, MountainPlanner.World.Lifts.LiftModelSet models,
                                         float x, float z, float length, string name)
        {
            var heights = session.Pipeline.Heights;
            var a = frame.ToAlbers(x, z);
            double gx = heights.At(a.X + 30, a.Y) - heights.At(a.X - 30, a.Y), gz = heights.At(a.X, a.Y + 30) - heights.At(a.X, a.Y - 30);
            double g = Math.Sqrt(gx * gx + gz * gz);
            if (g < 1e-6) return "no slope there";
            var line = new[] { x, z, (float)Math.Round(x + gx / g * length, 1), (float)Math.Round(z + gz / g * length, 1) };
            var report = new StringBuilder();
            BuildLift(session, frame, models, line, name, report);
            return report.ToString();
        }

        static void BuildLift(TerrainEditSession session, LocalFrame frame, MountainPlanner.World.Lifts.LiftModelSet models, float[] liftLine,
                              string name, StringBuilder report)
        {
            var history = session.History;
            var heights = session.Pipeline.Heights;
            report.Append("lift line [").Append(Join(liftLine)).Append("]; ");
            var bottom = PointMm.FromMetres(liftLine[0], liftLine[1]);
            var top = PointMm.FromMetres(liftLine[2], liftLine[3]);
            var b = frame.ToAlbers(liftLine[0], liftLine[1]);
            var t = frame.ToAlbers(liftLine[2], liftLine[3]);
            var after = new EditedHeights(heights.Source, heights.Lattice, history.Terrain);
            var type = MountainPlanner.Presentation.Lifts.LiftTypes.Measured(LiftType.Of(LiftCatalogIds.SesselliftFgq4), models);

            // The one build path every lift takes (round 3): pads, towers, any ramp or raise, the envelope, the clearing.
            var built = LiftBuild.Build(type, after, b.X, b.Y, t.X, t.Y, TowerStance.Leaned);
            var rope = built.Rope;
            report.AppendFormat(CultureInfo.InvariantCulture,
                "ends: bottom {0} ({1:F1}/{2:F1}/{3:F1} m), top {4} ({5:F1}/{6:F1}/{7:F1} m); " +
                "pads: bottom cut {8:F0} / fill {9:F0} yd³ (clipped {10:F2} m), top cut {11:F0} / fill {12:F0} yd³ (clipped {13:F2} m); " +
                "raised: bottom {14:F0} ft, top {15:F0} ft; envelope: {16} ",
                type.Bottom.Row, type.Bottom.Behind, type.Bottom.Ahead, type.Bottom.HalfWidth,
                type.Top.Row, type.Top.Behind, type.Top.Ahead, type.Top.HalfWidth,
                built.BottomPad.CutCubicMetres * 1.30795, built.BottomPad.FillCubicMetres * 1.30795, built.BottomPad.ClippedStepMetres,
                built.TopPad.CutCubicMetres * 1.30795, built.TopPad.FillCubicMetres * 1.30795, built.TopPad.ClippedStepMetres,
                built.BottomRaise / 0.3048, built.TopRaise / 0.3048,
                built.Envelope.CutCubicMetres > 0
                    ? string.Format(CultureInfo.InvariantCulture, "{0:F0} yd³, {1:F1} ft deep, {2:F0} ft wide, {3:F0}-{4:F0} ft;",
                        built.Envelope.CutCubicMetres * 1.30795, built.Envelope.DeepestMetres / 0.3048, built.Envelope.WidthMetres / 0.3048,
                        built.Envelope.FromS / 0.3048, built.Envelope.ToS / 0.3048)
                    : "none;");
            report.AppendFormat(CultureInfo.InvariantCulture,
                " rope: leaves the bottom at {0:F1}° loaded / {1:F1}° empty, reaches the top at {2:F1}° / {3:F1}° ({4} TW-16), " +
                "TW-01 v2 {5} (lowest {6:F1} ft at {7:F0} ft, shortfall {8:F1} ft at {9:F0} ft), foundations {10:F1} / {11:F1} ft; ",
                rope.BottomExitLoaded * 180 / Math.PI, rope.BottomExitEmpty * 180 / Math.PI, rope.TopEntryLoaded * 180 / Math.PI,
                rope.TopEntryEmpty * 180 / Math.PI, rope.TerminalsOk ? "within" : "OVER", rope.ClearanceOk ? "OK" : "SHORT",
                rope.LowestClearance / 0.3048, rope.LowestClearanceS / 0.3048, rope.Shortfall / 0.3048, rope.ShortfallS / 0.3048,
                rope.Towers[0].Reveal / 0.3048, rope.Towers[rope.Towers.Count - 1].Reveal / 0.3048);
            var lift = new AddLift(history.Revision, name, LiftCatalogIds.SesselliftFgq4, bottom, top, TowerMode.ByHand, TowerStance.Leaned,
                                   built.Towers, WorkStage.Built);
            var r2 = history.Submit(lift, built.Edits);
            report.AppendFormat(CultureInfo.InvariantCulture, "lift {0}: {1:F0} ft, top tension {2:P0} of rating, {3} towers [{4}], {5} cells cleared",
                r2.Accepted ? "built" : "REJECTED " + r2, Math.Sqrt(bottom.DistanceSquared(top)) / 304.8, rope.TopTensionShare,
                built.Towers.Count, Describe(built.Towers), built.ClearedCells);
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
    }
}
