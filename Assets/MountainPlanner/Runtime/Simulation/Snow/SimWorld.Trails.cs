using System;
using MountainPlanner.Domain.Snow;

namespace MountainPlanner.Simulation
{
    /// <summary>
    /// Task 4.07 B1 part 2a: the trail snow in the world (SN-03 v3, SN-14 v3, SN-17, SN-23 v3, SN-24 v3, SN-30 v2).
    /// - **The layout** (<see cref="SimCommandKind.SetTrailSnow"/>, a view-sequence command sent after the snow's terrain,
    ///   after every load and when the trails change): the fine field is made over it, keeping the fine cells of every 8 m
    ///   cell the old layout shared and seeding the rest from the 8 m snow. No layout, no fine field: today's bytes.
    /// - **The hour** (<see cref="SnowHour"/>) shares each 8 m cell's change with its block, the melt weighted by packing.
    /// - **Between hours** (at the roll, the job landed): the hour's skier passes wear the lanes, and the night's grooming
    ///   runs at each trail's keyed hour. Each touched block's roll-up is written back to its 8 m cell, and the water the
    ///   8 m cells lost that way is this file's ledger term (<see cref="TrailRemovedToday"/>; saved with the sidecar since 2c).
    /// - Saved since 2c (sidecar v12, <c>SimWorld.TrailSave</c>): a reopened game takes the fine field back when the layout
    ///   arrives. The fine cells aren't in the hash (the hour's job is still writing them when the hash is taken).
    /// </summary>
    /// <summary>A grooming pass's strength (SN-23 v4, task 4.25's light and heavy): its reach along the centreline (mm),
    /// how far it moves snow toward the reach's mean (‰) and how far it packs toward the groomed density (‰); and the
    /// steepest grade (%) it works, 0 for any (task 4.25, GO-04: without a winch a pitch past the winch grade is skipped).</summary>
    public readonly struct GroomStrength
    {
        public readonly int ReachMm, Blend, Pack, MaxGrade;
        /// <summary>A heavy push may work a Bare trail; a light groom skips one (SN-23 v4).</summary>
        public readonly bool Push;
        public GroomStrength(int reachMm, int blend, int pack, bool push, int maxGrade = 0) { ReachMm = reachMm; Blend = blend; Pack = pack; Push = push; MaxGrade = maxGrade; }
        public GroomStrength WithMaxGrade(int grade) => new GroomStrength(ReachMm, Blend, Pack, Push, grade);
        public static GroomStrength Heavy(Pacing p) => new GroomStrength(p.GroomReachMm, p.GroomBlend, p.GroomPack, true);
        public static GroomStrength Light(Pacing p) => new GroomStrength(p.GroomLightReachMm, p.GroomLightBlend, p.GroomPack, false);
    }

    public sealed partial class SimWorld
    {
        TrailSnowLayout _trailLayout;
        FineSnow _fine;
        int[] _trailWeights = Array.Empty<int>(), _trailPasses = Array.Empty<int>();
        // Per trail: its fine cells sorted by distance along (grooming's running window), and where each trail's run starts.
        int[] _trailOrder = Array.Empty<int>(), _trailStart = Array.Empty<int>();
        long[] _groomPrefix = Array.Empty<long>();
        int[] _groomCount = Array.Empty<int>();
        int[] _groomTarget = Array.Empty<int>();
        bool[] _blockTouched = Array.Empty<bool>();
        /// <summary>The blocks the last night's pass worked (the readout's "groomed last night"), and how many trails.</summary>
        bool[] _blockWorked = Array.Empty<bool>();
        int _trailBlocksLastGroom, _trailsLastGroom, _trailBlocksPowder;
        int _trailDay = int.MinValue;
        long _trailRemovedToday, _trailRemovedTotal;
        /// <summary>Fine µm dropped by folds and reseeds (each block's remainder, under 1 µm an 8 m cell: the 8 m snow and its
        /// ledger never held it).</summary>
        long _trailFoldRemainder;
        int _trailDepthMm, _trailPackedPermille, _trailsGroomedToday, _trailBlocksGroomed;

        /// <summary>The fine field (null without a layout or live snow): tests and the readout, between hours only.</summary>
        internal FineSnow FineSnow => _fine;
        /// <summary>Water (8 m µm summed over cells) the trails' wear and grooming took off the 8 m field this face day, and
        /// in all: the snow ledger's own term, saved since 2c (Balance + this = 0).</summary>
        public long TrailRemovedToday => _trailRemovedToday;
        public long TrailRemovedTotal => _trailRemovedTotal;

