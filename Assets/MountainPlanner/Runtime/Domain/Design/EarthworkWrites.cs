using System;
using System.Collections.Generic;
using MountainPlanner.Domain.Edits;

namespace MountainPlanner.Domain.Design
{
    /// <summary>
    /// What one built piece of work does to the ground (task 08, TL3): the lattice points it claims, the height it sets on
    /// some of them (whole mm over the source, absolute, not added), and the cells it clears. Folding every built piece's
    /// writes in order over the source gives the design's terrain edits (<see cref="DesignEarthwork"/>). A trail claims
    /// its whole interior, so later trails leave its ground alone (TF-10). Derived, never stored (SH-05).
    /// </summary>
    public sealed class EarthworkWrites
    {
        const int Side = EditTileKey.Side, Words = EditTileKey.Count / 64;

        sealed class Tile
        {
            public readonly ulong[] Claimed = new ulong[Words], Written = new ulong[Words], Cleared = new ulong[Words];
            public readonly int[] Delta = new int[EditTileKey.Count];
        }

        readonly SortedDictionary<EditTileKey, Tile> _tiles = new SortedDictionary<EditTileKey, Tile>();

        /// <summary>The lattice point (and cell) bounds of everything here, inclusive; empty when MinColumn &gt; MaxColumn.</summary>
        public int MinColumn { get; private set; } = int.MaxValue;
        public int MinRow { get; private set; } = int.MaxValue;
        public int MaxColumn { get; private set; } = int.MinValue;
        public int MaxRow { get; private set; } = int.MinValue;
        public bool IsEmpty => _tiles.Count == 0;
        public PointRect Bounds => new PointRect(MinColumn, MinRow, MaxColumn, MaxRow);
        public IEnumerable<EditTileKey> Tiles => _tiles.Keys;

        Tile For(int c, int r, out int at)
        {
            var key = EditTileKey.Of(c, r);
            if (!_tiles.TryGetValue(key, out var t)) _tiles[key] = t = new Tile();
            at = (r - key.FirstRow) * Side + (c - key.FirstColumn);
            if (c < MinColumn) MinColumn = c;
            if (c > MaxColumn) MaxColumn = c;
            if (r < MinRow) MinRow = r;
            if (r > MaxRow) MaxRow = r;
            return t;
        }

        Tile Find(int c, int r, out int at)
        {
            var key = EditTileKey.Of(c, r);
            at = (r - key.FirstRow) * Side + (c - key.FirstColumn);
            return _tiles.TryGetValue(key, out var t) ? t : null;
        }

        public void Claim(int column, int row)
        {
            var t = For(column, row, out int at);
            t.Claimed[at >> 6] |= 1UL << (at & 63);
        }

        /// <summary>Sets a point's height change over the source (and claims it).</summary>
        public void Write(int column, int row, int deltaMm)
        {
            var t = For(column, row, out int at);
            ulong bit = 1UL << (at & 63);
            t.Claimed[at >> 6] |= bit;
            t.Written[at >> 6] |= bit;
            t.Delta[at] = deltaMm;
        }

        /// <summary>Clears the trees of cell (column, row).</summary>
        public void Clear(int column, int row)
        {
            var t = For(column, row, out int at);
            t.Cleared[at >> 6] |= 1UL << (at & 63);
        }

        public bool Claims(int column, int row)
        {
            var t = Find(column, row, out int at);
            return t != null && (t.Claimed[at >> 6] & (1UL << (at & 63))) != 0;
        }

        public bool Writes(int column, int row, out int deltaMm)
        {
            var t = Find(column, row, out int at);
            deltaMm = 0;
            if (t == null || (t.Written[at >> 6] & (1UL << (at & 63))) == 0) return false;
            deltaMm = t.Delta[at];
            return true;
        }

        public bool Clears(int column, int row)
        {
            var t = Find(column, row, out int at);
            return t != null && (t.Cleared[at >> 6] & (1UL << (at & 63))) != 0;
        }

        /// <summary>
        /// Canonical bytes for goldens (SH-04): per tile in key order, its column and row, then for every point in row order
        /// a flag byte (1 claimed, 2 written, 4 cleared) and, when written, the delta as four little-endian bytes.
        /// </summary>
        public byte[] CanonicalBytes()
        {
            using var ms = new System.IO.MemoryStream();
            using var w = new System.IO.BinaryWriter(ms);
            foreach (var kv in _tiles)
            {
                w.Write(kv.Key.Column);
                w.Write(kv.Key.Row);
                var t = kv.Value;
                for (int at = 0; at < EditTileKey.Count; at++)
                {
                    ulong bit = 1UL << (at & 63);
                    int word = at >> 6;
                    byte flags = (byte)(((t.Claimed[word] & bit) != 0 ? 1 : 0) | ((t.Written[word] & bit) != 0 ? 2 : 0) | ((t.Cleared[word] & bit) != 0 ? 4 : 0));
                    w.Write(flags);
                    if ((flags & 2) != 0) w.Write(t.Delta[at]);
                }
            }
            w.Flush();
            return ms.ToArray();
        }

        /// <summary>ORs its claims on a tile into <paramref name="bits"/> (64 words, the tile's points in row order).</summary>
        public void OrClaims(EditTileKey key, ulong[] bits)
        {
            if (!_tiles.TryGetValue(key, out var t)) return;
            for (int w = 0; w < Words; w++) bits[w] |= t.Claimed[w];
        }

        /// <summary>ORs its cleared cells on a tile into <paramref name="bits"/>.</summary>
        public void OrClears(EditTileKey key, ulong[] bits)
        {
            if (!_tiles.TryGetValue(key, out var t)) return;
            for (int w = 0; w < Words; w++) bits[w] |= t.Cleared[w];
        }

