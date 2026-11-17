using System;

namespace MountainPlanner.Domain.Weather
{
    /// <summary>The <c>Pacing</c> numbers the static inputs read (SI-01), so Domain needs no reference to Simulation.</summary>
    public readonly struct WeatherTerrainTuning
    {
        /// <summary>WX-35: the hvf (ft) at which each exposure class above 0 starts (7 rising values), and the 7×7 rise (ft) that moves a cell a class.</summary>
        public readonly int[] ExposureHvfFeet;
        public readonly int ExposureRiseFeet;
        /// <summary>WX-35 v2: the orographic factor's most on a windward face, just past a crest and in the shadow (‰), and its reach (m).</summary>
        public readonly int Windward, Spillover, Shadow, SpilloverReachMetres;
        /// <summary>The climb (ft) that earns the whole windward factor, and the barrier (ft) that casts the whole shadow.</summary>
        public readonly int ClimbFeet, BarrierFeet;
        /// <summary>WX-39: the steering's turn reach (the valley's depth, ft, that turns the wind fully along it), the crest's speed-up (‰) and the lee's slow-down (‰ factor) behind a rise of <see cref="LeeFeet"/>.</summary>
        public readonly int ValleyFeet, ShelterFeet, HillSpeed, CrestLee, LeeFeet;

        public WeatherTerrainTuning(int[] exposureHvfFeet, int exposureRiseFeet, int windward, int spillover, int shadow, int spilloverReachMetres,
                                    int climbFeet, int barrierFeet, int valleyFeet, int shelterFeet, int hillSpeed, int crestLee, int leeFeet)
        {
            if (exposureHvfFeet == null || exposureHvfFeet.Length != 7) throw new ArgumentException("Seven class thresholds.", nameof(exposureHvfFeet));
            ExposureHvfFeet = (int[])exposureHvfFeet.Clone(); ExposureRiseFeet = exposureRiseFeet;
            Windward = windward; Spillover = spillover; Shadow = shadow; SpilloverReachMetres = spilloverReachMetres;
            ClimbFeet = Math.Max(1, climbFeet); BarrierFeet = Math.Max(1, barrierFeet);
            ValleyFeet = Math.Max(1, valleyFeet); ShelterFeet = Math.Max(1, shelterFeet); HillSpeed = hillSpeed; CrestLee = crestLee; LeeFeet = leeFeet;
        }
    }

    /// <summary>
    /// WX-35's static inputs (task 4.05 PR B2): what the weather over the map needs from the terrain, worked out once when an
    /// area opens from the downloaded heights on the 8 m surface grid, never from the player's earthwork (WX-08: no design
    /// moves the weather, no pit farms a warmer night). Integers only, immutable once built; the worker gets it by reference
    /// (SI-21).
    /// - Per 8 m cell: the elevation bin (<see cref="BinFeet"/>), the height above the valley floor (<c>hvf</c>, the lowest
    ///   cell within 1 km, 10 ft steps) and the exposure class (0 sheltered valley … 7 exposed summit).
    /// - WX-35 v3 (task 4.07 PR A2, owner 2026-11-14): per arrival octant, the upwind crest within 3 km (its height, its
    ///   distance, and the lowest ground on the way), marched over the 512 m blocks and carried on a 64 m lattice by the
    ///   curved filter. Each 8 m cell judges its orographic factor by its own height against it (<see cref="OrographicAt"/>),
    ///   so gullies sit deeper in a lee than ridges and the line of equal precipitation follows the ground; normalised so
    ///   its mean over the box's cells is 1.0 (the Daymet lattice is the box's pixels at 1 km, so it has the same mean).
    ///   v2 gave a whole 512 m block one factor from its mean height, blended in straight lines: a straight snow line.
    /// - Per 96 m cell: the valley's axis and how strongly it turns the wind; per octant, the speed factor (0.01).
    /// </summary>
    public sealed class WeatherTerrain
    {
        public const int CellMetres = Snow.SurfaceGrid.CellMetres, MinBinFeet = 33, Bins = 256, Bands = 16, BandFeet = 32, NoData = 255;
        public const int CoarseCells = 64, SteerCells = 12;
        /// <summary>WX-35 v3: the crest lattice's spacing (cells, 64 m).</summary>
        public const int FineCells = 8;
        /// <summary>WX-35 v3: the ramp that turns windward into lee without a step (over the first ft below the crest's height).</summary>
        public const int CrestRampFeet = 400;
        /// <summary>The valley floor's reach (1 km in cells), the reference pixel's half-size (500 m) and the upwind search (3 km).</summary>
        const int FloorRadius = 125, RefRadius = 62, UpwindMetres = 3000;

