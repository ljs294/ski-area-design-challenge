using System;
using MountainPlanner.Domain.Weather;

namespace MountainPlanner.Simulation
{
    /// <summary>One 8 m cell's weather this face hour (WX-36…40): temperatures 0.1 °F, RH and shares ‰, liquid 0.01 in, snow 0.1 in, wind 0.1 mph.</summary>
    public struct WeatherCell
    {
        public int Feet, HvfFeet, Exposure;
        public int Temp, Dew, Rh, WetBulb, SnowPermille;
        /// <summary>WX-38 v2: warm air over a cold pool: the hour's liquid glazes on contact (for 4.07).</summary>
        public bool FreezingRain;
        public int Liquid, SnowTenthsIn;
        /// <summary>The same liquid in µm, before the 0.01 in rounding: what the snow takes, so a rounding step between two
        /// neighbouring cells never shows in the snow (the readout and the reference points keep <see cref="Liquid"/>).</summary>
        public int LiquidUm;
        /// <summary>Wind and gust (0.1 mph) and the direction it blows from (°, the hour's plus the terrain's turn).</summary>
        public int Wind, Gust, Direction;
        /// <summary>WX-40: shortwave on a flat surface (W/m²): the snapshot's, never hashed state.</summary>
        public int Shortwave;
        public bool Known;
    }

    /// <summary>
    /// WX-02 v2's tables (task 4.05 PR B2): the weather over the map as small tables filled once per face hour from the hour's
    /// parameter set and the area's static inputs (<see cref="WeatherTerrain"/>), so a cell's weather is a few lookups, never
    /// a calculation, and nothing is stored per cell.
    /// - **Air**, per (elevation bin × pool band) the area has (≤ 4,096): temperature with the lapse and the cold pool
    ///   (WX-36 v2), dew point (WX-37), RH, the integer wet bulb (<see cref="WeatherAir"/>), snow share (WX-03 v3) and
    ///   freezing rain (WX-38 v2).
    /// - **Precipitation**: the elevation gradient per bin, and the orographic factor per 512 m cell, blended between the two
    ///   octants nearest the storm's direction (WX-38 v2).
    /// - **Wind**: the steering per 96 m cell, blended between the two octants nearest the hour's direction (WX-39); a cell's
    ///   wind is the free wind × its exposure class × that speed, and its gust × the hour's gust factor.
    /// The worker fills one; each snapshot copies it when the hour moves (<see cref="CopyFrom"/>), so the main thread
    /// (the readout, 4.06's layers) reads a whole hour, never a half-filled one. Filling allocates nothing.
    /// </summary>
    public sealed class WeatherFields
    {
        const int Pairs = WeatherTerrain.Bins * WeatherTerrain.Bands;
        /// <summary>WX-39: the wind under dense canopy (‰ of the open's): the caller's canopy, which follows the player's clearing.</summary>
        public const int DenseCanopyWind = 600;

        readonly short[] _temp = new short[Pairs], _dew = new short[Pairs], _rh = new short[Pairs], _wetBulb = new short[Pairs], _snow = new short[Pairs];
        readonly bool[] _freezing = new bool[Pairs];
        /// <summary>The pairs a fill computes: every one the map has, and every neighbour the snow's smooth read blends toward
        /// (task 4.07 PR A2). On an even slope a bin holds one band, so its neighbour pairs aren't on the map; the read fell back
        /// to its own pair there and stepped at every bin and band edge.</summary>
        readonly bool[] _needed = new bool[Pairs];
        readonly int[] _precipBin = new int[WeatherTerrain.Bins];
        readonly int[] _exposure = new int[8];
        /// <summary>Per (bin × band): the snow share × the snow ratio, so a cell's snow is its liquid × this ÷ 100,000.</summary>
        readonly int[] _snowFactor = new int[Pairs];
        /// <summary>Per 96 m cell: the free wind × the steering's speed (0.1 mph, before the exposure class) and the direction the
        /// wind comes from (°, true).</summary>
        int[] _steerWind = Array.Empty<int>(), _steerDir = Array.Empty<int>();
        /// <summary>WX-38 v3: the hour's two octants nearest the storm's direction and the second's weight (‰): each cell reads its
        /// own orographic factor for both (WX-35 v3, <see cref="WeatherTerrain.OrographicAt"/>) and blends them.</summary>
        int _oroA, _oroB, _oroW;
        int _liquid, _wind, _gustFactor, _direction, _shortwave;

