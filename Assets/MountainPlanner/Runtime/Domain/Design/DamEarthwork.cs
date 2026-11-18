using System;
using System.Collections.Generic;
using MountainPlanner.Domain.Edits;

namespace MountainPlanner.Domain.Design
{
    /// <summary>A dam's earthwork (PD-05…07, PD-09): its embankment, its reservoir on the graded ground and what stops it.</summary>
    public sealed class DamBuild : WaterBuild
    {
        public Dam Dam { get; internal set; }
        /// <summary>The line from bank to bank, metres.</summary>
        public double LengthMetres { get; internal set; }
        /// <summary>How much of the line the embankment stands on (over 2 in tall), metres.</summary>
        public double EmbankmentMetres { get; internal set; }
        public double HeightMaxMetres { get; internal set; }
        public double HeightMeanMetres { get; internal set; }
        /// <summary>PD-13: full pool over the lowest ground on the dam's line (the stream bed), metres.</summary>
        public double PoolOverBedMetres { get; internal set; }
        /// <summary>
        /// F3: where the problem is, design metres from the site's centre (NaN for none, or one with no single place): the
        /// first fill that never met the ground (PD-10), the water that came round an end (PD-06), the water at the reach's
        /// edge, or the bed under a dam too low (PD-13).
        /// </summary>
        public double ProblemX { get; internal set; } = double.NaN;
        public double ProblemY { get; internal set; } = double.NaN;
        /// <summary>F3: the spillway notch's middle on the dam line, design metres from the site's centre.</summary>
        public double SpillX { get; internal set; }
        public double SpillY { get; internal set; }
    }

    /// <summary>
    /// Task 4.14's dams in the earthwork fold (PD-05…07, PD-08, PD-09), phase 3 like ponds. The embankment is fill only:
    /// a deck 11.5 ft wide, 2 ft over full pool, 3:1 on the water side and 2:1 downstream, with PD-07's spillway notch
    /// 20 ft wide down to full pool at one bank, so a full reservoir spills there and never overtops. The reservoir is a
    /// 4-neighbour flood below full pool on the graded ground from the water side (MapLibre's barrier across the whole
    /// grid is fixed: the embankment is the barrier). Water reaching the flood's edge, or coming round an end of the dam
    /// to its downstream side, stops a Build.
    /// </summary>
    public sealed class DamEarthwork : IEarthworkContributor
    {
        public void Collect(DesignDocument doc, EarthworkSite site, List<IEarthworkItem> into)
        {
            if (doc.Dams.Count == 0) return;
            var order = EarthworkGeometry.OrdinalsOf("dam", doc, (seed, o) => DamId.Make(seed, o).Value);
            foreach (var d in doc.Dams)
                into.Add(new Item(d, order.TryGetValue(d.Id.Value, out long o) ? o : long.MaxValue, site));
        }

        /// <summary>Every lattice point a dam may read: its line's box grown by PD-06's flood reach.</summary>
        public static PointRect ReadsOf(Dam dam, EarthworkSite site) => Box(dam, site, PondRules.DamReachMm / 1000.0);

        static PointRect Box(Dam dam, EarthworkSite site, double grow)
        {
            var lat = site.Lattice;
            site.PointOf(dam.BankA, out double ax, out double ay);
            site.PointOf(dam.BankB, out double bx, out double by);
            return new PointRect((int)Math.Floor(Math.Min(ax, bx) - grow - lat.West), (int)Math.Floor(lat.North - Math.Max(ay, by) - grow),
                                 (int)Math.Ceiling(Math.Max(ax, bx) + grow - lat.West), (int)Math.Ceiling(lat.North - Math.Min(ay, by) + grow));
        }

