using System;
using System.IO;

namespace MountainPlanner.Simulation
{
    /// <summary>
    /// Task 4.07 B1 part 2c: the trail snow in the sidecar (v12, <see cref="SimSidecar.TrailSnowVersion"/>).
    /// - **What's saved:** the day's trail counters and the fold remainder; with the board live, the late season, the
    ///   readouts, the board per trail of the design's network (by id), the guests' snow table and the network shape it was
    ///   rated on, each layout trail's label, walk, cover, rating and main surface (all worked out before the hour's snow ran,
    ///   so none can be rebuilt from the saved snow), and the fine cells (<see cref="FineSnowCodec"/>, encoded off the worker).
    /// - **Reopening:** the board is live at once, so the state hashes as the game that saved it and the guests route the
    ///   same. The fine cells wait for the game's layout: the same layout takes every square back exactly, with its trails'
    ///   ratings; another keeps the blocks both share (the rest seed from the 8 m snow, as any edit's do). A block whose water
    ///   doesn't add up to its 8 m cell's is seeded again and counted.
    /// - Saving again before the layout arrives writes the same bytes: the waiting snow is written as it was read.
    /// </summary>
    public sealed partial class SimWorld
    {
        /// <summary>A reopened save's trail snow, waiting for the layout.</summary>
        sealed class TrailSnowSaved
        {
            public ulong LayoutHash, EdgeShape;
            public int FineMetres, GridWidth, GridHeight;
            public byte[] Label = Array.Empty<byte>(), MainType = Array.Empty<byte>();
            public int[] WalkMm = Array.Empty<int>(), Cover = Array.Empty<int>(), Quality = Array.Empty<int>();
            public int BlocksLastGroom, TrailsLastGroom;
            public FinePlanes Fine;
        }

        TrailSnowSaved _trailPending;
        int _trailBlocksReseeded, _trailBlocksRestored;

        /// <summary>Whether a reopened save's trail snow is still waiting for the layout (the game holds the clock).</summary>
        public bool TrailSnowWaiting => _trailPending?.Fine != null;
        /// <summary>Blocks of a save's trail snow that didn't add up to their 8 m cell and were seeded again (the log).</summary>
        public int TrailBlocksReseeded => _trailBlocksReseeded;
        /// <summary>Blocks of a save's trail snow taken back exactly (the log).</summary>
        public int TrailBlocksRestored => _trailBlocksRestored;

        const int MaxBoardTrails = 1 << 16, MaxBoardEdges = 1 << 20;

        void WriteTrailState(SidecarWriter o)
        {
            o.I32(_trailDay); o.I64(_trailRemovedToday); o.I64(_trailRemovedTotal); o.I64(_trailFoldRemainder); o.I32(_trailsGroomedToday);
            bool live = TrailBoardLive;
            o.Bool(live);
            if (!live) return;
            o.Bool(_lateSeason); o.I32(_lateDay);
            o.I32(_trailsCovered); o.I32(_trailsMarginal); o.I32(_trailsBare);
            o.I32(_trailDepthMm); o.I32(_trailPackedPermille); o.I32(_trailBlocksGroomed); o.I32(_trailBlocksPowder);
            // The board, per trail of the design's network.
            int n = Math.Min(_boardIds.Length, _trailOpenNet.Length);
            o.I32(n);
            for (int k = 0; k < n; k++)
            {
                o.U64(_boardIds[k]); o.U8(_trailOpenNet[k]); o.U8(_trailLabelNet[k]);
                o.I32(_trailWalkFtNet[k]); o.I32(_trailCoverNet[k]); o.I32(_trailQualityNet[k]);
            }
            // The guests' snow table.
            bool table = _edgeTableShape != 0;
            int edges = table ? _edgeQCells.Length : 0;
            o.U64(table ? _edgeTableShape : 0);
            o.I32(edges);
            for (int e = 0; e < edges; e++)
            {
                o.I32(_edgeQCells[e]); o.I32(_edgeWalkMm[e]);
                for (int b = 0; b < Pacing.Bands; b++) o.I32(_edgeQuality[e * Pacing.Bands + b]);
            }
            // The layout's trails and the fine cells: the live ones, else a reopened save's still waiting.
            var p = _trailPending;
            if (_fine != null)
            {
                var L = _trailLayout;
                o.U64(L.Hash()); o.I32(L.FineMetres); o.I32(L.GridWidth); o.I32(L.GridHeight);
                o.I32(_trailBlocksLastGroom); o.I32(_trailsLastGroom);
                o.I32(L.Trails);
                for (int t = 0; t < L.Trails; t++)
                {
                    o.U8((byte)_trailLabel[t]); o.I32(_trailWalkMm[t]); o.I32(_trailCoverPermille[t]); o.I32(_trailQuality[t]); o.U8(_trailMainType[t]);
                }
                o.Bool(true);
                var planes = FinePlanes.Of(_fine);   // the worker's copy; encoded with the save, off the worker
                o.Defer(w => FineSnowCodec.Write(w, planes), "the trail snow");
            }
            else if (p != null)
            {
                o.U64(p.LayoutHash); o.I32(p.FineMetres); o.I32(p.GridWidth); o.I32(p.GridHeight);
                o.I32(p.BlocksLastGroom); o.I32(p.TrailsLastGroom);
                o.I32(p.Label.Length);
                for (int t = 0; t < p.Label.Length; t++) { o.U8(p.Label[t]); o.I32(p.WalkMm[t]); o.I32(p.Cover[t]); o.I32(p.Quality[t]); o.U8(p.MainType[t]); }
                o.Bool(p.Fine != null);
                if (p.Fine != null) { var planes = p.Fine; o.Defer(w => FineSnowCodec.Write(w, planes), "the trail snow"); }
            }
            else
            {
                o.U64(0); o.I32(0); o.I32(0); o.I32(0); o.I32(0); o.I32(0); o.I32(0);
                o.Bool(false);
            }
        }

