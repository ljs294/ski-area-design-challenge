using System;
using MountainPlanner.Domain.Weather;

namespace MountainPlanner.Domain.Snow
{
    /// <summary>
    /// SN-01 v2 (task 4.07 PR A2): the snow's static inputs over the 8 m grid, built once at open from the downloaded
    /// heights (the opener's background task, beside the weather's) and patched after earthwork and Clear trees.
    /// Per cell (7 B):
    /// - <see cref="NormalX"/>, <see cref="NormalY"/>: the unit normal's grid-east and grid-north parts × 127 (its up part is
    ///   <see cref="NormalZMicro"/>), from central differences of the cells' mean heights over 16 m;
    /// - <see cref="Canopy"/>: the forest floor's mean weight (0–255);
    /// - <see cref="Rise"/>: the cell above (+) or below (−) the mean of the 7 × 7 cells round it (cm, ±327 m): the ridges the
    ///   wind scours and the hollows it loads (Snowpack v0's measure, SN-06, SN-07); cm, not dm, so the drift's ramps (SN-07
    ///   v3) run smoothly across the cells instead of stepping at each whole decimetre;
    /// - <see cref="BinPlace"/>: where the cell's own height sits in its weather elevation bin (0 its bottom, 255 its top), so
    ///   the snow reads the air blended between bins by its own height, not in 33 ft terraces;
    /// - <see cref="Exposure"/>: the weather's wind exposure factor blended over 3 × 3 cells (‰ ÷ 8), so the wind doesn't
    ///   step at a class border.
    /// On a 16 m lattice (every other cell's centre), from the downloaded heights only (never earthwork, as WX-59): the
    /// skyline in 16 grid directions (<see cref="Horizon"/>, the sine of its elevation × 255) and the open sky's share
    /// (<see cref="OpenSky"/>, ‰ ÷ 4). About 22 MB at 11 km. Integers only: the same bytes on every runtime.
    /// </summary>
    public sealed class SnowTerrain
    {
        public const int CellMetres = SurfaceGrid.CellMetres, LatticeCells = 2, Directions = 16, RiseRadius = 3;
        public const int MaxReachMetres = 3000;

        public readonly int Width, Depth, LatticeWidth, LatticeDepth;
        public readonly sbyte[] NormalX, NormalY;
        public readonly short[] Rise;
        public readonly byte[] Canopy, BinPlace, Exposure, Horizon, OpenSky;
        /// <summary>The weather's terrain hash these were built against (its bins).</summary>
        public readonly ulong WeatherHash;
        public ulong Hash { get; private set; }

        public int Cells => Width * Depth;

        SnowTerrain(int width, int depth, ulong weatherHash)
        {
            Width = width; Depth = depth; WeatherHash = weatherHash;
            LatticeWidth = (width + LatticeCells - 1) / LatticeCells;
            LatticeDepth = (depth + LatticeCells - 1) / LatticeCells;
            int n = width * depth, l = LatticeWidth * LatticeDepth;
            NormalX = new sbyte[n]; NormalY = new sbyte[n]; Rise = new short[n];
            Canopy = new byte[n]; BinPlace = new byte[n]; Exposure = new byte[n];
            Horizon = new byte[l * Directions]; OpenSky = new byte[l];
        }

        // ---------------- the unit normal ----------------

        /// <summary>A stored part (× 127) in millionths.</summary>
        public static int Micro(sbyte v) => v * 1_000_000 / 127;

        static readonly int[] NzTable = BuildNz();

        static int[] BuildNz()
        {
            var t = new int[2 * 127 * 127 + 1];
            for (int s = 0; s < t.Length; s++)
                t[s] = s >= 127 * 127 ? 0 : (int)WeatherMath.ISqrt((127L * 127 - s) * 1_000_000_000_000L / (127 * 127));
            return t;
        }

        /// <summary>The normal's up part (millionths) from the stored east and north parts.</summary>
        public int NormalZMicro(int cell) { int x = NormalX[cell], y = NormalY[cell]; return NzTable[x * x + y * y]; }

