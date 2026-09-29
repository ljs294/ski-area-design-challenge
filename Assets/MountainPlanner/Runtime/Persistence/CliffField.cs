#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using MountainPlanner.Domain.Cover;
using MountainPlanner.Domain.Geo;

namespace MountainPlanner.Persistence
{
    /// <summary>A tile's cliff shell: positions relative to the tile's south-west corner (x east, y up in metres, z north).</summary>
    public sealed class CliffMeshData
    {
        public float[] Positions = Array.Empty<float>();
        public float[] Normals = Array.Empty<float>();
        /// <summary>Shell strength per vertex (0–1): the shader blends faded edges to snow.</summary>
        public float[] Weights = Array.Empty<float>();
        public int[] Indices = Array.Empty<int>();
        public int VertexCount => Positions.Length / 3;

        public byte[] ToBytes()
        {
            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms))
            {
                w.Write(VertexCount);
                w.Write(Indices.Length);
                foreach (float f in Positions) w.Write(f);
                foreach (float f in Normals) w.Write(f);
                foreach (float f in Weights) w.Write(f);
                foreach (int i in Indices) w.Write(i);
            }
            return ms.ToArray();
        }

        public static CliffMeshData FromBytes(byte[] bytes)
        {
            using var r = new BinaryReader(new MemoryStream(bytes));
            int nv = r.ReadInt32(), ni = r.ReadInt32();
            var m = new CliffMeshData { Positions = new float[nv * 3], Normals = new float[nv * 3], Weights = new float[nv], Indices = new int[ni] };
            for (int i = 0; i < m.Positions.Length; i++) m.Positions[i] = r.ReadSingle();
            for (int i = 0; i < m.Normals.Length; i++) m.Normals[i] = r.ReadSingle();
            for (int i = 0; i < nv; i++) m.Weights[i] = r.ReadSingle();
            for (int i = 0; i < ni; i++) m.Indices[i] = r.ReadInt32();
            return m;
        }
    }

    /// <summary>
    /// Builds cliff shells (<see cref="CliffShape"/>): on a 2 m grid, wherever the lidar is steep, the terrain
    /// surface is pushed outward along its normal into ledges, buttresses and joints, fading back under the
    /// terrain at the cliff's edge. Everything is a function of world position (slope, weight blur,
    /// displacement, normals), so neighbouring tiles share identical edge vertices.
    /// </summary>
    public sealed class CliffField
    {
        public const double Spacing = 2;
        const int Pad = 3;
        const double MinWeight = 0.01;
        /// <summary>Shells stop this far beyond the core (fading over the last 200 m): further out their metre-scale relief can't be seen.</summary>
        public const double ReachMetres = 1000, FadeMetres = 200;

        readonly TerrainCache.HeightField _heights;
        readonly AlbersBox _ring, _core;
        readonly ulong _seed;

        public CliffField(PackageManifest package, TerrainCache.HeightField heights)
        {
            _heights = heights;
            _seed = CoverNoise.SeedFor(package.Site.CentreX, package.Site.CentreY) ^ 0xC11FUL;
            var site = SiteSquare.Create(new AlbersPoint(package.Site.CentreX, package.Site.CentreY), package.Site.SizeMetres / 1000.0);
            _ring = site.Ring;
            _core = site.Core;
        }

        public CliffMeshData BuildTile(AlbersBox tile)
        {
            // Tiles entirely beyond the shells' reach have none.
            double gapX = Math.Max(0, Math.Max(_core.West - tile.East, tile.West - _core.East)), gapY = Math.Max(0, Math.Max(_core.South - tile.North, tile.South - _core.North));
            if (Math.Sqrt(gapX * gapX + gapY * gapY) > ReachMetres + 2 * Pad * Spacing) return new CliffMeshData();
            int n = (int)Math.Round((tile.East - tile.West) / Spacing) + 1, m = n + 2 * Pad;
            double X(int i) => tile.West + (i - Pad) * Spacing;
            double Y(int j) => tile.North - (j - Pad) * Spacing;

            // 1. Shell weight from slope, softened by two 3×3 blurs (so it fades over ~6 m).
            var w0 = new double[m * m];
            bool any = false;
            for (int j = 0; j < m; j++)
                for (int i = 0; i < m; i++)
                {
                    double x = X(i), y = Y(j);
                    double hx = _heights.At(x + Spacing, y) - _heights.At(x - Spacing, y), hy = _heights.At(x, y + Spacing) - _heights.At(x, y - Spacing);
                    double slope = Math.Atan(Math.Sqrt(hx * hx + hy * hy) / (2 * Spacing)) * (180 / Math.PI);
                    double dxo = Math.Max(0, Math.Max(_core.West - x, x - _core.East)), dyo = Math.Max(0, Math.Max(_core.South - y, y - _core.North));
                    double reach = 1 - GroundCover.SmoothStep(ReachMetres - FadeMetres, ReachMetres, Math.Sqrt(dxo * dxo + dyo * dyo));
                    w0[j * m + i] = CliffShape.Weight(slope) * reach;
                    if (w0[j * m + i] > 0) any = true;
                }
            if (!any) return new CliffMeshData();
            var w = Blur(Blur(w0, m), m);

            // 2. Displaced positions (computed where needed, including one ring of neighbours for normals).
            var pos = new double[m * m * 3];
            var done = new bool[m * m];
            void Position(int i, int j)
            {
                int k = j * m + i;
                if (done[k]) return;
                double x = X(i), y = Y(j), h = _heights.At(x, y);
                double dx = (_heights.At(x + Spacing, y) - _heights.At(x - Spacing, y)) / (2 * Spacing);
                double dy = (_heights.At(x, y + Spacing) - _heights.At(x, y - Spacing)) / (2 * Spacing);
                double len = Math.Sqrt(dx * dx + 1 + dy * dy);
                double nx = -dx / len, ny = 1 / len, nz = -dy / len;   // east, up, north
                double d = CliffShape.Displacement(_seed, x, y, h, w[k]);
                pos[k * 3] = x - tile.West + nx * d;
                pos[k * 3 + 1] = h + ny * d;
                pos[k * 3 + 2] = y - tile.South + nz * d;
                done[k] = true;
            }

            // 3. Vertices inside the tile and the downloaded data, where the shell exists.
            var index = new int[m * m];
            var positions = new List<float>();
            var normals = new List<float>();
            var weights = new List<float>();
            for (int j = Pad; j < Pad + n; j++)
                for (int i = Pad; i < Pad + n; i++)
                {
                    int k = j * m + i;
                    index[k] = -1;
                    double x = X(i), y = Y(j);
                    if (w[k] <= MinWeight || x < _ring.West || x > _ring.East || y < _ring.South || y > _ring.North) continue;
                    Position(i, j); Position(i + 1, j); Position(i - 1, j); Position(i, j + 1); Position(i, j - 1);
                    int e = (j * m + i + 1) * 3, wv = (j * m + i - 1) * 3, s = ((j + 1) * m + i) * 3, nn = ((j - 1) * m + i) * 3;
                    double ex = pos[e] - pos[wv], ey = pos[e + 1] - pos[wv + 1], ez = pos[e + 2] - pos[wv + 2];      // eastward
                    double qx = pos[nn] - pos[s], qy = pos[nn + 1] - pos[s + 1], qz = pos[nn + 2] - pos[s + 2];      // northward
                    double cx = qy * ez - qz * ey, cy = qz * ex - qx * ez, cz = qx * ey - qy * ex;                 // north × east = up
                    double cl = Math.Sqrt(cx * cx + cy * cy + cz * cz);
                    if (cl < 1e-9) { cx = 0; cy = 1; cz = 0; cl = 1; }
                    index[k] = positions.Count / 3;
                    positions.Add((float)pos[k * 3]); positions.Add((float)pos[k * 3 + 1]); positions.Add((float)pos[k * 3 + 2]);
                    normals.Add((float)(cx / cl)); normals.Add((float)(cy / cl)); normals.Add((float)(cz / cl));
                    weights.Add((float)w[k]);
                }

            // 4. Two triangles per grid cell whose four corners all exist (clockwise seen from outside).
            var indices = new List<int>();
            for (int j = Pad; j < Pad + n - 1; j++)
                for (int i = Pad; i < Pad + n - 1; i++)
                {
                    int a = index[j * m + i], b = index[j * m + i + 1], c = index[(j + 1) * m + i], d = index[(j + 1) * m + i + 1];
                    if (a < 0 || b < 0 || c < 0 || d < 0) continue;
                    indices.Add(a); indices.Add(b); indices.Add(d);
                    indices.Add(a); indices.Add(d); indices.Add(c);
                }
            if (indices.Count == 0) return new CliffMeshData();
            return new CliffMeshData { Positions = positions.ToArray(), Normals = normals.ToArray(), Weights = weights.ToArray(), Indices = indices.ToArray() };
        }

        static double[] Blur(double[] v, int m)
        {
            var r = new double[v.Length];
            for (int j = 0; j < m; j++)
                for (int i = 0; i < m; i++)
                {
                    double sum = 0;
                    int count = 0;
                    for (int dj = -1; dj <= 1; dj++)
                        for (int di = -1; di <= 1; di++)
                        {
                            int x = i + di, y = j + dj;
                            if (x < 0 || y < 0 || x >= m || y >= m) continue;
                            sum += v[y * m + x];
                            count++;
                        }
                    r[j * m + i] = sum / count;
                }
            return r;
        }
    }
}
