using System;
using System.IO;
using System.Linq;
using MountainPlanner.Domain.Edits;
using MountainPlanner.Domain.Geo;
using MountainPlanner.Persistence;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    // Terrain edits over a real package (task P3-05; rules SH-02, GR-06, GR-09, CL-05, CL-07): the lazy float32 heights,
    // the edited cover and the cleared forest, on the committed Jackson Hole 2 km test package.
    public sealed class TerrainEditPackageTests
    {
        static (PackageManifest Manifest, string Dir, TileGrid Tiles) JacksonHole2Km()
        {
            string dir = TestData.Folder("jackson-hole-2km");
            foreach (string f in Directory.GetFiles(dir, "*.grid")) TestData.Bytes(dir, Path.GetFileName(f));
            var manifest = ResortPackage.ReadManifest(dir);
            var tiles = TileGrid.For(SiteSquare.Create(new AlbersPoint(manifest.Site.CentreX, manifest.Site.CentreY), manifest.Site.SizeMetres / 1000.0));
            return (manifest, dir, tiles);
        }

        static TerrainCache.HeightField FullHeights(PackageManifest m, string dir)
        {
            float[] core = ResortPackage.ReadLayer(dir, m, "heights-core", out var ch);
            float[] ring = ResortPackage.ReadLayer(dir, m, "heights-ring", out var rh);
            return new TerrainCache.HeightField(core, ch, ring, rh);
        }

        /// <summary>The core tile at the site's centre, and a 300 m bench across it, as a test edit.</summary>
        static (TileKey Key, AlbersBox Box, EditLattice Lattice) Centre(PackageManifest m, TileGrid tiles)
        {
            tiles.TryTileAt(new AlbersPoint(m.Site.CentreX, m.Site.CentreY), out var key);
            return (key, tiles.Bounds(key), new EditLattice(tiles.West, tiles.North));
        }

        [Test]
        public void SH02_TheLazyHeightsEqualTheCacheBuildsHeightsExactly()
        {
            var (m, dir, tiles) = JacksonHole2Km();
            var full = FullHeights(m, dir);
            using var lazy = new PackageHeights(dir, m);
            var ring = tiles.Bounds(new TileKey(0, 0));
            double w = tiles.West, n = tiles.North, size = tiles.Columns * TileGrid.TileMetres;
            int checkedPoints = 0;
            // A sweep over the whole ring and core, off the lattice too, through the core's 16 m blend and both grids' edges.
            for (int j = 0; j <= 400; j++)
                for (int i = 0; i <= 400; i++)
                {
                    double x = w + size * i / 400.0 + 0.37 * ((i * 7 + j * 3) % 5), y = n - size * j / 400.0 - 0.29 * ((i * 3 + j * 11) % 7);
                    Assert.That(lazy.At(x, y), Is.EqualTo(full.At(x, y)), $"({x}, {y})");
                    checkedPoints++;
                }
            Assert.That(checkedPoints, Is.EqualTo(401 * 401));
            Assert.That(lazy.ChunksRead, Is.GreaterThan(0));
        }

        [Test]
        public void GR09_RegionCoverOverThePackageHeightsEqualsTheCachedTile()
        {
            var (m, dir, tiles) = JacksonHole2Km();
            var full = FullHeights(m, dir);
            var cover = new CoverField(m, dir, full);
            var (key, box, _) = Centre(m, tiles);
            byte[] tile = cover.BuildTile(box, TerrainCache.CoreCoverResolution);
            int i0 = 300, j0 = 410, i1 = 380, j1 = 470, w = i1 - i0;
            var region = new byte[w * (j1 - j0) * TerrainCache.CoverBands];
            using var lazy = new PackageHeights(dir, m);
            cover.BuildRegion(box, TerrainCache.CoreCoverResolution, i0, j0, i1, j1, lazy, null, region);
            for (int j = j0; j < j1; j++)
                for (int i = i0; i < i1; i++)
                    for (int k = 0; k < TerrainCache.CoverBands; k++)
                        Assert.That(region[((j - j0) * w + i - i0) * TerrainCache.CoverBands + k], Is.EqualTo(tile[(j * TerrainCache.CoreCoverResolution + i) * TerrainCache.CoverBands + k]),
                                    $"texel ({i}, {j}) band {k}");
        }

        [Test]
        public void CL05_ClearedCellsLoseTheirForestFloorAndUndoRestoresTheCover()
        {
            var (m, dir, tiles) = JacksonHole2Km();
            using var lazy = new PackageHeights(dir, m);
            var cover = new CoverField(m, dir, lazy);
            var (key, box, lattice) = Centre(m, tiles);
            var natural = new EditedHeights(lazy, lattice, TerrainEdits.Empty);
            double cx = (box.West + box.East) / 2, cy = (box.North + box.South) / 2;
            var bench = TestBench.Build(natural, cx, cy + 150, cx, cy - 150, 30.48);
            var edited = new EditedHeights(lazy, lattice, bench.Edits);

            int res = TerrainCache.CoreCoverResolution, i0 = 470, i1 = 560, j0 = 330, j1 = 700, w = i1 - i0;
            var before = new byte[w * (j1 - j0) * TerrainCache.CoverBands];
            var after = new byte[before.Length];
            var undone = new byte[before.Length];
            cover.BuildRegion(box, res, i0, j0, i1, j1, natural, natural.IsCleared, before);
            cover.BuildRegion(box, res, i0, j0, i1, j1, edited, edited.IsCleared, after);
            cover.BuildRegion(box, res, i0, j0, i1, j1, natural, natural.IsCleared, undone);
            Assert.That(undone, Is.EqualTo(before), "undo gives the same cover bytes");

            double step = (box.East - box.West) / (res - 1);
            int forestBefore = 0, changedOutside = 0, clearedWithFloor = 0;
            for (int j = j0; j < j1; j++)
                for (int i = i0; i < i1; i++)
                {
                    double x = box.West + i * step, y = box.North - j * step;
                    int o = ((j - j0) * w + i - i0) * TerrainCache.CoverBands;
                    if (edited.IsCleared(x, y))
                    {
                        if (after[o] > 0) clearedWithFloor++;
                        if (before[o] > 0) forestBefore++;
                    }
                    else
                    {
                        // Outside the footprint (and a 2 m slope stencil) nothing may change (GR-06).
                        bool nearFootprint = Math.Abs(x - cx) <= 15.24 + 2 && y <= cy + 152 && y >= cy - 152;
                        if (!nearFootprint)
                            for (int k = 0; k < TerrainCache.CoverBands; k++)
                                if (after[o + k] != before[o + k]) changedOutside++;
                    }
                }
            TestContext.Progress.WriteLine($"cleared texels with forest floor before: {forestBefore}; after: {clearedWithFloor}");
            Assert.That(clearedWithFloor, Is.EqualTo(0), "no forest floor where the trees are cleared (CL-05)");
            Assert.That(changedOutside, Is.EqualTo(0), "nothing changes outside the footprint");
        }

        [Test]
        public void CL07_ClearingFiltersTheForestAndNeverRegrowsIt()
        {
            var (m, dir, tiles) = JacksonHole2Km();
            var forest = new ForestField(m, dir);
            var built = forest.BuildAll(tiles);
            var keys = tiles.All().ToList();
            var (key, box, lattice) = Centre(m, tiles);
            int n = keys.IndexOf(key);
            byte[] packed = ForestField.Encode(built[n], TileGrid.TileMetres);
            int count = packed.Length / ForestField.BytesPerTree;
            Assert.That(count, Is.GreaterThan(1000), "the centre tile is forested");

            using var lazy = new PackageHeights(dir, m);
            var natural = new EditedHeights(lazy, lattice, TerrainEdits.Empty);
            double cx = (box.West + box.East) / 2, cy = (box.North + box.South) / 2;
            var bench = TestBench.Build(natural, cx, cy + 150, cx, cy - 150, 30.48);

            int cleared = 0, wrong = 0;
            for (int i = 0; i < count; i++)
            {
                ForestField.Decode(packed, i, out float fx, out float fz, out _, out _, out _, out _);
                double x = box.West + fx * TileGrid.TileMetres, y = box.North - TileGrid.TileMetres + fz * TileGrid.TileMetres;
                bool inside = Math.Abs(x - cx) <= 15.24 + 1 && y <= cy + 151 && y >= cy - 151;
                bool c = ForestField.IsCleared(packed, i, box.West, box.North, TileGrid.TileMetres, bench.Edits, lattice);
                if (c) cleared++;
                if (c && !inside) wrong++;
                Assert.That(ForestField.IsCleared(packed, i, box.West, box.North, TileGrid.TileMetres, TerrainEdits.Empty, lattice), Is.False, "undo keeps every tree");
            }
            TestContext.Progress.WriteLine($"centre tile: {count:N0} trees, {cleared:N0} cleared by a 300 m × 100 ft bench");
            Assert.That(wrong, Is.EqualTo(0), "only trees inside the footprint are cleared");
            Assert.That(cleared, Is.GreaterThan(0));
            // The other tiles' trees are the package's, untouched: clearing never regrows the forest.
            var other = keys.Where(k => !k.Equals(key)).First(k => built[keys.IndexOf(k)].Count > 0);
            byte[] otherPacked = ForestField.Encode(built[keys.IndexOf(other)], TileGrid.TileMetres);
            var ob = tiles.Bounds(other);
            for (int i = 0; i < otherPacked.Length / ForestField.BytesPerTree; i++)
                Assert.That(ForestField.IsCleared(otherPacked, i, ob.West, ob.North, TileGrid.TileMetres, bench.Edits, lattice), Is.False);
        }

        [Test]
        public void GR06_TheBenchTiesInAtTheFootprintsEdge()
        {
            var (m, dir, tiles) = JacksonHole2Km();
            using var lazy = new PackageHeights(dir, m);
            var (key, box, lattice) = Centre(m, tiles);
            var natural = new EditedHeights(lazy, lattice, TerrainEdits.Empty);
            double cx = (box.West + box.East) / 2, cy = (box.North + box.South) / 2;
            // Diagonal to the lattice, as the demo's benches usually are.
            double hx = cx - 100, hy = cy + 100, tx = cx + 112, ty = cy - 112;
            var bench = TestBench.Build(natural, hx, hy, tx, ty, 30.48);
            double ex = (tx - hx) / 300.0, ey = (ty - hy) / 300.0, nx = -ey, ny = ex;
            double worst = 0;
            int edge = 0;
            int c0 = (int)Math.Floor(Math.Min(hx, tx) - 20 - lattice.West), c1 = (int)Math.Ceiling(Math.Max(hx, tx) + 20 - lattice.West);
            int r0 = (int)Math.Floor(lattice.North - Math.Max(hy, ty) - 20), r1 = (int)Math.Ceiling(lattice.North - Math.Min(hy, ty) + 20);
            for (int r = r0; r <= r1; r++)
                for (int c = c0; c <= c1; c++)
                {
                    double rx = lattice.X(c) - hx, ry = lattice.Y(r) - hy;
                    double t = rx * ex + ry * ey, u = Math.Abs(rx * nx + ry * ny);
                    if (t < 8 || t > 292 || u > 15.24 || u < 14.24) continue;
                    edge++;
                    worst = Math.Max(worst, Math.Abs(bench.Edits.DeltaMmAt(c, r)) / 1000.0);
                }
            TestContext.Progress.WriteLine($"{edge} edge points; the largest change within 1 m of the footprint's edge is {worst:F3} m");
            // A 1:1 face may still be rising to the ground in the last metre, so up to about 1 m is a face, not a step.
            Assert.That(worst, Is.LessThan(1.0), "the faces meet the ground at the edge, so no step stands there");
        }

        [Test]
        public void GR09_UndoGivesByteIdenticalCacheHeights()
        {
            var (m, dir, tiles) = JacksonHole2Km();
            var full = FullHeights(m, dir);
            using var lazy = new PackageHeights(dir, m);
            var (key, box, lattice) = Centre(m, tiles);
            var core = m.Layers.First(l => l.Id == "heights-core");
            var ring = m.Layers.First(l => l.Id == "heights-ring");
            double min = Math.Min(core.Min, ring.Min), range = Math.Max(1, Math.Max(core.Max, ring.Max) - min);
            var natural = new EditedHeights(lazy, lattice, TerrainEdits.Empty);
            double cx = (box.West + box.East) / 2, cy = (box.North + box.South) / 2;
            var bench = TestBench.Build(natural, cx, cy + 150, cx, cy - 150, 30.48);
            var edited = new EditedHeights(lazy, lattice, bench.Edits);
            var undone = new EditedHeights(lazy, lattice, bench.Edits.With(bench.Tiles.Select(TerrainEditTile.Empty)));

            int c0 = (int)Math.Round(box.West - lattice.West), r0 = (int)Math.Round(lattice.North - box.North);
            int changed = 0;
            for (int j = 0; j <= 1024; j += 1)
                for (int i = 0; i <= 1024; i += 1)
                {
                    ushort cached = TerrainCache.Quantize(full.At(box.West + i, box.North - j), min, range);
                    ushort back = TerrainCache.Quantize(undone.AtPost(box.West + i, box.North - j, c0 + i, r0 + j), min, range);
                    if (back != cached) Assert.Fail($"post ({i}, {j}): {back} after undo, {cached} cached");
                    if (TerrainCache.Quantize(edited.AtPost(box.West + i, box.North - j, c0 + i, r0 + j), min, range) != cached) changed++;
                }
            TestContext.Progress.WriteLine($"{changed:N0} posts changed by the bench");
            Assert.That(changed, Is.GreaterThan(1000));
        }
    }
}
