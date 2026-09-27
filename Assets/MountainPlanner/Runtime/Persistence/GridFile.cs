using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace MountainPlanner.Persistence
{
    /// <summary>The value type of a grid file.</summary>
    public enum GridValueType : byte
    {
        Float32 = 1,
        UInt8 = 2,
    }

    /// <summary>A grid's georeferencing: north-up on EPSG:6350, row 0 at the north edge (0.3 §4.1).</summary>
    public readonly struct GridHeader
    {
        public readonly GridValueType Type;
        public readonly int Width;
        public readonly int Height;
        public readonly double West;
        public readonly double North;
        public readonly double CellSize;

        public GridHeader(GridValueType type, int width, int height, double west, double north, double cellSize)
        {
            if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), "Grids need at least one cell.");
            if (!(cellSize > 0)) throw new ArgumentOutOfRangeException(nameof(cellSize));
            Type = type;
            Width = width;
            Height = height;
            West = west;
            North = north;
            CellSize = cellSize;
        }

        public int BytesPerCell => Type == GridValueType.Float32 ? 4 : 1;
    }

    /// <summary>
    /// The package's grid format (P5): a small header, then row chunks, each losslessly compressed
    /// with a floating-point predictor (byte planes plus differencing, as in TIFF predictor 3) and
    /// Deflate. Chunks let the cache builder read parts of a grid. Layer hashes (manifest) are taken
    /// over the uncompressed values, so they don't depend on the compressor's version.
    /// </summary>
    public static class GridFile
    {
        const uint Magic = 0x5247504D; // "MPGR"
        const ushort Version = 1;
        public const int ChunkRows = 256;

        public static void Write(Stream output, GridHeader header, float[] values)
        {
            if (header.Type != GridValueType.Float32) throw new ArgumentException("Header type must be Float32 for float values.");
            CheckLength(header, values.Length);
            var raw = new byte[values.Length * 4];
            Buffer.BlockCopy(values, 0, raw, 0, raw.Length);
            WriteRaw(output, header, raw);
        }

        public static void Write(Stream output, GridHeader header, byte[] values)
        {
            if (header.Type != GridValueType.UInt8) throw new ArgumentException("Header type must be UInt8 for byte values.");
            CheckLength(header, values.Length);
            WriteRaw(output, header, values);
        }

        public static GridHeader ReadHeader(Stream input) => ReadHeaderAndIndex(new BinaryReader(input, Encoding.UTF8, true), out _);

        public static float[] ReadFloats(Stream input, out GridHeader header)
        {
            byte[] raw = ReadRaw(input, out header);
            if (header.Type != GridValueType.Float32) throw new InvalidDataException("The grid is not float32.");
            var values = new float[header.Width * header.Height];
            Buffer.BlockCopy(raw, 0, values, 0, raw.Length);
            return values;
        }

        public static byte[] ReadBytes(Stream input, out GridHeader header)
        {
            byte[] raw = ReadRaw(input, out header);
            if (header.Type != GridValueType.UInt8) throw new InvalidDataException("The grid is not uint8.");
            return raw;
        }

        /// <summary>SHA-256 (hex) of the uncompressed values in little-endian order: the manifest's layer hash.</summary>
        public static string HashValues(float[] values)
        {
            var raw = new byte[values.Length * 4];
            Buffer.BlockCopy(values, 0, raw, 0, raw.Length);
            return Hex(raw);
        }

        public static string HashValues(byte[] values) => Hex(values);

        static string Hex(byte[] data)
        {
            using (var sha = SHA256.Create())
            {
                byte[] h = sha.ComputeHash(data);
                var sb = new StringBuilder(64);
                foreach (byte b in h) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        static void CheckLength(GridHeader header, int length)
        {
            if (length != header.Width * header.Height)
                throw new ArgumentException($"Expected {header.Width * header.Height} values, got {length}.");
        }

        static void WriteRaw(Stream output, GridHeader header, byte[] raw)
        {
            int chunks = (header.Height + ChunkRows - 1) / ChunkRows;
            var compressed = new byte[chunks][];
            int rowBytes = header.Width * header.BytesPerCell;
            for (int c = 0; c < chunks; c++)
            {
                int rows = Math.Min(ChunkRows, header.Height - c * ChunkRows);
                var chunk = new byte[rows * rowBytes];
                Buffer.BlockCopy(raw, c * ChunkRows * rowBytes, chunk, 0, chunk.Length);
                if (header.Type == GridValueType.Float32) FloatPredictor.Encode(chunk, header.Width, rows);
                else BytePredictor.Encode(chunk, header.Width, rows);
                compressed[c] = Deflate(chunk);
            }

            var w = new BinaryWriter(output, Encoding.UTF8, true);
            w.Write(Magic);
            w.Write(Version);
            w.Write((byte)header.Type);
            w.Write((byte)1); // compression: predictor + Deflate
            w.Write(header.Width);
            w.Write(header.Height);
            w.Write(header.West);
            w.Write(header.North);
            w.Write(header.CellSize);
            w.Write(ChunkRows);
            w.Write(chunks);
            foreach (byte[] c in compressed) w.Write(c.Length);
            foreach (byte[] c in compressed) w.Write(c);
            w.Flush();
        }

        static GridHeader ReadHeaderAndIndex(BinaryReader r, out int[] chunkLengths)
        {
            if (r.ReadUInt32() != Magic) throw new InvalidDataException("Not a grid file.");
            ushort version = r.ReadUInt16();
            if (version != Version) throw new InvalidDataException($"Unsupported grid file version {version}.");
            var type = (GridValueType)r.ReadByte();
            if (type != GridValueType.Float32 && type != GridValueType.UInt8) throw new InvalidDataException($"Unknown grid type {(byte)type}.");
            if (r.ReadByte() != 1) throw new InvalidDataException("Unknown grid compression.");
            var header = new GridHeader(type, r.ReadInt32(), r.ReadInt32(), r.ReadDouble(), r.ReadDouble(), r.ReadDouble());
            int chunkRows = r.ReadInt32();
            int chunks = r.ReadInt32();
            if (chunkRows != ChunkRows || chunks != (header.Height + ChunkRows - 1) / ChunkRows) throw new InvalidDataException("Corrupt chunk table.");
            chunkLengths = new int[chunks];
            for (int c = 0; c < chunks; c++) chunkLengths[c] = r.ReadInt32();
            return header;
        }

        static byte[] ReadRaw(Stream input, out GridHeader header)
        {
            var r = new BinaryReader(input, Encoding.UTF8, true);
            header = ReadHeaderAndIndex(r, out int[] lengths);
            int rowBytes = header.Width * header.BytesPerCell;
            var raw = new byte[header.Height * rowBytes];
            for (int c = 0; c < lengths.Length; c++)
            {
                int rows = Math.Min(ChunkRows, header.Height - c * ChunkRows);
                byte[] packed = r.ReadBytes(lengths[c]);
                if (packed.Length != lengths[c]) throw new InvalidDataException("The grid file is truncated.");
                byte[] chunk = Inflate(packed, rows * rowBytes);
                if (header.Type == GridValueType.Float32) FloatPredictor.Decode(chunk, header.Width, rows);
                else BytePredictor.Decode(chunk, header.Width, rows);
                Buffer.BlockCopy(chunk, 0, raw, c * ChunkRows * rowBytes, chunk.Length);
            }
            return raw;
        }

        static byte[] Deflate(byte[] data)
        {
            using (var ms = new MemoryStream())
            {
                using (var z = new DeflateStream(ms, CompressionLevel.Optimal, true)) z.Write(data, 0, data.Length);
                return ms.ToArray();
            }
        }

        static byte[] Inflate(byte[] packed, int expected)
        {
            var result = new byte[expected];
            using (var z = new DeflateStream(new MemoryStream(packed), CompressionMode.Decompress))
            {
                int read = 0;
                while (read < expected)
                {
                    int n = z.Read(result, read, expected - read);
                    if (n == 0) throw new InvalidDataException("The grid file is corrupt (short chunk).");
                    read += n;
                }
            }
            return result;
        }
    }

    /// <summary>
    /// Floating-point predictor: per row, split float32 values into four byte planes (most significant
    /// first) and difference neighbouring bytes. Smooth terrain then compresses about twice as well.
    /// </summary>
    public static class FloatPredictor
    {
        public static void Encode(byte[] data, int width, int rows)
        {
            int rowBytes = width * 4;
            var planar = new byte[rowBytes];
            for (int r = 0; r < rows; r++)
            {
                int o = r * rowBytes;
                for (int i = 0; i < width; i++)
                    for (int k = 0; k < 4; k++)
                        planar[k * width + i] = data[o + i * 4 + (3 - k)]; // little-endian in, big-endian planes
                for (int i = rowBytes - 1; i > 0; i--) planar[i] = (byte)(planar[i] - planar[i - 1]);
                Buffer.BlockCopy(planar, 0, data, o, rowBytes);
            }
        }

        public static void Decode(byte[] data, int width, int rows)
        {
            int rowBytes = width * 4;
            var planar = new byte[rowBytes];
            for (int r = 0; r < rows; r++)
            {
                int o = r * rowBytes;
                Buffer.BlockCopy(data, o, planar, 0, rowBytes);
                for (int i = 1; i < rowBytes; i++) planar[i] = (byte)(planar[i] + planar[i - 1]);
                for (int i = 0; i < width; i++)
                    for (int k = 0; k < 4; k++)
                        data[o + i * 4 + (3 - k)] = planar[k * width + i];
            }
        }
    }

    /// <summary>Horizontal byte differencing per row (TIFF predictor 2), for uint8 grids.</summary>
    public static class BytePredictor
    {
        public static void Encode(byte[] data, int width, int rows)
        {
            for (int r = 0; r < rows; r++)
                for (int i = width - 1; i > 0; i--)
                    data[r * width + i] = (byte)(data[r * width + i] - data[r * width + i - 1]);
        }

        public static void Decode(byte[] data, int width, int rows)
        {
            for (int r = 0; r < rows; r++)
                for (int i = 1; i < width; i++)
                    data[r * width + i] = (byte)(data[r * width + i] + data[r * width + i - 1]);
        }
    }
}