        void ReadTrailState(SidecarReader r)
        {
            _trailDay = r.I32(); _trailRemovedToday = r.I64(); _trailRemovedTotal = r.I64(); _trailFoldRemainder = r.I64(); _trailsGroomedToday = r.I32();
            if (!r.Bool()) return;
            _lateSeason = r.Bool(); _lateDay = r.Ranged(0, Calendar.YearFaceDays - 1, "The late season's day");
            _trailsCovered = r.Ranged(0, MaxBoardTrails, "The covered trails"); _trailsMarginal = r.Ranged(0, MaxBoardTrails, "The marginal trails");
            _trailsBare = r.Ranged(0, MaxBoardTrails, "The bare trails");
            _trailDepthMm = r.I32(); _trailPackedPermille = r.I32(); _trailBlocksGroomed = r.I32(); _trailBlocksPowder = r.I32();
            int n = r.Count(MaxBoardTrails);
            var ids = new ulong[n];
            _trailOpenNet = new byte[n]; _trailLabelNet = new byte[n];
            _trailWalkFtNet = new int[n]; _trailCoverNet = new int[n]; _trailQualityNet = new int[n];
            for (int k = 0; k < n; k++)
            {
                ids[k] = r.U64();
                if (k > 0 && ids[k] <= ids[k - 1]) throw new InvalidDataException("The trail board's trails are out of order.");
                _trailOpenNet[k] = r.Enum(0, 2, "A trail's open state");
                _trailLabelNet[k] = r.Enum(0, (int)TrailSnowLabel.Bare, "A trail's label");
                _trailWalkFtNet[k] = r.Ranged(0, int.MaxValue, "A trail's walk");
                _trailCoverNet[k] = r.Ranged(0, 1000, "A trail's cover");
                _trailQualityNet[k] = r.Ranged(0, int.MaxValue, "A trail's rating");
            }
            _boardIds = ids;
            _trailMapNet = null;
            _trailBoardRestored = true;
            _trailCardStamp++;   // task 4.07 B2: the card reads the restored board at once
            ulong shape = r.U64();
            int edges = r.Count(MaxBoardEdges);
            if ((shape == 0) != (edges == 0)) throw new InvalidDataException("The guests' snow table doesn't match its network.");
            int bands = Pacing.Bands;
            _edgeQCells = new int[edges]; _edgeWalkMm = new int[edges];
            _edgeQuality = new int[edges * bands]; _edgeQSum = new long[edges * bands];
            for (int e = 0; e < edges; e++)
            {
                _edgeQCells[e] = r.Ranged(0, int.MaxValue, "A segment's cells"); _edgeWalkMm[e] = r.Ranged(0, int.MaxValue, "A segment's walk");
                for (int b = 0; b < bands; b++) _edgeQuality[e * bands + b] = r.Ranged(0, int.MaxValue, "A segment's rating");
            }
            _edgeTableShape = shape;
            _edgeTableNet = null;
            var p = new TrailSnowSaved { LayoutHash = r.U64(), EdgeShape = shape };
            p.FineMetres = r.I32(); p.GridWidth = r.I32(); p.GridHeight = r.I32();
            p.BlocksLastGroom = r.I32(); p.TrailsLastGroom = r.I32();
            _trailBlocksLastGroom = p.BlocksLastGroom; _trailsLastGroom = p.TrailsLastGroom;
            int trails = r.Count(MaxBoardTrails);
            p.Label = new byte[trails]; p.WalkMm = new int[trails]; p.Cover = new int[trails]; p.Quality = new int[trails]; p.MainType = new byte[trails];
            for (int t = 0; t < trails; t++)
            {
                p.Label[t] = r.Enum(0, (int)TrailSnowLabel.Bare, "A trail's label"); p.WalkMm[t] = r.Ranged(0, int.MaxValue, "A trail's walk");
                p.Cover[t] = r.Ranged(0, 1000, "A trail's cover"); p.Quality[t] = r.Ranged(0, int.MaxValue, "A trail's rating");
                p.MainType[t] = r.Enum(0, 15, "A trail's surface");
            }
            if (r.Bool())
            {
                p.Fine = FineSnowCodec.Read(r);
                if (p.Fine.FineMetres != p.FineMetres) throw new InvalidDataException("The trail snow's fine size doesn't match its layout.");
            }
            _trailPending = p;
        }