        public readonly int Width, Depth;
        /// <summary>The south-west corner of cell (0, 0) in the design's frame, mm from the site's centre.</summary>
        public readonly int OriginXMm, OriginYMm;
        /// <summary>The lowest elevation (ft), bin 0's floor.</summary>
        public readonly int MinFeet;
        /// <summary>
        /// WX-02 v2: an elevation bin's height (ft): <see cref="MinBinFeet"/> (10 m), coarser only where the area's relief
        /// is more than 255 such bins (about 8,400 ft), so the highest cell always has its own bin (bin 255 is no data).
        /// </summary>
        public readonly int BinFeet;
        /// <summary>Per cell (west to east, then south to north): elevation bin, hvf (10 ft), exposure class; <see cref="NoData"/> where the heights have none.</summary>
        public readonly byte[] Bin, Hvf, Exposure;
        /// <summary>The (bin, pool band) pairs the area has: the air table fills only these.</summary>
        public readonly bool[] Present;
        /// <summary>WX-36 v2: the reference pixel's mean hvf (ft): the pool is measured against it.</summary>
        public readonly int HvfRefFeet;
        public readonly int CoarseWidth, CoarseDepth, SteerWidth, SteerDepth;
        /// <summary>
        /// WX-35 v3, [octant × fine nodes] (64 m, <see cref="FineWidth"/> × <see cref="FineDepth"/>; node m's centre is cell 8m + 4):
        /// the highest ground upwind within 3 km (ft), how far it is (m), and the lowest ground on the way (ft). Octant 0 is a
        /// wind from the north, clockwise. Where nothing lies upwind (the map's edge) the crest is the ground itself, far off.
        /// </summary>
        public readonly short[] CrestFeet, CrestMetres, UpwindLowFeet;
        public readonly int FineWidth, FineDepth;
        /// <summary>WX-35 v3: per octant, the raw factor's mean over the box's cells (‰): the factor is raw × 1000 ÷ this.</summary>
        public readonly int[] OrographicMean;
        /// <summary>The orographic rule's numbers (WeatherTerrainTuning's): windward, spillover and shadow (‰), the spillover's
        /// reach (m), the climb and the barrier (ft).</summary>
        public readonly int Windward, Spillover, Shadow, SpilloverReachMetres, ClimbFeet, BarrierFeet;
        /// <summary>Per steer cell: the valley's axis as a direction (× 45°, on the grid), pointing down the valley, and how
        /// strongly the cell turns the wind to it (%): see <see cref="TurnDegrees"/>.</summary>
        public readonly byte[] Axis, TurnWeight;
        /// <summary>[octant × steer cells]: the speed factor (0.01) for a wind arriving from that octant.</summary>
        public readonly byte[] Speed;
        /// <summary>
        /// The grid's convergence, 0.1°: from grid north clockwise to true north (0.3 §4.1). The weather's directions are true
        /// (POWER's); the octants and the axes are the grid's, so a direction is turned by this before either is read.
        /// </summary>
        public readonly int ConvergenceTenths;
        /// <summary>Per elevation bin: the mean hvf (ft) of the box's cells at that height, −1 where the box has none: where
        /// a reference point (the base, the summit) sits in the pool (WX-36 v2).</summary>
        public readonly short[] BinHvfFeet;
        /// <summary>A content hash over every field.</summary>
        public readonly ulong Hash;

        public int Cells => Width * Depth;

        /// <summary>The cell holding a point in the design's frame (mm from the site's centre), or −1 outside the grid.</summary>
        public int CellAt(long xMm, long yMm)
        {
            long i = FloorDiv(xMm - OriginXMm, CellMetres * 1000), j = FloorDiv(yMm - OriginYMm, CellMetres * 1000);
            return i < 0 || j < 0 || i >= Width || j >= Depth ? -1 : (int)(j * Width + i);
        }

        /// <summary>The bin holding a height (clamped to the area's range).</summary>
        public int BinOf(int feet) => WeatherMath.Clamp((feet - MinFeet) / BinFeet, 0, Bins - 2);

        /// <summary>A direction on the grid (°) from a true one.</summary>
        public int GridDegrees(int trueDegrees) => ((trueDegrees + (int)WeatherMath.DivRound(ConvergenceTenths, 10)) % 360 + 360) % 360;

        /// <summary>A cell's bin centre (ft).</summary>
        public int BinCentreFeet(int bin) => MinFeet + bin * BinFeet + BinFeet / 2;

        /// <summary>A cell's pool band (WX-02 v2: 32 ft steps over 0–512 ft, the last one everything above).</summary>
        public static int BandOf(int hvfTenFeet) => Math.Min(Bands - 1, hvfTenFeet * 10 / BandFeet);

