using System;
using System.IO;
using MountainPlanner.Domain.Water;

namespace MountainPlanner.Simulation
{
    /// <summary>
    /// Task 4.09's lakes (LK-01 v2…LK-08 v2, p4-02 §5): each water body's fall lag, ice and the snow on it, stepped once a face
    /// hour after the weather, in integers. The body table (<see cref="WaterBodyTable"/>) is the area's, built at open and sent
    /// with <see cref="SimCommandKind.SetWaterBodies"/>; the per-body records are the state, saved in the sidecar and hashed.
    /// - Lake time is calendar time: every degree-hour counts × SeasonCalendarDays ÷ SeasonDays (a face day stands for about
    ///   2.88 calendar days but carries one real day of temperatures).
    /// - The lag is kept in 0.1 °F-hours × SeasonDays; the ice as h² in 1e-8 in² (h in 1e-4 in); the snow on it as water
    ///   (1e-4 in). The thaw and the snow's melt carry their remainders, so no small hour rounds to nothing.
    /// - The records are matched to a table by id, then (lakes) by the nearest centroid within half the new radius, so a water
    ///   backfill that nudges a shape keeps its ice. A world read from a sidecar holds its records until the table arrives,
    ///   and hashes the same before and after: the hash reads the records, never the table.
    /// </summary>
    public sealed partial class SimWorld
    {
        WaterBodyTable _waterTable;
        int _lakeCount;
        WaterBodyKind[] _lakeKind = Array.Empty<WaterBodyKind>();
        ulong[] _lakeId = Array.Empty<ulong>();
        int[] _lakeCx = Array.Empty<int>(), _lakeCy = Array.Empty<int>(), _lakeRadius = Array.Empty<int>();
        bool[] _lakeFrozen = Array.Empty<bool>();
        long[] _lakeLag = Array.Empty<long>(), _lakeIce2 = Array.Empty<long>(), _lakeSwe = Array.Empty<long>();
        long[] _lakeThawRem = Array.Empty<long>(), _lakeMeltRem = Array.Empty<long>(), _lakeRunoff = Array.Empty<long>(), _lakeSink = Array.Empty<long>();
        short[] _lakeOn = Array.Empty<short>(), _lakeOut = Array.Empty<short>(), _lakeEventDay = Array.Empty<short>();
        /// <summary>The table hash the loaded sidecar's records were made against (0 when none): a changed table is logged.</summary>
        ulong _lakeSavedTableHash;

        /// <summary>The body table playing now, or null.</summary>
        public WaterBodyTable WaterTable => _waterTable;
        /// <summary>The per-body records (the table's order once it's set).</summary>
        public int LakeCount => _lakeCount;
        /// <summary>Whether the last table applied differs from the one the loaded sidecar's records were made against.</summary>
        public bool LakeTableChanged { get; private set; }

        // ---------------- the table ----------------

