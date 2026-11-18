using System;
using System.Collections.Generic;
using MountainPlanner.Domain.Design;
using MountainPlanner.Domain.Edits;
using MountainPlanner.Domain.Water;
using MountainPlanner.Presentation.Design;
using UnityEngine;

namespace MountainPlanner.UI.Tools
{
    /// <summary>
    /// Task 4.14's review preview for the Pond and Dam tools (F3): the graded ground on the mountain (the terrain brush's
    /// <see cref="ILiveGround.Show"/>), the water standing at full pool, its shore in ice, the spillway notch and a red mark
    /// where a refusal happens. <see cref="Clear"/> puts the committed ground back. Slots from <see cref="FirstSlot"/> up.
    /// </summary>
    public sealed class WaterPreview
    {
        public const int FirstSlot = 300;
        const int SpillSlot = FirstSlot, ProblemSlot = FirstSlot + 1, ShoreSlots = FirstSlot + 2, MaxShore = 24;
        public const int StreamSlots = 400;
        const int MaxStreams = 200;

        TerrainEdits _shownGround;
        bool _water;
        int _streams;

        /// <summary>
        /// Shows <paramref name="build"/>'s review: <paramref name="ground"/> (the rebuild's edits) on the mountain, the water
        /// when it holds any and no problem stops it (in place of the built one <paramref name="replaces"/> names), the shore,
        /// and the marks (design metres from the site's centre).
        /// </summary>
        public void Show(ToolContext ctx, TerrainEdits ground, WaterBuild build, EarthworkSite site, Vector2? spill, Vector2? problem, ulong replaces = 0)
        {
            if (ctx == null) return;
            if (ground != null && !ReferenceEquals(ground, _shownGround) && ctx.LiveGround != null)
            {
                ctx.LiveGround.Show(ground);
                _shownGround = ground;
            }
            var p = ctx.Preview;
            bool wet = build?.Wet != null && build.Wet.Count > 0;
            if (wet && build.Problem == null)
            {
                ctx.PreviewWater?.Invoke(build.Wet, build.FullPoolMetres, replaces);
                _water = true;
            }
            else HideWater(ctx);
            for (int k = 0; k < MaxShore; k++) p.Hide(ShoreSlots + k);
            if (wet)
            {
                var loops = Outline(build.Wet, site);
                for (int k = 0; k < loops.Count && k < MaxShore; k++)
                    p.Line(ShoreSlots + k, loops[k], loops[k].Length, build.Problem != null ? PreviewInk.Bad : PreviewInk.Snap, true);
            }
            if (spill.HasValue && build?.Problem == null) p.Dot(SpillSlot, spill.Value, PreviewInk.Snap); else p.Hide(SpillSlot);
            if (problem.HasValue) p.Dot(ProblemSlot, problem.Value, PreviewInk.Bad); else p.Hide(ProblemSlot);
        }

        /// <summary>Takes the review away: the water, the marks, and the committed ground back if this preview moved it.</summary>
        public void Clear(ToolContext ctx)
        {
            if (ctx == null) return;
            HideWater(ctx);
            if (_shownGround != null)
            {
                if (!ReferenceEquals(_shownGround, ctx.History.Terrain)) ctx.LiveGround?.Show(ctx.History.Terrain);
                _shownGround = null;
            }
            ctx.Preview.Hide(SpillSlot);
            ctx.Preview.Hide(ProblemSlot);
            for (int k = 0; k < MaxShore; k++) ctx.Preview.Hide(ShoreSlots + k);
        }

        /// <summary>
        /// The area's stream lines in ice while the Dam tool is armed (F3, the mockup's tab outlines): where a dam can start.
        /// </summary>
        public void ShowStreams(ToolContext ctx, bool on)
        {
            if (ctx == null) return;
            var lines = on ? ctx.StreamLines?.Invoke() : null;
            int n = 0;
            if (lines != null)
                foreach (var xz in lines)
                {
                    if (n >= MaxStreams) break;
                    if (xz == null || xz.Length < 2) continue;
                    ctx.Preview.Line(StreamSlots + n++, xz, xz.Length, PreviewInk.Snap);
                }
            for (int k = n; k < _streams; k++) ctx.Preview.Hide(StreamSlots + k);
            _streams = n;
        }

        void HideWater(ToolContext ctx)
        {
            if (!_water) return;
            ctx.PreviewWater?.Invoke(null, 0, 0);
            _water = false;
        }

        /// <summary>How far the outline stands out from the wet points' edges: past the water mesh (a metre and a half
        /// beyond the points east and west), so a line draped on the ground isn't drowned by the water it rings.</summary>
        public const double OutlinePadMetres = 1.5;

        /// <summary>
        /// The loops round <paramref name="wet"/>'s points (LK-10, F3), design metres from the site's centre, longest
        /// first: the shore, then its islands, each <see cref="OutlinePadMetres"/> out from the water.
        /// </summary>
        public static List<Vector2[]> Outline(WaterMask wet, EarthworkSite site)
        {
            var loops = new List<Vector2[]>();
            if (wet == null || wet.Count == 0) return loops;
            var box = wet.Tight();
            var lat = site.Lattice;
            foreach (var loop in WaterOutline.Trace(box.MinColumn, box.MinRow, box.MaxColumn, box.MaxRow, wet.Has))
            {
                int n = loop.Length / 2;
                if (n < 3) continue;
                // Every corner turns a right angle: each moves out along both its edges' outward normals. Which side is
                // out: the side of the first edge whose cell holds no water.
                int ex = Math.Sign(loop[2] - loop[0]), ey = Math.Sign(loop[3] - loop[1]);
                double mx = (loop[0] + loop[2]) / 2.0 + ey * 0.5, my = (loop[1] + loop[3]) / 2.0 - ex * 0.5;   // right of travel
                int sign = wet.Has((int)Math.Floor(mx), (int)Math.Floor(my)) ? -1 : 1;
                var xz = new Vector2[n];
                for (int k = 0; k < n; k++)
                {
                    int p = (k + n - 1) % n, q = (k + 1) % n;
                    int ax = Math.Sign(loop[2 * k] - loop[2 * p]), ay = Math.Sign(loop[2 * k + 1] - loop[2 * p + 1]);
                    int bx = Math.Sign(loop[2 * q] - loop[2 * k]), by = Math.Sign(loop[2 * q + 1] - loop[2 * k + 1]);
                    double ox = sign * (ay + by) * OutlinePadMetres, oy = -sign * (ax + bx) * OutlinePadMetres;
                    xz[k] = new Vector2((float)(lat.X(loop[2 * k]) - 0.5 - site.OriginX + ox), (float)(lat.Y(loop[2 * k + 1]) + 0.5 - site.OriginY - oy));
                }
                loops.Add(xz);
            }
            loops.Sort((a, c) => c.Length.CompareTo(a.Length));
            return loops;
        }
    }
}