        /// <summary>The face hour these tables describe (−1: none yet).</summary>
        public long Hour { get; private set; } = -1;
        /// <summary>The static inputs they were filled for.</summary>
        public WeatherTerrain Terrain { get; private set; }
        public int Shortwave => _shortwave;
        /// <summary>What this fill holds, bytes (the fixed tables and the per-terrain ones): beside SI-16's 64 KB, never inside it.</summary>
        public int SizeBytes => Pairs * (5 * 2 + 1 + 4) + _precipBin.Length * 4 + 8 * 4 + (_steerWind.Length + _steerDir.Length) * 4;

        /// <summary>Sizes the per-terrain tables (once, when the static inputs arrive: never per hour).</summary>
        void Size(WeatherTerrain t)
        {
            if (Terrain == t) return;
            int steer = t.SteerWidth * t.SteerDepth;
            if (_steerWind.Length != steer) { _steerWind = new int[steer]; _steerDir = new int[steer]; }
            Array.Clear(_needed, 0, Pairs);
            for (int b = 0; b < WeatherTerrain.Bins - 1; b++)
                for (int band = 0; band < WeatherTerrain.Bands; band++)
                {
                    if (!t.Present[b * WeatherTerrain.Bands + band]) continue;
                    for (int nb = Math.Max(0, b - 1); nb <= Math.Min(WeatherTerrain.Bins - 2, b + 1); nb++)
                        for (int nd = Math.Max(0, band - 1); nd <= Math.Min(WeatherTerrain.Bands - 1, band + 1); nd++)
                            _needed[nb * WeatherTerrain.Bands + nd] = true;
                }
            // Last: a reader that sees the new inputs finds arrays already sized for them.
            Terrain = t;
        }

        /// <summary>
        /// Fills the tables for face hour <paramref name="hour"/>: <paramref name="h"/> at zref (<paramref name="zrefFeet"/>), over
        /// <paramref name="t"/>, with <paramref name="shortwave"/> the hour's flat-surface sun (W/m²).
        /// </summary>
        public void Fill(long hour, in WeatherHour h, WeatherTerrain t, int zrefFeet, Pacing p, int shortwave)
        {
            Size(t);
            Hour = hour;
            _liquid = h.Liquid;
            _wind = h.Wind;
            _gustFactor = h.GustFactor;
            _direction = ((h.Direction % 360) + 360) % 360;
            _shortwave = shortwave;
            for (int k = 0; k < 8; k++) _exposure[k] = p.WindExposure[k];

            int refPool = PoolShare(t.HvfRefFeet, p);
            for (int b = 0; b < WeatherTerrain.Bins; b++)
            {
                int feet = t.BinCentreFeet(b), dz = feet - zrefFeet;
                _precipBin[b] = PrecipFactor(h, dz);
                int wbNoPool = int.MinValue;   // the bin's wet bulb without the pool: solved once, when a band needs it
                for (int band = 0; band < WeatherTerrain.Bands; band++)
                {
                    int k = b * WeatherTerrain.Bands + band;
                    if (!_needed[k]) continue;
                    // The last band is everything above the pool's reach: no share at all.
                    int share = band == WeatherTerrain.Bands - 1 ? 0 : PoolShare(band * WeatherTerrain.BandFeet + WeatherTerrain.BandFeet / 2, p);
                    Air(h, feet, dz, share - refPool, ref wbNoPool, out int temp, out int dew,
                        out int rh, out int wb, out int snow, out bool freezing);
                    _temp[k] = (short)temp; _dew[k] = (short)dew; _rh[k] = (short)rh; _wetBulb[k] = (short)wb; _snow[k] = (short)snow;
                    _freezing[k] = freezing;
                    _snowFactor[k] = snow * WeatherAir.SnowRatioTenths(wb);
                }
            }

            // WX-38 v2: the orographic factor between the two octants nearest the storm's direction (the hour's when there's none).
            // The weather's directions are true; the octants are the grid's (the convergence, 9° at Jackson Hole).
            int stormDir = h.StormOrdinal >= 0 ? h.StormDirection : _direction;
            Blend(t.GridDegrees(stormDir), out int o0, out int o1, out int w1);
            _oroA = o0; _oroB = o1; _oroW = w1;
            // WX-39: the speed between the two octants nearest the hour's direction; the turn from its exact heading.
            int gridFrom = t.GridDegrees(_direction), heading = (gridFrom + 180) % 360;
            Blend(gridFrom, out o0, out o1, out w1);
            int steer = t.SteerWidth * t.SteerDepth;
            for (int c = 0; c < steer; c++)
            {
                int speed = (t.Speed[o0 * steer + c] * (1000 - w1) + t.Speed[o1 * steer + c] * w1) / 100;   // ‰
                int turn = WeatherTerrain.TurnDegrees(t.Axis[c], t.TurnWeight[c], heading);
                _steerWind[c] = _wind * speed / 1000;
                _steerDir[c] = ((_direction + turn) % 360 + 360) % 360;
            }
        }