        bool ApplyTrailSnow(object payload)
        {
            if (!(payload is TrailSnowLayout layout)) return false;
            if (layout.GridWidth <= 0 || layout.GridHeight <= 0) return false;
            if (_wxTerrain != null && (layout.GridWidth != _wxTerrain.Width || layout.GridHeight != _wxTerrain.Depth
                                       || layout.OriginXMm != _wxTerrain.OriginXMm || layout.OriginYMm != _wxTerrain.OriginYMm)) return false;
            JoinSnow();
            _trailLayout = layout;
            RebuildFine(keep: true);
            return true;
        }

        /// <summary>Whether the fine field came from a save's trail snow over the same layout (the log).</summary>
        internal ulong TrailLayoutHash => _trailLayout?.Hash() ?? 0;

        /// <summary>
        /// The fine field over the layout: a new <see cref="Domain.Snow.TrailSnowLayout"/>'s blocks keep the old field's
        /// cells where the 8 m cell and the fine size are the same (<paramref name="keep"/>), the rest seeded from the 8 m
        /// snow; old blocks the new layout drops fold back (their remainder, under 1 µm a cell, into the ledger term).
        /// </summary>
        void RebuildFine(bool keep)
        {
            var old = keep ? _fine : null;
            if (!keep) _trailPending = null;   // the 8 m snow was seeded again: a save's trail snow no longer belongs to it
            var saved = old == null ? _trailPending : null;   // 2c: a reopened save's trail snow, waiting for this layout
            var layout = _trailLayout;
            if (layout == null || !Snow.Live || layout.GridWidth != Snow.Width || layout.GridHeight != Snow.Height)
            {
                _fine = null;
                return;
            }
            var fine = new FineSnow(layout);
            if (saved?.Fine != null) AdoptSavedFine(fine, saved.Fine);
            else
                for (int b = 0; b < layout.Blocks; b++)
                {
                    int ob = old != null && old.BlockCells == fine.BlockCells ? old.Layout.BlockOf(layout.BlockCell[b]) : -1;
                    if (ob >= 0) fine.CopyBlock(old, ob, b); else fine.SeedBlock(Snow.Field, b);
                }
            if (old != null)
                for (int ob = 0; ob < old.Layout.Blocks; ob++)
                    if (layout.BlockOf(old.Layout.BlockCell[ob]) < 0) _trailFoldRemainder += old.Fold(ob, Snow.Field);
            _fine = fine;
            int bc = fine.BlockCells;
            if (_trailWeights.Length < bc) _trailWeights = new int[bc];
            _blockTouched = new bool[layout.Blocks];
            _blockWorked = new bool[layout.Blocks];
            _trailBlocksLastGroom = 0; _trailsLastGroom = 0;
            // Each trail's fine cells, sorted by (along, index): counted, then placed.
            int trails = layout.Trails, n = fine.Cells;
            _trailStart = new int[trails + 1];
            for (int f = 0; f < n; f++) if (layout.Owner[f] >= 0) _trailStart[layout.Owner[f] + 1]++;
            for (int t = 0; t < trails; t++) _trailStart[t + 1] += _trailStart[t];
            _trailOrder = new int[_trailStart[trails]];
            var at = new int[trails];
            for (int f = 0; f < n; f++) { int t = layout.Owner[f]; if (t >= 0) _trailOrder[_trailStart[t] + at[t]++] = f; }
            var along = layout.AlongMm;
            for (int t = 0; t < trails; t++)
                Array.Sort(_trailOrder, _trailStart[t], _trailStart[t + 1] - _trailStart[t],
                           Comparer.For(along));
            _groomPrefix = new long[_trailOrder.Length + 1];
            _groomTarget = new int[_trailOrder.Length];
            _groomCount = new int[_trailOrder.Length + 1];
            AllocReadiness(layout);
            if (saved != null)
            {
                AdoptSavedRatings(saved);
                SurfacesNow();   // task 4.07 B2: the card's surface line from the snow just taken back
                _trailPending = null;
                _trailBoardRestored = false;   // the board is the fine snow's again
            }
            GroomLayoutChanged();   // task 4.25: the grooming's per-trail state follows the trails by id
        }

        /// <summary>Orders fine cells by their distance along, then by index (stable bytes).</summary>
        sealed class Comparer : System.Collections.Generic.IComparer<int>
        {
            readonly int[] _along;
            Comparer(int[] along) { _along = along; }
            public static Comparer For(int[] along) => new Comparer(along);
            public int Compare(int a, int b) { int c = _along[a].CompareTo(_along[b]); return c != 0 ? c : a.CompareTo(b); }
        }

