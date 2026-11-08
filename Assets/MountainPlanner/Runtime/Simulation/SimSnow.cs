using System;
using System.Threading;

namespace MountainPlanner.Simulation
{
    /// <summary>
    /// SI-17: the snow depths over the 8 m field, owned by the worker, in whole millimetres. Until 4.07 the presentation's
    /// snowpack (the natural snow of the ground) arrives whole or as a changed rectangle in a <see cref="SimSnowpack"/>
    /// command; nothing else writes it. The field is cut into <see cref="TileCells"/>-square tiles: each keeps its own hash
    /// (so the state hash costs a few hundred words, not a million cells) and a staging mark for the
    /// <see cref="TileStagingRing"/>, which is how the depths reach the screen. The staging marks are the presentation's
    /// bookkeeping: they're never in the hash or the save.
    /// </summary>
    public sealed class SimSnow
    {
        /// <summary>A tile's side in cells (64 × 8 m = 512 m), and so a staging slot's.</summary>
        public const int TileCells = 64;

        public int Width { get; private set; }
        public int Height { get; private set; }
        /// <summary>False until the first snowpack: the field has never been given depths (the screen keeps its own).</summary>
        public bool Set { get; private set; }
        public int TilesX { get; private set; }
        public int TilesY { get; private set; }

        ushort[] _mm = Array.Empty<ushort>();
        ulong[] _tileHash = Array.Empty<ulong>();
        // ---- staging (not state) ----
        bool[] _stageDue = Array.Empty<bool>();
        long[] _stagedAt = Array.Empty<long>();
        int _stageCursor, _stageDueCount;

        /// <summary>The depth at a cell, mm (tests, the sidecar).</summary>
        public ushort this[int x, int y] => _mm[y * Width + x];

        internal ushort[] Cells => _mm;

        /// <summary>Makes the field <paramref name="width"/> × <paramref name="height"/> cells, bare and never set.</summary>
        internal void Resize(int width, int height)
        {
            if (width < 0 || height < 0 || (long)width * height > 64_000_000) throw new ArgumentOutOfRangeException(nameof(width));
            Width = width; Height = height;
            _mm = new ushort[width * height];
            TilesX = (width + TileCells - 1) / TileCells;
            TilesY = (height + TileCells - 1) / TileCells;
            int tiles = TilesX * TilesY;
            _tileHash = new ulong[tiles];
            _stageDue = new bool[tiles];
            _stagedAt = new long[tiles];
            for (int i = 0; i < tiles; i++) _stagedAt[i] = long.MinValue / 2;
            _stageCursor = 0; _stageDueCount = 0;
            Set = false;
            for (int t = 0; t < tiles; t++) _tileHash[t] = TileHash(t);
        }

        /// <summary>Applies a snowpack: the field's size first (a new size starts bare), then its rectangle of depths.</summary>
        internal bool Apply(SimSnowpack p)
        {
            if (p.FieldWidth != Width || p.FieldHeight != Height) Resize(p.FieldWidth, p.FieldHeight);
            if (p.X < 0 || p.Y < 0 || p.X + p.Width > Width || p.Y + p.Height > Height) return false;
            for (int j = 0; j < p.Height; j++)
                Array.Copy(p.Millimetres, j * p.Width, _mm, (p.Y + j) * Width + p.X, p.Width);
            Set = true;
            if (p.Width == 0 || p.Height == 0) return true;
            int tx0 = p.X / TileCells, ty0 = p.Y / TileCells, tx1 = (p.X + p.Width - 1) / TileCells, ty1 = (p.Y + p.Height - 1) / TileCells;
            for (int ty = ty0; ty <= ty1; ty++)
                for (int tx = tx0; tx <= tx1; tx++)
                {
                    int t = ty * TilesX + tx;
                    _tileHash[t] = TileHash(t);
                    MarkDue(t);
                }
            return true;
        }

        /// <summary>Every tile to the screen again (a restored or new world: the reader starts from nothing).</summary>
        internal void StageAll()
        {
            if (!Set) return;
            for (int t = 0; t < _stageDue.Length; t++) MarkDue(t);
        }

