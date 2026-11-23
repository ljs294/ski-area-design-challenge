using System;
using MountainPlanner.Domain.Sky;
using MountainPlanner.Domain.Snow;
using MountainPlanner.Domain.Weather;

namespace MountainPlanner.Simulation
{
    /// <summary>
    /// One face hour of the natural snow over the whole 8 m field (task 4.07 PR A2), run on the snow crew from the hour roll
    /// and landed at a fixed tick (<see cref="Pacing.SnowLandFaceMs"/> later, BG-02 v4). It reads only what it was given at
    /// the roll (the hour's weather tables, the snow's terrain inputs, the lake mask, the sun) and writes only the snow, the
    /// next visible planes and the next tile hashes. Each unit is a band of 64 rows (one row of tiles).
    ///
    /// A wet hour runs these phases:
    /// - **A**, per band: each cell's share of the hour's water (SN-05 v2, the weather's rain/snow ramp), what its canopy holds
    ///   back (SN-06 v2) and what leaves it: shed down a steep slope (SN-06 v2) or blown off a rise (SN-07 v3);
    /// - **the shed's walk**, alone: each shed slides down the fall line to the first slope it holds on and spreads as a fan;
    /// - **the drift**, per band in three passes (bands 0, 3, 6… then 1, 4, 7… then 2, 5, 8…): each drifting cell lands its
    ///   snow over SN-07 v3's footprint. A footprint reaches less than a band, so no two bands in a pass write one cell, and
    ///   integer sums are the same in any order;
    /// - **C**, per band: the snow energy (SN-08 v2, SN-09 v2, SN-10 v2, SN-12 v2: <see cref="SnowEnergy.Step"/>), then the
    ///   conditions (SN-13, SN-14 v2, SN-16 v2, SN-18 v2), the planes and the tile's hash.
    /// A dry hour runs C alone, and skips a tile with no snow (bare ground in a dry hour stays bare: nothing to do).
    /// No unit reads what another writes, so any thread count gives the same bytes.
    /// </summary>
    internal sealed class SnowHour : SnowJob
    {
        readonly SimSnow _snow;
        // ---- the hour's inputs, set at the roll ----
        WeatherFields _fields;
        WeatherTerrain _wx;
        SnowTerrain _t;
        byte[] _water;
        SnowRules _rules;
        SunVector _sun;
        int _clear, _cloud, _seconds, _faceDay, _sunA, _sunB, _sunWB;
        bool _wet;
        int _phase;
        // ---- scratch, allocated once ----
        WeatherCell[][] _rows = Array.Empty<WeatherCell[]>();
        int[] _export = Array.Empty<int>(), _import = Array.Empty<int>(), _drift = Array.Empty<int>();
        // SN-07 v3: the hour's drift footprint (cell offsets downwind and their weights, of 65,536), the same for every cell.
        readonly int[] _kx = new int[MaxKernel], _ky = new int[MaxKernel], _kw = new int[MaxKernel];
        long[] _kernelScratch = Array.Empty<long>();
        int _kn;
        bool _drifting;
        const int MaxKernel = 1024;
        bool[] _readMask = Array.Empty<bool>();
        SnowLedger[] _bandLedger = Array.Empty<SnowLedger>();
        bool[] _bandDrifts = Array.Empty<bool>();
        // SN-06 v3: the mean water (µm) on each 128 m block's land at the hour's start, so a run-out fan's cone stays in scale.
        int[] _blockMean = Array.Empty<int>();
        int _blocksX, _blocksY;
        const int Block = 16;
        SnowLedger _scatterLedger;

        /// <summary>4.07 B1 part 2a: the trails' fine snow (null: none), set by the world before each launch, and SN-30 v2's
        /// packed floor. Each 8 m cell with a block shares its hour with it (<see cref="FineHour"/>).</summary>
        internal FineSnow Fine;
        internal int PackedFloor = FineSnow.WeightOne;
        int[][] _fineWeights = Array.Empty<int[]>();

        /// <summary>What the hour did, summed over the bands (read by the world once the job is done).</summary>
        internal SnowLedger Ledger;
        /// <summary>The water on the field at the hour's end (µm).</summary>
        internal long WaterOnField;

        internal SnowHour(SimSnow snow) { _snow = snow; }