        /// <summary>The hour roll's trail work (before the hour's snow starts): the last hour's wear, then the night's grooming.</summary>
        internal void HourStepTrails(long hour)
        {
            if (_fine == null) return;
            int day = (int)(hour / 24);
            if (day != _trailDay) { _trailDay = day; _trailRemovedToday = 0; _trailsGroomedToday = 0; }
            Array.Clear(_blockTouched, 0, _blockTouched.Length);
            Wear();
            GroomHour(hour);   // task 4.25: the shifts, Autopilot and the fleet (GO-03…GO-09)
            RollUpTouched();
            Summarise();
            RateTrails();
        }

        /// <summary>The readout's trail numbers, once an hour.</summary>
        void Summarise()
        {
            var fine = _fine;
            long depth = 0, n = 0, packed = 0;
            for (int f = 0; f < fine.Cells; f++)
            {
                if (_trailLayout.Owner[f] < 0) continue;
                n++; depth += fine.DepthUm(f);
                if ((fine.Flags[f] & FineSnow.Packed) != 0 && fine.Swe[f] > 0) packed++;
            }
            _trailDepthMm = n == 0 ? 0 : (int)(depth / n / 1000);
            _trailPackedPermille = n == 0 ? 0 : (int)(packed * 1000 / n);
            int mg = 0, powder = 0;
            foreach (int c in _trailLayout.BlockCell)
            {
                if (Snow.Field.Pack[c].Swe <= 0) continue;
                var t = (SnowType)Snow.Field.Type[c];
                if (t == SnowType.MG) mg++;
                else if (t == SnowType.P || t == SnowType.WP) powder++;
            }
            _trailBlocksGroomed = mg;
            _trailBlocksPowder = powder;
        }

        void WriteTrails(SimSnapshot s)
        {
            s.TrailFineMetres = _fine?.Layout.FineMetres ?? 0;
            s.TrailFineCells = _fine?.Cells ?? 0;
            s.TrailDepthMm = _fine != null ? _trailDepthMm : 0;
            s.TrailPackedPermille = _fine != null ? _trailPackedPermille : 0;
            s.TrailsGroomedToday = _trailsGroomedToday;
            s.TrailBlocks = _fine?.Layout.Blocks ?? 0;
            s.TrailBlocksGroomed = _fine != null ? _trailBlocksGroomed : 0;
            s.TrailBlocksLastGroom = _fine != null ? _trailBlocksLastGroom : 0;
            s.TrailsLastGroom = _fine != null ? _trailsLastGroom : 0;
            s.TrailBlocksPowder = _fine != null ? _trailBlocksPowder : 0;
            s.TrailRemovedTotal = _trailRemovedTotal;
            s.TrailSnowWaiting = TrailSnowWaiting;
            s.TrailBlocksRestored = _trailBlocksRestored;
            s.TrailBlocksReseeded = _trailBlocksReseeded;
            s.TrailsCovered = TrailBoardLive ? _trailsCovered : 0;
            s.TrailsMarginal = TrailBoardLive ? _trailsMarginal : 0;
            s.TrailsBare = TrailBoardLive ? _trailsBare : 0;
            s.LateSeason = TrailBoardLive && _lateSeason;
        }

        /// <summary>SN-24 v3, SN-14 v3 rule 6, SN-17: the hour's passes per segment and lane onto the lane's fine cells.</summary>
        void Wear()
        {
            var net = _guestsNetwork;
            var L = _trailLayout;
            if (net == null || L.NetworkHash != net.ShapeHash) return;
            int lanes = Pacing.WearBands;
            int need = net.EdgeFrom.Length * lanes;
            if (_trailPasses.Length < need) _trailPasses = new int[need];
            TakeHourPasses(_trailPasses);
            ApplyWear(_trailPasses, net.EdgeFrom.Length);
        }

