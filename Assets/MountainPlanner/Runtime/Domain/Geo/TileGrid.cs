using System;
using System.Collections.Generic;

namespace MountainPlanner.Domain.Geo
{
    /// <summary>A terrain tile's position in its <see cref="TileGrid"/>: column east, row south.</summary>
    public readonly struct TileKey : IEquatable<TileKey>, IComparable<TileKey>
    {
        public readonly int Column;
        public readonly int Row;

        public TileKey(int column, int row)
        {
            Column = column;
            Row = row;
        }

        public bool Equals(TileKey other) => Column == other.Column && Row == other.Row;
        public override bool Equals(object obj) => obj is TileKey other && Equals(other);
        public override int GetHashCode() => Column * 7919 ^ Row;
        public int CompareTo(TileKey other) => Row != other.Row ? Row.CompareTo(other.Row) : Column.CompareTo(other.Column);
        public override string ToString() => $"t{Column}_{Row}";
    }

    /// <summary>
    /// The terrain cache's tiling (0.3 §4.3): 1,024 m tiles, which fit Unity's 2ⁿ+1 heightmaps
    /// (1,025² samples at 1 m, 513² at 2 m) and share their edge rows. The tiling starts at the
    /// ring's north-west corner, so a 5 km site in its 11 km ring makes 11 × 11 = 121 tiles.
    /// Tiles that touch the core are 1 m; the rest are 2 m.
    /// </summary>
    public readonly struct TileGrid
    {
        public const double TileMetres = 1024;

        public readonly double West;
        public readonly double North;
        public readonly int Columns;
        public readonly int Rows;
        public readonly AlbersBox Core;

        TileGrid(double west, double north, int columns, int rows, AlbersBox core)
        {
            West = west;
            North = north;
            Columns = columns;
            Rows = rows;
            Core = core;
        }

        public static TileGrid For(SiteSquare site)
        {
            var ring = site.Ring;
            int cols = (int)Math.Ceiling(ring.Width / TileMetres - 1e-9);
            int rows = (int)Math.Ceiling(ring.Height / TileMetres - 1e-9);
            return new TileGrid(ring.West, ring.North, cols, rows, site.Core);
        }

        public int Count => Columns * Rows;

        /// <summary>The tile's footprint. Edge tiles may reach past the ring; that part holds no data.</summary>
        public AlbersBox Bounds(TileKey key) =>
            new AlbersBox(West + key.Column * TileMetres, North - (key.Row + 1) * TileMetres,
                          West + (key.Column + 1) * TileMetres, North - key.Row * TileMetres);

        /// <summary>True for tiles that overlap the 1 m core.</summary>
        public bool IsCore(TileKey key) => Bounds(key).Intersects(Core);

        /// <summary>Heightmap samples per side: 1,025 for core tiles (1 m), 513 for ring tiles (2 m).</summary>
        public int Resolution(TileKey key) => IsCore(key) ? 1025 : 513;

        /// <summary>The sample grid of a tile. Samples sit on cell corners, so neighbours share their edge row.</summary>
        public double SampleSpacing(TileKey key) => IsCore(key) ? 1 : 2;

        public bool TryTileAt(AlbersPoint p, out TileKey key)
        {
            int c = (int)Math.Floor((p.X - West) / TileMetres), r = (int)Math.Floor((North - p.Y) / TileMetres);
            key = new TileKey(c, r);
            return c >= 0 && c < Columns && r >= 0 && r < Rows;
        }

        /// <summary>Every tile, north row first, west to east (a stable order).</summary>
        public IEnumerable<TileKey> All()
        {
            for (int r = 0; r < Rows; r++)
                for (int c = 0; c < Columns; c++)
                    yield return new TileKey(c, r);
        }
    }
}