        /// <summary>The roll: takes the hour's inputs and starts the job.</summary>
        internal void Launch(WeatherFields fields, WeatherTerrain wx, SnowTerrain terrain, byte[] water, in SnowRules rules,
                             in SunVector sun, int clearness, int cloud, int seconds, int faceDay, int threads)
        {
            _fields = fields; _wx = wx; _t = terrain; _water = water; _rules = rules;
            _sun = sun; _clear = sun.Up > 0 ? clearness : 0; _cloud = cloud; _seconds = seconds; _faceDay = faceDay;
            _wet = fields.LiquidAtZref > 0;
            SunDirections(sun, out _sunA, out _sunB, out _sunWB);
            int bands = _snow.TilesY, tiles = _snow.TilesX * _snow.TilesY, n = _snow.Width * _snow.Height;
            if (_rows.Length != bands)
            {
                _rows = new WeatherCell[bands][];
                for (int b = 0; b < bands; b++) _rows[b] = new WeatherCell[_snow.Width];
                _bandLedger = new SnowLedger[bands];
                _bandDrifts = new bool[bands];
                _blocksX = (_snow.Width + Block - 1) / Block; _blocksY = (_snow.Height + Block - 1) / Block;
                _blockMean = new int[_blocksX * _blocksY];
                _readMask = new bool[tiles];
            }
            if (_wet && _export.Length != n) { _export = new int[n]; _import = new int[n]; _drift = new int[n]; }
            if (_wet)
            {
                int to = ((_wx.GridDegrees(fields.HourDirection) + 180) % 360 + 360) % 360;
                int side = 2 * (rules.DriftSteps + 3) + 1;
                if (_kernelScratch.Length < side * side) _kernelScratch = new long[side * side];
                _kn = DriftKernel(to, rules.DriftSteps, _kx, _ky, _kw, _kernelScratch);
            }
            _drifting = false;
            for (int b = 0; b < bands; b++) { _bandLedger[b] = default; _bandDrifts[b] = false; }
            _scatterLedger = default;
            for (int k = 0; k < tiles; k++) _readMask[k] = _wet || _snow.TileSnowy[k];
            if (Fine != null && (_fineWeights.Length != bands || _fineWeights[0].Length != Fine.BlockCells))
            {
                _fineWeights = new int[bands][];
                for (int b = 0; b < bands; b++) _fineWeights[b] = new int[Fine.BlockCells];
            }
            Threads = Math.Max(1, threads);
            Units = bands;
            _phase = _wet ? 0 : 1;
            SnowCrew.Start(this);
        }

        internal override void RunUnit(int band)
        {
            switch (_phase)
            {
                case 0: PassA(band); break;
                case 2: case 3: case 4: if (band % 3 == _phase - 2) PassDrift(band); break;
                default: PassC(band); break;
            }
        }

        internal override bool PhaseDone()
        {
            if (_phase == 0)
            {
                ShedScatter();
                // The drift's two passes run only in an hour some cell drifts.
                for (int b = 0; b < _bandDrifts.Length && !_drifting; b++) _drifting |= _bandDrifts[b];
                _phase = _drifting ? 2 : 1;
                SnowCrew.Run(this);
                return false;
            }
            if (_phase >= 2)
            {
                _phase = _phase == 4 ? 1 : _phase + 1;
                SnowCrew.Run(this);
                return false;
            }
            var sum = _scatterLedger;
            long water = 0;
            for (int b = 0; b < _bandLedger.Length; b++) { sum.Add(_bandLedger[b]); water += _bandLedger[b].End; }
            Ledger = sum;
            WaterOnField = water;
            return true;
        }

        // ================= the cell's water (A and C read it alike) =================

        void Incoming(in WeatherCell wc, int canopy, out int water, out int snow, out int rain, out int held)
        {
            water = Math.Max(0, wc.LiquidUm);   // µm, unrounded: no 0.01 in step between neighbours
            snow = water > 0 ? (int)((long)water * wc.SnowPermille / 1000) : 0;
            rain = water - snow;
            held = snow > 0 && canopy > 0 ? (int)((long)snow * _rules.CanopyHolds * canopy / 255_000) : 0;
            snow -= held;
        }

        bool Lake(int c) => _water != null && _water[c] != 0;

        bool OffMap(int c) => _wx.Bin[c] == WeatherTerrain.NoData;

        // ================= A: what leaves each cell =================

        void PassA(int band)
        {
            int w = _snow.Width, y0 = band * SimSnow.TileCells, y1 = Math.Min(_snow.Height, y0 + SimSnow.TileCells);
            BlockMeans(y0, y1);
            var row = _rows[band];
            ref var L = ref _bandLedger[band];
            var t = _t;
            for (int j = y0; j < y1; j++)
            {
                _fields.ReadRowsSmooth(j, j + 1, row, t.BinPlace, t.Exposure, t.Canopy);
                for (int i = 0; i < w; i++)
                {
                    int c = j * w + i;
                    _export[c] = 0; _import[c] = 0; _drift[c] = 0;
                    ref var wc = ref row[i];
                    if (!wc.Known) continue;
                    Incoming(wc, t.Canopy[c], out int water, out int snow, out _, out int held);
                    L.Fell += water;
                    if (Lake(c)) { L.ToWater += water; continue; }
                    L.Canopy += held;
                    if (snow <= 0) continue;
                    // SN-06 v2: a steep slope sheds from ShedStartGrade, all of it at ShedFullGrade.
                    int shed = (int)((long)snow * ShedShare(c) / 1000);
                    // SN-07 v3: a rise gives a share of what's left in a strong wind, more the higher it stands over the cells
                    // round it (a ramp from level to twice the exposed rise, so no rise is a line between giving and not).
                    int drift = 0, give = Give(c);
                    if (give > 0 && wc.Wind > _rules.DriftWind)
                    {
                        long over = Math.Min(1000L, (long)(wc.Wind - _rules.DriftWind) * 1000 / _rules.DriftWindSpan);
                        drift = (int)((long)(snow - shed) * (over * _rules.DriftShareMax / 1000) / 1000 * give / 1000);
                    }
                    int export = shed + drift;
                    if (export <= 0) continue;
                    _export[c] = export;
                    _drift[c] = drift;
                    if (drift > 0) _bandDrifts[band] = true;
                }
            }
        }

