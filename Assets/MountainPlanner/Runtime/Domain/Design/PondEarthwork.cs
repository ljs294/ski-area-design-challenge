using System;
using System.Collections.Generic;
using MountainPlanner.Domain.Edits;

namespace MountainPlanner.Domain.Design
{
    /// <summary>A pond's earthwork (PD-01…04, PD-09): its writes, bill, capacity and what (if anything) stops it.</summary>
    public sealed class PondBuild : WaterBuild
    {
        public Pond Pond { get; internal set; }
        /// <summary>The ring's own area, square metres (the shoreline drawn).</summary>
        public double RingSquareMetres { get; internal set; }
        /// <summary>The tallest the berm stands over natural ground, metres.</summary>
        public double BermMaxMetres { get; internal set; }
    }

    /// <summary>
    /// Task 4.14's ponds in the earthwork fold (PD-01…04, PD-08, PD-09): every pond is a piece laid after every trail, lift
    /// and terrain stroke (phase 3), in creation order, so it owns its footprint and its volume is exact on the final ground.
    /// The ground is design = max(fill(d), min(ground, cut(d))) by the signed distance d from the ring (inside negative):
    /// the fill envelope is the berm (3:1 water face up to the crest, 2 ft over full pool and 11.5 ft wide, then 2:1 down
    /// outside), the cut envelope the bowl (3:1 down from the waterline to the dig's floor inside, 1:1 up outside).
    /// </summary>
    public sealed class PondEarthwork : IEarthworkContributor
    {
        /// <summary>The fold's phase for ponds and dams: after trails (0), lifts (1) and strokes (2).</summary>
        public const int WaterPhase = 3;

        public void Collect(DesignDocument doc, EarthworkSite site, List<IEarthworkItem> into)
        {
            if (doc.Ponds.Count == 0) return;
            var order = EarthworkGeometry.OrdinalsOf("pond", doc, (seed, o) => PondId.Make(seed, o).Value);
            foreach (var p in doc.Ponds)
                into.Add(new Item(p, order.TryGetValue(p.Id.Value, out long o) ? o : long.MaxValue, site));
        }

        /// <summary>Every lattice point a pond may read or write: its ring's box grown by PD-02's reach.</summary>
        public static PointRect ReadsOf(IReadOnlyList<PointMm> ring, EarthworkSite site)
        {
            var r = RingDistance.Of(ring, site);
            var lat = site.Lattice;
            double reach = PondRules.ReachMm / 1000.0 + 1;
            return new PointRect((int)Math.Floor(r.MinX - reach - lat.West), (int)Math.Floor(lat.North - r.MaxY - reach),
                                 (int)Math.Ceiling(r.MaxX + reach - lat.West), (int)Math.Ceiling(lat.North - r.MinY + reach));
        }

        /// <summary>PD-03: the suggested full pool for a ring on this ground, whole millimetres.</summary>
        public static int SuggestPoolMm(IReadOnlyList<PointMm> ring, IHeightSource ground, EarthworkSite site)
        {
            var r = RingDistance.Of(ring, site);
            double sum = 0;
            long n = 0;
            for (double y = Math.Floor(r.MinY); y <= r.MaxY; y += 1)
                for (double x = Math.Floor(r.MinX); x <= r.MaxX; x += 1)
                    if (r.Inside(x + 0.5, y + 0.5)) { sum += ground.At(x + 0.5, y + 0.5); n++; }
            double mean = n > 0 ? sum / n : ground.At(r.MinX, r.MinY);
            return PondRules.SuggestedPoolMm(mean);
        }

