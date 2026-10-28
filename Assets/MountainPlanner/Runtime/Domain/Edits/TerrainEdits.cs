using System;
using System.Collections.Generic;

namespace MountainPlanner.Domain.Edits
{
    /// <summary>The change at a global 1 m lattice point, whole mm: a set of edits, or a brush stroke in hand over one (task 15's live ground).</summary>
    public interface IPointDeltas
    {
        int DeltaMmAt(int pointColumn, int pointRow);
    }

    /// <summary>
    /// Every committed terrain edit of a design: a sparse, immutable set of 64 m <see cref="TerrainEditTile"/>s over the
    /// immutable package (GR-09). A change makes a new set that shares every tile it didn't touch, so an undo step
    /// costs only the tiles it changed (owner ruling P3-05 Q2), and undo restores byte-identical heights and trees by
    /// switching back to the earlier set (CF-05, CL-07). Tiles iterate in key order (SH-04); empty tiles are never held.
    /// </summary>
    public sealed class TerrainEdits : IPointDeltas
    {
        public static readonly TerrainEdits Empty = new TerrainEdits(Array.Empty<TerrainEditTile>());

        readonly TerrainEditTile[] _tiles;   // sorted by key, none empty

        TerrainEdits(TerrainEditTile[] tiles) => _tiles = tiles;

        public int Count => _tiles.Length;
        public bool IsEmpty => _tiles.Length == 0;

        /// <summary>The tiles in key order.</summary>
        public IReadOnlyList<TerrainEditTile> Tiles => _tiles;

        /// <summary>A set holding these tiles; empty ones are dropped, and two tiles with one key are refused.</summary>
        public static TerrainEdits From(IEnumerable<TerrainEditTile> tiles)
        {
            if (tiles == null) throw new ArgumentNullException(nameof(tiles));
            var list = new List<TerrainEditTile>();
            foreach (var t in tiles)
            {
                if (t == null) throw new ArgumentException("A null tile.", nameof(tiles));
                if (!t.IsEmpty) list.Add(t);
            }
            list.Sort((a, b) => a.Key.CompareTo(b.Key));
            for (int i = 1; i < list.Count; i++)
                if (list[i].Key.Equals(list[i - 1].Key)) throw new ArgumentException($"Tile {list[i].Key} appears twice.", nameof(tiles));
            return list.Count == 0 ? Empty : new TerrainEdits(list.ToArray());
        }

        public bool TryGet(EditTileKey key, out TerrainEditTile tile)
        {
            int i = Find(key);
            tile = i >= 0 ? _tiles[i] : null;
            return i >= 0;
        }

        /// <summary>The tile at a key, or an empty one.</summary>
        public TerrainEditTile Get(EditTileKey key) => TryGet(key, out var t) ? t : TerrainEditTile.Empty(key);

        /// <summary>
        /// A new set with these tiles put in place of the ones at their keys; an empty tile removes its key. Untouched
        /// tiles are shared, not copied.
        /// </summary>
        public TerrainEdits With(IEnumerable<TerrainEditTile> replacements)
        {
            if (replacements == null) throw new ArgumentNullException(nameof(replacements));
            var byKey = new SortedDictionary<EditTileKey, TerrainEditTile>();
            foreach (var t in _tiles) byKey[t.Key] = t;
            var seen = new HashSet<EditTileKey>();
            foreach (var r in replacements)
            {
                if (r == null) throw new ArgumentException("A null tile.", nameof(replacements));
                if (!seen.Add(r.Key)) throw new ArgumentException($"Tile {r.Key} appears twice.", nameof(replacements));
                if (r.IsEmpty) byKey.Remove(r.Key);
                else byKey[r.Key] = r;
            }
            if (byKey.Count == 0) return Empty;
            var tiles = new TerrainEditTile[byKey.Count];
            byKey.Values.CopyTo(tiles, 0);
            return new TerrainEdits(tiles);
        }

        /// <summary>
        /// The keys whose content differs between two sets, in key order: the tiles to rebuild when the terrain moves
        /// from one to the other (an edit, an undo, a redo or a load).
        /// </summary>
        public static List<EditTileKey> Changed(TerrainEdits from, TerrainEdits to)
        {
            if (from == null) throw new ArgumentNullException(nameof(from));
            if (to == null) throw new ArgumentNullException(nameof(to));
            var keys = new List<EditTileKey>();
            if (ReferenceEquals(from, to)) return keys;
            int a = 0, b = 0;
            while (a < from._tiles.Length || b < to._tiles.Length)
            {
                if (b >= to._tiles.Length) { keys.Add(from._tiles[a++].Key); continue; }
                if (a >= from._tiles.Length) { keys.Add(to._tiles[b++].Key); continue; }
                var ta = from._tiles[a];
                var tb = to._tiles[b];
                int c = ta.Key.CompareTo(tb.Key);
                if (c < 0) { keys.Add(ta.Key); a++; }
                else if (c > 0) { keys.Add(tb.Key); b++; }
                else
                {
                    if (!ta.Equals(tb)) keys.Add(ta.Key);
                    a++;
                    b++;
                }
            }
            return keys;
        }

        /// <summary>The change at a global 1 m lattice point, in millimetres.</summary>
        public int DeltaMmAt(int pointColumn, int pointRow)
        {
            var key = EditTileKey.Of(pointColumn, pointRow);
            int i = Find(key);
            return i < 0 ? 0 : _tiles[i].DeltaMm(pointColumn - key.FirstColumn, pointRow - key.FirstRow);
        }

        /// <summary>Whether the global 1 m cell is cleared.</summary>
        public bool IsClearedAt(int cellColumn, int cellRow)
        {
            var key = EditTileKey.Of(cellColumn, cellRow);
            int i = Find(key);
            return i >= 0 && _tiles[i].IsCleared(cellColumn - key.FirstColumn, cellRow - key.FirstRow);
        }

        int Find(EditTileKey key)
        {
            int lo = 0, hi = _tiles.Length - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                int c = _tiles[mid].Key.CompareTo(key);
                if (c == 0) return mid;
                if (c < 0) lo = mid + 1;
                else hi = mid - 1;
            }
            return -1;
        }
    }
}