        static readonly int[] GradeTable = BuildGrades();

        /// <summary>Grade (%) by the normal's stored horizontal parts' squared length (x² + y², × 127²).</summary>
        static int[] BuildGrades()
        {
            var g = new int[2 * 127 * 127 + 1];
            for (int s = 0; s < g.Length; s++)
            {
                long up = 127L * 127 - s;
                g[s] = up <= 0 ? 100_000 : (int)Math.Min(100_000, WeatherMath.ISqrt((long)s * 10_000 * 1_000_000 / up) / 1000);
            }
            return g;
        }

        int Grade(int c) { int x = _t.NormalX[c], y = _t.NormalY[c]; return GradeTable[x * x + y * y]; }

        int ShedShare(int c)
        {
            int g = Grade(c), a = _rules.ShedStartGrade, b = _rules.ShedFullGrade;
            return g <= a ? 0 : g >= b ? 1000 : (g - a) * 1000 / (b - a);
        }

        // ================= the scatter =================

        static readonly int[] SinDeg = BuildSines();

        static int[] BuildSines()
        {
            var s = new int[360];
            for (int d = 0; d < 360; d++) s[d] = SunVector.SinMilli(d * 1000);
            return s;
        }

        void ShedScatter()
        {
            int n = _snow.Width * _snow.Height;
            for (int c = 0; c < n; c++)
            {
                int shed = _export[c] - _drift[c];
                if (shed > 0) ShedWalk(c, shed);
            }
        }

        void Deposit(int c, long amount)
        {
            if (amount <= 0) return;
            if (Lake(c)) _scatterLedger.ToWater += amount;
            else _import[c] += (int)amount;
        }

        /// <summary>Snow that slides or blows off the map's edge, or into a hole in its heights, leaves the field (the ledger's
        /// OffMap): the world goes on past the edge, so it lands there, not piled on the last cell.</summary>
        void Leave(long amount)
        {
            if (amount > 0) _scatterLedger.OffMap += amount;
        }

        /// <summary>
        /// SN-06 v2: the shed slides down the fall line (the normal's downhill octant) to the first cell under the start grade,
        /// at most the drift's reach; a lake takes it. From there it spreads over a run-out fan: along the fall line for
        /// <see cref="SnowRules.ShedSteps"/> cells, an equal share of what's still carried a step over the 3 × 3 cells round
        /// the path (weights 1-2-1), each cell taking at most <see cref="SnowRules.ShedPileMax"/> an hour in all and (SN-06 v3)
        /// only while its water stands under <see cref="SnowRules.ShedPileRatio"/> × the mean round it (<see cref="PileCap"/>);
        /// a full fan runs on down the fall line, to <see cref="RunOnSteps"/> times its length; what's left at the end lands
        /// round its last cell. So a gully under a cliff fills as a cone that spreads once it stands about three times the
        /// snow round it, never as one deep cell.
        /// </summary>
        void ShedWalk(int c, int amount)
        {
            int w = _snow.Width, h = _snow.Height, x = c % w, y = c / w, at = c, dx = 0, dy = 0;
            for (int s = 0; s < _rules.DriftSteps; s++)
            {
                if (!FallStep(at, ref dx, ref dy, false)) break;
                int nx = x + dx, ny = y + dy;
                if (nx < 0 || ny < 0 || nx >= w || ny >= h || OffMap(ny * w + nx)) { Leave(amount); return; }
                x = nx; y = ny; at = ny * w + nx;
                if (Lake(at) || Grade(at) < _rules.ShedStartGrade) break;
            }
            if (Lake(at)) { Deposit(at, amount); return; }
            long carry = amount;
            int steps = _rules.ShedSteps, most = RunOnSteps * steps;
            for (int s = 0; s < most && carry > 0; s++)
            {
                carry -= Fan(x, y, s < steps ? carry / (steps - s) : carry, true);
                if (s == most - 1 || carry <= 0 || !FallStep(at, ref dx, ref dy, true)) break;
                int nx = x + dx, ny = y + dy;
                if (nx < 0 || ny < 0 || nx >= w || ny >= h || OffMap(ny * w + nx)) { Leave(carry); return; }
                int next = ny * w + nx;
                if (Lake(next)) { Deposit(next, carry); return; }
                x = nx; y = ny; at = next;
            }
            if (carry > 0) carry -= Fan(x, y, carry, false);
            Deposit(at, carry);
        }

        /// <summary>SN-06 v3: how far a full fan runs on down the fall line (fan lengths) before what's left lands round its last cell.</summary>
        const int RunOnSteps = 8;

        /// <summary>The block means over rows [y0, y1) (whole block rows: a band is four of them).</summary>
        void BlockMeans(int y0, int y1)
        {
            int w = _snow.Width;
            var pack = _snow.Field.Pack;
            for (int by = y0 / Block; by * Block < y1; by++)
                for (int bx = 0; bx < _blocksX; bx++)
                {
                    long sum = 0; int n = 0;
                    int ya = by * Block, yb = Math.Min(y1, ya + Block), xa = bx * Block, xb = Math.Min(w, xa + Block);
                    for (int j = ya; j < yb; j++)
                        for (int i = xa; i < xb; i++)
                        {
                            int c = j * w + i;
                            if (OffMap(c) || Lake(c)) continue;
                            sum += pack[c].Swe; n++;
                        }
                    _blockMean[by * _blocksX + bx] = n == 0 ? 0 : (int)(sum / n);
                }
        }