        /// <summary>The <see cref="SimCommandKind.SetWaterBodies"/> command: matches the records to the table (see the class).</summary>
        bool ApplyWaterBodies(WaterBodyTable t)
        {
            if (t == null) return false;
            if (_waterTable != null && _waterTable.Hash == t.Hash) return true;
            int max = 1 + Pacing.MaxLakes + Pacing.MaxPonds;
            if (t.Count > max) return false;
            if (_waterTable == null && _lakeCount > 0) LakeTableChanged = _lakeSavedTableHash != t.Hash;
            int n = t.Count;
            var kind = new WaterBodyKind[n];
            var id = new ulong[n];
            var cx = new int[n]; var cy = new int[n]; var radius = new int[n];
            var frozen = new bool[n];
            var lag = new long[n]; var ice2 = new long[n]; var swe = new long[n];
            var thawRem = new long[n]; var meltRem = new long[n]; var runoff = new long[n]; var sink = new long[n];
            var on = new short[n]; var off = new short[n]; var eventDay = new short[n];
            var taken = new bool[_lakeCount];
            for (int b = 0; b < n; b++)
            {
                var body = t[b];
                kind[b] = body.Kind; id[b] = body.Id; cx[b] = body.CentroidXMm; cy[b] = body.CentroidYMm; radius[b] = body.RadiusMm;
                on[b] = -1; off[b] = -1; eventDay[b] = -1;
                int from = -1;
                for (int k = 0; k < _lakeCount; k++)
                    if (!taken[k] && _lakeId[k] == body.Id && _lakeKind[k] == body.Kind) { from = k; break; }
                if (from < 0 && body.Kind == WaterBodyKind.Lake)
                {
                    long best = (long)body.RadiusMm * body.RadiusMm / 4;
                    for (int k = 0; k < _lakeCount; k++)
                    {
                        if (taken[k] || _lakeKind[k] != WaterBodyKind.Lake || t.IndexOf(_lakeId[k]) >= 0) continue;
                        long dx = _lakeCx[k] - body.CentroidXMm, dy = _lakeCy[k] - body.CentroidYMm, d = dx * dx + dy * dy;
                        if (d <= best) { best = d; from = k; }
                    }
                }
                if (from < 0) continue;
                taken[from] = true;
                frozen[b] = _lakeFrozen[from]; lag[b] = _lakeLag[from]; ice2[b] = _lakeIce2[from]; swe[b] = _lakeSwe[from];
                thawRem[b] = _lakeThawRem[from]; meltRem[b] = _lakeMeltRem[from]; runoff[b] = _lakeRunoff[from]; sink[b] = _lakeSink[from];
                on[b] = _lakeOn[from]; off[b] = _lakeOut[from]; eventDay[b] = _lakeEventDay[from];
            }
            _lakeKind = kind; _lakeId = id; _lakeCx = cx; _lakeCy = cy; _lakeRadius = radius; _lakeFrozen = frozen;
            _lakeLag = lag; _lakeIce2 = ice2; _lakeSwe = swe; _lakeThawRem = thawRem; _lakeMeltRem = meltRem; _lakeRunoff = runoff; _lakeSink = sink;
            _lakeOn = on; _lakeOut = off; _lakeEventDay = eventDay;
            _lakeCount = n;
            _waterTable = t;
            return true;
        }

        // ---------------- the hour ----------------

        /// <summary>The hour step's lakes (WX-06's order: after the weather).</summary>
        void HourStepLakes(long hour)
        {
            if (_waterTable == null) return;
            long yearHours = (long)Calendar.YearFaceDays * 24;
            int hourOfYear = (int)(hour % yearHours);
            short faceDay = (short)(hourOfYear / 24);
            if (hourOfYear == 0)
                for (int b = 0; b < _lakeCount; b++) { _lakeOn[b] = -1; _lakeOut[b] = -1; _lakeEventDay[b] = -1; }
            for (int b = 0; b < _lakeCount; b++)
            {
                LakeAir(_waterTable[b], out int temp, out int liquid, out int snowPermille);
                StepLake(b, _waterTable[b], temp, liquid, snowPermille, faceDay);
            }
        }