        WeatherTerrain(int width, int depth, int originX, int originY, int minFeet, int binFeet, byte[] bin, byte[] hvf, byte[] exposure, bool[] present, int hvfRef,
                       int cw, int cd, Crest crest, int sw, int sd, byte[] axis, byte[] weight, byte[] speed, int convergenceTenths, short[] binHvf,
                       WeatherTerrainTuning t)
        {
            Width = width; Depth = depth; OriginXMm = originX; OriginYMm = originY; MinFeet = minFeet; BinFeet = binFeet;
            Bin = bin; Hvf = hvf; Exposure = exposure; Present = present; HvfRefFeet = hvfRef;
            CoarseWidth = cw; CoarseDepth = cd; SteerWidth = sw; SteerDepth = sd; Axis = axis; TurnWeight = weight; Speed = speed;
            CrestFeet = crest.Feet; CrestMetres = crest.Metres; UpwindLowFeet = crest.Low; FineWidth = crest.W; FineDepth = crest.D;
            OrographicMean = crest.Mean;
            Windward = t.Windward; Spillover = t.Spillover; Shadow = t.Shadow; SpilloverReachMetres = t.SpilloverReachMetres;
            ClimbFeet = t.ClimbFeet; BarrierFeet = t.BarrierFeet;
            ConvergenceTenths = convergenceTenths; BinHvfFeet = binHvf;
            ulong h = 14695981039346656037UL;
            void Mix(long v) => h = (h ^ (ulong)v) * 1099511628211UL;
            Mix(width); Mix(depth); Mix(originX); Mix(originY); Mix(minFeet); Mix(binFeet); Mix(hvfRef); Mix(convergenceTenths);
            foreach (byte v in bin) Mix(v);
            foreach (byte v in hvf) Mix(v);
            foreach (byte v in exposure) Mix(v);
            Mix(FineWidth); Mix(FineDepth);
            foreach (short v in CrestFeet) Mix(v);
            foreach (short v in CrestMetres) Mix(v);
            foreach (short v in UpwindLowFeet) Mix(v);
            foreach (int v in OrographicMean) Mix(v);
            Mix(Windward); Mix(Spillover); Mix(Shadow); Mix(SpilloverReachMetres); Mix(ClimbFeet); Mix(BarrierFeet);
            foreach (byte v in axis) Mix(v);
            foreach (byte v in weight) Mix(v);
            foreach (byte v in speed) Mix(v);
            Hash = h;
        }

        /// <summary>
        /// WX-35 v3: the orographic factor (‰, 100–3,000) for wind from octant <paramref name="octant"/> at cell
        /// (<paramref name="i"/>, <paramref name="j"/>) standing at <paramref name="feet"/>: the crest read from the 64 m lattice
        /// (straight-line blend between nodes, the nodes themselves already curved), the rule on the cell's own height.
        /// </summary>
        public int OrographicAt(int octant, int i, int j, int feet)
        {
            int raw = RawAt(CrestFeet, CrestMetres, UpwindLowFeet, FineWidth, FineDepth, octant, i, j, feet,
                            Windward, Spillover, Shadow, SpilloverReachMetres, ClimbFeet, BarrierFeet);
            return WeatherMath.Clamp((int)WeatherMath.DivRound(raw * 1000L, OrographicMean[octant]), 100, 3000);
        }

        /// <summary>The raw factor (‰, before the box's mean) at a cell: the crest read off the lattice, the rule on the cell's height.</summary>
        static int RawAt(short[] crestFeet, short[] crestMetres, short[] lowFeet, int fw, int fd, int octant, int i, int j, int feet,
                         int windward, int spillover, int shadow, int reach, int climbFeet, int barrierFeet)
        {
            // Node m's centre is cell 8m + 4: in half cells 16m + 8, the cell's centre 2i + 1.
            int fx = i * 2 + 1 - FineCells, fy = j * 2 + 1 - FineCells;
            int x0 = WeatherMath.Clamp(FloorDiv(fx, 2 * FineCells), 0, fw - 1), y0 = WeatherMath.Clamp(FloorDiv(fy, 2 * FineCells), 0, fd - 1);
            int ux = WeatherMath.Clamp(fx - x0 * 2 * FineCells, 0, 2 * FineCells), uy = WeatherMath.Clamp(fy - y0 * 2 * FineCells, 0, 2 * FineCells);
            int x1 = Math.Min(fw - 1, x0 + 1), y1 = Math.Min(fd - 1, y0 + 1);
            int b = octant * fw * fd, a00 = b + y0 * fw + x0, a10 = b + y0 * fw + x1, a01 = b + y1 * fw + x0, a11 = b + y1 * fw + x1;
            int w00 = (16 - ux) * (16 - uy), w10 = ux * (16 - uy), w01 = (16 - ux) * uy, w11 = ux * uy;
            int crest = (int)WeatherMath.DivRound((long)crestFeet[a00] * w00 + (long)crestFeet[a10] * w10 + (long)crestFeet[a01] * w01 + (long)crestFeet[a11] * w11, 256);
            int at = (int)WeatherMath.DivRound((long)crestMetres[a00] * w00 + (long)crestMetres[a10] * w10 + (long)crestMetres[a01] * w01 + (long)crestMetres[a11] * w11, 256);
            int low = (int)WeatherMath.DivRound((long)lowFeet[a00] * w00 + (long)lowFeet[a10] * w10 + (long)lowFeet[a01] * w01 + (long)lowFeet[a11] * w11, 256);
            return Raw(feet, crest, at, low, windward, spillover, shadow, reach, climbFeet, barrierFeet);
        }