        /// <summary>
        /// The air at <paramref name="feet"/> (<paramref name="dz"/> ft above zref) with a pool share (‰) relative to the
        /// reference pixel's. <paramref name="wbNoPool"/>, the wet bulb without the pool, depends on the height alone: pass
        /// <c>int.MinValue</c> and it's solved here when needed, then kept for the bin's other bands.
        /// </summary>
        static void Air(in WeatherHour h, int feet, int dz, int poolShare, ref int wbNoPool, out int temp, out int dew, out int rh, out int wb, out int snow, out bool freezing)
        {
            int free = h.Temp - (int)WeatherMath.DivRound((long)h.Lapse * dz, 1000);
            int dewFree = h.Dew - (int)WeatherMath.DivRound((long)h.DewLapse * dz, 1000);
            temp = free - (int)WeatherMath.DivRound((long)h.Pool * poolShare, 1000);
            dew = Math.Min(temp, dewFree);
            rh = WeatherAir.RhPermille(temp, dew);
            wb = WeatherAir.WetBulb(temp, dew, feet);
            // Freezing rain: without the pool it would rain, but the pooled air is at or below freezing.
            freezing = false;
            if (poolShare > 0 && temp <= WeatherAir.Freezing)
            {
                if (wbNoPool == int.MinValue) wbNoPool = WeatherAir.WetBulb(free, Math.Min(free, dewFree), feet);
                freezing = wbNoPool > WeatherAir.RainWetBulb;
            }
            snow = freezing ? 0 : WeatherAir.SnowPermille(wb);
        }

        /// <summary>WX-38 v2's elevation factor (‰): 1 + the hour's gradient × the height above zref, clamped 0–4.</summary>
        public static int PrecipFactor(in WeatherHour h, int dz) =>
            (int)WeatherMath.Clamp(1000 + WeatherMath.DivRound((long)h.PrecipGradient * dz, 100), 0, 4000);

        /// <summary>WX-36 v2's p(h) = max(0, 1 − h ÷ PoolDepth), ‰.</summary>
        public static int PoolShare(int hvfFeet, Pacing p) => Math.Max(0, 1000 - (int)((long)hvfFeet * 1000 / p.PoolDepthFeet));

        static void Blend(int direction, out int o0, out int o1, out int w1)
        {
            int d = ((direction % 360) + 360) % 360;
            o0 = d / 45;
            o1 = (o0 + 1) % 8;
            w1 = d % 45 * 1000 / 45;
        }

        /// <summary>
        /// A reference point's air (WX-35's base, mid, summit). With static inputs its pool is the box's own at that height
        /// (<see cref="WeatherTerrain.BinHvfFeet"/>), as the map's cells there; without them (or at a height the box doesn't
        /// have) it sits at the reference pixel's, with no pool of its own.
        /// </summary>
        public static void AirAt(in WeatherHour h, int zrefFeet, int feet, WeatherTerrain t, Pacing p,
                                 out int temp, out int dew, out int rh, out int wb, out int snow, out bool freezing)
        {
            int share = 0;
            if (t != null)
            {
                int hvf = t.BinHvfFeet[t.BinOf(feet)];
                if (hvf >= 0) share = (hvf >= (WeatherTerrain.Bands - 1) * WeatherTerrain.BandFeet ? 0 : PoolShare(hvf, p)) - PoolShare(t.HvfRefFeet, p);
            }
            int wbNoPool = int.MinValue;
            Air(h, feet, feet - zrefFeet, share, ref wbNoPool, out temp, out dew, out rh, out wb, out snow, out freezing);
        }

