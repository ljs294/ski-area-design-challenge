using System;

namespace MountainPlanner.Domain.Edits
{
    /// <summary>
    /// One 64 m tile of committed terrain edits (GR-09, CL-05; owner ruling P3-05 Q1). Immutable.
    /// <list type="bullet">
    /// <item><b>Heights:</b> 64 × 64 lattice points at 1 m, each a change in whole millimetres over the package's float32
    /// heights; 0 means untouched. Point (i, j) sits at the tile's north-west corner plus i m east and j m south, on
    /// the same corner lattice as the terrain cache's 1 m heightmaps. A tile owns its west and north edge points; the
    /// shared east and south ones belong to its neighbours.</item>
    /// <item><b>Clearing:</b> 64 × 64 cells of 1 m, one bit each. Cell (i, j) is the square east and south of point
    /// (i, j); a tree whose trunk stands in a cleared cell is removed, and its density there is 0.</item>
    /// </list>
    /// Either part may be absent (all zeros). Equal tiles have equal content; <see cref="TerrainEditBytes"/> writes it.
    /// </summary>
    public sealed class TerrainEditTile : IEquatable<TerrainEditTile>
    {
        /// <summary>Largest change, ±1 km: well past any 1:1 cut or fill a 400 ft trail can make.</summary>
        public const int DeltaLimitMm = 1_000_000;
        public const int ClearingWords = EditTileKey.Count / 64;

        readonly int[] _deltaMm;      // null when no height changes
        readonly ulong[] _cleared;    // null when nothing is cleared

        public EditTileKey Key { get; }

        TerrainEditTile(EditTileKey key, int[] deltaMm, ulong[] cleared)
        {
            Key = key;
            _deltaMm = deltaMm;
            _cleared = cleared;
        }

        public bool HasHeights => _deltaMm != null;
        public bool HasClearing => _cleared != null;
        public bool IsEmpty => _deltaMm == null && _cleared == null;

        /// <summary>Point (i, j)'s change in millimetres.</summary>
        public int DeltaMm(int i, int j) => _deltaMm == null ? 0 : _deltaMm[Index(i, j)];

        /// <summary>Whether cell (i, j) is cleared.</summary>
        public bool IsCleared(int i, int j)
        {
            if (_cleared == null) return false;
            int k = Index(i, j);
            return (_cleared[k >> 6] & (1UL << (k & 63))) != 0;
        }

        /// <summary>Clearing word w (cells 64w to 64w + 63 in row-major order, bit 0 first).</summary>
        public ulong ClearingWord(int w) => _cleared == null ? 0 : _cleared[w];

        /// <summary>Copies the heights out, row-major, north row first; zeros when there are none.</summary>
        public void CopyDeltas(int[] into)
        {
            if (into == null || into.Length != EditTileKey.Count) throw new ArgumentException("Needs 4,096 values.", nameof(into));
            if (_deltaMm == null) Array.Clear(into, 0, into.Length);
            else Array.Copy(_deltaMm, into, into.Length);
        }

        public static TerrainEditTile Empty(EditTileKey key) => new TerrainEditTile(key, null, null);

        /// <summary>A builder starting from this tile's content.</summary>
        public Builder Edit() => new Builder(Key, _deltaMm, _cleared);

        public bool Equals(TerrainEditTile other)
        {
            if (ReferenceEquals(this, other)) return true;
            if (other is null || !Key.Equals(other.Key)) return false;
            return SameInts(_deltaMm, other._deltaMm) && SameWords(_cleared, other._cleared);
        }

        public override bool Equals(object obj) => obj is TerrainEditTile t && Equals(t);
        public override int GetHashCode() => Key.GetHashCode();
        public override string ToString() => $"{Key}{(HasHeights ? " heights" : "")}{(HasClearing ? " clearing" : "")}";

        static int Index(int i, int j)
        {
            if ((uint)i >= EditTileKey.Side || (uint)j >= EditTileKey.Side) throw new ArgumentOutOfRangeException(nameof(i), $"({i}, {j}) is outside the 64 m tile.");
            return j * EditTileKey.Side + i;
        }

        static bool SameInts(int[] a, int[] b)
        {
            if (a == null || b == null) return a == b;
            for (int k = 0; k < a.Length; k++) if (a[k] != b[k]) return false;
            return true;
        }