        /// <summary>
        /// WX-35 v3's rule for ground at <paramref name="feet"/> under a crest of <paramref name="crest"/> ft, <paramref name="at"/> m
        /// upwind, with <paramref name="low"/> ft the lowest ground on the way (‰, before the box's mean):
        /// - at or above the crest, windward: up to +windward as the air climbs into the cell from the low ground;
        /// - below it, the spillover, +spillover at the crest easing to nothing at the reach, less the shadow, which eases in
        ///   from half the reach to twice it and is up to −shadow as the cell sits deeper below the crest;
        /// - distance only ever eases, and the turn to windward is a ramp over the first <see cref="CrestRampFeet"/> below the
        ///   crest's height (level ground under a level upwind is plain windward, 1.0), so no line on the map is a step.
        /// </summary>
        public static int Raw(int feet, int crest, int at, int low, int windward, int spillover, int shadow, int reach, int climbFeet, int barrierFeet)
        {
            int climb = WeatherMath.Clamp(feet - low, 0, climbFeet);
            long up = 1000 + (long)windward * climb / climbFeet;
            int below = Math.Max(0, crest - feet);
            // Distance only ever eases (smoothsteps over the reach and more): a line of equal distance from a crest is straight,
            // so nothing on it may step. What changes fast is the cell's own depth below the crest, which follows the ground.
            reach = Math.Max(1, reach);
            long spill = (long)spillover * (1000 - Smooth(at * 1000L / reach)) / 1000;
            long grow = Smooth((at - reach / 2) * 1000L / (reach * 3 / 2));
            long dark = (long)shadow * Math.Min(barrierFeet, below) / barrierFeet * grow / 1000;
            long lee = 1000 + spill - dark;
            int wl = WeatherMath.Clamp((crest - feet) * 1000 / CrestRampFeet, 0, 1000);
            return (int)((up * (1000 - wl) + lee * wl) / 1000);
        }

        /// <summary>Smoothstep in ‰: 0 at or below 0, 1000 at or above 1000, flat at both ends.</summary>
        static long Smooth(long x)
        {
            x = Math.Max(0, Math.Min(1000, x));
            return x * x * (3000 - 2 * x) / 1_000_000;
        }

        static int FloorDiv(int a, int b) => a >= 0 ? a / b : -((-a + b - 1) / b);

        /// <summary>Octant <paramref name="o"/>'s upwind unit step (east, north): the direction the wind comes from.</summary>
        static readonly int[] Dx = { 0, 1, 1, 1, 0, -1, -1, -1 }, Dy = { 1, 1, 0, -1, -1, -1, 0, 1 };

