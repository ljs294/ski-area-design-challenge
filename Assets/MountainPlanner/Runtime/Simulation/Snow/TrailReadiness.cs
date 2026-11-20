using System;
using MountainPlanner.Domain.Snow;

namespace MountainPlanner.Simulation
{
    /// <summary>SN-20 v3 (owner 2026-11-18): a trail's snow, as the board reads it. None until the first hour has rated it.</summary>
    public enum TrailSnowLabel : byte
    {
        None = 0,
        /// <summary>A skiable line top to bottom with no walk, and <see cref="Pacing.ReadyCover"/> of the trail covered.</summary>
        Covered = 1,
        /// <summary>Many bare patches but skiable all the way down; in the late season, also a trail with a walk.</summary>
        Marginal = 2,
        /// <summary>It can't be skied top to bottom.</summary>
        Bare = 3,
    }

    /// <summary>
    /// Task 4.07 B1 part 2b: the trails' readiness and the snow the guests ski (SN-16 v3, SN-20 v3, GU-07 v2, GU-17 v2).
    /// - **Once an hour** (the roll, after the trail work): each segment's snow rating by ability band, and each trail's
    ///   skiable line. The line walks the trail top to bottom in rows one fine cell deep; a row's covered stretches at
    ///   least <see cref="Pacing.LineWidthMm"/> wide (or the row's own width, where the trail is narrower) count, and a row
    ///   is reached when one of them overlaps a reached stretch of the row above. A row with none is walked.
    /// - **At the flip** (with the lifts): Auto opens a Covered trail and keeps a Marginal one open; Bare closes a trail
    ///   whatever its switch. The first close by Bare after the season's midpoint starts the late season, when a trail
    ///   with a walk is Marginal and the walk costs time and pull (exp(−(feet ÷ L)²)).
    /// - A world without the trail snow keeps the stand-ins: every built trail ready, and the same quality everywhere.
    /// - Not saved in 2b: readiness comes again from the snow at the next hour; the late season reseeds (2c saves it).
    /// </summary>
    public sealed partial class SimWorld
    {
        /// <summary>exp(−x²) in ‰ for x = 0, 0.1, … 3.0 (the walk's pull), interpolated between.</summary>
        static readonly int[] WalkPull =
        {
            1000, 990, 961, 914, 852, 779, 698, 613, 527, 445, 368, 298, 237, 185, 141, 105,
            77, 56, 39, 27, 18, 12, 8, 5, 3, 2, 1, 1, 0, 0, 0,
        };

        // per layout trail
        TrailSnowLabel[] _trailLabel = Array.Empty<TrailSnowLabel>();
        int[] _trailWalkMm = Array.Empty<int>(), _trailCoverPermille = Array.Empty<int>(), _trailQuality = Array.Empty<int>();
        long[] _trailQSum = Array.Empty<long>();
        byte[] _trailMainType = Array.Empty<byte>();
        int[] _trailRainCells = Array.Empty<int>();
        int[] _trailNetOf = Array.Empty<int>();
        bool[] _trailGroomedNow = Array.Empty<bool>();
        int[] _typeCount = new int[16];
        // a row across a trail, and the reached and current stretches (lateral mm, lo and hi cell centres)
        int[] _rowBuf = Array.Empty<int>();
        /// <summary>Per entry of <c>_trailOrder</c>, set at a row's first entry: the trail's own shape breaks the line there
        /// (with every cell covered), so the line restarts without a walk (see <see cref="FindShapeBreaks"/>).</summary>
        bool[] _shapeBreak = Array.Empty<bool>();
        int[] _reachLo = Array.Empty<int>(), _reachHi = Array.Empty<int>(), _curLo = Array.Empty<int>(), _curHi = Array.Empty<int>();

        // per edge of the guests' network: the rating by band, and the walk on a Marginal trail
        int[] _edgeQuality = Array.Empty<int>(), _edgeQCells = Array.Empty<int>(), _edgeWalkMm = Array.Empty<int>();
        long[] _edgeQSum = Array.Empty<long>();
        SimNetwork _edgeTableNet;

        // per trail of the design's network
        SimNetwork _trailMapNet;
        byte[] _trailOpenNet = Array.Empty<byte>(), _trailLabelNet = Array.Empty<byte>();
        int[] _trailWalkFtNet = Array.Empty<int>(), _trailCoverNet = Array.Empty<int>(), _trailQualityNet = Array.Empty<int>();
        bool _lateSeason;
        int _lateDay;
        int _trailsCovered, _trailsMarginal, _trailsBare;

