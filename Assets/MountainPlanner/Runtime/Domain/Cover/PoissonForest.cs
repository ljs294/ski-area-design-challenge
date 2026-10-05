namespace MountainPlanner.Domain.Cover
{
    /// <summary>One 10 m forest cell, prepared once per site (all of its floating-point maths happens there).</summary>
    public struct ForestCell
    {
        public const byte None = 0, Core = 1, Ring = 2;

        /// <summary>Trees expected in the whole cell, in 1/256 trees.</summary>
        public ushort Expected256;
        /// <summary>How far apart this cell's trees stand, in 1/256 m.</summary>
        public ushort Spacing256;
        /// <summary><see cref="None"/>, <see cref="Core"/> (trees only on canopy pixels) or <see cref="Ring"/>.</summary>
        public byte Kind;
        /// <summary>Dominant tree height in 0.25 m steps.</summary>
        public byte HeightCode;
        /// <summary>Crown width relative to the model, in 1/32 steps.</summary>
        public byte Width32;
        /// <summary>Chance (0–255) that a tree here grows as krummholz (just below the treeline).</summary>
        public byte Krummholz;
        /// <summary>1 where the ground is too steep for a krummholz mat (<see cref="Treeline.MatMaxSlopeDegrees"/>).</summary>
        public byte Steep;
        /// <summary>The conifers' share of the cell's species (0–255, BIGMAP weights).</summary>
        public byte Conifer;
        /// <summary>The shade-tolerant conifers' share (firs, spruces, hemlocks, cedars; 0–255): they grow multi-storied stands.</summary>
        public byte Tolerant;
        /// <summary>The cell's tree share (0–255): the canopy map's in the core, the stand field's in the ring.</summary>
        public byte Canopy;
        /// <summary>How fully the cell grows as a dense, shade-tolerant conifer stand (0–255): clumped, with an understory (NE8).</summary>
        public byte Stand;
    }

    /// <summary>One placed tree in the forest frame: 1/256 m east and north of the frame's south-west corner.</summary>
    public struct ForestPoint
    {
        public int X, Y;
        public ushort Spacing256;
        public byte HeightCode;
        public byte Prototype;
        public byte Rotation;
        public byte Width32;
        public ushort Pad;
    }

    /// <summary>
    /// Everything <see cref="PoissonForest"/> reads and writes, as raw memory so that the same code runs as
    /// plain C# (the downloader, CI) and Burst-compiled (the game) and gives identical bytes.
    /// </summary>
    public unsafe struct ForestInputs
    {
        public ulong Seed;
        /// <summary>64 m tiles over the frame; tile index = row (from the south) × TilesX + column.</summary>
        public int TilesX, TilesY;

        public ForestCell* Cells;
        public int CellsX, CellsY;
        /// <summary>The frame position of cell (0, 0)'s south-west corner, in 1/256 m (zero or negative).</summary>
        public int CellOriginX, CellOriginY;
        /// <summary>The largest spacing of any cell: how far into its neighbours a tile must look.</summary>
        public int MaxSpacing256;

        /// <summary>1 bit per metre where the canopy map has a tree, over the core, rows from the north like the canopy grid.</summary>
        public ulong* CanopyMask;
        /// <summary>The mask's west and north edges in whole metres of the frame.</summary>
        public int MaskWest, MaskNorth, MaskWidth, MaskHeight;

        /// <summary>BIGMAP's top four species per 30 m cell (rows from the north), 1-based table indices and weights.</summary>
        public byte* SpeciesIds, SpeciesWeights;
        public int SpeciesColumns, SpeciesRows;
        public int SpeciesWest, SpeciesNorth, SpeciesCell;
        /// <summary>Species table index (0–255) → model.</summary>
        public int* ModelOfIndex;
        /// <summary>Per model, the running total of the site's species shares (the last is the total).</summary>
        public int* SiteCumulative;
        public int Models, Variants;
        /// <summary>The krummholz model, or -1 while the library has none.</summary>
        public int KrummholzModel;
        /// <summary>The rotation (1/256 turns) that points a krummholz flag downwind.</summary>
        public int DownwindRotation;
        /// <summary>Per krummholz variant: its lowest and highest height (0.25 m steps), near its own size (<see cref="Treeline.HeightCodes"/>).</summary>
        public int* KrummholzHeights;

        /// <summary>
        /// Dense conifer stands (NE8): tree height as a share of the dominant (1/256) at <see cref="PoissonForest.StandQuantiles"/>
        /// + 1 evenly spaced quantiles, shortest first (<see cref="ForestPlacement.StandHeightTable"/>).
        /// </summary>
        public int* StandHeights;
        /// <summary>The clump field (NE8): clump centres one per lattice square (1/256 m), their reach (1/256 m), and the
        /// chance (0–256) a dart far from every clump is still kept.</summary>
        public int ClumpLattice, ClumpRadius, ClumpFloor;

        /// <summary>Per tile: where its trees start in <see cref="Points"/> (one extra entry: the end), and how many it has.</summary>
        public int* TileOffset;
        public int* TileCount;
        public ForestPoint* Points;
    }

    /// <summary>A tile's working memory (one per thread).</summary>
    public unsafe struct ForestScratch
    {
        public const int BinShift = 9;   // 2 m bins
        public int* Heads;
        public int HeadCapacity;
        public int* Next;
        public ForestPoint* Local;
        public int LocalCapacity;
    }

    /// <summary>
    /// Deterministic Poisson-disc forest (0.3 §4.5, T7): dart throwing per 64 m tile, seeded by
    /// hash(resort, tile). Every tree keeps its crown's distance from every other, across 10 m cells and
    /// across tiles. Tiles grow in four interleaved passes (their column and row parity); each tile first
    /// takes in the trees of neighbours already grown, so there are no seams, and tiles of one pass never
    /// touch, so they grow in any order or in parallel with the same result.
    ///
    /// How many trees a 10 m cell holds, how tall and how far apart comes from the forest rules
    /// (<see cref="ForestPlacement"/>, D4 calibration), prepared per cell. Here everything is integer maths,
    /// so every build, compiler and CPU grows the same forest.
    /// </summary>
    public static unsafe class PoissonForest
    {
        public const int TileMetres = 64;
        public const int Fixed = 256;
        public const int TileFixed = TileMetres * Fixed;
        public const int CellFixed = ForestPlacement.CellMetres * Fixed;
        /// <summary>Darts thrown per tree a cell should hold, plus a few.</summary>
        public const int DartsPerTree = 8, ExtraDarts = 2;
        /// <summary>Extra darts per tree in a full dense conifer stand, where the clump field turns many away.</summary>
        public const int StandDartsPerTree = 16;
        /// <summary>Intervals of the stand height table (<see cref="ForestInputs.StandHeights"/>).</summary>
        public const int StandQuantiles = 16;

        /// <summary>The pass (0–3) a tile grows in.</summary>
        public static int Phase(int tileX, int tileY) => (tileX & 1) | ((tileY & 1) << 1);

        /// <summary>The most trees the tile can hold: the sum of its cells' quotas.</summary>
        public static int TileQuota(ref ForestInputs f, int tile)
        {
            int tx = tile % f.TilesX, ty = tile / f.TilesX;
            int x0 = tx * TileFixed, y0 = ty * TileFixed;
            ulong tileSeed = Hash(f.Seed, tx, ty, 0x7113);
            CellRange(ref f, x0, y0, out int i0, out int i1, out int j0, out int j1);
            int total = 0;
            for (int j = j0; j <= j1; j++)
                for (int i = i0; i <= i1; i++)
                    total += Quota(ref f, tileSeed, x0, y0, i, j, out _, out _, out _, out _);
            return total;
        }

        /// <summary>
        /// Grows one tile. Neighbours of earlier passes must already be grown (their counts set); the tile's
        /// trees go to its slot in <see cref="ForestInputs.Points"/>. Returns how many it placed.
        /// </summary>
        public static int PlaceTile(ref ForestInputs f, int tile, ref ForestScratch s)
        {
            int tx = tile % f.TilesX, ty = tile / f.TilesX;
            int x0 = tx * TileFixed, y0 = ty * TileFixed;
            int margin = f.MaxSpacing256;
            int ox = x0 - margin, oy = y0 - margin;
            int bins = ((TileFixed + 2 * margin) >> ForestScratch.BinShift) + 1;
            if (bins * bins > s.HeadCapacity) bins = 0;   // cannot happen with sane spacings; grow nothing rather than overrun
            for (int b = 0; b < bins * bins; b++) s.Heads[b] = -1;
            int local = 0, maxSpacing = 0;

            // Trees of neighbours grown in earlier passes, within reach of this tile.
            int phase = Phase(tx, ty);
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int nx = tx + dx, ny = ty + dy;
                    if ((dx == 0 && dy == 0) || nx < 0 || ny < 0 || nx >= f.TilesX || ny >= f.TilesY || Phase(nx, ny) >= phase) continue;
                    int n = ny * f.TilesX + nx, start = f.TileOffset[n], count = f.TileCount[n];
                    for (int k = 0; k < count; k++)
                    {
                        var p = f.Points[start + k];
                        int lx = p.X - ox, ly = p.Y - oy;
                        if (lx < 0 || ly < 0 || lx >= bins << ForestScratch.BinShift || ly >= bins << ForestScratch.BinShift) continue;
                        if (!Insert(ref s, ref local, bins, lx, ly, p)) continue;
                        if (p.Spacing256 > maxSpacing) maxSpacing = p.Spacing256;
                    }
                }

            ulong tileSeed = Hash(f.Seed, tx, ty, 0x7113);
            int slot = f.TileOffset[tile], capacity = f.TileOffset[tile + 1] - slot, placed = 0;
            CellRange(ref f, x0, y0, out int i0, out int i1, out int j0, out int j1);
            for (int j = j0; j <= j1; j++)
                for (int i = i0; i <= i1; i++)
                {
                    int quota = Quota(ref f, tileSeed, x0, y0, i, j, out int rx0, out int ry0, out int rw, out int rh);
                    if (quota == 0) continue;
                    int cellIndex = j * f.CellsX + i;
                    var cell = f.Cells[cellIndex];
                    if (cell.Spacing256 > maxSpacing) maxSpacing = cell.Spacing256;
                    int accepted = 0, darts = quota * DartsPerTree + ExtraDarts + ((quota * StandDartsPerTree * cell.Stand) >> 8);
                    for (int k = 0; k < darts && accepted < quota && placed < capacity && local < s.LocalCapacity; k++)
                    {
                        ulong h = Hash(tileSeed, cellIndex, k, 0x0D47);
                        int px = rx0 + (int)(((h & 0xFFFFFFFFUL) * (ulong)rw) >> 32);
                        int py = ry0 + (int)(((h >> 32) * (ulong)rh) >> 32);
                        if (cell.Kind == ForestCell.Core && !IsCanopyTree(ref f, px, py)) continue;   // ski runs and glades stay open
                        if (cell.Stand != 0)
                        {
                            // Dense conifer stands grow in clumps with small gaps between (NE8): darts away from a clump are thinned.
                            int keep = 256 - (((256 - ClumpKeep(ref f, px, py)) * cell.Stand) >> 8);
                            if ((int)(Hash(tileSeed, cellIndex, k, 0xC1F7) & 0xFF) >= keep) continue;
                        }
                        var tree = Grow(ref f, cell, px, py, Hash(tileSeed, cellIndex, k, 0x6A0E), Hash(tileSeed, cellIndex, k, 0x5E1F));
                        int lx = px - ox, ly = py - oy;
                        if (Crowded(ref s, bins, lx, ly, tree.Spacing256, maxSpacing)) continue;

                        f.Points[slot + placed] = tree;
                        placed++;
                        accepted++;
                        Insert(ref s, ref local, bins, lx, ly, tree);
                    }
                }
            f.TileCount[tile] = placed;
            return placed;
        }

        /// <summary>A tree's looks at an accepted position: species, variant, height, rotation and width.</summary>
        static ForestPoint Grow(ref ForestInputs f, ForestCell cell, int px, int py, ulong a, ulong b)
        {
            int variant = (int)((a >> 40) % (ulong)f.Variants);
            int model = PickModel(ref f, px, py, (int)(a & 0xFFFF));
            int u = (int)((a >> 16) & 0xFF);
            int share = 166 + ((89 * u) >> 8);   // 65–100% of the dominant height
            if (cell.Stand != 0)
            {
                // A dense conifer stand (NE8): most trees near the canopy over an understory of intermediate and
                // suppressed trees, from the lidar-calibrated stand table, blended in by how fully the cell is a stand.
                int q = u * StandQuantiles, i = q >> 8, frac = q & 0xFF;
                int stand = f.StandHeights[i] + (((f.StandHeights[i + 1] - f.StandHeights[i]) * frac) >> 8);
                share += ((stand - share) * cell.Stand) >> 8;
            }
            int height = cell.HeightCode * share >> 8;
            int shortest = cell.HeightCode * 166 >> 8;
            if (height < shortest && height < ForestRule.TreeCode) height = shortest < ForestRule.TreeCode ? shortest : ForestRule.TreeCode;   // an understory tree is still a tree (3 m)
            int spacing = cell.Spacing256;
            if (cell.Stand != 0)
            {
                // A smaller tree has a smaller crown, so it may stand closer: the spacing floor follows each tree's own crown.
                spacing = (int)((long)spacing * CrownRadius256(height) / CrownRadius256(cell.HeightCode));
            }
            int width = cell.Width32 * (218 + ((77 * (int)((a >> 24) & 0xFF)) >> 8)) >> 8;       // 85–115%
            int rotation = (int)((a >> 32) & 0xFF);
            if (f.KrummholzModel >= 0 && (int)(b & 0xFF) < cell.Krummholz)
            {
                // Just below the treeline: a stunted, wind-shaped form near its own size, its flag downwind. Where the
                // band is fully krummholz (the most exposed ground) mostly mats and cushions; lower down mostly flag trees.
                int r = (int)((b >> 24) & 0xFF);
                variant = cell.Krummholz == 255 ? (r < 115 ? Treeline.Mat : r < 205 ? Treeline.Cushion : Treeline.FlagTree)
                                                 : (r < 128 ? Treeline.FlagTree : r < 205 ? Treeline.Cushion : Treeline.Mat);
                if (variant == Treeline.Mat && cell.Steep != 0) variant = Treeline.Cushion;   // a flat mat would float or bury itself on a slope
                int lo = f.KrummholzHeights[2 * variant], hi = f.KrummholzHeights[2 * variant + 1];
                model = f.KrummholzModel;
                height = lo + (int)((((b >> 8) & 0xFF) * (ulong)(hi - lo + 1)) >> 8);
                rotation = (f.DownwindRotation + (int)((b >> 16) & 0x1F) - 16) & 0xFF;
                width = 32;
                spacing = cell.Spacing256;
            }
            return new ForestPoint
            {
                X = px, Y = py, Spacing256 = (ushort)spacing,
                HeightCode = (byte)(height < 1 ? 1 : height > 255 ? 255 : height),
                Prototype = (byte)(model * f.Variants + variant),
                Rotation = (byte)rotation,
                Width32 = (byte)(width < 1 ? 1 : width > 255 ? 255 : width),
            };
        }

        /// <summary><see cref="ForestPlacement.CrownRadius"/> in 1/256 m for a height in 0.25 m steps: 0.1 h + 0.6 m, 1.2–4.5 m.</summary>
        public static int CrownRadius256(int heightCode)
        {
            int r = (heightCode * 32 + 768) / 5;
            return r < 307 ? 307 : r > 1152 ? 1152 : r;
        }

        /// <summary>
        /// The clump field (NE8) at a frame position: the chance (0–256) that a dart there is kept. One clump centre per
        /// lattice square, hashed from the site seed alone, so the field runs on across cells and tiles without seams.
        /// Near a centre every dart is kept, far from all of them only <see cref="ForestInputs.ClumpFloor"/>.
        /// </summary>
        public static int ClumpKeep(ref ForestInputs f, int px, int py)
        {
            if (f.ClumpLattice <= 0 || f.ClumpRadius <= 0) return 256;
            int gx = FloorDiv(px, f.ClumpLattice), gy = FloorDiv(py, f.ClumpLattice), best = 0;
            long r2 = (long)f.ClumpRadius * f.ClumpRadius;
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    ulong h = Hash(f.Seed, gx + dx, gy + dy, 0xC1A3);
                    long cx = (long)(gx + dx) * f.ClumpLattice + (long)(((h & 0xFFFFFFFFUL) * (ulong)f.ClumpLattice) >> 32);
                    long cy = (long)(gy + dy) * f.ClumpLattice + (long)(((h >> 32) * (ulong)f.ClumpLattice) >> 32);
                    long ex = px - cx, ey = py - cy, d2 = ex * ex + ey * ey;
                    if (d2 >= r2) continue;
                    int t = 256 - (int)(d2 * 256 / r2);   // 256 at the centre, 0 at the reach
                    int k = (t * t) >> 8;
                    if (k > best) best = k;
                }
            return f.ClumpFloor + (((256 - f.ClumpFloor) * best) >> 8);
        }

        /// <summary>A model drawn from the 30 m cell's BIGMAP weights, else from the site's mix.</summary>
        static int PickModel(ref ForestInputs f, int px, int py, int u16)
        {
            if (f.SpeciesIds != null)
            {
                int c = FloorDiv(px - f.SpeciesWest, f.SpeciesCell), r = FloorDiv(f.SpeciesNorth - py, f.SpeciesCell);
                if (c >= 0 && r >= 0 && c < f.SpeciesColumns && r < f.SpeciesRows)
                {
                    long o = ((long)r * f.SpeciesColumns + c) * 4;
                    int total = 0;
                    for (int k = 0; k < 4; k++) if (f.SpeciesIds[o + k] != 0) total += f.SpeciesWeights[o + k];
                    if (total > 0)
                    {
                        int pick = (u16 * total) >> 16;
                        for (int k = 0; k < 4; k++)
                        {
                            if (f.SpeciesIds[o + k] == 0) continue;
                            pick -= f.SpeciesWeights[o + k];
                            if (pick < 0) return f.ModelOfIndex[f.SpeciesIds[o + k]];
                        }
                    }
                }
            }
            int sum = f.SiteCumulative[f.Models - 1], p = (int)(((long)u16 * sum) >> 16), last = 0;
            for (int m = 0; m < f.Models; m++)
            {
                int before = m == 0 ? 0 : f.SiteCumulative[m - 1];
                if (f.SiteCumulative[m] > before)
                {
                    last = m;
                    if (p < f.SiteCumulative[m]) return m;
                }
            }
            return last;
        }

        /// <summary>
        /// A cell's share of this tile (its rectangle and quota). The quota rounds the expected count
        /// stochastically (keyed), so a cell split between tiles keeps its expected total.
        /// </summary>
        static int Quota(ref ForestInputs f, ulong tileSeed, int x0, int y0, int i, int j, out int rx0, out int ry0, out int rw, out int rh)
        {
            var cell = f.Cells[j * f.CellsX + i];
            int cx0 = f.CellOriginX + i * CellFixed, cy0 = f.CellOriginY + j * CellFixed;
            rx0 = cx0 > x0 ? cx0 : x0;
            ry0 = cy0 > y0 ? cy0 : y0;
            int rx1 = cx0 + CellFixed < x0 + TileFixed ? cx0 + CellFixed : x0 + TileFixed;
            int ry1 = cy0 + CellFixed < y0 + TileFixed ? cy0 + CellFixed : y0 + TileFixed;
            rw = rx1 - rx0;
            rh = ry1 - ry0;
            if (cell.Kind == ForestCell.None || cell.Expected256 == 0 || rw <= 0 || rh <= 0) return 0;
            long expected = (long)cell.Expected256 * rw * rh / ((long)CellFixed * CellFixed);
            ulong h = Hash(tileSeed, j * f.CellsX + i, -1, 0x0B07);
            return (int)((expected + (long)(h & 0xFF)) >> 8);
        }

        static void CellRange(ref ForestInputs f, int x0, int y0, out int i0, out int i1, out int j0, out int j1)
        {
            i0 = FloorDiv(x0 - f.CellOriginX, CellFixed);
            i1 = FloorDiv(x0 + TileFixed - 1 - f.CellOriginX, CellFixed);
            j0 = FloorDiv(y0 - f.CellOriginY, CellFixed);
            j1 = FloorDiv(y0 + TileFixed - 1 - f.CellOriginY, CellFixed);
            if (i0 < 0) i0 = 0;
            if (j0 < 0) j0 = 0;
            if (i1 >= f.CellsX) i1 = f.CellsX - 1;
            if (j1 >= f.CellsY) j1 = f.CellsY - 1;
        }

        static bool IsCanopyTree(ref ForestInputs f, int px, int py)
        {
            if (f.CanopyMask == null) return false;
            int mx = (px >> 8) - f.MaskWest, my = FloorDiv((f.MaskNorth << 8) - py, Fixed);   // a pixel owns its west and north edges, as in the grids
            if (mx < 0 || my < 0 || mx >= f.MaskWidth || my >= f.MaskHeight) return false;
            long bit = (long)my * f.MaskWidth + mx;
            return ((f.CanopyMask[bit >> 6] >> (int)(bit & 63)) & 1UL) != 0;
        }

        /// <summary>True when a tree at (lx, ly) with this spacing would stand too close to one already there.</summary>
        static bool Crowded(ref ForestScratch s, int bins, int lx, int ly, int spacing, int maxSpacing)
        {
            int reach = (spacing + maxSpacing + 1) >> 1;
            int bx0 = (lx - reach) >> ForestScratch.BinShift, bx1 = (lx + reach) >> ForestScratch.BinShift;
            int by0 = (ly - reach) >> ForestScratch.BinShift, by1 = (ly + reach) >> ForestScratch.BinShift;
            if (bx0 < 0) bx0 = 0;
            if (by0 < 0) by0 = 0;
            if (bx1 >= bins) bx1 = bins - 1;
            if (by1 >= bins) by1 = bins - 1;
            for (int by = by0; by <= by1; by++)
                for (int bx = bx0; bx <= bx1; bx++)
                    for (int k = s.Heads[by * bins + bx]; k >= 0; k = s.Next[k])
                    {
                        var q = s.Local[k];
                        long dx = lx - q.X, dy = ly - q.Y;
                        long d = (spacing + q.Spacing256) >> 1;
                        if (dx * dx + dy * dy < d * d) return true;
                    }
            return false;
        }

        /// <summary>Adds a tree to the tile's bins (in the tile's local frame).</summary>
        static bool Insert(ref ForestScratch s, ref int local, int bins, int lx, int ly, ForestPoint p)
        {
            if (local >= s.LocalCapacity) return false;
            int b = (ly >> ForestScratch.BinShift) * bins + (lx >> ForestScratch.BinShift);
            p.X = lx;
            p.Y = ly;
            s.Local[local] = p;
            s.Next[local] = s.Heads[b];
            s.Heads[b] = local;
            local++;
            return true;
        }

        static int FloorDiv(int a, int b) => a >= 0 ? a / b : -((-a + b - 1) / b);

        /// <summary>Keyed hash (SplitMix64 finalizer chain): the forest's only source of randomness.</summary>
        public static ulong Hash(ulong seed, long a, long b, long salt)
        {
            ulong h = Mix(seed ^ (ulong)a * 0x9E3779B97F4A7C15UL);
            h = Mix(h ^ (ulong)b * 0xBF58476D1CE4E5B9UL);
            return Mix(h ^ (ulong)salt * 0x94D049BB133111EBUL);
        }

        static ulong Mix(ulong h)
        {
            h ^= h >> 30;
            h *= 0xBF58476D1CE4E5B9UL;
            h ^= h >> 27;
            h *= 0x94D049BB133111EBUL;
            h ^= h >> 31;
            return h;
        }
    }
}