        /// <summary>One body's hour: the air (0.1 °F), the liquid (0.01 in) and its snow share (‰).</summary>
        internal void StepLake(int b, in WaterBody body, int temp, int liquid, int snowPermille, short faceDay)
        {
            var p = Pacing;
            int sd = p.SeasonDays, scd = p.SeasonCalendarDays;
            int depth = body.Kind == WaterBodyKind.Streams ? p.StreamsDepth : body.DepthTenthsFt;
            long cold = 320 - temp;   // 0.1 °F below freezing (negative: above)

            // The precipitation: snow water and rain in 1e-4 in.
            long fall = (long)Math.Max(0, liquid) * 100;
            long snowWater = fall * WeatherClamp(snowPermille) / 1000, rain = fall - snowWater;
            bool holds = _lakeFrozen[b] && IceOf(b) > p.IceOpenAt;
            if (holds)
            {
                _lakeSwe[b] += snowWater;
                long cap = (long)p.IceSnowMax * p.IceSnowDensity;   // LK-05 v2: depth (0.1 in) = water (1e-4 in) ÷ density
                if (_lakeSwe[b] > cap) { _lakeSink[b] += _lakeSwe[b] - cap; _lakeSwe[b] = cap; }
            }
            else _lakeSink[b] += snowWater + rain;   // SN-25: snow and rain on open water are a sink

            if (!_lakeFrozen[b])
            {
                // LK-03 v2: the lag, with warm hours giving heat back (never below 0).
                _lakeLag[b] = Math.Max(0, _lakeLag[b] + cold * scd);
                // Threshold: LakeLagPerFoot (0.1 °F-day/ft) × depth (0.1 ft) → °F-days ÷ 100 → 0.1 °F-hours × 240 → × SeasonDays.
                if (_lakeLag[b] * 100 >= (long)p.LakeLagPerFoot * depth * 240 * sd)
                {
                    _lakeFrozen[b] = true;
                    _lakeIce2[b] = 0; _lakeThawRem[b] = 0;
                    if (_lakeOn[b] < 0) _lakeOn[b] = faceDay;
                    LakeEvent(b, SimEventType.LakeFroze, faceDay);
                }
                return;
            }

            if (cold > 0)
            {
                // LK-04 v2: Stefan's law, h² += (α k)² ΔFDD, k = I ÷ (I + snow).
                long snow = _lakeSwe[b] / p.IceSnowDensity;
                long insulation = p.IceSnowInsulation;
                // α (0.01 in) → (1e-4 in)² per calendar °F-day: α² × 1e4; ΔFDD = cold (0.1 °F-h) × scd ÷ (sd × 240).
                long grow = (long)p.IceAlpha * p.IceAlpha * 10_000L * cold * scd / (sd * 240L);
                long k2 = insulation * insulation * 1_000_000L / ((insulation + snow) * (insulation + snow));
                _lakeIce2[b] += grow / 1_000_000L * k2 + grow % 1_000_000L * k2 / 1_000_000L;
                if (body.Kind == WaterBodyKind.Streams)
                {
                    long capH = (long)p.StreamsIceMax * 100;
                    if (_lakeIce2[b] > capH * capH) _lakeIce2[b] = capH * capH;
                }
            }
            else if (cold < 0)
            {
                long warm = -cold;
                // LK-05 v2: melt and rain take the snow first; it shields the ice.
                if (_lakeSwe[b] > 0)
                {
                    long num = (long)p.IceSnowMelt * warm * scd + _lakeMeltRem[b], den = 10L * sd;
                    long melt = num / den + rain * p.IceRainMelt / 1000;
                    _lakeMeltRem[b] = num % den;
                    melt = Math.Min(melt, _lakeSwe[b]);
                    _lakeSwe[b] -= melt;
                    _lakeRunoff[b] += melt;
                }
                else
                {
                    // LK-06 v2: 0.01 in per calendar °F-day × clamp(ref ÷ depth, ½, 2), in 1e-4 in.
                    long fNum = p.LakeThawRefDepth, fDen = depth;
                    if (fNum * 2 < fDen) { fNum = 1; fDen = 2; }
                    else if (fNum > 2 * fDen) { fNum = 2; fDen = 1; }
                    long num = (long)p.IceMeltPerDegDay * 100 * warm * scd * fNum + _lakeThawRem[b], den = sd * 240L * fDen;
                    long dh = num / den;
                    _lakeThawRem[b] = num % den;
                    long h = WaterBodyTable.ISqrt(_lakeIce2[b]);
                    h = Math.Max(0, h - dh);
                    _lakeIce2[b] = h * h;
                    if (h <= (long)p.IceOpenAt * 100)
                    {
                        // The thaw-out: open water, the FDD and the lag start again.
                        _lakeFrozen[b] = false;
                        _lakeIce2[b] = 0; _lakeLag[b] = 0; _lakeThawRem[b] = 0;
                        _lakeRunoff[b] += _lakeSwe[b]; _lakeSwe[b] = 0; _lakeMeltRem[b] = 0;
                        _lakeOut[b] = faceDay;
                        LakeEvent(b, SimEventType.LakeOpened, faceDay);
                    }
                }
            }
            if (rain > 0 && _lakeSwe[b] == 0 && _lakeFrozen[b]) _lakeRunoff[b] += rain;
        }