        // ---------------- build ----------------

        /// <summary>
        /// The inputs from the cells' mean heights (mm, <see cref="SurfaceSampler.NoData"/> where none) and canopy, as downloaded,
        /// against the weather's terrain on the same grid (its bins). <paramref name="exposureFactors"/> are the weather's eight
        /// exposure classes' wind factors (‰). The horizon is the slow part: build it here, or band by band with
        /// <see cref="BuildHorizonRows"/> on several threads (each lattice row is its own).
        /// </summary>
        public static SnowTerrain Build(int width, int depth, int[] mm, byte[] canopy, WeatherTerrain weather, int[] exposureFactors, bool horizon = true)
        {
            if (mm == null || mm.Length != width * depth) throw new ArgumentException("One height per cell.", nameof(mm));
            if (canopy == null || canopy.Length != mm.Length) throw new ArgumentException("One share per cell.", nameof(canopy));
            if (weather == null || weather.Width != width || weather.Depth != depth) throw new ArgumentException("The weather's grid.", nameof(weather));
            if (exposureFactors == null || exposureFactors.Length != 8) throw new ArgumentException("Eight factors.", nameof(exposureFactors));
            var t = new SnowTerrain(width, depth, weather.Hash);
            Array.Copy(canopy, t.Canopy, canopy.Length);
            t.Shape(mm, 0, 0, width, depth);
            for (int c = 0; c < mm.Length; c++)
            {
                int bin = weather.Bin[c];
                if (bin == WeatherTerrain.NoData || mm[c] == SurfaceSampler.NoData) { t.BinPlace[c] = 128; continue; }
                long feet = WeatherMath.DivRound(mm[c] * 10L, 3048);
                long low = weather.MinFeet + (long)bin * weather.BinFeet;
                t.BinPlace[c] = (byte)WeatherMath.Clamp((int)((feet - low) * 256 / weather.BinFeet), 0, 255);
            }
            for (int j = 0; j < depth; j++)
                for (int i = 0; i < width; i++)
                {
                    long sum = 0; int n = 0;
                    for (int y = Math.Max(0, j - 1); y <= Math.Min(depth - 1, j + 1); y++)
                        for (int x = Math.Max(0, i - 1); x <= Math.Min(width - 1, i + 1); x++)
                        {
                            int c = y * width + x;
                            if (weather.Bin[c] == WeatherTerrain.NoData) continue;
                            sum += exposureFactors[weather.Exposure[c]]; n++;
                        }
                    t.Exposure[j * width + i] = (byte)WeatherMath.Clamp(n == 0 ? 125 : (int)WeatherMath.DivRound(sum, 8L * n), 0, 255);
                }
            if (horizon) t.BuildHorizonRows(mm, 0, t.LatticeDepth);
            t.Rehash();
            return t;
        }

