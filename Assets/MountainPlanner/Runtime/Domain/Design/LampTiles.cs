using System;
using System.Collections.Generic;

namespace MountainPlanner.Domain.Design
{
    /// <summary>
    /// Task 4.50 slice 3 part 2: which building lamps can light each patch of ground, so a pixel's lamp light reads only
    /// the few lamps near it. The world is cut into square tiles <see cref="TileMetres"/> on a side (in the scene's own
    /// metres, x east and z north); a lamp is listed in every tile its reach (a disc of its range) touches. A tile holds at
    /// most <see cref="MaxPerTile"/> lamps: past that it keeps the ones nearest its centre (ties by lamp order), so the
    /// answer is the same every time. The tiles cover only the lamps' box; everywhere else has no lamps.
    /// </summary>
    public sealed class LampTiles
    {
        public const float TileMetres = 32f;
        public const int MaxPerTile = 16;

        /// <summary>The box's first tile column (x) and row (z), and its size in tiles.</summary>
        public int Column0 { get; private set; }
        public int Row0 { get; private set; }
        public int Width { get; private set; }
        public int Height { get; private set; }

        /// <summary>Per tile (row-major over the box, row = z): where its lamps start in <see cref="Index"/>, then how many.</summary>
        public int[] Cells { get; private set; } = Array.Empty<int>();
        /// <summary>Lamp numbers, tile after tile.</summary>
        public List<int> Index { get; } = new List<int>();

        public int TileCount => Width * Height;

        readonly List<(float dist, int lamp)> _near = new List<(float, int)>();

        /// <summary>The tiles for <paramref name="count"/> lamps at x, z reaching <paramref name="range"/> metres.</summary>
        public void Build(IReadOnlyList<float> x, IReadOnlyList<float> z, IReadOnlyList<float> range, int count)
        {
            Index.Clear();
            if (count == 0) { Column0 = Row0 = Width = Height = 0; Cells = Array.Empty<int>(); return; }
            int c0 = int.MaxValue, r0 = int.MaxValue, c1 = int.MinValue, r1 = int.MinValue;
            for (int i = 0; i < count; i++)
            {
                c0 = Math.Min(c0, Tile(x[i] - range[i])); c1 = Math.Max(c1, Tile(x[i] + range[i]));
                r0 = Math.Min(r0, Tile(z[i] - range[i])); r1 = Math.Max(r1, Tile(z[i] + range[i]));
            }
            Column0 = c0; Row0 = r0; Width = c1 - c0 + 1; Height = r1 - r0 + 1;
            int n = Width * Height;
            var lists = new List<int>[n];
            for (int i = 0; i < count; i++)
            {
                float r = range[i];
                for (int row = Tile(z[i] - r); row <= Tile(z[i] + r); row++)
                    for (int col = Tile(x[i] - r); col <= Tile(x[i] + r); col++)
                    {
                        // the disc meets the tile: the tile's nearest point to the lamp is within reach
                        float nx = Clamp(x[i], col * TileMetres, (col + 1) * TileMetres) - x[i];
                        float nz = Clamp(z[i], row * TileMetres, (row + 1) * TileMetres) - z[i];
                        if (nx * nx + nz * nz > r * r) continue;
                        int at = (row - r0) * Width + col - c0;
                        (lists[at] ??= new List<int>()).Add(i);
                    }
            }
            Cells = new int[n * 2];
            for (int t = 0; t < n; t++)
            {
                Cells[t * 2] = Index.Count;
                var list = lists[t];
                if (list == null) continue;
                if (list.Count > MaxPerTile)
                {
                    float cx = (Column0 + t % Width + 0.5f) * TileMetres, cz = (Row0 + t / Width + 0.5f) * TileMetres;
                    _near.Clear();
                    foreach (int i in list)
                    {
                        float dx = x[i] - cx, dz = z[i] - cz;
                        _near.Add((dx * dx + dz * dz, i));
                    }
                    _near.Sort((a, b) => a.dist != b.dist ? a.dist.CompareTo(b.dist) : a.lamp.CompareTo(b.lamp));
                    list.Clear();
                    for (int k = 0; k < MaxPerTile; k++) list.Add(_near[k].lamp);
                    list.Sort();
                }
                Index.AddRange(list);
                Cells[t * 2 + 1] = list.Count;
            }
        }

        /// <summary>The lamps tile (column, row) lists, for tests: empty outside the box.</summary>
        public IEnumerable<int> LampsIn(int column, int row)
        {
            int c = column - Column0, r = row - Row0;
            if (c < 0 || r < 0 || c >= Width || r >= Height) yield break;
            int t = r * Width + c;
            for (int k = 0; k < Cells[t * 2 + 1]; k++) yield return Index[Cells[t * 2] + k];
        }

        public static int Tile(float metres) => (int)Math.Floor(metres / TileMetres);

        static float Clamp(float v, float lo, float hi) => v < lo ? lo : v > hi ? hi : v;
    }
}
