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
    ///
    /// Task 4.07 PR A2: once the snow's terrain inputs arrive (command 32) the field is **live**: the snow itself
    /// (<see cref="SnowField"/>) is the state, the depths become one of the visible planes the screen reads (with the type,
    /// quality, age and density), and the tile hashes cover the whole pack. An hour's job writes the next planes and the
    /// next tile hashes while the worker keeps reading the landed ones; <see cref="Land"/> swaps them. A world that never
    /// gets the inputs keeps the static depths, hashed exactly as before.
    /// </summary>
    public sealed class SimSnow
    {
        /// <summary>A tile's side in cells (64 × 8 m = 512 m), and so a staging slot's.</summary>
        public const int TileCells = 64;
        public const int CellMetres = Domain.Snow.SurfaceGrid.CellMetres;

        public int Width { get; private set; }
        public int Height { get; private set; }
        /// <summary>False until the first snowpack: the field has never been given depths (the screen keeps its own).</summary>
        public bool Set { get; private set; }
        public int TilesX { get; private set; }
        public int TilesY { get; private set; }

        ushort[] _mm = Array.Empty<ushort>();
        ulong[] _tileHash = Array.Empty<ulong>();
        // ---- live (PR A2): the snow, the planes and hashes an hour's job writes, and what it found per tile ----
        internal SnowField Field;
        internal SnowRules Rules;
        internal ushort[] MmNext = Array.Empty<ushort>();
        internal byte[] VType = Array.Empty<byte>(), VQuality = Array.Empty<byte>(), VAge = Array.Empty<byte>(), VDensity = Array.Empty<byte>();
        internal byte[] VTypeNext = Array.Empty<byte>(), VQualityNext = Array.Empty<byte>(), VAgeNext = Array.Empty<byte>(), VDensityNext = Array.Empty<byte>();
        internal ulong[] TileHashNext = Array.Empty<ulong>();
        /// <summary>Per tile: some cell holds snow (the job skips a bare tile in a dry hour), and the job changed its planes.</summary>
        internal bool[] TileSnowy = Array.Empty<bool>(), TileChanged = Array.Empty<bool>();
        // ---- staging (not state) ----
        bool[] _stageDue = Array.Empty<bool>();
        long[] _stagedAt = Array.Empty<long>();
        int _stageCursor, _stageDueCount;

        /// <summary>The depth at a cell, mm (tests, the sidecar); live, the landed hour's.</summary>
        public ushort this[int x, int y] => _mm[y * Width + x];

        internal ushort[] Cells => _mm;
        internal ulong[] TileHashes => _tileHash;

        /// <summary>Whether the snow is simulated (PR A2), not a static picture.</summary>
        public bool Live => Field != null;

        /// <summary>The landed hour's type (SN-13), quality (‰), age (face hours) and density (kg/m³, to 4) at a cell.</summary>
        public SnowType TypeAt(int x, int y) => Live ? (SnowType)VType[y * Width + x] : SnowType.None;
        public int QualityAt(int x, int y) => Live ? VQuality[y * Width + x] * 4 : 0;
        public int AgeAt(int x, int y) => Live ? VAge[y * Width + x] : 0;
        public int DensityAt(int x, int y) => Live ? VDensity[y * Width + x] * 4 : 0;
        /// <summary>The live pack at a cell (tests, the readout's water); between hours only.</summary>
        public Domain.Weather.SnowPack PackAt(int x, int y) => Live ? Field.Pack[y * Width + x] : default;

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
            Field = null;
            for (int t = 0; t < tiles; t++) _tileHash[t] = TileHash(t);
        }

        // ================= live (task 4.07 PR A2) =================

        /// <summary>
        /// Makes the field live over <paramref name="width"/> × <paramref name="height"/> cells. Static depths the field already
        /// has (an older save's, or the opener's picture) become the seed (<see cref="SnowField.Seed"/>); a field never set
        /// starts bare. Returns the water seeded (µm, for the ledger).
        /// </summary>
        internal long MakeLive(int width, int height, in SnowRules rules)
        {
            bool keep = Set && Width == width && Height == height;
            ushort[] old = keep ? _mm : null;
            if (!keep) Resize(width, height);
            int n = width * height, tiles = TilesX * TilesY;
            Rules = rules;
            Field = new SnowField(width, height);
            _mm = new ushort[n];
            MmNext = new ushort[n];
            VType = new byte[n]; VQuality = new byte[n]; VAge = new byte[n]; VDensity = new byte[n];
            VTypeNext = new byte[n]; VQualityNext = new byte[n]; VAgeNext = new byte[n]; VDensityNext = new byte[n];
            TileHashNext = new ulong[tiles];
            TileSnowy = new bool[tiles]; TileChanged = new bool[tiles];
            long seeded = 0;
            if (old != null) for (int c = 0; c < n; c++) seeded += Field.Seed(c, old[c], rules.SeedDensity);
            Set = true;
            for (int t = 0; t < tiles; t++) Refresh(t);
            StageAll();
            return seeded;
        }

        /// <summary>Live: seeds a rectangle from a snowpack (<c>SetSnowpack</c>), refreshing its tiles. Returns the water's change
        /// (µm), or <see cref="long.MinValue"/> when the rectangle doesn't fit the field.</summary>
        internal long Seed(SimSnowpack p)
        {
            if (p.FieldWidth != Width || p.FieldHeight != Height || p.X < 0 || p.Y < 0 || p.X + p.Width > Width || p.Y + p.Height > Height) return long.MinValue;
            long change = 0;
            for (int j = 0; j < p.Height; j++)
                for (int i = 0; i < p.Width; i++)
                    change += Field.Seed((p.Y + j) * Width + p.X + i, p.Millimetres[j * p.Width + i], Rules.SeedDensity);
            if (p.Width == 0 || p.Height == 0) return change;
            int tx0 = p.X / TileCells, ty0 = p.Y / TileCells, tx1 = (p.X + p.Width - 1) / TileCells, ty1 = (p.Y + p.Height - 1) / TileCells;
            for (int ty = ty0; ty <= ty1; ty++)
                for (int tx = tx0; tx <= tx1; tx++) { int t = ty * TilesX + tx; Refresh(t); MarkDue(t); }
            return change;
        }

        /// <summary>Task 4.08: the tiles over 8 m cells x0..x1, y0..y1 (inclusive) worked out again and sent (a paint).</summary>
        internal void Refresh(int x0, int y0, int x1, int y1)
        {
            for (int ty = y0 / TileCells; ty <= y1 / TileCells; ty++)
                for (int tx = x0 / TileCells; tx <= x1 / TileCells; tx++) { int t = ty * TilesX + tx; Refresh(t); MarkDue(t); }
        }

        /// <summary>A tile's landed planes and both hashes worked out again from the snow (a seed, a load, the switch to live).</summary>
        internal void Refresh(int t)
        {
            TileBounds(t, out int x0, out int y0, out int x1, out int y1);
            bool snowy = false;
            for (int y = y0; y < y1; y++)
                for (int x = x0; x < x1; x++)
                {
                    int c = y * Width + x;
                    WritePlanes(c, _mm, VType, VQuality, VAge, VDensity);
                    snowy |= Field.Pack[c].Swe > 0;
                }
            TileSnowy[t] = snowy;
            _tileHash[t] = LiveTileHash(t);
            TileHashNext[t] = _tileHash[t];
        }

        /// <summary>A cell's visible planes from its snow; true when any of them changed.</summary>
        internal bool WritePlanes(int c, ushort[] mm, byte[] type, byte[] quality, byte[] age, byte[] density)
        {
            ref var p = ref Field.Pack[c];
            int d = p.DepthMm;
            ushort depth = d >= ushort.MaxValue ? ushort.MaxValue : (ushort)Math.Max(0, d);
            byte ty = Field.Type[c], ag = Field.Age[c];
            byte q = (byte)(Rules.Quality(ty, ag) / 4);
            byte rho = (byte)Math.Min(255, p.DensityKg / 4);
            bool changed = mm[c] != depth || type[c] != ty || quality[c] != q || age[c] != ag || density[c] != rho;
            mm[c] = depth; type[c] = ty; quality[c] = q; age[c] = ag; density[c] = rho;
            return changed;
        }

        internal void TileBounds(int t, out int x0, out int y0, out int x1, out int y1)
        {
            x0 = t % TilesX * TileCells; y0 = t / TilesX * TileCells;
            x1 = Math.Min(Width, x0 + TileCells); y1 = Math.Min(Height, y0 + TileCells);
        }

        /// <summary>A live tile's hash over its snow (the job's own formula: <see cref="SnowField.MixCell"/>).</summary>
        internal ulong LiveTileHash(int t)
        {
            TileBounds(t, out int x0, out int y0, out int x1, out int y1);
            ulong h = SnowField.TileSeed ^ (ulong)t;
            for (int y = y0; y < y1; y++)
                for (int x = x0; x < x1; x++) h = Field.MixCell(h, y * Width + x);
            return h;
        }

        /// <summary>
        /// An hour's job has finished: its planes and tile hashes become the landed ones, and every tile it changed goes to
        /// the screen again.
        /// </summary>
        internal void Land()
        {
            Swap(ref _mm, ref MmNext); Swap(ref VType, ref VTypeNext); Swap(ref VQuality, ref VQualityNext);
            Swap(ref VAge, ref VAgeNext); Swap(ref VDensity, ref VDensityNext);
            var h = _tileHash; _tileHash = TileHashNext; TileHashNext = h;
            for (int t = 0; t < TileChanged.Length; t++)
            {
                TileHashNext[t] = _tileHash[t];
                if (TileChanged[t]) MarkDue(t);
                TileChanged[t] = false;
            }
        }

        static void Swap<T>(ref T[] a, ref T[] b) { var t = a; a = b; b = t; }

        /// <summary>A tile the job skips keeps its landed planes in the next set.</summary>
        internal void CopyPlanesToNext(int t)
        {
            TileBounds(t, out int x0, out int y0, out int x1, out int y1);
            for (int y = y0; y < y1; y++)
            {
                int at = y * Width + x0, n = x1 - x0;
                Array.Copy(_mm, at, MmNext, at, n); Array.Copy(VType, at, VTypeNext, at, n); Array.Copy(VQuality, at, VQualityNext, at, n);
                Array.Copy(VAge, at, VAgeNext, at, n); Array.Copy(VDensity, at, VDensityNext, at, n);
            }
        }

        /// <summary>The live sidecar's reader: the snow, then the planes from it. The tile hashes as saved: a save made while an
        /// hour was in flight holds the hour's result in the snow and both hash sets, and lands as it would have.</summary>
        internal void LoadLive(int width, int height, in SnowRules rules, SnowField field, ulong[] landed, ulong[] next)
        {
            Resize(width, height);
            MakeLive(width, height, rules);
            Field = field;
            for (int t = 0; t < _tileHash.Length; t++) Refresh(t);
            if (landed != null) Array.Copy(landed, _tileHash, _tileHash.Length);
            if (next != null) Array.Copy(next, TileHashNext, TileHashNext.Length);
            for (int t = 0; t < _tileHash.Length; t++)
            {
                TileBounds(t, out int x0, out int y0, out int x1, out int y1);
                for (int y = y0; y < y1; y++)
                    for (int x = x0; x < x1; x++) WritePlanes(y * Width + x, MmNext, VTypeNext, VQualityNext, VAgeNext, VDensityNext);
                TileChanged[t] = false;
            }
            StageAll();
        }

        /// <summary>Applies a snowpack: the field's size first (a new size starts bare), then its rectangle of depths. Live, a
        /// seed (the world joins and lands any hour in flight first).</summary>
        internal bool Apply(SimSnowpack p)
        {
            if (Live) return Seed(p) != long.MinValue;
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
            if (Live) return LiveTileHash(t);
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
                bool written = Live ? ring.TryWrite(x0, y0, w, hgt, _mm, Width, VType, VQuality, VAge, VDensity)
                                    : ring.TryWrite(x0, y0, w, hgt, _mm, Width);
                if (!written) break;
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
    /// Task 4.07 PR A2: a slot also carries the tile's surface (type, quality ‰ ÷ 4, age in face hours and density kg/m³ ÷ 4,
    /// four bytes a cell, one texel of the screen's conditions texture), all zero for static snow.
    /// Allocated once: 64 slots of 64 × 64 cells (24 KB each).
    /// </summary>
    public sealed class TileStagingRing
    {
        public const int DefaultSlots = 64;
        const int SlotCells = SimSnow.TileCells * SimSnow.TileCells;

        readonly ushort[] _cells;
        readonly byte[] _surface;
        readonly int[] _x, _y, _w, _h;
        public readonly int Slots;
        long _written;      // the worker's
        long _released;     // the reader's

        public TileStagingRing(int slots = DefaultSlots)
        {
            if (slots < 1) throw new ArgumentOutOfRangeException(nameof(slots));
            Slots = slots;
            _cells = new ushort[slots * SlotCells];
            _surface = new byte[slots * SlotCells * 4];
            _x = new int[slots]; _y = new int[slots]; _w = new int[slots]; _h = new int[slots];
        }

        /// <summary>Slots written so far (the worker's count; the snapshot carries it).</summary>
        public long Written => Volatile.Read(ref _written);

        /// <summary>Slots the reader has handed back.</summary>
        public long Released => Volatile.Read(ref _released);

        /// <summary>The worker: copies a tile of a field into the next slot, or false when every slot is still the reader's.</summary>
        internal bool TryWrite(int x, int y, int width, int height, ushort[] field, int fieldWidth,
                               byte[] type = null, byte[] quality = null, byte[] age = null, byte[] density = null)
        {
            long n = _written;
            if (n - Volatile.Read(ref _released) >= Slots) return false;
            int s = (int)(n % Slots);
            int at = s * SlotCells;
            for (int j = 0; j < height; j++) Array.Copy(field, (y + j) * fieldWidth + x, _cells, at + j * width, width);
            int b = at * 4;
            if (type == null) Array.Clear(_surface, b, width * height * 4);
            else
                for (int j = 0; j < height; j++)
                {
                    int src = (y + j) * fieldWidth + x, dst = b + j * width * 4;
                    for (int i = 0; i < width; i++, dst += 4)
                    {
                        _surface[dst] = type[src + i]; _surface[dst + 1] = quality[src + i];
                        _surface[dst + 2] = age[src + i]; _surface[dst + 3] = density[src + i];
                    }
                }
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

        /// <summary>The same slot's surface: four bytes a cell (type, quality ÷ 4, age, density ÷ 4) from <paramref name="offset"/>,
        /// rows of the slot's width.</summary>
        public void ReadSurface(long n, out int offset, out byte[] surface)
        {
            offset = (int)(n % Slots) * SlotCells * 4;
            surface = _surface;
        }

        /// <summary>The reader hands back every slot before write number <paramref name="through"/>.</summary>
        public void Release(long through)
        {
            if (through > Volatile.Read(ref _written)) throw new ArgumentOutOfRangeException(nameof(through), "That slot isn't written yet.");
            if (through > _released) Volatile.Write(ref _released, through);
        }
    }
}
