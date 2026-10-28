using System;
using System.Collections.Generic;
using MountainPlanner.Domain.Design;
using MountainPlanner.Domain.Edits;
using MountainPlanner.Domain.Geo;
using Unity.Collections;
using UnityEngine;

namespace MountainPlanner.Presentation.Design
{
    /// <summary>
    /// The look of built trails (task 07, decision SK3): a 1 m world-space map the terrain shader reads, over the
    /// built trails' bounds. Per lattice point:
    /// R the footprint (1 inside, fading over the last metre);
    /// G the height change, edited minus natural, in metres (cut negative, fill positive);
    /// B 1 on a graded trail, 0 on a natural one (the terrain shader smooths a graded surface's shading; the offset
    /// across the trail, which corduroy and groomer passes run along, is the full-precision _TrailOffset map);
    /// A the edited ground's slope across the trail (0 on a level bench, about 1 on a 1:1 face).
    /// It is display data derived from the design and the edited heights, never stored and never read by a rule.
    /// Each trail's painted footprint and automatic centreline come from task 08's TrailFootprint and TrailCentreline.
    /// </summary>
    public sealed class TrailSurfaceMap : IDisposable
    {
        public const int MaxTexels = 2048;

        static readonly int MapId = Shader.PropertyToID("_TrailMap"), RectId = Shader.PropertyToID("_TrailRect"),
                            ParamsId = Shader.PropertyToID("_TrailParams"), OffsetId = Shader.PropertyToID("_TrailOffset");

        Texture2D _map;
        readonly Dictionary<TrailId, (Trail trail, TrailFootprint fp, double[] line)> _shapes = new Dictionary<TrailId, (Trail, TrailFootprint, double[])>();
        /// <summary>The offset across the trail at full float precision (finding #11: 1 in corduroy needs it).</summary>
        Texture2D _offset;

        public TrailSurfaceMap() => Shader.SetGlobalVector(ParamsId, Vector4.zero);