        /// <summary>WX-02's point read: one cell's weather this hour (<c>Known</c> false for a cell without heights).</summary>
        public void At(int cell, out WeatherCell c) => At(cell, -1, out c);

        /// <summary>One cell's weather, with the caller's canopy share (0–255, −1: open) for the wind.</summary>
        public void At(int cell, int canopy, out WeatherCell c)
        {
            c = default;
            var t = Terrain;
            if (t == null || Hour < 0 || cell < 0 || cell >= t.Cells) return;
            int i = cell % t.Width, j = cell / t.Width;
            Cell(t, cell, i, j, canopy, null, null, ref c);
        }

        /// <summary>WX-38 v3: the hour's orographic factor (‰) at a cell standing at <paramref name="feet"/>.</summary>
        int Oro(WeatherTerrain t, int i, int j, int feet)
        {
            int a = t.OrographicAt(_oroA, i, j, feet);
            if (_oroW == 0) return a;
            return (a * (1000 - _oroW) + t.OrographicAt(_oroB, i, j, feet) * _oroW) / 1000;
        }

        /// <summary>Task 4.26 BB9: <see cref="Oro"/> from the batch read's rows (<see cref="OroRows"/>), the same number.</summary>
        int OroRow(WeatherTerrain t, int[] rowA, int[] rowB, int i, int j, int feet)
        {
            int a = t.OrographicAtRow(_oroA, rowA, i, j, feet);
            if (_oroW == 0) return a;
            return (a * (1000 - _oroW) + t.OrographicAtRow(_oroB, rowB, i, j, feet) * _oroW) / 1000;
        }

        // Each reading thread's own rows (4.07's bands read on several threads at once), made once per thread.
        [ThreadStatic] static int[] _rowA, _rowB;

        /// <summary>A batch read's orographic rows for row <paramref name="j"/>, or nulls in a dry hour (the read needs none).</summary>
        void OroRows(WeatherTerrain t, int j, out int[] rowA, out int[] rowB)
        {
            rowA = rowB = null;
            if (_liquid <= 0) return;
            int n = t.OrographicRowLength;
            if (_rowA == null || _rowA.Length < n) _rowA = new int[n];
            rowA = _rowA;
            t.OrographicRow(_oroA, j, rowA);
            if (_oroW == 0) return;
            if (_rowB == null || _rowB.Length < n) _rowB = new int[n];
            rowB = _rowB;
            t.OrographicRow(_oroB, j, rowB);
        }

        /// <summary>
        /// One cell: the point read's and the batch read's one formula (so a lift's gust is the map's). The orographic factor is
        /// judged at the bin's centre (the smooth read uses the cell's own height).
        /// </summary>
        void Cell(WeatherTerrain t, int cell, int i, int j, int canopy, int[] rowA, int[] rowB, ref WeatherCell c)
        {
            int bin = t.Bin[cell];
            if (bin == WeatherTerrain.NoData) { c = default; return; }
            int hvf = t.Hvf[cell], k = bin * WeatherTerrain.Bands + WeatherTerrain.BandOf(hvf);
            c.Known = true;
            c.Feet = t.BinCentreFeet(bin);
            c.HvfFeet = hvf * 10;
            int e = t.Exposure[cell];
            c.Exposure = e;
            c.Temp = _temp[k]; c.Dew = _dew[k]; c.Rh = _rh[k]; c.WetBulb = _wetBulb[k]; c.SnowPermille = _snow[k]; c.FreezingRain = _freezing[k];
            if (_liquid > 0)
            {
                // WX-38 v3: the cell's own orographic factor (WX-35 v3) × the bin's elevation factor.
                int oro = rowA != null ? OroRow(t, rowA, rowB, i, j, c.Feet) : Oro(t, i, j, c.Feet);
                c.Liquid = (int)WeatherMath.DivRound((long)_liquid * _precipBin[bin] * oro, 1_000_000);   // rounded once, as the reference points'
                c.LiquidUm = (int)WeatherMath.DivRound((long)_liquid * _precipBin[bin] * oro * 254, 1_000_000);
                c.SnowTenthsIn = (int)WeatherMath.DivRound((long)c.Liquid * _snowFactor[k], 100_000);   // as WeatherAir.SnowTenthsIn
            }
            else { c.Liquid = 0; c.LiquidUm = 0; c.SnowTenthsIn = 0; }
            int s = (j / WeatherTerrain.SteerCells) * t.SteerWidth + i / WeatherTerrain.SteerCells;
            int wind = _steerWind[s] * _exposure[e] / 1000;
            if (canopy > 0) wind = wind * (1000 - (1000 - DenseCanopyWind) * canopy / 255) / 1000;
            c.Wind = Math.Min(5000, wind);
            c.Gust = (int)Math.Min(5000L, (long)wind * _gustFactor / 1000);
            c.Direction = _steerDir[s];
            c.Shortwave = _shortwave;
        }