        void MarkDue(int t)
        {
            if (_stageDue[t]) return;
            _stageDue[t] = true;
            _stageDueCount++;
        }

        public int TilesDue => _stageDueCount;

        ulong TileHash(int t)
        {
            var h = new StateHasher();
            int x0 = t % TilesX * TileCells, y0 = t / TilesX * TileCells;
            int x1 = Math.Min(Width, x0 + TileCells), y1 = Math.Min(Height, y0 + TileCells);
            for (int y = y0; y < y1; y++)
            {
                int row = y * Width;
                // Four cells a word: the hash costs a quarter of the cells.
                int x = x0;
                for (; x + 4 <= x1; x += 4)
                    h.Add((ulong)_mm[row + x] | (ulong)_mm[row + x + 1] << 16 | (ulong)_mm[row + x + 2] << 32 | (ulong)_mm[row + x + 3] << 48);
                for (; x < x1; x++) h.Add(_mm[row + x]);
            }
            return h.Value;
        }

        /// <summary>The field's part of the state hash (SI-08): its size, whether it's set and every tile's hash.</summary>
        internal void AddTo(ref StateHasher h)
        {
            h.Add(Set ? 1 : 0); h.Add(Width); h.Add(Height);
            for (int t = 0; t < _tileHash.Length; t++) h.Add(_tileHash[t]);
        }

        /// <summary>
        /// SI-17: copies up to <paramref name="maxTiles"/> due tiles into the ring, round-robin; a tile staged in the last
        /// <paramref name="flushTicks"/> ticks waits unless <paramref name="anyAge"/> (paused: no ticks pass). Stops when the
        /// ring is full (the reader hasn't caught up); the tiles stay due. Returns the tiles staged.
        /// </summary>
        internal int Stage(TileStagingRing ring, long tick, int maxTiles, int flushTicks, bool anyAge)
        {
            if (ring == null || _stageDueCount == 0) return 0;
            int tiles = _stageDue.Length, staged = 0;
            for (int n = 0; n < tiles && staged < maxTiles && _stageDueCount > 0; n++)
            {
                int t = _stageCursor;
                _stageCursor = (_stageCursor + 1) % tiles;
                if (!_stageDue[t]) continue;
                if (!anyAge && tick - _stagedAt[t] < flushTicks) continue;
                int x0 = t % TilesX * TileCells, y0 = t / TilesX * TileCells;
                int w = Math.Min(TileCells, Width - x0), hgt = Math.Min(TileCells, Height - y0);
                if (!ring.TryWrite(x0, y0, w, hgt, _mm, Width)) break;
                _stageDue[t] = false;
                _stageDueCount--;
                _stagedAt[t] = tick;
                staged++;
            }
            return staged;
        }

        /// <summary>The sidecar's reader: the whole field at once, then every tile's hash and every tile due.</summary>
        internal void Load(int width, int height, bool set, ushort[] mm)
        {
            Resize(width, height);
            Array.Copy(mm, _mm, _mm.Length);
            Set = set;
            for (int t = 0; t < _tileHash.Length; t++) _tileHash[t] = TileHash(t);
            StageAll();
        }
    }

    /// <summary>
    /// SI-17: a rectangle of the presentation's snowpack for the worker, in whole mm (the command's payload). The
    /// presentation builds it off the main thread's hot path and never touches it after sending.
    /// </summary>
    public sealed class SimSnowpack
    {
        public readonly int FieldWidth, FieldHeight, X, Y, Width, Height;
        /// <summary><see cref="Width"/> × <see cref="Height"/> depths, mm, west to east then south to north.</summary>
        public readonly ushort[] Millimetres;

        public SimSnowpack(int fieldWidth, int fieldHeight, int x, int y, int width, int height, ushort[] millimetres)
        {
            if (fieldWidth < 0 || fieldHeight < 0 || width < 0 || height < 0) throw new ArgumentOutOfRangeException(nameof(width));
            if (millimetres == null || millimetres.Length != width * height) throw new ArgumentException("One depth per cell.", nameof(millimetres));
            FieldWidth = fieldWidth; FieldHeight = fieldHeight; X = x; Y = y; Width = width; Height = height;
            Millimetres = millimetres;
        }

