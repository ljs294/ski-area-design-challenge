using System;
using System.IO;
using System.Linq;
using MountainPlanner.Domain.Edits;

namespace MountainPlanner.Persistence
{
    /// <summary>
    /// The package's float32 heights for the open game (SH-02, task P3-05): the same function as the cache build's
    /// <see cref="TerrainCache.HeightField"/>, giving identical doubles, but it reads the grid files a 256-row chunk at a
    /// time as edits and readouts need them, instead of holding about 230 MB for a 5 km site. A chunk read is cut into
    /// blocks of 256 by 256 cells and the most recently used blocks are kept per layer (finding D2, owner 2026-10-23: whole
    /// 256-row bands, six kept, made a long lift line that crosses more bands than that read every one again on each pass
    /// along it, a 7,900 ft line's build taking 86 s). The layers were checked against their hashes when the cache was
    /// built from them. Thread-safe.
    /// </summary>
    public sealed class PackageHeights : IHeightSource, IHeightRows, IDisposable
    {
        /// <summary>Cells along a block's side.</summary>
        public const int BlockCells = GridFile.ChunkRows;

        /// <summary>Blocks kept per layer: 256 KB each, 40 MB a layer (six whole bands were 30 MB on a 5 km site).</summary>
        public const int KeptBlocks = 160;

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

        /// <summary>The blocks a bilinear row reads lately (its row and the next, across a block edge), kept for the row.</summary>
        sealed class ChunkCache
        {
            public int B0 = -1, B1 = -1, B2 = -1, B3 = -1;
            public float[] A0, A1, A2, A3;
        }

        readonly struct CachedGrid : TerrainCache.IFloatGrid
        {
            readonly Layer _layer;
            readonly ChunkCache _cache;
            public CachedGrid(Layer layer, ChunkCache cache) { _layer = layer; _cache = cache; }
            public GridHeader Header => _layer.Header;

            public float Value(int column, int row)
            {
                int b = _layer.BlockOf(column, row);
                float[] block;
                if (b == _cache.B0) block = _cache.A0;
                else if (b == _cache.B1) block = _cache.A1;
                else if (b == _cache.B2) block = _cache.A2;
                else if (b == _cache.B3) block = _cache.A3;
                else
                {
                    block = _layer.Block(b);
                    _cache.B3 = _cache.B2; _cache.A3 = _cache.A2;
                    _cache.B2 = _cache.B1; _cache.A2 = _cache.A1;
                    _cache.B1 = _cache.B0; _cache.A1 = _cache.A0;
                    _cache.B0 = b; _cache.A0 = block;
                }
                return block[_layer.InBlock(column, row)];
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
            public float Value(int column, int row) => _layer.Block(_layer.BlockOf(column, row))[_layer.InBlock(column, row)];
        }

        sealed class Layer : IDisposable
        {
            public readonly GridHeader Header;
            readonly FileStream _file;
            readonly long[] _offsets;
            readonly int[] _lengths;
            readonly int _across;   // blocks across a band
            readonly float[][] _blocks;
            readonly long[] _used;
            long _tick;
            int _held;
            public int Reads;

            public Layer(string folder, PackageManifest package, string id)
            {
                var layer = package.Layers.FirstOrDefault(l => l.Id == id) ?? throw new InvalidDataException($"No layer '{id}'.");
                _file = new FileStream(Path.Combine(folder, layer.File), FileMode.Open, FileAccess.Read, FileShare.Read);
                Header = GridFile.ReadChunkIndex(_file, out _offsets, out _lengths);
                _across = (Header.Width + BlockCells - 1) / BlockCells;
                _blocks = new float[_offsets.Length * _across][];
                _used = new long[_blocks.Length];
            }

            public int BlockOf(int column, int row) => row / BlockCells * _across + column / BlockCells;

            /// <summary>A cell's index in its block (a block's rows are as wide as the block, the last column's narrower).</summary>
            public int InBlock(int column, int row)
            {
                int c0 = column / BlockCells * BlockCells;
                return (row % BlockCells) * Math.Min(BlockCells, Header.Width - c0) + column - c0;
            }

            public float[] Block(int b)
            {
                var block = System.Threading.Volatile.Read(ref _blocks[b]);
                if (block != null)
                {
                    // Marked used at the current tick, without the interlocked increment: every read paid one, and the
                    // edit pipeline's parallel workers fought over it (task 08). Ticks move on each load, which is all the
                    // keep-the-recent-ones rule needs.
                    _used[b] = System.Threading.Volatile.Read(ref _tick);
                    return block;
                }
                lock (this)
                {
                    block = _blocks[b];
                    if (block == null)
                    {
                        // The band read and cut into its blocks: the one asked for is the most recent, the rest the oldest
                        // (the first to go unless they're used), so a line along the band finds its neighbours in.
                        int c = b / _across;
                        var chunk = GridFile.ReadFloatChunk(_file, Header, _offsets[c], _lengths[c], c);
                        Reads++;
                        int rows = chunk.Length / Header.Width;
                        for (int j = 0; j < _across; j++)
                        {
                            int k = c * _across + j;
                            if (_blocks[k] != null) continue;
                            int c0 = j * BlockCells, w = Math.Min(BlockCells, Header.Width - c0);
                            var cut = new float[rows * w];
                            for (int r = 0; r < rows; r++) Array.Copy(chunk, r * Header.Width + c0, cut, r * w, w);
                            _used[k] = 0;
                            System.Threading.Volatile.Write(ref _blocks[k], cut);
                            _held++;
                        }
                        block = _blocks[b];
                        // Keep the most recently used; a reader still holding an evicted block keeps its own reference.
                        while (_held > KeptBlocks)
                        {
                            int oldest = -1;
                            for (int k = 0; k < _blocks.Length; k++)
                                if (_blocks[k] != null && k != b && (oldest < 0 || _used[k] < _used[oldest])) oldest = k;
                            if (oldest < 0) break;
                            System.Threading.Volatile.Write(ref _blocks[oldest], null);
                            _held--;
                        }
                    }
                    _used[b] = System.Threading.Interlocked.Increment(ref _tick);
                    return block;
                }
            }

            public void Dispose() => _file.Dispose();
        }
    }
}