        static int FloorDiv(int a, int b) => a >= 0 ? a / b : -((-a + b - 1) / b);

        /// <summary>
        /// The batch read (for 4.07's snow, 4.16's guns, 4.06's layers): rows <paramref name="y0"/> to <paramref name="y1"/>
        /// (exclusive) into <paramref name="dest"/> (one entry per cell of those rows, west to east). Cells whose 64-cell tile
        /// is false in <paramref name="tileMask"/> (tiles west to east, then south to north) are skipped and left as they were;
        /// <paramref name="canopy"/> is the caller's canopy share per cell (0–255), or null for open ground. Allocates nothing.
        /// </summary>
        public void ReadRows(int y0, int y1, WeatherCell[] dest, bool[] tileMask = null, byte[] canopy = null)
        {
            var t = Terrain;
            if (t == null || Hour < 0) return;
            int first = y0;   // dest's first row is y0 as asked, even when part of the range is off the grid
            y0 = Math.Max(0, y0);
            y1 = Math.Min(t.Depth, y1);
            int width = t.Width, tilesWide = (width + 63) / 64;
            // The only scratch is each thread's own (OroRows): two readers of one fill (4.07's bands on several threads) share nothing.
            for (int j = y0; j < y1; j++)
            {
                int row = j * width, outRow = (j - first) * width;
                OroRows(t, j, out var rowA, out var rowB);
                for (int i = 0; i < width; i++)
                {
                    if (tileMask != null && !tileMask[j / 64 * tilesWide + i / 64]) { i = (i / 64 + 1) * 64 - 1; continue; }
                    Cell(t, row + i, i, j, canopy == null ? -1 : canopy[row + i], rowA, rowB, ref dest[outRow + i]);
                }
            }
        }

        /// <summary>
        /// The smooth batch read (task 4.07 PR A2, the owner's smooth-map rule): as <see cref="ReadRows"/>, but the air, the snow
        /// share and the elevation factor are blended between the two elevation bins and the two pool bands round the cell's
        /// own height (<paramref name="binPlace"/>: where it sits in its bin, 0–255; the pool band by its 10 ft height above the
        /// floor), and the wind reads the blended exposure (<paramref name="exposure8"/>, ‰ ÷ 8) instead of the class's. A pair
        /// the area doesn't have counts as the cell's own. So the rain/snow line and the depth don't step in 33 ft terraces.
        /// <c>Feet</c> is the cell's own height. Allocates nothing; safe for several readers of one fill.
        /// </summary>
        public void ReadRowsSmooth(int y0, int y1, WeatherCell[] dest, byte[] binPlace, byte[] exposure8, byte[] canopy, bool[] tileMask = null)
        {
            var t = Terrain;
            if (t == null || Hour < 0) return;
            int first = y0;
            y0 = Math.Max(0, y0);
            y1 = Math.Min(t.Depth, y1);
            int width = t.Width, tilesWide = (width + 63) / 64;
            for (int j = y0; j < y1; j++)
            {
                int row = j * width, outRow = (j - first) * width;
                OroRows(t, j, out var rowA, out var rowB);
                for (int i = 0; i < width; i++)
                {
                    if (tileMask != null && !tileMask[j / 64 * tilesWide + i / 64]) { i = (i / 64 + 1) * 64 - 1; continue; }
                    int cell = row + i;
                    SmoothCell(t, cell, i, j, binPlace[cell], exposure8[cell], canopy == null ? -1 : canopy[cell], rowA, rowB, ref dest[outRow + i]);
                }
            }
        }

