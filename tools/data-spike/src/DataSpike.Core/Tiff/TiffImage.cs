using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MountainPlanner.DataSpike.IO;

namespace MountainPlanner.DataSpike.Tiff
{
    /// <summary>One TIFF image file directory: a full-resolution image or an overview.</summary>
    public sealed class TiffDirectory
    {
        public int Index;
        public int Width, Height;
        public int BitsPerSample, SampleFormat, Compression, Predictor, SamplesPerPixel;
        public bool Tiled;
        public int BlockWidth, BlockHeight; // tile size, or (width, rowsPerStrip) for strips
        public long[] BlockOffsets = Array.Empty<long>();
        public long[] BlockByteCounts = Array.Empty<long>();
        public bool IsOverview;
        public double[]? PixelScale;   // GeoTIFF ModelPixelScale
        public double[]? TiePoint;     // GeoTIFF ModelTiepoint (i, j, k, x, y, z)
        public ushort[]? GeoKeys;      // GeoTIFF GeoKeyDirectory
        public string? NoData;         // GDAL_NODATA

        public int BlocksAcross => (Width + BlockWidth - 1) / BlockWidth;
        public int BlocksDown => (Height + BlockHeight - 1) / BlockHeight;

        /// <summary>EPSG code of the projected (3072) or geographic (2048) CRS, if present.</summary>
        public int? Epsg
        {
            get
            {
                if (GeoKeys == null || GeoKeys.Length < 4) return null;
                int keys = GeoKeys[3];
                int? geographic = null;
                for (int k = 0; k < keys; k++)
                {
                    int i = 4 + k * 4;
                    if (i + 3 >= GeoKeys.Length) break;
                    if (GeoKeys[i] == 3072 && GeoKeys[i + 1] == 0) return GeoKeys[i + 3];
                    if (GeoKeys[i] == 2048 && GeoKeys[i + 1] == 0) geographic = GeoKeys[i + 3];
                }
                return geographic;
            }
        }
    }

    /// <summary>
    /// Minimal GeoTIFF / Cloud Optimized GeoTIFF reader over an <see cref="IByteSource"/>: classic and
    /// BigTIFF, tiles or strips, LZW or Deflate, predictors 1–3, 8-bit or float32 single-band data.
    /// Only the directories and the blocks a caller asks for are fetched.
    /// </summary>
    public sealed class TiffImage
    {
        const int HeaderProbe = 65536;
        readonly IByteSource _source;
        readonly bool _little;
        readonly bool _big;
        byte[] _prefix = Array.Empty<byte>();

        public IReadOnlyList<TiffDirectory> Directories { get; private set; } = Array.Empty<TiffDirectory>();
        public string Name => _source.Name;

        TiffImage(IByteSource source, bool little, bool big) { _source = source; _little = little; _big = big; }

        public static async Task<TiffImage> OpenAsync(IByteSource source, CancellationToken ct = default, int probeBytes = HeaderProbe)
        {
            byte[] head = await ReadPrefix(source, probeBytes, ct).ConfigureAwait(false);
            bool little = head[0] == (byte)'I';
            int magic = little ? head[2] | head[3] << 8 : head[2] << 8 | head[3];
            if (magic != 42 && magic != 43) throw new InvalidDataException("Not a TIFF file");
            var image = new TiffImage(source, little, magic == 43) { _prefix = head };
            await image.ReadDirectories(ct).ConfigureAwait(false);
            return image;
        }

        static async Task<byte[]> ReadPrefix(IByteSource source, int length, CancellationToken ct)
        {
            try { return await source.ReadAsync(0, length, ct).ConfigureAwait(false); }
            catch (ArgumentOutOfRangeException)
            {
                // Small in-memory files: read what exists.
                for (int n = length / 2; n >= 16; n /= 2)
                {
                    try { return await source.ReadAsync(0, n, ct).ConfigureAwait(false); }
                    catch (ArgumentOutOfRangeException) { }
                }
                throw;
            }
        }