        /// <summary>The wear of <paramref name="passes"/> (per edge × lane, <paramref name="edges"/> edges) on the fine cells.</summary>
        internal void ApplyWear(int[] passes, int edges)
        {
            var L = _trailLayout;
            int lanes = Pacing.WearBands;
            var fine = _fine;
            if (fine == null) return;
            int bc = fine.BlockCells;
            for (int f = 0; f < fine.Cells; f++)
            {
                int e = L.Edge[f];
                if (e < 0 || e >= edges || L.Lane[f] >= lanes) continue;
                int n = passes[e * lanes + L.Lane[f]];
                if (n <= 0 || fine.Swe[f] <= 0) continue;
                int cell = L.BlockCell[f / bc];
                int grade = GradeAt(cell);
                fine.Passes[f] = (ushort)Math.Min(ushort.MaxValue, fine.Passes[f] + n);
                fine.Flags[f] |= FineSnow.Packed;
                long factor = Math.Max(Pacing.WearFactorMin, Math.Min(Pacing.WearFactorMax, (long)grade * 1000 / Pacing.WearGradeRef));
                long worn = Math.Min(fine.Swe[f], (long)n * Pacing.WearPerPassUm * factor / 1000);
                fine.Swe[f] -= (int)worn;
                if (grade >= Pacing.MogulGrade && (SnowType)fine.Type[f] != SnowType.MG)
                    fine.Moguls[f] = (byte)Math.Min(255, (long)fine.Passes[f] * Pacing.MogulsPerKPasses / 1000);
                var type = (SnowType)fine.Type[f];
                int count = fine.Passes[f];
                if (type == SnowType.P && count >= Pacing.PackedPasses) type = SnowType.PP;
                else if ((type == SnowType.PP || type == SnowType.MG) && count >= Pacing.HardPasses) type = SnowType.HP;
                else if ((type == SnowType.FG || type == SnowType.IS) && count >= Pacing.CrustPasses) type = SnowType.LG;
                fine.Type[f] = (byte)type;
                if (fine.Swe[f] == 0) ClearFine(f);
                _blockTouched[f / bc] = true;
            }
        }

        /// <summary>The 8 m cell's grade (%), from the snow's terrain normals (0 without them).</summary>
        int GradeAt(int cell)
        {
            var t = _snowTerrain;
            if (t == null || cell >= t.NormalX.Length) return 0;
            long nx = SnowTerrain.Micro(t.NormalX[cell]), ny = SnowTerrain.Micro(t.NormalY[cell]), nz = Math.Max(1, t.NormalZMicro(cell));
            return (int)Math.Min(1000, TrailSnowLayout.ISqrt(nx * nx + ny * ny) * 100 / nz);
        }