        void SmoothCell(WeatherTerrain t, int cell, int i, int j, int place, int exposure8, int canopy, int[] rowA, int[] rowB, ref WeatherCell c)
        {
            int bin = t.Bin[cell];
            if (bin == WeatherTerrain.NoData) { c = default; return; }
            int hvf = t.Hvf[cell], band = WeatherTerrain.BandOf(hvf), k = bin * WeatherTerrain.Bands + band;
            // The bin's neighbour toward the cell's height and its weight (‰): 0 at the bin's middle, ½ at its edge.
            int binB = bin, wBin = 0;
            if (place >= 128) { if (bin + 1 < WeatherTerrain.Bins - 1) { binB = bin + 1; wBin = (place - 128) * 1000 / 256; } }
            else if (bin > 0) { binB = bin - 1; wBin = (128 - place) * 1000 / 256; }
            // The band's neighbour by the height above the floor (10 ft steps across a 32 ft band), the same way.
            // The last band is everything above the pool's reach: its lower half blends with the band below, as every band's
            // does (it was left out, a step of the pool's last 7 % at its foot).
            int bandB = band, wBand = 0;
            {
                int pos = (hvf * 10 + 5 - band * WeatherTerrain.BandFeet) * 1000 / WeatherTerrain.BandFeet;   // ‰ across the band (the 10 ft step's middle)
                pos = WeatherMath.Clamp(pos, 0, 1000);
                if (pos >= 500) { if (band + 1 < WeatherTerrain.Bands) { bandB = band + 1; wBand = pos - 500; } }
                else if (band > 0) { bandB = band - 1; wBand = 500 - pos; }
            }
            int kA = k, kB = binB * WeatherTerrain.Bands + band, kC = bin * WeatherTerrain.Bands + bandB, kD = binB * WeatherTerrain.Bands + bandB;
            // Every neighbour of a pair on the map is filled (_needed); the fallbacks only guard a fill for other inputs.
            if (!_needed[kB]) kB = k;
            if (!_needed[kC]) kC = k;
            if (!_needed[kD]) kD = _needed[kB] && kB != k ? kB : kC;
            long wa = (1000L - wBin) * (1000 - wBand), wb = (long)wBin * (1000 - wBand), wc = (1000L - wBin) * wBand, wd = (long)wBin * wBand;
            int Mix(short[] v) => (int)WeatherMath.DivRound(v[kA] * wa + v[kB] * wb + v[kC] * wc + v[kD] * wd, 1_000_000);
            c.Known = true;
            c.Feet = t.MinFeet + bin * t.BinFeet + place * t.BinFeet / 256;
            c.HvfFeet = hvf * 10;
            int e = t.Exposure[cell];
            c.Exposure = e;
            c.Temp = Mix(_temp); c.Dew = Mix(_dew); c.Rh = Mix(_rh); c.WetBulb = Mix(_wetBulb); c.SnowPermille = Mix(_snow);
            c.FreezingRain = _freezing[k];
            if (_liquid > 0)
            {
                int oro = OroRow(t, rowA, rowB, i, j, c.Feet);   // WX-35 v3: at the cell's own height
                long precip = WeatherMath.DivRound(_precipBin[bin] * (1000L - wBin) + _precipBin[binB] * (long)wBin, 1000);
                c.Liquid = (int)WeatherMath.DivRound((long)_liquid * precip * oro, 1_000_000);
                c.LiquidUm = (int)WeatherMath.DivRound((long)_liquid * precip * oro * 254, 1_000_000);
                long factor = WeatherMath.DivRound(_snowFactor[kA] * wa + _snowFactor[kB] * wb + _snowFactor[kC] * wc + _snowFactor[kD] * wd, 1_000_000);
                c.SnowTenthsIn = (int)WeatherMath.DivRound((long)c.Liquid * factor, 100_000);
            }
            else { c.Liquid = 0; c.LiquidUm = 0; c.SnowTenthsIn = 0; }
            int s = (j / WeatherTerrain.SteerCells) * t.SteerWidth + i / WeatherTerrain.SteerCells;
            int wind = SteerWindSmooth(t, i, j) * (exposure8 * 8) / 1000;
            if (canopy > 0) wind = wind * (1000 - (1000 - DenseCanopyWind) * canopy / 255) / 1000;
            c.Wind = Math.Min(5000, wind);
            c.Gust = (int)Math.Min(5000L, (long)wind * _gustFactor / 1000);
            c.Direction = _steerDir[s];
            c.Shortwave = _shortwave;
        }