        static bool SameWords(ulong[] a, ulong[] b)
        {
            if (a == null || b == null) return a == b;
            for (int k = 0; k < a.Length; k++) if (a[k] != b[k]) return false;
            return true;
        }

        /// <summary>
        /// Builds a tile. An all-zero part is dropped on <see cref="Build"/>, so equal content always gives equal tiles.
        /// </summary>
        public sealed class Builder
        {
            EditTileKey _key;
            int[] _deltaMm = new int[EditTileKey.Count];
            ulong[] _cleared = new ulong[ClearingWords];

            public Builder(EditTileKey key) => _key = key;

            /// <summary>
            /// Starts again from a tile's content (or empty, for null), reusing this builder's arrays where
            /// <see cref="BuildTaking"/> hasn't taken them: no copy of the tile and no new arrays (round 4).
            /// </summary>
            internal Builder Reset(EditTileKey key, TerrainEditTile from)
            {
                _key = key;
                _deltaMm ??= new int[EditTileKey.Count];
                _cleared ??= new ulong[ClearingWords];
                if (from?._deltaMm != null) Array.Copy(from._deltaMm, _deltaMm, _deltaMm.Length);
                else Array.Clear(_deltaMm, 0, _deltaMm.Length);
                if (from?._cleared != null) Array.Copy(from._cleared, _cleared, _cleared.Length);
                else Array.Clear(_cleared, 0, _cleared.Length);
                return this;
            }

            /// <summary>
            /// <see cref="Build"/> without the copies: the tile takes the arrays it needs, and the builder makes new ones on
            /// its next <see cref="Reset"/>. The same tile, byte for byte.
            /// </summary>
            internal TerrainEditTile BuildTaking()
            {
                int[] d = null;
                for (int k = 0; k < _deltaMm.Length; k++)
                    if (_deltaMm[k] != 0) { d = _deltaMm; _deltaMm = null; break; }
                ulong[] c = null;
                for (int k = 0; k < _cleared.Length; k++)
                    if (_cleared[k] != 0) { c = _cleared; _cleared = null; break; }
                return new TerrainEditTile(_key, d, c);
            }

            internal Builder(EditTileKey key, int[] deltaMm, ulong[] cleared)
            {
                _key = key;
                if (deltaMm != null) Array.Copy(deltaMm, _deltaMm, _deltaMm.Length);
                if (cleared != null) Array.Copy(cleared, _cleared, _cleared.Length);
            }

            public EditTileKey Key => _key;

            public int DeltaMm(int i, int j) => _deltaMm[Index(i, j)];

            public Builder SetDeltaMm(int i, int j, int deltaMm)
            {
                if (deltaMm < -DeltaLimitMm || deltaMm > DeltaLimitMm)
                    throw new ArgumentOutOfRangeException(nameof(deltaMm), $"{deltaMm} mm is past the ±{DeltaLimitMm} mm limit.");
                _deltaMm[Index(i, j)] = deltaMm;
                return this;
            }

            public bool IsCleared(int i, int j)
            {
                int k = Index(i, j);
                return (_cleared[k >> 6] & (1UL << (k & 63))) != 0;
            }

            public Builder SetCleared(int i, int j, bool cleared)
            {
                int k = Index(i, j);
                if (cleared) _cleared[k >> 6] |= 1UL << (k & 63);
                else _cleared[k >> 6] &= ~(1UL << (k & 63));
                return this;
            }

            /// <summary>Sets clearing word w directly (64 cells, bit 0 first), for readers of the canonical bytes.</summary>
            public Builder SetClearingWord(int w, ulong bits)
            {
                _cleared[w] = bits;
                return this;
            }

            public TerrainEditTile Build()
            {
                int[] d = null;
                for (int k = 0; k < _deltaMm.Length; k++)
                    if (_deltaMm[k] != 0) { d = (int[])_deltaMm.Clone(); break; }
                ulong[] c = null;
                for (int k = 0; k < _cleared.Length; k++)
                    if (_cleared[k] != 0) { c = (ulong[])_cleared.Clone(); break; }
                return new TerrainEditTile(_key, d, c);
            }
        }
    }
}