        /// <summary>
        /// The saved snow taken back over a new layout (from <see cref="RebuildFine"/>): a block keeps its cells when the save
        /// had it on the same 8 m cell at the same fine size and its water adds up to the cell's; the rest seed. Saved blocks
        /// the layout drops fold back into their 8 m cells.
        /// </summary>
        void AdoptSavedFine(FineSnow fine, FinePlanes saved)
        {
            var layout = fine.Layout;
            var field = Snow.Field;
            bool sameSize = saved.BlockCells == fine.BlockCells;
            int cells = field.Width * field.Height;
            for (int b = 0; b < layout.Blocks; b++)
            {
                int cell = layout.BlockCell[b];
                int ob = sameSize ? saved.BlockOf(cell) : -1;
                if (ob >= 0 && saved.AddsUp(ob, field.Pack[cell].Swe)) { fine.CopyBlock(saved, ob, b); _trailBlocksRestored++; }
                else
                {
                    if (ob >= 0) _trailBlocksReseeded++;
                    fine.SeedBlock(field, b);
                }
            }
            for (int ob = 0; ob < saved.Blocks; ob++)
            {
                int cell = saved.BlockCell[ob];
                if (cell >= cells || (sameSize && layout.BlockOf(cell) >= 0) || !saved.AddsUp(ob, field.Pack[cell].Swe)) continue;
                long sum = saved.BlockSum(ob), bc = saved.BlockCells;
                field.Pack[cell].Swe = (int)(sum / bc);
                _trailFoldRemainder += sum - sum / bc * bc;
            }
        }

        /// <summary>After a layout's readiness scratch is made: the same layout as the save's takes its trails' ratings back.</summary>
        void AdoptSavedRatings(TrailSnowSaved p)
        {
            var L = _trailLayout;
            if (p == null || L.Hash() != p.LayoutHash || p.Label.Length != L.Trails) return;
            for (int t = 0; t < L.Trails; t++)
            {
                _trailLabel[t] = (TrailSnowLabel)p.Label[t]; _trailWalkMm[t] = p.WalkMm[t]; _trailCoverPermille[t] = p.Cover[t];
                _trailQuality[t] = p.Quality[t]; _trailMainType[t] = p.MainType[t];
            }
            _trailBlocksLastGroom = p.BlocksLastGroom; _trailsLastGroom = p.TrailsLastGroom;
            if (p.EdgeShape != 0 && _edgeQCells.Length > 0) _edgeTableShape = p.EdgeShape;
        }
    }
}