        static int WeatherClamp(int permille) => permille < 0 ? 0 : permille > 1000 ? 1000 : permille;

        /// <summary>LK-08 v2: at most one lake event per body per face day.</summary>
        void LakeEvent(int b, SimEventType type, short faceDay)
        {
            if (_lakeEventDay[b] == faceDay) return;
            _lakeEventDay[b] = faceDay;
            Events.Add(Tick, type, b, faceDay);
        }

        /// <summary>The hour's air over a body: its own cell's weather over the map, else the hour at its surface's height, else the stand-in.</summary>
        void LakeAir(in WaterBody body, out int temp, out int liquid, out int snowPermille)
        {
            var t = _waterTable;
            if (_wxTerrain != null && _wxFields != null && body.AirCell >= 0 && t.Width == _wxTerrain.Width && t.Depth == _wxTerrain.Depth
                && t.OriginXMm == _wxTerrain.OriginXMm && t.OriginYMm == _wxTerrain.OriginYMm)
            {
                _wxFields.At(body.AirCell, out var c);
                if (c.Known) { temp = c.Temp; liquid = c.Liquid; snowPermille = c.SnowPermille; return; }
            }
            if (_weather != null && _wxHourSet)
            {
                var s = SampleAt(_wxHour, body.SurfaceFeet);
                temp = s.Temp; liquid = s.Liquid; snowPermille = s.SnowPermille;
                return;
            }
            temp = _weatherTenthsF; liquid = 0; snowPermille = 0;
        }

        // ---------------- reads ----------------

        /// <summary>A record's ice (0.01 in).</summary>
        public int IceOf(int b) => (int)(WaterBodyTable.ISqrt(_lakeIce2[b]) / 100);
        /// <summary>A record's snow on the ice (0.1 in).</summary>
        public int IceSnowOf(int b) => (int)(_lakeSwe[b] / Pacing.IceSnowDensity);
        public bool FrozenOf(int b) => _lakeFrozen[b];
        public ulong LakeIdOf(int b) => _lakeId[b];
        public int IceOnOf(int b) => _lakeOn[b];
        public int IceOutOf(int b) => _lakeOut[b];
        /// <summary>The water a record has sent off the body (snow melt, rain on the ice) and lost into it (snow and rain on open water), 1e-4 in over its area.</summary>
        public long RunoffOf(int b) => _lakeRunoff[b];
        public long SinkOf(int b) => _lakeSink[b];

        /// <summary>LK-08 v2: a record's surface.</summary>
        public WaterSurfaceState LakeStateOf(int b) =>
            !_lakeFrozen[b] || IceOf(b) <= Pacing.IceOpenAt ? WaterSurfaceState.OpenWater
            : IceSnowOf(b) >= Pacing.SnowCoveredAt ? WaterSurfaceState.SnowCoveredIce : WaterSurfaceState.Ice;

        void WriteLakes(SimSnapshot s)
        {
            int n = _waterTable == null ? 0 : Math.Min(_lakeCount, s.WaterBodyIds.Length);
            s.WaterBodyCount = n;
            for (int b = 0; b < n; b++)
            {
                s.WaterBodyIds[b] = _lakeId[b];
                s.WaterStates[b] = LakeStateOf(b);
                s.IceHundredthsIn[b] = IceOf(b);
                s.IceSnowTenthsIn[b] = IceSnowOf(b);
                s.IceOnDay[b] = _lakeOn[b];
                s.IceOutDay[b] = _lakeOut[b];
            }
        }