        /// <summary>The trails the map shows: built, with a stroke. Returns how many it drew.</summary>
        public int Rebuild(DesignDocument doc, EditedHeights edited, LocalFrame frame, Func<Trail, TrailBuild> built = null)
        {
            var trails = new List<(double[] line, double half, TrailFootprint fp, bool graded)>();
            var seen = new HashSet<TrailId>();
            foreach (var t in doc.Trails)
            {
                if (t.Stage != WorkStage.Built || !t.HasEnds) continue;
                seen.Add(t.Id);
                // Task 08: each trail's painted footprint and automatic centreline (TR-02, TR-03), kept while the trail is unchanged.
                if ((!_shapes.TryGetValue(t.Id, out var shape) || !ReferenceEquals(shape.trail, t))
                    && built?.Invoke(t) is TrailBuild b && ReferenceEquals(b.Trail, t) && b.Centreline != null && b.Centreline.Found)
                {
                    // The earthwork's own footprint and centreline for this very trail (task 08): no second computation.
                    shape = (t, b.Footprint, b.Centreline.Points);
                    _shapes[t.Id] = shape;
                }
                if (!_shapes.TryGetValue(t.Id, out shape) || !ReferenceEquals(shape.trail, t))
                {
                    var fp = TrailFootprint.Build(t.Shape, edited.Lattice, frame.Origin.X, frame.Origin.Y);
                    var head = frame.ToAlbers(t.Head.Value.XMetres, t.Head.Value.YMetres);
                    var tail = frame.ToAlbers(t.Tail.Value.XMetres, t.Tail.Value.YMetres);
                    var c = TrailCentreline.Find(fp, head.X, head.Y, tail.X, tail.Y);
                    shape = (t, fp, c.Found ? c.Points : null);
                    _shapes[t.Id] = shape;
                }
                if (shape.line == null) continue;
                trails.Add((shape.line, shape.fp.MaxClearance * 1.5 + 3, shape.fp, t.Settings.Grading == TrailGrading.Graded));
            }
            var stale = new List<TrailId>();
            foreach (var id in _shapes.Keys) if (!seen.Contains(id)) stale.Add(id);
            foreach (var id in stale) _shapes.Remove(id);
            if (trails.Count == 0)
            {
                Shader.SetGlobalVector(ParamsId, Vector4.zero);
                return 0;
            }

            var lattice = edited.Lattice;
            double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
            foreach (var (line, half, _, _) in trails)
                for (int i = 0; i < line.Length; i += 2)
                {
                    minX = Math.Min(minX, line[i] - half); maxX = Math.Max(maxX, line[i] + half);
                    minY = Math.Min(minY, line[i + 1] - half); maxY = Math.Max(maxY, line[i + 1] + half);
                }
            int c0 = (int)Math.Floor(minX - 2 - lattice.West), r0 = (int)Math.Floor(lattice.North - (maxY + 2));
            int w = Math.Min(MaxTexels, (int)Math.Ceiling(maxX + 2 - lattice.West) - c0 + 1);
            int h = Math.Min(MaxTexels, (int)Math.Ceiling(lattice.North - (minY - 2)) - r0 + 1);

            var foot = new float[w * h];
            var graded = new float[w * h];
            var change = new float[w * h];
            var offset = new float[w * h];
            var across = new float[w * h];
            var nearest = new double[w * h];
            for (int i = 0; i < nearest.Length; i++) nearest[i] = double.MaxValue;
            // Per trail: locate every point on its centreline, keep the nearest trail's offset and the footprint.
            var normalX = new float[w * h];
            var normalY = new float[w * h];
            foreach (var (line, half, fp, isGraded) in trails)
            {
                var along = new double[line.Length / 2];
                for (int k = 1; k < along.Length; k++)
                    along[k] = along[k - 1] + Math.Sqrt(Sq(line[2 * k] - line[2 * k - 2]) + Sq(line[2 * k + 1] - line[2 * k - 1]));
                var located = TrailGrader.Locate(line, along, lattice, c0, r0, w, h, 0, 0, half + 2);
                for (int i = 0; i < w * h; i++)
                {
                    double d = located.Dist[i];
                    if (d > half + 2) continue;
                    int pc = c0 + i % w, pr = r0 + i / w;
                    if (!fp.Contains(pc, pr) && !fp.Contains(pc - 1, pr - 1)) continue;
                    // The painted footprint (islands included), fading over its last metre: the most any trail covers the
                    // point, so a later trail's paint edge never cuts a line through an earlier one (the review, 2026-10-20).
                    float cover = Mathf.Clamp01((float)fp.PointClearance(pc, pr));
                    if (cover > foot[i]) foot[i] = cover;
                    if (isGraded && cover > graded[i]) graded[i] = cover;
                    if (d >= nearest[i]) continue;
                    nearest[i] = d;
                    offset[i] = (float)located.Offset[i];
                }
            }
            // The edited ground's change and its slope across the trail (along the gradient of the offset).
            for (int j = 0; j < h; j++)
                for (int i = 0; i < w; i++)
                {
                    int at = j * w + i;
                    if (foot[at] <= 0 && nearest[at] > 1e9) continue;
                    int c = c0 + i, r = r0 + j;
                    double x = lattice.X(c), y = lattice.Y(r);
                    change[at] = (float)(edited.AtPoint(c, r) - edited.Source.At(x, y));
                    if (i == 0 || j == 0 || i == w - 1 || j == h - 1) continue;
                    double ux = (offset[at + 1] - offset[at - 1]) / 2, uy = (offset[at - w] - offset[at + w]) / 2;   // rows run south
                    double len = Math.Sqrt(ux * ux + uy * uy);
                    if (len < 1e-6) continue;
                    ux /= len;
                    uy /= len;
                    // Over ±2 m, so the snow/earth boundary that reads it follows the face, not the 1 m lattice's steps.
                    double slope = (edited.At(x + 2 * ux, y + 2 * uy) - edited.At(x - 2 * ux, y - 2 * uy)) / 4;
                    across[at] = (float)Math.Abs(slope);
                }

            if (_map == null || _map.width != w || _map.height != h)
            {
                Release();
                _map = new Texture2D(w, h, TextureFormat.RGBAHalf, false, true)
                {
                    name = "Trail surface", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp,
                };
                _offset = new Texture2D(w, h, TextureFormat.RFloat, false, true)
                {
                    name = "Trail offset", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp,
                };
            }
            // Texture rows run north from the south edge; lattice rows run south.
            NativeArray<ushort> pixels = _map.GetPixelData<ushort>(0);
            for (int j = 0; j < h; j++)
            {
                int row = h - 1 - j;
                for (int i = 0; i < w; i++)
                {
                    int at = j * w + i, p = (row * w + i) * 4;
                    pixels[p] = Mathf.FloatToHalf(foot[at]);
                    pixels[p + 1] = Mathf.FloatToHalf(change[at]);
                    pixels[p + 2] = Mathf.FloatToHalf(graded[at]);
                    pixels[p + 3] = Mathf.FloatToHalf(across[at]);
                }
            }
            _map.Apply(false);
            NativeArray<float> offsets = _offset.GetPixelData<float>(0);
            for (int j = 0; j < h; j++)
            {
                int row = h - 1 - j;
                for (int i = 0; i < w; i++) offsets[row * w + i] = offset[j * w + i];
            }
            _offset.Apply(false);
            Shader.SetGlobalTexture(OffsetId, _offset);
            var (west, north) = frame.ToLocal(new AlbersPoint(lattice.X(c0), lattice.Y(r0)));
            double south = north - (h - 1);
            // Texel centres sit on lattice points: uv = (p − corner + 0.5) / size.
            Shader.SetGlobalTexture(MapId, _map);
            Shader.SetGlobalVector(RectId, new Vector4((float)(west - 0.5), (float)(south - 0.5), 1f / w, 1f / h));
            Shader.SetGlobalVector(ParamsId, new Vector4(1, 0, 0, 0));
            return trails.Count;
        }

        static double Sq(double v) => v * v;

        void Release()
        {
            foreach (var t in new[] { _map, _offset })
            {
                if (t == null) continue;
                if (Application.isPlaying) UnityEngine.Object.Destroy(t);
                else UnityEngine.Object.DestroyImmediate(t);
            }
            _map = null;
            _offset = null;
        }

        public void Dispose()
        {
            Shader.SetGlobalVector(ParamsId, Vector4.zero);
            Release();
        }
    }
}
