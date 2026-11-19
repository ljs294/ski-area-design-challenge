using System;
using System.Collections.Generic;
using MountainPlanner.Domain.Design;
using MountainPlanner.Domain.Snow;

namespace MountainPlanner.Simulation
{
    /// <summary>
    /// Task 4.07 B1 part 2a: the fine layout for the worker's <see cref="SimCommandKind.SetTrailSnow"/>, from the built trails
    /// and the network the worker skis on (SN-03 v3). Each trail in the network's <see cref="SimNetwork.TrailIds"/> order
    /// gives its painted mask, its centreline sampled every few metres (design mm), the network's width for it, and its
    /// segments (the network's edges along it, by where they start). Off the worker: it allocates.
    /// </summary>
    public static class TrailSnowInputs
    {
        /// <summary>How finely the centreline is sampled for the layout's along and lanes (mm).</summary>
        public const int SampleMm = 4000;

        /// <param name="lengthMm">The trail's centreline length (the network's measure).</param>
        /// <param name="pointAlong">The point that far along the trail's centreline from its head, design mm.</param>
        public static TrailSnowLayout Build(DesignDocument doc, SimNetwork net, Func<Trail, int> lengthMm, Func<Trail, int, PointMm> pointAlong,
                                            int gridWidth, int gridHeight, long originXMm, long originYMm, int fineCellCap)
        {
            if (doc == null || net == null) return null;
            var byId = new Dictionary<ulong, Trail>();
            foreach (var t in doc.Trails) if (t.Stage == WorkStage.Built) byId[t.Id.Value] = t;
            // Each trail's edges, by where they start along it.
            var edges = new List<(int start, int edge)>[net.TrailIds.Length];
            for (int e = 0; e < net.EdgeTrail.Length; e++)
            {
                int ti = net.EdgeTrail[e];
                if (ti < 0 || ti >= edges.Length) continue;
                (edges[ti] ??= new List<(int, int)>()).Add((e < net.EdgeStartMm.Length ? net.EdgeStartMm[e] : 0, e));
            }
            var inputs = new List<TrailSnowInput>();
            for (int ti = 0; ti < net.TrailIds.Length; ti++)
            {
                if (!byId.TryGetValue(net.TrailIds[ti], out var trail) || trail.Shape.IsEmpty || edges[ti] == null) continue;
                int length = Math.Max(1, lengthMm(trail));
                int samples = length / SampleMm + 2;
                var xs = new int[samples]; var ys = new int[samples];
                for (int k = 0; k < samples; k++)
                {
                    var p = pointAlong(trail, Math.Min(length, k * SampleMm));
                    xs[k] = p.X; ys[k] = p.Y;
                }
                var list = edges[ti];
                list.Sort((a, b) => a.start != b.start ? a.start.CompareTo(b.start) : a.edge.CompareTo(b.edge));
                var starts = new List<int>(); var segs = new List<int>();
                foreach (var (start, edge) in list)
                {
                    if (starts.Count > 0 && start <= starts[starts.Count - 1]) continue;
                    starts.Add(starts.Count == 0 ? 0 : start); segs.Add(edge);
                }
                int widthCm = net.EdgeWidthCm.Length > list[0].edge ? net.EdgeWidthCm[list[0].edge] : 0;
                inputs.Add(new TrailSnowInput
                {
                    Mask = trail.Shape.Mask, LineXMm = xs, LineYMm = ys, WidthMm = widthCm > 0 ? widthCm * 10 : 30_480,
                    SegmentStartMm = starts.ToArray(), SegmentEdge = segs.ToArray(),
                    NeverGroom = trail.Settings.Kind != TrailKind.Run, Id = trail.Id.Value,
                });
            }
            return TrailSnowLayout.Build(gridWidth, gridHeight, originXMm, originYMm, inputs, fineCellCap, net.ShapeHash);
        }
    }
}
