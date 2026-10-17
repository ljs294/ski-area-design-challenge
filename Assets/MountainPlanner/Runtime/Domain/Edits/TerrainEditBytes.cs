using System;
using System.IO;

namespace MountainPlanner.Domain.Edits
{
    /// <summary>
    /// The terrain edits' canonical bytes, shape version <see cref="ShapeVersion"/>: little-endian, tiles in key order.
    /// Equal sets give equal bytes, so goldens and undo checks compare these (SH-04). Like
    /// <see cref="Design.DesignBytes"/>, it is not the save file: task 04 picks that encoding and freezes it.
    /// <code>
    /// "MPTE"  u16 shape  i32 tiles
    /// per tile: i32 column  i32 row  u8 parts (1 heights, 2 clearing)
    ///           [heights: 4,096 × i32 mm, row-major, north row first]
    ///           [clearing: 64 × u64, cell k is bit k % 64 of word k / 64]
    /// </code>
    /// </summary>
    public static class TerrainEditBytes
    {
        public const int ShapeVersion = 1;
        const byte HeightsPart = 1, ClearingPart = 2;
        static readonly byte[] Magic = { (byte)'M', (byte)'P', (byte)'T', (byte)'E' };
        /// <summary>More tiles than a 6 km core holds (94²), with room to spare.</summary>
        public const int MaxTiles = 1 << 16;

        public static byte[] Encode(TerrainEdits edits)
        {
            if (edits == null) throw new ArgumentNullException(nameof(edits));
            var s = new MemoryStream();
            s.Write(Magic, 0, Magic.Length);
            U16(s, ShapeVersion);
            I32(s, edits.Count);
            var deltas = new int[EditTileKey.Count];
            foreach (var t in edits.Tiles)
            {
                I32(s, t.Key.Column);
                I32(s, t.Key.Row);
                s.WriteByte((byte)((t.HasHeights ? HeightsPart : 0) | (t.HasClearing ? ClearingPart : 0)));
                if (t.HasHeights)
                {
                    t.CopyDeltas(deltas);
                    foreach (int d in deltas) I32(s, d);
                }
                if (t.HasClearing)
                    for (int w = 0; w < TerrainEditTile.ClearingWords; w++) U64(s, t.ClearingWord(w));
            }
            return s.ToArray();
        }

        /// <summary>Reads <see cref="Encode"/>'s bytes back; anything malformed or out of order throws <see cref="FormatException"/>.</summary>
        public static TerrainEdits Decode(byte[] bytes)
        {
            if (bytes == null) throw new FormatException("No bytes.");
            int at = 0;
            for (int i = 0; i < Magic.Length; i++) if (U8(bytes, ref at) != Magic[i]) throw new FormatException("Not terrain edits.");
            int version = U8(bytes, ref at) | (U8(bytes, ref at) << 8);
            if (version != ShapeVersion) throw new FormatException($"Terrain edit shape {version} isn't readable here (this build reads {ShapeVersion}).");
            int count = I32(bytes, ref at);
            if (count < 0 || count > MaxTiles) throw new FormatException($"{count} tiles is out of range.");
            var tiles = new TerrainEditTile[count];
            for (int n = 0; n < count; n++)
            {
                var key = new EditTileKey(I32(bytes, ref at), I32(bytes, ref at));
                if (n > 0 && key.CompareTo(tiles[n - 1].Key) <= 0) throw new FormatException("Terrain edit tiles aren't in key order.");
                int parts = U8(bytes, ref at);
                if (parts == 0 || (parts & ~(HeightsPart | ClearingPart)) != 0) throw new FormatException($"Tile {key} has parts {parts}.");
                var b = new TerrainEditTile.Builder(key);
                bool anyHeight = false, anyClear = false;
                if ((parts & HeightsPart) != 0)
                    for (int j = 0; j < EditTileKey.Side; j++)
                        for (int i = 0; i < EditTileKey.Side; i++)
                        {
                            int d = I32(bytes, ref at);
                            if (d < -TerrainEditTile.DeltaLimitMm || d > TerrainEditTile.DeltaLimitMm) throw new FormatException($"Tile {key} has a {d} mm change.");
                            b.SetDeltaMm(i, j, d);
                            anyHeight |= d != 0;
                        }
                if ((parts & ClearingPart) != 0)
                    for (int w = 0; w < TerrainEditTile.ClearingWords; w++)
                    {
                        ulong bits = U64(bytes, ref at);
                        b.SetClearingWord(w, bits);
                        anyClear |= bits != 0;
                    }
                // Canonical: a part is written only when it holds something.
                if ((parts & HeightsPart) != 0 && !anyHeight) throw new FormatException($"Tile {key} writes an all-zero heights part.");
                if ((parts & ClearingPart) != 0 && !anyClear) throw new FormatException($"Tile {key} writes an empty clearing part.");
                tiles[n] = b.Build();
            }
            if (at != bytes.Length) throw new FormatException("Trailing bytes after the terrain edits.");
            return TerrainEdits.From(tiles);
        }

        static void U16(Stream s, int v) { s.WriteByte((byte)v); s.WriteByte((byte)(v >> 8)); }
        static void I32(Stream s, int v) { for (int i = 0; i < 4; i++) s.WriteByte((byte)(v >> (8 * i))); }
        static void U64(Stream s, ulong v) { for (int i = 0; i < 8; i++) s.WriteByte((byte)(v >> (8 * i))); }

        static byte U8(byte[] b, ref int at)
        {
            if (at >= b.Length) throw new FormatException("The terrain edits end early.");
            return b[at++];
        }

        static int I32(byte[] b, ref int at)
        {
            int v = 0;
            for (int i = 0; i < 4; i++) v |= U8(b, ref at) << (8 * i);
            return v;
        }

        static ulong U64(byte[] b, ref int at)
        {
            ulong v = 0;
            for (int i = 0; i < 8; i++) v |= (ulong)U8(b, ref at) << (8 * i);
            return v;
        }
    }
}
