using System;
using System.Collections.Generic;
using MountainPlanner.Domain.Edits;

namespace MountainPlanner.Domain.Design
{
    /// <summary>What a stroke's replay hands back (TB-09): its writes, volumes, trees cleared and what held it.</summary>
    public sealed class StrokeBuild
    {
        public TerrainStroke Stroke { get; internal set; }
        public EarthworkWrites Writes { get; internal set; }
        public double CutCubicMetres { get; internal set; }
        public double FillCubicMetres { get; internal set; }
        public int ClearedCells { get; internal set; }
        public IReadOnlyList<Hold> Held { get; internal set; }
    }

    /// <summary>
    /// Task 15's part of the earthwork fold (TB-14, owner BR6): every confirmed terrain stroke is a piece laid after every
    /// trail and lift (phase 2), in creation order, its dabs replayed by <see cref="BrushRun"/> over the ground the fold
    /// hands it, held by <see cref="FixedGround"/> from the lifts' own earthwork (the fold's detail, so a stroke never moves
    /// a lift's heads). A stroke is graded again when it, a lift near it or the ground under it changes.
    /// </summary>
    public sealed class BrushEarthwork : IEarthworkContributor
    {
        /// <summary>The fold's phase for strokes: after trails (0) and lifts (1).</summary>
        public const int StrokePhase = 2;

        public void Collect(DesignDocument doc, EarthworkSite site, List<IEarthworkItem> into)
        {
            if (doc.Strokes.Count == 0) return;
            var order = Ordinals(doc);
            foreach (var k in doc.Strokes)
                into.Add(new Item(k, doc, order.TryGetValue(k.Id.Value, out long o) ? o : long.MaxValue, site));
        }

        static Dictionary<ulong, long> Ordinals(DesignDocument doc)
        {
            var map = new Dictionary<ulong, long>();
            for (long o = 0; o < doc.NextOrdinal; o++) map[TerrainStrokeId.Make(doc.Seed, o).Value] = o;
            return map;
        }

        /// <summary>
        /// The lattice points a stroke can read or write: every dab's disc, grown by Smooth's box and a cell for clearing
        /// (TB-06, TB-10).
        /// </summary>
        public static PointRect ReadsOf(TerrainStroke stroke, EarthworkSite site, SortedSet<EditTileKey> tiles = null)
        {
            var lat = site.Lattice;
            double reach = stroke.Settings.RadiusMm / 1000.0 + (stroke.Settings.Kind == BrushKind.Smooth ? TerrainBrush.SmoothHalfWidth(stroke.Settings.RadiusMm) : 0) + 2;
            var rect = PointRect.Empty;
            PointMm last = default;
            bool any = false;
            foreach (var d in stroke.Dabs)
            {
                if (any && d.At.Equals(last)) continue;
                last = d.At;
                any = true;
                site.PointOf(d.At, out double x, out double y);
                var box = new PointRect((int)Math.Floor(x - reach - lat.West), (int)Math.Floor(lat.North - y - reach),
                                        (int)Math.Ceiling(x + reach - lat.West), (int)Math.Ceiling(lat.North - y + reach));
                rect = rect.Union(box);
                if (tiles != null) foreach (var key in box.TileKeys()) tiles.Add(key);
            }
            return rect;
        }

        sealed class Item : IEarthworkItem
        {
            readonly TerrainStroke _stroke;
            readonly DesignDocument _doc;
            readonly EarthworkSite _site;
            readonly Lift[] _lifts;   // the built lifts whose reach meets the stroke (their changes regrade it)

            public Item(TerrainStroke stroke, DesignDocument doc, long order, EarthworkSite site)
            {
                _stroke = stroke;
                _doc = doc;
                _site = site;
                Order = order;
                var tiles = new SortedSet<EditTileKey>();
                Reads = ReadsOf(stroke, site, tiles);
                Tiles = tiles;
                var near = new List<Lift>();
                var fixedHere = FixedGround.For(doc, site, null, Reads);
                foreach (var l in doc.Lifts)
                    if (l.Stage == WorkStage.Built && l.Levels != null && fixedHere.Covers(l.Id)) near.Add(l);
                _lifts = near.ToArray();
            }

            public ulong Key => _stroke.Id.Value;
            public int Phase => StrokePhase;
            public long Order { get; }
            public PointRect Reads { get; }
            public IReadOnlyCollection<EditTileKey> Tiles { get; }

            public bool SameAs(IEarthworkItem other)
            {
                if (!(other is Item o)) return false;
                if (!_stroke.SamePainting(o._stroke) || !SameSite(_site, o._site) || _lifts.Length != o._lifts.Length) return false;
                for (int i = 0; i < _lifts.Length; i++)
                    if (!ReferenceEquals(_lifts[i], o._lifts[i]) && !LiftEarthwork.SameWork(_lifts[i], o._lifts[i])) return false;
                return true;
            }

            static bool SameSite(EarthworkSite a, EarthworkSite b) =>
                a.Lattice.West == b.Lattice.West && a.Lattice.North == b.Lattice.North && a.OriginX == b.OriginX && a.OriginY == b.OriginY && a.Core.Equals(b.Core);

            public EarthworkWrites Build(EditedHeights ground, EarthworkContext context, out object detail)
            {
                var fixedGround = FixedGround.For(_doc, _site, id => context.DetailOf(id.Value) as LiftBuild.Works, Reads);
                var run = new BrushRun(_stroke.Settings, ground, _site, fixedGround);
                run.Dabs(_stroke.Dabs);
                var writes = run.Writes();
                run.Volumes(out double cut, out double fill);
                detail = new StrokeBuild { Stroke = _stroke, Writes = writes, CutCubicMetres = cut, FillCubicMetres = fill, ClearedCells = run.ClearedCount, Held = run.Held() };
                return writes;
            }
        }
    }
}