        /// <summary>
        /// PD-06: which side of the line from <paramref name="a"/> to <paramref name="b"/> the water sits on, from a point on
        /// the stream upstream of the crossing (the tool's median test, 65/165/330 ft along the stream).
        /// </summary>
        public static bool UpstreamLeftOf(PointMm a, PointMm b, PointMm upstream)
        {
            long cross = ((long)b.X - a.X) * ((long)upstream.Y - a.Y) - ((long)b.Y - a.Y) * ((long)upstream.X - a.X);
            return cross > 0;
        }

        /// <summary>PD-05…07, PD-09: builds <paramref name="dam"/> over <paramref name="ground"/> and floods its reservoir.</summary>
        public static DamBuild Analyse(Dam dam, EditedHeights ground, EarthworkSite site)
        {
            var lat = site.Lattice;
            site.PointOf(dam.BankA, out double ax, out double ay);
            site.PointOf(dam.BankB, out double bx, out double by);
            double P = dam.FullPoolMm / 1000.0, free = PondRules.FreeboardMm / 1000.0, C = P + free;
            double half = PondRules.CrestMm / 2000.0, spill = PondRules.SpillwayMm / 1000.0;
            double dx = bx - ax, dy = by - ay, L = Math.Sqrt(dx * dx + dy * dy), ux = dx / L, uy = dy / L;
            var b = new DamBuild { Dam = dam, FullPoolMetres = P, CrestMetres = C, LengthMetres = L };

            // The line's profile: the embankment's heights, and its lowest ground (where the stream crosses).
            int n = Math.Max(2, (int)Math.Ceiling(L));
            double hMax = 0, hSum = 0, lowG = double.MaxValue, lowS = 0;
            int stand = 0;
            for (int i = 0; i <= n; i++)
            {
                double s = L * i / n, g = ground.At(ax + ux * s, ay + uy * s), h = C - g;
                if (g < lowG) { lowG = g; lowS = s; }
                if (h <= 0.05) continue;
                stand++;
                hSum += h;
                if (h > hMax) hMax = h;
            }
            b.HeightMaxMetres = hMax;
            b.PoolOverBedMetres = P - lowG;
            b.HeightMeanMetres = stand > 0 ? hSum / stand : 0;
            b.EmbankmentMetres = stand * L / n;

            // The embankment: fill only, out to where its faces meet the ground.
            var writes = new EarthworkWrites();
            b.Writes = writes;
            var reach = Box(dam, site, half + PondRules.WetRun * (PondRules.DamMaxMm / 1000.0 + free) + 2);   // the tallest dam's water face
            double fill = 0;
            int cleared = 0;
            bool runaway = false;
            double runX = double.NaN, runY = double.NaN, leakX = double.NaN, leakY = double.NaN, edgeX = double.NaN, edgeY = double.NaN;
            double spillFrom = dam.Spill == SpillBank.First ? 0 : L - spill, spillTo = dam.Spill == SpillBank.First ? spill : L;
            for (int r = reach.MinRow; r <= reach.MaxRow; r++)
            {
                double y = lat.Y(r);
                for (int c = reach.MinColumn; c <= reach.MaxColumn; c++)
                {
                    double x = lat.X(c);
                    double along = (x - ax) * ux + (y - ay) * uy, side = -(x - ax) * uy + (y - ay) * ux;   // side > 0: left of A→B
                    double t = Math.Max(0, Math.Min(L, along));
                    double ex = ax + ux * t - x, ey = ay + uy * t - y, dist = Math.Sqrt(ex * ex + ey * ey);
                    bool water = side > 0 == dam.UpstreamLeft;
                    double deck = along >= spillFrom && along <= spillTo ? P : C;   // PD-07: the notch, down to full pool
                    double e = deck - Math.Max(0, dist - half) / (water ? PondRules.WetRun : PondRules.DryRun);
                    double g = ground.AtPoint(c, r);
                    // Rounded up: the fill never stands under its envelope, so the notch's deck is never a hair under full
                    // pool (rounded to the nearest, about half its points were, and the flood ran over the spillway).
                    int dzMm = (int)Math.Ceiling((e - g) * 1000 - 1e-6);
                    if (dzMm <= 0) continue;
                    // Fill at the edge of the reach: a face that never met the ground (a cliff below the dam).
                    if ((c == reach.MinColumn || c == reach.MaxColumn || r == reach.MinRow || r == reach.MaxRow) && !runaway) { runaway = true; runX = x; runY = y; }
                    writes.Write(c, r, ground.Edits.DeltaMmAt(c, r) + dzMm);
                    fill += dzMm / 1000.0;
                    if (dzMm >= TerrainBrush.ClearMm) { writes.Clear(c, r); cleared++; }
                }
            }
            b.FillCubicMetres = fill;

            // The reservoir: a 4-neighbour flood below full pool on the graded ground, from the water side at the stream.
            var box = ReadsOf(dam, site);
            var wet = new WaterMask(box);
            b.Wet = wet;
            // The seeds: every point against the dam's water face (the strip from the line out past the tallest face's toe,
            // between the banks) that stands under full pool. One seed straight out from the lowest point missed the
            // water wherever the stream bends there, and the dam read as holding nothing.
            double off = half + PondRules.WetRun * Math.Max(0, C - lowG) + 2;
            double Graded(int c, int r) => writes.Writes(c, r, out int d) ? ground.Source.At(lat.X(c), lat.Y(r)) + d / 1000.0 : ground.AtPoint(c, r);
            const double Dry = 1e-6;   // at full pool is dry: the notch's deck holds the water level, not under it
            bool edge = false, leak = false;
            double vol = 0, maxDepth = 0, leakAt = PondRules.LeakMm / 1000.0;
            var stack = new Stack<(int, int)>();
            var curve = new Water.StageCurve.Builder();
            var seen = new WaterMask(box);
            var strip = Box(dam, site, off);
            for (int r = Math.Max(strip.MinRow, box.MinRow); r <= Math.Min(strip.MaxRow, box.MaxRow); r++)
            for (int c = Math.Max(strip.MinColumn, box.MinColumn); c <= Math.Min(strip.MaxColumn, box.MaxColumn); c++)
            {
                double x = lat.X(c), y = lat.Y(r);
                double along = (x - ax) * ux + (y - ay) * uy, side = -(x - ax) * uy + (y - ay) * ux;
                if (along < 0 || along > L || side > 0 != dam.UpstreamLeft || Math.Abs(side) > off) continue;
                if (Graded(c, r) >= P - Dry) continue;
                seen.Set(c, r);
                stack.Push((c, r));
            }
            {
                while (stack.Count > 0)
                {
                    var (c, r) = stack.Pop();
                    double z = Graded(c, r);
                    if (z >= P - Dry) continue;
                    wet.Set(c, r);
                    vol += P - z;
                    curve.Add(P - z);
                    if (P - z > maxDepth) maxDepth = P - z;
                    double x = lat.X(c), y = lat.Y(r);
                    if ((c == box.MinColumn || c == box.MaxColumn || r == box.MinRow || r == box.MaxRow) && !edge) { edge = true; edgeX = x; edgeY = y; }
                    double side = -(x - ax) * uy + (y - ay) * ux;
                    if ((side > 0) != dam.UpstreamLeft && RingDistance.SegmentDistanceSquared(x, y, ax, ay, bx, by) < leakAt * leakAt && !leak) { leak = true; leakX = x; leakY = y; }
                    if (!writes.Clears(c, r)) { writes.Clear(c, r); cleared++; }
                    Push(c + 1, r); Push(c - 1, r); Push(c, r + 1); Push(c, r - 1);
                    void Push(int pc, int pr)
                    {
                        if (!box.Contains(pc, pr) || seen.Has(pc, pr)) return;
                        seen.Set(pc, pr);
                        stack.Push((pc, pr));
                    }
                }
            }
            b.VolumeCubicMetres = vol;
            b.AreaSquareMetres = wet.Count;
            b.MaxDepthMetres = maxDepth;
            b.Curve = curve.Build();
            b.ClearedCells = cleared;
            b.Problem = Problem(dam, site, b, edge, leak, runaway);
            double sMid = (spillFrom + spillTo) / 2;
            b.SpillX = ax + ux * sMid - site.OriginX;
            b.SpillY = ay + uy * sMid - site.OriginY;
            // Where to point: the same order Problem checks them in.
            double px = double.NaN, py = double.NaN;
            if (b.Problem != null)
            {
                if (runaway && b.Problem.Contains("cliff edge")) { px = runX; py = runY; }
                else if (leak && b.Problem.Contains("round the end")) { px = leakX; py = leakY; }
                else if (edge && b.Problem.Contains("past 1,640 ft")) { px = edgeX; py = edgeY; }
                else if (b.Problem.Contains("over the stream")) { px = ax + ux * lowS; py = ay + uy * lowS; }
            }
            b.ProblemX = double.IsNaN(px) ? double.NaN : px - site.OriginX;
            b.ProblemY = double.IsNaN(py) ? double.NaN : py - site.OriginY;
            return b;
        }