        /// <summary>Whether it touches the tile at all.</summary>
        public bool Touches(EditTileKey key) => _tiles.ContainsKey(key);

        /// <summary>Lays its writes on a tile being built: heights set where written, cells cleared (never uncleared).</summary>
        public void ApplyTo(TerrainEditTile.Builder tile)
        {
            if (!_tiles.TryGetValue(tile.Key, out var t)) return;
            for (int w = 0; w < Words; w++)
            {
                ulong bits = t.Written[w];
                while (bits != 0)
                {
                    int b = TrailingZeros(bits);
                    bits &= bits - 1;
                    int at = (w << 6) | b;
                    tile.SetDeltaMm(at % Side, at / Side, t.Delta[at]);
                }
                ulong cleared = t.Cleared[w];
                while (cleared != 0)
                {
                    int b = TrailingZeros(cleared);
                    cleared &= cleared - 1;
                    int at = (w << 6) | b;
                    tile.SetCleared(at % Side, at / Side, true);
                }
            }
        }

        /// <summary>
        /// The bounds of the points and cells where two sets of writes differ (claims, heights or clearing), or an empty
        /// rect when they're the same.
        /// </summary>
        public static PointRect Difference(EarthworkWrites a, EarthworkWrites b)
        {
            var rect = PointRect.Empty;
            var keys = new SortedSet<EditTileKey>();
            if (a != null) keys.UnionWith(a._tiles.Keys);
            if (b != null) keys.UnionWith(b._tiles.Keys);
            foreach (var key in keys)
            {
                Tile ta = null, tb = null;
                a?._tiles.TryGetValue(key, out ta);
                b?._tiles.TryGetValue(key, out tb);
                for (int at = 0; at < EditTileKey.Count; at++)
                {
                    int w = at >> 6;
                    ulong bit = 1UL << (at & 63);
                    bool ca = ta != null && (ta.Claimed[w] & bit) != 0, cb = tb != null && (tb.Claimed[w] & bit) != 0;
                    bool wa = ta != null && (ta.Written[w] & bit) != 0, wb = tb != null && (tb.Written[w] & bit) != 0;
                    bool xa = ta != null && (ta.Cleared[w] & bit) != 0, xb = tb != null && (tb.Cleared[w] & bit) != 0;
                    bool same = ca == cb && wa == wb && xa == xb && (!wa || ta.Delta[at] == tb.Delta[at]);
                    if (!same) rect = rect.Include(key.FirstColumn + at % Side, key.FirstRow + at / Side);
                }
            }
            return rect;
        }

        static int TrailingZeros(ulong v)
        {
            int n = 0;
            while ((v & 1) == 0) { v >>= 1; n++; }
            return n;
        }
    }

    /// <summary>An inclusive rectangle of lattice points (or cells): columns west to east, rows north to south.</summary>
    public readonly struct PointRect : IEquatable<PointRect>
    {
        public readonly int MinColumn, MinRow, MaxColumn, MaxRow;
        public PointRect(int minColumn, int minRow, int maxColumn, int maxRow)
        {
            MinColumn = minColumn; MinRow = minRow; MaxColumn = maxColumn; MaxRow = maxRow;
        }

        public static readonly PointRect Empty = new PointRect(int.MaxValue, int.MaxValue, int.MinValue, int.MinValue);
        public bool IsEmpty => MinColumn > MaxColumn || MinRow > MaxRow;

        public PointRect Include(int c, int r) => IsEmpty ? new PointRect(c, r, c, r)
            : new PointRect(Math.Min(MinColumn, c), Math.Min(MinRow, r), Math.Max(MaxColumn, c), Math.Max(MaxRow, r));

        public PointRect Union(PointRect o) => IsEmpty ? o : o.IsEmpty ? this
            : new PointRect(Math.Min(MinColumn, o.MinColumn), Math.Min(MinRow, o.MinRow), Math.Max(MaxColumn, o.MaxColumn), Math.Max(MaxRow, o.MaxRow));

        public PointRect Grow(int n) => IsEmpty ? this : new PointRect(MinColumn - n, MinRow - n, MaxColumn + n, MaxRow + n);

        public bool Intersects(PointRect o) => !IsEmpty && !o.IsEmpty
            && MinColumn <= o.MaxColumn && o.MinColumn <= MaxColumn && MinRow <= o.MaxRow && o.MinRow <= MaxRow;

        public bool Contains(int c, int r) => c >= MinColumn && c <= MaxColumn && r >= MinRow && r <= MaxRow;

        /// <summary>The edit tiles it touches, in key order.</summary>
        public IEnumerable<EditTileKey> TileKeys()
        {
            if (IsEmpty) yield break;
            var a = EditTileKey.Of(MinColumn, MinRow);
            var b = EditTileKey.Of(MaxColumn, MaxRow);
            for (int r = a.Row; r <= b.Row; r++)
                for (int c = a.Column; c <= b.Column; c++)
                    yield return new EditTileKey(c, r);
        }

        public bool Equals(PointRect o) => MinColumn == o.MinColumn && MinRow == o.MinRow && MaxColumn == o.MaxColumn && MaxRow == o.MaxRow;
        public override bool Equals(object obj) => obj is PointRect o && Equals(o);
        public override int GetHashCode() => MinColumn ^ (MinRow << 8) ^ (MaxColumn << 16) ^ (MaxRow << 24);
        public override string ToString() => IsEmpty ? "(empty)" : $"[{MinColumn}..{MaxColumn}] x [{MinRow}..{MaxRow}]";
    }
}
