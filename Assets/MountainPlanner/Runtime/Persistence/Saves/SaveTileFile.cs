using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace MountainPlanner.Persistence.Saves
{
    /// <summary>
    /// A save's tile file (task P3-04): <c>tiles/&lt;sha256&gt;.tile</c>, named by the SHA-256 of its own bytes, so a tile is
    /// written once and never overwritten, and an interrupted save can't damage one the last good save uses. Little-endian:
    /// <code>
    /// "MPTL"  u16 layout (1)  u8 kind  u8 0  i32 x  i32 y  i32 payload length  payload
    /// </code>
    /// Uncompressed, so every runtime writes the same bytes.
    /// </summary>
    public static class SaveTileFile
    {
        public const string Extension = ".tile";
        public const int Layout = 1;
        /// <summary>Generous: a 64 × 64 tile of 8-byte cells is 32 KB.</summary>
        public const int MaxPayload = 1 << 20;
        static readonly byte[] Magic = { (byte)'M', (byte)'P', (byte)'T', (byte)'L' };
        const int HeaderBytes = 4 + 2 + 1 + 1 + 4 + 4 + 4;

        public static byte[] Encode(SaveTileKind kind, SaveTile tile)
        {
            if (tile.Payload.Length > MaxPayload) throw new ArgumentException("A tile's payload is too large.", nameof(tile));
            var b = new byte[HeaderBytes + tile.Payload.Length];
            Array.Copy(Magic, b, 4);
            b[4] = Layout & 0xff;
            b[5] = Layout >> 8;
            b[6] = (byte)kind;
            b[7] = 0;
            PutI32(b, 8, tile.X);
            PutI32(b, 12, tile.Y);
            PutI32(b, 16, tile.Payload.Length);
            Array.Copy(tile.Payload, 0, b, HeaderBytes, tile.Payload.Length);
            return b;
        }

        /// <summary>Reads a tile back; anything malformed, or of the wrong kind, throws <see cref="InvalidDataException"/>.</summary>
        public static SaveTile Decode(byte[] b, SaveTileKind expected)
        {
            if (b == null || b.Length < HeaderBytes) throw new InvalidDataException("A save tile is too short.");
            for (int i = 0; i < 4; i++) if (b[i] != Magic[i]) throw new InvalidDataException("Not a save tile.");
            int layout = b[4] | (b[5] << 8);
            if (layout != Layout) throw new InvalidDataException($"Save tile layout {layout} isn't readable here.");
            if (b[6] != (byte)expected) throw new InvalidDataException($"A {expected} tile holds another kind ({b[6]}).");
            int length = I32(b, 16);
            if (length < 0 || length > MaxPayload || HeaderBytes + length != b.Length) throw new InvalidDataException("A save tile's length is wrong.");
            var payload = new byte[length];
            Array.Copy(b, HeaderBytes, payload, 0, length);
            return new SaveTile(I32(b, 8), I32(b, 12), payload);
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

        static int I32(byte[] b, int at)
        {
            int v = 0;
            for (int i = 0; i < 4; i++) v |= b[at + i] << (8 * i);
            return v;
        }
    }
}