        /// <summary>
        /// Builds the static inputs from the 8 m grid's elevations (ft, <see cref="int.MinValue"/> where there's no data).
        /// <paramref name="core"/> is the box in cells (x0, y0, x1, y1, ends exclusive), <paramref name="reference"/> the cell under
        /// the site's centre (the reference pixel), <paramref name="convergenceTenths"/> the grid's convergence (0.1°, grid
        /// north clockwise to true north).
        /// </summary>
        public static WeatherTerrain Build(int width, int depth, int[] feet, int originXMm, int originYMm,
                                           (int X0, int Y0, int X1, int Y1) core, int reference, WeatherTerrainTuning t, int convergenceTenths = 0)
        {
            if (width <= 0 || depth <= 0 || feet == null || feet.Length != width * depth) throw new ArgumentException("One elevation per cell.", nameof(feet));
            int n = width * depth;
            int min = int.MaxValue, max = int.MinValue;
            foreach (int f in feet)
                if (f != int.MinValue) { if (f < min) min = f; if (f > max) max = f; }
            if (min == int.MaxValue) min = max = 0;
            int binFeet = Math.Max(MinBinFeet, (int)(((long)max - min + Bins - 1) / (Bins - 1)));

            // hvf: the cell minus the lowest within 1 km (a square window, separable running minimum: rows, then columns).
            var floor = new int[n];
            var line = new int[Math.Max(width, depth)];
            var outLine = new int[line.Length];
            int[] pre = new int[line.Length], suf = new int[line.Length];   // the running minimum's, once (not per row: 30 MB of garbage at 11 km)
            for (int j = 0; j < depth; j++)
            {
                for (int i = 0; i < width; i++) { int f = feet[j * width + i]; line[i] = f == int.MinValue ? int.MaxValue : f; }
                RunningMin(line, width, FloorRadius, outLine, pre, suf);
                Array.Copy(outLine, 0, floor, j * width, width);
            }
            for (int i = 0; i < width; i++)
            {
                for (int j = 0; j < depth; j++) line[j] = floor[j * width + i];
                RunningMin(line, depth, FloorRadius, outLine, pre, suf);
                for (int j = 0; j < depth; j++) floor[j * width + i] = outLine[j];
            }

            // SN-01's 7×7 rise on the untouched terrain: the cell minus its window's mean (summed areas over the valid cells).
            var sum = new long[(width + 1) * (depth + 1)];
            var cnt = new int[(width + 1) * (depth + 1)];
            for (int j = 0; j < depth; j++)
                for (int i = 0; i < width; i++)
                {
                    int f = feet[j * width + i];
                    int k = (j + 1) * (width + 1) + i + 1;
                    sum[k] = (f == int.MinValue ? 0 : f) + sum[k - 1] + sum[k - width - 1] - sum[k - width - 2];
                    cnt[k] = (f == int.MinValue ? 0 : 1) + cnt[k - 1] + cnt[k - width - 1] - cnt[k - width - 2];
                }

            var bin = new byte[n];
            var hvf = new byte[n];
            var exposure = new byte[n];
            var present = new bool[Bins * Bands];
            var binHvfSum = new long[Bins];
            var binHvfCount = new int[Bins];
            for (int j = 0; j < depth; j++)
                for (int i = 0; i < width; i++)
                {
                    int c = j * width + i, f = feet[c];
                    if (f == int.MinValue) { bin[c] = hvf[c] = exposure[c] = NoData; continue; }
                    int b = Math.Min(Bins - 2, (f - min) / binFeet);
                    int above = Math.Max(0, f - floor[c]);
                    int h = Math.Min(254, (above + 5) / 10);
                    bin[c] = (byte)b;
                    hvf[c] = (byte)h;
                    present[b * Bands + BandOf(h)] = true;
                    if (i >= core.X0 && i < core.X1 && j >= core.Y0 && j < core.Y1) { binHvfSum[b] += above; binHvfCount[b]++; }
                    int x0 = Math.Max(0, i - 3), x1 = Math.Min(width, i + 4), y0 = Math.Max(0, j - 3), y1 = Math.Min(depth, j + 4);
                    long s = Area(sum, width, x0, y0, x1, y1);
                    int m = (int)Area(cnt, width, x0, y0, x1, y1);
                    long rise = m == 0 ? 0 : f - WeatherMath.DivRound(s, m);
                    int cls = 0;
                    while (cls < 7 && above >= t.ExposureHvfFeet[cls]) cls++;
                    if (rise >= t.ExposureRiseFeet) cls++;
                    else if (rise <= -t.ExposureRiseFeet) cls--;
                    exposure[c] = (byte)WeatherMath.Clamp(cls, 0, 7);
                }

            // The reference pixel's hvf: the mean over the 1 km around the site's centre.
            long refSum = 0;
            int refCount = 0;
            if (reference >= 0 && reference < n)
            {
                int ri = reference % width, rj = reference / width;
                for (int j = Math.Max(0, rj - RefRadius); j <= Math.Min(depth - 1, rj + RefRadius); j++)
                    for (int i = Math.Max(0, ri - RefRadius); i <= Math.Min(width - 1, ri + RefRadius); i++)
                        if (hvf[j * width + i] != NoData) { refSum += hvf[j * width + i] * 10; refCount++; }
            }
            int hvfRef = refCount == 0 ? 0 : (int)WeatherMath.DivRound(refSum, refCount);

            var (cw, cd, coarse) = BlockMeans(feet, width, depth, CoarseCells);
            var crest = BuildCrest(coarse, cw, cd, width, depth, feet, core, t);
            var (sw, sd, steer) = BlockMeans(feet, width, depth, SteerCells);
            var steerHvf = BlockHvf(hvf, width, depth, sw, sd);
            var (axis, weight, speed) = Steering(steer, steerHvf, sw, sd, t);
            var binHvf = new short[Bins];
            for (int b = 0; b < Bins; b++)
                binHvf[b] = binHvfCount[b] == 0 ? (short)-1 : (short)Math.Min(short.MaxValue, WeatherMath.DivRound(binHvfSum[b], binHvfCount[b]));
            return new WeatherTerrain(width, depth, originXMm, originYMm, min, binFeet, bin, hvf, exposure, present, hvfRef, cw, cd, crest, sw, sd,
                                      axis, weight, speed, convergenceTenths, binHvf, t);
        }

        static long Area(long[] s, int width, int x0, int y0, int x1, int y1)
        {
            int w = width + 1;
            return s[y1 * w + x1] - s[y0 * w + x1] - s[y1 * w + x0] + s[y0 * w + x0];
        }

        static long Area(int[] s, int width, int x0, int y0, int x1, int y1)
        {
            int w = width + 1;
            return (long)s[y1 * w + x1] - s[y0 * w + x1] - s[y1 * w + x0] + s[y0 * w + x0];
        }

        /// <summary>The minimum over [k − r, k + r] for each k (van Herk–Gil-Werman: block prefix and suffix minima, O(n)).</summary>
        static void RunningMin(int[] v, int count, int r, int[] result, int[] pre, int[] suf)
        {
            int w = 2 * r + 1;
            for (int k = 0; k < count; k++) pre[k] = k % w == 0 ? v[k] : Math.Min(pre[k - 1], v[k]);
            for (int k = count - 1; k >= 0; k--) suf[k] = k == count - 1 || (k + 1) % w == 0 ? v[k] : Math.Min(suf[k + 1], v[k]);
            for (int k = 0; k < count; k++)
            {
                int lo = Math.Max(0, k - r), hi = Math.Min(count - 1, k + r);
                // [lo, hi] spans at most two blocks: the suffix of lo's block and the prefix of hi's. Inside one block (the
                // ends of a line) a window from the block's start is a prefix, and one to its end (or the line's) a suffix.
                if (lo / w != hi / w) result[k] = Math.Min(suf[lo], pre[hi]);
                else if (lo % w == 0) result[k] = pre[hi];
                else if (hi == count - 1 || (hi + 1) % w == 0) result[k] = suf[lo];
                else result[k] = Scan(v, lo, hi);
            }
        }