        /// <summary>
        /// PD-01…04, PD-09: grades <paramref name="pond"/> over <paramref name="ground"/> (the fold's ground, or the committed
        /// ground for a tool's preview) and measures it. <see cref="WaterBuild.Problem"/> says what stops a Build.
        /// </summary>
        public static PondBuild Analyse(Pond pond, EditedHeights ground, EarthworkSite site)
        {
            var lat = site.Lattice;
            var ring = RingDistance.Of(pond.Ring, site);
            var box = ReadsOf(pond.Ring, site);
            double P = pond.FullPoolMm / 1000.0, F = P - pond.ExcavationMm / 1000.0;
            double free = PondRules.FreeboardMm / 1000.0, crest = PondRules.CrestMm / 1000.0, reach = PondRules.ReachMm / 1000.0;
            double wetEdge = free * PondRules.WetRun, top = wetEdge + crest;
            var writes = new EarthworkWrites();
            var wet = new WaterMask(box);
            var b = new PondBuild { Pond = pond, Writes = writes, Wet = wet, FullPoolMetres = P, CrestMetres = P + free, RingSquareMetres = ring.Area() };
            bool edge = false;
            double cut = 0, fill = 0, vol = 0, maxDepth = 0, berm = 0;
            int cleared = 0;
            for (int r = box.MinRow; r <= box.MaxRow; r++)
            {
                double y = lat.Y(r);
                for (int c = box.MinColumn; c <= box.MaxColumn; c++)
                {
                    double x = lat.X(c);
                    double d = ring.Signed(x, y);
                    if (d > reach) continue;
                    double fillZ = d <= wetEdge ? P + d / PondRules.WetRun : d <= top ? P + free : P + free - (d - top) / PondRules.DryRun;
                    double cutZ = d <= 0 ? Math.Max(F, P + d / PondRules.WetRun) : P + d / PondRules.CutRun;
                    double g = ground.AtPoint(c, r);
                    double z = Math.Max(fillZ, Math.Min(g, cutZ));
                    int dzMm = (int)Math.Round((z - g) * 1000);
                    if (dzMm != 0)
                    {
                        writes.Write(c, r, ground.Edits.DeltaMmAt(c, r) + dzMm);
                        double dz = dzMm / 1000.0;
                        if (dz > 0) fill += dz; else cut -= dz;
                        if (d > reach - 1.5) edge = true;
                        if (d > 0 && dz > berm) berm = dz;
                        z = g + dz;
                    }
                    bool inside = d < 0;
                    if (inside && z < P)
                    {
                        wet.Set(c, r);
                        vol += P - z;
                        if (P - z > maxDepth) maxDepth = P - z;
                    }
                    if (inside || Math.Abs(dzMm) >= TerrainBrush.ClearMm) { writes.Clear(c, r); cleared++; }
                }
            }
            b.CutCubicMetres = cut;
            b.FillCubicMetres = fill;
            b.VolumeCubicMetres = vol;
            b.AreaSquareMetres = wet.Count;
            b.MaxDepthMetres = maxDepth;
            b.BermMaxMetres = berm;
            b.ClearedCells = cleared;
            b.Problem = Problem(pond, site, edge, berm, vol, maxDepth);
            if (b.Problem == null)
            {
                string worth = PondRules.WorthProblem("pond", vol, cut + fill, out bool refused);
                if (refused) b.Problem = worth; else b.Warning = worth;
            }
            return b;
        }

        static string Problem(Pond pond, EarthworkSite site, bool edge, double berm, double vol, double maxDepth)
        {
            if (!site.Core.IsEmpty)
                foreach (var q in pond.Ring)
                {
                    site.PointOf(q, out double x, out double y);
                    int c = (int)Math.Round(x - site.Lattice.West), r = (int)Math.Round(site.Lattice.North - y);
                    if (!site.Core.Contains(c, r)) return "Keep the whole pond inside the core.";
                }
            if (edge) return "The berm can't meet natural ground within 820 ft: lower the pool or draw a smaller pond.";
            if (berm > PondRules.BermMaxMm / 1000.0)
                return $"This pond needs a {WaterBuild.Feet(berm)} berm. Keep it under 65 ft: lower the pool, or dam a stream instead.";
            if (vol <= 1 || maxDepth < 0.1) return "Raise the pool above the ground inside the shoreline.";
            return null;
        }

        sealed class Item : IEarthworkItem
        {
            readonly Pond _pond;
            readonly EarthworkSite _site;

            public Item(Pond pond, long order, EarthworkSite site)
            {
                _pond = pond;
                _site = site;
                Order = order;
                Reads = ReadsOf(pond.Ring, site);
                Tiles = new List<EditTileKey>(Reads.TileKeys());
            }

            public ulong Key => _pond.Id.Value;
            public int Phase => WaterPhase;
            public long Order { get; }
            public PointRect Reads { get; }
            public IReadOnlyCollection<EditTileKey> Tiles { get; }

            public bool SameAs(IEarthworkItem other) => other is Item o && _pond.SameWork(o._pond) && SameSite(_site, o._site);

            public EarthworkWrites Build(EditedHeights ground, EarthworkContext context, out object detail)
            {
                var b = Analyse(_pond, ground, _site);
                detail = b;
                return b.Writes;
            }
        }

        internal static bool SameSite(EarthworkSite a, EarthworkSite b) =>
            a.Lattice.West == b.Lattice.West && a.Lattice.North == b.Lattice.North && a.OriginX == b.OriginX && a.OriginY == b.OriginY && a.Core.Equals(b.Core);
    }
}
