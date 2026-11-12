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
        readonly int[] _precipBin = new int[WeatherTerrain.Bins];
        readonly int[] _exposure = new int[8];
        /// <summary>Per (bin × band): the snow share × the snow ratio, so a cell's snow is its liquid × this ÷ 100,000.</summary>
        readonly int[] _snowFactor = new int[Pairs];
        /// <summary>Per 512 m cell: the blended orographic factor (‰). Per 96 m cell: the free wind × the steering's speed (0.1 mph,
        /// before the exposure class) and the direction the wind comes from (°). A per-row scratch for the batch read.</summary>
        int[] _oro = Array.Empty<int>(), _steerWind = Array.Empty<int>(), _steerDir = Array.Empty<int>(), _rowOro = Array.Empty<int>();
        int _liquid, _wind, _gustFactor, _direction, _shortwave;

        /// <summary>The face hour these tables describe (−1: none yet).</summary>
        public long Hour { get; private set; } = -1;
        /// <summary>The static inputs they were filled for.</summary>
        public WeatherTerrain Terrain { get; private set; }
        public int Shortwave => _shortwave;
        /// <summary>What this fill holds, bytes (the fixed tables and the per-terrain ones): beside SI-16's 64 KB, never inside it.</summary>
        public int SizeBytes => Pairs * (5 * 2 + 1 + 4) + _precipBin.Length * 4 + 8 * 4 + (_oro.Length + _steerWind.Length + _steerDir.Length + _rowOro.Length) * 4;

        /// <summary>Sizes the per-terrain tables (once, when the static inputs arrive: never per hour).</summary>
        void Size(WeatherTerrain t)
        {
            if (Terrain == t) return;
            Terrain = t;
            int coarse = t.CoarseWidth * t.CoarseDepth, steer = t.SteerWidth * t.SteerDepth;
            if (_oro.Length != coarse) _oro = new int[coarse];
            if (_rowOro.Length != t.CoarseWidth) _rowOro = new int[t.CoarseWidth];
            if (_steerWind.Length != steer) { _steerWind = new int[steer]; _steerDir = new int[steer]; }
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
                int dz = t.BinCentreFeet(b) - zrefFeet;
                _precipBin[b] = PrecipFactor(h, dz);
                for (int band = 0; band < WeatherTerrain.Bands; band++)
                {
                    int k = b * WeatherTerrain.Bands + band;
                    if (!t.Present[k]) continue;
                    Air(h, dz, PoolShare(band * WeatherTerrain.BandFeet + WeatherTerrain.BandFeet / 2, p) - refPool, out int temp, out int dew,
                        out int rh, out int wb, out int snow, out bool freezing);
                    _temp[k] = (short)temp; _dew[k] = (short)dew; _rh[k] = (short)rh; _wetBulb[k] = (short)wb; _snow[k] = (short)snow;
                    _freezing[k] = freezing;
                    _snowFactor[k] = snow * WeatherAir.SnowRatioTenths(wb);
                }
            }

            // WX-38 v2: the orographic factor between the two octants nearest the storm's direction (the hour's when there's none).
            int stormDir = h.StormOrdinal >= 0 ? h.StormDirection : _direction;
            Blend(stormDir, out int o0, out int o1, out int w1);
            int coarse = t.CoarseWidth * t.CoarseDepth;
            for (int c = 0; c < coarse; c++)
                _oro[c] = (t.Orographic[o0 * coarse + c] * (1000 - w1) + t.Orographic[o1 * coarse + c] * w1) / 1000;
            // WX-39: the steering between the two octants nearest the hour's direction.
            Blend(_direction, out o0, out o1, out w1);
            int steer = t.SteerWidth * t.SteerDepth;
            for (int c = 0; c < steer; c++)
            {
                int speed = (t.Speed[o0 * steer + c] * (1000 - w1) + t.Speed[o1 * steer + c] * w1) / 100;   // ‰
                int turn = (t.Turn[o0 * steer + c] * 2 * (1000 - w1) + t.Turn[o1 * steer + c] * 2 * w1) / 1000;
                _steerWind[c] = _wind * speed / 1000;
                _steerDir[c] = ((_direction + turn) % 360 + 360) % 360;
            }
        }

        /// <summary>The air at <paramref name="dz"/> ft above zref with a pool share (‰) relative to the reference pixel's.</summary>
        static void Air(in WeatherHour h, int dz, int poolShare, out int temp, out int dew, out int rh, out int wb, out int snow, out bool freezing)
        {
            int free = h.Temp - (int)WeatherMath.DivRound((long)h.Lapse * dz, 1000);
            int dewFree = h.Dew - (int)WeatherMath.DivRound((long)h.DewLapse * dz, 1000);
            temp = free - (int)WeatherMath.DivRound((long)h.Pool * poolShare, 1000);
            dew = Math.Min(temp, dewFree);
            rh = WeatherAir.RhPermille(temp, dew);
            wb = WeatherAir.WetBulb(temp, rh);
            // Freezing rain: without the pool it would rain, but the pooled air is at or below freezing.
            int dewNoPool = Math.Min(free, dewFree);
            int wbNoPool = WeatherAir.WetBulb(free, WeatherAir.RhPermille(free, dewNoPool));
            freezing = poolShare > 0 && wbNoPool > WeatherAir.RainWetBulb && temp <= WeatherAir.Freezing;
            snow = freezing ? 0 : WeatherAir.SnowPermille(wb);
        }

        /// <summary>WX-38 v2's elevation factor (‰): 1 + the hour's gradient × the height above zref, clamped 0–4.</summary>
        public static int PrecipFactor(in WeatherHour h, int dz) =>
            (int)WeatherMath.Clamp(1000 + WeatherMath.DivRound((long)h.PrecipGradient * dz, 100), 0, 4000);

        /// <summary>WX-36 v2's p(h) = max(0, 1 − h ÷ PoolDepth), ‰.</summary>
        static int PoolShare(int hvfFeet, Pacing p) => Math.Max(0, 1000 - (int)((long)hvfFeet * 1000 / p.PoolDepthFeet));

        static void Blend(int direction, out int o0, out int o1, out int w1)
        {
            int d = ((direction % 360) + 360) % 360;
            o0 = d / 45;
            o1 = (o0 + 1) % 8;
            w1 = d % 45 * 1000 / 45;
        }

        /// <summary>A reference point's air with no pool of its own (the reference points sit at the reference pixel's hvf).</summary>
        public static void AirAt(in WeatherHour h, int zrefFeet, int feet, out int temp, out int dew, out int rh, out int wb, out int snow)
        {
            Air(h, feet - zrefFeet, 0, out temp, out dew, out rh, out wb, out snow, out _);
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
            int fy = j * 2 + 1 - N, y0 = Math.Max(0, Math.Min(t.CoarseDepth - 1, FloorDiv(fy, 2 * N))), y1 = Math.Min(t.CoarseDepth - 1, y0 + 1);
            int uy = WeatherMath.Clamp(fy - y0 * 2 * N, 0, 2 * N);
            int fx = i * 2 + 1 - N, x0 = Math.Max(0, Math.Min(t.CoarseWidth - 1, FloorDiv(fx, 2 * N))), x1 = Math.Min(t.CoarseWidth - 1, x0 + 1);
            int w = t.CoarseWidth;
            int col0 = _oro[y0 * w + x0] * (2 * N - uy) + _oro[y1 * w + x0] * uy;
            int col1 = _oro[y0 * w + x1] * (2 * N - uy) + _oro[y1 * w + x1] * uy;
            Cell(t, cell, i, j, col0, col1, canopy, ref c);
        }

        const int N = WeatherTerrain.CoarseCells;

        /// <summary>
        /// One cell: the point read's and the batch read's one formula (so a lift's gust is the map's). <paramref name="col0"/>
        /// and <paramref name="col1"/> are the orographic factor's two neighbouring 512 m columns, already blended along y.
        /// </summary>
        void Cell(WeatherTerrain t, int cell, int i, int j, int col0, int col1, int canopy, ref WeatherCell c)
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
                // WX-38 v2: the bilinear orographic factor (one division for the whole weighted sum) × the bin's elevation factor.
                int fx = i * 2 + 1 - N;
                int x0 = Math.Max(0, Math.Min(t.CoarseWidth - 1, FloorDiv(fx, 2 * N)));
                int ux = WeatherMath.Clamp(fx - x0 * 2 * N, 0, 2 * N);
                int oro = (int)(((long)col0 * (2 * N - ux) + (long)col1 * ux) / (4L * N * N));
                c.Liquid = (int)WeatherMath.DivRound((long)_liquid * _precipBin[bin] * oro, 1_000_000);   // rounded once, as the reference points'
                c.SnowTenthsIn = (int)WeatherMath.DivRound((long)c.Liquid * _snowFactor[k], 100_000);   // as WeatherAir.SnowTenthsIn
            }
            else { c.Liquid = 0; c.SnowTenthsIn = 0; }
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
            y0 = Math.Max(0, y0);
            y1 = Math.Min(t.Depth, y1);
            int width = t.Width, tilesWide = (width + 63) / 64, cw = t.CoarseWidth;
            for (int j = y0; j < y1; j++)
            {
                // The orographic factor blended along y once per row, per 512 m column.
                int fy = j * 2 + 1 - N, r0 = Math.Max(0, Math.Min(t.CoarseDepth - 1, FloorDiv(fy, 2 * N))), r1 = Math.Min(t.CoarseDepth - 1, r0 + 1);
                int uy = WeatherMath.Clamp(fy - r0 * 2 * N, 0, 2 * N);
                for (int x = 0; x < cw; x++) _rowOro[x] = _oro[r0 * cw + x] * (2 * N - uy) + _oro[r1 * cw + x] * uy;
                int row = j * width, outRow = (j - y0) * width;
                for (int i = 0; i < width; i++)
                {
                    if (tileMask != null && !tileMask[j / 64 * tilesWide + i / 64]) { i = (i / 64 + 1) * 64 - 1; continue; }
                    int fx = i * 2 + 1 - N, x0 = Math.Max(0, Math.Min(cw - 1, FloorDiv(fx, 2 * N))), x1 = Math.Min(cw - 1, x0 + 1);
                    Cell(t, row + i, i, j, _rowOro[x0], _rowOro[x1], canopy == null ? -1 : canopy[row + i], ref dest[outRow + i]);
                }
            }
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
            Array.Copy(o._oro, _oro, _oro.Length);
            Array.Copy(o._steerWind, _steerWind, _steerWind.Length);
            Array.Copy(o._steerDir, _steerDir, _steerDir.Length);
            _liquid = o._liquid; _wind = o._wind; _gustFactor = o._gustFactor; _direction = o._direction; _shortwave = o._shortwave;
        }
    }
}