        /// <summary>Whether the late season has started (SN-20 v3), and on which face day of the label year.</summary>
        public bool LateSeason => _lateSeason;

        /// <summary>A layout trail's label (tests and the readout).</summary>
        internal TrailSnowLabel TrailLabelOf(int t) => t >= 0 && t < _trailLabel.Length ? _trailLabel[t] : TrailSnowLabel.None;
        internal int TrailWalkMmOf(int t) => t >= 0 && t < _trailWalkMm.Length ? _trailWalkMm[t] : 0;
        internal int TrailCoverPermilleOf(int t) => t >= 0 && t < _trailCoverPermille.Length ? _trailCoverPermille[t] : 0;

        /// <summary>The readiness scratch for a new layout (allocates: a layout arrives as a command, never per hour).</summary>
        void AllocReadiness(TrailSnowLayout layout)
        {
            int n = layout.Trails;
            _trailLabel = new TrailSnowLabel[n];
            _trailWalkMm = new int[n]; _trailCoverPermille = new int[n]; _trailQuality = new int[n];
            _trailQSum = new long[n]; _trailMainType = new byte[n]; _trailRainCells = new int[n];
            _trailGroomedNow = new bool[n];
            _trailNetOf = new int[n];
            for (int t = 0; t < n; t++) _trailNetOf[t] = -1;
            _trailMapNet = null;
            int widest = 0;
            for (int t = 0; t < n; t++) widest = Math.Max(widest, _trailStart[t + 1] - _trailStart[t]);
            _rowBuf = new int[widest];
            _reachLo = new int[widest]; _reachHi = new int[widest]; _curLo = new int[widest]; _curHi = new int[widest];
            _shapeBreak = new bool[_trailOrder.Length];
            for (int t = 0; t < n; t++) RateTrail(t, shapeOnly: true);
            _edgeTableNet = null;
        }

        /// <summary>The hour's ratings and lines (the roll, after the wear and the grooming).</summary>
        void RateTrails()
        {
            RateEdges();
            for (int t = 0; t < _trailLayout.Trails; t++) RateTrail(t);
            Array.Clear(_trailGroomedNow, 0, _trailGroomedNow.Length);
        }

        long CoverUm(int t) => (long)(_trailLayout.TrailGraded[t] ? Pacing.CoverGradedMm : Pacing.CoverNaturalMm) * 1000;

        bool CoveredCell(int f, long coverUm) => _fine.Swe[f] > 0 && _fine.DepthUm(f) >= coverUm;

        /// <summary>
        /// SN-16 v3 with GU-07 v2's taste: a covered fine cell's quality for a band (‰): the type's base × its age, × the
        /// passes, × the moguls for the bands below the trail's posted band; experts like powder and moguls, beginners
        /// don't like moguls or ice.
        /// </summary>
        int CellQuality(int f, int band, int posted)
        {
            var fine = _fine;
            int type = fine.Type[f];
            var qb = Pacing.QualityBase;
            if (type <= 0 || type > qb.Length) return 0;
            long q = (long)qb[type - 1] * Math.Max(Pacing.QualityAgeFloor, 1000 - Pacing.QualityAgePerHour * fine.Age[f]) / 1000;
            q = q * Math.Max(Pacing.QualityPassFloor, 1000 - (long)fine.Passes[f] * Pacing.QualityPassTenths / 10) / 1000;
            int moguls = fine.Moguls[f];
            if (band < posted) q = q * (1000 - (long)Pacing.QualityMogulPermille * moguls / 255) / 1000;
            bool bumpy = moguls >= Pacing.TasteMogulMin && moguls > 0;
            if (band == Pacing.Bands - 1 && (type == (int)SnowType.P || bumpy)) q = q * Pacing.TasteExpert / 1000;
            else if (band == 0 && (type == (int)SnowType.IS || bumpy)) q = q * Pacing.TasteBeginner / 1000;
            return (int)q;
        }

