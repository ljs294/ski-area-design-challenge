using System;
using System.IO;
using System.Linq;
using MountainPlanner.Domain.Edits;

namespace MountainPlanner.Persistence
{
    /// <summary>
    /// The package's float32 heights for the open game (SH-02, task P3-05): the same function as the cache build's
    /// <see cref="TerrainCache.HeightField"/>, giving identical doubles, but it reads the grid files a 256-row chunk at a
    /// time as edits and readouts need them, instead of holding about 230 MB for a 5 km site. A few recent chunks are
    /// kept per layer. The layers were checked against their hashes when the cache was built from them. Thread-safe.
    /// </summary>
    public sealed class PackageHeights : IHeightSource, IHeightRows, IDisposable
    {
        /// <summary>Chunks kept per layer: about 5 MB each on a 5 km site.</summary>
        public const int KeptChunks = 6;

        readonly Layer _core, _ring;

        public PackageHeights(string packageFolder, PackageManifest package)
        {
            _core = new Layer(packageFolder, package, "heights-core");
            _ring = new Layer(packageFolder, package, "heights-ring");
        }

        public GridHeaderView Core => new GridHeaderView(_core.Header);

        public double At(double x, double y) => TerrainCache.Blend(new ChunkGrid(_core), new ChunkGrid(_ring), x, y);

        /// <summary>
        /// A row of lattice points (task 08's grader): the same <see cref="TerrainCache.Blend"/> as <see cref="At"/>, so the
        /// same doubles, with each layer's chunks looked up once for the row rather than eight times a point.
        /// </summary>
        public void AtRow(double y, double west, int column0, int count, double[] into, int offset)
        {
            var core = new CachedGrid(_core, new ChunkCache());
            var ring = new CachedGrid(_ring, new ChunkCache());
            for (int i = 0; i < count; i++) into[offset + i] = TerrainCache.Blend(core, ring, west + (column0 + i), y);
        }

        /// <summary>The two chunks a bilinear row reads (its row and the next), kept for the row.</summary>
        sealed class ChunkCache
        {
            public int C0 = -1, C1 = -1;
            public float[] A0, A1;
        }

        readonly struct CachedGrid : TerrainCache.IFloatGrid
        {
            readonly Layer _layer;
            readonly ChunkCache _cache;
            public CachedGrid(Layer layer, ChunkCache cache) { _layer = layer; _cache = cache; }
            public GridHeader Header => _layer.Header;

            public float Value(int column, int row)
            {
                int c = row / GridFile.ChunkRows;
                float[] chunk;
                if (c == _cache.C0) chunk = _cache.A0;
                else if (c == _cache.C1) chunk = _cache.A1;
                else
                {
                    chunk = _layer.Chunk(c);
                    _cache.C1 = _cache.C0;
                    _cache.A1 = _cache.A0;
                    _cache.C0 = c;
                    _cache.A0 = chunk;
                }
                return chunk[(row % GridFile.ChunkRows) * _layer.Header.Width + column];
            }
        }

        /// <summary>Chunks read from disk so far (for the timing report).</summary>
        public int ChunksRead => _core.Reads + _ring.Reads;

        public void Dispose()
        {
            _core.Dispose();
            _ring.Dispose();
        }

        /// <summary>A grid's position and size, without exposing its values.</summary>
        public readonly struct GridHeaderView
        {
            public readonly double West, North, CellSize;
            public readonly int Width, Height;
            internal GridHeaderView(GridHeader h)
            {
                West = h.West;
                North = h.North;
                CellSize = h.CellSize;
                Width = h.Width;
                Height = h.Height;
            }
        }

        readonly struct ChunkGrid : TerrainCache.IFloatGrid
        {
            readonly Layer _layer;
            public ChunkGrid(Layer layer) => _layer = layer;
            public GridHeader Header => _layer.Header;
            public float Value(int column, int row) => _layer.Chunk(row / GridFile.ChunkRows)[(row % GridFile.ChunkRows) * _layer.Header.Width + column];
        }

        sealed class Layer : IDisposable
        {
            public readonly GridHeader Header;
            readonly FileStream _file;
            readonly long[] _offsets;
            readonly int[] _lengths;
            readonly float[][] _chunks;
            readonly long[] _used;
            long _tick;
            public int Reads;

            public Layer(string folder, PackageManifest package, string id)
            {
                var layer = package.Layers.FirstOrDefault(l => l.Id == id) ?? throw new InvalidDataException($"No layer '{id}'.");
                _file = new FileStream(Path.Combine(folder, layer.File), FileMode.Open, FileAccess.Read, FileShare.Read);
                Header = GridFile.ReadChunkIndex(_file, out _offsets, out _lengths);
                _chunks = new float[_offsets.Length][];
                _used = new long[_offsets.Length];
            }

            public float[] Chunk(int c)
            {
                var chunk = System.Threading.Volatile.Read(ref _chunks[c]);
                if (chunk != null)
                {
                    _used[c] = System.Threading.Interlocked.Increment(ref _tick);
                    return chunk;
                }
                lock (this)
                {
                    chunk = _chunks[c];
                    if (chunk == null)
                    {
                        chunk = GridFile.ReadFloatChunk(_file, Header, _offsets[c], _lengths[c], c);
                        Reads++;
                        // Keep the most recently used few; a reader still holding an evicted chunk keeps its own reference.
                        int held = 0, oldest = -1;
                        for (int k = 0; k < _chunks.Length; k++)
                            if (_chunks[k] != null)
                            {
                                held++;
                                if (oldest < 0 || _used[k] < _used[oldest]) oldest = k;
                            }
                        if (held >= KeptChunks && oldest >= 0) System.Threading.Volatile.Write(ref _chunks[oldest], null);
                        System.Threading.Volatile.Write(ref _chunks[c], chunk);
                    }
                    _used[c] = System.Threading.Interlocked.Increment(ref _tick);
                    return chunk;
                }
            }

            public void Dispose() => _file.Dispose();
        }
    }
}