        /// <summary>
        /// SN-06 v3: the most water a run-out fan's cell may stand at (µm): the cone's ratio × the mean water round it, read
        /// bilinearly between the 128 m blocks' centres (so the cap has no seam), and never under an hour's pile.
        /// </summary>
        long PileCap(int i, int j)
        {
            int gx = i * 2 + 1 - Block, gy = j * 2 + 1 - Block;   // half-cells from block (0, 0)'s centre
            int x0 = FloorDiv(gx, 2 * Block), y0 = FloorDiv(gy, 2 * Block);
            int ux = gx - x0 * 2 * Block, uy = gy - y0 * 2 * Block;
            int xa = Math.Max(0, Math.Min(_blocksX - 1, x0)), xb = Math.Max(0, Math.Min(_blocksX - 1, x0 + 1));
            int ya = Math.Max(0, Math.Min(_blocksY - 1, y0)), yb = Math.Max(0, Math.Min(_blocksY - 1, y0 + 1));
            long top = (long)_blockMean[ya * _blocksX + xa] * (2 * Block - ux) + (long)_blockMean[ya * _blocksX + xb] * ux;
            long bottom = (long)_blockMean[yb * _blocksX + xa] * (2 * Block - ux) + (long)_blockMean[yb * _blocksX + xb] * ux;
            long mean = (top * (2 * Block - uy) + bottom * uy) / (4L * Block * Block);
            return Math.Max(_rules.ShedPileMax, mean * _rules.ShedPileRatio / 1000);
        }

        static int FloorDiv(int a, int b) => a >= 0 ? a / b : -((-a + b - 1) / b);

        /// <summary>The fall line's step from a cell (its normal's downhill octant); where the ground is flat a fan keeps its
        /// heading (<paramref name="keep"/>), a slide stops.</summary>
        bool FallStep(int at, ref int dx, ref int dy, bool keep)
        {
            int hx = _t.NormalX[at], hy = _t.NormalY[at], ax = Math.Abs(hx), ay = Math.Abs(hy);
            int sx = ax * 5 >= ay * 2 ? Math.Sign(hx) : 0, sy = ay * 5 >= ax * 2 ? Math.Sign(hy) : 0;
            if (sx != 0 || sy != 0) { dx = sx; dy = sy; return true; }
            return keep && (dx != 0 || dy != 0);
        }

        /// <summary>Lands <paramref name="amount"/> over the 3 × 3 cells round x, y (weights 1-2-1 by 1-2-1, of 16); capped, a cell
        /// takes no more than leaves its hour's import at the pile cap; a share past the map's edge leaves it. Returns what
        /// landed or left.</summary>
        long Fan(int x, int y, long amount, bool capped)
        {
            // BG-02 v4 (the same bytes, faster): the largest share is 4/16, so under 4 µm every share rounds to nothing. A
            // walk's last µm ride its fan to the end of the run-on (most of a busy hour's fans, measured on 11 km), and each
            // of them read the pile cap nine times for nothing.
            if (amount < 4) return 0;
            int w = _snow.Width, h = _snow.Height;
            long landed = 0;
            for (int b = -1; b <= 1; b++)
            {
                int ny = y + b;
                for (int a = -1; a <= 1; a++)
                {
                    int nx = x + a;
                    long take = amount * ((2 - Math.Abs(a)) * (2 - Math.Abs(b))) / 16;
                    if (take <= 0) continue;   // a share that rounds to nothing lands and leaves nothing
                    if (ny < 0 || ny >= h || nx < 0 || nx >= w || OffMap(ny * w + nx)) { Leave(take); landed += take; continue; }
                    int n = ny * w + nx;
                    if (capped && !Lake(n))
                    {
                        // The hour's pile max first: a cell that has taken it needs no pile cap read.
                        long left = _rules.ShedPileMax - (long)_import[n];
                        if (left <= 0) continue;
                        long standing = _snow.Field.Pack[n].Swe + (long)_import[n];
                        take = Math.Min(take, Math.Max(0, Math.Min(left, PileCap(nx, ny) - standing)));
                    }
                    if (take <= 0) continue;
                    Deposit(n, take);
                    landed += take;
                }
            }
            return landed;
        }

        // ================= SN-07 v3: the drift as a gather =================

        /// <summary>How much a cell gives to the wind (‰): nothing at or below its surroundings, all of it at twice the exposed
        /// rise over the mean of the 7 × 7 cells round it (the rise in cm, the rule in dm).</summary>
        int Give(int c)
        {
            int r = _t.Rise[c], e = Math.Max(1, _rules.DriftExposedRise * 10);   // cm
            return r <= 0 ? 0 : r >= 2 * e ? 1000 : r * 1000 / (2 * e);
        }

        /// <summary>How much of what reaches a cell it keeps (‰): water and ground at or below its surroundings all of it, a
        /// rise of the exposed rise or more none, a ramp between.</summary>
        int Take(int c)
        {
            if (Lake(c)) return 1000;
            int r = _t.Rise[c], e = Math.Max(1, _rules.DriftExposedRise * 10);   // cm
            return r <= 0 ? 1000 : r >= e ? 0 : (e - r) * 1000 / e;
        }

