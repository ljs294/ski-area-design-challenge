using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using MountainPlanner.Domain.Edits;

namespace MountainPlanner.Persistence.Saves
{
    /// <summary>
    /// A save's tile file (task P3-04; frozen with save v1): one 64 m <see cref="TerrainEditTile"/> (decision TE1) as
    /// <c>tiles/&lt;sha256&gt;.tile</c>, named by the SHA-256 of its own bytes, so a tile is written once and never
    /// overwritten, and an interrupted save can't damage one the last good save uses. Little-endian, uncompressed (every
    /// runtime writes the same bytes):
    /// <code>
    /// "MPTL"  u16 layout (1)  u8 parts (1 heights, 2 clearing)  u8 0  i32 column  i32 row
    /// [heights: 4,096 × i32 whole-mm changes, point (i, j) at j × 64 + i]
    /// [clearing: 64 × u64 words, cell k at word k / 64, bit k % 64]
    /// </code>
    /// </summary>
    public static class SaveTileFile
    {
        public const string Extension = ".tile";
        public const int Layout = 1;
        const byte HeightsPart = 1, ClearingPart = 2;
        static readonly byte[] Magic = { (byte)'M', (byte)'P', (byte)'T', (byte)'L' };
        const int HeaderBytes = 4 + 2 + 1 + 1 + 4 + 4;
        const int HeightBytes = EditTileKey.Count * 4, ClearingBytes = TerrainEditTile.ClearingWords * 8;

        public static byte[] Encode(TerrainEditTile tile)
        {
            if (tile == null || tile.IsEmpty) throw new ArgumentException("An empty tile isn't saved.", nameof(tile));
            int length = HeaderBytes + (tile.HasHeights ? HeightBytes : 0) + (tile.HasClearing ? ClearingBytes : 0);
            var b = new byte[length];
            Array.Copy(Magic, b, 4);
            b[4] = Layout & 0xff;
            b[5] = Layout >> 8;
            b[6] = (byte)((tile.HasHeights ? HeightsPart : 0) | (tile.HasClearing ? ClearingPart : 0));
            b[7] = 0;
            PutI32(b, 8, tile.Key.Column);
            PutI32(b, 12, tile.Key.Row);
            int at = HeaderBytes;
            if (tile.HasHeights)
            {
                var deltas = new int[EditTileKey.Count];
                tile.CopyDeltas(deltas);
                foreach (int d in deltas) { PutI32(b, at, d); at += 4; }
            }
            if (tile.HasClearing)
                for (int w = 0; w < TerrainEditTile.ClearingWords; w++) { PutU64(b, at, tile.ClearingWord(w)); at += 8; }
            return b;
        }

        /// <summary>Reads a tile back; anything malformed throws <see cref="InvalidDataException"/>.</summary>
        public static TerrainEditTile Decode(byte[] b)
        {
            if (b == null || b.Length < HeaderBytes) throw new InvalidDataException("A save tile is too short.");
            for (int i = 0; i < 4; i++) if (b[i] != Magic[i]) throw new InvalidDataException("Not a save tile.");
            int layout = b[4] | (b[5] << 8);
            if (layout != Layout) throw new InvalidDataException($"Save tile layout {layout} isn't readable here.");
            byte parts = b[6];
            if (parts == 0 || (parts & ~(HeightsPart | ClearingPart)) != 0 || b[7] != 0) throw new InvalidDataException("A save tile's parts are wrong.");
            bool heights = (parts & HeightsPart) != 0, clearing = (parts & ClearingPart) != 0;
            if (b.Length != HeaderBytes + (heights ? HeightBytes : 0) + (clearing ? ClearingBytes : 0)) throw new InvalidDataException("A save tile's length is wrong.");
            var builder = new TerrainEditTile.Builder(new EditTileKey(I32(b, 8), I32(b, 12)));
            int at = HeaderBytes;
            try
            {
                if (heights)
                    for (int k = 0; k < EditTileKey.Count; k++, at += 4) builder.SetDeltaMm(k % EditTileKey.Side, k / EditTileKey.Side, I32(b, at));
                if (clearing)
                    for (int w = 0; w < TerrainEditTile.ClearingWords; w++, at += 8) builder.SetClearingWord(w, U64(b, at));
            }
            catch (ArgumentException e) { throw new InvalidDataException("A save tile is damaged: " + e.Message, e); }
            var tile = builder.Build();
            // Canonical: a part present in the file is never all zeros (the writer drops those), so equal tiles have equal bytes.
            if (tile.HasHeights != heights || tile.HasClearing != clearing) throw new InvalidDataException("A save tile holds an empty part.");
            return tile;
        }

        /// <summary>Lower-case hex SHA-256: the tile's file name and its entry in save.json.</summary>
        public static string Hash(byte[] bytes)
        {
            using (var sha = SHA256.Create())
            {
                byte[] h = sha.ComputeHash(bytes);
                var sb = new StringBuilder(64);
                foreach (byte x in h) sb.Append(x.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }

        public static bool IsHash(string s)
        {
            if (s == null || s.Length != 64) return false;
            foreach (char c in s) if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
            return true;
        }

        static void PutI32(byte[] b, int at, int v)
        {
            for (int i = 0; i < 4; i++) b[at + i] = (byte)(v >> (8 * i));
        }

        static void PutU64(byte[] b, int at, ulong v)
        {
            for (int i = 0; i < 8; i++) b[at + i] = (byte)(v >> (8 * i));
        }

        static int I32(byte[] b, int at)
        {
            int v = 0;
            for (int i = 0; i < 4; i++) v |= b[at + i] << (8 * i);
            return v;
        }

        static ulong U64(byte[] b, int at)
        {
            ulong v = 0;
            for (int i = 0; i < 8; i++) v |= (ulong)b[at + i] << (8 * i);
            return v;
        }
    }
}