        static int Scan(int[] v, int lo, int hi)
        {
            int m = int.MaxValue;
            for (int k = lo; k <= hi; k++) if (v[k] < m) m = v[k];
            return m;
        }

        /// <summary>The mean elevation (ft) of each block of <paramref name="size"/>² cells, <see cref="int.MinValue"/> where none has data.</summary>
        static (int W, int D, int[] Means) BlockMeans(int[] feet, int width, int depth, int size)
        {
            int bw = (width + size - 1) / size, bd = (depth + size - 1) / size;
            var s = new long[bw * bd];
            var c = new int[bw * bd];
            for (int j = 0; j < depth; j++)
                for (int i = 0; i < width; i++)
                {
                    int f = feet[j * width + i];
                    if (f == int.MinValue) continue;
                    int k = j / size * bw + i / size;
                    s[k] += f;
                    c[k]++;
                }
            var m = new int[bw * bd];
            for (int k = 0; k < m.Length; k++) m[k] = c[k] == 0 ? int.MinValue : (int)WeatherMath.DivRound(s[k], c[k]);
            return (bw, bd, m);
        }

        static int[] BlockHvf(byte[] hvf, int width, int depth, int bw, int bd)
        {
            var s = new long[bw * bd];
            var c = new int[bw * bd];
            for (int j = 0; j < depth; j++)
                for (int i = 0; i < width; i++)
                {
                    byte h = hvf[j * width + i];
                    if (h == NoData) continue;
                    int k = j / SteerCells * bw + i / SteerCells;
                    s[k] += h * 10;
                    c[k]++;
                }
            var m = new int[bw * bd];
            for (int k = 0; k < m.Length; k++) m[k] = c[k] == 0 ? 0 : (int)WeatherMath.DivRound(s[k], c[k]);
            return m;
        }

        /// <summary>WX-35 v3's crest lattice, built once.</summary>
        sealed class Crest
        {
            public short[] Feet, Metres, Low;
            public int W, D;
            public int[] Mean;
        }

        /// <summary>
        /// WX-35 v3: per arrival octant, march upwind over the 512 m blocks to 3 km from each block (its mean height is where the
        /// air arrives): the highest block on the way, its distance (to its near edge, so the block just behind a crest is just
        /// behind it), the lowest. Where nothing lies upwind (the edge) the crest is the block itself, far off; a block without
        /// heights takes its neighbours'. Then onto the 64 m lattice by the curved filter (cubic B-spline), and the raw factor's
        /// mean over the box's own cells, each at its own height, so the factor moves snow and never adds any.
        /// </summary>
        static Crest BuildCrest(int[] z, int cw, int cd, int width, int depth, int[] feet, (int X0, int Y0, int X1, int Y1) core, WeatherTerrainTuning t)
        {
            int cells = cw * cd, stepMetres = CoarseCells * CellMetres, far = 2 * t.SpilloverReachMetres + 1000;   // past the shadow's full depth
            int fw = (width + FineCells - 1) / FineCells, fd = (depth + FineCells - 1) / FineCells, fine = fw * fd;
            var crest = new Crest { W = fw, D = fd, Feet = new short[8 * fine], Metres = new short[8 * fine], Low = new short[8 * fine], Mean = new int[8] };
            // Blocks without heights take their neighbours' mean (a few passes), so the curved filter never reads a hole.
            var zf = (int[])z.Clone();
            for (int pass = 0; pass < 64; pass++)
            {
                bool any = false;
                var next = (int[])zf.Clone();
                for (int j = 0; j < cd; j++)
                    for (int i = 0; i < cw; i++)
                    {
                        if (zf[j * cw + i] != int.MinValue) continue;
                        long s = 0; int n = 0;
                        if (i > 0 && zf[j * cw + i - 1] != int.MinValue) { s += zf[j * cw + i - 1]; n++; }
                        if (i + 1 < cw && zf[j * cw + i + 1] != int.MinValue) { s += zf[j * cw + i + 1]; n++; }
                        if (j > 0 && zf[(j - 1) * cw + i] != int.MinValue) { s += zf[(j - 1) * cw + i]; n++; }
                        if (j + 1 < cd && zf[(j + 1) * cw + i] != int.MinValue) { s += zf[(j + 1) * cw + i]; n++; }
                        if (n > 0) next[j * cw + i] = (int)WeatherMath.DivRound(s, n); else any = true;
                    }
                zf = next;
                if (!any) break;
            }
            for (int c = 0; c < cells; c++) if (zf[c] == int.MinValue) zf[c] = 0;

            var hi = new int[cells]; var at = new int[cells]; var lo = new int[cells];
            var rowTmp = new long[fw * cd];
            for (int o = 0; o < 8; o++)
            {
                // A diagonal step is 1.41 cells long: fewer of them reach the same 3 km (5 along the grid, 4 on a diagonal).
                int diag = Dx[o] != 0 && Dy[o] != 0 ? 1414 : 1000;
                int steps = UpwindMetres * 1000 / (stepMetres * diag);
                for (int j = 0; j < cd; j++)
                    for (int i = 0; i < cw; i++)
                    {
                        int c = j * cw + i, zc = zf[c];
                        int highest = int.MinValue, highestAt = 0, lowest = int.MaxValue;
                        for (int s = 1; s <= steps; s++)
                        {
                            int x = i + Dx[o] * s, y = j + Dy[o] * s;
                            if (x < 0 || y < 0 || x >= cw || y >= cd) break;
                            if (z[y * cw + x] == int.MinValue) break;
                            int zu = zf[y * cw + x];
                            if (zu > highest) { highest = zu; highestAt = (2 * s - 1) * stepMetres * diag / 2000; }
                            if (zu < lowest) lowest = zu;
                        }
                        if (highest == int.MinValue) { hi[c] = zc; at[c] = far; lo[c] = zc; }
                        else { hi[c] = highest; at[c] = highestAt; lo[c] = Math.Min(lowest, zc); }
                    }
                Upsample(hi, cw, cd, fw, fd, rowTmp, crest.Feet, o * fine);
                Upsample(at, cw, cd, fw, fd, rowTmp, crest.Metres, o * fine);
                Upsample(lo, cw, cd, fw, fd, rowTmp, crest.Low, o * fine);
            }

            // The raw factor's mean over the box's cells, each at its own height.
            for (int o = 0; o < 8; o++)
            {
                long sum = 0; long count = 0;
                for (int j = Math.Max(0, core.Y0); j < Math.Min(depth, core.Y1); j++)
                    for (int i = Math.Max(0, core.X0); i < Math.Min(width, core.X1); i++)
                    {
                        int f = feet[j * width + i];
                        if (f == int.MinValue) continue;
                        sum += RawAt(crest.Feet, crest.Metres, crest.Low, fw, fd, o, i, j, f, t.Windward, t.Spillover, t.Shadow,
                                     t.SpilloverReachMetres, t.ClimbFeet, t.BarrierFeet);
                        count++;
                    }
                crest.Mean[o] = count == 0 ? 1000 : (int)Math.Max(1, WeatherMath.DivRound(sum, count));
            }
            return crest;
        }