        /// <summary>
        /// SN-07 v3: the hour's drift footprint, the same for every cell: <paramref name="steps"/> steps a cell long downwind
        /// (<paramref name="to"/>, grid degrees the wind blows toward) to the reach, an equal share a step, each spread 1-2-1 across
        /// the wind a cell either side and landed bilinearly. Offsets in cells, weights of 65,536 (rounded so they sum to it
        /// exactly, the remainder on the largest). Integer only, so every machine builds the same footprint.
        /// </summary>
        internal static int DriftKernel(int to, int steps, int[] kx, int[] ky, int[] kw, long[] scratch = null)
        {
            int r = steps + 3, side = 2 * r + 1;
            // The caller's scratch (the hour keeps one, so a windy hour allocates nothing, BG-02), else a new one.
            var acc = scratch != null && scratch.Length >= side * side ? scratch : new long[side * side];
            Array.Clear(acc, 0, side * side);
            long ux = SinDeg[to], uy = SinDeg[(to + 90) % 360];   // east and north parts of the way the wind goes, millionths
            int stepX = (int)(ux * 256 / SunVector.One), stepY = (int)(uy * 256 / SunVector.One);
            for (int s = 1; s <= steps; s++)
                for (int l = -1; l <= 1; l++)
                {
                    long px = (long)s * stepX - (long)l * stepY, py = (long)s * stepY + (long)l * stepX;   // 1/256 cell; across = (−uy, ux)
                    long ix = px >> 8, iy = py >> 8;
                    int fx = (int)(px - (ix << 8)), fy = (int)(py - (iy << 8)), share = 2 - Math.Abs(l);   // of 4 a step
                    void Add(long ox, long oy, int wgt) { if (wgt != 0) acc[(int)(oy + r) * side + (int)(ox + r)] += (long)wgt * share; }
                    Add(ix, iy, (256 - fx) * (256 - fy));
                    Add(ix + 1, iy, fx * (256 - fy));
                    Add(ix, iy + 1, (256 - fx) * fy);
                    Add(ix + 1, iy + 1, fx * fy);
                }
            long total = (long)steps * 4 * 65_536;
            int n = 0, sum = 0, big = 0;
            for (int k = 0; k < acc.Length; k++)
            {
                if (acc[k] == 0) continue;
                int wgt = (int)(acc[k] * 65_536 / total);
                if (wgt == 0) continue;
                kx[n] = k % side - r; ky[n] = k / side - r; kw[n] = wgt;
                if (wgt > kw[big]) big = n;
                sum += wgt; n++;
            }
            kw[big] += 65_536 - sum;
            return n;
        }

        /// <summary>One footprint cell's share of a drifting cell's snow (µm): its footprint weight × how much of it the cell
        /// keeps. Not renormalised over the keepers: a ridge whose footprint has only a strip of shelter would pour its whole
        /// drift onto that strip (a line along the keep's edge).</summary>
        static long Share(long drift, int kernelWeight, int take) => drift * kernelWeight * take / 65_536_000;

        /// <summary>
        /// The drift: each drifting cell lands its shares over its footprint (water's to the lakes, past the map's edge or into
        /// a hole off the map). What finds no shelter within the reach stays where it fell, with the rounding.
        /// </summary>
        void PassDrift(int band)
        {
            int w = _snow.Width, h = _snow.Height, y0 = band * SimSnow.TileCells, y1 = Math.Min(h, y0 + SimSnow.TileCells);
            ref var L = ref _bandLedger[band];
            for (int j = y0; j < y1; j++)
                for (int i = 0; i < w; i++)
                {
                    int c = j * w + i, drift = _drift[c];
                    if (drift <= 0) continue;
                    long sent = 0;
                    for (int k = 0; k < _kn; k++)
                    {
                        int x = i + _kx[k], y = j + _ky[k], n = y * w + x;
                        if (x < 0 || y < 0 || x >= w || y >= h || OffMap(n))
                        {
                            long gone = Share(drift, _kw[k], 1000);
                            L.OffMap += gone; sent += gone;
                            continue;
                        }
                        if (Lake(n)) { long a = Share(drift, _kw[k], 1000); L.ToWater += a; sent += a; continue; }
                        int take = Take(n);
                        if (take <= 0) continue;
                        long s = Share(drift, _kw[k], take);
                        _import[n] += (int)s; sent += s;
                    }
                    _export[c] -= (int)(drift - sent);   // what finds no shelter stays where it fell
                }
        }

        // ================= C: the energy and the conditions =================

