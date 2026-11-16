using System;
using System.Collections.Generic;
using MountainPlanner.Domain.Weather;
using UnityEngine;

namespace MountainPlanner.Presentation.Weather
{
    /// <summary>
    /// Task 4.06 (WX-53, B9): where spindrift streams from in a gale: the most exposed high ground (the area's most exposed
    /// class, in the top part of its range), one point per <see cref="PatchCells"/> patch (the highest cell in it), the
    /// <see cref="Most"/> highest patches kept. It reads every 8 m cell once, so it runs on a worker thread when the area's
    /// static inputs arrive; the same area always gives the same points. <see cref="Emitters"/> turns them into
    /// <see cref="WindRibbons"/> emitters: snow lifted off the crest and streamed down the lee.
    /// </summary>
    public static class Spindrift
    {
        /// <summary>A patch's side (cells; 200 m), how many points are kept, and the share of the area's relief, from the top, they come from.</summary>
        public const int PatchCells = 25, Most = 48;
        public const float TopShare = 0.3f;
        /// <summary>
        /// A plume: its life (s), a puff's size at birth and at death (m), the lift off the crest and the sink down the lee (m).
        /// Big enough to read from the overview: in a 40 mph gale a banner runs about 350 m down the lee.
        /// </summary>
        public const float Life = 20f, SizeFrom = 8f, SizeTo = 60f, Lift = 20f, Sink = 45f;

        /// <summary>The points (world metres: x and z in the design's frame, y the cell's height above sea level).</summary>
        public static Vector3[] Points(WeatherTerrain t)
        {
            if (t == null || t.Cells == 0) return Array.Empty<Vector3>();
            int most = -1, lo = int.MaxValue, hi = -1;
            for (int c = 0; c < t.Cells; c++)
            {
                int bin = t.Bin[c];
                if (bin == WeatherTerrain.NoData || t.Exposure[c] == WeatherTerrain.NoData) continue;
                most = Math.Max(most, t.Exposure[c]);
                lo = Math.Min(lo, bin);
                hi = Math.Max(hi, bin);
            }
            if (most < 5 || hi < 0) return Array.Empty<Vector3>();   // nowhere exposed enough to drift
            int floor = hi - (int)Math.Ceiling((hi - lo) * TopShare);

            int pw = (t.Width + PatchCells - 1) / PatchCells, pd = (t.Depth + PatchCells - 1) / PatchCells;
            var best = new int[pw * pd];
            for (int p = 0; p < best.Length; p++) best[p] = -1;
            for (int j = 0; j < t.Depth; j++)
                for (int i = 0; i < t.Width; i++)
                {
                    int c = j * t.Width + i;
                    int bin = t.Bin[c];
                    if (bin == WeatherTerrain.NoData || bin < floor || t.Exposure[c] != most) continue;
                    int p = (j / PatchCells) * pw + i / PatchCells;
                    if (best[p] < 0 || bin > t.Bin[best[p]]) best[p] = c;   // ties keep the first: the same area, the same points
                }

            var kept = new List<int>();
            for (int p = 0; p < best.Length; p++) if (best[p] >= 0) kept.Add(best[p]);
            kept.Sort((a, b) => t.Bin[b] != t.Bin[a] ? t.Bin[b].CompareTo(t.Bin[a]) : a.CompareTo(b));
            int n = Math.Min(Most, kept.Count);
            var points = new Vector3[n];
            float cell = WeatherTerrain.CellMetres;
            for (int k = 0; k < n; k++)
            {
                int c = kept[k], i = c % t.Width, j = c / t.Width;
                points[k] = new Vector3(t.OriginXMm / 1000f + (i + 0.5f) * cell, t.BinCentreFeet(t.Bin[c]) * 0.3048f,
                                        t.OriginYMm / 1000f + (j + 0.5f) * cell);
            }
            return points;
        }

        /// <summary>The emitters for the points: the highest strongest; lifted off the crest, carried and sunk down the lee.</summary>
        public static WindRibbons.Emitter[] Emitters(Vector3[] points)
        {
            var e = new WindRibbons.Emitter[points.Length];
            for (int k = 0; k < points.Length; k++)
                e[k] = new WindRibbons.Emitter
                {
                    Position = points[k] + new Vector3(0, 3, 0), Jet = new Vector3(0, Lift, 0),
                    Strength = Mathf.Lerp(1f, 0.6f, points.Length > 1 ? k / (points.Length - 1f) : 0), Life = Life,
                    SizeFrom = SizeFrom, SizeTo = SizeTo, Sink = Sink, Carry = 1,
                };
            return e;
        }
    }
}