        void HashLakes(ref StateHasher h)
        {
            // A world without records hashes as before, so the goldens' designs (no water) stand.
            if (_lakeCount == 0) return;
            h.Add(_lakeCount);
            for (int b = 0; b < _lakeCount; b++)
            {
                h.Add((long)_lakeKind[b]); h.Add(_lakeId[b]); h.Add(_lakeCx[b]); h.Add(_lakeCy[b]); h.Add(_lakeRadius[b]);
                h.Add(_lakeFrozen[b] ? 1 : 0); h.Add(_lakeLag[b]); h.Add(_lakeIce2[b]); h.Add(_lakeSwe[b]);
                h.Add(_lakeThawRem[b]); h.Add(_lakeMeltRem[b]); h.Add(_lakeRunoff[b]); h.Add(_lakeSink[b]);
                h.Add(_lakeOn[b]); h.Add(_lakeOut[b]); h.Add(_lakeEventDay[b]);
            }
        }

        // ---------------- the sidecar (v7) ----------------

        void WriteLakeState(SidecarWriter o)
        {
            o.U64(_waterTable != null ? _waterTable.Hash : _lakeSavedTableHash);
            o.I32(_lakeCount);
            for (int b = 0; b < _lakeCount; b++)
            {
                o.U8((byte)_lakeKind[b]); o.U64(_lakeId[b]); o.I32(_lakeCx[b]); o.I32(_lakeCy[b]); o.I32(_lakeRadius[b]);
                o.Bool(_lakeFrozen[b]); o.I64(_lakeLag[b]); o.I64(_lakeIce2[b]); o.I64(_lakeSwe[b]);
                o.I64(_lakeThawRem[b]); o.I64(_lakeMeltRem[b]); o.I64(_lakeRunoff[b]); o.I64(_lakeSink[b]);
                o.I32(_lakeOn[b]); o.I32(_lakeOut[b]); o.I32(_lakeEventDay[b]);
            }
        }

        void ReadLakeState(SidecarReader r)
        {
            _waterTable = null;
            _lakeSavedTableHash = r.U64();
            int n = r.Count(1 + Pacing.MaxLakes + Pacing.MaxPonds);
            SizeLakes(n);
            for (int b = 0; b < n; b++)
            {
                _lakeKind[b] = (WaterBodyKind)r.Enum(0, 2, "A water body's kind");
                _lakeId[b] = r.U64(); _lakeCx[b] = r.I32(); _lakeCy[b] = r.I32(); _lakeRadius[b] = r.I32();
                _lakeFrozen[b] = r.Bool(); _lakeLag[b] = r.I64(); _lakeIce2[b] = r.I64(); _lakeSwe[b] = r.I64();
                if (_lakeLag[b] < 0 || _lakeIce2[b] < 0 || _lakeSwe[b] < 0) throw new InvalidDataException("A water body's state is negative.");
                _lakeThawRem[b] = r.I64(); _lakeMeltRem[b] = r.I64(); _lakeRunoff[b] = r.I64(); _lakeSink[b] = r.I64();
                _lakeOn[b] = (short)r.I32(); _lakeOut[b] = (short)r.I32(); _lakeEventDay[b] = (short)r.I32();
            }
        }

        /// <summary>v1–v6 → v7 (task 4.09): no lake block. Every body starts as open water with no lag when the table arrives.</summary>
        void StartLakesForOlderSave()
        {
            _waterTable = null;
            _lakeSavedTableHash = 0;
            SizeLakes(0);
        }

        void SizeLakes(int n)
        {
            _lakeCount = n;
            _lakeKind = new WaterBodyKind[n]; _lakeId = new ulong[n];
            _lakeCx = new int[n]; _lakeCy = new int[n]; _lakeRadius = new int[n]; _lakeFrozen = new bool[n];
            _lakeLag = new long[n]; _lakeIce2 = new long[n]; _lakeSwe = new long[n];
            _lakeThawRem = new long[n]; _lakeMeltRem = new long[n]; _lakeRunoff = new long[n]; _lakeSink = new long[n];
            _lakeOn = new short[n]; _lakeOut = new short[n]; _lakeEventDay = new short[n];
        }
    }
}