        /// <summary>
        /// The 512 m blocks onto the 64 m lattice by the uniform cubic B-spline (the snow's curved filter): node m's centre is
        /// cell 8m + 4, block k's 64k + 32, so the node sits at (2m − 7) ÷ 16 blocks. Integer weights over 6 × 16³; the ends
        /// repeat the edge block. Rows first (into <paramref name="tmp"/>), then columns, rounded once.
        /// </summary>
        static void Upsample(int[] v, int cw, int cd, int fw, int fd, long[] tmp, short[] dest, int offset)
        {
            const long Sum = 6 * 16 * 16 * 16;
            for (int j = 0; j < cd; j++)
                for (int m = 0; m < fw; m++)
                {
                    int p = 2 * m - 7, k0 = FloorDiv(p, 16), q = p - 16 * k0;
                    long s = 0;
                    for (int a = 0; a < 4; a++)
                        s += Weight(a, q) * (long)v[j * cw + WeatherMath.Clamp(k0 - 1 + a, 0, cw - 1)];
                    tmp[j * fw + m] = s;
                }
            for (int n = 0; n < fd; n++)
            {
                int p = 2 * n - 7, k0 = FloorDiv(p, 16), q = p - 16 * k0;
                for (int m = 0; m < fw; m++)
                {
                    long s = 0;
                    for (int a = 0; a < 4; a++)
                        s += Weight(a, q) * tmp[WeatherMath.Clamp(k0 - 1 + a, 0, cd - 1) * fw + m];
                    dest[offset + n * fw + m] = (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, WeatherMath.DivRound(s, Sum * Sum)));
                }
            }
        }

        /// <summary>The cubic B-spline's weight for tap <paramref name="a"/> (0–3) at q ÷ 16 past tap 1, over 6 × 16³.</summary>
        static long Weight(int a, int q)
        {
            long u = 16 - q, q2 = (long)q * q, q3 = q2 * q;
            switch (a)
            {
                case 0: return u * u * u;
                case 1: return 3 * q3 - 6 * 16 * q2 + 4 * 16 * 16 * 16;
                case 2: return -3 * q3 + 3 * 16 * q2 + 3 * 16 * 16 * q + 16 * 16 * 16;
                default: return q3;
            }
        }