        /// <summary>The normal and rise over [i0, i1) × [j0, j1) from the heights (whole grid's array).</summary>
        void Shape(int[] mm, int i0, int j0, int i1, int j1)
        {
            int w = Width, d = Depth;
            // The 7 × 7 mean from a summed-area table over the rows it needs.
            int ya = Math.Max(0, j0 - RiseRadius), yb = Math.Min(d, j1 + RiseRadius);
            int sw = w + 1, sh = yb - ya + 1;
            var sum = new long[sw * sh];
            var cnt = new int[sw * sh];
            for (int y = ya; y < yb; y++)
            {
                long row = 0; int rc = 0;
                int r = (y - ya + 1) * sw, up = (y - ya) * sw;
                for (int x = 0; x < w; x++)
                {
                    int v = mm[y * w + x];
                    if (v != SurfaceSampler.NoData) { row += v; rc++; }
                    sum[r + x + 1] = sum[up + x + 1] + row;
                    cnt[r + x + 1] = cnt[up + x + 1] + rc;
                }
            }
            for (int j = j0; j < j1; j++)
                for (int i = i0; i < i1; i++)
                {
                    int c = j * w + i, e = mm[c];
                    if (e == SurfaceSampler.NoData) { NormalX[c] = 0; NormalY[c] = 0; Rise[c] = 0; continue; }
                    Difference(mm, i, j, 1, 0, out long ax, out long runX);
                    Difference(mm, i, j, 0, 1, out long ay, out long runY);
                    // Normal ∝ (−ax·runY, −ay·runX, runX·runY), in cm so a cliff's square stays in range.
                    long nx = -ax / 10 * runY, ny = -ay / 10 * runX, nz = runX * runY;
                    long len = WeatherMath.ISqrt(nx * nx + ny * ny + nz * nz);
                    NormalX[c] = (sbyte)WeatherMath.Clamp((int)WeatherMath.DivRound(nx * 127, Math.Max(1, len)), -127, 127);
                    NormalY[c] = (sbyte)WeatherMath.Clamp((int)WeatherMath.DivRound(ny * 127, Math.Max(1, len)), -127, 127);

                    int x0 = Math.Max(0, i - RiseRadius), x1 = Math.Min(w, i + RiseRadius + 1);
                    int y0 = Math.Max(ya, j - RiseRadius) - ya, y1 = Math.Min(yb, j + RiseRadius + 1) - ya;
                    long s = sum[y1 * sw + x1] - sum[y0 * sw + x1] - sum[y1 * sw + x0] + sum[y0 * sw + x0];
                    int n = cnt[y1 * sw + x1] - cnt[y0 * sw + x1] - cnt[y1 * sw + x0] + cnt[y0 * sw + x0];
                    long mean = n == 0 ? e : WeatherMath.DivRound(s, n);
                    Rise[c] = (short)WeatherMath.Clamp((int)WeatherMath.DivRound(e - mean, 10), -32_767, 32_767);
                }
        }

        /// <summary>The height difference across a cell along one axis (mm) and the run it spans (8 m units: 2 for central, 1
        /// one-sided at an edge or beside missing data; 0 run means flat).</summary>
        void Difference(int[] mm, int i, int j, int dx, int dy, out long rise, out long run)
        {
            int w = Width, d = Depth, c = mm[j * w + i];
            int xa = i - dx, ya = j - dy, xb = i + dx, yb = j + dy;
            bool before = xa >= 0 && ya >= 0 && mm[ya * w + xa] != SurfaceSampler.NoData;
            bool after = xb < w && yb < d && mm[yb * w + xb] != SurfaceSampler.NoData;
            if (before && after) { rise = mm[yb * w + xb] - mm[ya * w + xa]; run = 2 * CellMetres * 1000L / 10; return; }
            if (after) { rise = mm[yb * w + xb] - c; run = CellMetres * 1000L / 10; return; }
            if (before) { rise = c - mm[ya * w + xa]; run = CellMetres * 1000L / 10; return; }
            rise = 0; run = CellMetres * 1000L / 10;
        }

        // ---------------- the horizon (WX-59) ----------------

        /// <summary>The 16 grid directions' (east, north) in millionths, clockwise from grid north.</summary>
        static readonly int[] DirEast = { 0, 382_683, 707_107, 923_880, 1_000_000, 923_880, 707_107, 382_683, 0, -382_683, -707_107, -923_880, -1_000_000, -923_880, -707_107, -382_683 };
        static readonly int[] DirNorth = { 1_000_000, 923_880, 707_107, 382_683, 0, -382_683, -707_107, -923_880, -1_000_000, -923_880, -707_107, -382_683, 0, 382_683, 707_107, 923_880 };

        /// <summary>The distances the skyline is looked for at (mm): every 16 m to 128 m, then each 1/8 further, to 3 km.</summary>
        static readonly int[] Reach = BuildReach();

        static int[] BuildReach()
        {
            var r = new System.Collections.Generic.List<int>();
            int d = 16_000;
            while (d <= MaxReachMetres * 1000)
            {
                r.Add(d);
                d += Math.Max(16_000, d / 8);
            }
            return r.ToArray();
        }