        /// <summary>
        /// SN-23 v4, one grooming pass over a trail at <paramref name="strength"/>; false when nothing on it was
        /// workable. Cells the groomer can't work (under the depth floor, over the push limit, or a strip narrower than the
        /// floor's width at that station) are left alone. The rest: water moved toward the running mean within the reach
        /// along the centreline (the full width), the trail's total exact; packed toward the groomed density; types per
        /// SN-14 v3; age, passes and moguls reset; a green push's flag cleared (SN-27 v2).
        /// </summary>
        internal bool GroomTrail(int t, in GroomStrength strength)
        {
            var L = _trailLayout;
            var fine = _fine;
            if (fine == null || t < 0 || t >= L.Trails) return false;
            if (!strength.Push && TrailLabelOf(t) == TrailSnowLabel.Bare) return false;   // SN-23 v4: a light groom skips Bare
            int s = L.FineMetres * 1000, bc = fine.BlockCells, maxGrade = strength.MaxGrade;
            long reach = strength.ReachMm / 2;
            bool Can(int f) => Workable(f) && (maxGrade <= 0 || GradeAt(L.BlockCell[f / bc]) <= maxGrade);
            {
                int a = _trailStart[t], z = _trailStart[t + 1];
                if (z <= a) return false;
                // Which cells the groomer works (-1 not): deep enough, not too deep, on a station (one fine cell of along)
                // whose workable strip is wide enough.
                for (int i = a; i < z;)
                {
                    int station = L.AlongMm[_trailOrder[i]] / s, j = i, workable = 0;
                    while (j < z && L.AlongMm[_trailOrder[j]] / s == station) { if (Can(_trailOrder[j])) workable++; j++; }
                    bool wide = (long)workable * s >= Pacing.GroomMinWidthMm;
                    for (int k = i; k < j; k++) _groomTarget[k] = wide && Can(_trailOrder[k]) ? 0 : -1;
                    i = j;
                }
                // Running sums of the workable cells' water and count, in along order.
                _groomPrefix[a] = 0; _groomCount[a] = 0;
                for (int k = a; k < z; k++)
                {
                    bool w = _groomTarget[k] >= 0;
                    _groomPrefix[k + 1] = _groomPrefix[k] + (w ? fine.Swe[_trailOrder[k]] : 0);
                    _groomCount[k + 1] = _groomCount[k] + (w ? 1 : 0);
                }
                if (_groomCount[z] == _groomCount[a]) return false;
                // Each workable cell toward the mean of the workable cells within the reach along (the full width).
                long moved = 0;
                for (int k = a, lo = a, hi = a; k < z; k++)
                {
                    if (_groomTarget[k] < 0) continue;
                    int f = _trailOrder[k];
                    long al = L.AlongMm[f];
                    while (L.AlongMm[_trailOrder[lo]] < al - reach) lo++;
                    if (hi < k) hi = k;
                    while (hi < z && L.AlongMm[_trailOrder[hi]] <= al + reach) hi++;
                    long sum = _groomPrefix[hi] - _groomPrefix[lo];
                    int cnt = _groomCount[hi] - _groomCount[lo];
                    long next = fine.Swe[f] + (sum / cnt - fine.Swe[f]) * strength.Blend / 1000;
                    _groomTarget[k] = (int)next;
                    moved += next - fine.Swe[f];
                }
                // Back to the trail's exact total: the difference spread evenly over the groomed cells, the rest a µm each
                // in along order (a gain is always coverable: the groomed cells then hold more than it).
                while (moved != 0)
                {
                    int eligible = 0;
                    for (int k = a; k < z; k++) if (_groomTarget[k] > 0 || (moved < 0 && _groomTarget[k] == 0)) eligible++;
                    if (eligible == 0) break;
                    long per = moved / eligible;
                    for (int k = a; k < z && moved != 0; k++)
                    {
                        int v = _groomTarget[k];
                        if (v < 0 || (moved > 0 && v == 0)) continue;
                        long take = per != 0 ? per : Math.Sign(moved);
                        if (take > v) take = v;
                        _groomTarget[k] = (int)(v - take);
                        moved -= take;
                    }
                }
                for (int k = a; k < z; k++)
                {
                    if (_groomTarget[k] < 0) continue;
                    int f = _trailOrder[k];
                    _blockTouched[f / bc] = true;
                    // The groomer works the surface: the new-snow count (SN-18 v2's 0.1 in) starts again from tonight, so
                    // the day's earlier flakes can't turn the corduroy back to powder.
                    Snow.Field.NewSince[_trailLayout.BlockCell[f / bc]] = 0;
                    _blockWorked[f / bc] = true;
                    fine.Swe[f] = _groomTarget[k];
                    if (fine.Swe[f] == 0) { ClearFine(f); continue; }
                    long d = fine.Density[f], want = (long)Pacing.GroomedDensity * FineSnow.DensityOne;
                    if (d < want) fine.Density[f] = (ushort)(d + (want - d) * strength.Pack / 1000);
                    var type = (SnowType)fine.Type[f];
                    int grooms = Math.Min(7, ((fine.Flags[f] & FineSnow.GroomMask) >> FineSnow.GroomShift) + 1);
                    if (type == SnowType.IS || type == SnowType.FG) type = SnowType.LG;
                    else type = grooms >= Pacing.GroomsToHard ? SnowType.HP : SnowType.MG;
                    fine.Type[f] = (byte)type;
                    fine.Flags[f] = (byte)((fine.Flags[f] & ~(FineSnow.GroomMask | FineSnow.RainWet | FineSnow.PushedGreen)) | FineSnow.Packed | (grooms << FineSnow.GroomShift));
                    fine.Age[f] = 0; fine.Passes[f] = 0; fine.Moguls[f] = 0;
                }
            }
            return true;
        }

        /// <summary>The groomer's floor and push limit (owner 2026-11-18): it can't work thin snow, nor push a deep pile.</summary>
        bool Workable(int f)
        {
            long depth = _fine.DepthUm(f);
            return depth >= Pacing.GroomMinDepthMm * 1000L && depth <= Pacing.GroomMaxDepthMm * 1000L;
        }

        void ClearFine(int f)
        {
            var fine = _fine;
            fine.Swe[f] = 0; fine.Density[f] = 0; fine.Type[f] = 0; fine.Flags[f] = 0; fine.Passes[f] = 0; fine.Moguls[f] = 0;
        }

        /// <summary>Each touched block's roll-up into its 8 m cell; what the 8 m cells lost is the ledger term.</summary>
        void RollUpTouched()
        {
            var fine = _fine;
            var field = Snow.Field;
            for (int b = 0; b < _blockTouched.Length; b++)
            {
                if (!_blockTouched[b]) continue;
                int c = _trailLayout.BlockCell[b];
                fine.RollUp(b, out int swe, out int density, out byte type);
                ref var p = ref field.Pack[c];
                long before = p.Swe;
                if (swe == 0) { field.Clear(c); }
                else
                {
                    p.Liquid = Math.Min(p.Liquid, swe); p.SurfaceLiquid = Math.Min(p.SurfaceLiquid, p.Liquid);
                    p.Swe = swe;
                    if (density > 0) p.Density = density;
                    field.Type[c] = type;
                }
                _trailRemovedToday += before - p.Swe;
                _trailRemovedTotal += before - p.Swe;
            }
        }
    }
}