        /// <summary>Each segment's rating by band (uncovered cells count 0), and each trail's band mean and main surface.</summary>
        void RateEdges()
        {
            var L = _trailLayout;
            var fine = _fine;
            var net = _guestsNetwork;
            bool edges = net != null && L.NetworkHash == net.ShapeHash;
            int bands = Pacing.Bands;
            if (edges)
            {
                int n = net.EdgeFrom.Length;
                if (_edgeQCells.Length != n)
                {
                    _edgeQuality = new int[n * bands]; _edgeQSum = new long[n * bands];
                    _edgeQCells = new int[n]; _edgeWalkMm = new int[n];
                }
                Array.Clear(_edgeQSum, 0, _edgeQSum.Length);
                Array.Clear(_edgeQCells, 0, _edgeQCells.Length);
                Array.Clear(_edgeWalkMm, 0, _edgeWalkMm.Length);
                _edgeTableNet = net;
            }
            else _edgeTableNet = null;
            Array.Clear(_trailQSum, 0, _trailQSum.Length);
            Array.Clear(_trailRainCells, 0, _trailRainCells.Length);
            for (int t = 0; t < L.Trails; t++)
            {
                long cover = CoverUm(t);
                Array.Clear(_typeCount, 0, _typeCount.Length);
                int covered = 0;
                for (int k = _trailStart[t]; k < _trailStart[t + 1]; k++)
                {
                    int f = _trailOrder[k];
                    int e = L.Edge[f];
                    bool onEdge = edges && e >= 0 && e < _edgeQCells.Length;
                    if (onEdge) _edgeQCells[e]++;
                    if (!CoveredCell(f, cover)) continue;
                    covered++;
                    _typeCount[fine.Type[f] & 15]++;
                    if ((fine.Flags[f] & FineSnow.RainWet) != 0) _trailRainCells[t]++;
                    int posted = onEdge && e < _fitBand.Length ? _fitBand[e] : 0;
                    long mean = 0;
                    for (int b = 0; b < bands; b++)
                    {
                        int q = CellQuality(f, b, posted);
                        mean += q;
                        if (onEdge) _edgeQSum[e * bands + b] += q;
                    }
                    _trailQSum[t] += mean / bands;
                }
                int cells = _trailStart[t + 1] - _trailStart[t];
                _trailQuality[t] = cells == 0 ? 0 : (int)(_trailQSum[t] / cells);
                // The main surface (SN-21's majority), and a change of it with its cause (WX-52): groomed tonight, rain, else nature.
                int main = 0;
                for (int y = 1; y < _typeCount.Length; y++) if (_typeCount[y] > _typeCount[main]) main = y;
                if (covered == 0) main = 0;
                if (main != _trailMainType[t])
                {
                    int cause = _trailGroomedNow[t] ? 1 : _trailRainCells[t] * 2 > covered ? 2 : 0;
                    if (_trailMainType[t] != 0 || main != 0)
                        Events.Add(Tick, SimEventType.TrailSurfaceChanged, NetIndexOf(t), (long)main + ((long)cause << 8));
                    _trailMainType[t] = (byte)main;
                }
            }
            if (!edges) return;
            for (int e = 0; e < _edgeQCells.Length; e++)
                for (int b = 0; b < bands; b++)
                    _edgeQuality[e * bands + b] = _edgeQCells[e] == 0 ? StandInQuality : (int)(_edgeQSum[e * bands + b] / _edgeQCells[e]);
        }