        void PassC(int band)
        {
            var s = _snow;
            var f = s.Field;
            var t = _t;
            int w = s.Width, y0 = band * SimSnow.TileCells, y1 = Math.Min(s.Height, y0 + SimSnow.TileCells);
            var row = _rows[band];
            ref var L = ref _bandLedger[band];
            int tx0 = band * s.TilesX;
            for (int tx = 0; tx < s.TilesX; tx++)
            {
                int k = tx0 + tx;
                s.TileChanged[k] = false;
                if (_readMask[k]) { s.TileHashNext[k] = SnowField.TileSeed ^ (ulong)k; s.TileSnowy[k] = false; }
                else { s.CopyPlanesToNext(k); s.TileHashNext[k] = s.TileHashes[k]; }
            }
            var energy = _rules.Energy;
            int lw = t.LatticeWidth, ld = t.LatticeDepth;
            var runoff = f.RunoffHour;
            var fine = Fine;
            int[] blockCell = fine?.Layout.BlockCell;
            // The cells run in increasing index within a band, so one cursor finds each cell's block.
            int blk = blockCell == null ? 0 : LowerBound(blockCell, y0 * w);
            for (int j = y0; j < y1; j++)
            {
                _fields.ReadRowsSmooth(j, j + 1, row, t.BinPlace, t.Exposure, t.Canopy, _readMask);
                int lb0 = j >> 1, lb1 = (j & 1) != 0 ? Math.Min(ld - 1, lb0 + 1) : lb0;
                for (int tx = 0; tx < s.TilesX; tx++)
                {
                    int k = tx0 + tx;
                    int x0 = tx * SimSnow.TileCells, x1 = Math.Min(w, x0 + SimSnow.TileCells);
                    if (!_readMask[k]) { Array.Clear(runoff, j * w + x0, x1 - x0); continue; }
                    ulong hash = s.TileHashNext[k];
                    bool snowy = s.TileSnowy[k], changed = s.TileChanged[k];
                    for (int i = x0; i < x1; i++)
                    {
                        int c = j * w + i;
                        ref var p = ref f.Pack[c];
                        ref var wc = ref row[i];
                        runoff[c] = 0;
                        int b = -1;
                        if (blockCell != null)
                        {
                            while (blk < blockCell.Length && blockCell[blk] < c) blk++;
                            if (blk < blockCell.Length && blockCell[blk] == c) b = blk;
                        }
                        if (wc.Known && !Lake(c))
                        {
                            int snow = 0, rain = 0;
                            if (_wet)
                            {
                                Incoming(wc, t.Canopy[c], out _, out snow, out rain, out _);
                                snow = snow - _export[c] + _import[c];
                            }
                            if (p.Swe <= 0 && snow <= 0)
                            {
                                if (rain > 0) { L.Runoff += rain; runoff[c] = rain; }
                                if (f.Type[c] != 0 || p.Swe != 0) f.Clear(c);
                            }
                            else
                            {
                                int la0 = i >> 1, la1 = (i & 1) != 0 ? Math.Min(lw - 1, la0 + 1) : la0;
                                int l00 = lb0 * lw + la0, l10 = lb0 * lw + la1, l01 = lb1 * lw + la0, l11 = lb1 * lw + la1;
                                var os = t.OpenSky;
                                int open = os[l00] + os[l10] + os[l01] + os[l11];   // four (‰ ÷ 4): the bilinear mean in ‰
                                int beam = 0, diffuse = 0;
                                if (_clear > 0)
                                {
                                    var hz = t.Horizon;
                                    long sum = Sky(hz, l00) + Sky(hz, l10) + Sky(hz, l01) + Sky(hz, l11);   // sin × 255 × 4000
                                    long skyline = sum * 1000 / 1020;                                         // millionths
                                    int visible = (int)Math.Max(0, Math.Min(1000, 500 + (_sun.Up - skyline) * 1000 / _rules.SkylineFade));
                                    (beam, diffuse) = SlopeSun.At(_sun, _clear, SnowTerrain.Micro(t.NormalX[c]), SnowTerrain.Micro(t.NormalY[c]),
                                                                  t.NormalZMicro(c), open, visible);
                                }
                                var x = new SnowInput
                                {
                                    Seconds = _seconds, Air = wc.Temp, Dew = wc.Dew, WetBulb = wc.WetBulb, Feet = wc.Feet, Wind = wc.Wind,
                                    Cloud = _cloud, Beam = beam, Diffuse = diffuse, OpenSky = open, Canopy = t.Canopy[c] * 1000 / 255,
                                    Snow = Math.Max(0, snow), SnowDensity = 10_000 / WeatherAir.SnowRatioTenths(wc.WetBulb), Rain = rain,
                                };
                                int swe0 = p.Swe, rho0 = p.Density;
                                byte type0 = f.Type[c], age0 = f.Age[c];
                                var o = SnowEnergy.Step(ref p, x, energy);
                                if (b >= 0) KeepPacked(ref p, ref o, b, band);
                                L.Runoff += o.Runoff; L.Sublimated += o.Sublimated; L.Melt += o.Melt;
                                runoff[c] = (int)o.Runoff;
                                Conditions(_rules, _faceDay, f, c, o.Thawed, o.Skin, x.Snow, rain, wc.WetBulb);
                                if (b >= 0) FineHour(b, c, swe0, rho0, type0, age0, x.Snow, x.SnowDensity, rain, band);
                            }
                        }
                        else if (p.Swe != 0)
                        {
                            // A lake's cell holds no snow of its own (SN-25): what a seed or an older table left goes to the water.
                            L.ToWater += p.Swe;
                            f.Clear(c);
                            if (b >= 0) fine.ClearBlock(b);
                        }
                        L.End += p.Swe;
                        snowy |= p.Swe > 0;
                        changed |= Planes(c);
                        hash = f.MixCell(hash, c);
                    }
                    s.TileHashNext[k] = hash; s.TileSnowy[k] = snowy; s.TileChanged[k] = changed;
                }
            }
        }

