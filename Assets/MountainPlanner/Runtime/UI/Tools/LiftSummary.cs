using System;
using System.Collections.Generic;
using System.Text;
using MountainPlanner.Domain.Design;

namespace MountainPlanner.UI.Tools
{
    /// <summary>
    /// LD3 (owner, 2026-11-01): the figures New lift and Edit lift show while a lift is built: its ride and capacity, and a
    /// summary of its towers (how many, the tallest, the longest span and the heads), numbered T1…Tn as the profile strip
    /// and the 3D view number them. Made when the build changes, never per frame.
    /// </summary>
    public static class LiftSummary
    {
        /// <summary>The ride: its time and the rope speed, over the slope length of a line <paramref name="run"/> across and
        /// <paramref name="rise"/> up, in metres.</summary>
        public static string Ride(LiftType type, double run, double rise) =>
            $"{ToolFormat.Duration(type.RideSeconds(Math.Sqrt(run * run + rise * rise)))} at {type.SpeedMetresPerSecond * 196.850394:#,0} ft/min";

        /// <summary>People an hour, with a detachable's loading interval.</summary>
        public static string Capacity(LiftType type) =>
            type.Detachable ? $"{type.Capacity:#,0} an hour · a chair every {type.IntervalSeconds:0.#} s" : $"{type.Capacity:#,0} an hour";

        /// <summary>A head's short name in the summary (S4: support, 4 sheaves; B: breakover; D: hold-down; C: combination).</summary>
        public static string HeadCode(LiftRope.Head head) => head switch
        {
            LiftRope.Head.S4 => "S4",
            LiftRope.Head.S6 => "S6",
            LiftRope.Head.S10 => "S10",
            LiftRope.Head.B8 => "B8",
            LiftRope.Head.B10 => "B10",
            LiftRope.Head.D8 => "D8",
            LiftRope.Head.D10 => "D10",
            LiftRope.Head.C8 => "C8",
            _ => "too sharp",
        };

        /// <summary>
        /// "8 towers · tallest 55 ft (T4) · longest span 410 ft (T5–T6) · 5 S4, 2 B8, 1 D8": the towers as placed, with the
        /// rope's heads when <paramref name="rope"/> was solved over them (null or stale: the count and heights alone).
        /// </summary>
        public static string Towers(IReadOnlyList<Tower> towers, LiftRope rope, double length) =>
            Towers(towers?.Count ?? 0, i => towers[i].HeightMm, rope, length);

        /// <summary>The same for a build's towers (<see cref="LiftBuild.Result.Towers"/>).</summary>
        public static string Towers(IReadOnlyList<TowerSpec> towers, LiftRope rope, double length) =>
            Towers(towers?.Count ?? 0, i => towers[i].HeightMm, rope, length);

        static string Towers(int count, Func<int, int> heightMm, LiftRope rope, double length)
        {
            if (count == 0) return "none";
            var text = new StringBuilder().Append(count).Append(count == 1 ? " tower" : " towers");
            int tallest = 0;
            for (int i = 1; i < count; i++) if (heightMm(i) > heightMm(tallest)) tallest = i;
            text.Append(" · tallest ").Append(ToolFormat.Feet((int)Math.Round(heightMm(tallest) / 304.8))).Append(" (T").Append(tallest + 1).Append(')');
            if (rope == null || rope.Towers.Count != count) return text.ToString();

            // Spans from where the rope leaves the bottom terminal, over each tower, to where it meets the top one.
            double from = rope.Type.Bottom.ExitAlong, longest = -1;
            int at = 0;
            for (int i = 0; i <= count; i++)
            {
                double to = i < count ? rope.Towers[i].RopeS : length - rope.Type.Top.ExitAlong;
                if (to - from > longest) { longest = to - from; at = i; }
                from = to;
            }
            string a = at == 0 ? "bottom" : "T" + at, b = at == count ? "top" : "T" + (at + 1);
            text.Append(" · longest span ").Append(ToolFormat.Length(longest)).Append(" (").Append(a).Append('–').Append(b).Append(')');

            // The heads, in the order the kit lists them.
            var counts = new int[Enum.GetValues(typeof(LiftRope.Head)).Length];
            foreach (var t in rope.Towers) counts[(int)t.Head]++;
            string sep = " · ";
            foreach (LiftRope.Head h in new[] { LiftRope.Head.S4, LiftRope.Head.S6, LiftRope.Head.S10, LiftRope.Head.B8, LiftRope.Head.B10,
                                                LiftRope.Head.C8, LiftRope.Head.D8, LiftRope.Head.D10, LiftRope.Head.TooSharp })
            {
                if (counts[(int)h] == 0) continue;
                text.Append(sep).Append(counts[(int)h]).Append(' ').Append(HeadCode(h));
                sep = ", ";
            }
            // LD9: the top terminal's row where it has a choice (4, 6 or 8 sheaves a side, picked from the summit).
            if (rope.Type.TopTrains != null) text.Append(" · top row ").Append(rope.Type.Top.Sheaves).Append(" sheaves");
            return text.ToString();
        }
    }
}