        /// <summary>
        /// SN-20 v3: one trail's skiable line, its walk and its label. With <paramref name="shapeOnly"/> (once, when the layout
        /// arrives) every cell counts as covered and it marks the rows where the trail's own shape breaks the line: near a join
        /// a row one cell deep holds only the scattered cells the other trail doesn't own, or it shifts aside from the row above
        /// (the drawn Jackson Hole broke 13 of its 20 trails so, at 100 % cover, 2026-11-19). The line restarts there without a
        /// walk: only bare snow walks.
        /// </summary>
        void RateTrail(int t, bool shapeOnly = false)
        {
            var L = _trailLayout;
            int a = _trailStart[t], z = _trailStart[t + 1];
            if (z <= a) { if (!shapeOnly) _trailLabel[t] = TrailSnowLabel.None; return; }
            int s = L.FineMetres * 1000;
            long cover = CoverUm(t);
            var lat = L.LateralMm;
            bool edges = _edgeTableNet != null;
            int coveredCells = 0, reached = 0;
            long walk = 0;
            bool walking = true;   // from the top: rows before the line starts are walked
            for (int i = a; i < z;)
            {
                int row = L.AlongMm[_trailOrder[i]] / s, n = 0, j = i;
                while (j < z && L.AlongMm[_trailOrder[j]] / s == row) _rowBuf[n++] = _trailOrder[j++];
                // Across the row, left to right (then by index: stable bytes).
                for (int x = 1; x < n; x++)
                {
                    int v = _rowBuf[x], y = x - 1;
                    while (y >= 0 && (lat[_rowBuf[y]] > lat[v] || (lat[_rowBuf[y]] == lat[v] && _rowBuf[y] > v))) { _rowBuf[y + 1] = _rowBuf[y]; y--; }
                    _rowBuf[y + 1] = v;
                }
                long own = (long)lat[_rowBuf[n - 1]] - lat[_rowBuf[0]] + s;
                long need = Math.Min(Pacing.LineWidthMm, own);
                // The row's covered stretches wide enough for the line (neighbours across a row lie within 1.5 cells).
                int cn = 0;
                for (int k = 0; k < n;)
                {
                    if (!shapeOnly && !CoveredCell(_rowBuf[k], cover)) { k++; continue; }
                    int st = k++;
                    coveredCells++;
                    while (k < n && (shapeOnly || CoveredCell(_rowBuf[k], cover)) && (long)lat[_rowBuf[k]] - lat[_rowBuf[k - 1]] <= s * 3 / 2) { coveredCells++; k++; }
                    long w = (long)lat[_rowBuf[k - 1]] - lat[_rowBuf[st]] + s;
                    if (w >= need) { _curLo[cn] = lat[_rowBuf[st]]; _curHi[cn] = lat[_rowBuf[k - 1]]; cn++; }
                }
                // Reached: any stretch after a walk (the line starts again), else one overlapping a reached stretch above.
                int rn = 0;
                if (cn > 0)
                {
                    if (walking || (!shapeOnly && _shapeBreak[i])) { rn = cn; walking = false; }
                    else
                        for (int c = 0; c < cn; c++)
                            for (int p = 0; p < reached; p++)
                                if (_curLo[c] <= (long)_reachHi[p] + s && _reachLo[p] <= (long)_curHi[c] + s)
                                {
                                    _curLo[rn] = _curLo[c]; _curHi[rn] = _curHi[c]; rn++;
                                    break;
                                }
                }
                if (shapeOnly && rn == 0) { _shapeBreak[i] = true; rn = cn; walking = cn == 0; }
                if (shapeOnly) { }
                else if (rn == 0 && _shapeBreak[i]) walking = true;   // the shape's own break, with no line: no walk, restart below
                else if (rn == 0)
                {
                    walking = true;
                    walk += s;
                    int e = L.Edge[_rowBuf[0]];
                    if (edges && e >= 0 && e < _edgeWalkMm.Length) _edgeWalkMm[e] += s;
                }
                if (rn > 0)
                {
                    for (int c = 0; c < rn; c++) { _reachLo[c] = _curLo[c]; _reachHi[c] = _curHi[c]; }
                    reached = rn;
                }
                i = j;
            }
            if (shapeOnly) return;
            int cells = z - a;
            int share = (int)((long)coveredCells * 1000 / cells);
            _trailCoverPermille[t] = share;
            _trailWalkMm[t] = (int)Math.Min(int.MaxValue, walk);
            TrailSnowLabel label;
            if (coveredCells == 0) label = TrailSnowLabel.Bare;
            else if (walk == 0) label = share >= Pacing.ReadyCover ? TrailSnowLabel.Covered : TrailSnowLabel.Marginal;
            else label = _lateSeason && FeetOf(walk) <= 2L * WalkToleranceFt() ? TrailSnowLabel.Marginal : TrailSnowLabel.Bare;
            _trailLabel[t] = label;
        }

        static int FeetOf(long mm) => (int)Math.Min(int.MaxValue, mm * 10 / 3048);

        /// <summary>SN-20 v3: the late season's walk tolerance today, feet (its start value before the late season).</summary>
        int WalkToleranceFt()
        {
            if (!_lateSeason) return Pacing.LateWalkStartFt;
            int day = FaceDayNow % Calendar.YearFaceDays;
            int span = Math.Max(1, Calendar.April1FaceDay - _lateDay);
            int x = Math.Max(0, Math.Min(span, day - _lateDay));
            return Pacing.LateWalkStartFt + (Pacing.LateWalkEndFt - Pacing.LateWalkStartFt) * x / span;
        }

        /// <summary>The design network's index of a layout trail (−1 before the first board, or a trail it doesn't have).</summary>
        int NetIndexOf(int t) => t >= 0 && t < _trailNetOf.Length ? _trailNetOf[t] : -1;

