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
    /// B the offset across the trail from its centreline, in metres (corduroy and groomer passes run along it);
    /// A the edited ground's slope across the trail (0 on a level bench, about 1 on a 1:1 face).
    /// It is display data derived from the design and the edited heights, never stored and never read by a rule.
    /// Strokes give each trail's footprint for now: the first stroke's points are its centreline (task 07's trails
    /// are one stroke); task 08 swaps in the automatic centreline.
    /// </summary>
    public sealed class TrailSurfaceMap : IDisposable
    {
        public const int MaxTexels = 2048;

        static readonly int MapId = Shader.PropertyToID("_TrailMap"), RectId = Shader.PropertyToID("_TrailRect"),
                            ParamsId = Shader.PropertyToID("_TrailParams");

        Texture2D _map;

        public TrailSurfaceMap() => Shader.SetGlobalVector(ParamsId, Vector4.zero);

        /// <summary>The trails the map shows: built, with a stroke. Returns how many it drew.</summary>
        public int Rebuild(DesignDocument doc, EditedHeights edited, LocalFrame frame)
        {
            var trails = new List<(double[] line, double half)>();
            foreach (var t in doc.Trails)
            {
                if (t.Stage != WorkStage.Built || t.Parts.Count == 0 || t.Parts[0].Strokes.Count == 0) continue;
                var stroke = t.Parts[0].Strokes[0];
                if (stroke.Erase || stroke.Points.Count < 2) continue;
                var line = new double[stroke.Points.Count * 2];
                for (int i = 0; i < stroke.Points.Count; i++)
                {
                    var a = frame.ToAlbers(stroke.Points[i].XMetres, stroke.Points[i].YMetres);
                    line[2 * i] = a.X;
                    line[2 * i + 1] = a.Y;
                }
                trails.Add((line, stroke.WidthMm / 2000.0));
            }
            if (trails.Count == 0)
            {
                Shader.SetGlobalVector(ParamsId, Vector4.zero);
                return 0;
            }

            var lattice = edited.Lattice;
            double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
            foreach (var (line, half) in trails)
                for (int i = 0; i < line.Length; i += 2)
                {
                    minX = Math.Min(minX, line[i] - half); maxX = Math.Max(maxX, line[i] + half);
                    minY = Math.Min(minY, line[i + 1] - half); maxY = Math.Max(maxY, line[i + 1] + half);
                }
            int c0 = (int)Math.Floor(minX - 2 - lattice.West), r0 = (int)Math.Floor(lattice.North - (maxY + 2));
            int w = Math.Min(MaxTexels, (int)Math.Ceiling(maxX + 2 - lattice.West) - c0 + 1);
            int h = Math.Min(MaxTexels, (int)Math.Ceiling(lattice.North - (minY - 2)) - r0 + 1);

            var foot = new float[w * h];
            var change = new float[w * h];
            var offset = new float[w * h];
            var across = new float[w * h];
            var nearest = new double[w * h];
            for (int i = 0; i < nearest.Length; i++) nearest[i] = double.MaxValue;
            // Per trail: locate every point on its centreline, keep the nearest trail's offset and the footprint.
            var normalX = new float[w * h];
            var normalY = new float[w * h];
            foreach (var (line, half) in trails)
            {
                var along = new double[line.Length / 2];
                for (int k = 1; k < along.Length; k++)
                    along[k] = along[k - 1] + Math.Sqrt(Sq(line[2 * k] - line[2 * k - 2]) + Sq(line[2 * k + 1] - line[2 * k - 1]));
                var located = TrailGrader.Locate(line, along, lattice, c0, r0, w, h, 0, 0, half + 2);
                for (int i = 0; i < w * h; i++)
                {
                    double d = located.Dist[i];
                    if (d >= nearest[i] || d > half + 2) continue;
                    nearest[i] = d;
                    foot[i] = Mathf.Clamp01((float)(half - d) + 0.5f);
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
                    double slope = (edited.At(x + ux, y + uy) - edited.At(x - ux, y - uy)) / 2;
                    across[at] = (float)Math.Abs(slope);
                }

            if (_map == null || _map.width != w || _map.height != h)
            {
                Release();
                _map = new Texture2D(w, h, TextureFormat.RGBAHalf, false, true)
                {
                    name = "Trail surface", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp,
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
                    pixels[p + 2] = Mathf.FloatToHalf(offset[at]);
                    pixels[p + 3] = Mathf.FloatToHalf(across[at]);
                }
            }
            _map.Apply(false);
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
            if (_map == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(_map);
            else UnityEngine.Object.DestroyImmediate(_map);
            _map = null;
        }

        public void Dispose()
        {
            Shader.SetGlobalVector(ParamsId, Vector4.zero);
            Release();
        }
    }
}