        /// <summary>
        /// The steering's wind (0.1 mph, before the exposure class) read bilinearly between the 96 m cells' centres: the snow's
        /// smooth read only, so nothing wind-driven (its melt, sublimation, drift) steps along a straight 96 m line. The point
        /// read (the readout, a lift's gust) keeps the cell's own value.
        /// </summary>
        int SteerWindSmooth(WeatherTerrain t, int i, int j)
        {
            const int S = WeatherTerrain.SteerCells;
            int fx = i * 2 + 1 - S, fy = j * 2 + 1 - S, sw = t.SteerWidth, sd = t.SteerDepth;
            int x0 = WeatherMath.Clamp(FloorDiv(fx, 2 * S), 0, sw - 1), y0 = WeatherMath.Clamp(FloorDiv(fy, 2 * S), 0, sd - 1);
            int ux = WeatherMath.Clamp(fx - x0 * 2 * S, 0, 2 * S), uy = WeatherMath.Clamp(fy - y0 * 2 * S, 0, 2 * S);
            int x1 = Math.Min(sw - 1, x0 + 1), y1 = Math.Min(sd - 1, y0 + 1);
            long v = (long)_steerWind[y0 * sw + x0] * (2 * S - ux) * (2 * S - uy) + (long)_steerWind[y0 * sw + x1] * ux * (2 * S - uy)
                   + (long)_steerWind[y1 * sw + x0] * (2 * S - ux) * uy + (long)_steerWind[y1 * sw + x1] * ux * uy;
            return (int)WeatherMath.DivRound(v, 4L * S * S);
        }

        /// <summary>The hour's liquid at zref (0.01 in) and free wind (0.1 mph): the snow's quick test for a dry, calm hour.</summary>
        public int LiquidAtZref => _liquid;
        public int FreeWind => _wind;
        /// <summary>The hour's free wind direction (°, true, the way it blows from): the snow's drift (SN-07 v3) for the whole map.</summary>
        public int HourDirection => _direction;

        /// <summary>The direction the wind blows from at a cell (°, the hour's plus the terrain's turn), without the rest of the
        /// read: the snow's drift (SN-07 v2) walks downwind from it.</summary>
        public int DirectionAt(int i, int j)
        {
            var t = Terrain;
            if (t == null || Hour < 0) return 0;
            return _steerDir[(j / WeatherTerrain.SteerCells) * t.SteerWidth + i / WeatherTerrain.SteerCells];
        }

        /// <summary>Copies another fill (the snapshot's copy; allocates only when the static inputs change).</summary>
        public void CopyFrom(WeatherFields o)
        {
            if (o.Terrain == null) { Hour = -1; Terrain = null; return; }
            Size(o.Terrain);
            Hour = o.Hour;
            Array.Copy(o._temp, _temp, Pairs); Array.Copy(o._dew, _dew, Pairs); Array.Copy(o._rh, _rh, Pairs);
            Array.Copy(o._wetBulb, _wetBulb, Pairs); Array.Copy(o._snow, _snow, Pairs); Array.Copy(o._freezing, _freezing, Pairs);
            Array.Copy(o._precipBin, _precipBin, _precipBin.Length);
            Array.Copy(o._exposure, _exposure, 8);
            Array.Copy(o._snowFactor, _snowFactor, Pairs);
            _oroA = o._oroA; _oroB = o._oroB; _oroW = o._oroW;
            Array.Copy(o._steerWind, _steerWind, _steerWind.Length);
            Array.Copy(o._steerDir, _steerDir, _steerDir.Length);
            _liquid = o._liquid; _wind = o._wind; _gustFactor = o._gustFactor; _direction = o._direction; _shortwave = o._shortwave;
        }
    }
}