        /// <summary>
        /// The trails' board at the hour's flip (before the lifts', so their way down reads it): Auto opens at Covered and
        /// keeps a Marginal trail open; Bare closes. A change rebuilds the guests' routes; the first close by Bare after the
        /// midpoint starts the late season.
        /// </summary>
        void BoardTrails()
        {
            if (_fine == null) return;
            var net = Design.Network;
            if (net == null) return;
            var L = _trailLayout;
            int n = net.TrailIds.Length;
            if (_trailMapNet != net || _trailOpenNet.Length != n)
            {
                var oldIds = _trailMapNet?.TrailIds;
                var oldOpen = _trailOpenNet;
                _trailOpenNet = new byte[n]; _trailLabelNet = new byte[n];
                _trailWalkFtNet = new int[n]; _trailCoverNet = new int[n]; _trailQualityNet = new int[n];
                for (int k = 0; k < n; k++)
                {
                    _trailOpenNet[k] = 2;   // not yet rated: open, as the stand-in had it
                    if (oldIds != null)
                        for (int o = 0; o < oldIds.Length && o < oldOpen.Length; o++)
                            if (oldIds[o] == net.TrailIds[k]) { _trailOpenNet[k] = oldOpen[o]; break; }
                }
                for (int t = 0; t < L.Trails; t++)
                {
                    _trailNetOf[t] = -1;
                    for (int k = 0; k < n; k++) if (net.TrailIds[k] == L.TrailId[t]) { _trailNetOf[t] = k; break; }
                }
                _trailMapNet = net;
            }
            int day = FaceDayNow % Calendar.YearFaceDays;
            if (_lateSeason && day < _lateDay) _lateSeason = false;   // a new label year
            bool changed = false, closedByBare = false;
            _trailsCovered = _trailsMarginal = _trailsBare = 0;
            int walkL = WalkToleranceFt();
            for (int t = 0; t < L.Trails; t++)
            {
                int k = _trailNetOf[t];
                if (k < 0) continue;
                var label = _trailLabel[t];
                byte was = _trailOpenNet[k];
                byte open = label == TrailSnowLabel.Bare ? (byte)0
                          : label == TrailSnowLabel.Covered ? (byte)1
                          : label == TrailSnowLabel.Marginal ? (was == 0 ? (byte)0 : (byte)1)
                          : was;
                if ((byte)label != _trailLabelNet[k] && label != TrailSnowLabel.None)
                    Events.Add(Tick, SimEventType.TrailReadinessChanged, k, (long)label + ((long)open << 8));
                if ((open != 0) != (was != 0)) changed = true;
                if (was != 0 && open == 0) closedByBare = true;
                _trailOpenNet[k] = open;
                _trailLabelNet[k] = (byte)label;
                _trailWalkFtNet[k] = FeetOf(_trailWalkMm[t]);   // a Bare trail shows its walk too
                _trailCoverNet[k] = _trailCoverPermille[t];
                _trailQualityNet[k] = _trailQuality[t];
                if (label == TrailSnowLabel.Covered) _trailsCovered++;
                else if (label == TrailSnowLabel.Marginal) _trailsMarginal++;
                else if (label == TrailSnowLabel.Bare) _trailsBare++;
            }
            int mid = (Calendar.December1FaceDay + Calendar.April1FaceDay) / 2;
            if (closedByBare && !_lateSeason && Phase == SeasonPhase.Season && day >= mid)
            {
                _lateSeason = true;
                _lateDay = day;
                Events.Add(Tick, SimEventType.LateSeasonStarted, -1, day);
            }
            if (changed) GuestBoardChanged();
        }

        /// <summary>SN-20 v3's readiness of a design-network trail: the board's, or every trail without the trail snow.</summary>
        internal bool TrailReady(int trail) => _fine == null || trail < 0 || trail >= _trailOpenNet.Length || _trailOpenNet[trail] != 0;

        /// <summary>Built trails open by the board (the snapshot's <c>TrailsReady</c>).</summary>
        int TrailsReadyNow()
        {
            int built = Design.BuiltTrails;
            if (_fine == null) return built;
            int closed = 0;
            for (int k = 0; k < _trailOpenNet.Length; k++) if (_trailOpenNet[k] == 0) closed++;
            return Math.Max(0, built - closed);
        }

