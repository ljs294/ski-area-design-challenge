using System;

namespace MountainPlanner.Domain.Snow
{
    /// <summary>
    /// Task 4.08 PR A: the fine trail snow (<see cref="TrailSnowLayout"/>) cut into 64 m tiles for the screen. A tile holds
    /// 8 × 8 of the 8 m cells; only tiles with at least one block are listed, in ascending tile order (south row first, west
    /// to east). The worker stages a listed tile's fine cells by its list index and the screen's atlas keeps one slot per
    /// listed tile, so both sides number the tiles the same way from the same layout.
    /// </summary>
    public sealed class FineTiles
    {
        /// <summary>8 m cells along a tile's side.</summary>
        public const int CellsPerTile = 8;

        public readonly TrailSnowLayout Layout;
        /// <summary>Fine cells along a tile's side (64 at 1 m, 32 at 2 m, 16 at 4 m).</summary>
        public readonly int TileFine;
        /// <summary>Tiles across and up the whole 8 m grid (listed or not).</summary>
        public readonly int TilesX, TilesY;
        /// <summary>The listed tiles' grid index (ty × <see cref="TilesX"/> + tx), ascending.</summary>
        public readonly int[] Tiles;
        /// <summary>Per grid tile: its index in <see cref="Tiles"/>, or −1 (no block there).</summary>
        public readonly int[] Slot;

        public FineTiles(TrailSnowLayout layout)
        {
            Layout = layout ?? throw new ArgumentNullException(nameof(layout));
            TileFine = CellsPerTile * layout.PerSide;
            TilesX = (layout.GridWidth + CellsPerTile - 1) / CellsPerTile;
            TilesY = (layout.GridHeight + CellsPerTile - 1) / CellsPerTile;
            Slot = new int[TilesX * TilesY];
            for (int t = 0; t < Slot.Length; t++) Slot[t] = -1;
            int count = 0;
            foreach (int cell in layout.BlockCell)
            {
                int t = GridTileOf(cell);
                if (Slot[t] < 0) { Slot[t] = 0; count++; }
            }
            Tiles = new int[count];
            int n = 0;
            for (int t = 0; t < Slot.Length; t++)
                if (Slot[t] >= 0) { Slot[t] = n; Tiles[n++] = t; }
        }

        public int Count => Tiles.Length;

        /// <summary>The grid tile under an 8 m cell.</summary>
        public int GridTileOf(int cell)
        {
            int ci = cell % Layout.GridWidth, cj = cell / Layout.GridWidth;
            return cj / CellsPerTile * TilesX + ci / CellsPerTile;
        }

        /// <summary>A listed tile's south-west 8 m cell.</summary>
        public void Origin(int listed, out int cellX, out int cellY)
        {
            int t = Tiles[listed];
            cellX = t % TilesX * CellsPerTile;
            cellY = t / TilesX * CellsPerTile;
        }

        /// <summary>
        /// A listed tile's blocks into <paramref name="blocks"/>, with each block's south-west fine cell within the tile (x, y
        /// in fine cells from the tile's south-west corner); returns how many (at most 64).
        /// </summary>
        public int BlocksOf(int listed, Span<int> blocks, Span<int> fineX, Span<int> fineY)
        {
            Origin(listed, out int cx, out int cy);
            int n = 0, per = Layout.PerSide;
            for (int j = 0; j < CellsPerTile; j++)
            {
                int y = cy + j;
                if (y >= Layout.GridHeight) break;
                for (int i = 0; i < CellsPerTile; i++)
                {
                    int x = cx + i;
                    if (x >= Layout.GridWidth) break;
                    int b = Layout.BlockOf(y * Layout.GridWidth + x);
                    if (b < 0) continue;
                    blocks[n] = b; fineX[n] = i * per; fineY[n] = j * per;
                    n++;
                }
            }
            return n;
        }
    }
}