        /// <summary>
        /// WX-39's steering over the 96 m grid, a static terrain-following estimate (no flow solver).
        /// - **The valley's axis and weight (one per cell, whatever the wind):** across each of the four lines through the cell
        ///   the valley's depth is how far the ground rises on *both* sides (the mean of the two changes about 400 m out, so a
        ///   plain hillside, up one side and down the other, is no valley). The axis is the line across the deepest; the
        ///   weight is that depth less the depth along the axis, over <see cref="WeatherTerrainTuning.ValleyFeet"/>, times
        ///   the cell's shelter (low hvf). <see cref="Axis"/> points down the valley (the lower end), which settles a wind
        ///   blowing exactly across it. The hour's turn is <see cref="TurnDegrees"/>, from its exact direction.
        /// - **The speed, per arrival octant:** faster over a rise and slower in a hollow, and down to
        ///   <see cref="WeatherTerrainTuning.CrestLee"/> behind ground standing <see cref="WeatherTerrainTuning.LeeFeet"/>
        ///   above the cell within about 400 m upwind.
        /// Diagonals are sampled 3 cells out (407 m) and the others 4 (384 m), so no direction reaches further than another.
        /// </summary>
        static (byte[] Axis, byte[] Weight, byte[] Speed) Steering(int[] z, int[] hvf, int sw, int sd, WeatherTerrainTuning t)
        {
            int cells = sw * sd;
            var axisOut = new byte[cells];
            var weightOut = new byte[cells];
            var speed = new byte[8 * cells];
            var change = new int[8];
            for (int j = 0; j < sd; j++)
                for (int i = 0; i < sw; i++)
                {
                    int c = j * sw + i, zc = z[c];
                    if (zc == int.MinValue)
                    {
                        for (int o = 0; o < 8; o++) speed[o * cells + c] = 100;
                        continue;
                    }
                    long ring = 0;
                    int ringCount = 0;
                    for (int d = 0; d < 8; d++)
                    {
                        int r = Reach(d);
                        int x = i + Dx[d] * r, y = j + Dy[d] * r;
                        // Past the grid's edge or over a hole there is nothing to read: the ground counts as level there.
                        int zd = x < 0 || y < 0 || x >= sw || y >= sd ? int.MinValue : z[y * sw + x];
                        change[d] = zd == int.MinValue ? 0 : zd - zc;
                        if (zd != int.MinValue) { ring += zd; ringCount++; }
                    }
                    int deepest = 0, across = 0;
                    for (int q = 0; q < 4; q++)
                    {
                        int v = (change[q] + change[q + 4]) / 2;
                        if (v > across) { across = v; deepest = q; }
                    }
                    int axis = (deepest + 2) % 4;
                    int along = Math.Max(0, (change[axis] + change[axis + 4]) / 2);
                    int depth = Math.Max(0, across - along);
                    int shelter = Math.Max(0, 1000 - hvf[c] * 1000 / t.ShelterFeet);
                    int weight = (int)((long)Math.Min(1000, depth * 1000 / t.ValleyFeet) * shelter / 1000);
                    // Down the valley: the axis end the ground falls toward (the first of the two when it's level).
                    axisOut[c] = (byte)(change[axis + 4] < change[axis] ? axis + 4 : axis);
                    weightOut[c] = (byte)((weight + 5) / 10);
                    int hill = ringCount == 0 ? 0 : zc - (int)(ring / ringCount);
                    int baseSpeed = 1000 + t.HillSpeed * WeatherMath.Clamp(hill, -300, 300) / 300;
                    for (int o = 0; o < 8; o++)
                    {
                        int s = baseSpeed;
                        for (int q = 1, r = Reach(o); q <= r; q++)
                        {
                            int x = i + Dx[o] * q, y = j + Dy[o] * q;
                            if (x < 0 || y < 0 || x >= sw || y >= sd) break;
                            int zu = z[y * sw + x];
                            if (zu != int.MinValue && zu - zc >= t.LeeFeet) { s = s * t.CrestLee / 1000; break; }
                        }
                        speed[o * cells + c] = (byte)WeatherMath.Clamp((s + 5) / 10, 50, 150);
                    }
                }
            return (axisOut, weightOut, speed);
        }

        /// <summary>How many 96 m cells out a direction reads: 4 along the grid (384 m), 3 on a diagonal (407 m).</summary>
        static int Reach(int direction) => (direction & 1) == 0 ? 4 : 3;

        /// <summary>
        /// WX-39: how far the terrain turns a wind (°, + clockwise), from a steering cell's <see cref="Axis"/> and
        /// <see cref="TurnWeight"/> and the wind's heading on the grid (the way it blows *to*, °). The wind turns toward the
        /// nearer end of the valley's axis, by the cell's weight; blowing exactly across, it turns down the valley. From the
        /// hour's own direction, never blended between octants, so it is continuous everywhere but that one line.
        /// </summary>
        public static int TurnDegrees(int axis, int weightPercent, int headingDegrees)
        {
            if (weightPercent <= 0) return 0;
            int down = AngleDiff(axis * 45, headingDegrees), up = AngleDiff(axis * 45 + 180, headingDegrees);
            int target = Math.Abs(down) <= Math.Abs(up) ? down : up;
            return WeatherMath.Clamp(target * weightPercent / 100, -90, 90);
        }

        /// <summary>The signed turn (°, −180…180, + clockwise) from the wind's heading to an axis direction.</summary>
        static int AngleDiff(int axis, int heading) => ((axis - heading) % 360 + 540) % 360 - 180;

        static long FloorDiv(long a, long b) => a >= 0 ? a / b : -((-a + b - 1) / b);
    }
}