        async Task ReadDirectories(CancellationToken ct)
        {
            var list = new List<TiffDirectory>();
            long offset = _big ? (long)U64(_prefix, 8) : U32(_prefix, 4);
            while (offset != 0 && list.Count < 64)
            {
                int countSize = _big ? 8 : 2, entrySize = _big ? 20 : 12, nextSize = _big ? 8 : 4;
                byte[] countBytes = await Bytes(offset, countSize, ct).ConfigureAwait(false);
                long count = _big ? (long)U64(countBytes, 0) : U16(countBytes, 0);
                byte[] entries = await Bytes(offset + countSize, (int)(count * entrySize) + nextSize, ct).ConfigureAwait(false);

                var dir = new TiffDirectory { Index = list.Count, SamplesPerPixel = 1, Predictor = 1, SampleFormat = 1, Compression = 1 };
                long rowsPerStrip = 0;
                for (int e = 0; e < count; e++)
                {
                    int p = e * entrySize;
                    int tag = U16(entries, p), type = U16(entries, p + 2);
                    long n = _big ? (long)U64(entries, p + 4) : U32(entries, p + 4);
                    int valueAt = p + (_big ? 12 : 8);
                    switch (tag)
                    {
                        case 254: dir.IsOverview = (await Longs(entries, valueAt, type, n, ct))[0] == 1; break;
                        case 256: dir.Width = (int)(await Longs(entries, valueAt, type, n, ct))[0]; break;
                        case 257: dir.Height = (int)(await Longs(entries, valueAt, type, n, ct))[0]; break;
                        case 258: dir.BitsPerSample = (int)(await Longs(entries, valueAt, type, n, ct))[0]; break;
                        case 259: dir.Compression = (int)(await Longs(entries, valueAt, type, n, ct))[0]; break;
                        case 277: dir.SamplesPerPixel = (int)(await Longs(entries, valueAt, type, n, ct))[0]; break;
                        case 278: rowsPerStrip = (await Longs(entries, valueAt, type, n, ct))[0]; break;
                        case 317: dir.Predictor = (int)(await Longs(entries, valueAt, type, n, ct))[0]; break;
                        case 322: dir.BlockWidth = (int)(await Longs(entries, valueAt, type, n, ct))[0]; dir.Tiled = true; break;
                        case 323: dir.BlockHeight = (int)(await Longs(entries, valueAt, type, n, ct))[0]; break;
                        case 273: case 324: dir.BlockOffsets = await Longs(entries, valueAt, type, n, ct); break;
                        case 279: case 325: dir.BlockByteCounts = await Longs(entries, valueAt, type, n, ct); break;
                        case 339: dir.SampleFormat = (int)(await Longs(entries, valueAt, type, n, ct))[0]; break;
                        case 33550: dir.PixelScale = await Doubles(entries, valueAt, n, ct); break;
                        case 33922: dir.TiePoint = await Doubles(entries, valueAt, n, ct); break;
                        case 34735: dir.GeoKeys = await Shorts(entries, valueAt, n, ct); break;
                        case 42113: dir.NoData = await Ascii(entries, valueAt, n, ct); break;
                    }
                }
                if (!dir.Tiled) { dir.BlockWidth = dir.Width; dir.BlockHeight = (int)(rowsPerStrip > 0 ? Math.Min(rowsPerStrip, dir.Height) : dir.Height); }
                list.Add(dir);
                int nextAt = (int)(count * entrySize);
                offset = _big ? (long)U64(entries, nextAt) : U32(entries, nextAt);
            }
            // Overviews inherit georeferencing from the full-resolution image, scaled by size.
            var full = list[0];
            foreach (var d in list)
            {
                if (d.PixelScale != null || full.PixelScale == null) continue;
                double sx = (double)full.Width / d.Width, sy = (double)full.Height / d.Height;
                d.PixelScale = new[] { full.PixelScale[0] * sx, full.PixelScale[1] * sy, 0 };
                d.TiePoint = full.TiePoint;
                d.GeoKeys = full.GeoKeys;
                d.NoData ??= full.NoData;
            }
            Directories = list;
        }

