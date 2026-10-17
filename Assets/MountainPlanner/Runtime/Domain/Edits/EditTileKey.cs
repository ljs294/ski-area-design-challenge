using System;
using MountainPlanner.Domain.Geo;

namespace MountainPlanner.Domain.Edits
{
    /// <summary>
    /// A 64 m edit tile's place (GR-09, owner ruling P3-05 Q1): column east and row south from the terrain cache's
    /// north-west corner (<see cref="TileGrid"/>), so 16 × 16 edit tiles fill one 1,024 m cache tile exactly and line
    /// up with the forest's 64 m tiles. Ordered row first, then column, like <see cref="TileKey"/>.
    /// </summary>
    public readonly struct EditTileKey : IEquatable<EditTileKey>, IComparable<EditTileKey>
    {
        /// <summary>Edge length in metres; also the points (and clearing cells) per side at 1 m.</summary>
        public const int Metres = 64;
        public const int Side = 64;
        public const int Count = Side * Side;
        /// <summary>Edit tiles per cache tile side (1,024 / 64).</summary>
        public const int PerCacheTile = 16;

        public readonly int Column;
        public readonly int Row;

        public EditTileKey(int column, int row)
        {
            Column = column;
            Row = row;
        }

        /// <summary>The cache tile this edit tile lies in.</summary>
        public TileKey CacheTile => new TileKey(FloorDiv(Column, PerCacheTile), FloorDiv(Row, PerCacheTile));

        /// <summary>The edit tile holding the 1 m lattice point (or cell) at a global column and row.</summary>
        public static EditTileKey Of(int pointColumn, int pointRow) => new EditTileKey(FloorDiv(pointColumn, Side), FloorDiv(pointRow, Side));

        /// <summary>The tile's first global point column and row (its north-west corner).</summary>
        public int FirstColumn => Column * Side;
        public int FirstRow => Row * Side;

        public bool Equals(EditTileKey other) => Column == other.Column && Row == other.Row;
        public override bool Equals(object obj) => obj is EditTileKey other && Equals(other);
        public override int GetHashCode() => Column * 7919 ^ Row;
        public int CompareTo(EditTileKey other) => Row != other.Row ? Row.CompareTo(other.Row) : Column.CompareTo(other.Column);
        public static bool operator ==(EditTileKey a, EditTileKey b) => a.Equals(b);
        public static bool operator !=(EditTileKey a, EditTileKey b) => !a.Equals(b);
        public override string ToString() => $"e{Column}_{Row}";

        static int FloorDiv(int a, int b) => a >= 0 ? a / b : -((-a + b - 1) / b);
    }
}