        static string Problem(Dam dam, EarthworkSite site, DamBuild b, bool edge, bool leak, bool runaway)
        {
            if (!site.Core.IsEmpty)
                foreach (var q in new[] { dam.BankA, dam.BankB })
                {
                    site.PointOf(q, out double x, out double y);
                    if (!site.Core.Contains((int)Math.Round(x - site.Lattice.West), (int)Math.Round(site.Lattice.North - y))) return "Keep the whole dam inside the core.";
                }
            if (b.HeightMaxMetres > PondRules.DamMaxMm / 1000.0) return $"This dam would stand {WaterBuild.Feet(b.HeightMaxMetres)} tall: keep it under 130 ft.";
            if (runaway) return "The dam's faces never meet the ground: it stands on a cliff edge. Build where the stream runs gentler.";
            if (leak) return "Water runs round the end of the dam: the bank there sits below full pool.";
            if (edge) return "Full pool here backs water past 1,640 ft: pick a lower point on the bank, or move the dam.";
            if (b.PoolOverBedMetres * 1000 < PondRules.DamMinPoolOverBedMm)
                return $"Full pool stands only {WaterBuild.Feet(Math.Max(0, b.PoolOverBedMetres))} over the stream: a dam needs at least 5 ft. Click higher up the bank.";
            if (b.AreaSquareMetres < PondRules.MinAreaSquareMetres || b.VolumeCubicMetres <= 1 || b.MaxDepthMetres < 0.1)
                return "This line doesn't hold a usable pond: the embankment fills the basin.";
            string worth = PondRules.WorthProblem("dam", b.VolumeCubicMetres, b.EarthCubicMetres, out bool refused);
            if (refused) return worth;
            b.Warning = worth;
            return null;
        }

        sealed class Item : IEarthworkItem
        {
            readonly Dam _dam;
            readonly EarthworkSite _site;

            public Item(Dam dam, long order, EarthworkSite site)
            {
                _dam = dam;
                _site = site;
                Order = order;
                Reads = ReadsOf(dam, site);
                Tiles = new List<EditTileKey>(Reads.TileKeys());
            }

            public ulong Key => _dam.Id.Value;
            public int Phase => PondEarthwork.WaterPhase;
            public long Order { get; }
            public PointRect Reads { get; }
            public IReadOnlyCollection<EditTileKey> Tiles { get; }

            public bool SameAs(IEarthworkItem other) => other is Item o && _dam.SameWork(o._dam) && PondEarthwork.SameSite(_site, o._site);

            public EarthworkWrites Build(EditedHeights ground, EarthworkContext context, out object detail)
            {
                var b = Analyse(_dam, ground, _site);
                detail = b;
                return b.Writes;
            }
        }
    }
}