        /// <summary>A rectangle of a field of depths in metres (the snowpack's own form), rounded to whole mm.</summary>
        public static SimSnowpack FromMetres(float[] metres, int fieldWidth, int fieldHeight, int x, int y, int width, int height)
        {
            if (metres == null || metres.Length != fieldWidth * fieldHeight) throw new ArgumentException("One depth per cell.", nameof(metres));
            var mm = new ushort[width * height];
            for (int j = 0; j < height; j++)
                for (int i = 0; i < width; i++)
                    mm[j * width + i] = ToMillimetres(metres[(y + j) * fieldWidth + x + i]);
            return new SimSnowpack(fieldWidth, fieldHeight, x, y, width, height, mm);
        }

        /// <summary>Metres to whole mm, clamped to what a cell holds (0 to 65.535 m).</summary>
        public static ushort ToMillimetres(float metres)
        {
            if (!(metres > 0)) return 0;
            double mm = Math.Round(metres * 1000.0, MidpointRounding.AwayFromZero);
            return mm >= ushort.MaxValue ? ushort.MaxValue : (ushort)mm;
        }
    }

    /// <summary>
    /// SI-17, BG-11: the presentation's staging ring. The worker copies a due tile's depths into the next free slot; the
    /// snapshot says how many it has written (<see cref="SimSnapshot.StagedTiles"/>), and the main thread copies each slot
    /// up to there into its texture and hands the slots back (<see cref="Release"/>). The worker never writes a slot the
    /// reader hasn't released, and the reader never reads past the snapshot's count, so neither waits on the other.
    /// Allocated once: 64 slots of 64 × 64 depths (8 KB each).
    /// </summary>
    public sealed class TileStagingRing
    {
        public const int DefaultSlots = 64;
        const int SlotCells = SimSnow.TileCells * SimSnow.TileCells;

        readonly ushort[] _cells;
        readonly int[] _x, _y, _w, _h;
        public readonly int Slots;
        long _written;      // the worker's
        long _released;     // the reader's

        public TileStagingRing(int slots = DefaultSlots)
        {
            if (slots < 1) throw new ArgumentOutOfRangeException(nameof(slots));
            Slots = slots;
            _cells = new ushort[slots * SlotCells];
            _x = new int[slots]; _y = new int[slots]; _w = new int[slots]; _h = new int[slots];
        }

        /// <summary>Slots written so far (the worker's count; the snapshot carries it).</summary>
        public long Written => Volatile.Read(ref _written);

        /// <summary>Slots the reader has handed back.</summary>
        public long Released => Volatile.Read(ref _released);

        /// <summary>The worker: copies a tile of a field into the next slot, or false when every slot is still the reader's.</summary>
        internal bool TryWrite(int x, int y, int width, int height, ushort[] field, int fieldWidth)
        {
            long n = _written;
            if (n - Volatile.Read(ref _released) >= Slots) return false;
            int s = (int)(n % Slots);
            int at = s * SlotCells;
            for (int j = 0; j < height; j++) Array.Copy(field, (y + j) * fieldWidth + x, _cells, at + j * width, width);
            _x[s] = x; _y[s] = y; _w[s] = width; _h[s] = height;
            Volatile.Write(ref _written, n + 1);
            return true;
        }

        /// <summary>The reader: the slot holding write number <paramref name="n"/> (from <see cref="Released"/> up to a snapshot's count).</summary>
        public void Read(long n, out int x, out int y, out int width, out int height, out int offset, out ushort[] cells)
        {
            int s = (int)(n % Slots);
            x = _x[s]; y = _y[s]; width = _w[s]; height = _h[s];
            offset = s * SlotCells;
            cells = _cells;
        }

        /// <summary>The reader hands back every slot before write number <paramref name="through"/>.</summary>
        public void Release(long through)
        {
            if (through > Volatile.Read(ref _written)) throw new ArgumentOutOfRangeException(nameof(through), "That slot isn't written yet.");
            if (through > _released) Volatile.Write(ref _released, through);
        }
    }
}