        /// <summary>The skyline and open sky for lattice rows [b0, b1) from the cells' mean heights (as downloaded).</summary>
        public void BuildHorizonRows(int[] mm, int b0, int b1)
        {
            int w = Width, d = Depth, lw = LatticeWidth;
            long cellMm = CellMetres * 1000L;
            for (int b = Math.Max(0, b0); b < Math.Min(LatticeDepth, b1); b++)
                for (int a = 0; a < lw; a++)
                {
                    int ci = Math.Min(w - 1, a * LatticeCells), cj = Math.Min(d - 1, b * LatticeCells);
                    int e0 = mm[cj * w + ci];
                    int l = b * lw + a;
                    if (e0 == SurfaceSampler.NoData)
                    {
                        for (int k = 0; k < Directions; k++) Horizon[l * Directions + k] = 0;
                        OpenSky[l] = 250;
                        continue;
                    }
                    long px = ci * cellMm + cellMm / 2, py = cj * cellMm + cellMm / 2;
                    long sky = 0;
                    for (int k = 0; k < Directions; k++)
                    {
                        long best = 0;   // tan of the skyline's elevation, millionths
                        for (int s = 0; s < Reach.Length; s++)
                        {
                            long r = Reach[s];
                            long qx = px + r * DirEast[k] / 1_000_000, qy = py + r * DirNorth[k] / 1_000_000;
                            if (qx < 0 || qy < 0) break;
                            int qi = (int)(qx / cellMm), qj = (int)(qy / cellMm);
                            if (qi >= w || qj >= d) break;
                            int e = mm[qj * w + qi];
                            if (e == SurfaceSampler.NoData) continue;
                            long tan = (e - e0) * 1_000_000L / r;
                            if (tan > best) best = tan;
                        }
                        // sin = tan ÷ √(1 + tan²), × 255.
                        long sin255 = best == 0 ? 0 : best * 255 / WeatherMath.ISqrt(1_000_000_000_000L + best * best);
                        Horizon[l * Directions + k] = (byte)Math.Min(255, sin255);
                        sky += sin255 * sin255;
                    }
                    // The open sky over flat ground: 1 − the mean of sin² of the skyline's elevation (‰ ÷ 4).
                    long open = 1000 - sky * 1000 / (Directions * 255L * 255);
                    OpenSky[l] = (byte)WeatherMath.Clamp((int)((open + 2) / 4), 0, 250);
                }
        }

        // ---------------- edits ----------------

        /// <summary>
        /// The changed rectangle [i0, i1) × [j0, j1) of an edit's heights and canopy (the whole grid's arrays, mean heights in
        /// mm) as a patch for the worker: the rectangle grows by the rise's reach so every cell whose shape changed is in it.
        /// The bins and the horizon stay as downloaded.
        /// </summary>
        public SnowTerrainPatch Patch(int[] mm, byte[] canopy, int i0, int j0, int i1, int j1)
        {
            i0 = Math.Max(0, i0 - RiseRadius); j0 = Math.Max(0, j0 - RiseRadius);
            i1 = Math.Min(Width, i1 + RiseRadius); j1 = Math.Min(Depth, j1 + RiseRadius);
            if (i1 <= i0 || j1 <= j0) return null;
            var scratch = new SnowTerrain(Width, Depth, WeatherHash, false);
            scratch.Shape(mm, i0, j0, i1, j1);
            int pw = i1 - i0, ph = j1 - j0;
            var p = new SnowTerrainPatch(i0, j0, pw, ph);
            for (int j = 0; j < ph; j++)
            {
                int src = (j0 + j) * Width + i0, dst = j * pw;
                Array.Copy(scratch.NormalX, src, p.NormalX, dst, pw);
                Array.Copy(scratch.NormalY, src, p.NormalY, dst, pw);
                Array.Copy(scratch.Rise, src, p.Rise, dst, pw);
                Array.Copy(canopy, src, p.Canopy, dst, pw);
            }
            return p;
        }