        static int LowerBound(int[] sorted, int value)
        {
            int lo = 0, hi = sorted.Length;
            while (lo < hi) { int m = (lo + hi) >> 1; if (sorted[m] < value) lo = m + 1; else hi = m; }
            return lo;
        }

        /// <summary>
        /// SN-30 v2 on the 8 m step: the water the hour ran off or sublimated, times the block's mean loss weight, is what
        /// leaves; the rest stays in the pack, taken back off the runoff first, then the sublimation (exactly 1000 off the
        /// packed cells, so loose snow's bytes are today's).
        /// </summary>
        void KeepPacked(ref SnowPack p, ref SnowOutput o, int b, int band)
        {
            var fine = Fine;
            int bc = fine.BlockCells, f0 = b * bc;
            var wgt = _fineWeights[band];
            long sum = 0;
            for (int k = 0; k < bc; k++) { wgt[k] = fine.Weight(f0 + k, PackedFloor); sum += wgt[k]; }
            long lost = (long)o.Runoff + Math.Max(0, o.Sublimated);
            if (lost <= 0 || sum >= (long)bc * FineSnow.WeightOne) return;
            long kept = lost - lost * sum / ((long)bc * FineSnow.WeightOne);
            int fromRunoff = (int)Math.Min(kept, o.Runoff);
            o.Runoff -= fromRunoff; p.Swe += fromRunoff; p.Liquid += fromRunoff;
            int fromSublimation = (int)(kept - fromRunoff);
            o.Sublimated -= fromSublimation; p.Swe += fromSublimation;
        }

        /// <summary>
        /// The 8 m cell's hour on its block: the net change shared (<see cref="FineSnow.ShareHour"/>, the loss by the weights
        /// <see cref="KeepPacked"/> filled), a dry hour's settling, a natural change of type or age passed to every fine
        /// cell with snow (new snow clears the packing and the groomings; 6 in of it halves the moguls, SN-17), then the
        /// roll-up's density and type back on the 8 m cell. The 8 m water already equals the roll-up's.
        /// </summary>
        void FineHour(int b, int c, int swe0, int rho0, byte type0, byte age0, int snow, int snowDensity, int rain, int band)
        {
            var fine = Fine;
            var f = _snow.Field;
            ref var p = ref f.Pack[c];
            int bc = fine.BlockCells, f0 = b * bc;
            int delta = p.Swe - swe0;
            int gain = Math.Max(0, delta), loss = Math.Max(0, -delta);
            int density = snow > 0 ? snowDensity * 1000 : 1_000_000;
            var wgt = _fineWeights[band];
            if (!fine.ShareHour(b, gain, density, loss, wgt)) fine.SeedBlock(f, b);   // never with the invariant held
            if (snow <= 0 && rain <= 0 && rho0 > 0) fine.Settle(b, (int)Math.Min(int.MaxValue, (long)p.Density * 1000 / rho0));
            byte type1 = f.Type[c], age1 = f.Age[c];
            bool fresh = type1 != type0 && (type1 == (byte)SnowType.P || type1 == (byte)SnowType.WP);
            bool deep = snow > 0 && snowDensity > 0 && (long)snow * 1000 / snowDensity >= 152_400;
            for (int q = f0; q < f0 + bc; q++)
            {
                if (fine.Swe[q] <= 0) continue;
                if (type1 != type0 && type1 != 0) fine.Type[q] = type1;   // a bare 8 m cell has no type to pass on (task 4.25)
                if (fresh) fine.Flags[q] = (byte)(fine.Flags[q] & ~(FineSnow.Packed | FineSnow.GroomMask));
                if (deep) fine.Moguls[q] = (byte)(fine.Moguls[q] / 2);
                if (age1 < age0) fine.Age[q] = age1;
                else if (age1 > age0) fine.Age[q] = (byte)Math.Min(255, fine.Age[q] + (age1 - age0));
            }
            // Only a block the trails have packed speaks for its 8 m cell: loose fine cells follow it exactly, so a trail no
            // one grooms or skies leaves the 8 m snow byte for byte as it was without one.
            bool packed = false;
            for (int q = f0; q < f0 + bc && !packed; q++) packed = (fine.Flags[q] & FineSnow.Packed) != 0 && fine.Swe[q] > 0;
            if (!packed) return;
            // A block under a µm a cell rolls up to a bare 8 m cell, which stays bare: no density or type (the sidecar's rule;
            // task 4.25 found it under thin groomed snow).
            if (p.Swe <= 0) return;
            fine.RollUp(b, out _, out int rho, out byte type);
            if (rho > 0) p.Density = rho;
            f.Type[c] = type;
        }

        /// <summary>The cell's next planes; true when they differ from the landed ones.</summary>
        bool Planes(int c)
        {
            var s = _snow;
            var f = s.Field;
            ref var p = ref f.Pack[c];
            int d = p.DepthMm;
            ushort depth = d >= ushort.MaxValue ? ushort.MaxValue : (ushort)Math.Max(0, d);
            byte ty = f.Type[c], ag = f.Age[c];
            byte q = (byte)(_rules.Quality(ty, ag) / 4);
            byte rho = (byte)Math.Min(255, p.DensityKg / 4);
            s.MmNext[c] = depth; s.VTypeNext[c] = ty; s.VQualityNext[c] = q; s.VAgeNext[c] = ag; s.VDensityNext[c] = rho;
            return s.Cells[c] != depth || s.VType[c] != ty || s.VQuality[c] != q || s.VAge[c] != ag || s.VDensity[c] != rho;
        }