        /// <summary>Whether the guests' rating table is for the network they stand on.</summary>
        bool EdgeTableLive => _fine != null && _edgeTableNet != null && _edgeTableNet == _guestsNetwork;

        /// <summary>The label of an edge's trail on the guests' network (None without the trail snow).</summary>
        TrailSnowLabel EdgeLabel(int edge)
        {
            if (_fine == null) return TrailSnowLabel.None;
            int t = _guestsNetwork.EdgeTrail[edge];
            return t >= 0 && t < _trailLabelNet.Length ? (TrailSnowLabel)_trailLabelNet[t] : TrailSnowLabel.None;
        }

        /// <summary>GU-07 v2: the walk's pull on an edge's trail, exp(−(feet ÷ L)²) in ‰ (1000 with no walk).</summary>
        int WalkFactorOf(int edge)
        {
            if (_fine == null) return 1000;
            int t = _guestsNetwork.EdgeTrail[edge];
            if (t < 0 || t >= _trailWalkFtNet.Length || _trailWalkFtNet[t] <= 0) return 1000;
            return WalkPullOf(_trailWalkFtNet[t], WalkToleranceFt());
        }

        /// <summary>exp(−(feet ÷ L)²) in ‰, from the table (integer only: the same on every machine).</summary>
        internal static int WalkPullOf(int feet, int toleranceFt)
        {
            if (feet <= 0) return 1000;
            long x = (long)feet * 100 / Math.Max(1, toleranceFt);   // hundredths of L
            if (x >= (WalkPull.Length - 1) * 10) return 0;
            int i = (int)(x / 10), r = (int)(x % 10);
            return WalkPull[i] + (WalkPull[i + 1] - WalkPull[i]) * r / 10;
        }

        /// <summary>Tests: rate the trails now (between hours), and set the late season.</summary>
        internal void RateTrailsNow() { if (_fine != null) RateTrails(); }
        internal void SetLateSeason(bool on, int day) { _lateSeason = on; _lateDay = day; }

        /// <summary>GU-09: the walk on a Marginal trail's segment, ticks (0 on any other).</summary>
        int EdgeWalkTicks(int edge)
        {
            if (!EdgeTableLive || edge >= _edgeWalkMm.Length || _edgeWalkMm[edge] <= 0 || EdgeLabel(edge) != TrailSnowLabel.Marginal) return 0;
            long ms = (long)_edgeWalkMm[edge] * 1000 / Math.Max(1, Pacing.WalkSpeedMmS);
            return MsToTicks((int)Math.Min(int.MaxValue, ms), 1000);
        }

        /// <summary>GU-04: the mean rating over the open trails' segments (the stand-in without the trail snow).</summary>
        int OpenTrailQuality()
        {
            if (!EdgeTableLive) return StandInQuality;
            var net = _guestsNetwork;
            long sum = 0; int n = 0, bands = Pacing.Bands;
            for (int e = 0; e < _edgeQCells.Length; e++)
            {
                if (_edgeQCells[e] == 0 || net.EdgeKindOf[e] != (byte)SimEdgeKind.TrailSegment || !TrailOpen(net.EdgeTrail[e])) continue;
                for (int b = 0; b < bands; b++) sum += _edgeQuality[e * bands + b];
                n += bands;
            }
            return n == 0 ? StandInQuality : (int)(sum / n);
        }

        void HashTrailBoard(ref StateHasher h)
        {
            if (_fine == null) return;   // the goldens (no trail snow) hash as before
            h.Add(_lateSeason ? 1 : 0); h.Add(_lateDay);
            for (int k = 0; k < _trailOpenNet.Length; k++) { h.Add(_trailOpenNet[k]); h.Add(_trailLabelNet[k]); h.Add(_trailWalkFtNet[k]); }
        }

        void WriteTrailBoard(SimSnapshot s, int k)
        {
            bool live = _fine != null && k < _trailLabelNet.Length;
            if (!live) { s.TrailBoard[k] = 0; s.TrailCoverPct[k] = 0; return; }
            int q = _trailQualityNet[k];
            int word = q >= 800 ? 3 : q >= 600 ? 2 : q >= 400 ? 1 : 0;   // SN-16's words
            s.TrailBoard[k] = (ushort)(Math.Min(0xFFF, _trailWalkFtNet[k]) | ((_trailLabelNet[k] & 3) << 12) | (word << 14));
            s.TrailCoverPct[k] = (byte)(_trailCoverNet[k] / 10);
        }
    }
}