        // A full-size scratch for a patch's shapes (no lattice): only the rectangle is written.
        SnowTerrain(int width, int depth, ulong weatherHash, bool lattice)
        {
            Width = width; Depth = depth; WeatherHash = weatherHash;
            int n = width * depth;
            NormalX = new sbyte[n]; NormalY = new sbyte[n]; Rise = new short[n];
            Canopy = Array.Empty<byte>(); BinPlace = Array.Empty<byte>(); Exposure = Array.Empty<byte>();
            Horizon = Array.Empty<byte>(); OpenSky = Array.Empty<byte>();
        }

        /// <summary>The worker's copy takes a patch in place (the worker owns it once sent); the hash follows. The presentation's
        /// copy may leave the hash for later (<paramref name="rehash"/> false: a few MB to hash, once per clone, not per edit).</summary>
        public void Apply(SnowTerrainPatch p, bool rehash = true)
        {
            if (p == null || p.X < 0 || p.Y < 0 || p.X + p.Width > Width || p.Y + p.Height > Depth) return;
            for (int j = 0; j < p.Height; j++)
            {
                int dst = (p.Y + j) * Width + p.X, src = j * p.Width;
                Array.Copy(p.NormalX, src, NormalX, dst, p.Width);
                Array.Copy(p.NormalY, src, NormalY, dst, p.Width);
                Array.Copy(p.Rise, src, Rise, dst, p.Width);
                Array.Copy(p.Canopy, src, Canopy, dst, p.Width);
            }
            if (rehash) Rehash();
            else _stale = true;
        }

        bool _stale;

        /// <summary>A copy the worker can own while the presentation keeps patching its own.</summary>
        public SnowTerrain Clone()
        {
            var t = new SnowTerrain(Width, Depth, WeatherHash);
            Array.Copy(NormalX, t.NormalX, NormalX.Length); Array.Copy(NormalY, t.NormalY, NormalY.Length);
            Array.Copy(Rise, t.Rise, Rise.Length); Array.Copy(Canopy, t.Canopy, Canopy.Length);
            Array.Copy(BinPlace, t.BinPlace, BinPlace.Length); Array.Copy(Exposure, t.Exposure, Exposure.Length);
            Array.Copy(Horizon, t.Horizon, Horizon.Length); Array.Copy(OpenSky, t.OpenSky, OpenSky.Length);
            if (_stale) t.Rehash();
            else t.Hash = Hash;
            return t;
        }

        /// <summary>FNV-1a over every array, in a fixed order.</summary>
        public void Rehash()
        {
            ulong h = 14695981039346656037UL;
            void Add(byte b) { h ^= b; h *= 1099511628211UL; }
            void Int(int v) { for (int s = 0; s < 32; s += 8) Add((byte)(v >> s)); }
            Int(Width); Int(Depth);
            foreach (sbyte v in NormalX) Add((byte)v);
            foreach (sbyte v in NormalY) Add((byte)v);
            foreach (short v in Rise) { Add((byte)v); Add((byte)(v >> 8)); }
            foreach (byte v in Canopy) Add(v);
            foreach (byte v in BinPlace) Add(v);
            foreach (byte v in Exposure) Add(v);
            foreach (byte v in Horizon) Add(v);
            foreach (byte v in OpenSky) Add(v);
            Hash = h;
            _stale = false;
        }
    }

    /// <summary>A rectangle of new shapes and canopy after an edit (command 32's payload), never touched after it's sent.</summary>
    public sealed class SnowTerrainPatch
    {
        public readonly int X, Y, Width, Height;
        public readonly sbyte[] NormalX, NormalY;
        public readonly short[] Rise;
        public readonly byte[] Canopy;

        public SnowTerrainPatch(int x, int y, int width, int height)
        {
            X = x; Y = y; Width = width; Height = height;
            int n = width * height;
            NormalX = new sbyte[n]; NormalY = new sbyte[n]; Rise = new short[n]; Canopy = new byte[n];
        }
    }
}
