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
            ExposureHvfFeet = exposureHvfFeet; ExposureRiseFeet = exposureRiseFeet;
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
    /// - Per 512 m cell and arrival octant: the orographic factor (‰), from the upwind terrain within 3 km, normalised so its
    ///   mean over the box is 1.0 (the Daymet lattice is the box's pixels at 1 km, so it has the same mean).
    /// - Per 96 m cell and octant: the steering's turn (2° units) and speed factor (0.01).
    /// </summary>
    public sealed class WeatherTerrain
    {
        public const int CellMetres = Snow.SurfaceGrid.CellMetres, MinBinFeet = 33, Bins = 256, Bands = 16, BandFeet = 32, NoData = 255;
        public const int CoarseCells = 64, SteerCells = 12;
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
        /// <summary>[octant × coarse cells]: the orographic factor (‰). Octant 0 is a wind from the north, clockwise.</summary>
        public readonly short[] Orographic;
        /// <summary>[octant × steer cells]: the turn (2° units, + clockwise) and the speed factor (0.01).</summary>
        public readonly sbyte[] Turn;
        public readonly byte[] Speed;
        /// <summary>A content hash over every field.</summary>
        public readonly ulong Hash;

        public int Cells => Width * Depth;

        /// <summary>The cell holding a point in the design's frame (mm from the site's centre), or −1 outside the grid.</summary>
        public int CellAt(long xMm, long yMm)
        {
            long i = FloorDiv(xMm - OriginXMm, CellMetres * 1000), j = FloorDiv(yMm - OriginYMm, CellMetres * 1000);
            return i < 0 || j < 0 || i >= Width || j >= Depth ? -1 : (int)(j * Width + i);
        }

        /// <summary>A cell's bin centre (ft).</summary>
        public int BinCentreFeet(int bin) => MinFeet + bin * BinFeet + BinFeet / 2;

        /// <summary>A cell's pool band (WX-02 v2: 32 ft steps over 0–512 ft, the last one everything above).</summary>
        public static int BandOf(int hvfTenFeet) => Math.Min(Bands - 1, hvfTenFeet * 10 / BandFeet);

        WeatherTerrain(int width, int depth, int originX, int originY, int minFeet, int binFeet, byte[] bin, byte[] hvf, byte[] exposure, bool[] present, int hvfRef,
                       int cw, int cd, short[] oro, int sw, int sd, sbyte[] turn, byte[] speed)
        {
            Width = width; Depth = depth; OriginXMm = originX; OriginYMm = originY; MinFeet = minFeet; BinFeet = binFeet;
            Bin = bin; Hvf = hvf; Exposure = exposure; Present = present; HvfRefFeet = hvfRef;
            CoarseWidth = cw; CoarseDepth = cd; Orographic = oro; SteerWidth = sw; SteerDepth = sd; Turn = turn; Speed = speed;
            ulong h = 14695981039346656037UL;
            void Mix(long v) => h = (h ^ (ulong)v) * 1099511628211UL;
            Mix(width); Mix(depth); Mix(originX); Mix(originY); Mix(minFeet); Mix(binFeet); Mix(hvfRef);
            foreach (byte v in bin) Mix(v);
            foreach (byte v in hvf) Mix(v);
            foreach (byte v in exposure) Mix(v);
            foreach (short v in oro) Mix(v);
            foreach (sbyte v in turn) Mix(v);
            foreach (byte v in speed) Mix(v);
            Hash = h;
        }

        /// <summary>Octant <paramref name="o"/>'s upwind unit step (east, north): the direction the wind comes from.</summary>
        static readonly int[] Dx = { 0, 1, 1, 1, 0, -1, -1, -1 }, Dy = { 1, 1, 0, -1, -1, -1, 0, 1 };

        /// <summary>
        /// Builds the static inputs from the 8 m grid's elevations (ft, <see cref="int.MinValue"/> where there's no data).
        /// <paramref name="core"/> is the box in cells (x0, y0, x1, y1, ends exclusive), <paramref name="reference"/> the cell under
        /// the site's centre (the reference pixel).
        /// </summary>
        public static WeatherTerrain Build(int width, int depth, int[] feet, int originXMm, int originYMm,
                                           (int X0, int Y0, int X1, int Y1) core, int reference, WeatherTerrainTuning t)
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
            for (int j = 0; j < depth; j++)
            {
                for (int i = 0; i < width; i++) { int f = feet[j * width + i]; line[i] = f == int.MinValue ? int.MaxValue : f; }
                RunningMin(line, width, FloorRadius, outLine);
                Array.Copy(outLine, 0, floor, j * width, width);
            }
            for (int i = 0; i < width; i++)
            {
                for (int j = 0; j < depth; j++) line[j] = floor[j * width + i];
                RunningMin(line, depth, FloorRadius, outLine);
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
            var oro = BuildOrographic(coarse, cw, cd, core, t);
            var (sw, sd, steer) = BlockMeans(feet, width, depth, SteerCells);
            var steerHvf = BlockHvf(hvf, width, depth, sw, sd);
            var (turn, speed) = Steering(steer, steerHvf, sw, sd, t);
            return new WeatherTerrain(width, depth, originXMm, originYMm, min, binFeet, bin, hvf, exposure, present, hvfRef, cw, cd, oro, sw, sd, turn, speed);
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
        static void RunningMin(int[] v, int count, int r, int[] result)
        {
            int w = 2 * r + 1;
            var pre = new int[count];
            var suf = new int[count];
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
            for (int k = 0; k < m.Length; k++) m[k] = c[k] == 0 ? 0 : (int)(s[k] / c[k]);
            return m;
        }

        /// <summary>
        /// WX-35 v2's orographic factor per arrival octant over the 512 m grid: march upwind to 3 km. A cell the air climbs into
        /// gets up to +<see cref="WeatherTerrainTuning.Windward"/>; within the spillover reach past a higher crest, up to
        /// +<see cref="WeatherTerrainTuning.Spillover"/> (never below 1.0); further behind a barrier, the shadow, down to
        /// −<see cref="WeatherTerrainTuning.Shadow"/> as the barrier grows. Then each octant's mean over the box is 1.0.
        /// </summary>
        static short[] BuildOrographic(int[] z, int cw, int cd, (int X0, int Y0, int X1, int Y1) core, WeatherTerrainTuning t)
        {
            int cells = cw * cd, stepMetres = CoarseCells * CellMetres, steps = UpwindMetres / stepMetres;
            var oro = new short[8 * cells];
            var raw = new int[cells];
            for (int o = 0; o < 8; o++)
            {
                int diag = Dx[o] != 0 && Dy[o] != 0 ? 1414 : 1000;
                for (int j = 0; j < cd; j++)
                    for (int i = 0; i < cw; i++)
                    {
                        int c = j * cw + i, zc = z[c];
                        if (zc == int.MinValue) { raw[c] = 1000; continue; }
                        int highest = int.MinValue, highestAt = 0, lowest = int.MaxValue;
                        for (int s = 1; s <= steps; s++)
                        {
                            int x = i + Dx[o] * s, y = j + Dy[o] * s;
                            if (x < 0 || y < 0 || x >= cw || y >= cd) break;
                            int zu = z[y * cw + x];
                            if (zu == int.MinValue) break;
                            if (zu > highest) { highest = zu; highestAt = s * stepMetres * diag / 1000; }
                            if (zu < lowest) lowest = zu;
                        }
                        int f = 1000;
                        if (highest == int.MinValue) f = 1000;
                        else if (highest <= zc)
                            f = 1000 + (int)((long)t.Windward * Math.Min(t.ClimbFeet, zc - lowest) / t.ClimbFeet);
                        else if (highestAt <= t.SpilloverReachMetres)
                            f = 1000 + t.Spillover * Math.Max(0, t.SpilloverReachMetres - highestAt) / Math.Max(1, t.SpilloverReachMetres);
                        else
                            f = 1000 - (int)((long)t.Shadow * Math.Min(t.BarrierFeet, highest - zc) / t.BarrierFeet);
                        raw[c] = f;
                    }
                // Normalise over the box's cells (the lattice's pixels): the factor moves snow, never adds any.
                long sum = 0;
                int count = 0;
                for (int j = 0; j < cd; j++)
                    for (int i = 0; i < cw; i++)
                    {
                        int cx = i * CoarseCells + CoarseCells / 2, cy = j * CoarseCells + CoarseCells / 2;
                        if (cx < core.X0 || cx >= core.X1 || cy < core.Y0 || cy >= core.Y1 || z[j * cw + i] == int.MinValue) continue;
                        sum += raw[j * cw + i];
                        count++;
                    }
                long mean = count == 0 ? 1000 : Math.Max(1, sum / count);
                for (int c = 0; c < cells; c++) oro[o * cells + c] = (short)WeatherMath.Clamp(WeatherMath.DivRound(raw[c] * 1000L, mean), 100, 3000);
            }
            return oro;
        }

        /// <summary>
        /// WX-39's steering per arrival octant over the 96 m grid, a static terrain-following estimate (no flow solver): the
        /// wind turns toward the axis of a valley (the pair of opposite directions the ground changes least along, 384 m out),
        /// as much as the valley is deep across it and the cell is sheltered (low hvf); it speeds up over a rise and slows in
        /// a hollow, and drops to <see cref="WeatherTerrainTuning.CrestLee"/> behind a rise upwind.
        /// </summary>
        static (sbyte[] Turn, byte[] Speed) Steering(int[] z, int[] hvf, int sw, int sd, WeatherTerrainTuning t)
        {
            const int R = 4;
            int cells = sw * sd;
            var turn = new sbyte[8 * cells];
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
                        int x = Math.Max(0, Math.Min(sw - 1, i + Dx[d] * R)), y = Math.Max(0, Math.Min(sd - 1, j + Dy[d] * R));
                        int zd = z[y * sw + x];
                        change[d] = zd == int.MinValue ? 0 : zd - zc;
                        if (zd != int.MinValue) { ring += zd; ringCount++; }
                    }
                    // The valley's axis: the opposite pair the ground changes least along; its depth: how much more it changes across.
                    int axis = 0, along = int.MaxValue, across = 0;
                    for (int k = 0; k < 4; k++)
                    {
                        int a = (Math.Abs(change[k]) + Math.Abs(change[k + 4])) / 2;
                        if (a < along) { along = a; axis = k; }
                        if (a > across) across = a;
                    }
                    int depth = Math.Max(0, across - along);
                    int shelter = Math.Max(0, 1000 - hvf[c] * 1000 / t.ShelterFeet);
                    int weight = (int)((long)Math.Min(1000, depth * 1000 / t.ValleyFeet) * shelter / 1000);
                    int hill = ringCount == 0 ? 0 : zc - (int)(ring / ringCount);
                    int baseSpeed = 1000 + t.HillSpeed * WeatherMath.Clamp(hill, -300, 300) / 300;
                    for (int o = 0; o < 8; o++)
                    {
                        // The wind blows toward o + 180°; the axis direction nearer to that is the one it turns to.
                        int to = (o * 45 + 180) % 360, a1 = axis * 45, a2 = a1 + 180;
                        int d1 = AngleDiff(a1, to), d2 = AngleDiff(a2, to);
                        int target = Math.Abs(d1) <= Math.Abs(d2) ? d1 : d2;
                        int deg = WeatherMath.Clamp(target * weight / 1000, -90, 90);
                        turn[o * cells + c] = (sbyte)((deg >= 0 ? deg + 1 : deg - 1) / 2);
                        // The lee: the highest ground up to 384 m upwind standing well above the cell.
                        int s = baseSpeed;
                        for (int k = 1; k <= R; k++)
                        {
                            int x = i + Dx[o] * k, y = j + Dy[o] * k;
                            if (x < 0 || y < 0 || x >= sw || y >= sd) break;
                            int zu = z[y * sw + x];
                            if (zu != int.MinValue && zu - zc >= t.LeeFeet) { s = s * t.CrestLee / 1000; break; }
                        }
                        speed[o * cells + c] = (byte)WeatherMath.Clamp((s + 5) / 10, 50, 150);
                    }
                }
            return (turn, speed);
        }

        /// <summary>The signed turn (°, −180…180, + clockwise) from the wind's heading to an axis direction.</summary>
        static int AngleDiff(int axis, int heading) => ((axis - heading) % 360 + 540) % 360 - 180;

        static long FloorDiv(long a, long b) => a >= 0 ? a / b : -((-a + b - 1) / b);
    }
}