        /// <summary>Read a pixel window of a directory as float32 (NaN where the file has no data).</summary>
        public async Task<float[]> ReadWindowAsync(int dirIndex, int x0, int y0, int width, int height, CancellationToken ct = default, int parallelism = 8)
        {
            var dir = Directories[dirIndex];
            if (width <= 0 || height <= 0 || x0 < 0 || y0 < 0 || x0 + width > dir.Width || y0 + height > dir.Height)
                throw new ArgumentOutOfRangeException(nameof(x0), $"window ({x0}, {y0}, {width}×{height}) is outside the {dir.Width}×{dir.Height} image");
            var result = new float[width * height];
            for (int i = 0; i < result.Length; i++) result[i] = float.NaN;
            float? noData = dir.NoData != null && float.TryParse(dir.NoData, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float nd) ? nd : (float?)null;

            int bx0 = Math.Max(0, x0 / dir.BlockWidth), by0 = Math.Max(0, y0 / dir.BlockHeight);
            int bx1 = Math.Min(dir.BlocksAcross - 1, (x0 + width - 1) / dir.BlockWidth);
            int by1 = Math.Min(dir.BlocksDown - 1, (y0 + height - 1) / dir.BlockHeight);

            var blocks = new List<int>();
            for (int by = by0; by <= by1; by++)
                for (int bx = bx0; bx <= bx1; bx++) blocks.Add(by * dir.BlocksAcross + bx);

            // Coalesce blocks whose bytes sit close together into single range requests.
            var groups = GroupByteRanges(dir, blocks, 256 * 1024);
            using (var gate = new SemaphoreSlim(parallelism))
            {
                var tasks = new List<Task>();
                foreach (var group in groups)
                {
                    await gate.WaitAsync(ct).ConfigureAwait(false);
                    tasks.Add(Task.Run(async () =>
                    {
                        try
                        {
                            long start = group.Start;
                            byte[] span = await _source.ReadAsync(start, (int)(group.End - start), ct).ConfigureAwait(false);
                            foreach (int b in group.Blocks)
                            {
                                int count = (int)dir.BlockByteCounts[b];
                                var raw = new byte[count];
                                Buffer.BlockCopy(span, (int)(dir.BlockOffsets[b] - start), raw, 0, count);
                                float[] values = DecodeBlock(dir, raw, b);
                                CopyBlock(dir, b, values, result, x0, y0, width, height, noData);
                            }
                        }
                        finally { gate.Release(); }
                    }, ct));
                }
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            return result;
        }

        sealed class ByteGroup { public long Start, End; public List<int> Blocks = new List<int>(); }

        static List<ByteGroup> GroupByteRanges(TiffDirectory dir, List<int> blocks, long maxGap)
        {
            var sorted = new List<int>(blocks);
            sorted.RemoveAll(b => dir.BlockByteCounts[b] == 0);
            sorted.Sort((a, b) => dir.BlockOffsets[a].CompareTo(dir.BlockOffsets[b]));
            var groups = new List<ByteGroup>();
            ByteGroup? current = null;
            foreach (int b in sorted)
            {
                long s = dir.BlockOffsets[b], e = s + dir.BlockByteCounts[b];
                if (current != null && s - current.End <= maxGap && e - current.Start <= 32L * 1024 * 1024)
                {
                    current.End = Math.Max(current.End, e);
                    current.Blocks.Add(b);
                }
                else
                {
                    current = new ByteGroup { Start = s, End = e };
                    current.Blocks.Add(b);
                    groups.Add(current);
                }
            }
            return groups;
        }

        static float[] DecodeBlock(TiffDirectory dir, byte[] raw, int block)
        {
            int rows = dir.BlockHeight;
            if (!dir.Tiled)
            {
                int stripTop = block / dir.BlocksAcross * dir.BlockHeight;
                rows = Math.Min(dir.BlockHeight, dir.Height - stripTop);
            }
            int bytesPerSample = dir.BitsPerSample / 8;
            int expected = dir.BlockWidth * rows * bytesPerSample;
            byte[] data;
            switch (dir.Compression)
            {
                case 1: data = raw; break;
                case 5: data = Lzw.Decode(raw, expected); break;
                case 8: case 32946: data = Deflate.Decode(raw, expected); break;
                default: throw new NotSupportedException($"TIFF compression {dir.Compression}");
            }

            if (dir.BitsPerSample == 32 && dir.SampleFormat == 3)
            {
                if (dir.Predictor == 3) return Predictors.UndoFloatingPoint32(data, dir.BlockWidth, rows);
                if (dir.Predictor != 1) throw new NotSupportedException($"Predictor {dir.Predictor} for float32");
                var f = new float[dir.BlockWidth * rows];
                Buffer.BlockCopy(data, 0, f, 0, f.Length * 4);
                return f;
            }
            if (dir.BitsPerSample == 8)
            {
                if (dir.Predictor == 2) Predictors.UndoHorizontal8(data, dir.BlockWidth, rows);
                var f = new float[dir.BlockWidth * rows];
                for (int i = 0; i < f.Length; i++) f[i] = data[i];
                return f;
            }
            throw new NotSupportedException($"{dir.BitsPerSample}-bit samples, format {dir.SampleFormat}");
        }

        static void CopyBlock(TiffDirectory dir, int block, float[] values, float[] result, int x0, int y0, int width, int height, float? noData)
        {
            int bx = block % dir.BlocksAcross, by = block / dir.BlocksAcross;
            int left = bx * dir.BlockWidth, top = by * dir.BlockHeight;
            int rows = values.Length / dir.BlockWidth;
            for (int r = 0; r < rows; r++)
            {
                int y = top + r - y0;
                if (y < 0 || y >= height) continue;
                for (int c = 0; c < dir.BlockWidth; c++)
                {
                    int x = left + c - x0;
                    if (x < 0 || x >= width || left + c >= dir.Width) continue;
                    float v = values[r * dir.BlockWidth + c];
                    result[y * width + x] = noData.HasValue && v == noData.Value ? float.NaN : v;
                }
            }
        }

        // ---- low-level value access -------------------------------------------------------------

        async Task<byte[]> Bytes(long offset, int length, CancellationToken ct)
        {
            if (offset + length <= _prefix.Length)
            {
                var b = new byte[length];
                Buffer.BlockCopy(_prefix, (int)offset, b, 0, length);
                return b;
            }
            return await _source.ReadAsync(offset, length, ct).ConfigureAwait(false);
        }

        async Task<byte[]> ValueBytes(byte[] entries, int valueAt, int byteLength, CancellationToken ct)
        {
            int inline = _big ? 8 : 4;
            if (byteLength <= inline)
            {
                var b = new byte[byteLength];
                Buffer.BlockCopy(entries, valueAt, b, 0, byteLength);
                return b;
            }
            long pointer = _big ? (long)U64(entries, valueAt) : U32(entries, valueAt);
            return await Bytes(pointer, byteLength, ct).ConfigureAwait(false);
        }

        async Task<long[]> Longs(byte[] entries, int valueAt, int type, long n, CancellationToken ct)
        {
            int size = type == 3 ? 2 : type == 4 ? 4 : type == 16 ? 8 : type == 1 ? 1 : throw new InvalidDataException($"Integer tag type {type}");
            byte[] b = await ValueBytes(entries, valueAt, (int)(n * size), ct).ConfigureAwait(false);
            var r = new long[n];
            for (int i = 0; i < n; i++)
                r[i] = size == 1 ? b[i] : size == 2 ? U16(b, i * 2) : size == 4 ? U32(b, i * 4) : (long)U64(b, i * 8);
            return r;
        }

        async Task<ushort[]> Shorts(byte[] entries, int valueAt, long n, CancellationToken ct)
        {
            byte[] b = await ValueBytes(entries, valueAt, (int)(n * 2), ct).ConfigureAwait(false);
            var r = new ushort[n];
            for (int i = 0; i < n; i++) r[i] = (ushort)U16(b, i * 2);
            return r;
        }

        async Task<double[]> Doubles(byte[] entries, int valueAt, long n, CancellationToken ct)
        {
            byte[] b = await ValueBytes(entries, valueAt, (int)(n * 8), ct).ConfigureAwait(false);
            var r = new double[n];
            for (int i = 0; i < n; i++) r[i] = BitConverter.Int64BitsToDouble((long)U64(b, i * 8));
            return r;
        }

        async Task<string> Ascii(byte[] entries, int valueAt, long n, CancellationToken ct)
        {
            byte[] b = await ValueBytes(entries, valueAt, (int)n, ct).ConfigureAwait(false);
            return Encoding.ASCII.GetString(b).TrimEnd('\0', ' ');
        }

        int U16(byte[] b, int i) => _little ? b[i] | b[i + 1] << 8 : b[i] << 8 | b[i + 1];
        long U32(byte[] b, int i) => _little
            ? (long)(uint)(b[i] | b[i + 1] << 8 | b[i + 2] << 16 | b[i + 3] << 24)
            : (long)(uint)(b[i] << 24 | b[i + 1] << 16 | b[i + 2] << 8 | b[i + 3]);
        ulong U64(byte[] b, int i)
        {
            ulong lo = (ulong)U32(b, i), hi = (ulong)U32(b, i + 4);
            return _little ? hi << 32 | lo : lo << 32 | hi;
        }
    }
}