        long Sky(byte[] hz, int l) => hz[l * SnowTerrain.Directions + _sunA] * (1000L - _sunWB) + hz[l * SnowTerrain.Directions + _sunB] * (long)_sunWB;

        /// <summary>The two skyline directions either side of the sun's grid azimuth and the second's weight (‰).</summary>
        static void SunDirections(in SunVector sun, out int a, out int b, out int wb)
        {
            a = 0; b = 0; wb = 0;
            long len = WeatherMath.ISqrt((long)sun.East * sun.East + (long)sun.North * sun.North);
            if (len <= 0) return;
            // The direction whose unit vector is nearest the sun's (16 of them, 22.5° apart, clockwise from grid north).
            long best = long.MinValue;
            for (int d = 0; d < SnowTerrain.Directions; d++)
            {
                long dot = (long)sun.East * SinMilli16(d) + (long)sun.North * SinMilli16(d + 4);
                if (dot > best) { best = dot; a = d; }
            }
            // Which neighbour the sun leans toward, and how far: sin of the angle off a over sin 22.5°.
            long cross = (long)sun.East * SinMilli16(a + 4) - (long)sun.North * SinMilli16(a);   // + : clockwise of a
            b = cross >= 0 ? (a + 1) % SnowTerrain.Directions : (a + SnowTerrain.Directions - 1) % SnowTerrain.Directions;
            long sinOff = Math.Abs(cross) / len;                                                   // millionths
            wb = (int)Math.Min(500, sinOff * 1000 / 382_683);
        }

        static int SinMilli16(int d) => SunVector.SinMilli((d % SnowTerrain.Directions) * 22_500);

        // ================= SN-14 v2: the conditions =================

        /// <summary>One face hour of a cell's surface after the energy (first match wins): <paramref name="thawed"/> and
        /// <paramref name="skin"/> are the energy's, <paramref name="snow"/> and <paramref name="rain"/> the hour's water (µm),
        /// <paramref name="wetBulb"/> its wet bulb. A static of its inputs so each rule can be tested alone (SnowConditionsTests).</summary>
        internal static void Conditions(in SnowRules r, int faceDay, SnowField f, int c, bool thawed, int skin, int snow, int rain, int wetBulb)
        {
            ref var p = ref f.Pack[c];
            if (p.Swe <= 0) { f.Clear(c); return; }
            int type = f.Type[c], flags = f.Flags[c], age = f.Age[c];
            int newSince = f.NewSince[c] * 10;
            bool rainWet = (flags & SnowField.RainWet) != 0;
            int cold = flags >> SnowField.ColdShift & 3, cycles = flags >> SnowField.CycleShift & 3;
            if (cycles > 0 && (byte)(faceDay - f.CycleDay[c]) > r.CornWindowDays) cycles = 0;
            bool thaw = thawed || rain > 0;
            cold = !thaw && skin < r.RefreezeSkin ? Math.Min(3, cold + 1) : 0;
            age = Math.Min(255, age + 1);
            newSince = Math.Min(655_350, newSince + Math.Max(0, snow));
            byte fresh = (byte)(wetBulb > r.WetPowderWetBulb ? SnowType.WP : SnowType.P);
            if (type == (byte)SnowType.None) { type = fresh; age = 0; }

            if (newSince >= r.PowderNewUm)
            {
                // 1. Powder once 0.1 in of water has built up since the last surface event; the age starts again (SN-18 v2).
                type = fresh; age = 0; newSince = 0; rainWet = false;
            }
            else if (thaw && rain == 0 && cycles + 1 >= r.CornCycles
                     && (type == (byte)SnowType.FG || type == (byte)SnowType.LG || type == (byte)SnowType.CO))
            {
                // 3. The spring cycle's thaw (tested before the plain thaw): corn while the thaw holds.
                type = (byte)SnowType.CO; newSince = 0;
            }
            else if (thaw)
            {
                // 4. A thaw or rain: wet granular (powder's first: wet powder). Rain marks the wet for the ice test.
                type = type == (byte)SnowType.P || type == (byte)SnowType.PP || type == (byte)SnowType.MM ? (byte)SnowType.WP : (byte)SnowType.WG;
                if (rain > 0) rainWet = true;
                newSince = 0;
            }
            else if (cold >= r.RefreezeHours && (type == (byte)SnowType.WG || type == (byte)SnowType.WP || type == (byte)SnowType.CO))
            {
                // 5. The refreeze: ice after rain or glaze, else a crust that counts a thaw–refreeze cycle.
                if (rainWet) type = (byte)SnowType.IS;
                else
                {
                    type = (byte)SnowType.FG;
                    cycles = Math.Min(3, cycles + 1);
                    f.CycleDay[c] = (byte)faceDay;
                }
                rainWet = false; newSince = 0;
            }
            f.Type[c] = (byte)type;
            f.Age[c] = (byte)age;
            f.NewSince[c] = (ushort)(newSince / 10);
            f.Flags[c] = (byte)((rainWet ? SnowField.RainWet : 0) | cold << SnowField.ColdShift | cycles << SnowField.CycleShift);
        }
    }
}
